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
///                                                               (Phase 3.4: mit den Klassenbucheinträgen, die man lesen darf)
///   GET  api/klassenbuch/gruppen                                „Meine Gruppen" (Admin: alle)
///   GET  api/klassenbuch/gruppe?schluessel=…                    Gruppe mit Pool (Schüler der Klassen)
///   PUT  api/klassenbuch/gruppe?schluessel=…                    Gruppe zusammenstellen ({ mitglieder: [ids] })
///   Phase 3.3:
///   PUT  api/klassenbuch/stunde nimmt zusätzlich inhalt { thema, hausaufgabe, faellig } und
///        anwesenheitErfassen (false = nur Thema/Hausaufgabe speichern)
///   GET  api/klassenbuch/themen?unr=&amp;lehrer=&amp;von=&amp;bis=        Themenliste eines Unterrichts (Lehrkraft selbst, Admin)
///   PUT  api/klassenbuch/sichtbarkeit                           Hausaufgaben für Schüler sichtbar? (KbSichtbarkeitDto)
/// Rechte siehe Services/KlassenbuchService.cs und Services/UnterrichtInhaltService.cs.
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
        var (u, tag, st, fehler) = await LadeAsync(datum, stunde, unr, lehrer);
        if (fehler != null) return fehler;
        return Ok(await KlassenbuchService.HistorieAsync(_db, u!, tag!, st!));
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

    /// <summary>Themenliste eines Unterrichts (UNr + Lehrkraft laut Plan); Standard: laufendes Schuljahr.</summary>
    [HttpGet("themen")]
    public async Task<IActionResult> Themen(
        [FromQuery] int unr, [FromQuery] string? lehrer, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(lehrer)) return BadRequest(new { fehler = "Die Lehrkraft fehlt." });
        if (!u.IstAdmin && !KlassenbuchService.Gleich(u.Kuerzel, lehrer))
            return StatusCode(403, new { fehler = "Die Themenliste sieht nur die Lehrkraft des Unterrichts." });

        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var d = await UnterrichtInhaltService.ThemenAsync(_db, u, unr, lehrer, v, b);
        return d == null ? NotFound(new { fehler = "Diesen Unterricht gibt es nicht." }) : Ok(d);
    }

    /// <summary>Hausaufgaben eines Unterrichts für Schüler sichtbar bzw. ausgeblendet (Entscheidung 30).</summary>
    [HttpPut("sichtbarkeit")]
    public async Task<IActionResult> Sichtbarkeit([FromBody] KbSichtbarkeitDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var e = await UnterrichtInhaltService.SichtbarkeitSetzenAsync(_db, u, dto);
        if (e.Status != 200) return StatusCode(e.Status, new { fehler = e.Fehler, konflikt = e.Konflikt });
        return Ok(new { sichtbar = dto.Sichtbar, meldung = e.Meldung });
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
