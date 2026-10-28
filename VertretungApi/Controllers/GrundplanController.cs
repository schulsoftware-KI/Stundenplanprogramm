using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Controllers;

/// <summary>
/// Grundplan-Endpunkte (Phase 2, Schritt 1):
///   POST api/grundplan/push      – Desktop lädt Grundplan-Version + Kalender hoch (X-Api-Key).
///   GET  api/grundplan/versionen – vorhandene Versionen (Kontrolle).
///   GET  api/grundplan/kalender  – Schuljahreskalender samt freien Zeiten.
///   GET  api/grundplan/slots     – Slots einer Version, filterbar (Kontrolle; Wochen-Endpunkte folgen in Schritt 2).
///
/// Versionierung: (Schuljahr, GueltigAb) ist eindeutig. Gleicher Schlüssel = Version wird ersetzt,
/// anderes GueltigAb = neue Version. Der Kalender wird je Schuljahr ersetzt.
/// </summary>
[ApiController]
[Route("api/grundplan")]
public class GrundplanController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public GrundplanController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    [HttpPost("push")]
    public async Task<IActionResult> Push([FromBody] GrundplanPushDto dto)
    {
        var erwartet = _cfg["Api:PushKey"];
        if (!string.IsNullOrEmpty(erwartet))
        {
            if (!Request.Headers.TryGetValue("X-Api-Key", out var key) || key != erwartet)
                return Unauthorized("Ungültiger oder fehlender X-Api-Key.");
        }

        if (string.IsNullOrWhiteSpace(dto.Schuljahr)) return BadRequest("Schuljahr fehlt.");
        if (dto.GueltigAb == default) return BadRequest("GueltigAb fehlt.");
        if (dto.Slots == null || dto.Slots.Count == 0) return BadRequest("Keine Slots übermittelt.");

        await using var tx = await _db.Database.BeginTransactionAsync();

        // --- Version: gleiche (Schuljahr, GueltigAb) ersetzen, sonst neu ---
        var vorhanden = await _db.GrundplanVersionen
            .FirstOrDefaultAsync(v => v.Schuljahr == dto.Schuljahr && v.GueltigAb == dto.GueltigAb);
        bool ersetzt = vorhanden != null;
        if (vorhanden != null)
        {
            _db.GrundplanVersionen.Remove(vorhanden);      // Slots fallen per Cascade mit
            await _db.SaveChangesAsync();
        }

        var version = new GrundplanVersion
        {
            Schuljahr = dto.Schuljahr,
            GueltigAb = dto.GueltigAb,
            Bezeichnung = dto.Bezeichnung ?? "",
            Erstellt = DateTimeOffset.UtcNow
        };
        foreach (var s in dto.Slots)
        {
            version.Slots.Add(new GrundplanSlot
            {
                WTag = s.WTag ?? "",
                WTagIndex = s.WTagIndex,
                Stunde = s.Stunde,
                UNr = s.UNr,
                Lehrer = s.Lehrer ?? "",
                Fach = s.Fach ?? "",
                Fachraum = s.Fachraum,
                Stammraum = s.Stammraum,
                LerngruppeTyp = string.IsNullOrWhiteSpace(s.LerngruppeTyp) ? "Klasse" : s.LerngruppeTyp,
                LerngruppeKey = s.LerngruppeKey ?? "",
                Klasse = s.Klasse ?? "",
                Schuelergruppe = s.Schuelergruppe,
                Art = string.IsNullOrWhiteSpace(s.Art) ? "Unterricht" : s.Art
            });
        }
        _db.GrundplanVersionen.Add(version);

        // --- Kalender: je Schuljahr ersetzen ---
        bool kalenderErsetzt = false;
        if (dto.Kalender != null)
        {
            var altKal = await _db.Kalender.FirstOrDefaultAsync(k => k.Schuljahr == dto.Schuljahr);
            if (altKal != null)
            {
                _db.Kalender.Remove(altKal);               // freie Zeiten per Cascade
                await _db.SaveChangesAsync();
                kalenderErsetzt = true;
            }

            var kal = new KalenderDb
            {
                Schuljahr = dto.Schuljahr,
                Start = dto.Kalender.Start,
                Ende = dto.Kalender.Ende,
                RasterTage = string.Join(",", dto.Kalender.RasterTage ?? new()),
                Aktualisiert = DateTimeOffset.UtcNow
            };
            foreach (var f in dto.Kalender.FreieZeiten ?? new())
            {
                kal.FreieZeiten.Add(new FreieZeitDb
                {
                    Typ = f.Typ ?? "",
                    Von = f.Von,
                    Bis = f.Bis,
                    Bezeichnung = f.Bezeichnung ?? "",
                    Klassen = f.Klassen ?? ""
                });
            }
            _db.Kalender.Add(kal);
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Ok(new
        {
            version.Id,
            version.Schuljahr,
            version.GueltigAb,
            version.Bezeichnung,
            Slots = version.Slots.Count,
            VersionErsetzt = ersetzt,
            KalenderUebertragen = dto.Kalender != null,
            KalenderErsetzt = kalenderErsetzt
        });
    }

    [HttpGet("versionen")]
    public async Task<IActionResult> Versionen([FromQuery] string? schuljahr)
    {
        var q = _db.GrundplanVersionen.AsQueryable();
        if (!string.IsNullOrWhiteSpace(schuljahr)) q = q.Where(v => v.Schuljahr == schuljahr);

        var liste = await q
            .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
            .Select(v => new { v.Id, v.Schuljahr, v.GueltigAb, v.Bezeichnung, Slots = v.Slots.Count })
            .ToListAsync();
        return Ok(liste);
    }

    [HttpGet("kalender")]
    public async Task<IActionResult> KalenderAbfrage([FromQuery] string? schuljahr)
    {
        var q = _db.Kalender.Include(k => k.FreieZeiten).AsQueryable();
        var kal = string.IsNullOrWhiteSpace(schuljahr)
            ? await q.OrderByDescending(k => k.Id).FirstOrDefaultAsync()
            : await q.FirstOrDefaultAsync(k => k.Schuljahr == schuljahr);
        if (kal == null) return NotFound();

        return Ok(new
        {
            kal.Schuljahr,
            kal.Start,
            kal.Ende,
            RasterTage = kal.RasterTage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            FreieZeiten = kal.FreieZeiten.OrderBy(f => f.Von).Select(f => new
            {
                f.Typ, f.Von, f.Bis, f.Bezeichnung,
                Klassen = f.Klassen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            })
        });
    }

    /// <summary>Kontrollabfrage: Slots einer Version (Standard: neueste), optional nach Lehrer/Lerngruppe/Tag gefiltert.</summary>
    [HttpGet("slots")]
    public async Task<IActionResult> Slots(
        [FromQuery] int? versionId, [FromQuery] string? lehrer,
        [FromQuery] string? gruppe, [FromQuery] int? wtag, [FromQuery] int limit = 500)
    {
        int vid = versionId ?? await _db.GrundplanVersionen
            .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
            .Select(v => v.Id).FirstOrDefaultAsync();
        if (vid == 0) return NotFound("Keine Grundplan-Version vorhanden.");

        var q = _db.GrundplanSlots.Where(s => s.VersionId == vid);
        if (!string.IsNullOrWhiteSpace(lehrer)) q = q.Where(s => s.Lehrer == lehrer);
        if (!string.IsNullOrWhiteSpace(gruppe)) q = q.Where(s => s.LerngruppeKey == gruppe || s.Klasse == gruppe);
        if (wtag.HasValue) q = q.Where(s => s.WTagIndex == wtag.Value);

        var liste = await q
            .OrderBy(s => s.WTagIndex).ThenBy(s => s.Stunde).ThenBy(s => s.UNr)
            .Take(Math.Clamp(limit, 1, 5000))
            .Select(s => new
            {
                s.WTag, s.Stunde, s.UNr, s.Lehrer, s.Fach, s.Fachraum, s.Stammraum,
                s.LerngruppeTyp, s.LerngruppeKey, s.Klasse, s.Art
            })
            .ToListAsync();
        return Ok(liste);
    }
}
