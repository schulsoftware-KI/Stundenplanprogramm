using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P5c-2 – Frist und Löschen ganzer Schuljahre (Entscheidungen 266–270; Vorschlag P5c §6).
//
//  Frist (266): Einstellungen → Datenschutz → „Klassenbuchdaten ganzer Schuljahre behalten … Jahre“ (0 = aus, Standard;
//  1–15). Löschbar ab = Ende laut Kalender + n Jahre + 1 Tag (2026/27, Ende 31.07.2027, n = 1 → ab 01.08.2028), gerechnet mit
//  dem „Heute“ der API (Testdatum, 221). Die Frist löscht nie selbst (241) – sie gibt nur den Knopf frei.
//
//  Der Knopf „Schuljahr … löschen…“ (Karte „Archiv und Fristen“) erscheint nur (241), wenn: abgeschlossen · Archiv-Export
//  nach dem letzten Abschluss · dessen Aufbewahrung danach bestätigt · Frist gesetzt und erreicht · nicht das „Aktuelle
//  Schuljahr“ · noch nicht gelöscht. Der Server prüft alles noch einmal (LoeschSperre – dieselben Regeln wie die Karte).
//
//  Gelöscht wird (267) – alles dieses Schuljahres:
//    Stunden mit Klassenbuch samt Anwesenheiten, Thema, Hausaufgabe · Klassenbucheinträge · Unterrichtseinstellungen ·
//    Lerngruppen und Zugehörigkeiten · Vertretungstage samt Einträgen (auch Gründe) · Grundplan-Versionen samt Stunden ·
//    Kalender samt freien Zeiten · Entschuldigungen im Zeitraum des Kalenders (eine, die darüber hinausreicht, wird auf den
//    Teil außerhalb gekürzt; reicht sie auf beiden Seiten hinaus, wird sie geteilt) · Verlaufszeilen zu den Stunden bzw.
//    Einträgen, Zeilen ohne Stunde („Entschuldigung“, „Gruppe“, „Sichtbarkeit“, gelöschte Einträge) nach dem Zeitpunkt vom
//    01.08. bis 31.07. des Schuljahres.
//    Schüler (268) mit Zugehörigkeit in diesem Jahr und KEINEN Daten in einem anderen Schuljahr (keine Zugehörigkeit, keine
//    Anwesenheit in einer Stunde, kein Klassenbucheintrag eines anderen Jahres): Datensatz, Konto, alle Entschuldigungen,
//    Verlaufs- und Leseprotokollzeilen; je Konto ein Eintrag im Kontenprotokoll.
//    Zuständigkeiten (269) für das Jahr: entfallen (Klassenleitung/Stufenkoordination → Lehrkraft), abgemeldet,
//    Kontenprotokoll.
//  Es bleibt: die Zeile in „Schuljahre“ (Status „abgeschlossen“, Archiv- und Löschangaben), Protokolle mit eigener Frist
//  (Konten, Anmeldungen, Downloads, Einstellungen, SchILD-Importe, Leseprotokoll anderer Schüler), Schüler und Konten mit
//  Daten in anderen Jahren, Lehrkräfte, Einstellungen. Nicht in der App: Vertretungsdatei des Desktops, Sicherungen.
//
//  Ablauf (270): POST ohne Prüfsumme → 409 mit den Zahlen je Art und ihrer Prüfsumme · POST mit Schuljahr (eingetippt),
//  Passwort (Login-Bremse) und Prüfsumme → der Server zählt neu (abweichend → 409 mit den neuen Zahlen) → EINE Transaktion
//  mit ExecuteDelete (Befehls-Zeitlimit 10 Minuten); Fehler → nichts gelöscht → GeloeschtAm/-Von/-Text, Protokoll der
//  Einstellungen, Log ohne Namen. Danach kein Wiederöffnen, kein Archiv-Export; Desktop-Pushes für das Jahr lehnt die API
//  weiter mit 409 ab (Status „abgeschlossen“). Während des Löschens startet kein Archiv (ArchivAuftraege.LoeschenPruefen).
//  Zählen und Löschen benutzen dieselben Abfragen (LoeschAuswahl) und dieselben Listen (Schüler, Konten).
// =====================================================================

public static partial class Jahresablauf
{
    /// <summary>Befehls-Zeitlimit beim Löschen (statt 30 Sekunden).</summary>
    public const int LoeschenMinuten = 10;

    /// <summary>Frist eines Schuljahres: Jahre (0 = aus), Ende laut Kalender, ist es das „Aktuelle Schuljahr“?</summary>
    public sealed record LoeschFrist(int Jahre, DateOnly Ende, bool Aktuell);

    /// <summary>Löschbar ab = Ende + n Jahre + 1 Tag (266).</summary>
    public static DateOnly LoeschbarAb(DateOnly ende, int jahre) => ende.AddYears(jahre).AddDays(1);

    /// <summary>Ende eines Schuljahres ohne Kalender: 31.07. des zweiten Jahres („2026/27“ → 31.07.2027).</summary>
    public static DateOnly EndeNachDatum(string sj)
    {
        var m = SchuljahrForm.Match(sj ?? "");
        int a = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : KlassenbuchService.Heute().Year - 1;
        return new DateOnly(a + 1, 7, 31);
    }

    /// <summary>Beginn eines Schuljahres ohne Kalender: 01.08. des ersten Jahres.</summary>
    public static DateOnly BeginnNachDatum(string sj) => new(EndeNachDatum(sj).Year - 1, 8, 1);

    /// <summary>
    /// Warum <paramref name="jahr"/> (noch) nicht gelöscht werden darf – null = es darf (241). Dieselben Regeln wie der Knopf
    /// der Karte (JahresablaufArchiv.FristBestaetigt).
    /// </summary>
    public static string? LoeschSperre(string jahr, SchuljahrDb? z, LoeschFrist f, DateOnly heute)
    {
        if (z?.GeloeschtAm != null) return $"Die Daten von {jahr} sind schon gelöscht.";
        if (z == null || z.Status != SchuljahrStatus.Abgeschlossen)
            return $"{jahr} ist nicht abgeschlossen – gelöscht werden nur abgeschlossene Schuljahre.";
        if (f.Aktuell) return $"{jahr} ist das „Aktuelle Schuljahr“ – es lässt sich nicht löschen.";
        if (z.ArchivAm == null) return $"Für {jahr} gibt es noch keinen Archiv-Export – erst erstellen und seine Aufbewahrung bestätigen.";
        if (ArchivVeraltet(z))
            return $"Der Archiv-Export von {jahr} ist veraltet (wieder geöffnet) – erst neu erstellen und seine Aufbewahrung bestätigen.";
        if (!ArchivBestaetigt(z)) return $"Die Aufbewahrung des Archiv-Exports von {jahr} ist noch nicht bestätigt.";
        if (f.Jahre <= 0)
            return "Die Frist „Klassenbuchdaten ganzer Schuljahre behalten“ ist aus (Einstellungen → Datenschutz) – ganze " +
                   "Schuljahre bleiben in der Datenbank.";
        var ab = LoeschbarAb(f.Ende, f.Jahre);
        if (heute < ab) return $"Laut Frist darf {jahr} erst ab dem {D(ab)} gelöscht werden.";
        return null;
    }

    // -----------------------------------------------------------------
    //  Zahlen
    // -----------------------------------------------------------------

    /// <summary>„1.312“ (Tausenderpunkt, ohne Kultur des Servers).</summary>
    public static string ZahlText(int n) => n.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.');

    /// <summary>Was das Löschen trifft bzw. getroffen hat – nur Zahlen, keine Namen.</summary>
    public sealed record LoeschZahlen(
        int Stunden, int Anwesenheiten, int Eintraege, int Entschuldigungen, int Gekuerzt, int Verlauf, int Lerngruppen,
        int Zugehoerigkeiten, int Unterricht, int Vertretungstage, int Vertretungen, int Grundplaene, int GrundplanStunden,
        int Kalender, int FreieZeiten, int Schueler, int Konten, int Leseprotokoll, int Zustaendigkeiten)
    {
        private int[] Werte() => new[]
        {
            Stunden, Anwesenheiten, Eintraege, Entschuldigungen, Gekuerzt, Verlauf, Lerngruppen, Zugehoerigkeiten, Unterricht,
            Vertretungstage, Vertretungen, Grundplaene, GrundplanStunden, Kalender, FreieZeiten, Schueler, Konten, Leseprotokoll,
            Zustaendigkeiten
        };

        /// <summary>Prüfsumme der Zahlen (16 Zeichen) – die Bestätigung gilt nur für genau diese Zahlen.</summary>
        public string Pruefsumme(string jahr)
        {
            var text = jahr + "|" + string.Join("|", Werte().Select(x => x.ToString(CultureInfo.InvariantCulture)));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        }

        /// <summary>Zeilen für die Rückfrage (nur die mit Anzahl &gt; 0).</summary>
        public List<JaLoeschZeileDto> Zeilen(string jahr)
        {
            var l = new List<JaLoeschZeileDto>();
            void Z(string art, int n, string text)
            {
                if (n > 0) l.Add(new JaLoeschZeileDto(art, $"{ZahlText(n)} {text}", n));
            }
            Z("stunden", Stunden, Stunden == 1 ? "Stunde mit Klassenbuch (Anwesenheit, Thema, Hausaufgabe)"
                : "Stunden mit Klassenbuch (Anwesenheit, Thema, Hausaufgabe)");
            Z("anwesenheiten", Anwesenheiten, "Angaben zur Anwesenheit (Fehlstunden, Verspätungen, Beurlaubungen …)");
            Z("eintraege", Eintraege, Eintraege == 1 ? "Klassenbucheintrag" : "Klassenbucheinträge (Schüler- und Klasseneinträge)");
            Z("entschuldigungen", Entschuldigungen, Entschuldigungen == 1 ? "Entschuldigung" : "Entschuldigungen");
            Z("gekuerzt", Gekuerzt, (Gekuerzt == 1 ? "Entschuldigung reicht" : "Entschuldigungen reichen") +
                                    $" über {jahr} hinaus – sie werden auf den Teil außerhalb gekürzt");
            Z("verlauf", Verlauf, Verlauf == 1 ? "Zeile im Änderungsverlauf des Klassenbuchs" : "Zeilen im Änderungsverlauf des Klassenbuchs");
            Z("lerngruppen", Lerngruppen, "Klassen, Kurse und Gruppen");
            Z("zugehoerigkeiten", Zugehoerigkeiten, "Zugehörigkeiten (wer in welcher Klasse, welchem Kurs, welcher Gruppe war)");
            Z("unterricht", Unterricht, "Einstellungen je Unterricht (Sichtbarkeit der Hausaufgaben)");
            Z("vertretungstage", Vertretungstage, (Vertretungstage == 1 ? "veröffentlichter Vertretungstag" : "veröffentlichte Vertretungstage") +
                                                  $" mit {ZahlText(Vertretungen)} Einträgen (auch Abwesenheitsgründe)");
            Z("grundplaene", Grundplaene, (Grundplaene == 1 ? "Grundplan-Version" : "Grundplan-Versionen") +
                                          $" mit {ZahlText(GrundplanStunden)} Stunden");
            Z("kalender", Kalender, $"Kalender mit {ZahlText(FreieZeiten)} freien Zeiten");
            Z("schueler", Schueler, (Schueler == 1 ? "Schülerin bzw. Schüler" : "Schülerinnen und Schüler") +
                                    $" ohne Daten in einem anderen Schuljahr (Abschluss, Abgang) – mit {ZahlText(Konten)} " +
                                    (Konten == 1 ? "Konto" : "Konten") + " und allen ihren Daten");
            Z("leseprotokoll", Leseprotokoll, "Zeilen im Leseprotokoll zu diesen Schülerinnen und Schülern");
            Z("zustaendigkeiten", Zustaendigkeiten, (Zustaendigkeiten == 1 ? "Zuständigkeit" : "Zuständigkeiten") +
                                                    $" für {jahr} (Klassenleitung, Stufenkoordination) – die Konten werden " +
                                                    "Lehrkraft und abgemeldet");
            return l;
        }

        /// <summary>Kurz für GeloeschtText, Protokoll und Meldung.</summary>
        public string Kurztext()
        {
            var t = new List<string>();
            void Z(int n, string text)
            {
                if (n > 0) t.Add($"{ZahlText(n)} {text}");
            }
            Z(Stunden, "Stunden mit Klassenbuch");
            Z(Anwesenheiten, "Anwesenheitsangaben");
            Z(Eintraege, "Einträge");
            Z(Entschuldigungen, "Entschuldigungen");
            Z(Gekuerzt, "gekürzt");
            Z(Verlauf, "Verlaufszeilen");
            Z(Lerngruppen, "Lerngruppen");
            Z(Zugehoerigkeiten, "Zugehörigkeiten");
            Z(Unterricht, "Unterrichtseinstellungen");
            Z(Vertretungstage, "Vertretungstage");
            Z(Grundplaene, "Grundpläne");
            Z(Kalender, "Kalender");
            Z(Schueler, $"Schüler ({ZahlText(Konten)} Konten)");
            Z(Leseprotokoll, "Leseprotokollzeilen");
            Z(Zustaendigkeiten, "Zuständigkeiten");
            return t.Count == 0 ? "keine Daten mehr" : string.Join(", ", t);
        }
    }

    /// <summary>Was gelöscht werden soll: Zeitraum, Listen (Schüler, Konten) und die Zahlen beim Zählen.</summary>
    public sealed record LoeschPlan(string Schuljahr, DateOnly Von, DateOnly Bis, DateTime VerlaufAb, DateTime VerlaufBis,
        List<int> SchuelerIds, List<int> KontoIds, LoeschZahlen Zahlen, SchuljahrDb Zeile);

    /// <summary>Ergebnis der Prüfung vor dem Löschen: Fehler (Status, Text) oder der Plan.</summary>
    public sealed record LoeschVorbereitung(int Status, string? Fehler, LoeschPlan? Plan);

    /// <summary>Die Abfragen des Löschens – dieselben für Zählen und Löschen (Werte vorher in lokale Variablen).</summary>
    private sealed class LoeschAuswahl
    {
        private readonly AppDbContext _db;
        private readonly string _jahr;
        private readonly DateOnly _von, _bis;
        private readonly DateTime _verlaufAb, _verlaufBis;
        private readonly List<int> _schueler, _konten;

        public LoeschAuswahl(AppDbContext db, string jahr, DateOnly von, DateOnly bis, DateTime verlaufAb, DateTime verlaufBis,
            List<int> schueler, List<int> konten)
        {
            _db = db;
            _jahr = jahr;
            _von = von;
            _bis = bis;
            _verlaufAb = verlaufAb;
            _verlaufBis = verlaufBis;
            _schueler = schueler;
            _konten = konten;
        }

        public IQueryable<int> LektionIds()
        {
            string jahr = _jahr;
            return _db.Lektionen.Where(l => l.Schuljahr == jahr).Select(l => l.Id);
        }

        private IQueryable<int> EintragIds()
        {
            string jahr = _jahr;
            return _db.KlassenbuchEintraege.Where(e => e.Schuljahr == jahr).Select(e => e.Id);
        }

        private IQueryable<int> LerngruppeIds()
        {
            string jahr = _jahr;
            return _db.Lerngruppen.Where(l => l.Schuljahr == jahr).Select(l => l.Id);
        }

        public IQueryable<Lektion> QStunden()
        {
            string jahr = _jahr;
            return _db.Lektionen.Where(l => l.Schuljahr == jahr);
        }

        public IQueryable<Anwesenheit> QAnwesenheiten()
        {
            var lids = LektionIds();
            var s = _schueler;
            return _db.Anwesenheiten.Where(a => lids.Contains(a.LektionId) || s.Contains(a.SchuelerId));
        }

        public IQueryable<KlassenbuchEintrag> QEintraege()
        {
            string jahr = _jahr;
            var s = _schueler;
            return _db.KlassenbuchEintraege.Where(e => e.Schuljahr == jahr || (e.SchuelerId != null && s.Contains(e.SchuelerId.Value)));
        }

        /// <summary>Ganz im Zeitraum des Kalenders bzw. von einem gelöschten Schüler.</summary>
        public IQueryable<Entschuldigung> QEntschuldigungenWeg()
        {
            DateOnly von = _von, bis = _bis;
            var s = _schueler;
            return _db.Entschuldigungen.Where(e => (e.Von >= von && e.Bis <= bis) || s.Contains(e.SchuelerId));
        }

        /// <summary>Überschneiden den Zeitraum, reichen aber darüber hinaus (nicht von gelöschten Schülern) – werden gekürzt.</summary>
        public IQueryable<Entschuldigung> QEntschuldigungenKuerzen()
        {
            DateOnly von = _von, bis = _bis;
            var s = _schueler;
            return _db.Entschuldigungen.Where(e => e.Von <= bis && e.Bis >= von && (e.Von < von || e.Bis > bis)
                                                   && !s.Contains(e.SchuelerId));
        }

        public IQueryable<KlassenbuchProtokoll> QVerlauf()
        {
            string jahr = _jahr;
            DateTime ab = _verlaufAb, bis = _verlaufBis;
            var lids = LektionIds();
            var eids = EintragIds();
            var uebrige = _db.KlassenbuchEintraege.Where(e => e.Schuljahr != jahr).Select(e => e.Id);
            var s = _schueler;
            return _db.KlassenbuchProtokolle.Where(p =>
                (p.LektionId != null && lids.Contains(p.LektionId.Value))
                || (p.EintragId != null && eids.Contains(p.EintragId.Value))
                || (p.SchuelerId != null && s.Contains(p.SchuelerId.Value))
                || (p.LektionId == null && p.Zeit >= ab && p.Zeit < bis
                    && (p.EintragId == null || !uebrige.Contains(p.EintragId.Value))));
        }

        public IQueryable<LeseProtokoll> QLeseprotokoll()
        {
            var s = _schueler;
            var k = _konten;
            return _db.LeseProtokolle.Where(p => (p.SchuelerId != null && s.Contains(p.SchuelerId.Value))
                                                 || (p.KontoId != null && k.Contains(p.KontoId.Value)));
        }

        public IQueryable<Lerngruppe> QLerngruppen()
        {
            string jahr = _jahr;
            return _db.Lerngruppen.Where(l => l.Schuljahr == jahr);
        }

        public IQueryable<Zugehoerigkeit> QZugehoerigkeiten()
        {
            string jahr = _jahr;
            var lg = LerngruppeIds();
            var s = _schueler;
            return _db.Zugehoerigkeiten.Where(z => z.Schuljahr == jahr || lg.Contains(z.LerngruppeId) || s.Contains(z.SchuelerId));
        }

        public IQueryable<UnterrichtEinstellung> QUnterricht()
        {
            string jahr = _jahr;
            return _db.UnterrichtEinstellungen.Where(e => e.Schuljahr == jahr);
        }

        public IQueryable<VertretungsTag> QTage()
        {
            string jahr = _jahr;
            return _db.Tage.Where(t => t.Schuljahr == jahr);
        }

        public IQueryable<VertretungsEintragDb> QVertretungen()
        {
            var tage = QTage().Select(t => t.Id);
            return _db.Eintraege.Where(e => tage.Contains(e.TagId));
        }

        public IQueryable<GrundplanVersion> QGrundplaene()
        {
            string jahr = _jahr;
            return _db.GrundplanVersionen.Where(v => v.Schuljahr == jahr);
        }

        public IQueryable<GrundplanSlot> QGrundplanStunden()
        {
            var versionen = QGrundplaene().Select(v => v.Id);
            return _db.GrundplanSlots.Where(x => versionen.Contains(x.VersionId));
        }

        public IQueryable<KalenderDb> QKalender()
        {
            string jahr = _jahr;
            return _db.Kalender.Where(k => k.Schuljahr == jahr);
        }

        public IQueryable<FreieZeitDb> QFreieZeiten()
        {
            var kal = QKalender().Select(k => k.Id);
            return _db.FreieZeiten.Where(f => kal.Contains(f.KalenderId));
        }

        public IQueryable<Schueler> QSchueler()
        {
            var s = _schueler;
            return _db.Schueler.Where(x => s.Contains(x.Id));
        }

        public IQueryable<Benutzer> QKonten()
        {
            var k = _konten;
            return _db.Benutzer.Where(b => k.Contains(b.Id));
        }

        public IQueryable<Benutzer> QZustaendigkeiten()
        {
            string jahr = _jahr;
            return _db.Benutzer.Where(b => b.ZustaendigSchuljahr == jahr && b.Zustaendig != null && b.Zustaendig != ""
                                           && b.Rolle != BenutzerRolle.Schueler);
        }

        public async Task<LoeschZahlen> ZaehlenAsync()
            => new(
                await QStunden().CountAsync(),
                await QAnwesenheiten().CountAsync(),
                await QEintraege().CountAsync(),
                await QEntschuldigungenWeg().CountAsync(),
                await QEntschuldigungenKuerzen().CountAsync(),
                await QVerlauf().CountAsync(),
                await QLerngruppen().CountAsync(),
                await QZugehoerigkeiten().CountAsync(),
                await QUnterricht().CountAsync(),
                await QTage().CountAsync(),
                await QVertretungen().CountAsync(),
                await QGrundplaene().CountAsync(),
                await QGrundplanStunden().CountAsync(),
                await QKalender().CountAsync(),
                await QFreieZeiten().CountAsync(),
                _schueler.Count,
                _konten.Count,
                await QLeseprotokoll().CountAsync(),
                await QZustaendigkeiten().CountAsync());
    }

    /// <summary>Zeitpunkt der Verlaufszeilen ohne Stunde: 01.08. (Ortszeit) des ersten bis 01.08. des zweiten Jahres, als UTC.</summary>
    private static (DateTime Ab, DateTime Bis) VerlaufZeitraum(string sj)
    {
        int a = EndeNachDatum(sj).Year - 1;
        return (new DateTime(a, 8, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(),
                new DateTime(a + 1, 8, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime());
    }

    // -----------------------------------------------------------------
    //  Prüfen und Zählen (1. Schritt bzw. vor dem Löschen)
    // -----------------------------------------------------------------

    public static async Task<LoeschVorbereitung> LoeschenVorbereitenAsync(AppDbContext db, Schuleinstellungen einst, string? eingabe)
    {
        var jahr = SchuljahrLesen(eingabe, out var fehler);
        if (fehler != null) return new LoeschVorbereitung(400, fehler, null);
        var w = await einst.LesenAsync(db);
        var zeile = await db.Schuljahre.AsNoTracking().FirstOrDefaultAsync(s => s.Schuljahr == jahr);
        var kal = await db.Kalender.AsNoTracking().Where(k => k.Schuljahr == jahr)
            .Select(k => new { k.Start, k.Ende }).FirstOrDefaultAsync();
        var von = kal?.Start ?? BeginnNachDatum(jahr!);
        var bis = kal?.Ende ?? EndeNachDatum(jahr!);
        var frist = new LoeschFrist(w.SchuljahrBehaltenJahre, bis, jahr == w.Schuljahr);
        if (LoeschSperre(jahr!, zeile, frist, KlassenbuchService.Heute()) is string sperre)
            return new LoeschVorbereitung(409, sperre, null);

        // Schüler ohne Daten in einem anderen Schuljahr (268) – einmal festgelegt, damit Zählen und Löschen dieselben treffen
        var lids = db.Lektionen.Where(l => l.Schuljahr == jahr).Select(l => l.Id);
        var mitJahr = db.Zugehoerigkeiten.Where(z => z.Schuljahr == jahr).Select(z => z.SchuelerId);
        var anderesJahr = db.Zugehoerigkeiten.Where(z => z.Schuljahr != jahr).Select(z => z.SchuelerId);
        var andereStunden = db.Anwesenheiten.Where(a => !lids.Contains(a.LektionId)).Select(a => a.SchuelerId);
        var andereEintraege = db.KlassenbuchEintraege.Where(e => e.Schuljahr != jahr && e.SchuelerId != null)
            .Select(e => e.SchuelerId!.Value);
        var schueler = await db.Schueler.AsNoTracking()
            .Where(s => mitJahr.Contains(s.Id) && !anderesJahr.Contains(s.Id) && !andereStunden.Contains(s.Id)
                        && !andereEintraege.Contains(s.Id))
            .Select(s => s.Id).OrderBy(id => id).ToListAsync();
        var konten = await db.Benutzer.AsNoTracking()
            .Where(b => b.Rolle == BenutzerRolle.Schueler && b.SchuelerId != null && schueler.Contains(b.SchuelerId.Value))
            .Select(b => b.Id).OrderBy(id => id).ToListAsync();

        var (verlaufAb, verlaufBis) = VerlaufZeitraum(jahr!);
        var auswahl = new LoeschAuswahl(db, jahr!, von, bis, verlaufAb, verlaufBis, schueler, konten);
        var zahlen = await auswahl.ZaehlenAsync();
        return new LoeschVorbereitung(200, null,
            new LoeschPlan(jahr!, von, bis, verlaufAb, verlaufBis, schueler, konten, zahlen, zeile!));
    }

    /// <summary>Rückfrage mit den Zahlen (409). <paramref name="neu"/>: die Zahlen haben sich seit der letzten Rückfrage geändert.</summary>
    public static JaLoeschRueckfrageDto LoeschRueckfrage(LoeschPlan p, bool neu)
    {
        string jahr = p.Schuljahr;
        var z = p.Zeile;
        var (vAb, vBis) = (DateOnly.FromDateTime(p.VerlaufAb.ToLocalTime()), DateOnly.FromDateTime(p.VerlaufBis.ToLocalTime()).AddDays(-1));
        var hinweise = new List<string>
        {
            $"Zeitraum: Kalender {D(p.Von)} – {D(p.Bis)}. Entschuldigungen, die darüber hinausreichen, behalten den Teil " +
            $"außerhalb. Verlaufszeilen ohne Stunde (z. B. Entschuldigungen, Gruppenlisten) zählen nach ihrem Zeitpunkt " +
            $"({D(vAb)} – {D(vBis)}) – Nachträge danach bleiben.",
            "Es bleibt: die Zeile des Schuljahres mit den Archiv- und Löschangaben, die Protokolle mit eigener Frist (Konten, " +
            "Anmeldungen, Downloads, Einstellungen, SchILD-Importe, Leseprotokoll anderer Schüler – ohne Namen), Schülerinnen und " +
            "Schüler mit Daten in einem anderen Schuljahr, Lehrkräfte und Einstellungen.",
            $"Danach lässt sich {jahr} nicht wieder öffnen und nicht mehr exportieren; Vertretungen, Grundpläne und SchILD-Importe " +
            $"für {jahr} nimmt die API weiter nicht an.",
            $"Nicht in der App: Die Vertretungsdatei des Desktops (Vertretung_{jahr.Replace('/', '_')}.xlsx, mit den " +
            "Abwesenheitsgründen der Lehrkräfte) löscht die Schule selbst; ältere Sicherungen der Datenbank enthalten das " +
            "Schuljahr, bis sie nach ihrer Frist auslaufen.",
        };
        string archiv = $"Archiv-Export {Zeitpunkt(z.ArchivAm, z.ArchivVon)}" +
                        (z.ArchivGroesse is long g ? $", {Mb(g)}" : "") +
                        $", Prüfsumme {PruefsummeKurz(z.ArchivPruefsumme)}; bestätigt {Zeitpunkt(z.AufbewahrtAm, z.AufbewahrtVon)}: " +
                        $"Ort „{z.AufbewahrtOrt}“" + (z.AufbewahrenBis is DateOnly b ? $", aufzubewahren bis {D(b)}" : "");
        return new JaLoeschRueckfrageDto(true, jahr, $"Schuljahr {jahr} aus der Datenbank löschen?",
            neu ? "Inzwischen sind es andere Zahlen – bitte prüfen und noch einmal bestätigen."
                : $"Bitte bestätigen: Schuljahr {jahr} aus der Datenbank löschen.",
            p.Zahlen.Zeilen(jahr), p.Zahlen.Pruefsumme(jahr), hinweise, archiv, neu, $"Schuljahr {jahr} endgültig löschen");
    }

    // -----------------------------------------------------------------
    //  Löschen (eine Transaktion)
    // -----------------------------------------------------------------

    /// <summary>
    /// Löscht das Schuljahr laut <paramref name="plan"/> (vorher geprüft, gezählt und bestätigt). Eine Transaktion – bei einem
    /// Fehler wird nichts gelöscht (die Ausnahme geht an den Aufrufer).
    /// </summary>
    public static async Task<AktionErgebnis> LoeschenAusfuehrenAsync(AppDbContext db, LoeschPlan plan, string akteur, ILogger log)
    {
        var uhr = Stopwatch.StartNew();
        string jahr = plan.Schuljahr;
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(LoeschenMinuten));
        await using var tx = await db.Database.BeginTransactionAsync();

        // Innerhalb der Transaktion noch einmal: nicht inzwischen wieder geöffnet, gelöscht oder neu exportiert?
        var zeile = await db.Schuljahre.FirstOrDefaultAsync(s => s.Schuljahr == jahr);
        if (zeile == null || zeile.GeloeschtAm != null || zeile.Status != SchuljahrStatus.Abgeschlossen || !ArchivBestaetigt(zeile)
            || zeile.ArchivPruefsumme != plan.Zeile.ArchivPruefsumme || zeile.AufbewahrtAm != plan.Zeile.AufbewahrtAm)
            return new AktionErgebnis(409, Fehler: $"Der Stand von {jahr} hat sich inzwischen geändert – bitte „Aktualisieren“ und " +
                                                   "noch einmal versuchen. Es wurde nichts gelöscht.");

        var a = new LoeschAuswahl(db, jahr, plan.Von, plan.Bis, plan.VerlaufAb, plan.VerlaufBis, plan.SchuelerIds, plan.KontoIds);

        // 1. Zuständigkeiten für das Jahr entfallen (269) – wie „entfällt“ in P5b-2
        var zust = await a.QZustaendigkeiten().ToListAsync();
        foreach (var u in zust)
        {
            string vorher = KontenService.Zuordnung(u);
            string bisher = u.Zustaendig ?? "";
            if (u.Rolle is BenutzerRolle.Klassenlehrer or BenutzerRolle.Stufenkoordination) u.Rolle = BenutzerRolle.Lehrer;
            u.Zustaendig = null;
            u.ZustaendigSchuljahr = null;
            u.SicherheitsStempel = KontenService.NeuerStempel();   // Rolle und Zuständigkeit stehen im Token
            KontenService.Protokoll(db, akteur, KontenService.Aktion.Geaendert, u,
                $"Zuständigkeit {bisher} ({jahr}) entfällt – Schuljahr {jahr} aus der Datenbank gelöscht; {vorher} → " +
                $"{KontenService.Zuordnung(u)} (Jahresablauf)");
        }

        // 2. Konten der Schüler ohne Daten in einem anderen Schuljahr (268) – je Konto ein Eintrag im Kontenprotokoll
        var konten = await a.QKonten().ToListAsync();
        foreach (var k in konten)
        {
            KontenService.Protokoll(db, akteur, KontenService.Aktion.Geloescht, k,
                $"Schuljahr {jahr} aus der Datenbank gelöscht (keine Daten in einem anderen Schuljahr)");
            db.Benutzer.Remove(k);
        }
        await db.SaveChangesAsync();

        // 3. Verlauf und Leseprotokoll (ihre Abfragen brauchen die Stunden und Einträge noch)
        int verlauf = await a.QVerlauf().ExecuteDeleteAsync();
        int lese = await a.QLeseprotokoll().ExecuteDeleteAsync();

        // 4. Anwesenheiten – vor den Entschuldigungen (Verweis) und den Stunden
        int anwesenheiten = await a.QAnwesenheiten().ExecuteDeleteAsync();

        // 5. Entschuldigungen: übergreifende kürzen bzw. teilen, dann die übrigen löschen
        int gekuerzt = await EntschuldigungenKuerzenAsync(db, a, plan.Von, plan.Bis);
        int entschuldigungen = await a.QEntschuldigungenWeg().ExecuteDeleteAsync();

        // 6. Klassenbuch, Lerngruppen, Schüler
        int stunden = await a.QStunden().ExecuteDeleteAsync();
        int eintraege = await a.QEintraege().ExecuteDeleteAsync();
        int unterricht = await a.QUnterricht().ExecuteDeleteAsync();
        int zugehoerigkeiten = await a.QZugehoerigkeiten().ExecuteDeleteAsync();
        int lerngruppen = await a.QLerngruppen().ExecuteDeleteAsync();
        int schueler = await a.QSchueler().ExecuteDeleteAsync();

        // 7. Vertretungen, Grundplan, Kalender (Kinder zuerst)
        int vertretungen = await a.QVertretungen().ExecuteDeleteAsync();
        int tage = await a.QTage().ExecuteDeleteAsync();
        int grundplanStunden = await a.QGrundplanStunden().ExecuteDeleteAsync();
        int grundplaene = await a.QGrundplaene().ExecuteDeleteAsync();
        int freieZeiten = await a.QFreieZeiten().ExecuteDeleteAsync();
        int kalender = await a.QKalender().ExecuteDeleteAsync();

        var zahlen = new LoeschZahlen(stunden, anwesenheiten, eintraege, entschuldigungen, gekuerzt, verlauf, lerngruppen,
            zugehoerigkeiten, unterricht, tage, vertretungen, grundplaene, grundplanStunden, kalender, freieZeiten, schueler,
            konten.Count, lese, zust.Count);

        // 8. Zeile des Jahres und Protokoll
        var jetzt = DateTime.UtcNow;
        string text = zahlen.Kurztext();
        zeile.GeloeschtAm = jetzt;
        zeile.GeloeschtVon = akteur;
        zeile.GeloeschtText = text.Length > 2000 ? text[..2000] : text;
        Protokoll(db, akteur, $"Schuljahr {jahr} aus der Datenbank gelöscht",
            $"{text}. Grundlage: Archiv-Export {Zeitpunkt(zeile.ArchivAm, zeile.ArchivVon)} (Prüfsumme {zeile.ArchivPruefsumme}), " +
            $"Ort „{zeile.AufbewahrtOrt}“" + (zeile.AufbewahrenBis is DateOnly b ? $", aufzubewahren bis {D(b)}" : ""));
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        if (zahlen != plan.Zahlen)
            log.LogWarning("Jahresablauf: Beim Löschen von {Schuljahr} weichen die Zahlen von der Rückfrage ab (vorher {Vorher}; " +
                           "gelöscht {Nachher}).", jahr, plan.Zahlen.Kurztext(), text);
        log.LogInformation("Jahresablauf: Schuljahr {Schuljahr} aus der Datenbank gelöscht von {Akteur} in {Sekunden} s: {Zahlen}.",
            jahr, akteur, Math.Round(uhr.Elapsed.TotalSeconds, 1), text);
        return new AktionErgebnis(200, Meldung: $"Schuljahr {jahr} aus der Datenbank gelöscht: {text}. Die Daten stehen nur noch im " +
                                                $"Archiv-Export (Ort „{zeile.AufbewahrtOrt}“)." +
                                                (zust.Count > 0 ? $" {Anzahl(zust.Count, "Konto ist", "Konten sind")} jetzt Lehrkraft " +
                                                                  "und auf den Geräten abgemeldet." : ""));
    }

    /// <summary>
    /// Entschuldigungen, die über den Zeitraum hinausreichen, auf den Teil außerhalb kürzen; reicht eine auf beiden Seiten
    /// hinaus, wird sie geteilt (die Fehlstunden davor zeigen dann auf den vorderen Teil). Rückgabe: betroffene Entschuldigungen.
    /// </summary>
    private static async Task<int> EntschuldigungenKuerzenAsync(AppDbContext db, LoeschAuswahl a, DateOnly von, DateOnly bis)
    {
        var liste = await a.QEntschuldigungenKuerzen().ToListAsync();
        if (liste.Count == 0) return 0;
        var geteilt = new List<(int OriginalId, Entschuldigung Vorne)>();
        foreach (var e in liste)
        {
            bool davor = e.Von < von, danach = e.Bis > bis;
            if (davor && danach)
            {
                var teil = new Entschuldigung
                {
                    SchuelerId = e.SchuelerId, Von = e.Von, Bis = von.AddDays(-1), Grund = e.Grund, ErfasstVon = e.ErfasstVon,
                    ErfasstAm = e.ErfasstAm, IstPseudo = e.IstPseudo
                };
                db.Entschuldigungen.Add(teil);
                geteilt.Add((e.Id, teil));
                e.Von = bis.AddDays(1);
            }
            else if (davor) e.Bis = von.AddDays(-1);
            else e.Von = bis.AddDays(1);
        }
        await db.SaveChangesAsync();
        foreach (var (originalId, vorne) in geteilt)
        {
            DateOnly vVon = vorne.Von, vBis = vorne.Bis;
            var fehlstunden = await db.Anwesenheiten
                .Where(x => x.EntschuldigungId == originalId && x.Lektion!.Datum >= vVon && x.Lektion.Datum <= vBis)
                .ToListAsync();
            foreach (var f in fehlstunden) f.EntschuldigungId = vorne.Id;
        }
        if (geteilt.Count > 0) await db.SaveChangesAsync();
        return liste.Count;
    }

    // -----------------------------------------------------------------
    //  Hinweis nach dem Speichern der Frist (Einstellungen)
    // -----------------------------------------------------------------

    /// <summary>Ab wann welches abgeschlossene Schuljahr laut der neuen Frist löschbar ist (für die Antwort der Einstellungen).</summary>
    public static async Task<string> FristHinweisAsync(AppDbContext db, int jahre, string aktuell)
    {
        if (jahre <= 0)
            return "„Klassenbuchdaten ganzer Schuljahre behalten“ ist aus – ganze Schuljahre bleiben in der Datenbank; der " +
                   "Jahresablauf bietet kein Löschen an.";
        var offen = (await db.Schuljahre.AsNoTracking()
                .Where(s => s.Status == SchuljahrStatus.Abgeschlossen && s.GeloeschtAm == null)
                .Select(s => s.Schuljahr).ToListAsync())
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        var kalender = await db.Kalender.AsNoTracking().Select(k => new { k.Schuljahr, k.Ende }).ToListAsync();
        var heute = KlassenbuchService.Heute();
        var teile = offen.Select(sj =>
        {
            var ende = kalender.FirstOrDefault(k => k.Schuljahr == sj)?.Ende ?? EndeNachDatum(sj);
            var ab = LoeschbarAb(ende, jahre);
            return $"{sj} ab {D(ab)}" + (heute >= ab ? " (schon erreicht)" : "") +
                   (sj == aktuell ? " – ist aber das „Aktuelle Schuljahr“" : "");
        }).ToList();
        return "Gelöscht wird nichts von selbst: Ist die Frist erreicht und die Aufbewahrung des Archiv-Exports bestätigt, " +
               "erscheint im Jahresablauf (Karte „Archiv und Fristen“) der Knopf „Schuljahr … löschen…“." +
               (teile.Count > 0 ? " Abgeschlossene Schuljahre: " + string.Join(", ", teile) + "."
                                : " Noch ist kein Schuljahr abgeschlossen.");
    }
}
