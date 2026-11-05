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
///   GET api/berichte/protokoll?limit=                                Download-Protokoll (Admin)
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

    /// <summary>Download-Protokoll (Entscheidung 50), neueste zuerst.</summary>
    [HttpGet("protokoll")]
    public async Task<IActionResult> Protokoll([FromQuery] int limit = 300)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin) return StatusCode(403, new { fehler = "Das Download-Protokoll sieht nur die Verwaltung." });
        limit = Math.Clamp(limit, 1, 2000);
        var liste = await _db.DownloadProtokolle.AsNoTracking()
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(limit)
            .Select(p => new { p.Zeit, p.Akteur, p.Art, p.Was, p.Von, p.Bis, p.MitSchuelerEintraegen })
            .ToListAsync();
        return Ok(liste);
    }

    // -----------------------------------------------------------------

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

        _db.DownloadProtokolle.Add(new DownloadProtokoll
        {
            Zeit = DateTime.UtcNow,
            Akteur = u.Name,
            Art = art,
            Was = was,
            Von = bericht.Kopf.Von,
            Bis = bericht.Kopf.Bis,
            MitSchuelerEintraegen = bericht.Kopf.MitSchuelerEintraegen,
        });
        await _db.SaveChangesAsync();

        return File(pdf, "application/pdf", BerichtService.Dateiname(bericht.Kopf));
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
