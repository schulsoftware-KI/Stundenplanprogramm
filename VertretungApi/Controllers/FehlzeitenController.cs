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
/// Fehlzeiten und Entschuldigungen (Phase 3.2):
///   GET    api/fehlzeiten/rechte                              was darf ich sehen? (Personal)
///   GET    api/fehlzeiten/nicht-erfasst?ansicht=ich|zustaendig|alle&amp;lehrer=&amp;klasse=&amp;von=&amp;bis=
///                                                             &amp;art=alle|anwesenheit|thema (Phase 3.3)
///   GET    api/fehlzeiten/unterrichte                         „Meine Kurse" (Lehrkraft)
///   GET    api/fehlzeiten/unterricht?unr=&amp;lehrer=&amp;von=&amp;bis=   Liste eines Unterrichts (Lehrkraft selbst, Admin)
///   GET    api/fehlzeiten/klasse/{klasse}?von=&amp;bis=          Liste einer Klasse/Stufe (KL/SK zuständig, Admin)
///   GET    api/fehlzeiten/schueler/{id}?von=&amp;bis=&amp;unr=&amp;lehrer=  Fehlzeiten eines Schülers (Personal; Lehrkräfte nur eigene Stunden)
///   GET    api/fehlzeiten/ich?von=&amp;bis=                       eigene Fehlzeiten (Schülerkonto)
///   POST   api/fehlzeiten/schueler/{id}/entschuldigung        Zeitraum entschuldigen { von, bis, grund }
///   DELETE api/fehlzeiten/entschuldigung/{id}                  Entschuldigung löschen
///   PUT    api/fehlzeiten/eintrag/{id}                        Fehlstunde bewerten { bewertung, grund? }
/// Phase 4.1:
///   GET    api/fehlzeiten/zeitraeume                          1./2. Halbjahr, Schuljahr (alle Angemeldeten)
///   GET    api/fehlzeiten/offen/{klasse}                      offene Fälle im Schuljahr (KL/SK zuständig, Admin)
/// Zeitraum ohne von/bis: das laufende Schuljahr. Rechte siehe Services/FehlzeitenService.cs.
/// </summary>
[ApiController]
[Route("api/fehlzeiten")]
public class FehlzeitenController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public FehlzeitenController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    /// <summary>Ab diesem Tag gelten Stunden ohne Anwesenheit als „nicht erfasst" (Klassenbuch:ErfassungAb).</summary>
    private DateOnly ErfassungAb()
        => DateOnly.TryParseExact(_cfg["Klassenbuch:ErfassungAb"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var d)
            ? d
            : new DateOnly(2026, 11, 2);

    /// <summary>
    /// Phase 3.3: Ab diesem Tag gelten erfasste Stunden ohne Thema als offen (Klassenbuch:ThemaAb);
    /// fehlt die Einstellung, gilt ErfassungAb.
    /// </summary>
    private DateOnly ThemaAb()
        => DateOnly.TryParseExact(_cfg["Klassenbuch:ThemaAb"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var d)
            ? d
            : ErfassungAb();

    /// <summary>
    /// Phase 4.1: erster Tag des 2. Halbjahres (Klassenbuch:HalbjahrAb); fehlt die Einstellung oder
    /// liegt sie nicht im Schuljahr, gilt der 1. Februar.
    /// </summary>
    private DateOnly? HalbjahrAb()
        => DateOnly.TryParseExact(_cfg["Klassenbuch:HalbjahrAb"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var d)
            ? d
            : null;

    /// <summary>Phase 4.1 (Entscheidung 40): wählbare Zeiträume – auch für Schülerkonten.</summary>
    [Authorize]
    [HttpGet("zeitraeume")]
    public async Task<IActionResult> Zeitraeume()
        => Ok(await FehlzeitenService.ZeitraeumeAsync(_db, HalbjahrAb()));

    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("rechte")]
    public async Task<IActionResult> Rechte()
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        return Ok(await FehlzeitenService.RechteAsync(_db, u, ErfassungAb(), _cfg.GetValue("Berichte:NurAdmin", false)));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("nicht-erfasst")]
    public async Task<IActionResult> NichtErfasst(
        [FromQuery] string? ansicht, [FromQuery] string? lehrer, [FromQuery] string? klasse,
        [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] string? art)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        string a = (ansicht ?? "ich").Trim().ToLowerInvariant();
        bool erlaubt = a switch
        {
            "ich" => u.Kuerzel != null,
            "zustaendig" => u.IstKlassenleitungOderKoordination && u.Zustaendig.Count > 0,
            "alle" => u.IstAdmin,
            _ => false
        };
        if (!erlaubt) return StatusCode(403, new { fehler = "Diese Übersicht steht dir nicht zur Verfügung." });
        string was = (art ?? "alle").Trim().ToLowerInvariant();
        if (was is not ("alle" or "anwesenheit" or "thema")) was = "alle";
        return Ok(await FehlzeitenService.NichtErfasstAsync(_db, u, a, lehrer, klasse, ErfassungAb(), ThemaAb(), was, von, bis));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("unterrichte")]
    public async Task<IActionResult> Unterrichte()
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (u.Kuerzel == null) return Ok(Array.Empty<FzUnterrichtDto>());
        return Ok(await FehlzeitenService.UnterrichteAsync(_db, u.Kuerzel));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("unterricht")]
    public async Task<IActionResult> Unterricht(
        [FromQuery] int unr, [FromQuery] string? lehrer, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(lehrer)) return BadRequest(new { fehler = "Die Lehrkraft fehlt." });
        if (!u.IstAdmin && !KlassenbuchService.Gleich(u.Kuerzel, lehrer))
            return StatusCode(403, new { fehler = "Diese Liste sieht nur die Lehrkraft des Unterrichts." });

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var liste = await FehlzeitenService.UnterrichtAsync(_db, u, unr, lehrer, v, b);
        return liste == null ? NotFound(new { fehler = "Diesen Unterricht gibt es nicht." }) : Ok(liste);
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("klasse/{klasse}")]
    public async Task<IActionResult> Klasse(string klasse, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin && !(u.IstKlassenleitungOderKoordination
                             && KlassenbuchService.ZustaendigFuer(u.Zustaendig, new[] { klasse })))
            return StatusCode(403, new { fehler = "Diese Liste sehen nur die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        return Ok(await FehlzeitenService.KlasseAsync(_db, klasse, v, b));
    }

    /// <summary>Phase 4.1 (Entscheidung 43): offene Fälle einer Klasse/Stufe im ganzen Schuljahr.</summary>
    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("offen/{klasse}")]
    public async Task<IActionResult> Offen(string klasse)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin && !(u.IstKlassenleitungOderKoordination
                             && KlassenbuchService.ZustaendigFuer(u.Zustaendig, new[] { klasse })))
            return StatusCode(403, new { fehler = "Offene Fälle sehen nur die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });
        return Ok(await FehlzeitenService.OffeneFaelleAsync(_db, klasse));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("schueler/{id:int}")]
    public async Task<IActionResult> Schueler(
        int id, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] int? unr, [FromQuery] string? lehrer)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var d = await FehlzeitenService.SchuelerAsync(_db, u, id, v, b, unr, lehrer);
        return d == null ? NotFound(new { fehler = "Schüler nicht gefunden." }) : Ok(d);
    }

    /// <summary>Eigene Fehlzeiten – nur Schülerkonten; die Zuordnung kommt aus dem Konto.</summary>
    [Authorize(Roles = Rollen.Schueler)]
    [HttpGet("ich")]
    public async Task<IActionResult> Ich([FromQuery] DateOnly? von, [FromQuery] DateOnly? bis)
    {
        var id = Rollen.BenutzerId(User);
        var konto = id == null ? null : await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value);
        if (konto == null) return Unauthorized();
        if (konto.SchuelerId is not int sid)
            return NotFound(new { fehler = "Diesem Konto ist kein Schüler zugeordnet." });

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var d = await FehlzeitenService.SchuelerAsync(_db, null, sid, v, b, null, null);
        return d == null ? NotFound(new { fehler = "Diesem Konto ist kein Schüler zugeordnet." }) : Ok(d);
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpPost("schueler/{id:int}/entschuldigung")]
    public async Task<IActionResult> Entschuldigen(int id, [FromBody] FzEntschuldigungNeuDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        return Antwort(await FehlzeitenService.EntschuldigenAsync(_db, u, id, dto));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpDelete("entschuldigung/{id:int}")]
    public async Task<IActionResult> EntschuldigungLoeschen(int id)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        return Antwort(await FehlzeitenService.EntschuldigungLoeschenAsync(_db, u, id));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpPut("eintrag/{id:int}")]
    public async Task<IActionResult> Bewerten(int id, [FromBody] FzBewertungDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        return Antwort(await FehlzeitenService.BewertenAsync(_db, u, id, dto));
    }

    // -----------------------------------------------------------------

    private IActionResult Antwort(KbErgebnis e)
        => e.Status == 200
            ? Ok(new { meldung = e.Meldung })
            : StatusCode(e.Status, new { fehler = e.Fehler });

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
