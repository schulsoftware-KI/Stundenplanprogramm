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
/// P6b (232): Leseprotokoll für alle lesenden Abrufe (Stunde, Verlauf, Schüler, Klasse/Stufe, Unterricht).
/// P5a (215, 216, 219, 242): Einträge eines abgeschlossenen Schuljahres sieht nur der Admin (nur lesbar); eine Zuständigkeit gilt
/// nur für Einträge ihres Schuljahres (Services/SchuljahrRechte.cs).
/// </summary>
[ApiController]
[Route("api/eintraege")]
[Authorize(Roles = Rollen.Personal)]
public class EintraegeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Leseprotokoll _lese;
    private readonly Schuleinstellungen _einst;

    public EintraegeController(AppDbContext db, Leseprotokoll lese, Schuleinstellungen einst)
    {
        _db = db;
        _lese = lese;
        _einst = einst;
    }

    private IActionResult Fehler(KbErgebnis e) => StatusCode(e.Status, new { fehler = e.Fehler });

    [HttpGet("stunde")]
    public async Task<IActionResult> Stunde(
        [FromQuery] DateOnly datum, [FromQuery] int stunde, [FromQuery] int unr, [FromQuery] string? lehrer)
    {
        var (u, tag, st, fehler) = await LadeAsync(datum, stunde, unr, lehrer);
        if (fehler != null) return fehler;
        var d = await KlassenbuchEintragService.StundeAsync(_db, u!, tag!, st!);
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.EintraegeStunde,
            LeseAnsichten.StundeGegenstand(tag!.Datum, st!), Gruppe: LeseAnsichten.GruppenDerStunde(st!)));
        return Ok(d);
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
        // P6b (232): Schülereintrag → Einzelansicht des Schülers; Klasseneintrag → Lerngruppen der Stunde.
        var e = await _db.KlassenbuchEintraege.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.SchuelerId, x.Lerngruppen, x.Klassen, x.Schuljahr, x.Datum }).FirstOrDefaultAsync();
        if (e != null)
        {
            // P5a (216, 219): Schuljahr des Eintrags
            var jahr = string.IsNullOrWhiteSpace(e.Schuljahr) ? await SchuljahrRechte.JahrAmAsync(_db, e.Datum) : e.Schuljahr;
            var (uj, sperre) = await SchuljahrRechte.LesenAsync(_db, u, jahr);
            if (sperre != null) return Fehler(sperre);
            u = uj;
        }
        var liste = await KlassenbuchEintragService.HistorieAsync(_db, u, id);
        if (liste == null) return NotFound(new { fehler = "Diesen Eintrag gibt es nicht (mehr)." });
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.EintragVerlauf, $"Eintrag-Id {id}",
            SchuelerId: e?.SchuelerId,
            Gruppe: e == null || e.SchuelerId != null ? null
                : LeseAnsichten.Liste(LeseAnsichten.Teile(string.IsNullOrWhiteSpace(e.Lerngruppen) ? e.Klassen : e.Lerngruppen))));
        return Ok(liste);
    }

    [HttpGet("schueler/{id:int}")]
    public async Task<IActionResult> Schueler(
        int id, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] int? unr, [FromQuery] string? lehrer)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var (uj, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, v, b);   // P5a (216, 219)
        if (sperre != null) return Fehler(sperre);
        u = uj;
        var d = await KlassenbuchEintragService.SchuelerAsync(_db, u, id, v, b, unr, lehrer);
        if (d == null) return NotFound(new { fehler = "Schüler nicht gefunden." });
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.EintraegeSchueler,
            LeseAnsichten.SchuelerGegenstand(id, null), SchuelerId: id));
        return Ok(d);
    }

    [HttpGet("klasse/{klasse}")]
    public async Task<IActionResult> Klasse(string klasse, [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var (v, b) = await FehlzeitenService.ZeitraumAsync(_db, von, bis);
        var (uj, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, v, b);   // P5a (216, 219)
        if (sperre != null) return Fehler(sperre);
        var vorher = u;
        u = uj;
        if (!u.IstAdmin && !(u.IstKlassenleitungOderKoordination
                             && KlassenbuchService.ZustaendigFuer(u.Zustaendig, new[] { klasse })))
            return StatusCode(403, new { fehler = SchuljahrRechte.AnderesJahrText(vorher, new[] { klasse })
                ?? "Die Einträge einer Klasse bzw. Stufe sehen die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });
        var d = await KlassenbuchEintragService.KlasseAsync(_db, u, klasse, v, b);
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.EintraegeKlasse, LeseAnsichten.GruppenGegenstand(klasse),
            Gruppe: LeseAnsichten.Liste(new[] { klasse })));
        return Ok(d);
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
        var (uj, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, v, b);   // P5a (216)
        if (sperre != null) return Fehler(sperre);
        u = uj;
        var d = await KlassenbuchEintragService.UnterrichtAsync(_db, u, unr, lehrer, v, b);
        if (d == null) return NotFound(new { fehler = "Diesen Unterricht gibt es nicht." });
        int un = unr;
        string le = lehrer;
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.EintraegeUnterricht,
            LeseAnsichten.UnterrichtGegenstand(un, le), GruppeLaden: db => LeseAnsichten.GruppenDesUnterrichtsAsync(db, un, le)));
        return Ok(d);
    }

    // -----------------------------------------------------------------

    /// <summary>Angemeldete Person samt Kürzel und Zuständigkeit (mit Schuljahr, P5a) – aus der Datenbank.</summary>
    private async Task<KbNutzer?> NutzerAsync()
    {
        var id = Rollen.BenutzerId(User);
        if (id == null) return null;
        var b = await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value);
        if (b == null) return null;
        return KlassenbuchService.NutzerAus(b, await _einst.AktuellesSchuljahrAsync(_db));
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

        // P5a (216, 219, 242): abgeschlossenes Schuljahr nur für den Admin; Zuständigkeit nur im eigenen Schuljahr.
        var (uj, sperre) = await SchuljahrRechte.LesenAsync(_db, u, tag.Schuljahr);
        if (sperre != null) return (u, tag, st, Fehler(sperre));
        var vorher = u;
        u = uj;

        if (!KlassenbuchService.DarfStunde(u, st))
            return (u, tag, st, StatusCode(403, new
            {
                fehler = SchuljahrRechte.AnderesJahrText(vorher, st.Klassen) ??
                         "Einträge dieser Stunde sehen nur die unterrichtende bzw. vertretende Lehrkraft, " +
                         "die Klassenleitung bzw. Stufenkoordination und die Verwaltung."
            }));

        var grund = KlassenbuchService.KeinKlassenbuchGrund(st);
        if (grund != null) return (u, tag, st, Conflict(new { fehler = grund }));

        return (u, tag, st, null);
    }
}
