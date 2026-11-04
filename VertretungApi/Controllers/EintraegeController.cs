using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

/// <summary>
/// Klassenbucheinträge (Phase 3.4) – nur Personal; Schüler sehen keine Einträge (Entscheidung 36):
///   GET    api/eintraege/stunde?datum=&amp;stunde=&amp;unr=&amp;lehrer=      Einträge einer Stunde (wie das Stundenblatt)
///   POST   api/eintraege/stunde                                   neuer Klassen- bzw. Schülereintrag (KeNeuStundeDto)
///   POST   api/eintraege/schueler/{id}                            Schülereintrag ohne Stunde (KL/SK/Admin; KeNeuOhneStundeDto)
///   PUT    api/eintraege/{id}                                     ändern (Verfasser, Admin; KeAendernDto)
///   DELETE api/eintraege/{id}                                     löschen (Verfasser, Admin)
///   GET    api/eintraege/{id}/historie                            Änderungen eines Eintrags
///   GET    api/eintraege/schueler/{id}?von=&amp;bis=&amp;unr=&amp;lehrer=     Einträge eines Schülers (Lehrkräfte: nur eigene)
///   GET    api/eintraege/klasse/{klasse}?von=&amp;bis=                 Einträge einer Klasse/Stufe (KL/SK zuständig, Admin)
///   GET    api/eintraege/unterricht?unr=&amp;lehrer=&amp;von=&amp;bis=        Einträge eines Unterrichts (Lehrkraft selbst, Admin)
/// Zeitraum ohne von/bis: das laufende Schuljahr. Rechte siehe Services/KlassenbuchEintragService.cs.
/// </summary>
[ApiController]
[Route("api/eintraege")]
[Authorize(Roles = Rollen.Personal)]
public class EintraegeController : ControllerBase
{
    private readonly AppDbContext _db;

    public EintraegeController(AppDbContext db) => _db = db;

    [HttpGet("stunde")]
    public async Task<IActionResult> Stunde(
        [FromQuery] DateOnly datum, [FromQuery] int stunde, [FromQuery] int unr, [FromQuery] string? lehrer)
    {
        var (u, tag, st, fehler) = await LadeAsync(datum, stunde, unr, lehrer);
        if (fehler != null) return fehler;
        return Ok(await KlassenbuchEintragService.StundeAsync(_db, u!, tag!, st!));
    }

    [HttpPost("stunde")]
    public async Task<IActionResult> Anlegen([FromBody] KeNeuStundeDto dto)
    {
        var (u, tag, st, fehler) = await LadeAsync(dto.Datum, dto.Stunde, dto.GrundUNr, dto.GrundLehrer);
        if (fehler != null) return fehler;
        var e = await KlassenbuchEintragService.AnlegenAsync(_db, u!, tag!, st!, dto);
        if (e.Status != 200) return StatusCode(e.Status, new { fehler = e.Fehler });
        return Ok(await KlassenbuchEintragService.StundeAsync(_db, u!, tag!, st!, e.Meldung));
    }

    [HttpPost("schueler/{id:int}")]
    public async Task<IActionResult> AnlegenOhneStunde(int id, [FromBody] KeNeuOhneStundeDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var e = await KlassenbuchEintragService.AnlegenOhneStundeAsync(_db, u, id, dto);
        return e.Status == 200 ? Ok(new { meldung = e.Meldung }) : StatusCode(e.Status, new { fehler = e.Fehler });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Aendern(int id, [FromBody] KeAendernDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var (e, eintrag) = await KlassenbuchEintragService.AendernAsync(_db, u, id, dto);
        if (e.Status != 200) return StatusCode(e.Status, new { fehler = e.Fehler, konflikt = e.Konflikt });
        return Ok(new { meldung = e.Meldung, eintrag });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Loeschen(int id)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var e = await KlassenbuchEintragService.LoeschenAsync(_db, u, id);
        return e.Status == 200 ? Ok(new { meldung = e.Meldung }) : StatusCode(e.Status, new { fehler = e.Fehler });
    }

    [HttpGet("{id:int}/historie")]
    public async Task<IActionResult> Historie(int id)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var liste = await KlassenbuchEintragService.HistorieAsync(_db, u, id);
        return liste == null ? NotFound(new { fehler = "Diesen Eintrag gibt es nicht (mehr)." }) : Ok(liste);
    }

    [HttpGet("schueler/{id:int}")]
    public async Task<IActionResult> Schueler(
        int id, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] int? unr, [FromQuery] string? lehrer)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var d = await KlassenbuchEintragService.SchuelerAsync(_db, u, id, v, b, unr, lehrer);
        return d == null ? NotFound(new { fehler = "Schüler nicht gefunden." }) : Ok(d);
    }

    [HttpGet("klasse/{klasse}")]
    public async Task<IActionResult> Klasse(string klasse, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        if (!u.IstAdmin && !(u.IstKlassenleitungOderKoordination
                             && KlassenbuchService.ZustaendigFuer(u.Zustaendig, new[] { klasse })))
            return StatusCode(403, new { fehler = "Die Einträge einer Klasse bzw. Stufe sehen die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        return Ok(await KlassenbuchEintragService.KlasseAsync(_db, u, klasse, v, b));
    }

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
        var d = await KlassenbuchEintragService.UnterrichtAsync(_db, u, unr, lehrer, v, b);
        return d == null ? NotFound(new { fehler = "Diesen Unterricht gibt es nicht." }) : Ok(d);
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

    /// <summary>Person, Tag und Stunde laden; prüft Rechte und ob es ein Klassenbuch gibt (wie das Stundenblatt).</summary>
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
                fehler = "Einträge dieser Stunde sehen nur die unterrichtende bzw. vertretende Lehrkraft, " +
                         "die Klassenleitung bzw. Stufenkoordination und die Verwaltung."
            }));

        var grund = KlassenbuchService.KeinKlassenbuchGrund(st);
        if (grund != null) return (u, tag, st, Conflict(new { fehler = grund }));

        return (u, tag, st, null);
    }
}
