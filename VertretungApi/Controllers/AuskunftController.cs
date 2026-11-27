using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

// =====================================================================
//  P6b – 5.4 Auskunft nach Art. 15 DSGVO (Entscheidungen 234, 235), nur Admin:
//
//    GET api/auskunft/schueler/{id}   – PDF über alle gespeicherten Schuljahre (Klassenbuch → Schülerdetail)
//    GET api/auskunft/konto/{id}      – Personalkonto: kompakte Auskunft; Schülerkonto: die Schüler-Auskunft
//                                       (Konten verwalten → Konto)
//
//  Teurer Abruf (Anfragebegrenzung, 230). Jeder Abruf steht im Download-Protokoll (Art „Auskunft“, Was ohne Namen:
//  „Schüler-Id n (Klasse)“ bzw. „Konto-Id n (Rolle)“; Zeitraum = erstes gespeichertes Datum bis heute). Wer die Auskunft
//  bekommt (Identität, Eltern Minderjähriger), regelt die Schule (Hilfe „Auskunft nach Art. 15“).
// =====================================================================

[ApiController]
[Route("api/auskunft")]
[Authorize(Roles = Rollen.Admin)]
public class AuskunftController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Schuleinstellungen _einst;
    private readonly ILogger<AuskunftController> _log;

    public AuskunftController(AppDbContext db, Schuleinstellungen einst, ILogger<AuskunftController> log)
    {
        _db = db;
        _einst = einst;
        _log = log;
    }

    private string Ich => User.Identity?.Name ?? "";

    [TeurerAbruf]
    [HttpGet("schueler/{id:int}")]
    public async Task<IActionResult> Schueler(int id) => await SchuelerPdfAsync(id);

    [TeurerAbruf]
    [HttpGet("konto/{id:int}")]
    public async Task<IActionResult> Konto(int id)
    {
        var k = await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (k == null) return NotFound(new { fehler = "Dieses Konto gibt es nicht (mehr)." });
        if (k.Rolle == BenutzerRolle.Schueler)
        {
            if (k.SchuelerId is not int sid || !await _db.Schueler.AnyAsync(s => s.Id == sid))
                return NotFound(new { fehler = "Diesem Schülerkonto ist kein Schüler zugeordnet – eine Auskunft über das Konto " +
                                               "allein steht im Kontenprotokoll." });
            return await SchuelerPdfAsync(sid);
        }

        AuskunftDokument dok;
        try
        {
            dok = await Auskunft.KontoAsync(_db, _einst, k, Ich, await _einst.LeseprotokollAnAsync(_db));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auskunft (Konto-Id {Id}) konnte nicht zusammengestellt werden.", id);
            return StatusCode(500, new { fehler = "Die Auskunft konnte nicht erstellt werden. Details stehen im Protokoll der API." });
        }
        return await PdfAsync(dok, $"Konto-Id {k.Id} ({KontenService.RollenText(k.Rolle)})", ErstesDatumKonto(k));
    }

    private async Task<IActionResult> SchuelerPdfAsync(int id)
    {
        AuskunftDokument? dok;
        try
        {
            dok = await Auskunft.SchuelerAsync(_db, _einst, id, Ich, await _einst.LeseprotokollAnAsync(_db));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auskunft (Schüler-Id {Id}) konnte nicht zusammengestellt werden.", id);
            return StatusCode(500, new { fehler = "Die Auskunft konnte nicht erstellt werden. Details stehen im Protokoll der API." });
        }
        if (dok == null) return NotFound(new { fehler = "Schüler nicht gefunden." });

        var w = await _einst.LesenAsync(_db);
        string was = await Auskunft.SchuelerWasAsync(_db, id, w.Schuljahr);
        var von = await _db.Anwesenheiten.AsNoTracking().Where(a => a.SchuelerId == id)
            .Select(a => (DateOnly?)a.Lektion!.Datum).MinAsync();
        return await PdfAsync(dok, was, von);
    }

    /// <summary>Erstes Datum mit Daten des Kontos (angelegt am); unbekannt = null (dann heute).</summary>
    private static DateOnly? ErstesDatumKonto(Benutzer k)
        => k.AngelegtAm is DateTime a ? Ortszeit.TagAusUtc(a) : null;

    private async Task<IActionResult> PdfAsync(AuskunftDokument dok, string was, DateOnly? von)
    {
        byte[] pdf;
        try
        {
            pdf = AuskunftPdf.Erzeuge(dok);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auskunft als PDF konnte nicht erzeugt werden ({Was}).", was);
            return StatusCode(500, new { fehler = "Das PDF konnte nicht erzeugt werden. Details stehen im Protokoll der API." });
        }

        var heute = KlassenbuchService.Heute();
        var ab = von is DateOnly v && v <= heute ? v : heute;
        _db.DownloadProtokolle.Add(new DownloadProtokoll
        {
            Zeit = DateTime.UtcNow,
            Akteur = Ich,
            Art = Auskunft.DownloadArt,
            Was = was,
            Von = ab,
            Bis = heute,
            MitSchuelerEintraegen = false,
        });
        await _db.SaveChangesAsync();
        _log.LogInformation("Auskunft nach Art. 15 erstellt von {Akteur}: {Was}", Ich, was);
        return File(pdf, "application/pdf", dok.Dateiname);
    }
}
