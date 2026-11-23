using System.Globalization;
using Microsoft.AspNetCore.Authorization;
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
//                                      Import, Vorschlag für „Änderungen gelten ab“ (79), seit 5.2c-1
//                                      die Kennung des Schlüssels beim letzten Import (189).
//    POST api/schild/import          – Probelauf (Vorschau mit Prüfsumme) bzw. Import (78).
//    POST api/schild/seed-entfernen  – nur Entwicklung: Seed-Schüler samt Klassenbuchdaten entfernen
//                                      (Punkt A), danach Vorschau neu berechnen.
//    GET  api/schild/protokolle      – seit 5.2c-2 (Entscheidung 196): Importprotokoll für die App, nur Admin mit
//                                      Anmeldung (JWT) – nicht über den X-Api-Key; ohne Namen.
//
//  P4a (5.3a, Entscheidung 206): Der Status nennt das „Heute“ der API (Testdatum), das wirksame „2. Halbjahr ab“
//  (Einstellung bzw. 1. Februar) und den passenden Abschnitt; die Vorschau warnt (ohne Sperre), wenn Abschnitt und
//  „Änderungen gelten ab“ nicht zum 2. Halbjahr passen bzw. zum 2. Halbjahr kein neuer Grundplan veröffentlicht ist.
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
    private readonly Schuleinstellungen _einst;

    public SchildController(AppDbContext db, IConfiguration cfg, IHostEnvironment env, ILogger<SchildController> log,
        Schuleinstellungen einst)
    {
        _db = db;
        _cfg = cfg;
        _env = env;
        _log = log;
        _einst = einst;
    }

    private bool Produktiv => Betrieb.Produktiv(_env);

    /// <summary>Was der Desktop vor der Vorschau wissen muss (ohne Personendaten).</summary>
    [DesktopSchnittstelle]   // P6a (229)
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string? schuljahr, [FromQuery] int? abschnitt)
    {
        if (!ApiSchluessel.Gueltig(Request, _cfg))
            return Unauthorized(ApiSchluessel.Fehlertext);

        // P3 (E-1): aktuelles Schuljahr und 2. Halbjahr aus den Schuleinstellungen (vorher appsettings).
        var werte = await _einst.LesenAsync(_db);
        string sj = string.IsNullOrWhiteSpace(schuljahr) ? werte.Schuljahr : schuljahr.Trim();
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
        // 5.2c-1 (189): Kennung des Schlüssels beim letzten Import dieser Art (über alle Schuljahre) – verrät ihn nicht.
        string? kennung = await _db.SchildImportProtokolle.AsNoTracking()
            .Where(p => p.IstPseudo == pseudo && p.SchluesselKennung != null)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Select(p => p.SchluesselKennung).FirstOrDefaultAsync();

        // P4a (206): wirksames „2. Halbjahr ab“ – die Einstellung bzw. der 1. Februar (vorher nur die Einstellung; mit
        // „Standard“ schlug der erste Import von Abschnitt 2 dann den Schuljahresbeginn vor).
        DateOnly? halbjahrAb = kal != null ? FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, werte.HalbjahrAb) : werte.HalbjahrAb;
        int abschnittVorschlag = halbjahrAb is DateOnly hv && heute >= hv ? 2 : 1;

        // Entscheidung 79: beim ersten Import eines Abschnitts dessen Beginn, sonst heute – nie vor dem letzten Import.
        DateOnly? vorschlag = null;
        if (kal != null)
        {
            bool erster = importiert == 0 || !protokolle.Any(p => p.Abschnitt == ab);
            DateOnly v = erster
                ? (ab >= 2 && halbjahrAb is DateOnly h ? h : kal.Start)
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
            // P4a (206, 221): „Heute“ der API (Testdatum) und der Abschnitt, der heute gilt (Vorwahl im Desktop)
            heute,
            testdatum = Testdatum.Wert != null,
            abschnittVorschlag,
            grundplaene = plaene,
            schueler = new { echt, seed, pseudoImportiert, importiert },
            seedEntfernenMoeglich = !Produktiv && seed > 0,
            letzterImport = letzter == null ? null : (object)new
            {
                id = letzter.Id, zeit = letzter.Zeit, akteur = letzter.Akteur, abschnitt = letzter.Abschnitt,
                giltAb = letzter.GiltAb, neu = letzter.Neu, geaendert = letzter.Geaendert,
                klassenwechsel = letzter.Klassenwechsel, kurswechsel = letzter.Kurswechsel, abgaenge = letzter.Abgaenge,
                unveraendert = letzter.Unveraendert, uebersprungen = letzter.Uebersprungen,
                kennung = letzter.SchluesselKennung, neuZuordnung = letzter.NeuZuordnung,
                zusammengefuehrt = letzter.Zusammengefuehrt, neuZugeordnet = letzter.NeuZugeordnet
            },
            kennungLetzterImport = kennung,
            fruehestensAb = importiert > 0 ? spaetester?.GiltAb : null,
            vorschlagGiltAb = vorschlag
        });
    }

    /// <summary>
    /// Probelauf (probelauf = true, Standard): Vorschau mit Kategorien, Sperren, Hinweisen und Prüfsumme – schreibt nichts.
    /// Import (probelauf = false): dieselbe Nutzlast + vorschau (Prüfsumme) + abgaenge (bestätigte Ids). Sperre → 409,
    /// veraltete Vorschau → 409 mit neuer Vorschau.
    /// </summary>
    [DesktopSchnittstelle]   // P6a (229)
    [TeurerAbruf]            // P6a (230)
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
        var halbjahr = (await _einst.LesenAsync(_db)).HalbjahrAb;   // P4a (206): Einstellung „2. Halbjahr ab“
        if (dto.Probelauf)
        {
            var vorschau = await SchildImportService.BerechnenAsync(_db, dto, Produktiv, heute, halbjahr);
            _log.LogInformation("SchILD-Import: Vorschau {Schuljahr}, Abschnitt {Abschnitt}, gilt ab {GiltAb} – {Anzahl} Schüler, " +
                                "Paare {Paare}, Neu-Zuordnung {Neuzuordnung}, gesperrt {Gesperrt}, Warnungen {Warnungen}", dto.Schuljahr,
                dto.Abschnitt, dto.GiltAb, vorschau.ImExport, vorschau.DieselbePerson + vorschau.ZuordnungPaare, dto.Neuzuordnung,
                vorschau.Gesperrt, vorschau.Warnungen.Count);
            return Ok(vorschau.Antwort(probelauf: true));
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        var lauf = await SchildImportService.BerechnenAsync(_db, dto, Produktiv, heute, halbjahr);
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
                            "unverändert {Unveraendert}, übersprungen {Uebersprungen}, zusammengeführt {Zusammengefuehrt}, " +
                            "Neu-Zuordnung {Neuzuordnung} ({NeuZugeordnet}), Schlüssel {Kennung}; Klassenleitung erstmals " +
                            "{KlNeu}, geändert {KlGeaendert}; Lehrkräfte-Namen neu {LkNeu}, geändert {LkGeaendert}, entfernt {LkWeg}",
            protokoll.Id, protokoll.Schuljahr, protokoll.Abschnitt, protokoll.GiltAb, protokoll.IstPseudo ? "Pseudodaten" : "echte Daten",
            protokoll.Neu, protokoll.Geaendert, protokoll.Klassenwechsel, protokoll.Kurswechsel, protokoll.Abgaenge,
            protokoll.Unveraendert, protokoll.Uebersprungen, protokoll.Zusammengefuehrt, protokoll.NeuZuordnung,
            protokoll.NeuZugeordnet, protokoll.SchluesselKennung ?? "–", lauf.KlassenleitungNeu, lauf.KlassenleitungGeaendert,
            lauf.LehrkraefteNeu, lauf.LehrkraefteGeaendert, lauf.LehrkraefteEntfernt);
        return Ok(lauf.ImportAntwort(protokoll));
    }

    /// <summary>
    /// Nur Entwicklung (Punkt A): entfernt alle Pseudo-Schüler – die aus seed/pseudodaten und ggf. früher importierte –
    /// samt ihren Klassenbuchdaten, wie ein neuer Seed. Testkonten s05A/sQ1 werden gelöst (danach seed-konten).
    /// </summary>
    [NurEntwicklung]
    [DesktopSchnittstelle]   // P6a (229)
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
            hinweis = "Entfernt samt Klassenbuchdaten, Pseudo-Lerngruppen (mit Klassenleitung), Gruppenlisten und Namen der " +
                      "Pseudo-Lehrkräfte. Jetzt die Vorschau neu berechnen; " +
                      "nach dem Import die Testkonten mit POST /api/auth/seed-konten neu verknüpfen."
        });
    }

    /// <summary>
    /// 5.2c-2 (Entscheidung 196): Importprotokoll für die App (Kontomenü → Konten verwalten → „SchILD“). Nur Admin, nur lesen,
    /// ohne Namen – Zahlen, Kennung des Schlüssels, Neu-Zuordnung. Mit Anmeldung (JWT), nicht über den X-Api-Key.
    /// </summary>
    [Authorize(Roles = Rollen.Admin)]
    [HttpGet("protokolle")]
    public async Task<IActionResult> Protokolle([FromQuery] int limit = 100)
    {
        var liste = await _db.SchildImportProtokolle.AsNoTracking()
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(p => new
            {
                id = p.Id, zeit = p.Zeit, akteur = p.Akteur, schuljahr = p.Schuljahr, abschnitt = p.Abschnitt,
                giltAb = p.GiltAb, pseudo = p.IstPseudo, kennung = p.SchluesselKennung, kennungVorher = p.KennungVorher,
                neuZuordnung = p.NeuZuordnung, neu = p.Neu, geaendert = p.Geaendert, klassenwechsel = p.Klassenwechsel,
                kurswechsel = p.Kurswechsel, abgaenge = p.Abgaenge, unveraendert = p.Unveraendert,
                uebersprungen = p.Uebersprungen, zusammengefuehrt = p.Zusammengefuehrt, neuZugeordnet = p.NeuZugeordnet,
                // P3 (Kleinigkeit a): null = keine Angabe bzw. Import vor P3
                klassenleitungKlassen = p.KlassenleitungKlassen, klassenleitungNeu = p.KlassenleitungNeu,
                klassenleitungGeaendert = p.KlassenleitungGeaendert, lehrkraefte = p.Lehrkraefte,
                lehrkraefteNeu = p.LehrkraefteNeu, lehrkraefteGeaendert = p.LehrkraefteGeaendert,
                lehrkraefteEntfernt = p.LehrkraefteEntfernt
            })
            .ToListAsync();
        int gesamt = await _db.SchildImportProtokolle.CountAsync();
        return Ok(new { protokolle = liste, gesamt, betrieb = Produktiv ? "Echtbetrieb" : "Entwicklung" });
    }

    /// <summary>„Desktop (Windows-Benutzer)“ fürs Importprotokoll – ohne Steuerzeichen, gekürzt.</summary>
    private static string Akteur(string? benutzer)
    {
        var b = new string((benutzer ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (b.Length > 60) b = b[..60];
        return b.Length == 0 ? "Desktop" : $"Desktop ({b})";
    }
}
