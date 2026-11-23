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
/// P6b (232, 236): Leseprotokoll der Ansichten (Schüler, Klasse/Stufe, offene Fälle, Unterricht, „Nicht erfasst“ anderer
/// Lehrkräfte – nicht „ich“ und nicht „Meine Fehlzeiten“); Schalter „Grund der Entschuldigung erfassen“ (aus → „ohne
/// Angabe“) beim Entschuldigen und Bewerten, „rechte“ meldet ihn der App.
/// P5a (215, 216, 219, 242): Zeiträume in einem abgeschlossenen Schuljahr sieht nur der Admin (auch „Meine Fehlzeiten“ der
/// Schüler); Entschuldigen und Bewerten dort lehnt die API ab (nur lesbar). Eine Zuständigkeit gilt nur für Zeiträume ihres
/// Schuljahres (Services/SchuljahrRechte.cs). „Nicht erfasst“ in einem früheren Schuljahr rechnet mit dessen „erfasst ab“.
/// </summary>
[ApiController]
[Route("api/fehlzeiten")]
public class FehlzeitenController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Schuleinstellungen _einst;
    private readonly Leseprotokoll _lese;

    public FehlzeitenController(AppDbContext db, Schuleinstellungen einst, Leseprotokoll lese)
    {
        _db = db;
        _einst = einst;
        _lese = lese;
    }

    // P3 (E-1): „Anwesenheit erfasst ab“, „Thema erfasst ab“ und „2. Halbjahr ab“ kommen aus den Schuleinstellungen
    // (vorher Klassenbuch:ErfassungAb/ThemaAb/HalbjahrAb in appsettings). Standard: erster Schultag laut Kalender,
    // Thema wie Anwesenheit, 2. Halbjahr ab 1. Februar (FehlzeitenService.HalbjahrAb).
    private Task<SchulWerte> WerteAsync() => _einst.LesenAsync(_db);

    private IActionResult Fehler(KbErgebnis e) => StatusCode(e.Status, new { fehler = e.Fehler });

    /// <summary>Phase 4.1 (Entscheidung 40): wählbare Zeiträume – auch für Schülerkonten.</summary>
    [Authorize]
    [HttpGet("zeitraeume")]
    public async Task<IActionResult> Zeitraeume()
        => Ok(await FehlzeitenService.ZeitraeumeAsync(_db, (await WerteAsync()).HalbjahrAb));

    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("rechte")]
    public async Task<IActionResult> Rechte()
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var w = await WerteAsync();
        return Ok(await FehlzeitenService.RechteAsync(_db, u, w.ErfassungAb, w.BerichteNurAdmin, w.GrundErfassen));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("nicht-erfasst")]
    public async Task<IActionResult> NichtErfasst(
        [FromQuery] string? ansicht, [FromQuery] string? lehrer, [FromQuery] string? klasse,
        [FromQuery] DateOnly? von, [FromQuery] DateOnly? bis, [FromQuery] string? art)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        var w = await WerteAsync();
        // P5a (216, 242): Ohne Zeitraum beginnt die Liste beim noch offenen Vorjahr (Nachträge bis zum Abschluss), sonst bei
        // „erfasst ab“; ein Zeitraum in einem früheren Schuljahr rechnet mit dessen „erfasst ab“. Abgeschlossene Schuljahre
        // nur für den Admin. Die Zuständigkeit prüft NichtErfasstAsync je Tag (nur im eigenen Schuljahr).
        var heute = KlassenbuchService.Heute();
        var (erfassungAb, themaAb) = (w.ErfassungAb, w.ThemaAb);
        if (von == null && bis == null)
        {
            if (await SchuljahrRechte.OffenesVorjahrAsync(_db, w) is { } vorjahr)
                (erfassungAb, themaAb) = (vorjahr.ErfassungAb, vorjahr.ThemaAb);
        }
        else
        {
            (erfassungAb, themaAb) = await SchuljahrRechte.ErfassungAbAsync(_db, w, von ?? bis);
        }
        var pruefVon = von ?? (erfassungAb < themaAb ? erfassungAb : themaAb);
        var pruefBis = bis ?? heute.AddDays(-1);
        var (_, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, pruefVon, pruefBis < pruefVon ? pruefVon : pruefBis);
        if (sperre != null) return Fehler(sperre);
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
        var d = await FehlzeitenService.NichtErfasstAsync(_db, u, a, lehrer, klasse, erfassungAb, themaAb, was, von, bis);
        // P6b (232): Daten über andere Lehrkräfte – „ich“ sind eigene Daten.
        if (a != "ich")
        {
            var teile = new List<string> { a == "alle" ? "alle Lehrkräfte" : "Zuständigkeit" };
            if (!string.IsNullOrWhiteSpace(lehrer)) teile.Add("Lehrkraft " + lehrer.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(klasse)) teile.Add(LeseAnsichten.GruppenGegenstand(klasse));
            if (was != "alle") teile.Add(was == "thema" ? "Thema" : "Anwesenheit");
            await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.NichtErfasst, string.Join(" · ", teile)));
        }
        return Ok(d);
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
        var (uj, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db, u, v, b);   // P5a (216)
        if (sperre != null) return Fehler(sperre);
        u = uj;
        var liste = await FehlzeitenService.UnterrichtAsync(_db, u, unr, lehrer, v, b);
        if (liste == null) return NotFound(new { fehler = "Diesen Unterricht gibt es nicht." });
        int un = unr;
        string le = lehrer;
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.FehlzeitenUnterricht,
            LeseAnsichten.UnterrichtGegenstand(un, le), GruppeLaden: db => LeseAnsichten.GruppenDesUnterrichtsAsync(db, un, le)));
        return Ok(liste);
    }

    [Authorize(Roles = Rollen.Personal)]
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
                ?? "Diese Liste sehen nur die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });

        var d = await FehlzeitenService.KlasseAsync(_db, klasse, v, b);
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.FehlzeitenKlasse, LeseAnsichten.GruppenGegenstand(klasse),
            Gruppe: LeseAnsichten.Liste(new[] { klasse })));
        return Ok(d);
    }

    /// <summary>Phase 4.1 (Entscheidung 43): offene Fälle einer Klasse/Stufe im ganzen Schuljahr.</summary>
    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("offen/{klasse}")]
    public async Task<IActionResult> Offen(string klasse)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        // P5a (216, 219): Schuljahr von heute (offene Fälle gelten für das laufende Schuljahr laut Kalender)
        var (uj, sperre) = await SchuljahrRechte.LesenAsync(_db, u,
            await SchuljahrRechte.JahrAmAsync(_db, KlassenbuchService.Heute()));
        if (sperre != null) return Fehler(sperre);
        var vorher = u;
        u = uj;
        if (!u.IstAdmin && !(u.IstKlassenleitungOderKoordination
                             && KlassenbuchService.ZustaendigFuer(u.Zustaendig, new[] { klasse })))
            return StatusCode(403, new { fehler = SchuljahrRechte.AnderesJahrText(vorher, new[] { klasse })
                ?? "Offene Fälle sehen nur die Klassenleitung bzw. Stufenkoordination und die Verwaltung." });
        var d = await FehlzeitenService.OffeneFaelleAsync(_db, klasse);
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.OffeneFaelle, LeseAnsichten.GruppenGegenstand(klasse),
            Gruppe: LeseAnsichten.Liste(new[] { klasse })));
        return Ok(d);
    }

    [Authorize(Roles = Rollen.Personal)]
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
        var d = await FehlzeitenService.SchuelerAsync(_db, u, id, v, b, unr, lehrer);
        if (d == null) return NotFound(new { fehler = "Schüler nicht gefunden." });
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.FehlzeitenSchueler,
            LeseAnsichten.SchuelerGegenstand(id, d.Klasse), SchuelerId: id));
        return Ok(d);
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
        // P5a (216): Zeiträume eines abgeschlossenen Schuljahres sieht nur die Verwaltung – auch nicht der Schüler selbst.
        var (_, sperre) = await SchuljahrRechte.LesenZeitraumAsync(_db,
            new KbNutzer(konto.Benutzername, konto.Rolle, null, Array.Empty<string>()), v, b);
        if (sperre != null) return Fehler(sperre);
        var d = await FehlzeitenService.SchuelerAsync(_db, null, sid, v, b, null, null);
        return d == null ? NotFound(new { fehler = "Diesem Konto ist kein Schüler zugeordnet." }) : Ok(d);
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpPost("schueler/{id:int}/entschuldigung")]
    public async Task<IActionResult> Entschuldigen(int id, [FromBody] FzEntschuldigungNeuDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        return Antwort(await FehlzeitenService.EntschuldigenAsync(_db, u, id, dto, await _einst.GrundErfassenAsync(_db)));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpDelete("entschuldigung/{id:int}")]
    public async Task<IActionResult> EntschuldigungLoeschen(int id)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        return Antwort(await FehlzeitenService.EntschuldigungLoeschenAsync(_db, u, id, await _einst.GrundErfassenAsync(_db)));
    }

    [Authorize(Roles = Rollen.Personal)]
    [HttpPut("eintrag/{id:int}")]
    public async Task<IActionResult> Bewerten(int id, [FromBody] FzBewertungDto dto)
    {
        var u = await NutzerAsync();
        if (u == null) return Unauthorized();
        return Antwort(await FehlzeitenService.BewertenAsync(_db, u, id, dto, await _einst.GrundErfassenAsync(_db)));
    }

    // -----------------------------------------------------------------

    private IActionResult Antwort(KbErgebnis e)
        => e.Status == 200
            ? Ok(new { meldung = e.Meldung })
            : StatusCode(e.Status, new { fehler = e.Fehler });

    /// <summary>Angemeldete Person samt Kürzel und Zuständigkeit (mit Schuljahr, P5a) – aus der Datenbank.</summary>
    private async Task<KbNutzer?> NutzerAsync()
    {
        var id = Rollen.BenutzerId(User);
        if (id == null) return null;
        var b = await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value);
        if (b == null) return null;
        return KlassenbuchService.NutzerAus(b, await _einst.AktuellesSchuljahrAsync(_db));
    }
}
