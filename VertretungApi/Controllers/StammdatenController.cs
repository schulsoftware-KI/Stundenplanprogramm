using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

// =====================================================================
//  Stammdaten (Phase 1): Pseudodaten erzeugen + Lerngruppen/Schüler lesen.
//  Seed ist per X-Api-Key geschützt und nur in der Entwicklung erreichbar (Phase 5.1);
//  die Lese-Endpunkte brauchen eine Anmeldung (Personal).
// =====================================================================

[ApiController]
[Route("api")]
public class StammdatenController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public StammdatenController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    /// <summary>
    /// Erzeugt (bzw. ersetzt) die Pseudo-Stammdaten. Schützt per X-Api-Key.
    /// Liegt für das Schuljahr ein Grundplan vor, entstehen Klassen/Kurse genau aus dessen
    /// Lerngruppen (neueste Version); sonst frei erfundene Daten wie in Phase 1B.
    /// quelle=synthetisch erzwingt den alten Weg.
    /// </summary>
    // Phase 5.1: nur in der Entwicklung (im Echtbetrieb 404) und nur, solange keine echten
    // Schüler in der Datenbank stehen.
    [NurEntwicklung]
    [HttpPost("seed/pseudodaten")]
    public IActionResult Seed([FromQuery] string? schuljahr, [FromQuery] int? seed, [FromQuery] string? quelle)
    {
        if (!ApiSchluessel.Gueltig(Request, _cfg))      // Phase 5.1: Vergleich in konstanter Zeit
            return Unauthorized(ApiSchluessel.Fehlertext);
        if (_db.Schueler.Any(x => !x.IstPseudo))
            return Conflict(new { fehler = "Es sind echte Schülerdaten vorhanden – Pseudodaten werden nicht erzeugt." });

        string sj = string.IsNullOrWhiteSpace(schuljahr)
            ? (_cfg["DefaultSchuljahr"] ?? "2026/27")
            : schuljahr;

        // Schritt 5: Schülerkonten der alten Pseudo-Schüler lösen bzw. entfernen, bevor die
        // Pseudodaten ersetzt werden (sonst zeigte ein Konto später einen anderen Schüler).
        var konten = SchuelerKontenLoesen();

        bool synthetischErzwungen = string.Equals(quelle, "synthetisch", StringComparison.OrdinalIgnoreCase);
        var version = synthetischErzwungen ? null : _db.GrundplanVersionen
            .Where(v => v.Schuljahr == sj)
            .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
            .FirstOrDefault();

        if (version != null)
        {
            var slots = _db.GrundplanSlots.Where(s => s.VersionId == version.Id).ToList();
            var eg = PseudoDatenGenerator.GenerierenAusGrundplan(_db, version, slots, seed ?? 12345);
            return Ok(new
            {
                schuljahr = sj,
                quelle = "grundplan",
                grundplan = version.Bezeichnung,
                gueltigAb = version.GueltigAb,
                eg.Schueler, eg.Klassen, eg.Kurse, eg.Zuordnungen,
                schuelerKonten = konten
            });
        }

        var e = PseudoDatenGenerator.Generieren(_db, sj, seed ?? 12345);
        return Ok(new { schuljahr = sj, quelle = "synthetisch", e.Schueler, e.Klassen, e.Kurse, e.Zuordnungen,
            schuelerKonten = konten });
    }

    /// <summary>
    /// Vor dem Neu-Seeden: Schülerkonten, die auf Pseudo-Schüler (oder auf gar keinen Schüler)
    /// zeigen, werden gelöscht – außer den Testkonten s05A/sQ1, die nur gelöst und von
    /// /api/auth/seed-konten neu verknüpft werden. Konten echter Schüler bleiben unberührt.
    /// </summary>
    private object SchuelerKontenLoesen()
    {
        var testkonten = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "s05A", "sQ1" };
        var echteIds = _db.Schueler.Where(x => !x.IstPseudo).Select(x => x.Id).ToHashSet();
        int entfernt = 0, geloest = 0;
        foreach (var k in _db.Benutzer.Where(b => b.Rolle == BenutzerRolle.Schueler).ToList())
        {
            if (k.SchuelerId is int sid && echteIds.Contains(sid)) continue;
            if (testkonten.Contains(k.Benutzername))
            {
                if (k.SchuelerId == null) continue;
                k.SchuelerId = null;
                k.SicherheitsStempel = KontenService.NeuerStempel();
                geloest++;
            }
            else
            {
                KontenService.Protokoll(_db, "System", KontenService.Aktion.Geloescht, k, "Pseudodaten neu erzeugt");
                _db.Benutzer.Remove(k);
                entfernt++;
            }
        }
        _db.SaveChanges();
        return new
        {
            entfernt,
            geloest,
            hinweis = geloest > 0 ? "Testkonten s05A/sQ1 neu verknüpfen: POST /api/auth/seed-konten" : null
        };
    }

    /// <summary>Lerngruppen (Klassen + Kurse), optional gefiltert. Nur Personal.</summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("lerngruppen")]
    public async Task<IActionResult> Lerngruppen([FromQuery] string? typ, [FromQuery] string? stufe)
    {
        var q = _db.Lerngruppen.AsQueryable();
        if (!string.IsNullOrWhiteSpace(typ) && Enum.TryParse<LerngruppeTyp>(typ, true, out var t))
            q = q.Where(l => l.Typ == t);
        if (!string.IsNullOrWhiteSpace(stufe))
            q = q.Where(l => l.Stufe == stufe);

        var liste = await q
            .OrderBy(l => l.Stufe).ThenBy(l => l.Bezeichnung)
            .Select(l => new
            {
                l.Id,
                Typ = l.Typ.ToString(),
                l.Bezeichnung,
                l.Stufe,
                l.Fach,
                Kursart = l.Kursart == Kursart.Keine ? null : l.Kursart.ToString(),
                l.LehrerKuerzel,
                Anzahl = l.Mitglieder.Count
            })
            .ToListAsync();
        return Ok(liste);
    }

    /// <summary>Schülerliste einer Lerngruppe. Nur Personal.</summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("lerngruppen/{id:int}/schueler")]
    public async Task<IActionResult> LerngruppenSchueler(int id)
    {
        var lg = await _db.Lerngruppen.FindAsync(id);
        if (lg == null) return NotFound();

        var liste = await _db.Zugehoerigkeiten
            .Where(z => z.LerngruppeId == id)
            .Select(z => z.Schueler!)
            .OrderBy(s => s.Nachname).ThenBy(s => s.Vorname)
            .Select(s => new { s.Id, s.Nachname, s.Vorname, s.Stufe })
            .ToListAsync();

        return Ok(new { lg.Id, lg.Bezeichnung, Typ = lg.Typ.ToString(), Anzahl = liste.Count, Schueler = liste });
    }

    /// <summary>
    /// Schülerliste, optional nach Stufe/Namenssuche; begrenzt. Nur Personal.
    /// Suche (Phase 2, Schritt 4): mehrere Wörter möglich („mia mül"), jedes Wort muss im Vor-
    /// oder Nachnamen vorkommen; Groß-/Kleinschreibung egal (auch bei Umlauten am Wortanfang).
    /// </summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("schueler")]
    public async Task<IActionResult> SchuelerListe(
        [FromQuery] string? stufe, [FromQuery] string? q, [FromQuery] int limit = 100)
    {
        var query = _db.Schueler.AsQueryable();
        if (!string.IsNullOrWhiteSpace(stufe)) query = query.Where(s => s.Stufe == stufe);

        var woerter = (q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var wort in woerter.Take(3))
        {
            // SQLite kennt lower() nur für A–Z; „öz" findet „Özdemir" daher über die
            // zusätzliche Variante mit großem Anfangsbuchstaben.
            var klein = wort.ToLowerInvariant();
            var gross = char.ToUpperInvariant(wort[0]) + wort[1..];
            query = query.Where(s =>
                s.Nachname.ToLower().Contains(klein) || s.Vorname.ToLower().Contains(klein) ||
                s.Nachname.Contains(gross) || s.Vorname.Contains(gross));
        }

        var liste = await query
            .OrderBy(s => s.Nachname).ThenBy(s => s.Vorname).ThenBy(s => s.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(s => new
            {
                s.Id, s.Nachname, s.Vorname, s.Stufe,
                Klasse = s.Zugehoerigkeiten
                    .Where(z => z.Lerngruppe!.Typ == LerngruppeTyp.Klasse)
                    .Select(z => z.Lerngruppe!.Bezeichnung)
                    .FirstOrDefault()
            })
            .ToListAsync();
        return Ok(liste);
    }

    /// <summary>Ein Schüler mit seinen Lerngruppen (Klasse + Kurse). Nur Personal.</summary>
    [Authorize(Roles = "Admin,Lehrer,Klassenlehrer,Stufenkoordination")]
    [HttpGet("schueler/{id:int}")]
    public async Task<IActionResult> SchuelerEinzeln(int id)
    {
        var s = await _db.Schueler.FindAsync(id);
        if (s == null) return NotFound();

        var gruppen = await _db.Zugehoerigkeiten
            .Where(z => z.SchuelerId == id)
            .Select(z => z.Lerngruppe!)
            .OrderBy(l => l.Typ).ThenBy(l => l.Bezeichnung)
            .Select(l => new { l.Id, Typ = l.Typ.ToString(), l.Bezeichnung, l.Fach, l.LehrerKuerzel })
            .ToListAsync();

        return Ok(new { s.Id, s.Nachname, s.Vorname, s.Stufe, Lerngruppen = gruppen });
    }
}
