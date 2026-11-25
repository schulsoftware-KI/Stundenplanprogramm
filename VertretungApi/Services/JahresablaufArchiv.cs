using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;
using K = VertretungApi.Services.EinstellungsKatalog;

namespace VertretungApi.Services;

// =====================================================================
//  P5c-1 – Karte „Archiv und Fristen“ im Jahresablauf (Entscheidungen 259–265, 271–273; Vorschlag P5c §3–§5).
//
//  Je abgeschlossenem Schuljahr ein Punkt mit Ampel:
//    gelb  „Noch kein Archiv-Export“ bzw. „Export vom … ist veraltet“ (erstellt vor dem letzten Abschluss, 265)
//          → Knopf „Archiv-Export … erstellen…“
//    gelb  Export da, noch nicht bestätigt → „Aufbewahrung bestätigen…“ (und „erneut erstellen…“)
//    grün  geprüft und aufbewahrt (Ort, aufzubewahren bis) – ist das Datum erreicht, ein grauer Punkt „vernichten“
//    grau  wieder geöffnet (Export gilt danach als veraltet) bzw. gelöscht (P5c-2)
//  Dazu der Punkt „Fristen“ (Löschfristen der Karte „Datenschutz“ mit „abgestimmt am / mit“; gelb, solange eine nicht
//  abgestimmt ist) und in der Karte „Schuljahreswechsel“ nach dem Jahresabschluss ein Punkt mit Sprung hierher.
//  Ohne abgeschlossenes Schuljahr ist die Karte grau („Erst nach einem Jahresabschluss“). Das Löschen ganzer Schuljahre
//  (Frist „Klassenbuchdaten behalten“) kommt mit P5c-2.
//
//  Aktionen (nur Admin, ohne Namen von Schülern in Protokoll und Log):
//    ArchivPlanAsync     – prüft und plant den Archiv-Export (abgeschlossen, nicht gelöscht, mit Kalender); den Auftrag
//                          im Hintergrund startet der Controller (ArchivAuftraege)
//    ArchivAbgeholtAsync – nach dem Download: Zeile „Schuljahre“ (ArchivAm/-Von/-Groesse/-Pruefsumme/-Mit…; eine vorhandene
//                          Bestätigung gilt nicht mehr, 263), Download-Protokoll („ArchivExport“ bzw. „Archiv“ für 4.3),
//                          Protokoll der Einstellungen, Log
//    ArchivBestaetigenAsync – „geprüft und aufbewahrt“ nur mit passender Prüfsumme, Haken, Ort und „aufzubewahren bis“ (264)
// =====================================================================

public static partial class Jahresablauf
{
    /// <summary>Art im Download-Protokoll für den Archiv-Export eines Schuljahres (P5c-1, 263).</summary>
    public const string ArtArchivExport = "ArchivExport";

    /// <summary>Art im Download-Protokoll für das Jahresarchiv aus 4.3 (wie bisher).</summary>
    public const string ArtJahresarchiv = "Archiv";

    public const int OrtMin = 3, OrtMax = 200;

    private static readonly Regex HexForm = new("^[0-9A-F]{64}$", RegexOptions.Compiled);
    private static readonly Regex LeerraumForm = new(@"\s+", RegexOptions.Compiled);

    /// <summary>„184 MB“ bzw. „0,4 MB“.</summary>
    public static string Mb(long bytes)
    {
        double mb = bytes / 1048576.0;
        string t = mb >= 10 ? Math.Round(mb).ToString("0", CultureInfo.InvariantCulture)
            : mb.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        return $"{t} MB";
    }

    /// <summary>„3F2A…C91D“ (Anfang und Ende der SHA-256).</summary>
    public static string PruefsummeKurz(string? sha)
        => string.IsNullOrEmpty(sha) || sha.Length < 8 ? sha ?? "" : $"{sha[..4]}…{sha[^4..]}";

    /// <summary>Prüfsumme vereinheitlichen: ohne Leerraum, Großbuchstaben (wie Get-FileHash). null = keine SHA-256.</summary>
    public static string? PruefsummeLesen(string? eingabe)
    {
        var s = LeerraumForm.Replace(eingabe ?? "", "").ToUpperInvariant();
        return HexForm.IsMatch(s) ? s : null;
    }

    /// <summary>Export erstellt vor dem letzten Abschluss (wieder geöffnet und erneut abgeschlossen, 265)?</summary>
    public static bool ArchivVeraltet(SchuljahrDb z)
        => z.ArchivAm is DateTime a
           && ((z.AbgeschlossenAm is DateTime ab && a < ab) || (z.GeoeffnetAm is DateTime g && a < g));

    /// <summary>Bestätigt „geprüft und aufbewahrt“ – nach dem (gültigen) Export.</summary>
    public static bool ArchivBestaetigt(SchuljahrDb z)
        => z.ArchivAm is DateTime a && z.AufbewahrtAm is DateTime b && b >= a && !ArchivVeraltet(z);

    private static string ArchivBeschreibung(SchuljahrDb z)
        => $"Erstellt {Zeitpunkt(z.ArchivAm, z.ArchivVon)}" +
           (z.ArchivGroesse is long g ? $", {Mb(g)}" : "") +
           (z.ArchivMitSchuelerEintraegen == true ? ", mit Schülereinträgen" : ", ohne Schülereinträge") +
           (string.IsNullOrEmpty(z.ArchivPruefsumme) ? "" : $", Prüfsumme {PruefsummeKurz(z.ArchivPruefsumme)}");

    /// <summary>Hinweis für die Rückfrage von „Wieder öffnen“ (265) – null ohne Export.</summary>
    public static string? ArchivHinweisOeffnen(SchuljahrDb? z)
    {
        if (z?.ArchivAm == null || z.GeloeschtAm != null) return null;
        return $"Es gibt einen Archiv-Export {Zeitpunkt(z.ArchivAm, z.ArchivVon)}. Nach dem Wiederöffnen gilt er als veraltet: " +
               "Nach dem erneuten Abschluss bitte einen neuen Archiv-Export erstellen und dessen Aufbewahrung bestätigen.";
    }

    /// <summary>Ampel und Kurztext eines Schuljahres (für den Punkt in „Schuljahreswechsel“).</summary>
    public static (string Ampel, string Text) ArchivStand(SchuljahrDb z, DateOnly heute)
    {
        if (z.GeloeschtAm != null) return (Grau, $"Gelöscht {Zeitpunkt(z.GeloeschtAm, z.GeloeschtVon)}.");
        if (z.Status != SchuljahrStatus.Abgeschlossen)
            return (Grau, z.ArchivAm != null
                ? $"Wieder geöffnet – der Archiv-Export {Zeitpunkt(z.ArchivAm, z.ArchivVon)} gilt danach als veraltet."
                : "Erst nach dem Jahresabschluss.");
        if (z.ArchivAm == null) return (Gelb, "Noch kein Archiv-Export.");
        if (ArchivVeraltet(z))
            return (Gelb, $"Der Archiv-Export {Zeitpunkt(z.ArchivAm, z.ArchivVon)} ist veraltet (wieder geöffnet " +
                          $"{Zeitpunkt(z.GeoeffnetAm, z.GeoeffnetVon)}) – bitte neu erstellen.");
        if (!ArchivBestaetigt(z)) return (Gelb, $"{ArchivBeschreibung(z)}. Noch nicht bestätigt, dass er geprüft und aufbewahrt ist.");
        return (Gruen, $"Geprüft und aufbewahrt (bestätigt {Zeitpunkt(z.AufbewahrtAm, z.AufbewahrtVon)})" +
                       (z.AufbewahrenBis is DateOnly bis ? $", aufzubewahren bis {D(bis)}" : "") + ".");
    }

    /// <summary>Text zum Auftrag im Hintergrund, wenn er zu diesem Schuljahr gehört.</summary>
    private static string AuftragText(JaArchivAuftragDto a)
    {
        string start = DateTime.SpecifyKind(a.Start, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        return a.Status switch
        {
            ArchivAuftraege.Laeuft => $" Gerade: Der Archiv-Export wird erstellt ({a.Schritt}; gestartet {start} Uhr von {a.Akteur}).",
            ArchivAuftraege.Fertig => a.Eigener
                ? $" Der neue Archiv-Export ist fertig und liegt bis {Uhr(a.GueltigBis)} Uhr zum Herunterladen bereit (nur einmal)."
                : $" Ein neuer Archiv-Export von {a.Akteur} wartet auf den Download.",
            ArchivAuftraege.Fehlgeschlagen => $" Der letzte Versuch ({start} Uhr) ist fehlgeschlagen: {a.Fehler}",
            _ => "",
        };
    }

    private static string Uhr(DateTime? utc)
        => utc is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "";

    /// <summary>Punkt eines Schuljahres in der Karte „Archiv und Fristen“ (ohne Datenbank, für den Nachbau prüfbar).</summary>
    public static List<JaPunktDto> ArchivPunkte(SchuljahrDb z, DateOnly heute, JaArchivAuftragDto? auftrag)
    {
        string jahr = z.Schuljahr;
        string titel = $"Archiv-Export {jahr}";
        var erstellen = new JaAktionDto("archivExport", $"Archiv-Export {jahr} erstellen…", jahr);
        var erneut = new JaAktionDto("archivExport", $"Archiv-Export {jahr} erneut erstellen…", jahr);
        var bestaetigen = new JaAktionDto("archivBestaetigen", "Aufbewahrung bestätigen…", jahr);
        var aktionen = new List<JaAktionDto>();
        string text;
        string ampel;
        var l = new List<JaPunktDto>();

        if (z.GeloeschtAm != null)
        {
            ampel = Grau;
            text = $"Die Daten von {jahr} wurden {Zeitpunkt(z.GeloeschtAm, z.GeloeschtVon)} aus der Datenbank gelöscht" +
                   (string.IsNullOrWhiteSpace(z.GeloeschtText) ? "." : $": {z.GeloeschtText}.") +
                   (z.ArchivAm != null ? $" Sie stehen nur noch im Archiv-Export ({ArchivBeschreibung(z)}" +
                                         (string.IsNullOrWhiteSpace(z.AufbewahrtOrt) ? ")." : $"; Ort: {z.AufbewahrtOrt}).") : "");
        }
        else if (z.Status != SchuljahrStatus.Abgeschlossen)
        {
            ampel = Grau;
            text = $"{jahr} ist wieder geöffnet {Zeitpunkt(z.GeoeffnetAm, z.GeoeffnetVon)}. Der Archiv-Export " +
                   $"{Zeitpunkt(z.ArchivAm, z.ArchivVon)} gilt danach als veraltet – nach dem erneuten Abschluss bitte neu " +
                   "erstellen und die Aufbewahrung bestätigen.";
        }
        else if (z.ArchivAm == null)
        {
            ampel = Gelb;
            text = $"Noch kein Archiv-Export. Er enthält die Klassenbücher, Kurshefte und die Fehlzeiten je Halbjahr von {jahr} " +
                   "in einer mit Kennwort verschlüsselten Zip-Datei, die die Schule selbst aufbewahrt.";
            aktionen.Add(erstellen);
        }
        else if (ArchivVeraltet(z))
        {
            ampel = Gelb;
            text = $"Der Archiv-Export {Zeitpunkt(z.ArchivAm, z.ArchivVon)} ist veraltet: {jahr} wurde danach wieder geöffnet " +
                   $"{Zeitpunkt(z.GeoeffnetAm, z.GeoeffnetVon)} und abgeschlossen {Zeitpunkt(z.AbgeschlossenAm, z.AbgeschlossenVon)}. " +
                   "Bitte neu erstellen und die Aufbewahrung bestätigen.";
            aktionen.Add(erstellen);
        }
        else if (!ArchivBestaetigt(z))
        {
            ampel = Gelb;
            text = $"{ArchivBeschreibung(z)}. Noch nicht bestätigt, dass die Datei geprüft (mit 7-Zip und dem Kennwort geöffnet) " +
                   "und sicher aufbewahrt ist.";
            aktionen.Add(bestaetigen);
            aktionen.Add(erneut);
        }
        else
        {
            ampel = Gruen;
            text = $"{ArchivBeschreibung(z)}. Geprüft und aufbewahrt (bestätigt {Zeitpunkt(z.AufbewahrtAm, z.AufbewahrtVon)}): " +
                   $"Ort „{z.AufbewahrtOrt}“" + (z.AufbewahrenBis is DateOnly bis ? $", aufzubewahren bis {D(bis)}." : ".") +
                   " Gelöscht wird nichts von selbst.";
            aktionen.Add(new JaAktionDto("archivBestaetigen", "Bestätigung ändern…", jahr));
            aktionen.Add(erneut);
        }

        if (auftrag != null && auftrag.Art == ArchivArt.Export && auftrag.Schuljahr == jahr)
        {
            text += AuftragText(auftrag);
            if (auftrag.Eigener && auftrag.Status is ArchivAuftraege.Laeuft or ArchivAuftraege.Fertig)
            {
                aktionen.RemoveAll(a => a.Id == "archivExport");
                aktionen.Insert(0, new JaAktionDto("archivAuftrag",
                    auftrag.Status == ArchivAuftraege.Fertig ? "Herunterladen…" : "Fortschritt ansehen…", jahr));
            }
        }
        l.Add(new JaPunktDto("archivJahr", titel, ampel, text, Aktionen: aktionen.Count > 0 ? aktionen : null));

        if (ampel == Gruen && z.AufbewahrenBis is DateOnly frist && frist <= heute)
            l.Add(new JaPunktDto("archivVernichten", $"Archiv-Export {jahr} vernichten", Grau,
                $"„Aufzubewahren bis“ ({D(frist)}) ist erreicht: Der Archiv-Export {jahr} darf jetzt vernichtet werden (Ort: " +
                $"{z.AufbewahrtOrt}) – das erledigt die Schule selbst; die App kann die Datei nicht löschen."));
        return l;
    }

    /// <summary>Löschfristen der Karte „Datenschutz“ mit „abgestimmt am / mit“ (nur Überblick).</summary>
    public static JaPunktDto FristenPunkt(IReadOnlyDictionary<string, Einstellung> zeilen)
    {
        var defs = K.Alle.Where(d => d.Gruppe == "datenschutz" && d.Loescht).ToList();
        var liste = new List<string>();
        int offen = 0;
        foreach (var d in defs)
        {
            zeilen.TryGetValue(d.Schluessel, out var z);
            string abgestimmt;
            if (z?.AbgestimmtAm is DateOnly am)
                abgestimmt = $"abgestimmt am {D(am)}" + (string.IsNullOrWhiteSpace(z.AbgestimmtMit) ? "" : $" mit {z.AbgestimmtMit}");
            else
            {
                abgestimmt = "noch nicht mit dem DSB abgestimmt";
                offen++;
            }
            liste.Add($"{d.Titel}: {K.Text(d, z?.Wert)} – {abgestimmt}");
        }
        liste.Add("Klassenbuchdaten ganzer Schuljahre: bleiben in der Datenbank (das Löschen ganzer Schuljahre kommt später)");
        return new JaPunktDto("fristen", "Fristen", offen > 0 ? Gelb : Grau,
            offen > 0
                ? $"{Anzahl(offen, "Frist ist", "Fristen sind")} noch nicht mit der bzw. dem Datenschutzbeauftragten abgestimmt " +
                  "(Einstellungen → Datenschutz, „abgestimmt am / mit“). Nur ein Überblick – hier ändert sich nichts."
                : "Alle Löschfristen sind abgestimmt. Nur ein Überblick – ändern unter Einstellungen → Datenschutz.",
            liste, Ziele: new[] { new JaZielDto("einstellungen", "Einstellungen") });
    }

    private static async Task<(JaKarteDto Karte, List<JaArchivJahrDto> Jahre)> ArchivKarteAsync(AppDbContext db, DateOnly heute,
        JaArchivAuftragDto? auftrag)
    {
        const string titel = "Archiv und Fristen";
        var zeilen = (await db.Schuljahre.AsNoTracking().ToListAsync())
            .Where(z => z.Status == SchuljahrStatus.Abgeschlossen || z.ArchivAm != null || z.GeloeschtAm != null)
            .OrderByDescending(z => z.Schuljahr, StringComparer.Ordinal)
            .ToList();
        var kalender = (await db.Kalender.AsNoTracking().Select(k => new { k.Schuljahr, k.Start, k.Ende }).ToListAsync())
            .ToDictionary(k => k.Schuljahr, k => (k.Start, k.Ende), StringComparer.Ordinal);
        var einstellungen = await db.Einstellungen.AsNoTracking().ToDictionaryAsync(e => e.Schluessel, StringComparer.Ordinal);

        var punkte = new List<JaPunktDto>();
        foreach (var z in zeilen) punkte.AddRange(ArchivPunkte(z, heute, auftrag));
        punkte.Add(FristenPunkt(einstellungen));
        var jahre = zeilen.Select(z =>
        {
            (DateOnly Start, DateOnly Ende)? kal = kalender.TryGetValue(z.Schuljahr, out var k) ? k : null;
            return new JaArchivJahrDto(z.Schuljahr, z.Status, kal?.Start, kal?.Ende, z.AbgeschlossenAm, z.ArchivAm, z.ArchivVon,
                z.ArchivGroesse, z.ArchivPruefsumme, z.ArchivMitSchuelerEintraegen, ArchivVeraltet(z), ArchivBestaetigt(z),
                z.AufbewahrtAm, z.AufbewahrtVon, z.AufbewahrtOrt, z.AufbewahrenBis, z.GeloeschtAm != null);
        }).ToList();

        bool aktiv = zeilen.Any(z => z.Status == SchuljahrStatus.Abgeschlossen);
        string text = aktiv
            ? "Je abgeschlossenem Schuljahr ein Archiv-Export: Klassenbücher, Kurshefte und Fehlzeiten in einer mit Kennwort " +
              "(AES-256) verschlüsselten Zip-Datei, die die Schule selbst aufbewahrt – die App speichert sie nicht. Danach " +
              "bestätigen, dass sie geprüft und aufbewahrt ist. Gelöscht wird nichts von selbst."
            : "Erst nach einem Jahresabschluss: Dann gibt es hier je abgeschlossenem Schuljahr den Archiv-Export (verschlüsselte " +
              "Zip-Datei) und die Bestätigung, dass er geprüft und aufbewahrt ist.";
        var ampel = aktiv ? Schlechteste(punkte.Select(p => p.Ampel)) : Grau;
        return (new JaKarteDto("archiv", titel, ampel, text, null, null, aktiv, punkte), jahre);
    }

    /// <summary>Punkt in der Karte „Schuljahreswechsel“ nach dem Jahresabschluss – mit Sprung zur Karte „Archiv und Fristen“.</summary>
    public static JaPunktDto ArchivSprungPunkt(SchuljahrDb z, DateOnly heute)
    {
        var (ampel, text) = ArchivStand(z, heute);
        return new JaPunktDto("archivSprung", $"Archiv-Export {z.Schuljahr}", ampel, text,
            Ziele: new[] { new JaZielDto("archiv", "Archiv und Fristen") });
    }

    // -----------------------------------------------------------------
    //  Archiv-Export planen (260) – den Auftrag startet der Controller
    // -----------------------------------------------------------------

    /// <summary>Prüft den Archiv-Export eines Schuljahres und plant ihn (Inhalt, Kopf von Inhalt.txt, Dateiname).</summary>
    public static async Task<(AktionErgebnis? Fehler, ArchivPlan? Plan)> ArchivPlanAsync(AppDbContext db, Schuleinstellungen einst,
        string? eingabe, bool schuelerEintraege, string akteur)
    {
        var jahr = SchuljahrLesen(eingabe, out var fehler);
        if (fehler != null) return (new AktionErgebnis(400, Fehler: fehler), null);
        var zeile = await db.Schuljahre.AsNoTracking().FirstOrDefaultAsync(s => s.Schuljahr == jahr);
        if (zeile == null || zeile.Status != SchuljahrStatus.Abgeschlossen)
            return (new AktionErgebnis(409, Fehler: $"Den Archiv-Export gibt es nur für abgeschlossene Schuljahre – {jahr} ist " +
                                                    "nicht abgeschlossen."), null);
        if (zeile.GeloeschtAm != null)
            return (new AktionErgebnis(409, Fehler: $"Die Daten von {jahr} sind gelöscht – es gibt nichts mehr zu exportieren."), null);
        var kal = (await KalenderLadenAsync(db, jahr)).FirstOrDefault();
        if (kal == null)
            return (new AktionErgebnis(409, Fehler: $"Für {jahr} gibt es keinen Kalender – ohne ihn fehlt der Zeitraum."), null);
        if (ExportService.BisHoechstensHeute(kal.Ende) < kal.Start)
            return (new AktionErgebnis(409, Fehler: $"{jahr} hat noch nicht begonnen."), null);

        var w = await einst.LesenAsync(db);
        var (_, _, hjEinstellung) = DatenDesJahres(w, jahr!, kal, zeile);
        var h = FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, hjEinstellung);
        string sj = jahr!.Replace('/', '-');
        var csv = h is DateOnly hj
            ? new List<ArchivCsvTeil>
            {
                new($"Fehlzeiten/Fehlzeiten_{sj}_1-Halbjahr.csv", "1. Halbjahr", kal.Start, hj.AddDays(-1)),
                new($"Fehlzeiten/Fehlzeiten_{sj}_2-Halbjahr.csv", "2. Halbjahr", hj, kal.Ende),
            }
            : new List<ArchivCsvTeil> { new($"Fehlzeiten/Fehlzeiten_{sj}_Schuljahr.csv", "Schuljahr", kal.Start, kal.Ende) };

        string schule = string.IsNullOrWhiteSpace(w.SchuleName) ? "" : w.SchuleName.Trim();
        string kurz = string.IsNullOrWhiteSpace(w.SchuleKurzname) ? "" : w.SchuleKurzname.Trim();
        var kopf = new List<string>
        {
            $"Archiv-Export des Schuljahres {jahr}",
            "Schule: " + (schule.Length > 0 ? schule : "(kein Name eingetragen)") + (kurz.Length > 0 ? $" ({kurz})" : ""),
            $"Zeitraum: {D(kal.Start)} – {D(ExportService.BisHoechstensHeute(kal.Ende))} (laut Kalender)",
            $"Erstellt am {DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} von {akteur}",
            schuelerEintraege ? "Schülereinträge: aufgenommen" : "Schülereinträge: nicht aufgenommen",
            "",
            "Verschlüsselt mit AES-256 (WinZip-AES). Öffnen mit 7-Zip, WinZip oder WinRAR und dem Kennwort – der Windows-",
            "Explorer kann es nicht. Ohne Kennwort ist das Archiv wertlos: das Kennwort getrennt vom Datenträger aufbewahren.",
            "",
            "ACHTUNG: Enthält personenbezogene Daten von Schülerinnen und Schülern. Nur auf dienstlich freigegebenen Geräten",
            "bzw. Datenträgern aufbewahren, nicht weitergeben. Aufbewahrung nach den Vorgaben der Schule (Löschkonzept,",
            "VO-DV I); danach vernichtet die Schule die Datei selbst. Namen stehen nur in den PDF- und CSV-Dateien.",
        };
        string name = BerichtService.SichererName(
            $"Archiv_{kurz}_{sj}_{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", ".zip");
        return (null, new ArchivPlan(ArchivArt.Export, jahr, kal.Start, kal.Ende, schuelerEintraege, name,
            schule.Length > 0 ? schule : null, kopf, csv));
    }

    // -----------------------------------------------------------------
    //  Nach dem Download (263)
    // -----------------------------------------------------------------

    /// <summary>Merkt sich den abgeholten Archiv-Export (bzw. protokolliert das Jahresarchiv aus 4.3).</summary>
    public static async Task ArchivAbgeholtAsync(AppDbContext db, ArchivAuftragStand a, ILogger log)
    {
        var plan = a.Plan;
        var erg = a.Ergebnis;
        int klassen = erg?.Klassen ?? 0, kurse = erg?.Kurse ?? 0, csv = erg?.Csv ?? 0, nicht = erg?.Fehler.Count ?? 0;
        string zahlen = $"{Anzahl(klassen, "Klassenbuch", "Klassenbücher")}, {Anzahl(kurse, "Kursheft", "Kurshefte")}" +
                        (plan.Art == ArchivArt.Export ? $", {csv} CSV" : "") + $", {Mb(a.Groesse)}" +
                        (nicht > 0 ? $", {nicht} nicht erstellt" : "");
        var jetzt = DateTime.UtcNow;
        if (plan.Art == ArchivArt.Export && plan.Schuljahr != null)
        {
            var zeile = await db.Schuljahre.FirstOrDefaultAsync(s => s.Schuljahr == plan.Schuljahr);
            string? aufgehoben = null;
            if (zeile != null)
            {
                if (zeile.AufbewahrtAm != null)
                    aufgehoben = $"Die Bestätigung {Zeitpunkt(zeile.AufbewahrtAm, zeile.AufbewahrtVon)} gilt nicht mehr (sie galt dem " +
                                 "vorigen Export).";
                // Zeitpunkt = Beginn des Auftrags: Wer währenddessen wieder öffnet, macht diesen Export veraltet (265).
                zeile.ArchivAm = a.Start;
                zeile.ArchivVon = a.Akteur;
                zeile.ArchivGroesse = a.Groesse;
                zeile.ArchivPruefsumme = a.Pruefsumme;
                zeile.ArchivMitSchuelerEintraegen = plan.MitSchuelerEintraegen;
                zeile.AufbewahrtAm = null;
                zeile.AufbewahrtVon = null;
                zeile.AufbewahrtOrt = null;
                zeile.AufbewahrenBis = null;
            }
            db.DownloadProtokolle.Add(new DownloadProtokoll
            {
                Zeit = jetzt, Akteur = a.Akteur, Art = ArtArchivExport, Was = $"Archiv-Export {plan.Schuljahr} ({zahlen}, verschlüsselt)",
                Von = plan.Von, Bis = ExportService.BisHoechstensHeute(plan.Bis), MitSchuelerEintraegen = plan.MitSchuelerEintraegen
            });
            Protokoll(db, a.Akteur, $"Archiv-Export {plan.Schuljahr} erstellt",
                $"{zahlen}, {(plan.MitSchuelerEintraegen ? "mit" : "ohne")} Schülereinträge, Prüfsumme {a.Pruefsumme}" +
                (aufgehoben != null ? $". {aufgehoben}" : ""));
            await db.SaveChangesAsync();
            log.LogInformation("Jahresablauf: Archiv-Export {Schuljahr} heruntergeladen von {Akteur} ({Dateien} Dateien, {Groesse} " +
                               "Bytes).", plan.Schuljahr, a.Akteur, a.Dateien, a.Groesse);
            return;
        }
        db.DownloadProtokolle.Add(new DownloadProtokoll
        {
            Zeit = jetzt, Akteur = a.Akteur, Art = ArtJahresarchiv, Was = $"{zahlen} (verschlüsselt)",
            Von = plan.Von, Bis = erg?.Bis ?? plan.Bis, MitSchuelerEintraegen = plan.MitSchuelerEintraegen
        });
        await db.SaveChangesAsync();
        log.LogInformation("Jahresarchiv heruntergeladen von {Akteur} ({Dateien} Dateien, {Groesse} Bytes).", a.Akteur, a.Dateien,
            a.Groesse);
    }

    // -----------------------------------------------------------------
    //  Bestätigung „geprüft und aufbewahrt“ (264)
    // -----------------------------------------------------------------

    public static async Task<AktionErgebnis> ArchivBestaetigenAsync(AppDbContext db, JaArchivBestaetigenDto? dto, string akteur,
        ILogger log)
    {
        var jahr = SchuljahrLesen(dto?.Schuljahr, out var fehler);
        if (fehler != null) return new AktionErgebnis(400, Fehler: fehler);
        var zeile = await db.Schuljahre.FirstOrDefaultAsync(s => s.Schuljahr == jahr);
        if (zeile == null || zeile.Status != SchuljahrStatus.Abgeschlossen)
            return new AktionErgebnis(409, Fehler: $"{jahr} ist nicht abgeschlossen.");
        if (zeile.GeloeschtAm != null) return new AktionErgebnis(409, Fehler: $"Die Daten von {jahr} sind gelöscht.");
        if (zeile.ArchivAm == null || string.IsNullOrEmpty(zeile.ArchivPruefsumme))
            return new AktionErgebnis(409, Fehler: $"Für {jahr} gibt es noch keinen Archiv-Export.");
        if (ArchivVeraltet(zeile))
            return new AktionErgebnis(409, Fehler: $"Der Archiv-Export {Zeitpunkt(zeile.ArchivAm, zeile.ArchivVon)} ist veraltet " +
                                                   $"({jahr} wurde danach wieder geöffnet) – bitte neu erstellen.");

        var sha = PruefsummeLesen(dto!.Pruefsumme);
        if (sha == null)
            return new AktionErgebnis(400, Fehler: "Bitte die gespeicherte Datei wählen bzw. ihre Prüfsumme (SHA-256, 64 Zeichen 0–9 " +
                                                   "und A–F) eintragen.");
        if (sha != zeile.ArchivPruefsumme)
            return new AktionErgebnis(400, Fehler: "Die Prüfsumme passt nicht zum Archiv-Export " +
                                                   $"{Zeitpunkt(zeile.ArchivAm, zeile.ArchivVon)} (Prüfsumme " +
                                                   $"{PruefsummeKurz(zeile.ArchivPruefsumme)}) – ist es die richtige Datei, vollständig " +
                                                   "heruntergeladen?");
        if (!dto.Geoeffnet)
            return new AktionErgebnis(400, Fehler: "Bitte bestätigen, dass du die Datei mit 7-Zip und dem Kennwort geöffnet und " +
                                                   "Inhalt.txt gelesen hast.");
        var ort = LeerraumForm.Replace((dto.Ort ?? "").Trim(), " ");
        if (ort.Any(char.IsControl)) return new AktionErgebnis(400, Fehler: "Den Ort bitte ohne Steuerzeichen.");
        if (ort.Length < OrtMin)
            return new AktionErgebnis(400, Fehler: "Bitte eintragen, wo der Archiv-Export liegt (z. B. „USB-Stick im Tresor " +
                                                   "Sekretariat, Kennwort bei der Schulleitung“) – nie das Kennwort selbst.");
        if (ort.Length > OrtMax) return new AktionErgebnis(400, Fehler: $"Der Ort ist zu lang (höchstens {OrtMax} Zeichen).");
        var heute = KlassenbuchService.Heute();
        if (dto.AufbewahrenBis is not DateOnly bis)
            return new AktionErgebnis(400, Fehler: "Bitte „aufzubewahren bis“ eintragen – den Wert nennt das Löschkonzept bzw. die " +
                                                   "oder der Datenschutzbeauftragte.");
        if (bis <= heute) return new AktionErgebnis(400, Fehler: "„Aufzubewahren bis“ muss nach heute liegen.");
        if (bis > heute.AddYears(100)) return new AktionErgebnis(400, Fehler: "„Aufzubewahren bis“ liegt zu weit in der Zukunft.");

        bool erneut = ArchivBestaetigt(zeile);
        zeile.AufbewahrtAm = DateTime.UtcNow;
        zeile.AufbewahrtVon = akteur;
        zeile.AufbewahrtOrt = ort;
        zeile.AufbewahrenBis = bis;
        Protokoll(db, akteur, $"Archiv-Export {jahr} geprüft und aufbewahrt" + (erneut ? " (Bestätigung geändert)" : ""),
            $"Ort: {ort}; aufzubewahren bis {D(bis)}; Prüfsumme {sha}");
        await db.SaveChangesAsync();
        log.LogInformation("Jahresablauf: Aufbewahrung des Archiv-Exports {Schuljahr} bestätigt von {Akteur}.", jahr, akteur);
        return new AktionErgebnis(200, Meldung: $"Bestätigt: Der Archiv-Export {jahr} ist geprüft und aufbewahrt (aufzubewahren bis " +
                                                $"{D(bis)}).");
    }
}
