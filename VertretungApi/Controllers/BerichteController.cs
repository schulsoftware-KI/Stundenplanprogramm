using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

/// <summary>
/// Klassenbuch und Kursheft als PDF (Phase 4.2, Entscheidungen 44–47, 50):
///   GET api/berichte/unterrichte?klasse=05A                         Unterrichte einer Klasse/Stufe (Auswahl „Kursheft")
///   GET api/berichte/klassenbuch?klasse=&amp;von=&amp;bis=&amp;schuelerEintraege=   PDF (KL/SK zuständig, Admin)
///   GET api/berichte/kursheft?unr=&amp;lehrer=&amp;von=&amp;bis=&amp;schuelerEintraege=  PDF (Lehrkraft laut Plan, KL/SK, Admin)
///   GET api/berichte/protokoll?limit=&amp;art=&amp;akteur=                   Download-Protokoll (Admin; Filter seit 4.3)
/// Phase 4.3 (Entscheidungen 48–50, 60–65):
///   GET api/berichte/fehlzeiten-csv/pruefen?klasse=|alle=true&amp;von=&amp;bis=  Vorabprüfung (offene/nicht erfasste Stunden)
///   GET api/berichte/fehlzeiten-csv?klasse=|alle=true&amp;von=&amp;bis=          CSV (KL/SK zuständig, Admin; „alle" nur Admin)
///   GET api/berichte/archiv?von=&amp;bis=&amp;schuelerEintraege=               Jahresarchiv als Zip (nur Admin)
/// Zeitraum ohne von/bis: das laufende Schuljahr; er endet spätestens heute.
/// „Berichte:NurAdmin" = true: Downloads nur für Admin (Entscheidung 50, vorbereitet).
/// Jeder Download wird protokolliert (wer, wann, was, Zeitraum – ohne Inhalt).
/// </summary>
[ApiController]
[Route("api/berichte")]
[Authorize(Roles = Rollen.Personal)]
public class BerichteController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<BerichteController> _log;

    public BerichteController(AppDbContext db, IConfiguration cfg, ILogger<BerichteController> log)
    {
        _db = db;
        _cfg = cfg;
        _log = log;
    }

    private bool NurAdmin => _cfg.GetValue("Berichte:NurAdmin", false);

    /// <summary>Ab diesem Tag gelten Stunden ohne Anwesenheit als „nicht erfasst" (wie in den Fehlzeiten).</summary>
    private DateOnly ErfassungAb()
        => DateOnly.TryParseExact(_cfg["Klassenbuch:ErfassungAb"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var d)
            ? d
            : new DateOnly(2026, 11, 2);

    [HttpGet("unterrichte")]
    public async Task<IActionResult> Unterrichte([FromQuery] string? klasse)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(klasse)) return BadRequest(new { fehler = "Die Klasse fehlt." });
        if (!BerichtService.DarfKlassenbuch(u, klasse))
            return StatusCode(403, new { fehler = "Das sehen nur die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });
        return Ok(await BerichtService.UnterrichteAsync(_db, klasse));
    }

    [HttpGet("klassenbuch")]
    public async Task<IActionResult> Klassenbuch(
        [FromQuery] string? klasse, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] bool schuelerEintraege = false)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (NurAdmin && !u.IstAdmin) return Gesperrt();
        if (string.IsNullOrWhiteSpace(klasse)) return BadRequest(new { fehler = "Die Klasse fehlt." });
        if (!BerichtService.DarfKlassenbuch(u, klasse))
            return StatusCode(403, new { fehler = "Das Klassenbuch drucken die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var bericht = await BerichtService.KlassenbuchAsync(_db, u, klasse, v, b, schuelerEintraege);
        return await PdfAntwortAsync(u, bericht, "KlassenbuchPdf", bericht.Kopf.Titel);
    }

    [HttpGet("kursheft")]
    public async Task<IActionResult> Kursheft(
        [FromQuery] int unr, [FromQuery] string? lehrer, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis,
        [FromQuery] bool schuelerEintraege = false)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (NurAdmin && !u.IstAdmin) return Gesperrt();
        if (string.IsNullOrWhiteSpace(lehrer)) return BadRequest(new { fehler = "Die Lehrkraft fehlt." });

        var info = await BerichtService.UnterrichtInfoAsync(_db, unr, lehrer);
        if (info == null) return NotFound(new { fehler = "Diesen Unterricht gibt es nicht." });
        var (fach, klassen) = info.Value;
        if (!BerichtService.DarfKursheft(u, lehrer, klassen))
            return StatusCode(403, new { fehler = "Das Kursheft drucken die Lehrkraft des Unterrichts, die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var bericht = await BerichtService.KursheftAsync(_db, u, unr, lehrer, fach, klassen, v, b, schuelerEintraege);
        return await PdfAntwortAsync(u, bericht, "KursheftPdf",
            $"{bericht.Kopf.Titel} (UNr {unr}, {lehrer.Trim().ToUpperInvariant()})");
    }

    /// <summary>
    /// Download-Protokoll (Entscheidung 50), neueste zuerst. Phase 4.3 (Entscheidung 65): Filter nach
    /// Art (KlassenbuchPdf | KursheftPdf | FehlzeitenCsv | Archiv) und Person (Teil des Benutzernamens).
    /// </summary>
    [HttpGet("protokoll")]
    public async Task<IActionResult> Protokoll([FromQuery] int limit = 300, [FromQuery] string? art = null,
        [FromQuery] string? akteur = null)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin) return StatusCode(403, new { fehler = "Das Download-Protokoll sieht nur die Verwaltung." });
        limit = Math.Clamp(limit, 1, 2000);
        var q = _db.DownloadProtokolle.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(art))
        {
            string a = art.Trim();
            q = q.Where(p => p.Art == a);
        }
        if (!string.IsNullOrWhiteSpace(akteur))
        {
            string w = akteur.Trim().ToLower();
            q = q.Where(p => p.Akteur.ToLower().Contains(w));
        }
        var liste = await q
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(limit)
            .Select(p => new { p.Zeit, p.Akteur, p.Art, p.Was, p.Von, p.Bis, p.MitSchuelerEintraegen })
            .ToListAsync();
        return Ok(liste);
    }

    // =================================================================
    //  Phase 4.3: Fehlzeiten als CSV (Entscheidungen 48, 60–62)
    // =================================================================

    /// <summary>
    /// Welche Klassen/Stufen in den Export dürfen: eine zuständige Klasse/Stufe (KL/SK, Admin) oder –
    /// nur Admin – alle Klassen und Stufen. Niemand exportiert mehr, als er am Bildschirm sieht.
    /// </summary>
    private async Task<(List<string>? Gruppen, IActionResult? Fehler)> CsvGruppenAsync(KbNutzer u, string? klasse, bool alle)
    {
        if (NurAdmin && !u.IstAdmin) return (null, Gesperrt());
        if (alle)
        {
            if (!u.IstAdmin)
                return (null, StatusCode(403, new { fehler = "Alle Klassen auf einmal exportiert nur die Verwaltung." }));
            return (await FehlzeitenService.AlleKlassenUndStufenAsync(_db), null);
        }
        if (string.IsNullOrWhiteSpace(klasse)) return (null, BadRequest(new { fehler = "Die Klasse fehlt." }));
        if (!BerichtService.DarfKlassenbuch(u, klasse))
            return (null, StatusCode(403, new { fehler = "Die Fehlzeiten einer Klasse bzw. Stufe exportieren die Klassenleitung bzw. Stufenkoordination und die Verwaltung." }));
        return (new List<string> { klasse.Trim().ToUpperInvariant() }, null);
    }

    /// <summary>Vorab: offene Fehlstunden/Verspätungen und nicht erfasste Stunden im Zeitraum (Entscheidung 62).</summary>
    [HttpGet("fehlzeiten-csv/pruefen")]
    public async Task<IActionResult> FehlzeitenCsvPruefen(
        [FromQuery] string? klasse, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] bool alle = false)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var (gruppen, fehler) = await CsvGruppenAsync(u, klasse, alle);
        if (fehler != null) return fehler;

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var bisEff = ExportService.BisHoechstensHeute(b);
        if (bisEff < v) return BadRequest(new { fehler = "Der Zeitraum liegt noch in der Zukunft." });

        // Nur zum Zählen: die Admin-Sicht von „nicht erfasst", gefiltert auf die oben geprüfte Klasse/Stufe
        // (bzw. ungefiltert bei „alle", das nur der Admin darf). Zurück geht allein die Anzahl.
        var zaehler = new KbNutzer(u.Name, BenutzerRolle.Admin, null, Array.Empty<string>());
        var erfassungAb = ErfassungAb();
        var ne = await FehlzeitenService.NichtErfasstAsync(_db, zaehler, "alle", null, alle ? null : gruppen![0],
            erfassungAb, erfassungAb, "anwesenheit", v, bisEff);
        return Ok(await ExportService.PruefenAsync(_db, gruppen!, v, bisEff, ne.Anzahl, erfassungAb));
    }

    [HttpGet("fehlzeiten-csv")]
    public async Task<IActionResult> FehlzeitenCsv(
        [FromQuery] string? klasse, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] bool alle = false)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var (gruppen, fehler) = await CsvGruppenAsync(u, klasse, alle);
        if (fehler != null) return fehler;

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var bisEff = ExportService.BisHoechstensHeute(b);
        if (bisEff < v) return BadRequest(new { fehler = "Der Zeitraum liegt noch in der Zukunft." });

        CsvErgebnis erg;
        try
        {
            erg = await ExportService.FehlzeitenCsvAsync(_db, gruppen!, alle ? "alle" : gruppen![0], v, bisEff);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CSV konnte nicht erstellt werden ({Was}).", alle ? "alle" : gruppen![0]);
            return StatusCode(500, new { fehler = "Die CSV-Datei konnte nicht erstellt werden. Details stehen im Protokoll der API." });
        }

        await ProtokollierenAsync(u, "FehlzeitenCsv",
            (alle ? "Alle Klassen und Stufen" : GruppenText(gruppen![0])) + $" ({Anzahl(erg.Schueler, "Schüler", "Schüler")})",
            v, bisEff, false);
        return File(erg.Daten, "text/csv; charset=utf-8", erg.Dateiname);
    }

    // =================================================================
    //  Phase 4.3: Jahresarchiv (Entscheidungen 49, 63, 64)
    // =================================================================

    /// <summary>
    /// Alle Klassenbücher (Sek I) und Kurshefte (Oberstufe) des Zeitraums als Zip – nur Admin. Wird in
    /// einem Durchgang erzeugt (Zwischendatei im Temp-Ordner, nach dem Senden gelöscht); bricht die
    /// Verbindung ab, wird die Erzeugung zwischen zwei Heften beendet.
    /// </summary>
    [HttpGet("archiv")]
    public async Task<IActionResult> Archiv(
        [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] bool schuelerEintraege = false)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin) return StatusCode(403, new { fehler = "Das Jahresarchiv erstellt nur die Verwaltung." });

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        if (ExportService.BisHoechstensHeute(b) < v) return BadRequest(new { fehler = "Der Zeitraum liegt noch in der Zukunft." });

        var ct = HttpContext.RequestAborted;
        FileStream? datei = null;
        try
        {
            datei = new FileStream(Path.Combine(Path.GetTempPath(), $"klassenbuch-archiv-{Guid.NewGuid():N}.zip"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            var erg = await ExportService.ArchivSchreibenAsync(_db, u, v, b, schuelerEintraege, datei, _log, ct);

            string was = $"{Anzahl(erg.Klassen, "Klassenbuch", "Klassenbücher")}, {Anzahl(erg.Kurse, "Kursheft", "Kurshefte")}"
                         + (erg.Fehler.Count > 0 ? $", {erg.Fehler.Count} nicht erstellt" : "");
            await ProtokollierenAsync(u, "Archiv", was, erg.Von, erg.Bis, schuelerEintraege);

            datei.Position = 0;
            var antwort = File(datei, "application/zip", erg.Dateiname);
            datei = null;   // gehört jetzt der Antwort; wird nach dem Senden geschlossen und damit gelöscht
            return antwort;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("Jahresarchiv abgebrochen – die Verbindung wurde beendet.");
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jahresarchiv konnte nicht erstellt werden.");
            return StatusCode(500, new { fehler = "Das Archiv konnte nicht erstellt werden. Details stehen im Protokoll der API." });
        }
        finally
        {
            datei?.Dispose();
        }
    }

    // -----------------------------------------------------------------

    private static string Anzahl(int n, string eins, string viele) => $"{n} {(n == 1 ? eins : viele)}";

    /// <summary>„Klasse 05A", „Stufe Q1" bzw. „Jahrgang 05" – wie die Titel der Fehlzeitenlisten.</summary>
    private static string GruppenText(string k)
        => FehlzeitenService.IstOberstufe(k) ? $"Stufe {k}"
            : k.Length == 2 && char.IsDigit(k[0]) && char.IsDigit(k[1]) ? $"Jahrgang {k}"
            : $"Klasse {k}";

    private IActionResult Gesperrt()
        => StatusCode(403, new { fehler = "Downloads sind zurzeit nur für die Verwaltung freigegeben." });

    /// <summary>PDF erzeugen, Download protokollieren und als Datei zurückgeben.</summary>
    private async Task<IActionResult> PdfAntwortAsync(KbNutzer u, Bericht bericht, string art, string was)
    {
        byte[] pdf;
        try
        {
            pdf = KlassenbuchPdf.Erzeuge(bericht);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PDF konnte nicht erzeugt werden ({Art}, {Was}).", art, was);
            return StatusCode(500, new { fehler = "Das PDF konnte nicht erzeugt werden. Details stehen im Protokoll der API." });
        }

        await ProtokollierenAsync(u, art, was, bericht.Kopf.Von, bericht.Kopf.Bis, bericht.Kopf.MitSchuelerEintraegen);
        return File(pdf, "application/pdf", BerichtService.Dateiname(bericht.Kopf));
    }

    /// <summary>Download protokollieren (wer, wann, was, Zeitraum – ohne Inhalt; Entscheidung 50).</summary>
    private async Task ProtokollierenAsync(KbNutzer u, string art, string was, DateOnly von, DateOnly bis, bool mitSchuelerEintraegen)
    {
        _db.DownloadProtokolle.Add(new DownloadProtokoll
        {
            Zeit = DateTime.UtcNow,
            Akteur = u.Name,
            Art = art,
            Was = was,
            Von = von,
            Bis = bis,
            MitSchuelerEintraegen = mitSchuelerEintraegen,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>Angemeldete Person samt Kürzel und Zuständigkeit – aus der Datenbank.</summary>
    private async Task<KbNutzer?> NutzerAsync()
    {
        var id = Rollen.BenutzerId(User);
        if (id == null) return null;
        var b = await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value);
        if (b == null) return null;
        return new KbNutzer(
            b.Benutzername, b.Rolle,
            string.IsNullOrWhiteSpace(b.LehrerKuerzel) ? null : b.LehrerKuerzel.Trim(),
            KontenService.Zustaendigkeiten(b.Zustaendig));
    }
}
