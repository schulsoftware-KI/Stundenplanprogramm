using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VertretungApi.Data;
using VertretungApi.Dtos;
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
//   - Die SchILD-Id ist bis Phase 5 (echte Daten) leer.
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

/// <summary>Ergebnis des Jahresarchivs.</summary>
public sealed record ArchivErgebnis(
    string Dateiname, DateOnly Von, DateOnly Bis, int Klassen, int Kurse, IReadOnlyList<string> Fehler);

public static class ExportService
{
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
            ids.UnionWith((await FehlzeitenService.KlassenSchuelerAsync(db, g)).Ids);
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
    public static async Task<CsvErgebnis> FehlzeitenCsvAsync(
        AppDbContext db, IReadOnlyList<string> gruppen, string dateiTeil, DateOnly von, DateOnly bis)
    {
        var sb = new StringBuilder();
        CsvZeile(sb, "SchILD-Id", "Name", "Vorname", "Klasse", "Kurs",
            "Fehlstunden", "entschuldigt", "unentschuldigt", "offen", "Verspätungen", "Minuten");

        var gesehen = new HashSet<int>();   // „alle Klassen": jede Person nur einmal
        int zeilen = 0;
        foreach (var g in gruppen)
        {
            var (_, istStufe) = await FehlzeitenService.KlassenSchuelerAsync(db, g);
            var liste = await FehlzeitenService.KlasseAsync(db, g, von, bis);
            var neu = liste.Schueler.Where(z => gesehen.Add(z.Id)).ToList();
            if (neu.Count == 0) continue;

            var ids = neu.Select(z => z.Id).ToList();
            var extIds = await db.Schueler.AsNoTracking()
                .Where(s => ids.Contains(s.Id))
                .Select(s => new { s.Id, s.ExtId })
                .ToDictionaryAsync(s => s.Id, s => s.ExtId);
            // Kurszeilen nur für die Oberstufe (eine Stufe ohne Klasse dieses Namens).
            var kurse = istStufe && IstOberstufenStufe(g) ? await KurszeilenAsync(db, ids, von, bis) : null;

            foreach (var z in neu)
            {
                string ext = extIds.TryGetValue(z.Id, out var e) ? e ?? "" : "";
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
    /// der Schüler heute eingetragen ist, bekommt eine Zeile – auch ohne Fehlzeiten.
    /// </summary>
    private static async Task<Dictionary<int, List<(string Kurs, FzSummeDto Summe)>>> KurszeilenAsync(
        AppDbContext db, List<int> ids, DateOnly von, DateOnly bis)
    {
        var mitglied = await db.Zugehoerigkeiten.AsNoTracking()
            .Where(z => ids.Contains(z.SchuelerId) && z.Lerngruppe!.Typ == LerngruppeTyp.Kurs)
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
    /// Schreibt das Archiv als Zip nach <paramref name="ziel"/> (der Stream bleibt offen).
    /// Abbruch über <paramref name="ct"/> (Verbindung beendet) zwischen zwei Heften.
    /// </summary>
    public static async Task<ArchivErgebnis> ArchivSchreibenAsync(
        AppDbContext db, KbNutzer u, DateOnly von, DateOnly bis, bool schuelerEintraege,
        Stream ziel, ILogger log, CancellationToken ct)
    {
        var bisEff = BisHoechstensHeute(bis);
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

        var fehler = new List<string>();
        var dateienKlassen = new List<string>();
        var dateienKurse = new List<string>();
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

        using (var zip = new ZipArchive(ziel, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var k in klassenListe)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bericht = await BerichtService.KlassenbuchAsync(db, u, k, von, bisEff, schuelerEintraege, tage);
                    var pdf = KlassenbuchPdf.Erzeuge(bericht);
                    string name = Eindeutig("Klassen/" + BerichtService.Dateiname(bericht.Kopf));
                    Schreibe(zip, name, pdf);
                    dateienKlassen.Add(name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "Jahresarchiv: Klassenbuch {Klasse} konnte nicht erstellt werden.", k);
                    fehler.Add($"Klassenbuch {k}");
                }
            }

            foreach (var kurs in kursListe)
            {
                ct.ThrowIfCancellationRequested();
                string bezeichnung = $"{kurs.Fach} · {string.Join(", ", kurs.Klassen)} · {kurs.Lehrer} (UNr {kurs.UNr})";
                try
                {
                    var bericht = await BerichtService.KursheftAsync(db, u, kurs.UNr, kurs.Lehrer, kurs.Fach, kurs.Klassen,
                        von, bisEff, schuelerEintraege, tage);
                    var pdf = KlassenbuchPdf.Erzeuge(bericht);
                    string datei = BerichtService.SichererName(
                        $"Kursheft_{kurs.Fach}_{string.Join("_", kurs.Klassen)}_{kurs.Lehrer}_UNr{kurs.UNr}_{von:yyyy-MM-dd}_{bisEff:yyyy-MM-dd}",
                        ".pdf");
                    string name = Eindeutig($"Kurse/{BerichtService.SichererName(kurs.Stufe, "")}/{datei}");
                    Schreibe(zip, name, pdf);
                    dateienKurse.Add(name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "Jahresarchiv: Kursheft {Kurs} konnte nicht erstellt werden.", bezeichnung);
                    fehler.Add($"Kursheft {bezeichnung}");
                }
            }

            var inhalt = new StringBuilder();
            inhalt.Append("Jahresarchiv – Klassenbücher und Kurshefte\r\n");
            inhalt.Append($"Zeitraum: {von:dd.MM.yyyy} – {bisEff:dd.MM.yyyy}\r\n");
            inhalt.Append($"Erstellt am {DateTime.Now:dd.MM.yyyy HH:mm} von {u.Name}\r\n");
            inhalt.Append(schuelerEintraege ? "Schülereinträge: aufgenommen\r\n" : "Schülereinträge: nicht aufgenommen\r\n");
            inhalt.Append("ACHTUNG: Enthält personenbezogene Daten von Schülerinnen und Schülern. Nur auf dienstlich\r\n");
            inhalt.Append("freigegebenen Geräten speichern, nicht weitergeben. Aufbewahrung nach den Vorgaben der Schule.\r\n\r\n");
            inhalt.Append($"Klassenbücher (Sek I): {dateienKlassen.Count}\r\n");
            foreach (var d in dateienKlassen) inhalt.Append("  ").Append(d).Append("\r\n");
            inhalt.Append($"\r\nKurshefte (Oberstufe): {dateienKurse.Count}\r\n");
            foreach (var d in dateienKurse) inhalt.Append("  ").Append(d).Append("\r\n");
            if (fehler.Count > 0)
            {
                inhalt.Append($"\r\nNicht erstellt ({fehler.Count}) – Details im Protokoll der API:\r\n");
                foreach (var f in fehler) inhalt.Append("  ").Append(f).Append("\r\n");
            }
            var text = Encoding.UTF8.GetBytes(inhalt.ToString());
            var mitBom = new byte[3 + text.Length];
            mitBom[0] = 0xEF; mitBom[1] = 0xBB; mitBom[2] = 0xBF;
            Buffer.BlockCopy(text, 0, mitBom, 3, text.Length);
            Schreibe(zip, "Inhalt.txt", mitBom);
        }

        string dateiname = BerichtService.SichererName($"Klassenbuch-Archiv_{von:yyyy-MM-dd}_{bisEff:yyyy-MM-dd}", ".zip");
        return new ArchivErgebnis(dateiname, von, bisEff, dateienKlassen.Count, dateienKurse.Count, fehler);
    }

    private static void Schreibe(ZipArchive zip, string name, byte[] daten)
    {
        var eintrag = zip.CreateEntry(name, CompressionLevel.Fastest);
        eintrag.LastWriteTime = DateTimeOffset.Now;
        using var s = eintrag.Open();
        s.Write(daten, 0, daten.Length);
    }
}
