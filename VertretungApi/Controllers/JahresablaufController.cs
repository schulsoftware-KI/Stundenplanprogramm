using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

// =====================================================================
//  P4a (5.3a, Entscheidung 205): Bereich „Jahresablauf“ – nur Admin (JWT).
//
//    GET  api/jahresablauf                 – Karten mit Ampel, ohne Namen
//
//  P5a (5.3b, Entscheidungen 215–217, 239, 242): Aktionen der Karte „Schuljahreswechsel“ – jede mit Rückfrage
//  (409 mit „bestaetigen“, Warnungen der Vorprüfung und den Änderungen) und Eintrag im Protokoll der Einstellungen
//  („Jahresablauf“):
//    POST api/jahresablauf/abschliessen    – { schuljahr, bestaetigt }: Schuljahr abschließen (nur lesbar, nur Admin)
//    POST api/jahresablauf/oeffnen         – { schuljahr, begruendung }: wieder öffnen (Begründung 10–300 Zeichen)
//    POST api/jahresablauf/neues-schuljahr – { schuljahr (das neue), bestaetigt }: „Aktuelles Schuljahr“ umstellen,
//                                            Daten des bisherigen Jahres auf Standard
//  Antwort 200: { meldung, stand } mit der neuen Übersicht. Logik und Texte: Services/Jahresablauf*.cs.
// =====================================================================

[ApiController]
[Route("api/jahresablauf")]
[Authorize(Roles = Rollen.Admin)]
public class JahresablaufController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Schuleinstellungen _einst;
    private readonly IHostEnvironment _env;
    private readonly ILogger<JahresablaufController> _log;

    public JahresablaufController(AppDbContext db, Schuleinstellungen einst, IHostEnvironment env,
        ILogger<JahresablaufController> log)
    {
        _db = db;
        _einst = einst;
        _env = env;
        _log = log;
    }

    private string Ich => User.Identity?.Name ?? "";

    private async Task<JaUebersichtDto> StandAsync()
    {
        var werte = await _einst.LesenAsync(_db);
        return await Jahresablauf.UebersichtAsync(_db, werte, Betrieb.Produktiv(_env), Ich);
    }

    [HttpGet]
    public async Task<IActionResult> Uebersicht() => Ok(await StandAsync());

    [HttpPost("abschliessen")]
    public async Task<IActionResult> Abschliessen([FromBody] JaAbschliessenDto dto)
        => await AntwortAsync(await Jahresablauf.AbschliessenAsync(_db, _einst, dto?.Schuljahr, dto?.Bestaetigt == true, Ich, _log));

    [HttpPost("oeffnen")]
    public async Task<IActionResult> Oeffnen([FromBody] JaOeffnenDto dto)
        => await AntwortAsync(await Jahresablauf.OeffnenAsync(_db, dto?.Schuljahr, dto?.Begruendung, Ich, _log));

    [HttpPost("neues-schuljahr")]
    public async Task<IActionResult> NeuesSchuljahr([FromBody] JaVorbereitenDto dto)
        => await AntwortAsync(await Jahresablauf.VorbereitenAsync(_db, _einst, dto?.Schuljahr, dto?.Bestaetigt == true, Ich, _log));

    private async Task<IActionResult> AntwortAsync(Jahresablauf.AktionErgebnis e)
    {
        if (e.Rueckfrage != null) return Conflict(e.Rueckfrage);
        if (e.Status != 200) return StatusCode(e.Status, new { fehler = e.Fehler });
        return Ok(new JaAktionErgebnisDto(e.Meldung ?? "Erledigt.", await StandAsync()));
    }
}
