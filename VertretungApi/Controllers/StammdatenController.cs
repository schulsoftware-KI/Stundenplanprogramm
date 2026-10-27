using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Model;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

// =====================================================================
//  Stammdaten (Phase 1): Pseudodaten erzeugen + Lerngruppen/Schüler lesen.
//  Seed ist per X-Api-Key geschützt; die Lese-Endpunkte sind in Phase 1
//  noch offen (nur Pseudodaten!) und werden in Phase 1C (Auth) geschützt.
// =====================================================================

[ApiController]
[Route("api")]
public class StammdatenController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public StammdatenController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    /// <summary>Erzeugt (bzw. ersetzt) die Pseudo-Stammdaten. Schützt per X-Api-Key.</summary>
    [HttpPost("seed/pseudodaten")]
    public IActionResult Seed([FromQuery] string? schuljahr, [FromQuery] int? seed)
    {
        var erwartet = _cfg["Api:PushKey"];
        if (!string.IsNullOrEmpty(erwartet))
        {
            if (!Request.Headers.TryGetValue("X-Api-Key", out var key) || key != erwartet)
                return Unauthorized("Ungültiger oder fehlender X-Api-Key.");
        }

        string sj = string.IsNullOrWhiteSpace(schuljahr)
            ? (_cfg["DefaultSchuljahr"] ?? "2026/27")
            : schuljahr;

        var e = PseudoDatenGenerator.Generieren(_db, sj, seed ?? 12345);
        return Ok(new { schuljahr = sj, e.Schueler, e.Klassen, e.Kurse, e.Zuordnungen });
    }

    /// <summary>Lerngruppen (Klassen + Kurse), optional gefiltert. Nur Personal.</summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("lerngruppen")]
    public async Task<IActionResult> Lerngruppen([FromQuery] string? typ, [FromQuery] string? stufe)
    {
        var q = _db.Lerngruppen.AsQueryable();
        if (!string.IsNullOrWhiteSpace(typ) && Enum.TryParse<LerngruppeTyp>(typ, true, out var t))
            q = q.Where(l => l.Typ == t);
        if (!string.IsNullOrWhiteSpace(stufe))
            q = q.Where(l => l.Stufe == stufe);

        var liste = await q
            .OrderBy(l => l.Stufe).ThenBy(l => l.Bezeichnung)
            .Select(l => new
            {
                l.Id,
                Typ = l.Typ.ToString(),
                l.Bezeichnung,
                l.Stufe,
                l.Fach,
                Kursart = l.Kursart == Kursart.Keine ? null : l.Kursart.ToString(),
                l.LehrerKuerzel,
                Anzahl = l.Mitglieder.Count
            })
            .ToListAsync();
        return Ok(liste);
    }

    /// <summary>Schülerliste einer Lerngruppe. Nur Personal.</summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("lerngruppen/{id:int}/schueler")]
    public async Task<IActionResult> LerngruppenSchueler(int id)
    {
        var lg = await _db.Lerngruppen.FindAsync(id);
        if (lg == null) return NotFound();

        var liste = await _db.Zugehoerigkeiten
            .Where(z => z.LerngruppeId == id)
            .Select(z => z.Schueler!)
            .OrderBy(s => s.Nachname).ThenBy(s => s.Vorname)
            .Select(s => new { s.Id, s.Nachname, s.Vorname, s.Stufe })
            .ToListAsync();

        return Ok(new { lg.Id, lg.Bezeichnung, Typ = lg.Typ.ToString(), Anzahl = liste.Count, Schueler = liste });
    }

    /// <summary>Schülerliste, optional nach Stufe/Namenssuche; begrenzt. Nur Personal.</summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("schueler")]
    public async Task<IActionResult> SchuelerListe(
        [FromQuery] string? stufe, [FromQuery] string? q, [FromQuery] int limit = 100)
    {
        var query = _db.Schueler.AsQueryable();
        if (!string.IsNullOrWhiteSpace(stufe)) query = query.Where(s => s.Stufe == stufe);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(s => s.Nachname.Contains(q) || s.Vorname.Contains(q));

        var liste = await query
            .OrderBy(s => s.Nachname).ThenBy(s => s.Vorname)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(s => new { s.Id, s.Nachname, s.Vorname, s.Stufe })
            .ToListAsync();
        return Ok(liste);
    }

    /// <summary>Ein Schüler mit seinen Lerngruppen (Klasse + Kurse). Nur Personal.</summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("schueler/{id:int}")]
    public async Task<IActionResult> SchuelerEinzeln(int id)
    {
        var s = await _db.Schueler.FindAsync(id);
        if (s == null) return NotFound();

        var gruppen = await _db.Zugehoerigkeiten
            .Where(z => z.SchuelerId == id)
            .Select(z => z.Lerngruppe!)
            .OrderBy(l => l.Typ).ThenBy(l => l.Bezeichnung)
            .Select(l => new { l.Id, Typ = l.Typ.ToString(), l.Bezeichnung, l.Fach, l.LehrerKuerzel })
            .ToListAsync();

        return Ok(new { s.Id, s.Nachname, s.Vorname, s.Stufe, Lerngruppen = gruppen });
    }
}
