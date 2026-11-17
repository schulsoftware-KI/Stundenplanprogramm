using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

// =====================================================================
//  Phase 5.2b-2: SchILD-Import (Desktop → API, Header X-Api-Key wie beim Grundplan).
//
//    GET  api/schild/status          – Betriebsart, Kalender, Grundpläne, Schülerzahlen, letzter
//                                      Import, Vorschlag für „Änderungen gelten ab“ (79).
//    POST api/schild/import          – Probelauf (Vorschau mit Prüfsumme) bzw. Import (78).
//    POST api/schild/seed-entfernen  – nur Entwicklung: Seed-Schüler samt Klassenbuchdaten entfernen
//                                      (Punkt A), danach Vorschau neu berechnen.
//
//  Abgleich und Schreiben: Services/SchildImportService.cs. Das Log nennt nur Zahlen.
// =====================================================================

[ApiController]
[Route("api/schild")]
public class SchildController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ILogger<SchildController> _log;

    public SchildController(AppDbContext db, IConfiguration cfg, IHostEnvironment env, ILogger<SchildController> log)
    {
        _db = db;
        _cfg = cfg;
        _env = env;
        _log = log;
    }

    private bool Produktiv => Betrieb.Produktiv(_env);

    /// <summary>Was der Desktop vor der Vorschau wissen muss (ohne Personendaten).</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string? schuljahr, [FromQuery] int? abschnitt)
    {
        if (!ApiSchluessel.Gueltig(Request, _cfg))
            return Unauthorized(ApiSchluessel.Fehlertext);

        string sj = string.IsNullOrWhiteSpace(schuljahr) ? (_cfg["DefaultSchuljahr"] ?? "2026/27") : schuljahr.Trim();
        int ab = abschnitt is >= 1 and <= 4 ? abschnitt.Value : 1;
        bool pseudo = !Produktiv;
        var heute = KlassenbuchService.Heute();

        var kal = await _db.Kalender.AsNoTracking().FirstOrDefaultAsync(k => k.Schuljahr == sj);
        var schuljahre = await _db.Kalender.AsNoTracking().Select(k => k.Schuljahr).ToListAsync();
        var plaene = await _db.GrundplanVersionen.AsNoTracking()
            .Where(v => v.Schuljahr == sj)
            .OrderBy(v => v.GueltigAb).ThenBy(v => v.Id)
            .Select(v => new { bezeichnung = v.Bezeichnung, gueltigAb = v.GueltigAb })
            .ToListAsync();
        int echt = await _db.Schueler.CountAsync(s => !s.IstPseudo);
        int seed = await _db.Schueler.CountAsync(s => s.IstPseudo && (s.ExtId == null || s.ExtId == ""));
        int pseudoImportiert = await _db.Schueler.CountAsync(s => s.IstPseudo && s.ExtId != null && s.ExtId != "");
        int importiert = pseudo ? pseudoImportiert : await _db.Schueler.CountAsync(s => !s.IstPseudo && s.ExtId != null && s.ExtId != "");
        var protokolle = await _db.SchildImportProtokolle.AsNoTracking()
            .Where(p => p.Schuljahr == sj && p.IstPseudo == pseudo)
            .ToListAsync();
        var letzter = protokolle.OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id).FirstOrDefault();
        var spaetester = protokolle.OrderByDescending(p => p.GiltAb).FirstOrDefault();

        DateOnly? halbjahrAb = DateOnly.TryParseExact(_cfg["Klassenbuch:HalbjahrAb"], "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var hj) ? hj : null;

        // Entscheidung 79: beim ersten Import eines Abschnitts dessen Beginn, sonst heute – nie vor dem letzten Import.
        DateOnly? vorschlag = null;
        if (kal != null)
        {
            bool erster = importiert == 0 || !protokolle.Any(p => p.Abschnitt == ab);
            DateOnly v = erster
                ? (ab >= 2 && halbjahrAb is DateOnly h && h >= kal.Start && h <= kal.Ende ? h : kal.Start)
                : heute < kal.Start ? kal.Start : heute > kal.Ende ? kal.Ende : heute;
            if (importiert > 0 && spaetester != null && v < spaetester.GiltAb) v = spaetester.GiltAb;
            vorschlag = v;
        }

        return Ok(new
        {
            betrieb = Produktiv ? "Echtbetrieb" : "Entwicklung",
            pseudo,
            nimmt = pseudo ? "nur Pseudodaten" : "nur echte Daten",
            schuljahr = sj,
            abschnitt = ab,
            schuljahre,
            kalender = kal == null ? null : (object)new { start = kal.Start, ende = kal.Ende },
            halbjahrAb,
            grundplaene = plaene,
            schueler = new { echt, seed, pseudoImportiert, importiert },
            seedEntfernenMoeglich = !Produktiv && seed > 0,
            letzterImport = letzter == null ? null : (object)new
            {
                id = letzter.Id, zeit = letzter.Zeit, akteur = letzter.Akteur, abschnitt = letzter.Abschnitt,
                giltAb = letzter.GiltAb, neu = letzter.Neu, geaendert = letzter.Geaendert,
                klassenwechsel = letzter.Klassenwechsel, kurswechsel = letzter.Kurswechsel, abgaenge = letzter.Abgaenge,
                unveraendert = letzter.Unveraendert, uebersprungen = letzter.Uebersprungen
            },
            fruehestensAb = importiert > 0 ? spaetester?.GiltAb : null,
            vorschlagGiltAb = vorschlag
        });
    }

    /// <summary>
    /// Probelauf (probelauf = true, Standard): Vorschau mit Kategorien, Sperren, Hinweisen und Prüfsumme – schreibt nichts.
    /// Import (probelauf = false): dieselbe Nutzlast + vorschau (Prüfsumme) + abgaenge (bestätigte Ids). Sperre → 409,
    /// veraltete Vorschau → 409 mit neuer Vorschau.
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] SchildImportDto dto)
    {
        if (!ApiSchluessel.Gueltig(Request, _cfg))
            return Unauthorized(ApiSchluessel.Fehlertext);

        SchildImportService.Normalisieren(dto);
        var fehler = SchildImportService.Pruefen(dto);
        if (fehler.Count > 0)
            return BadRequest(new { fehler = "Die Nutzlast ist unvollständig oder fehlerhaft.", details = fehler });

        var heute = KlassenbuchService.Heute();
        if (dto.Probelauf)
        {
            var vorschau = await SchildImportService.BerechnenAsync(_db, dto, Produktiv, heute);
            _log.LogInformation("SchILD-Import: Vorschau {Schuljahr}, Abschnitt {Abschnitt}, gilt ab {GiltAb} – {Anzahl} Schüler, " +
                                "gesperrt {Gesperrt}", dto.Schuljahr, dto.Abschnitt, dto.GiltAb, vorschau.ImExport, vorschau.Gesperrt);
            return Ok(vorschau.Antwort(probelauf: true));
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        var lauf = await SchildImportService.BerechnenAsync(_db, dto, Produktiv, heute);
        if (lauf.Gesperrt)
            return Conflict(lauf.Antwort(probelauf: true));
        if (!string.Equals(dto.Vorschau, lauf.VorschauPruefsumme, StringComparison.Ordinal))
            return Conflict(new
            {
                fehler = "Die Vorschau ist nicht mehr aktuell (Datenbank oder Nutzlast haben sich geändert). Bitte die neue " +
                         "Vorschau prüfen und dann importieren.",
                vorschauVeraltet = true,
                vorschau = lauf.Antwort(probelauf: true)
            });

        var bestaetigt = (dto.Abgaenge ?? new List<int>()).Distinct().ToList();
        var fremd = bestaetigt.Where(id => !lauf.IstAbgangKandidat(id)).ToList();
        if (fremd.Count > 0)
            return BadRequest(new
            {
                fehler = $"{fremd.Count} bestätigte Abgänge passen nicht zur Vorschau (Id {string.Join(", ", fremd.Take(10))})."
            });

        var protokoll = lauf.Schreiben(_db, bestaetigt, Akteur(dto.Akteur));
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        _log.LogInformation("SchILD-Import ausgeführt (Protokoll {Id}): {Schuljahr}, Abschnitt {Abschnitt}, gilt ab {GiltAb}, {Art} – " +
                            "neu {Neu}, geändert {Geaendert}, Klassenwechsel {Klassen}, Kurswechsel {Kurse}, Abgänge {Abgaenge}, " +
                            "unverändert {Unveraendert}, übersprungen {Uebersprungen}",
            protokoll.Id, protokoll.Schuljahr, protokoll.Abschnitt, protokoll.GiltAb, protokoll.IstPseudo ? "Pseudodaten" : "echte Daten",
            protokoll.Neu, protokoll.Geaendert, protokoll.Klassenwechsel, protokoll.Kurswechsel, protokoll.Abgaenge,
            protokoll.Unveraendert, protokoll.Uebersprungen);
        return Ok(lauf.ImportAntwort(protokoll));
    }

    /// <summary>
    /// Nur Entwicklung (Punkt A): entfernt alle Pseudo-Schüler – die aus seed/pseudodaten und ggf. früher importierte –
    /// samt ihren Klassenbuchdaten, wie ein neuer Seed. Testkonten s05A/sQ1 werden gelöst (danach seed-konten).
    /// </summary>
    [NurEntwicklung]
    [HttpPost("seed-entfernen")]
    public async Task<IActionResult> SeedEntfernen()
    {
        if (!ApiSchluessel.Gueltig(Request, _cfg))
            return Unauthorized(ApiSchluessel.Fehlertext);
        if (await _db.Schueler.AnyAsync(s => !s.IstPseudo))
            return Conflict(new { fehler = "Es sind echte Schülerdaten vorhanden – hier wird nichts entfernt." });

        int seed = await _db.Schueler.CountAsync(s => s.IstPseudo && (s.ExtId == null || s.ExtId == ""));
        int importiert = await _db.Schueler.CountAsync(s => s.IstPseudo && s.ExtId != null && s.ExtId != "");
        var konten = PseudoDatenGenerator.SchuelerKontenLoesen(_db);
        PseudoDatenGenerator.LoescheVorhandene(_db);
        _log.LogInformation("SchILD: Pseudo-Schüler entfernt – {Seed} aus dem Seed, {Importiert} importiert", seed, importiert);

        return Ok(new
        {
            seedSchueler = seed,
            importierteSchueler = importiert,
            schuelerKonten = konten,
            hinweis = "Entfernt samt Klassenbuchdaten, Pseudo-Lerngruppen und Gruppenlisten. Jetzt die Vorschau neu berechnen; " +
                      "nach dem Import die Testkonten mit POST /api/auth/seed-konten neu verknüpfen."
        });
    }

    /// <summary>„Desktop (Windows-Benutzer)“ fürs Importprotokoll – ohne Steuerzeichen, gekürzt.</summary>
    private static string Akteur(string? benutzer)
    {
        var b = new string((benutzer ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (b.Length > 60) b = b[..60];
        return b.Length == 0 ? "Desktop" : $"Desktop ({b})";
    }
}
