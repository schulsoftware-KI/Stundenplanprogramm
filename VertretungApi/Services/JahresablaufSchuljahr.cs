using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;
using K = VertretungApi.Services.EinstellungsKatalog;

namespace VertretungApi.Services;

// =====================================================================
//  P5a – 5.3b Karte „Schuljahreswechsel“ im Jahresablauf (Entscheidungen 215–217, 239, 242).
//
//  Welches Schuljahr die Karte zeigt („Abschlussjahr“): das Vorjahr des aktuellen Schuljahres, solange es Daten hat
//  (Kalender bzw. Grundplan) und nicht abgeschlossen ist – „Aktuelles Schuljahr“ steht dann schon auf dem neuen Jahr (242);
//  sonst das aktuelle Schuljahr. Sichtbar ab 6 Wochen vor dem letzten Schultag laut Kalender (und immer, solange ein Vorjahr
//  offen bzw. das Jahr abgeschlossen ist). Letzter Schultag (Nachtrag P5a-a) = letzter Unterrichtstag des Rasters bis
//  „Ende“, der nicht in einer freien Zeit der ganzen Schule liegt (wie im Desktop „bis Schuljahresende“); „Ende“ selbst ist
//  nur die Grenze des Schuljahres (meist der 31.07., in den Sommerferien).
//
//  Punkte: 1. letzter Schultag · 2. offene Fehlstunden und Verspätungen · 3. nicht erfasste Stunden · 4. Rückweg nach SchILD
//  für das 2. Halbjahr · 5. Jahresabschluss (Knopf „abschließen…“ bzw. „Wieder öffnen…“) · 6. neues Schuljahr vorbereiten
//  (Knopf) · 7. seit P5b-1 Jahreswechsel-Import aus SchILD (erster Import des neuen Jahres, aus dem Importprotokoll) ·
//  8. Zuständigkeiten (seit P5b-2 mit Knopf „Zuständigkeiten für … übernehmen…“ und Ampel – JahresablaufZustaendigkeit.cs) ·
//  P5c-1: nach „Jahresabschluss“ der Punkt „Archiv-Export …“ mit Sprung zur Karte „Archiv und Fristen“ (beim abgeschlossenen
//  Vorjahr bei den weiteren Punkten, solange sein Archiv-Export nicht grün ist) ·
//  dazu weitere abgeschlossene Schuljahre (Knopf „Wieder öffnen…“); ist das Vorjahr schon abgeschlossen und noch ein Konto
//  nur dafür zuständig, steht der Punkt „Zuständigkeiten“ für den Wechsel ins aktuelle Schuljahr bei diesen.
//  1.–4. sind die Vorprüfung (215): nur Warnungen, keine Sperre.
//
//  Aktionen (nur Admin, Rückfrage, Protokoll der Einstellungen mit Schlüssel „Jahresablauf“, ohne Namen von Schülern):
//    Abschließen – Status „abgeschlossen“; danach ist das Klassenbuch des Jahres nur lesbar und nur für den Admin sichtbar.
//      War es das aktuelle Schuljahr, merkt sich seine Zeile „Anwesenheit/Thema erfasst ab“ und „2. Halbjahr ab“.
//    Wieder öffnen – mit Begründung (10–300 Zeichen); Status „laufend“.
//    Neues Schuljahr vorbereiten (217) – „Aktuelles Schuljahr“ → neues Jahr; „Anwesenheit/Thema erfasst ab“ und
//      „2. Halbjahr ab“, die nicht im neuen Schuljahr liegen, → Standard (je ein Eintrag im Protokoll der Einstellungen);
//      das bisherige Jahr merkt sich seine Werte; Zuständigkeiten ohne Schuljahr bleiben im bisherigen (219).
//      Kalender und Grundplan des neuen Jahres kommen vorher aus dem Desktop – fehlen sie, ist das nur eine Warnung.
// =====================================================================

public static partial class Jahresablauf
{
    /// <summary>So viele Tage vor dem letzten Schultag erscheint die Karte „Schuljahreswechsel“.</summary>
    public const int VorlaufSchuljahrTage = 42;

    /// <summary>Schlüssel im Protokoll der Einstellungen für Aktionen des Jahresablaufs.</summary>
    public const string ProtokollSchluessel = "Jahresablauf";

    public const int BegruendungMin = 10, BegruendungMax = 300;

    private static readonly Regex SchuljahrForm = new(@"^(\d{4})/(\d{2})$", RegexOptions.Compiled);

    /// <summary>„2026/27“ um <paramref name="n"/> Jahre verschoben („2027/28“); sonst unverändert.</summary>
    public static string Verschieben(string sj, int n)
    {
        var m = SchuljahrForm.Match(sj ?? "");
        if (!m.Success) return sj ?? "";
        int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + n;
        return $"{a}/{(a + 1) % 100:00}";
    }

    /// <summary>
    /// Kalender eines Schuljahres: Start/Ende = Grenzen des Schuljahres; LetzterSchultag = letzter Unterrichtstag
    /// (Nachtrag P5a-a; ohne Angabe = Ende). Nie in einer EF-Abfrage bauen (optionaler Parameter) – KalenderLadenAsync.
    /// </summary>
    public sealed record KalStand(string Schuljahr, DateOnly Start, DateOnly Ende, DateOnly? LetzterSchultag = null)
    {
        public DateOnly Letzter => LetzterSchultag ?? Ende;
    }

    /// <summary>
    /// Letzter Schultag (Nachtrag P5a-a): von „Ende“ rückwärts der erste Tag, der ein Unterrichtstag des Rasters ist und
    /// in keiner freien Zeit der ganzen Schule liegt. Ohne solchen Tag: „Ende“.
    /// </summary>
    public static DateOnly LetzterSchultagBerechnen(DateOnly start, DateOnly ende, IReadOnlySet<int> raster,
        IReadOnlyCollection<(DateOnly Von, DateOnly Bis)> frei)
    {
        for (var d = ende; d >= start; d = d.AddDays(-1))
        {
            if (!raster.Contains(WochenplanService.IsoTag(d))) continue;
            if (frei.Any(f => f.Von <= d && d <= f.Bis)) continue;
            return d;
        }
        return ende;
    }

    /// <summary>Kalender mit letztem Schultag (alle bzw. nur <paramref name="nurJahr"/>).</summary>
    private static async Task<List<KalStand>> KalenderLadenAsync(AppDbContext db, string? nurJahr = null)
    {
        var q = db.Kalender.AsNoTracking().Include(k => k.FreieZeiten).AsQueryable();
        if (nurJahr != null) q = q.Where(k => k.Schuljahr == nurJahr);
        var liste = await q.ToListAsync();
        return liste.Select(k =>
        {
            var frei = k.FreieZeiten.Where(f => string.IsNullOrWhiteSpace(f.Klassen)).Select(f => (f.Von, f.Bis)).ToList();
            return new KalStand(k.Schuljahr, k.Start, k.Ende,
                LetzterSchultagBerechnen(k.Start, k.Ende, WochenplanService.RasterTage(k), frei));
        }).ToList();
    }

    /// <summary>Was die Karte über die Schuljahre weiß (zum Prüfen im Nachbau ohne Datenbank).</summary>
    public sealed record JahrStand(string Abschlussjahr, string NeuesJahr, bool VorjahrOffen);

    /// <summary>Abschlussjahr: das offene Vorjahr (mit Kalender bzw. Grundplan), sonst das aktuelle Schuljahr (242).</summary>
    public static JahrStand Jahre(string aktuell, IReadOnlyCollection<string> mitDaten, IReadOnlyCollection<string> abgeschlossen)
    {
        string vorjahr = Verschieben(aktuell, -1);
        bool offen = mitDaten.Contains(vorjahr) && !abgeschlossen.Contains(vorjahr);
        string jahr = offen ? vorjahr : aktuell;
        return new JahrStand(jahr, Verschieben(jahr, 1), offen);
    }

    /// <summary>Werte von „erfasst ab“ und „2. Halbjahr ab“, die nicht im Schuljahr liegen (217: werden auf Standard gesetzt).</summary>
    public static bool LiegtIn(DateOnly d, string sj, KalStand? kal)
        => kal != null ? d >= kal.Start && d <= kal.Ende : K.SchuljahrVon(d) == sj;

    // -----------------------------------------------------------------
    //  Karte
    // -----------------------------------------------------------------

    private static async Task<JaKarteDto> SchuljahrKarteAsync(AppDbContext db, SchulWerte w, DateOnly heute, bool produktiv,
        string akteur)
    {
        const string titel = "Schuljahreswechsel";
        var zeilen = await db.Schuljahre.AsNoTracking().ToListAsync();
        var kalender = await KalenderLadenAsync(db);
        var mitPlan = await db.GrundplanVersionen.AsNoTracking().Select(v => v.Schuljahr).Distinct().ToListAsync();
        var mitDaten = kalender.Select(k => k.Schuljahr).Concat(mitPlan).ToHashSet(StringComparer.Ordinal);
        var zu = zeilen.Where(z => z.Status == SchuljahrStatus.Abgeschlossen).Select(z => z.Schuljahr)
            .ToHashSet(StringComparer.Ordinal);

        var stand = Jahre(w.Schuljahr, mitDaten, zu);
        string jahr = stand.Abschlussjahr;
        var kal = kalender.FirstOrDefault(k => k.Schuljahr == jahr);
        var zeile = zeilen.FirstOrDefault(z => z.Schuljahr == jahr);
        bool abgeschlossen = zu.Contains(jahr);

        // weitere abgeschlossene Schuljahre (Wieder öffnen)
        var weitere = zeilen.Where(z => z.Status == SchuljahrStatus.Abgeschlossen && z.Schuljahr != jahr)
            .OrderByDescending(z => z.Schuljahr, StringComparer.Ordinal).ToList();
        var weiterePunkte = new List<JaPunktDto>();
        // P5b-2 (249): Vorjahr schon abgeschlossen, aber noch Konten nur dafür zuständig → Punkt für den Wechsel ins aktuelle Jahr
        string vorjahrAkt = Verschieben(w.Schuljahr, -1);
        if (vorjahrAkt != w.Schuljahr && zu.Contains(vorjahrAkt)
            && await ZustaendigkeitPunktAsync(db, w, vorjahrAkt, w.Schuljahr, nurWennOffen: true) is JaPunktDto zv)
            weiterePunkte.Add(zv);
        // P5c-1 (271): Vorjahr abgeschlossen, sein Archiv-Export noch nicht erledigt → Punkt mit Sprung zu „Archiv und Fristen“
        if (vorjahrAkt != w.Schuljahr && vorjahrAkt != jahr && zu.Contains(vorjahrAkt)
            && zeilen.FirstOrDefault(x => x.Schuljahr == vorjahrAkt) is SchuljahrDb zeileVorjahr
            && ArchivSprungPunkt(zeileVorjahr, heute) is { Ampel: not Gruen } sprungVorjahr)
            weiterePunkte.Add(sprungVorjahr);
        if (weitere.Count > 0)
            weiterePunkte.Add(new JaPunktDto("abgeschlossen", "Abgeschlossene Schuljahre", Grau,
                "Nur lesbar und nur für die Verwaltung sichtbar (Klassenbuch, PDF, Archiv).",
                weitere.Take(MaxListe).Select(z => $"{z.Schuljahr}: abgeschlossen {Zeitpunkt(z.AbgeschlossenAm, z.AbgeschlossenVon)}")
                    .ToList(),
                Aktionen: weitere.Take(MaxListe)
                    .Select(z => new JaAktionDto("oeffnen", $"{z.Schuljahr} wieder öffnen…", z.Schuljahr,
                        Hinweis: ArchivHinweisOeffnen(z))).ToList()));

        if (kal == null && !abgeschlossen)
            return new JaKarteDto("schuljahr", titel, Grau,
                $"Für das Schuljahr {jahr} gibt es in der App noch keinen Kalender (Desktop: Wocheneditor → „Grundplan " +
                "veröffentlichen…“) – ohne ihn gibt es keinen letzten Schultag.", null, null, false, weiterePunkte);

        DateOnly? ab = kal?.Letzter.AddDays(-VorlaufSchuljahrTage);
        bool aktiv = abgeschlossen || stand.VorjahrOffen || (ab is DateOnly a0 && heute >= a0);
        if (!aktiv)
        {
            string text = $"Der letzte Schultag von {jahr} ist laut Kalender {Tag(kal!.Letzter)}. Jahresabschluss und neues " +
                          $"Schuljahr erscheinen hier ab dem {D(ab!.Value)} (6 Wochen vorher).";
            if (zeile?.VorbereitetAm != null)
                text = $"Das Schuljahr {jahr} wurde {Zeitpunkt(zeile.VorbereitetAm, zeile.VorbereitetVon)} vorbereitet. " + text;
            return new JaKarteDto("schuljahr", titel, Grau, text, kal.Letzter, ab, false, weiterePunkte);
        }

        var punkte = new List<JaPunktDto>();
        if (!abgeschlossen && kal != null)
            punkte.AddRange(await VorpruefungAsync(db, w, jahr, kal, zeile, heute, akteur));
        punkte.Add(AbschlussPunkt(jahr, kal, zeile, abgeschlossen, heute));
        if (abgeschlossen && zeile != null) punkte.Add(ArchivSprungPunkt(zeile, heute));   // P5c-1 (271)
        punkte.Add(await NeuesJahrPunktAsync(db, w, stand, kalender, zeilen, kal, heute));
        punkte.Add(await JahreswechselImportPunktAsync(db, stand.NeuesJahr, !produktiv));   // P5b-1 (218, 246)
        punkte.Add((await ZustaendigkeitPunktAsync(db, w, jahr, stand.NeuesJahr))!);   // P5b-2 (249)
        punkte.AddRange(weiterePunkte);

        string kopf = abgeschlossen
            ? $"Schuljahr {jahr}: abgeschlossen {Zeitpunkt(zeile?.AbgeschlossenAm, zeile?.AbgeschlossenVon)}."
            : $"Schuljahr {jahr}" + (kal != null ? $" (laut Kalender {D(kal.Start)} – {D(kal.Ende)}, letzter Schultag " +
                                                   $"{D(kal.Letzter)})" : "") +
              (stand.VorjahrOffen ? $" – „Aktuelles Schuljahr“ ist schon {w.Schuljahr}; {jahr} bleibt bis zum Abschluss für " +
                                    "Nachträge bearbeitbar." : ".");
        return new JaKarteDto("schuljahr", titel, Schlechteste(punkte.Select(p => p.Ampel)),
            kopf + " Reihenfolge: Jahresabschluss (nach dem letzten Schultag) → neues Schuljahr vorbereiten. Nichts geschieht " +
            "von selbst.", kal?.Letzter, ab, true, punkte);
    }

    private static string Zeitpunkt(DateTime? am, string? von)
    {
        if (am is not DateTime t) return "";
        var lokal = DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();
        return $"am {lokal.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}" + (string.IsNullOrWhiteSpace(von) ? "" : $" von {von}");
    }

    // ---- Vorprüfung (215) ----

    /// <summary>„Anwesenheit/Thema erfasst ab“ und „2. Halbjahr ab“ eines Schuljahres (aktuell: Einstellungen; sonst gemerkt).</summary>
    private static (DateOnly ErfassungAb, DateOnly ThemaAb, DateOnly? HalbjahrAb) DatenDesJahres(SchulWerte w, string jahr,
        KalStand kal, SchuljahrDb? zeile)
    {
        if (jahr == w.Schuljahr) return (w.ErfassungAb, w.ThemaAb, w.HalbjahrAb);
        var erfassung = zeile?.ErfassungAb ?? kal.Start;
        return (erfassung, zeile?.ThemaAb ?? erfassung, zeile?.HalbjahrAb);
    }

    private static async Task<List<JaPunktDto>> VorpruefungAsync(AppDbContext db, SchulWerte w, string jahr, KalStand kal,
        SchuljahrDb? zeile, DateOnly heute, string akteur)
    {
        var (erfassungAb, themaAb, hjEinstellung) = DatenDesJahres(w, jahr, kal, zeile);
        var punkte = new List<JaPunktDto> { EndePunkt(kal, heute) };

        // offene Fehlstunden und Verspätungen (bis heute)
        var bisOffen = kal.Ende < heute ? kal.Ende : heute;
        int fehlt = 0, versp = 0;
        if (bisOffen >= kal.Start)
        {
            fehlt = await db.Anwesenheiten.CountAsync(a => a.Lektion!.Datum >= kal.Start && a.Lektion.Datum <= bisOffen
                                                          && a.Status == AnwesenheitStatus.Fehlt && a.Bewertung == FehlBewertung.Offen);
            versp = await db.Anwesenheiten.CountAsync(a => a.Lektion!.Datum >= kal.Start && a.Lektion.Datum <= bisOffen
                                                          && a.Status == AnwesenheitStatus.Verspaetet && a.Bewertung == FehlBewertung.Offen);
        }
        punkte.Add(OffenJahrPunkt(fehlt, versp, kal, jahr == w.Schuljahr));

        // nicht erfasste Stunden (bis gestern)
        var admin = new KbNutzer(akteur, BenutzerRolle.Admin, null, Array.Empty<string>());
        var ne = await FehlzeitenService.NichtErfasstAsync(db, admin, "alle", null, null, erfassungAb, themaAb, "alle",
            kal.Start, kal.Ende);
        punkte.Add(NichtErfasstJahrPunkt(ne.OhneAnwesenheit, ne.OhneThema, kal, erfassungAb, jahr == w.Schuljahr));

        // Rückweg nach SchILD für das 2. Halbjahr (ohne 2. Halbjahr: für das Schuljahr)
        var h = FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, hjEinstellung);
        var noetigAb = h ?? (erfassungAb > kal.Start ? erfassungAb : kal.Start);
        var arten = SchildRueckweg.Arten;
        var liste = await db.DownloadProtokolle.AsNoTracking()
            .Where(p => arten.Contains(p.Art) && p.Von <= kal.Ende && p.Bis >= noetigAb)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Select(p => new RueckwegStand(p.Zeit, p.Akteur, p.Von, p.Bis, p.Art))
            .ToListAsync();
        punkte.Add(RueckwegPunkt(liste, noetigAb, kal.Ende, h != null ? "2. Halbjahr" : "Schuljahr"));
        return punkte;
    }

    public static JaPunktDto EndePunkt(KalStand kal, DateOnly heute)
    {
        const string titel = "Letzter Schultag";
        return heute > kal.Letzter
            ? new JaPunktDto("ende", titel, Gruen, $"Der letzte Schultag laut Kalender war {Tag(kal.Letzter)}.")
            : new JaPunktDto("ende", titel, Gelb,
                $"Der Unterricht läuft laut Kalender noch bis {Tag(kal.Letzter)}. Erst danach abschließen – sonst sind die " +
                "restlichen Tage schon nur noch lesbar.");
    }

    public static JaPunktDto OffenJahrPunkt(int fehlt, int versp, KalStand kal, bool aktuell)
    {
        const string titel = "Offene Fehlstunden und Verspätungen";
        var ziele = new[] { new JaZielDto("offeneFaelle", "Offene Fälle (Klassen)") };
        string zeitraum = $"{D(kal.Start)} – {D(kal.Ende)}";
        if (fehlt + versp == 0)
            return new JaPunktDto("offenJahr", titel, Gruen,
                $"Keine offenen Fehlstunden oder Verspätungen im Schuljahr ({zeitraum}).", Ziele: ziele);
        return new JaPunktDto("offenJahr", titel, Gelb,
            $"Noch weder entschuldigt noch unentschuldigt: {Anzahl(fehlt, "Fehlstunde", "Fehlstunden")}, " +
            $"{Anzahl(versp, "Verspätung", "Verspätungen")} ({zeitraum}). Nach dem Abschluss lassen sie sich nicht mehr bewerten." +
            (aktuell ? "" : " In „Offene Fälle“ den Zeitraum des Schuljahres wählen."), Ziele: ziele);
    }

    public static JaPunktDto NichtErfasstJahrPunkt(int ohneAnwesenheit, int ohneThema, KalStand kal, DateOnly erfassungAb, bool aktuell)
    {
        const string titel = "Nicht erfasste Stunden";
        var ziele = new[] { new JaZielDto("nichtErfasst", "Nicht erfasst") };
        string zeitraum = $"{D(kal.Start)} – {D(kal.Ende)}, Anwesenheit erfasst ab {D(erfassungAb)}";
        if (ohneAnwesenheit + ohneThema == 0)
            return new JaPunktDto("nichtErfasstJahr", titel, Gruen, $"Alle Stunden sind erfasst ({zeitraum}).", Ziele: ziele);
        return new JaPunktDto("nichtErfasstJahr", titel, Gelb,
            $"{Anzahl(ohneAnwesenheit, "Stunde", "Stunden")} ohne Anwesenheit, {ohneThema} ohne Thema ({zeitraum}). Nachträge gehen " +
            "nur bis zum Abschluss." + (aktuell ? "" : " Unter „Nicht erfasst“ den Zeitraum des Schuljahres wählen."), Ziele: ziele);
    }

    private static string Anzahl(int n, string eins, string viele) => $"{n} {(n == 1 ? eins : viele)}";

    // ---- Jahresabschluss (215) ----

    public static JaPunktDto AbschlussPunkt(string jahr, KalStand? kal, SchuljahrDb? zeile, bool abgeschlossen, DateOnly heute)
    {
        string titel = $"Jahresabschluss {jahr}";
        if (abgeschlossen)
            return new JaPunktDto("abschluss", titel, Gruen,
                $"Abgeschlossen {Zeitpunkt(zeile?.AbgeschlossenAm, zeile?.AbgeschlossenVon)}. Das Klassenbuch des Schuljahres ist nur " +
                "noch lesbar und nur für die Verwaltung sichtbar (lesen, PDF, Archiv); Vertretungen, Grundpläne und SchILD-Importe " +
                "für das Jahr nimmt die API nicht mehr an. Für Nachträge: wieder öffnen (mit Begründung, protokolliert).",
                Aktionen: new[] { new JaAktionDto("oeffnen", "Wieder öffnen…", jahr, Hinweis: ArchivHinweisOeffnen(zeile)) });
        string geoeffnet = zeile?.GeoeffnetAm != null
            ? $" Zuletzt wieder geöffnet {Zeitpunkt(zeile.GeoeffnetAm, zeile.GeoeffnetVon)} („{zeile.GeoeffnetGrund}“)."
            : "";
        bool dran = kal != null && heute > kal.Letzter;
        return new JaPunktDto("abschluss", titel, dran ? Gelb : Grau,
            "Noch nicht abgeschlossen." + geoeffnet + " Danach ist das Klassenbuch des Schuljahres nur noch lesbar und nur für " +
            "die Verwaltung sichtbar; Lehrkräfte, Klassenleitung, Stufenkoordination und Schüler sehen es nicht mehr. Die Punkte " +
            "oben sind nur Warnungen.",
            Aktionen: new[] { new JaAktionDto("abschliessen", $"Schuljahr {jahr} abschließen…", jahr, Gefahr: true) });
    }

    // ---- Neues Schuljahr vorbereiten (217) ----

    /// <summary>Was „Neues Schuljahr vorbereiten“ ändern würde (lesbar) – leer = nichts mehr zu tun.</summary>
    public sealed record Vorbereitung(List<string> Aenderungen, List<string> Warnungen, bool SchuljahrSetzen,
        List<string> AufStandard);

    private static async Task<Vorbereitung> VorbereitungAsync(AppDbContext db, Schuleinstellungen einst, string neu,
        IReadOnlyDictionary<string, Einstellung> roh, List<KalStand> kalender, string bisher)
    {
        var kalNeu = kalender.FirstOrDefault(k => k.Schuljahr == neu);
        var aenderungen = new List<string>();
        var warnungen = new List<string>();
        var aufStandard = new List<string>();

        string? sjRoh = einst.Roh(roh, K.Schuljahr);
        bool setzen = sjRoh != neu;
        if (setzen)
            aenderungen.Add($"„Aktuelles Schuljahr“: {K.Text(K.Finde(K.Schuljahr)!, sjRoh)} → {neu}");
        foreach (var schluessel in new[] { K.ErfassungAb, K.ThemaAb, K.HalbjahrAb })
        {
            var d = K.Finde(schluessel)!;
            var wert = K.AlsDatum(einst.Roh(roh, schluessel));
            if (wert is DateOnly t && !LiegtIn(t, neu, kalNeu))
            {
                aufStandard.Add(schluessel);
                aenderungen.Add($"„{d.Titel}“: {K.Text(d, einst.Roh(roh, schluessel))} → Standard ({d.StandardText}) – danach neu " +
                                "eintragen, wenn nötig");
            }
        }
        int ohneJahr = await db.Benutzer.CountAsync(b => b.Zustaendig != null && b.Zustaendig != ""
                                                         && (b.ZustaendigSchuljahr == null || b.ZustaendigSchuljahr == ""));
        if (setzen && ohneJahr > 0)
            aenderungen.Add($"{Anzahl(ohneJahr, "Zuständigkeit", "Zuständigkeiten")} ohne Schuljahr gelten ab jetzt für {bisher} " +
                            "(Kontenprotokoll)");

        if (kalNeu == null)
            warnungen.Add($"Für {neu} gibt es noch keinen Kalender – erst im Desktop „Grundplan veröffentlichen…“ für {neu}. Bis " +
                          "dahin rechnet „Anwesenheit erfasst ab“ mit heute, und der Plan zeigt „Außerhalb des Schuljahres“.");
        if (!await db.GrundplanVersionen.AnyAsync(v => v.Schuljahr == neu))
            warnungen.Add($"Für {neu} ist noch kein Grundplan veröffentlicht (Desktop: Wocheneditor → „Grundplan veröffentlichen…“).");
        if (setzen && !await SchuljahrRechte.IstAbgeschlossenAsync(db, bisher))
            warnungen.Add($"{bisher} ist noch nicht abgeschlossen – es bleibt für Nachträge bearbeitbar, bis du es abschließt.");
        int zust = await db.Benutzer.CountAsync(b => b.Zustaendig != null && b.Zustaendig != ""
                                                     && (b.ZustaendigSchuljahr == bisher || b.ZustaendigSchuljahr == null
                                                         || b.ZustaendigSchuljahr == ""));
        if (setzen && zust > 0)
            warnungen.Add($"{Anzahl(zust, "Konto", "Konten")} mit Zuständigkeit (Klassenleitung, Stufenkoordination) gelten weiter " +
                          $"nur für {bisher}: Diese Personen sehen die Daten von {bisher} (bis zum Abschluss), aber nicht die " +
                          $"Klassen von {neu}, und betreuen keine Schülerkonten mehr. Nach dem Jahreswechsel-Import: hier „Zuständigkeiten " +
                          $"für {neu} übernehmen…“ (Vorschläge laut SchILD bzw. Folgeklasse).");
        return new Vorbereitung(aenderungen, warnungen, setzen, aufStandard);
    }

    private static async Task<JaPunktDto> NeuesJahrPunktAsync(AppDbContext db, SchulWerte w, JahrStand stand,
        List<KalStand> kalender, List<SchuljahrDb> zeilen, KalStand? kal, DateOnly heute)
    {
        string neu = stand.NeuesJahr;
        string titel = $"Neues Schuljahr {neu} vorbereiten";
        bool kalNeu = kalender.Any(k => k.Schuljahr == neu);
        bool planNeu = await db.GrundplanVersionen.AnyAsync(v => v.Schuljahr == neu);
        var zeileNeu = zeilen.FirstOrDefault(z => z.Schuljahr == neu);
        string desktop = (kalNeu ? $"Kalender {neu} ist da" : $"Kalender {neu} fehlt noch") + ", " +
                         (planNeu ? $"Grundplan {neu} ist da" : $"Grundplan {neu} fehlt noch") +
                         (kalNeu && planNeu ? "." : " (Desktop: Wocheneditor → „Grundplan veröffentlichen…“ für " + neu + ").");
        var aktion = new[] { new JaAktionDto("vorbereiten", $"Schuljahr {neu} vorbereiten…", neu) };

        if (w.Schuljahr == neu)
        {
            // Schon umgestellt: eingetragene Daten des bisherigen Jahres in den Einstellungen?
            var kalN = kalender.FirstOrDefault(k => k.Schuljahr == neu);
            var roh = await db.Einstellungen.AsNoTracking()
                .Where(e => e.Schluessel == K.ErfassungAb || e.Schluessel == K.ThemaAb || e.Schluessel == K.HalbjahrAb)
                .ToDictionaryAsync(e => e.Schluessel, e => e.Wert, StringComparer.Ordinal);
            var alt = new List<string>();
            foreach (var key in new[] { K.ErfassungAb, K.ThemaAb, K.HalbjahrAb })
                if (roh.TryGetValue(key, out var wert) && K.AlsDatum(wert) is DateOnly t && !LiegtIn(t, neu, kalN))
                    alt.Add($"„{K.Finde(key)!.Titel}“");
            string vorbereitet = zeileNeu?.VorbereitetAm != null
                ? $" Vorbereitet {Zeitpunkt(zeileNeu.VorbereitetAm, zeileNeu.VorbereitetVon)}." : "";
            if (alt.Count > 0)
                return new JaPunktDto("neuesJahr", titel, Gelb,
                    $"„Aktuelles Schuljahr“ ist {neu}, aber {string.Join(" und ", alt)} gehören noch zum bisherigen Schuljahr." +
                    vorbereitet + " " + desktop, Aktionen: aktion);
            return new JaPunktDto("neuesJahr", titel, kalNeu && planNeu ? Gruen : Gelb,
                $"„Aktuelles Schuljahr“ ist {neu}.{vorbereitet} {desktop} Danach: Jahreswechsel-Import aus SchILD (Punkt darunter) " +
                "und Zuständigkeiten.");
        }

        bool dran = kal != null && heute > kal.Letzter;
        return new JaPunktDto("neuesJahr", titel, dran ? Gelb : Grau,
            $"„Aktuelles Schuljahr“ ist noch {w.Schuljahr}. {desktop} Der Knopf stellt „Aktuelles Schuljahr“ auf {neu} und setzt " +
            "„Anwesenheit/Thema erfasst ab“ und „2. Halbjahr ab“ des bisherigen Jahres auf Standard (mit Rückfrage, protokolliert). " +
            $"Ob {w.Schuljahr} vorher abgeschlossen ist, ist egal – bis zum Abschluss bleibt es für Nachträge bearbeitbar.",
            Aktionen: aktion);
    }

    // ---- Jahreswechsel-Import aus SchILD (P5b-1, 218, 246–248) ----

    /// <summary>Erster SchILD-Import des neuen Schuljahres (dieser Art) aus dem Importprotokoll – grau, solange es keinen gibt.</summary>
    private static async Task<JaPunktDto> JahreswechselImportPunktAsync(AppDbContext db, string neu, bool pseudo)
    {
        var liste = await db.SchildImportProtokolle.AsNoTracking()
            .Where(x => x.Schuljahr == neu && x.IstPseudo == pseudo)
            .OrderBy(x => x.Zeit).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Zeit, x.GiltAb, x.Neu, x.Klassenwechsel, x.Kurswechsel, x.Abgaenge })
            .ToListAsync();
        var erster = liste.Select(x => new ImportStand(x.Id, x.Zeit, x.GiltAb, x.Neu, x.Klassenwechsel, x.Kurswechsel, x.Abgaenge))
            .FirstOrDefault();
        return JahreswechselImportPunkt(neu, erster, liste.Count, pseudo);
    }

    public static JaPunktDto JahreswechselImportPunkt(string neu, ImportStand? erster, int anzahl, bool pseudo)
    {
        string titel = $"Jahreswechsel-Import aus SchILD {neu}";
        var ziel = new[] { new JaZielDto("schildImporte", "SchILD-Importe") };
        string art = pseudo ? " (Pseudodaten)" : "";
        if (erster == null)
            return new JaPunktDto("jahreswechselImport", titel, Grau,
                $"Noch kein SchILD-Import für {neu}{art}. Nach „Schuljahr {neu} vorbereiten…“: in SchILD das neue Schuljahr " +
                $"exportieren, dann im Desktop (Wocheneditor mit dem Schuljahr {neu}) „SchILD-Daten…“. Die Vorschau vergleicht " +
                $"mit {Verschieben(neu, -1)}: versetzt, wiederholt, Abgänge, fehlt im Export.", Ziele: ziel);
        var am = DateOnly.FromDateTime(DateTime.SpecifyKind(erster.Zeit, DateTimeKind.Utc).ToLocalTime());
        return new JaPunktDto("jahreswechselImport", titel, Gruen,
            $"Import Nr. {erster.Id} vom {D(am)}{art}, gilt ab {D(erster.GiltAb)}: neu {erster.Neu}, Abgänge {erster.Abgaenge}, " +
            $"versetzt bzw. Klassenwechsel {erster.Klassenwechsel}." +
            (anzahl > 1 ? $" Danach {Anzahl(anzahl - 1, "weiterer Import", "weitere Importe")}." : ""), Ziele: ziel);
    }

    // ---- Zuständigkeiten (219) – seit P5b-2 mit Knopf und Ampel: JahresablaufZustaendigkeit.cs ----

    // -----------------------------------------------------------------
    //  Aktionen
    // -----------------------------------------------------------------

    /// <summary>Ergebnis einer Aktion: 200 (Meldung), 409 mit Rückfrage, sonst Status + Fehler.</summary>
    public sealed record AktionErgebnis(int Status, string? Meldung = null, string? Fehler = null, JaRueckfrageDto? Rueckfrage = null);

    private static string? SchuljahrLesen(string? eingabe, out string? fehler)
    {
        var d = K.Finde(K.Schuljahr)!;
        var sj = K.Normalisieren(d, eingabe, out fehler);
        if (fehler == null && sj == null) fehler = "Das Schuljahr fehlt.";
        return sj;
    }

    private static void Protokoll(AppDbContext db, string akteur, string neu, string? details)
        => db.EinstellungsProtokolle.Add(new EinstellungsProtokoll
        {
            Zeit = DateTime.UtcNow, Akteur = akteur, Schluessel = ProtokollSchluessel, Neu = neu, Details = details
        });

    /// <summary>„Schuljahr abschließen“ (215): ohne Bestätigung die Rückfrage mit der Vorprüfung.</summary>
    public static async Task<AktionErgebnis> AbschliessenAsync(AppDbContext db, Schuleinstellungen einst, string? eingabe,
        bool bestaetigt, string akteur, ILogger log)
    {
        var jahr = SchuljahrLesen(eingabe, out var fehler);
        if (fehler != null) return new AktionErgebnis(400, Fehler: fehler);
        var w = await einst.LesenAsync(db);
        var kal = (await KalenderLadenAsync(db, jahr)).FirstOrDefault();
        if (kal == null)
            return new AktionErgebnis(400, Fehler: $"Für das Schuljahr {jahr} gibt es keinen Kalender – es lässt sich nicht abschließen.");
        var zeile = await db.Schuljahre.FirstOrDefaultAsync(s => s.Schuljahr == jahr);
        if (zeile?.Status == SchuljahrStatus.Abgeschlossen)
            return new AktionErgebnis(409, Fehler: $"Das Schuljahr {jahr} ist schon abgeschlossen.");
        if (string.CompareOrdinal(jahr, w.Schuljahr) > 0)
            return new AktionErgebnis(400, Fehler: $"{jahr} liegt nach dem aktuellen Schuljahr ({w.Schuljahr}).");

        var heute = KlassenbuchService.Heute();
        var punkte = await VorpruefungAsync(db, w, jahr!, kal, zeile, heute, akteur);
        var warnungen = punkte.Where(p => p.Ampel != Gruen).Select(p => $"{p.Titel}: {p.Text}").ToList();
        if (!bestaetigt)
            return new AktionErgebnis(409, Rueckfrage: new JaRueckfrageDto(true, $"Schuljahr {jahr} abschließen?",
                $"Bitte bestätigen: Schuljahr {jahr} abschließen.",
                warnungen,
                new List<string>
                {
                    $"Das Klassenbuch von {jahr} (Anwesenheit, Entschuldigungen, Einträge, Thema, Hausaufgaben, Gruppen) ist danach " +
                    "nur noch lesbar – auch für dich.",
                    $"Sichtbar nur noch für die Verwaltung (lesen, PDF, Archiv); Lehrkräfte, Klassenleitung, Stufenkoordination und " +
                    $"Schüler sehen {jahr} nicht mehr – auch nicht im Plan.",
                    $"Vertretungen, Grundpläne und SchILD-Importe für {jahr} nimmt die API nicht mehr an.",
                    "Wieder öffnen geht hier mit Begründung (protokolliert).",
                },
                $"Schuljahr {jahr} abschließen"));

        var jetzt = DateTime.UtcNow;
        if (zeile == null)
        {
            zeile = new SchuljahrDb { Schuljahr = jahr! };
            db.Schuljahre.Add(zeile);
        }
        zeile.Status = SchuljahrStatus.Abgeschlossen;
        zeile.AbgeschlossenAm = jetzt;
        zeile.AbgeschlossenVon = akteur;
        if (jahr == w.Schuljahr)   // die Daten des Jahres für später merken (217)
        {
            zeile.ErfassungAb ??= w.ErfassungAb;
            zeile.ThemaAb ??= w.ThemaAb;
            zeile.HalbjahrAb ??= w.HalbjahrAb;
        }
        Protokoll(db, akteur, $"Schuljahr {jahr} abgeschlossen",
            warnungen.Count == 0 ? "Vorprüfung ohne Warnung"
                : "Vorprüfung mit Warnung: " + string.Join("; ", punkte.Where(p => p.Ampel != Gruen).Select(p => p.Titel)));
        await db.SaveChangesAsync();
        log.LogInformation("Jahresablauf: Schuljahr {Schuljahr} abgeschlossen von {Akteur} ({Warnungen} Warnung(en)).",
            jahr, akteur, warnungen.Count);
        return new AktionErgebnis(200, Meldung: $"Schuljahr {jahr} abgeschlossen – das Klassenbuch ist jetzt nur noch lesbar " +
                                                "und nur für die Verwaltung sichtbar.");
    }

    /// <summary>„Wieder öffnen“ (215) – nur mit Begründung.</summary>
    public static async Task<AktionErgebnis> OeffnenAsync(AppDbContext db, string? eingabe, string? begruendung, string akteur,
        ILogger log)
    {
        var jahr = SchuljahrLesen(eingabe, out var fehler);
        if (fehler != null) return new AktionErgebnis(400, Fehler: fehler);
        var grund = Regex.Replace((begruendung ?? "").Trim(), @"\s+", " ");
        if (grund.Any(char.IsControl)) return new AktionErgebnis(400, Fehler: "Die Begründung bitte ohne Steuerzeichen.");
        if (grund.Length < BegruendungMin)
            return new AktionErgebnis(400, Fehler: $"Bitte begründen, warum das Schuljahr wieder geöffnet wird (mindestens " +
                                                   $"{BegruendungMin} Zeichen, z. B. „Nachträge Fehlzeiten 10B“).");
        if (grund.Length > BegruendungMax)
            return new AktionErgebnis(400, Fehler: $"Die Begründung ist zu lang (höchstens {BegruendungMax} Zeichen).");
        var zeile = await db.Schuljahre.FirstOrDefaultAsync(s => s.Schuljahr == jahr);
        if (zeile == null || zeile.Status != SchuljahrStatus.Abgeschlossen)
            return new AktionErgebnis(409, Fehler: $"Das Schuljahr {jahr} ist nicht abgeschlossen.");
        if (zeile.GeloeschtAm != null)
            return new AktionErgebnis(409, Fehler: $"Die Daten des Schuljahres {jahr} sind gelöscht – es lässt sich nicht wieder öffnen.");

        // P5c-1 (265): ein vorhandener Archiv-Export gilt danach als veraltet (ArchivAm < GeoeffnetAm)
        bool mitExport = zeile.ArchivAm != null;
        zeile.Status = SchuljahrStatus.Laufend;
        zeile.GeoeffnetAm = DateTime.UtcNow;
        zeile.GeoeffnetVon = akteur;
        zeile.GeoeffnetGrund = grund;
        Protokoll(db, akteur, $"Schuljahr {jahr} wieder geöffnet", $"Begründung: {grund}" +
            (mitExport ? $"; der Archiv-Export {Zeitpunkt(zeile.ArchivAm, zeile.ArchivVon)} gilt jetzt als veraltet" : ""));
        await db.SaveChangesAsync();
        log.LogInformation("Jahresablauf: Schuljahr {Schuljahr} wieder geöffnet von {Akteur}.", jahr, akteur);
        return new AktionErgebnis(200, Meldung: $"Schuljahr {jahr} wieder geöffnet – Lehrkräfte können wieder nachtragen. Danach " +
                                                "erneut abschließen." +
                                                (mitExport ? " Der bisherige Archiv-Export gilt jetzt als veraltet – nach dem " +
                                                             "Abschluss bitte neu erstellen." : ""));
    }

    /// <summary>„Neues Schuljahr vorbereiten“ (217); <paramref name="eingabe"/> = das neue Schuljahr.</summary>
    public static async Task<AktionErgebnis> VorbereitenAsync(AppDbContext db, Schuleinstellungen einst, string? eingabe,
        bool bestaetigt, string akteur, ILogger log)
    {
        var neu = SchuljahrLesen(eingabe, out var fehler);
        if (fehler != null) return new AktionErgebnis(400, Fehler: fehler);
        var w = await einst.LesenAsync(db);
        string bisher = w.Schuljahr;
        if (neu != bisher && neu != Verschieben(bisher, 1))
            return new AktionErgebnis(400, Fehler: $"Vorbereiten geht nur für das nächste Schuljahr ({Verschieben(bisher, 1)}).");
        if (neu == bisher) bisher = Verschieben(neu!, -1);

        var roh = await db.Einstellungen.AsNoTracking().ToDictionaryAsync(e => e.Schluessel, StringComparer.Ordinal);
        var kalender = await KalenderLadenAsync(db);
        var plan = await VorbereitungAsync(db, einst, neu!, roh, kalender, bisher);
        if (!bestaetigt)
        {
            if (plan.Aenderungen.Count == 0)
                return new AktionErgebnis(200, Meldung: $"Schuljahr {neu} ist schon vorbereitet – es gibt nichts zu ändern.");
            return new AktionErgebnis(409, Rueckfrage: new JaRueckfrageDto(true, $"Schuljahr {neu} vorbereiten?",
                $"Bitte bestätigen: Schuljahr {neu} vorbereiten.", plan.Warnungen, plan.Aenderungen, $"Schuljahr {neu} vorbereiten"));
        }

        var jetzt = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync();

        // 1. Das bisherige Jahr merkt sich seine Daten (für den späteren Jahresabschluss bzw. „Nicht erfasst“).
        var zeileAlt = await db.Schuljahre.FirstOrDefaultAsync(s => s.Schuljahr == bisher);
        if (zeileAlt == null)
        {
            zeileAlt = new SchuljahrDb { Schuljahr = bisher };
            db.Schuljahre.Add(zeileAlt);
        }
        if (bisher == w.Schuljahr)
        {
            zeileAlt.ErfassungAb ??= w.ErfassungAb;
            zeileAlt.ThemaAb ??= w.ThemaAb;
            zeileAlt.HalbjahrAb ??= w.HalbjahrAb;
        }
        else
        {
            // Schon umgestellt (z. B. nach dem Datum): eingetragene Werte des bisherigen Jahres vor dem Zurücksetzen merken.
            var kalAlt = kalender.FirstOrDefault(k => k.Schuljahr == bisher);
            DateOnly? Alt(string key)
                => K.AlsDatum(einst.Roh(roh, key)) is DateOnly t && LiegtIn(t, bisher, kalAlt) ? t : null;
            zeileAlt.ErfassungAb ??= Alt(K.ErfassungAb);
            zeileAlt.ThemaAb ??= Alt(K.ThemaAb);
            zeileAlt.HalbjahrAb ??= Alt(K.HalbjahrAb);
        }

        // 2. Zuständigkeiten ohne Schuljahr bleiben im bisherigen (219).
        int festgeschrieben = 0;
        if (plan.SchuljahrSetzen)
            festgeschrieben = await SchuljahrRechte.ZustaendigkeitenFestschreibenAsync(db, bisher, akteur,
                $"Neues Schuljahr {neu} vorbereitet");

        // 3. Einstellungen: „Aktuelles Schuljahr“ und die Daten des bisherigen Jahres (je ein Eintrag im Protokoll).
        var zeilen = await db.Einstellungen.ToDictionaryAsync(e => e.Schluessel, StringComparer.Ordinal);
        void Setzen(string key, string? wert)
        {
            var d = K.Finde(key)!;
            string? alt = einst.Roh(zeilen, key);
            if (alt == wert) return;
            if (!zeilen.TryGetValue(key, out var z))
            {
                z = new Einstellung { Schluessel = key };
                db.Einstellungen.Add(z);
                zeilen[key] = z;
            }
            z.Wert = wert;
            z.GeaendertAm = jetzt;
            z.GeaendertVon = akteur;
            db.EinstellungsProtokolle.Add(new EinstellungsProtokoll
            {
                Zeit = jetzt, Akteur = akteur, Schluessel = key, Alt = K.Text(d, alt), Neu = K.Text(d, wert),
                Details = $"Jahresablauf: neues Schuljahr {neu} vorbereitet"
            });
        }
        if (plan.SchuljahrSetzen) Setzen(K.Schuljahr, neu);
        foreach (var schluessel in plan.AufStandard) Setzen(schluessel, null);

        // 4. Zeile des neuen Jahres
        var zeileNeu = await db.Schuljahre.FirstOrDefaultAsync(s => s.Schuljahr == neu);
        if (zeileNeu == null)
        {
            zeileNeu = new SchuljahrDb { Schuljahr = neu! };
            db.Schuljahre.Add(zeileNeu);
        }
        zeileNeu.VorbereitetAm = jetzt;
        zeileNeu.VorbereitetVon = akteur;
        Protokoll(db, akteur, $"Neues Schuljahr {neu} vorbereitet",
            string.Join("; ", plan.Aenderungen) + (plan.Warnungen.Count > 0 ? $" – {plan.Warnungen.Count} Warnung(en)" : ""));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        einst.Verwerfen();
        log.LogInformation("Jahresablauf: Schuljahr {Neu} vorbereitet von {Akteur} ({Aenderungen} Änderung(en), {Fest} " +
                           "Zuständigkeit(en) festgeschrieben).", neu, akteur, plan.Aenderungen.Count, festgeschrieben);
        return new AktionErgebnis(200, Meldung: $"Schuljahr {neu} vorbereitet – „Aktuelles Schuljahr“ ist jetzt {neu}." +
            (plan.AufStandard.Count > 0 ? " Die Daten des bisherigen Schuljahres stehen wieder auf Standard (Einstellungen → Schuljahr)." : ""));
    }
}
