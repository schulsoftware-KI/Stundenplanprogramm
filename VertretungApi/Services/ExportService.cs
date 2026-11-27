using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Phase 4.3 – Export für die Verwaltung und Jahresarchiv
//  (Entscheidungen 48–50 und 60–65, Nutzer 04./05.11.2026)
//
//  CSV (Entscheidungen 48, 60–62):
//   - Summen je Schüler im Zeitraum; in der Oberstufe zusätzlich je Kurs eine
//     Zeile (Spalte „Kurs", leer = Gesamtzeile). Spalten: SchILD-Id, Name,
//     Vorname, Klasse, Kurs, Fehlstunden, entschuldigt, unentschuldigt, offen,
//     Verspätungen, Minuten. Zählung wie die Konferenzübersicht (beurlaubt =
//     entschuldigte Fehlstunde, schulische Veranstaltung zählt nicht).
//   - Format wie die Zugangszettel: Semikolon, UTF-8 mit BOM, Formelschutz.
//   - Die SchILD-Id bleibt leer: Die API kennt sie nicht (nur eine Prüfsumme, Entscheidungen
//     72/85); den Rückweg nach SchILD erzeugt der Desktop (5.3).
//   - Vorab: offene Fehlstunden/Verspätungen und nicht erfasste Stunden im
//     Zeitraum (Warnung, Export bleibt erlaubt).
//
//  Jahresarchiv (Entscheidungen 49, 63, 64; nur Admin):
//   - Sek I: je Klasse ein Klassenbuch; Oberstufe: je Kurs ein Kursheft
//     (UNr + Lehrkraft laut Plan). Welche Klassen/Kurse, ergibt sich aus dem
//     effektiven Plan des Zeitraums (auch Unterricht, der heute nicht mehr im
//     Grundplan steht).
//   - Zip mit „Klassen/…", „Kurse/<Stufe>/…" und „Inhalt.txt". Die Plantage
//     werden einmal berechnet und für alle Hefte verwendet.
//   - Ein Heft, das nicht erstellt werden kann, bricht das Archiv nicht ab: es
//     steht in Inhalt.txt unter „Nicht erstellt", Details im Log der API.
//
//  Rechte prüft der Aufrufer (BerichteController). Protokolliert wird dort.
//
//  P5c-1 (Entscheidungen 260–263, 273): Das Archiv ist jetzt immer mit WinZip-AES-256 verschlüsselt (AesZipSchreiber,
//  Kennwort des Admins) und entsteht im Hintergrund (ArchivAuftraege) – für das Jahresarchiv aus 4.3 wie für den
//  Archiv-Export eines abgeschlossenen Schuljahres. Der Archiv-Export bringt dazu die Fehlzeiten-CSV je Halbjahr mit
//  (alle Klassen und Stufen dieses Schuljahres) und einen eigenen Kopf in Inhalt.txt. Inhalt.txt nennt je Datei Größe und
//  SHA-256 – keine Namen von Schülern (die stehen nur in den PDFs und CSV).
// =====================================================================

/// <summary>Vorabprüfung zum CSV-Export (Entscheidung 62).</summary>
public sealed record CsvPruefungDto(
    int Gruppen,                // Klassen/Stufen im Export
    int Schueler,
    int OffeneFehlstunden,      // „fehlt" ohne Entscheidung
    int OffeneVerspaetungen,
    int NichtErfasst,           // Stunden ohne Anwesenheit (ab Klassenbuch:ErfassungAb, bis gestern)
    DateOnly Von,
    DateOnly Bis,               // endet spätestens heute
    DateOnly ErfassungAb);

/// <summary>Fertige CSV-Datei.</summary>
public sealed record CsvErgebnis(byte[] Daten, string Dateiname, int Schueler, int Zeilen);

/// <summary>Ergebnis des Jahresarchivs (P5c-1: dazu die Fehlzeiten-CSV und die Schüler darin).</summary>
public sealed record ArchivErgebnis(
    string Dateiname, DateOnly Von, DateOnly Bis, int Klassen, int Kurse, IReadOnlyList<string> Fehler,
    int Csv = 0, int CsvSchueler = 0);

/// <summary>P5c-1: eine Fehlzeiten-CSV im Archiv-Export (Name im Zip, Text für Inhalt.txt, Zeitraum).</summary>
public sealed record ArchivCsvTeil(string Name, string Text, DateOnly Von, DateOnly Bis);

/// <summary>P5c-1: Fortschritt beim Erstellen eines Archivs (z. B. „Klassenbuch 12 von 41“).</summary>
public sealed record ArchivFortschritt(string Schritt, int Erledigt, int Gesamt);

public static class ExportService
{
    /// <summary>PB-1a (299 e, F11): Vermerk in Klassenbuch-PDF, Jahresarchiv/Archiv-Export und CSV im Probebetrieb.</summary>
    public const string ProbebetriebVermerk = "PROBEBETRIEB – Testdaten (erfundene Daten, kein Schulbetrieb)";

    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] OberstufenStufen = { "EF", "Q1", "Q2" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static bool IstOberstufenStufe(string k) => OberstufenStufen.Contains(k.Trim(), Cmp);

    /// <summary>Exporte reichen höchstens bis heute (künftige Stunden haben noch kein Klassenbuch).</summary>
    public static DateOnly BisHoechstensHeute(DateOnly bis)
    {
        var heute = KlassenbuchService.Heute();
        return bis > heute ? heute : bis;
    }

    // =================================================================
    //  CSV: Vorabprüfung
    // =================================================================

    public static async Task<CsvPruefungDto> PruefenAsync(
        AppDbContext db, IReadOnlyList<string> gruppen, DateOnly von, DateOnly bis, int nichtErfasst, DateOnly erfassungAb)
    {
        var ids = new HashSet<int>();
        foreach (var g in gruppen)
            ids.UnionWith((await FehlzeitenService.KlassenSchuelerAsync(db, g, von, bis)).Ids);   // 5.2b-1: im Zeitraum
        var idListe = ids.ToList();

        int offenFehl = 0, offenVersp = 0;
        if (idListe.Count > 0)
        {
            var offen = db.Anwesenheiten.AsNoTracking()
                .Where(a => idListe.Contains(a.SchuelerId)
                            && a.Lektion!.Datum >= von && a.Lektion.Datum <= bis
                            && a.Bewertung == FehlBewertung.Offen);
            offenFehl = await offen.CountAsync(a => a.Status == AnwesenheitStatus.Fehlt);
            offenVersp = await offen.CountAsync(a => a.Status == AnwesenheitStatus.Verspaetet);
        }
        return new CsvPruefungDto(gruppen.Count, idListe.Count, offenFehl, offenVersp, nichtErfasst, von, bis, erfassungAb);
    }

    // =================================================================
    //  CSV: Datei
    // =================================================================

    /// <param name="dateiTeil">Teil des Dateinamens, z. B. „05A" bzw. „alle".</param>
    /// <param name="schuljahr">P5c-1: nur Klassen dieses Schuljahres und Stufen nach seinen Kursen (Archiv-Export).</param>
    public static async Task<CsvErgebnis> FehlzeitenCsvAsync(
        AppDbContext db, IReadOnlyList<string> gruppen, string dateiTeil, DateOnly von, DateOnly bis, string? schuljahr = null)
    {
        var sb = new StringBuilder();
        // PB-1a (299 e, F11): im Probebetrieb eine erste Zeile mit dem Vermerk (die Spaltenköpfe folgen darunter)
        if (Betrieb.Probebetrieb) CsvZeile(sb, ProbebetriebVermerk);
        CsvZeile(sb, "SchILD-Id", "Name", "Vorname", "Klasse", "Kurs",
            "Fehlstunden", "entschuldigt", "unentschuldigt", "offen", "Verspätungen", "Minuten");

        var gesehen = new HashSet<int>();   // „alle Klassen": jede Person nur einmal
        int zeilen = 0;
        foreach (var g in gruppen)
        {
            var (_, istStufe) = await FehlzeitenService.KlassenSchuelerAsync(db, g, schuljahr: schuljahr);
            var liste = await FehlzeitenService.KlasseAsync(db, g, von, bis, schuljahr);
            var neu = liste.Schueler.Where(z => gesehen.Add(z.Id)).ToList();
            if (neu.Count == 0) continue;

            var ids = neu.Select(z => z.Id).ToList();
            // Kurszeilen nur für die Oberstufe (eine Stufe ohne Klasse dieses Namens).
            var kurse = istStufe && IstOberstufenStufe(g) ? await KurszeilenAsync(db, ids, von, bis) : null;

            foreach (var z in neu)
            {
                // Phase 5.2b-1: Schueler.ExtId ist eine Prüfsumme (HMAC, Entscheidung 72), keine SchILD-Id –
                // die Spalte bleibt leer. Den Rückweg nach SchILD erzeugt der Desktop (5.3, Entscheidung 73).
                string ext = "";
                string klasse = z.Klasse ?? "";
                SummenZeile(sb, ext, z.Nachname, z.Vorname, klasse, "", z.Summe);
                zeilen++;
                if (kurse != null && kurse.TryGetValue(z.Id, out var jeKurs))
                {
                    foreach (var (kurs, summe) in jeKurs)
                    {
                        SummenZeile(sb, ext, z.Nachname, z.Vorname, klasse, kurs, summe);
                        zeilen++;
                    }
                }
            }
        }

        var text = Encoding.UTF8.GetBytes(sb.ToString());
        var daten = new byte[3 + text.Length];
        daten[0] = 0xEF; daten[1] = 0xBB; daten[2] = 0xBF;   // BOM: Excel erkennt UTF-8
        Buffer.BlockCopy(text, 0, daten, 3, text.Length);
        string name = BerichtService.SichererName($"Fehlzeiten_{dateiTeil}_{von:yyyy-MM-dd}_{bis:yyyy-MM-dd}", ".csv");
        return new CsvErgebnis(daten, name, gesehen.Count, zeilen);
    }

    /// <summary>
    /// Summen je Schüler und Kurs (Oberstufe). Kurs = Lerngruppen-Schlüssel der Stunde, zu dem der
    /// Schüler gehört (sonst der erste der Stunde, ohne Schlüssel „Fach Lehrkraft"). Jeder Kurs, in dem
    /// der Schüler im Zeitraum eingetragen war (seit 5.2b-1; vorher: heute), bekommt eine Zeile – auch ohne Fehlzeiten.
    /// </summary>
    private static async Task<Dictionary<int, List<(string Kurs, FzSummeDto Summe)>>> KurszeilenAsync(
        AppDbContext db, List<int> ids, DateOnly von, DateOnly bis)
    {
        var mitglied = await db.Zugehoerigkeiten.AsNoTracking()
            .Where(z => ids.Contains(z.SchuelerId) && z.Lerngruppe!.Typ == LerngruppeTyp.Kurs)
            .GueltigIm(von, bis)
            .Select(z => new { z.SchuelerId, z.Lerngruppe!.Bezeichnung })
            .ToListAsync();
        var kurseJe = new Dictionary<int, HashSet<string>>();
        foreach (var m in mitglied)
        {
            string b = (m.Bezeichnung ?? "").Trim();
            if (b.Length == 0) continue;
            if (!kurseJe.TryGetValue(m.SchuelerId, out var set)) kurseJe[m.SchuelerId] = set = new HashSet<string>(Cmp);
            set.Add(b);
        }

        var werte = (await db.Anwesenheiten.AsNoTracking()
                .Where(a => ids.Contains(a.SchuelerId) && a.Lektion!.Datum >= von && a.Lektion.Datum <= bis)
                .Select(a => new
                {
                    a.SchuelerId, a.Status, a.Minuten, a.Bewertung,
                    a.Lektion!.Fach, a.Lektion.GrundLehrer, a.Lektion.Lerngruppen
                })
                .ToListAsync())
            .ToLookup(x => x.SchuelerId);

        var ergebnis = new Dictionary<int, List<(string Kurs, FzSummeDto Summe)>>();
        foreach (var id in ids)
        {
            var eigene = kurseJe.TryGetValue(id, out var k) ? k : new HashSet<string>(Cmp);
            var jeKurs = new Dictionary<string, List<(AnwesenheitStatus Status, int? Minuten, FehlBewertung Bewertung)>>(Cmp);
            foreach (var kurs in eigene) jeKurs[kurs] = new();

            foreach (var w in werte[id])
            {
                var teile = w.Lerngruppen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string kurs = teile.FirstOrDefault(t => eigene.Contains(t))
                              ?? teile.FirstOrDefault()
                              ?? $"{w.Fach} {w.GrundLehrer}".Trim();
                if (!jeKurs.TryGetValue(kurs, out var liste)) jeKurs[kurs] = liste = new();
                liste.Add((w.Status, w.Minuten, w.Bewertung));
            }

            if (jeKurs.Count == 0) continue;
            ergebnis[id] = jeKurs
                .OrderBy(x => x.Key, Cmp)
                .Select(x => (x.Key, FehlzeitenService.Summe(x.Value)))
                .ToList();
        }
        return ergebnis;
    }

    private static void SummenZeile(StringBuilder sb, string ext, string name, string vorname, string klasse, string kurs, FzSummeDto s)
        => CsvZeile(sb, ext, name, vorname, klasse, kurs,
            s.Fehlstunden.ToString(Inv), s.Entschuldigt.ToString(Inv), s.Unentschuldigt.ToString(Inv),
            s.Offen.ToString(Inv), s.Verspaetungen.ToString(Inv), s.VerspaetungMinuten.ToString(Inv));

    private static void CsvZeile(StringBuilder sb, params string[] werte)
        => sb.Append(string.Join(";", werte.Select(CsvZelle))).Append("\r\n");

    /// <summary>Formelschutz (kein „=", „+", „-", „@" am Anfang) und Anführungszeichen wie bei den Zugangszetteln.</summary>
    internal static string CsvZelle(string? wert)
    {
        string s = wert ?? "";
        if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
        return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // =================================================================
    //  Jahresarchiv
    // =================================================================

    private sealed class KursInfo
    {
        public readonly Dictionary<string, int> Faecher = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Klassen = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Schreibt Klassenbücher, Kurshefte, ggf. Fehlzeiten-CSV (<paramref name="csv"/>) und Inhalt.txt in das verschlüsselte
    /// Archiv <paramref name="zip"/> – abschließen (Zentralverzeichnis) muss der Aufrufer. Abbruch über <paramref name="ct"/>
    /// zwischen zwei Dateien. P5c-1: <paramref name="kopf"/> = eigene Kopfzeilen für Inhalt.txt (Archiv-Export),
    /// <paramref name="schuljahr"/> = nur Klassen dieses Schuljahres, <paramref name="fortschritt"/> = Meldung je Datei.
    /// </summary>
    public static async Task<ArchivErgebnis> ArchivSchreibenAsync(
        AppDbContext db, KbNutzer u, DateOnly von, DateOnly bis, bool schuelerEintraege,
        AesZipSchreiber zip, ILogger log, CancellationToken ct, string? schule = null,
        IReadOnlyList<string>? kopf = null, IReadOnlyList<ArchivCsvTeil>? csv = null, string? schuljahr = null,
        Action<ArchivFortschritt>? fortschritt = null)
    {
        var bisEff = BisHoechstensHeute(bis);
        fortschritt?.Invoke(new ArchivFortschritt("Plan des Zeitraums wird berechnet", 0, 0));
        var tage = await WochenplanService.TageAsync(db, von, bisEff);
        ct.ThrowIfCancellationRequested();

        // Klassen (Sek I) und Kurse (Oberstufe) aus dem effektiven Plan des Zeitraums.
        var klassen = new HashSet<string>(Cmp);
        var kurse = new Dictionary<(int UNr, string Lehrer), KursInfo>();
        foreach (var tag in tage)
        {
            foreach (var st in tag.Stunden)
            {
                if (!string.Equals(st.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)) continue;
                var ks = st.Klassen.Select(k => k.Trim().ToUpperInvariant()).Where(k => k.Length > 0).ToList();
                if (ks.Count == 0) continue;
                if (ks.Any(IstOberstufenStufe))
                {
                    string lehrer = st.GrundLehrer.Trim().ToUpperInvariant();
                    if (lehrer.Length == 0) continue;
                    var key = (st.GrundUNr, lehrer);
                    if (!kurse.TryGetValue(key, out var info)) kurse[key] = info = new KursInfo();
                    string fach = st.Fach.Trim();
                    info.Faecher[fach] = (info.Faecher.TryGetValue(fach, out var n) ? n : 0) + 1;
                    info.Klassen.UnionWith(ks);
                }
                else
                {
                    klassen.UnionWith(ks);
                }
            }
        }

        var klassenListe = klassen
            .OrderBy(k => char.IsDigit(k[0]) ? 0 : 1)
            .ThenBy(k => k, Cmp)
            .ToList();
        var kursListe = kurse
            .Select(x =>
            {
                var kl = x.Value.Klassen.OrderBy(k => k, Cmp).ToList();
                string fach = x.Value.Faecher.OrderByDescending(f => f.Value).ThenBy(f => f.Key, Cmp).First().Key;
                string stufe = kl.FirstOrDefault(IstOberstufenStufe) ?? "Oberstufe";
                return (x.Key.UNr, x.Key.Lehrer, Fach: fach, Klassen: kl, Stufe: stufe);
            })
            .OrderBy(x => Array.FindIndex(OberstufenStufen, s => Cmp.Equals(s, x.Stufe)))
            .ThenBy(x => x.Fach, Cmp)
            .ThenBy(x => x.Lehrer, Cmp)
            .ThenBy(x => x.UNr)
            .ToList();

        var csvTeile = csv ?? Array.Empty<ArchivCsvTeil>();
        int gesamt = klassenListe.Count + kursListe.Count + csvTeile.Count;
        int erledigt = 0;
        var fehler = new List<string>();
        var dateienKlassen = new List<string>();
        var dateienKurse = new List<string>();
        var csvZeilen = new List<string>();
        int csvSchueler = 0, csvAnzahl = 0;
        var namen = new HashSet<string>(Cmp);

        string Eindeutig(string pfad)
        {
            if (namen.Add(pfad)) return pfad;
            int punkt = pfad.LastIndexOf('.');
            string basis = punkt > 0 ? pfad[..punkt] : pfad, endung = punkt > 0 ? pfad[punkt..] : "";
            for (int i = 2; ; i++)
            {
                string neu = $"{basis}_{i}{endung}";
                if (namen.Add(neu)) return neu;
            }
        }

        foreach (var k in klassenListe)
        {
            ct.ThrowIfCancellationRequested();
            fortschritt?.Invoke(new ArchivFortschritt($"Klassenbuch {dateienKlassen.Count + 1} von {klassenListe.Count}",
                erledigt, gesamt));
            try
            {
                var bericht = await BerichtService.KlassenbuchAsync(db, u, k, von, bisEff, schuelerEintraege, tage, schuljahr);
                var pdf = KlassenbuchPdf.Erzeuge(bericht, schule);
                string name = Eindeutig("Klassen/" + BerichtService.Dateiname(bericht.Kopf));
                zip.Hinzufuegen(name, pdf);
                dateienKlassen.Add(name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ArchivZuGrossException) { throw; }
            catch (Exception ex)
            {
                log.LogError(ex, "Jahresarchiv: Klassenbuch {Klasse} konnte nicht erstellt werden.", k);
                fehler.Add($"Klassenbuch {k}");
            }
            erledigt++;
        }

        int kursNr = 0;
        foreach (var kurs in kursListe)
        {
            ct.ThrowIfCancellationRequested();
            fortschritt?.Invoke(new ArchivFortschritt($"Kursheft {++kursNr} von {kursListe.Count}", erledigt, gesamt));
            string bezeichnung = $"{kurs.Fach} · {string.Join(", ", kurs.Klassen)} · {kurs.Lehrer} (UNr {kurs.UNr})";
            try
            {
                var bericht = await BerichtService.KursheftAsync(db, u, kurs.UNr, kurs.Lehrer, kurs.Fach, kurs.Klassen,
                    von, bisEff, schuelerEintraege, tage);
                var pdf = KlassenbuchPdf.Erzeuge(bericht, schule);
                string datei = BerichtService.SichererName(
                    $"Kursheft_{kurs.Fach}_{string.Join("_", kurs.Klassen)}_{kurs.Lehrer}_UNr{kurs.UNr}_{von:yyyy-MM-dd}_{bisEff:yyyy-MM-dd}",
                    ".pdf");
                string name = Eindeutig($"Kurse/{BerichtService.SichererName(kurs.Stufe, "")}/{datei}");
                zip.Hinzufuegen(name, pdf);
                dateienKurse.Add(name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ArchivZuGrossException) { throw; }
            catch (Exception ex)
            {
                log.LogError(ex, "Jahresarchiv: Kursheft {Kurs} konnte nicht erstellt werden.", bezeichnung);
                fehler.Add($"Kursheft {bezeichnung}");
            }
            erledigt++;
        }

        // P5c-1 (260): Fehlzeiten je Halbjahr – alle Klassen und Stufen des Schuljahres (Admin-Sicht wie „alle“ in 4.3)
        if (csvTeile.Count > 0)
        {
            var gruppen = schuljahr != null
                ? await FehlzeitenService.KlassenUndStufenDesJahresAsync(db, schuljahr)
                : await FehlzeitenService.AlleKlassenUndStufenAsync(db);
            int csvNr = 0;
            foreach (var teil in csvTeile)
            {
                ct.ThrowIfCancellationRequested();
                fortschritt?.Invoke(new ArchivFortschritt($"Fehlzeiten-CSV {++csvNr} von {csvTeile.Count}", erledigt, gesamt));
                var bisTeil = BisHoechstensHeute(teil.Bis);
                try
                {
                    if (bisTeil < teil.Von) throw new InvalidOperationException("Zeitraum liegt in der Zukunft.");
                    var erg = await FehlzeitenCsvAsync(db, gruppen, "alle", teil.Von, bisTeil, schuljahr);
                    string name = Eindeutig(teil.Name);
                    zip.Hinzufuegen(name, erg.Daten);
                    csvZeilen.Add($"  {name} – {teil.Text} {teil.Von:dd.MM.yyyy} – {bisTeil:dd.MM.yyyy}: " +
                                  $"{erg.Schueler} Schüler, {erg.Zeilen} Zeilen");
                    csvSchueler = Math.Max(csvSchueler, erg.Schueler);
                    csvAnzahl++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (ArchivZuGrossException) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "Archiv-Export: Fehlzeiten-CSV {Teil} konnte nicht erstellt werden.", teil.Text);
                    fehler.Add($"Fehlzeiten-CSV {teil.Text}");
                }
                erledigt++;
            }
        }

        fortschritt?.Invoke(new ArchivFortschritt("Inhaltsverzeichnis", erledigt, gesamt));
        var inhalt = new StringBuilder();
        // PB-1a (299 e, F11): im Probebetrieb ganz oben – keine Verwechslung mit einem echten Archiv
        if (Betrieb.Probebetrieb) inhalt.Append(ProbebetriebVermerk).Append("\r\n\r\n");
        if (kopf != null)
        {
            foreach (var z in kopf) inhalt.Append(z).Append("\r\n");
        }
        else
        {
            inhalt.Append("Jahresarchiv – Klassenbücher und Kurshefte\r\n");
            inhalt.Append($"Zeitraum: {von:dd.MM.yyyy} – {bisEff:dd.MM.yyyy}\r\n");
            inhalt.Append($"Erstellt am {Ortszeit.Jetzt:dd.MM.yyyy HH:mm} von {u.Name}\r\n");
            inhalt.Append(schuelerEintraege ? "Schülereinträge: aufgenommen\r\n" : "Schülereinträge: nicht aufgenommen\r\n");
            inhalt.Append("Verschlüsselt mit AES-256 (WinZip-AES) – öffnen mit 7-Zip, WinZip oder WinRAR und dem Kennwort\r\n");
            inhalt.Append("(der Windows-Explorer kann es nicht).\r\n");
            inhalt.Append("ACHTUNG: Enthält personenbezogene Daten von Schülerinnen und Schülern. Nur auf dienstlich\r\n");
            inhalt.Append("freigegebenen Geräten speichern, nicht weitergeben. Aufbewahrung nach den Vorgaben der Schule.\r\n");
        }
        inhalt.Append($"\r\nKlassenbücher (Sek I): {dateienKlassen.Count}\r\n");
        inhalt.Append($"Kurshefte (Oberstufe): {dateienKurse.Count}\r\n");
        if (csvTeile.Count > 0)
        {
            inhalt.Append($"Fehlzeiten (CSV, alle Klassen und Stufen): {csvAnzahl}\r\n");
            foreach (var z in csvZeilen) inhalt.Append(z).Append("\r\n");
        }
        if (fehler.Count > 0)
        {
            inhalt.Append($"\r\nNicht erstellt ({fehler.Count}) – Details im Protokoll der API:\r\n");
            foreach (var f in fehler) inhalt.Append("  ").Append(f).Append("\r\n");
        }
        inhalt.Append("\r\nDateien – SHA-256 (wie PowerShell „Get-FileHash“), Größe in Bytes, Name:\r\n");
        foreach (var d in zip.Dateien)
            inhalt.Append("  ").Append(d.Sha256).Append("  ").Append(d.Groesse.ToString(Inv).PadLeft(10)).Append("  ")
                .Append(d.Name).Append("\r\n");
        var text = Encoding.UTF8.GetBytes(inhalt.ToString());
        var mitBom = new byte[3 + text.Length];
        mitBom[0] = 0xEF; mitBom[1] = 0xBB; mitBom[2] = 0xBF;
        Buffer.BlockCopy(text, 0, mitBom, 3, text.Length);
        zip.Hinzufuegen("Inhalt.txt", mitBom);

        string dateiname = BerichtService.SichererName($"Klassenbuch-Archiv_{von:yyyy-MM-dd}_{bisEff:yyyy-MM-dd}", ".zip");
        return new ArchivErgebnis(dateiname, von, bisEff, dateienKlassen.Count, dateienKurse.Count, fehler, csvAnzahl,
            csvSchueler);
    }
}
