using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

// =====================================================================
//  P4b (5.3a, Entscheidungen 210–213): Rückweg der Fehlstunden nach SchILD.
//
//    App (nur Admin, Anmeldung/JWT):
//      GET    api/schild/rueckweg?von=&bis=   – Zeiträume, Zahlen (übertragen / nicht übertragen), nicht erfasste
//                                              Stunden, frühere Bereitstellungen, gültiger Code (ohne den Code)
//      POST   api/schild/rueckweg/code        – Abholcode erzeugen (8 Zeichen, 15 Minuten, ein Abruf)
//      DELETE api/schild/rueckweg/code        – gültigen Code ungültig machen
//    Desktop (Header X-Api-Key wie beim Import):
//      POST   api/schild/rueckweg/abruf       – mit Abholcode: Fehlstunden nur mit Prüfsumme (ExtId), ohne Namen;
//                                              Eintrag im Download-Protokoll (Art „SchildRueckweg“)
//      POST   api/schild/rueckweg/datei       – Meldung „Datei geschrieben“ (vollständig bzw. Probedatei), ohne Namen;
//                                              Eintrag im Download-Protokoll (Art „SchildRueckwegDatei“ bzw. „…Probe“)
//
//  Pseudodaten nur in der Entwicklung, echte Daten nur im Echtbetrieb (83). Log und Protokoll nennen nur Zahlen.
// =====================================================================

[ApiController]
[Route("api/schild/rueckweg")]
public class RueckwegController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ILogger<RueckwegController> _log;
    private readonly Schuleinstellungen _einst;
    private readonly RueckwegCodes _codes;

    public RueckwegController(AppDbContext db, IConfiguration cfg, IHostEnvironment env, ILogger<RueckwegController> log,
        Schuleinstellungen einst, RueckwegCodes codes)
    {
        _db = db;
        _cfg = cfg;
        _env = env;
        _log = log;
        _einst = einst;
        _codes = codes;
    }

    private bool Pseudo => !Betrieb.Produktiv(_env);
    private string Akteur => User.Identity?.Name ?? "";

    private static ObjectResult Fehler(int code, string text) => new(new { fehler = text }) { StatusCode = code };

    private async Task<(SchulWerte W, KalenderDb? Kal, DateOnly? H)> GrundlagenAsync()
    {
        var w = await _einst.LesenAsync(_db);
        var kal = await _db.Kalender.AsNoTracking().FirstOrDefaultAsync(k => k.Schuljahr == w.Schuljahr);
        DateOnly? h = kal != null ? FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, w.HalbjahrAb) : null;
        return (w, kal, h);
    }

    // -----------------------------------------------------------------
    //  App
    // -----------------------------------------------------------------

    [Authorize(Roles = Rollen.Admin)]
    [HttpGet]
    public async Task<IActionResult> Vorschau([FromQuery] DateOnly? von, [FromQuery] DateOnly? bis)
    {
        var (w, kal, h) = await GrundlagenAsync();
        var heute = KlassenbuchService.Heute();
        if (kal == null)
            return Fehler(409, $"Für das Schuljahr {w.Schuljahr} gibt es in der App noch keinen Kalender (Desktop: Wocheneditor → " +
                               "„Grundplan veröffentlichen…“).");
        var (liste, standard) = SchildRueckweg.Zeitraeume(kal, h, heute);
        var z0 = liste.First(z => z.Id == standard);
        var geprueft = SchildRueckweg.Pruefen(kal, von ?? z0.Von, bis ?? z0.Bis, heute, out var fehler);
        if (geprueft == null) return Fehler(400, fehler ?? "Ungültiger Zeitraum.");
        var (v, b) = geprueft.Value;

        var (_, zahlen) = await SchildRueckweg.DatenAsync(_db, v, b, Pseudo);
        int ne = await SchildRueckweg.NichtErfasstAsync(_db, w, v, b, Akteur);
        var frueher = await SchildRueckweg.FruehereAsync(_db, v, b);
        var c = _codes.Aktuell();
        return Ok(new RwVorschauDto(w.Schuljahr, heute, Testdatum.Wert != null, kal.Start, kal.Ende, h, liste, standard, v, b,
            h is DateOnly hj && v < hj && b >= hj, zahlen, ne, frueher,
            c == null ? null : new RwCodeStandDto(c.GueltigBisUtc, c.Von, c.Bis, c.Akteur), Pseudo));
    }

    [Authorize(Roles = Rollen.Admin)]
    [HttpPost("code")]
    public async Task<IActionResult> CodeErzeugen([FromBody] RwCodeAnfrageDto dto)
    {
        var (w, kal, _) = await GrundlagenAsync();
        if (kal == null) return Fehler(409, $"Für das Schuljahr {w.Schuljahr} gibt es in der App noch keinen Kalender.");
        var geprueft = SchildRueckweg.Pruefen(kal, dto.Von, dto.Bis, KlassenbuchService.Heute(), out var fehler);
        if (geprueft == null) return Fehler(400, fehler ?? "Ungültiger Zeitraum.");
        var (v, b) = geprueft.Value;
        var (code, a) = _codes.Erzeugen(w.Schuljahr, v, b, Pseudo, Akteur);
        _log.LogInformation("Rückweg nach SchILD: Abholcode erzeugt für {Von} – {Bis} ({Art}).", v, b,
            Pseudo ? "Pseudodaten" : "echte Daten");
        return Ok(new RwCodeDto(RueckwegCodes.Anzeige(code), a.GueltigBisUtc, v, b, (int)RueckwegCodes.Gueltigkeit.TotalMinutes));
    }

    [Authorize(Roles = Rollen.Admin)]
    [HttpDelete("code")]
    public IActionResult CodeVerwerfen() => Ok(new { verworfen = _codes.Verwerfen() });

    // -----------------------------------------------------------------
    //  Desktop
    // -----------------------------------------------------------------

    [DesktopSchnittstelle]   // P6a (229)
    [TeurerAbruf]            // P6a (230)
    [HttpPost("abruf")]
    public async Task<IActionResult> Abruf([FromBody] RwAbrufAnfrageDto dto)
    {
        if (!ApiSchluessel.Gueltig(Request, _cfg)) return Unauthorized(ApiSchluessel.Fehlertext);

        // Erst prüfen, was ohne Code geht – dann bleibt der Code bei einem Fehler gültig.
        if (dto.Pseudo != Pseudo)
            return Fehler(409, Pseudo
                ? "Die API läuft in der Entwicklung und gibt nur Pseudodaten heraus (Entscheidung 83) – für echte Daten den " +
                  "Server im Echtbetrieb verwenden."
                : "Die API läuft im Echtbetrieb und gibt keine Pseudodaten heraus (Entscheidung 83).");
        string? kennung = await _db.SchildImportProtokolle.AsNoTracking()
            .Where(p => p.IstPseudo == Pseudo && p.SchluesselKennung != null)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Select(p => p.SchluesselKennung).FirstOrDefaultAsync();
        if (kennung == null)
            return Fehler(409, "Es gibt noch keinen SchILD-Import dieser Art – ohne importierte Schüler kann der Desktop keine " +
                               "Fehlstunde zuordnen.");
        if (!string.IsNullOrWhiteSpace(dto.Kennung) && !string.Equals(dto.Kennung.Trim(), kennung, StringComparison.OrdinalIgnoreCase))
            return Fehler(409, $"Der Schlüssel des Desktops (Kennung {dto.Kennung.Trim()}) ist ein anderer als beim letzten Import " +
                               $"(Kennung {kennung}) – damit ließe sich keine Fehlstunde zuordnen. Abhilfe: im Reiter „Import“ " +
                               "„Schlüssel einlesen…“. Der Abholcode bleibt gültig.");

        var a = _codes.Einloesen(dto.Code, out var grund);
        if (a == null)
            return Fehler(403, grund switch
            {
                "abgelaufen" => "Der Abholcode ist abgelaufen (15 Minuten) – bitte in der App einen neuen erzeugen.",
                "verfallen" => $"Der Abholcode wurde {RueckwegCodes.MaxFehlversuche}-mal falsch eingegeben und ist verfallen – " +
                               "bitte in der App einen neuen erzeugen.",
                "falsch" => "Der Abholcode stimmt nicht.",
                _ => "Es gibt keinen gültigen Abholcode – bitte in der App (Jahresablauf → „Rückweg nach SchILD“) einen erzeugen."
            });
        if (a.Pseudo != Pseudo) return Fehler(409, "Der Code wurde in einer anderen Betriebsart erzeugt.");

        var w = await _einst.LesenAsync(_db);
        var kal = await _db.Kalender.AsNoTracking().FirstOrDefaultAsync(k => k.Schuljahr == a.Schuljahr);
        DateOnly? h = kal != null ? FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, w.HalbjahrAb) : null;
        var (zeilen, zahlen) = await SchildRueckweg.DatenAsync(_db, a.Von, a.Bis, Pseudo);
        int ne = await SchildRueckweg.NichtErfasstAsync(_db, w, a.Von, a.Bis, a.Akteur);
        string desktop = SchildRueckweg.Desktop(dto.Desktop);

        _db.DownloadProtokolle.Add(new DownloadProtokoll
        {
            Akteur = a.Akteur,
            Art = SchildRueckweg.ArtAbruf,
            Was = SchildRueckweg.AbrufText(zahlen, desktop),
            Von = a.Von,
            Bis = a.Bis,
            MitSchuelerEintraegen = false
        });
        await _db.SaveChangesAsync();
        string merk = _codes.AbrufMerken(new RueckwegCodes.Abruf(a.Schuljahr, a.Von, a.Bis, Pseudo, a.Akteur, desktop,
            DateTime.UtcNow + RueckwegCodes.MeldenBis));
        _log.LogInformation("Rückweg nach SchILD: abgeholt {Von} – {Bis}, {Zeilen} Fehlstunden ({Art}).", a.Von, a.Bis,
            zeilen.Count, Pseudo ? "Pseudodaten" : "echte Daten");
        return Ok(new RwAbrufDto(a.Schuljahr, a.Von, a.Bis, Pseudo, kal?.Start, kal?.Ende, h, a.Akteur, merk, zahlen, ne, zeilen));
    }

    [DesktopSchnittstelle]   // P6a (229)
    [HttpPost("datei")]
    public async Task<IActionResult> Datei([FromBody] RwDateiMeldungDto dto)
    {
        if (!ApiSchluessel.Gueltig(Request, _cfg)) return Unauthorized(ApiSchluessel.Fehlertext);
        if (dto.Bis < dto.Von || dto.Zeilen < 0 || dto.Fehlstunden < 0 || dto.Abschnitt is < 1 or > 4)
            return Fehler(400, "Die Meldung ist unvollständig.");
        var a = _codes.Melden(dto.Merkzeichen);
        if (a == null)
            return Fehler(403, "Die Meldung passt zu keinem Abruf der letzten 2 Stunden – das Download-Protokoll nennt nur den " +
                               "Abruf.");
        if (dto.Von < a.Von || dto.Bis > a.Bis)
            return Fehler(400, "Der Zeitraum der Datei liegt außerhalb des abgeholten Zeitraums.");
        var m = dto with { Klassen = SchildRueckweg.KlassenText(dto.Klassen) };
        _db.DownloadProtokolle.Add(new DownloadProtokoll
        {
            Akteur = a.Akteur,
            Art = m.Probe ? SchildRueckweg.ArtProbe : SchildRueckweg.ArtDatei,
            Was = SchildRueckweg.DateiText(m, a.Desktop),
            Von = m.Von,
            Bis = m.Bis,
            MitSchuelerEintraegen = false
        });
        await _db.SaveChangesAsync();
        _log.LogInformation("Rückweg nach SchILD: {Art} gemeldet {Von} – {Bis}, {Zeilen} Zeilen.", m.Probe ? "Probedatei" : "Datei",
            m.Von, m.Bis, m.Zeilen);
        return Ok(new { gemeldet = true });
    }
}
