using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

/// <summary>
/// Wochenpläne (Phase 2, Schritt 2) – Grundplan × Kalender × Vertretungen:
///   GET api/woche/lehrer/{kuerzel}?datum=YYYY-MM-DD&amp;wochen=1
///   GET api/woche/klasse/{klasse}?datum=…&amp;wochen=…   (z. B. 05A, auch EF/Q1/Q2 = ganze Stufe)
///   GET api/woche/gruppe/{schluessel}?datum=…&amp;wochen=… (Lerngruppe laut Grundplan, z. B. BIG1_EF_L68)
///   GET api/woche/schueler/{id}?datum=…&amp;wochen=…      (individueller Plan; nur Personal, JWT)
///
/// datum fehlt = heute; es wird immer ab dem Montag dieser Woche geliefert. wochen: 1–6.
/// Lehrer/Klasse/Gruppe sind wie die bisherigen Plan-Abfragen vorerst offen (nur Plan-,
/// keine Schülerdaten); der Login für die PWA folgt in Phase 2, Schritt 4.
/// </summary>
[ApiController]
[Route("api/woche")]
public class WocheController : ControllerBase
{
    private readonly AppDbContext _db;

    public WocheController(AppDbContext db) => _db = db;

    private static DateOnly Heute() => DateOnly.FromDateTime(DateTime.Now);

    [HttpGet("lehrer/{kuerzel}")]
    public async Task<IActionResult> Lehrer(string kuerzel, [FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
        => Ok(await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Lehrer, kuerzel, new[] { kuerzel }, datum ?? Heute(), wochen));

    [HttpGet("klasse/{klasse}")]
    public async Task<IActionResult> Klasse(string klasse, [FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
        => Ok(await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Klasse, klasse, new[] { klasse }, datum ?? Heute(), wochen));

    [HttpGet("gruppe/{schluessel}")]
    public async Task<IActionResult> Gruppe(string schluessel, [FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
        => Ok(await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Gruppe, schluessel, new[] { schluessel }, datum ?? Heute(), wochen));

    /// <summary>Individueller Plan eines Schülers aus seinen Lerngruppen (Klasse bzw. Kurse).</summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("schueler/{id:int}")]
    public async Task<IActionResult> Schueler(int id, [FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
    {
        var s = await _db.Schueler.FindAsync(id);
        if (s == null) return NotFound();

        var gruppen = await _db.Zugehoerigkeiten
            .Where(z => z.SchuelerId == id)
            .Select(z => z.Lerngruppe!.Bezeichnung)
            .ToListAsync();

        var plan = await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Schueler, $"{s.Vorname} {s.Nachname}", gruppen, datum ?? Heute(), wochen);

        return Ok(new
        {
            Schueler = new { s.Id, s.Vorname, s.Nachname, s.Stufe, Lerngruppen = gruppen.OrderBy(g => g).ToList() },
            Plan = plan
        });
    }
}
