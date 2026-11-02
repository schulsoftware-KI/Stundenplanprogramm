using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

/// <summary>
/// Klassenbuch (Phase 3.1) – nur Personal:
///   GET  api/klassenbuch/stunde?datum=&amp;stunde=&amp;unr=&amp;lehrer=   Stundenblatt (unr/lehrer = GrundUNr/GrundLehrer)
///   PUT  api/klassenbuch/stunde                                 Anwesenheit speichern (KbSpeichernDto;
///                                                               folgestundeUebernehmen = Doppelstunde mit erfassen)
///   GET  api/klassenbuch/stunde/historie?datum=&amp;stunde=&amp;unr=&amp;lehrer=  Änderungshistorie
///   GET  api/klassenbuch/gruppen                                „Meine Gruppen" (Admin: alle)
///   GET  api/klassenbuch/gruppe?schluessel=…                    Gruppe mit Pool (Schüler der Klassen)
///   PUT  api/klassenbuch/gruppe?schluessel=…                    Gruppe zusammenstellen ({ mitglieder: [ids] })
/// Rechte siehe Services/KlassenbuchService.cs.
/// </summary>
[ApiController]
[Route("api/klassenbuch")]
[Authorize(Roles = Rollen.Personal)]
public class KlassenbuchController : ControllerBase
{
    private readonly AppDbContext _db;

    public KlassenbuchController(AppDbContext db) => _db = db;

    [HttpGet("stunde")]
    public async Task<IActionResult> Stunde(
        [FromQuery] DateOnly datum, [FromQuery] int stunde, [FromQuery] int unr, [FromQuery] string? lehrer)
    {
        var (u, tag, st, fehler) = await LadeAsync(datum, stunde, unr, lehrer);
        if (fehler != null) return fehler;
        return Ok(await KlassenbuchService.StundenblattAsync(_db, u!, tag!, st!));
    }

    [HttpPut("stunde")]
    public async Task<IActionResult> Speichern([FromBody] KbSpeichernDto dto)
    {
        var (u, tag, st, fehler) = await LadeAsync(dto.Datum, dto.Stunde, dto.GrundUNr, dto.GrundLehrer);
        if (fehler != null) return fehler;

        var e = await KlassenbuchService.SpeichernAsync(_db, u!, tag!, st!, dto);
        if (e.Status != 200) return StatusCode(e.Status, new { fehler = e.Fehler, konflikt = e.Konflikt });
        return Ok(await KlassenbuchService.StundenblattAsync(_db, u!, tag!, st!, e.Meldung));
    }

    [HttpGet("stunde/historie")]
    public async Task<IActionResult> Historie(
        [FromQuery] DateOnly datum, [FromQuery] int stunde, [FromQuery] int unr, [FromQuery] string? lehrer)
    {
        var (_, tag, st, fehler) = await LadeAsync(datum, stunde, unr, lehrer);
        if (fehler != null) return fehler;
        return Ok(await KlassenbuchService.HistorieAsync(_db, tag!, st!));
    }

    [HttpGet("gruppen")]
    public async Task<IActionResult> Gruppen()
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        return Ok(await KlassenbuchService.GruppenListeAsync(_db, u));
    }

    [HttpGet("gruppe")]
    public async Task<IActionResult> Gruppe([FromQuery] string? schluessel)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(schluessel)) return BadRequest(new { fehler = "Gruppe fehlt." });

        var g = await KlassenbuchService.GruppeAusGrundplanAsync(_db, schluessel);
        if (g == null) return NotFound(new { fehler = "Diese Gruppe steht nicht im aktuellen Stundenplan." });
        if (!KlassenbuchService.DarfGruppeSehen(u, g))
            return StatusCode(403, new { fehler = "Diese Gruppe dürfen nur ihre Lehrkräfte, die Klassenleitung bzw. Stufenkoordination und die Verwaltung sehen." });
        return Ok(await KlassenbuchService.GruppeAsync(_db, u, g));
    }

    [HttpPut("gruppe")]
    public async Task<IActionResult> GruppeSpeichern([FromQuery] string? schluessel, [FromBody] KbGruppeSpeichernDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(schluessel)) return BadRequest(new { fehler = "Gruppe fehlt." });

        var g = await KlassenbuchService.GruppeAusGrundplanAsync(_db, schluessel);
        if (g == null) return NotFound(new { fehler = "Diese Gruppe steht nicht im aktuellen Stundenplan." });
        if (!KlassenbuchService.IstGruppenLehrer(u, g))
            return StatusCode(403, new { fehler = "Die Gruppe dürfen nur ihre Lehrkräfte (laut Stundenplan) und die Verwaltung zusammenstellen." });

        var e = await KlassenbuchService.GruppeSpeichernAsync(_db, u, g, dto.Mitglieder ?? new List<int>());
        if (e.Status != 200) return StatusCode(e.Status, new { fehler = e.Fehler });
        return Ok(await KlassenbuchService.GruppeAsync(_db, u, g));
    }

    // -----------------------------------------------------------------

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

    /// <summary>Person, Tag und Stunde laden; prüft Rechte und ob es ein Klassenbuch gibt.</summary>
    private async Task<(KbNutzer? U, TagKontext? Tag, PlanStunde? St, IActionResult? Fehler)> LadeAsync(
        DateOnly datum, int stunde, int unr, string? lehrer)
    {
        var u = await NutzerAsync();
        if (u == null) return (null, null, null, Unauthorized());
        if (string.IsNullOrWhiteSpace(lehrer))
            return (u, null, null, BadRequest(new { fehler = "Die Lehrkraft der Stunde fehlt." }));

        var tag = await WochenplanService.TagAsync(_db, datum);
        if (tag == null)
            return (u, null, null, NotFound(new { fehler = "An diesem Tag findet laut Plan kein Unterricht statt." }));

        var st = KlassenbuchService.FindeStunde(tag, stunde, unr, lehrer);
        if (st == null)
            return (u, tag, null, NotFound(new { fehler = "Diese Stunde gibt es laut Plan nicht (mehr)." }));

        if (!KlassenbuchService.DarfStunde(u, st))
            return (u, tag, st, StatusCode(403, new
            {
                fehler = "Dieses Stundenblatt dürfen nur die unterrichtende bzw. vertretende Lehrkraft, " +
                         "die Klassenleitung bzw. Stufenkoordination und die Verwaltung öffnen."
            }));

        var grund = KlassenbuchService.KeinKlassenbuchGrund(st);
        if (grund != null) return (u, tag, st, Conflict(new { fehler = grund }));

        return (u, tag, st, null);
    }
}
