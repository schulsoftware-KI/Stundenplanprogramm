using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

/// <summary>
/// Hausaufgaben für Schüler (Phase 3.3, „Was ist auf?"):
///   GET api/hausaufgaben/ich     eigene Hausaufgaben nach Fälligkeit (Schülerkonto)
/// Die Zuordnung kommt aus dem Konto, nie aus der Adresse. Ausgeblendete Unterrichte fehlen.
/// Abhaken („erledigt") speichert nur die App auf dem eigenen Gerät (Entscheidung 26).
/// </summary>
[ApiController]
[Route("api/hausaufgaben")]
[Authorize(Roles = Rollen.Schueler)]
public class HausaufgabenController : ControllerBase
{
    private readonly AppDbContext _db;

    public HausaufgabenController(AppDbContext db) => _db = db;

    [HttpGet("ich")]
    public async Task<IActionResult> Ich()
    {
        var id = Rollen.BenutzerId(User);
        var konto = id == null ? null : await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value);
        if (konto == null) return Unauthorized();
        var s = konto.SchuelerId is int sid
            ? await _db.Schueler.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid)
            : null;
        if (s == null) return NotFound(new { fehler = "Diesem Konto ist kein Schüler zugeordnet." });
        return Ok(await UnterrichtInhaltService.HausaufgabenAsync(_db, s));
    }
}
