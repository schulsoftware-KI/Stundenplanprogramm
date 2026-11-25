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
//  P5b-2 (219, 240, 249–251): Zuständigkeiten ins neue Schuljahr (nur „Aktuelles Schuljahr“, nach dem Jahreswechsel-Import):
//    GET  api/jahresablauf/zustaendigkeiten – Vorschläge je Konto (laut SchILD, Folgeklasse/-stufe, entfällt, Hinweis)
//    POST api/jahresablauf/zustaendigkeiten – { schuljahr, schluessel[] }: die angehakten übernehmen (nur, was noch genauso
//                                            vorgeschlagen wird; Kontenprotokoll, Abmeldung auf allen Geräten)
//  P5c-1 (259–265, 271–273): Archiv-Export eines abgeschlossenen Schuljahres – im Hintergrund (Services/ArchivAuftraege.cs):
//    POST   api/jahresablauf/archiv            – { schuljahr, schuelerEintraege, kennwort, kennwortWiederholung }: Auftrag
//                                               starten (Kennwort nur im Arbeitsspeicher) → { auftrag }
//    GET    api/jahresablauf/archiv/auftrag    – Stand des Auftrags (gemeinsam mit dem Jahresarchiv aus 4.3) → { auftrag }
//    DELETE api/jahresablauf/archiv/auftrag    – eigenen Auftrag abbrechen bzw. die fertige Datei verwerfen
//    GET    api/jahresablauf/archiv/datei      – die fertige Zip-Datei EINMAL (danach gelöscht); merkt sich Zeitpunkt, Person,
//                                               Größe, SHA-256 und protokolliert (JahresablaufArchiv.cs)
//    POST   api/jahresablauf/archiv/bestaetigen – { schuljahr, pruefsumme, geoeffnet, ort, aufbewahrenBis }: „geprüft und
//                                               aufbewahrt“ (nur mit passender Prüfsumme)
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
    private readonly ArchivAuftraege _auftraege;

    public JahresablaufController(AppDbContext db, Schuleinstellungen einst, IHostEnvironment env,
        ILogger<JahresablaufController> log, ArchivAuftraege auftraege)
    {
        _db = db;
        _einst = einst;
        _env = env;
        _log = log;
        _auftraege = auftraege;
    }

    private string Ich => User.Identity?.Name ?? "";
    private int IchId => Rollen.BenutzerId(User) ?? 0;

    private async Task<JaUebersichtDto> StandAsync()
    {
        var werte = await _einst.LesenAsync(_db);
        return await Jahresablauf.UebersichtAsync(_db, werte, Betrieb.Produktiv(_env), Ich, _auftraege.Stand(IchId));
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

    [HttpGet("zustaendigkeiten")]
    public async Task<IActionResult> Zustaendigkeiten() => Ok(await Jahresablauf.ZustaendigkeitenAsync(_db, _einst));

    [HttpPost("zustaendigkeiten")]
    public async Task<IActionResult> ZustaendigkeitenUebernehmen([FromBody] JaZustaendigkeitenUebernehmenDto dto)
        => await AntwortAsync(await Jahresablauf.ZustaendigkeitenUebernehmenAsync(_db, _einst, dto?.Schuljahr, dto?.Schluessel,
            Ich, _log));

    // ---- P5c-1: Archiv-Export (im Hintergrund), Bestätigung ----

    [HttpPost("archiv")]
    public async Task<IActionResult> ArchivStarten([FromBody] JaArchivStartDto dto)
    {
        if (Kennwort.Pruefen(dto?.Kennwort) is string kwFehler) return BadRequest(new { fehler = kwFehler, feld = "kennwort" });
        if (!string.Equals(dto!.Kennwort, dto.KennwortWiederholung, StringComparison.Ordinal))
            return BadRequest(new { fehler = "Die beiden Kennwörter stimmen nicht überein.", feld = "kennwort2" });
        var (fehler, plan) = await Jahresablauf.ArchivPlanAsync(_db, _einst, dto.Schuljahr, dto.SchuelerEintraege, Ich);
        if (fehler != null) return StatusCode(fehler.Status, new { fehler = fehler.Fehler });
        if (_auftraege.Starten(plan!, Ich, IchId, dto.Kennwort!) is string belegt) return Conflict(new { fehler = belegt });
        _log.LogInformation("Jahresablauf: Archiv-Export {Schuljahr} gestartet von {Akteur} ({Mit}).", plan!.Schuljahr, Ich,
            plan.MitSchuelerEintraegen ? "mit Schülereinträgen" : "ohne Schülereinträge");
        return Ok(new JaArchivAuftragAntwortDto(_auftraege.Stand(IchId)));
    }

    [HttpGet("archiv/auftrag")]
    public IActionResult ArchivAuftrag() => Ok(new JaArchivAuftragAntwortDto(_auftraege.Stand(IchId)));

    [HttpDelete("archiv/auftrag")]
    public IActionResult ArchivVerwerfen()
    {
        bool verworfen = _auftraege.Verwerfen(IchId);
        if (verworfen) _log.LogInformation("Archiv: Auftrag von {Akteur} abgebrochen bzw. verworfen.", Ich);
        return Ok(new { verworfen });
    }

    /// <summary>Die fertige Datei – nur einmal; danach ist sie gelöscht (DeleteOnClose).</summary>
    [TeurerAbruf]            // P6a (230): großer Download
    [HttpGet("archiv/datei")]
    public async Task<IActionResult> ArchivDatei()
    {
        var (datei, stand, status, fehler) = _auftraege.Abholen(IchId);
        if (datei == null || stand == null) return StatusCode(status, new { fehler });
        try
        {
            await Jahresablauf.ArchivAbgeholtAsync(_db, stand, _log);
        }
        catch (Exception ex)
        {
            await datei.DisposeAsync();
            _log.LogError(ex, "Archiv: Der Download konnte nicht protokolliert werden – die Datei wurde verworfen.");
            return StatusCode(500, new { fehler = "Der Download konnte nicht protokolliert werden – bitte das Archiv neu erstellen. " +
                                                  "Details stehen im Protokoll der API." });
        }
        return File(datei, "application/zip", stand.Plan.Dateiname);
    }

    [HttpPost("archiv/bestaetigen")]
    public async Task<IActionResult> ArchivBestaetigen([FromBody] JaArchivBestaetigenDto dto)
        => await AntwortAsync(await Jahresablauf.ArchivBestaetigenAsync(_db, dto, Ich, _log));

    private async Task<IActionResult> AntwortAsync(Jahresablauf.AktionErgebnis e)
    {
        if (e.Rueckfrage != null) return Conflict(e.Rueckfrage);
        if (e.Status != 200) return StatusCode(e.Status, new { fehler = e.Fehler });
        return Ok(new JaAktionErgebnisDto(e.Meldung ?? "Erledigt.", await StandAsync()));
    }
}
