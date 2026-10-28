using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Controllers;

/// <summary>
/// Vertretungsplan-Endpunkte:
///   POST api/plan/push            – Desktop lädt einen Tag hoch (X-Api-Key).
///   GET  api/plan/tag             – voller Tag (Admin/Aushang).
///   GET  api/plan/lehrer/{kuerzel}– Stunden einer Lehrkraft (voll).
///   GET  api/plan/klasse/{klasse} – Stunden einer Klasse (Schülersicht, ohne Grund).
///
/// Auth ist bewusst minimal (nur Push per API-Key). Rollen-Login (JWT) folgt
/// in einem späteren M4-Schritt; die GETs sind vorerst offen.
/// </summary>
[ApiController]
[Route("api/plan")]
public class PlanController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public PlanController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    [HttpPost("push")]
    public async Task<IActionResult> Push([FromBody] PlanPushDto dto)
    {
        // API-Key prüfen, wenn in der Konfiguration gesetzt.
        var erwartet = _cfg["Api:PushKey"];
        if (!string.IsNullOrEmpty(erwartet))
        {
            if (!Request.Headers.TryGetValue("X-Api-Key", out var key) || key != erwartet)
                return Unauthorized("Ungültiger oder fehlender X-Api-Key.");
        }

        if (string.IsNullOrWhiteSpace(dto.Schuljahr))
            return BadRequest("Schuljahr fehlt.");

        var tag = await _db.Tage.Include(t => t.Eintraege)
            .FirstOrDefaultAsync(t => t.Schuljahr == dto.Schuljahr && t.Datum == dto.Datum);

        if (tag == null)
        {
            tag = new VertretungsTag { Schuljahr = dto.Schuljahr, Datum = dto.Datum };
            _db.Tage.Add(tag);
        }
        else
        {
            // Tag komplett ersetzen – der Desktop ist die Wahrheit.
            _db.Eintraege.RemoveRange(tag.Eintraege);
            tag.Eintraege.Clear();
        }

        tag.Aktualisiert = DateTimeOffset.UtcNow;
        foreach (var e in dto.Eintraege ?? new())
        {
            tag.Eintraege.Add(new VertretungsEintragDb
            {
                Stunde = e.Stunde,
                Klassen = e.Klassen ?? "",
                Fach = e.Fach ?? "",
                Lehrer = e.Lehrer,
                OriginalLehrer = e.OriginalLehrer,
                Raum = e.Raum,
                Art = Enum.TryParse<PlanArt>(e.Art, true, out var a) ? a : PlanArt.Sonstiges,
                Status = e.Status ?? "",
                Grund = e.Grund,
                Bemerkung = e.Bemerkung,
                Fachgerecht = e.Fachgerecht,
                IstAenderung = e.IstAenderung,
                UNr = e.UNr,
                GrundUNr = e.GrundUNr
            });
        }

        await _db.SaveChangesAsync();
        return Ok(new { tag.Id, dto.Datum, Anzahl = tag.Eintraege.Count, tag.Aktualisiert });
    }

    /// <summary>Alle gespeicherten Tage (zum Nachschauen, was veröffentlicht wurde).</summary>
    [HttpGet("tage")]
    public async Task<IActionResult> Tage()
    {
        var liste = await _db.Tage
            .OrderByDescending(t => t.Datum)
            .Select(t => new { t.Schuljahr, t.Datum, Anzahl = t.Eintraege.Count, t.Aktualisiert })
            .ToListAsync();
        return Ok(liste);
    }

    [HttpGet("tag")]
    public async Task<IActionResult> Tag([FromQuery] string? schuljahr, [FromQuery] DateOnly datum)
    {
        var tag = await LadeTag(schuljahr, datum);
        if (tag == null) return NotFound();
        return Ok(tag.Eintraege.OrderBy(e => e.Stunde).ThenBy(e => e.Klassen).Select(ToVoll));
    }

    [HttpGet("lehrer/{kuerzel}")]
    public async Task<IActionResult> Lehrer(string kuerzel,
        [FromQuery] string? schuljahr, [FromQuery] DateOnly datum)
    {
        var tag = await LadeTag(schuljahr, datum);
        if (tag == null) return Ok(Array.Empty<PlanEintragDto>());

        var rel = tag.Eintraege.Where(e =>
            string.Equals(e.Lehrer, kuerzel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.OriginalLehrer, kuerzel, StringComparison.OrdinalIgnoreCase));
        return Ok(rel.OrderBy(e => e.Stunde).Select(ToVoll));
    }

    [HttpGet("klasse/{klasse}")]
    public async Task<IActionResult> Klasse(string klasse,
        [FromQuery] string? schuljahr, [FromQuery] DateOnly datum)
    {
        var tag = await LadeTag(schuljahr, datum);
        if (tag == null) return Ok(Array.Empty<PlanEintragOeffentlichDto>());

        var rel = tag.Eintraege.Where(e => e.Klassen
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(k => string.Equals(k, klasse, StringComparison.OrdinalIgnoreCase)));
        // Schülersicht: ohne Grund/Bemerkung/Fachgerecht.
        return Ok(rel.OrderBy(e => e.Stunde).Select(ToOeffentlich));
    }

    // schuljahr optional: fehlt es, wird nur nach Datum gesucht (zuletzt angelegter
    // Tag). Sortierung über Id statt Aktualisiert – SQLite kann nicht nach
    // DateTimeOffset sortieren.
    private Task<VertretungsTag?> LadeTag(string? schuljahr, DateOnly datum)
        => string.IsNullOrWhiteSpace(schuljahr)
            ? _db.Tage.Include(t => t.Eintraege)
                  .Where(t => t.Datum == datum)
                  .OrderByDescending(t => t.Id)
                  .FirstOrDefaultAsync()
            : _db.Tage.Include(t => t.Eintraege)
                  .FirstOrDefaultAsync(t => t.Schuljahr == schuljahr && t.Datum == datum);

    private static PlanEintragDto ToVoll(VertretungsEintragDb e) => new(
        e.Stunde, e.Klassen, e.Fach, e.Lehrer, e.OriginalLehrer, e.Raum,
        e.Art.ToString(), e.Status, e.Grund, e.Bemerkung, e.Fachgerecht, e.IstAenderung,
        e.UNr, e.GrundUNr);

    private static PlanEintragOeffentlichDto ToOeffentlich(VertretungsEintragDb e) => new(
        e.Stunde, e.Klassen, e.Fach, e.Lehrer, e.OriginalLehrer, e.Raum,
        e.Art.ToString(), e.Status, e.IstAenderung);
}
