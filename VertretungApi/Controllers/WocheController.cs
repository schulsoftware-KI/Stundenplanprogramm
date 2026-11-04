using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

/// <summary>
/// Wochenpläne (Phase 2, Schritt 2) – Grundplan × Kalender × Vertretungen:
///   GET api/woche/ich?datum=YYYY-MM-DD&amp;wochen=1     (eigener Plan: Lehrkraft bzw. Schüler; JWT)
///   GET api/woche/lehrer/{kuerzel}?datum=…&amp;wochen=…  (Richtlinie PlanLesen)
///   GET api/woche/klasse/{klasse}?datum=…&amp;wochen=…   (z. B. 05A, auch EF/Q1/Q2 = ganze Stufe; PlanLesen)
///   GET api/woche/gruppe/{schluessel}?datum=…&amp;wochen=… (Lerngruppe laut Grundplan, z. B. BIG1_EF_L68; PlanLesen)
///   GET api/woche/schueler/{id}?datum=…&amp;wochen=…      (individueller Plan; nur Personal)
///   GET api/woche/auswahl?datum=…                     (Auswahllisten für die PWA; PlanLesen)
///
/// Phase 3.3: Stunden tragen Thema/Hausaufgabe, soweit die angemeldete Person sie lesen darf
/// (siehe InhaltLeser in Services/WochenplanService.cs); ohne Anmeldung nie.
///
/// datum fehlt = heute; es wird immer ab dem Montag dieser Woche geliefert. wochen: 1–6.
/// PlanLesen (Phase 2, Schritt 4) = nur Personal; mit "Woche:OhneLogin": true für alle.
/// Schüler sehen ausschließlich ihren eigenen Plan über /ich.
/// </summary>
[ApiController]
[Route("api/woche")]
public class WocheController : ControllerBase
{
    private readonly AppDbContext _db;

    public WocheController(AppDbContext db) => _db = db;

    private static DateOnly Heute() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Was sich in der PWA auswählen lässt (aus dem Grundplan, der am Datum gilt).</summary>
    [Authorize(Policy = Rollen.PlanLesen)]
    [HttpGet("auswahl")]
    public async Task<IActionResult> Auswahl([FromQuery] DateOnly? datum)
        => Ok(await WochenplanService.AuswahlAsync(_db, datum ?? Heute()));

    [Authorize(Policy = Rollen.PlanLesen)]
    [HttpGet("lehrer/{kuerzel}")]
    public async Task<IActionResult> Lehrer(string kuerzel, [FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
        => Ok(await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Lehrer, kuerzel, new[] { kuerzel }, datum ?? Heute(), wochen, null, await PersonalLeserAsync()));

    [Authorize(Policy = Rollen.PlanLesen)]
    [HttpGet("klasse/{klasse}")]
    public async Task<IActionResult> Klasse(string klasse, [FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
        => Ok(await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Klasse, klasse, new[] { klasse }, datum ?? Heute(), wochen, null, await PersonalLeserAsync()));

    [Authorize(Policy = Rollen.PlanLesen)]
    [HttpGet("gruppe/{schluessel}")]
    public async Task<IActionResult> Gruppe(string schluessel, [FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
        => Ok(await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Gruppe, schluessel, new[] { schluessel }, datum ?? Heute(), wochen, null, await PersonalLeserAsync()));

    /// <summary>Individueller Plan eines Schülers aus seinen Lerngruppen (Klasse bzw. Kurse).</summary>
    [Authorize(Roles = Rollen.Personal)]
    [HttpGet("schueler/{id:int}")]
    public async Task<IActionResult> Schueler(int id, [FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
    {
        var s = await _db.Schueler.FindAsync(id);
        if (s == null) return NotFound(new { fehler = "Schüler nicht gefunden." });
        return Ok(await SchuelerPlan(s, datum ?? Heute(), wochen, await PersonalLeserAsync()));
    }

    /// <summary>
    /// Eigener Plan der angemeldeten Person: Schülerkonto -> Plan aus den eigenen Lerngruppen,
    /// Lehrkraft -> Plan zum eigenen Kürzel. Die Zuordnung kommt aus dem Konto, nie aus der Adresse.
    /// </summary>
    [Authorize]
    [HttpGet("ich")]
    public async Task<IActionResult> Ich([FromQuery] DateOnly? datum, [FromQuery] int wochen = 1)
    {
        var id = Rollen.BenutzerId(User);
        var konto = id == null ? null : await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id.Value);
        if (konto == null) return Unauthorized();

        if (konto.Rolle == BenutzerRolle.Schueler)
        {
            var s = konto.SchuelerId is int sid ? await _db.Schueler.FindAsync(sid) : null;
            if (s == null)
                return NotFound(new { fehler = "Diesem Konto ist kein Schüler zugeordnet." });
            return Ok(await SchuelerPlan(s, datum ?? Heute(), wochen, InhaltLeser.FuerSchueler()));
        }

        if (string.IsNullOrWhiteSpace(konto.LehrerKuerzel))
            return NotFound(new { fehler = "Diesem Konto ist keine Lehrkraft zugeordnet." });

        var k = konto.LehrerKuerzel.Trim();
        var plan = await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Lehrer, k, new[] { k }, datum ?? Heute(), wochen, null,
            InhaltLeser.FuerPersonal(NutzerAus(konto)));
        return Ok(new { Art = "lehrer", Lehrer = k, Plan = plan });
    }

    private async Task<object> SchuelerPlan(Schueler s, DateOnly datum, int wochen, InhaltLeser? leser)
    {
        var gruppen = await _db.Zugehoerigkeiten
            .Where(z => z.SchuelerId == s.Id)
            .Select(z => z.Lerngruppe!.Bezeichnung)
            .ToListAsync();

        // Phase 3.1: zusammengestellte Sek-I-Gruppen – Stunden einer solchen Gruppe sieht der
        // Schüler nur, wenn er dazugehört (sonst wie bisher die ganze Klasse).
        var fertig = await WochenplanService.FertigeGruppenAsync(_db);
        var plan = await WochenplanService.BerechneAsync(
            _db, WochenZielArt.Schueler, $"{s.Vorname} {s.Nachname}", gruppen, datum, wochen, fertig, leser);

        return new
        {
            Art = "schueler",
            Schueler = new { s.Id, s.Vorname, s.Nachname, s.Stufe, Lerngruppen = gruppen.OrderBy(g => g).ToList() },
            Plan = plan
        };
    }

    /// <summary>
    /// Phase 3.3: Leserechte für Thema/Hausaufgabe der angemeldeten Person (Personal).
    /// Ohne Anmeldung bzw. für Schülerkonten in diesen Ansichten: keine.
    /// </summary>
    private async Task<InhaltLeser?> PersonalLeserAsync()
    {
        var id = Rollen.BenutzerId(User);
        if (id == null) return null;
        var b = await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value);
        if (b == null || b.Rolle == BenutzerRolle.Schueler) return null;
        return InhaltLeser.FuerPersonal(NutzerAus(b));
    }

    private static KbNutzer NutzerAus(Benutzer b) => new(
        b.Benutzername, b.Rolle,
        string.IsNullOrWhiteSpace(b.LehrerKuerzel) ? null : b.LehrerKuerzel.Trim(),
        KontenService.Zustaendigkeiten(b.Zustaendig));
}
