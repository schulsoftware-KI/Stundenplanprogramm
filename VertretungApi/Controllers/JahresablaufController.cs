using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

// =====================================================================
//  P4a (5.3a, Entscheidung 205): Bereich „Jahresablauf“ – nur Admin (JWT).
//
//    GET api/jahresablauf   – Karten mit Ampel (P4a: „Halbjahreswechsel“), ohne Namen
//
//  Nur lesen: Jede spätere Aktion (Rückweg, Jahresabschluss … in P4b/P5) bekommt einen eigenen Knopf mit
//  Rückfrage und Protokoll. Logik und Texte: Services/Jahresablauf.cs.
// =====================================================================

[ApiController]
[Route("api/jahresablauf")]
[Authorize(Roles = Rollen.Admin)]
public class JahresablaufController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Schuleinstellungen _einst;
    private readonly IHostEnvironment _env;

    public JahresablaufController(AppDbContext db, Schuleinstellungen einst, IHostEnvironment env)
    {
        _db = db;
        _einst = einst;
        _env = env;
    }

    [HttpGet]
    public async Task<IActionResult> Uebersicht()
    {
        var werte = await _einst.LesenAsync(_db);
        return Ok(await Jahresablauf.UebersichtAsync(_db, werte, Betrieb.Produktiv(_env), User.Identity?.Name ?? ""));
    }
}
