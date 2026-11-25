using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
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
///   POST api/berichte/archiv { von, bis, schuelerEintraege, kennwort, kennwortWiederholung }
///        Jahresarchiv als verschlüsselte Zip (nur Admin; P5c-1, 273: WinZip-AES-256, im Hintergrund – Stand und Datei wie
///        beim Archiv-Export über api/jahresablauf/archiv/auftrag bzw. …/datei; protokolliert wird beim Download)
/// Zeitraum ohne von/bis: das laufende Schuljahr; er endet spätestens heute.
/// „Downloads nur für die Verwaltung“ (Einstellungen → Datenschutz, seit P3; vorher „Berichte:NurAdmin“): nur Admin
/// (Entscheidung 50).
/// Jeder Download wird protokolliert (wer, wann, was, Zeitraum – ohne Inhalt).
/// P5a (216, 219): Zeiträume eines abgeschlossenen Schuljahres lädt nur der Admin (PDF, CSV, Archiv); eine Zuständigkeit gilt
/// nur für Zeiträume ihres Schuljahres.
/// </summary>
[ApiController]
[Route("api/berichte")]
[Authorize(Roles = Rollen.Personal)]
public class BerichteController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<BerichteController> _log;
    private readonly Schuleinstellungen _einst;
    private readonly ArchivAuftraege _auftraege;

    public BerichteController(AppDbContext db, ILogger<BerichteController> log, Schuleinstellungen einst,
        ArchivAuftraege auftraege)
    {
        _db = db;
        _log = log;
        _einst = einst;
        _auftraege = auftraege;
    }

    /// <summary>P3 (E-1): „Downloads nur für die Verwaltung“ aus den Schuleinstellungen (wirkt sofort).</summary>
    private async Task<bool> NurAdminAsync() => (await _einst.LesenAsync(_db)).BerichteNurAdmin;

    private IActionResult Fehler(KbErgebnis e) => StatusCode(e.Status, new { fehler = e.Fehler });

    [HttpGet("unterrichte")]
    public async Task<IActionResult> Unterrichte([FromQuery] string? klasse)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(klasse)) return BadRequest(new { fehler = "Die Klasse fehlt." });
        // P5a (216, 219): Unterrichte laut heutigem Grundplan – Schuljahr von heute
        var (uj, sperre) = await SchuljahrRechte.LesenAsync(_db, u, await SchuljahrRechte.JahrAmAsync(_db, KlassenbuchService.Heute()));
        if (sperre != null) return Fehler(sperre);
        u = uj;
        if (!BerichtService.DarfKlassenbuch(u, klasse))
            return StatusCode(403, new { fehler = "Das sehen nur die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });
        return Ok(await BerichtService.UnterrichteAsync(_db, klasse));
    }

    [TeurerAbruf]            // P6a (230)
    [HttpGet("klassenbuch")]
    public async Task<IActionResult> Klassenbuch(
        [FromQuery] string? klasse, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] bool schuelerEintraege = false)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin && await NurAdminAsync()) return Gesperrt();
        if (string.IsNullOrWhiteSpace(klasse)) return BadRequest(new { fehler = "Die Klasse fehlt." });
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var (uj, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, v, b);   // P5a (216, 219)
        if (sperre != null) return Fehler(sperre);
        var vorher = u;
        u = uj;
        if (!BerichtService.DarfKlassenbuch(u, klasse))
            return StatusCode(403, new { fehler = SchuljahrRechte.AnderesJahrText(vorher, new[] { klasse })
                ?? "Das Klassenbuch drucken die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });

        var bericht = await BerichtService.KlassenbuchAsync(_db, u, klasse, v, b, schuelerEintraege);
        return await PdfAntwortAsync(u, bericht, "KlassenbuchPdf", bericht.Kopf.Titel);
    }

    [TeurerAbruf]            // P6a (230)
    [HttpGet("kursheft")]
    public async Task<IActionResult> Kursheft(
        [FromQuery] int unr, [FromQuery] string? lehrer, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis,
        [FromQuery] bool schuelerEintraege = false)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin && await NurAdminAsync()) return Gesperrt();
        if (string.IsNullOrWhiteSpace(lehrer)) return BadRequest(new { fehler = "Die Lehrkraft fehlt." });

        var info = await BerichtService.UnterrichtInfoAsync(_db, unr, lehrer);
        if (info == null) return NotFound(new { fehler = "Diesen Unterricht gibt es nicht." });
        var (fach, klassen) = info.Value;
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var (uj, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, v, b);   // P5a (216, 219)
        if (sperre != null) return Fehler(sperre);
        u = uj;
        if (!BerichtService.DarfKursheft(u, lehrer, klassen))
            return StatusCode(403, new { fehler = "Das Kursheft drucken die Lehrkraft des Unterrichts, die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });

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
        if (!u.IstAdmin && await NurAdminAsync()) return (null, Gesperrt());
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
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var (uj, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, v, b);   // P5a (216, 219)
        if (sperre != null) return Fehler(sperre);
        u = uj;
        var (gruppen, fehler) = await CsvGruppenAsync(u, klasse, alle);
        if (fehler != null) return fehler;

        var bisEff = ExportService.BisHoechstensHeute(b);
        if (bisEff < v) return BadRequest(new { fehler = "Der Zeitraum liegt noch in der Zukunft." });

        // Nur zum Zählen: die Admin-Sicht von „nicht erfasst", gefiltert auf die oben geprüfte Klasse/Stufe
        // (bzw. ungefiltert bei „alle", das nur der Admin darf). Zurück geht allein die Anzahl.
        var zaehler = new KbNutzer(u.Name, BenutzerRolle.Admin, null, Array.Empty<string>());
        // P5a: ein früheres Schuljahr mit seinem „Anwesenheit erfasst ab“
        var (erfassungAb, _) = await SchuljahrRechte.ErfassungAbAsync(_db, await _einst.LesenAsync(_db), v);
        var ne = await FehlzeitenService.NichtErfasstAsync(_db, zaehler, "alle", null, alle ? null : gruppen![0],
            erfassungAb, erfassungAb, "anwesenheit", v, bisEff);
        return Ok(await ExportService.PruefenAsync(_db, gruppen!, v, bisEff, ne.Anzahl, erfassungAb));
    }

    [TeurerAbruf]            // P6a (230)
    [HttpGet("fehlzeiten-csv")]
    public async Task<IActionResult> FehlzeitenCsv(
        [FromQuery] string? klasse, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] bool alle = false)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var (uj, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, v, b);   // P5a (216, 219)
        if (sperre != null) return Fehler(sperre);
        u = uj;
        var (gruppen, fehler) = await CsvGruppenAsync(u, klasse, alle);
        if (fehler != null) return fehler;

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
    /// Alle Klassenbücher (Sek I) und Kurshefte (Oberstufe) des Zeitraums als Zip – nur Admin. P5c-1 (273): verschlüsselt mit
    /// dem Kennwort des Admins (WinZip-AES-256) und im Hintergrund erstellt (ArchivAuftraege) – die Antwort ist der Stand des
    /// Auftrags; die Datei holt die App einmal über api/jahresablauf/archiv/datei (dort wird der Download protokolliert).
    /// </summary>
    [HttpPost("archiv")]
    public async Task<IActionResult> Archiv([FromBody] JaJahresarchivStartDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin) return StatusCode(403, new { fehler = "Das Jahresarchiv erstellt nur die Verwaltung." });
        if (Kennwort.Pruefen(dto?.Kennwort) is string kwFehler) return BadRequest(new { fehler = kwFehler, feld = "kennwort" });
        if (!string.Equals(dto!.Kennwort, dto.KennwortWiederholung, StringComparison.Ordinal))
            return BadRequest(new { fehler = "Die beiden Kennwörter stimmen nicht überein.", feld = "kennwort2" });

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, dto.Von, dto.Bis);
        var bisEff = ExportService.BisHoechstensHeute(b);
        if (bisEff < v) return BadRequest(new { fehler = "Der Zeitraum liegt noch in der Zukunft." });

        string name = BerichtService.SichererName($"Klassenbuch-Archiv_{v:yyyy-MM-dd}_{bisEff:yyyy-MM-dd}", ".zip");
        var plan = new ArchivPlan(ArchivArt.Jahresarchiv, null, v, b, dto.SchuelerEintraege, name,
            (await _einst.LesenAsync(_db)).SchuleName, null, null);
        int kontoId = Rollen.BenutzerId(User) ?? 0;
        if (_auftraege.Starten(plan, u.Name, kontoId, dto.Kennwort!) is string belegt) return Conflict(new { fehler = belegt });
        _log.LogInformation("Jahresarchiv gestartet von {Akteur} ({Von} – {Bis}).", u.Name, v, bisEff);
        return Ok(new JaArchivAuftragAntwortDto(_auftraege.Stand(kontoId)));
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
            pdf = KlassenbuchPdf.Erzeuge(bericht, (await _einst.LesenAsync(_db)).SchuleName);
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

    /// <summary>Angemeldete Person samt Kürzel und Zuständigkeit (mit Schuljahr, P5a) – aus der Datenbank.</summary>
    private async Task<KbNutzer?> NutzerAsync()
    {
        var id = Rollen.BenutzerId(User);
        if (id == null) return null;
        var b = await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value);
        if (b == null) return null;
        return KlassenbuchService.NutzerAus(b, await _einst.AktuellesSchuljahrAsync(_db));
    }
}
