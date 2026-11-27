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

// =====================================================================
//  P6a (Entscheidung 228): Einsicht in das Anmeldeprotokoll – nur Admin (Konten verwalten → Reiter „Zugriffe“).
//
//    GET api/zugriffe/anmeldungen?konto=&ergebnis=&von=&bis=&limit=
//        konto    – Teil des Benutzernamens; „-“ = unbekannte Konten
//        ergebnis – „erfolglos“ | „erfolgreich“ | ein Ergebnis aus der Liste (Antwort: ergebnisse)
//        von, bis – Tage (JJJJ-MM-TT, Zeitzone des Programms – PB-1a), beide einschließlich
//
//  Oben „letzte 24 Stunden: n erfolglos“ (ohne Filter). Kein Löschen von Hand; die Frist ist „Aufbewahrung der
//  Protokolle“.
//
//  P6b (Entscheidungen 232, 233): Leseprotokoll – Teil „Ansichten“:
//    GET api/zugriffe/ansichten?schueler=&konto=&ansicht=&von=&bis=&limit=
//        schueler – Schüler-Id (Einzelansichten dieses Schülers); konto – Teil des Benutzernamens; ansicht – Id aus
//        dem Katalog (Antwort: ansichten); von, bis – Tage wie oben
//  Jeder Aufruf steht selbst im Leseprotokoll (Ansicht „Leseprotokoll“, Gegenstand = Filter). Namen der Schüler
//  werden nur beim Lesen ergänzt (gespeichert ist die Id).
// =====================================================================

[ApiController]
[Route("api/zugriffe")]
[Authorize(Roles = Rollen.Admin)]
public class ZugriffeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Zugriffsschutz _zugriff;
    private readonly Leseprotokoll _lese;
    private readonly Schuleinstellungen _einst;

    public ZugriffeController(AppDbContext db, Zugriffsschutz zugriff, Leseprotokoll lese, Schuleinstellungen einst)
    {
        _db = db;
        _zugriff = zugriff;
        _lese = lese;
        _einst = einst;
    }

    [HttpGet("anmeldungen")]
    public async Task<IActionResult> Anmeldungen([FromQuery] string? konto, [FromQuery] string? ergebnis,
        [FromQuery] string? von, [FromQuery] string? bis, [FromQuery] int limit = 300)
    {
        var q = _db.AnmeldeProtokolle.AsNoTracking().AsQueryable();

        var k = (konto ?? "").Trim();
        if (k == "-") q = q.Where(p => p.Konto == "");
        else if (k.Length > 0)
        {
            var klein = k.ToLower();
            q = q.Where(p => p.Konto.ToLower().Contains(klein));
        }

        var e = (ergebnis ?? "").Trim();
        if (e.Equals("erfolglos", StringComparison.OrdinalIgnoreCase)) q = q.Where(p => !p.Erfolgreich);
        else if (e.Equals("erfolgreich", StringComparison.OrdinalIgnoreCase)) q = q.Where(p => p.Erfolgreich);
        else if (e.Length > 0) q = q.Where(p => p.Ergebnis == e);

        if (Tag(von) is DateOnly v) { var ab = Utc(v); q = q.Where(p => p.Zeit >= ab); }
        if (Tag(bis) is DateOnly b) { var vor = Utc(b.AddDays(1)); q = q.Where(p => p.Zeit < vor); }

        int gesamt = await q.CountAsync();
        var liste = await q.OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync();

        var seit = DateTime.UtcNow.AddHours(-24);
        var letzte = await _db.AnmeldeProtokolle.AsNoTracking()
            .Where(p => p.Zeit >= seit && !p.Erfolgreich)
            .Select(p => p.KontoId)
            .ToListAsync();

        return Ok(new AnmeldungenDto(
            liste.Select(p => new AnmeldungEintragDto(p.Id, p.Zeit, p.KontoId, p.Konto, p.Rolle, RollenText(p.Rolle),
                p.Ergebnis, p.Erfolgreich, p.Adresse, p.Schulnetz, p.Geraet)).ToList(),
            gesamt,
            letzte.Count,
            letzte.Distinct().Count(),
            _zugriff.HatSchuladressen,
            Anmeldeprotokoll.Ergebnisse.Alle.ToList()));
    }

    [HttpGet("ansichten")]
    public async Task<IActionResult> Ansichten([FromQuery] int? schueler, [FromQuery] string? konto, [FromQuery] string? ansicht,
        [FromQuery] string? von, [FromQuery] string? bis, [FromQuery] int limit = 300)
    {
        var q = _db.LeseProtokolle.AsNoTracking().AsQueryable();
        var filter = new List<string>();

        string? schuelerName = null;
        if (schueler is int sid)
        {
            q = q.Where(p => p.SchuelerId == sid);
            filter.Add($"Schüler-Id {sid}");
            schuelerName = await _db.Schueler.AsNoTracking().Where(s => s.Id == sid)
                .Select(s => s.Vorname + " " + s.Nachname).FirstOrDefaultAsync();
        }
        var k = (konto ?? "").Trim();
        if (k.Length > 0)
        {
            var klein = k.ToLower();
            q = q.Where(p => p.Akteur.ToLower().Contains(klein));
            filter.Add($"Konto „{(k.Length > 40 ? k[..40] : k)}“");
        }
        var a = LeseAnsichten.Finde((ansicht ?? "").Trim());
        if (a != null)
        {
            string aid = a.Id;
            q = q.Where(p => p.Ansicht == aid);
            filter.Add(a.Text);
        }
        if (Tag(von) is DateOnly v) { q = q.Where(p => p.Tag >= v); filter.Add($"ab {v:dd.MM.yyyy}"); }
        if (Tag(bis) is DateOnly b) { q = q.Where(p => p.Tag <= b); filter.Add($"bis {b:dd.MM.yyyy}"); }

        int gesamt = await q.CountAsync();
        int aufrufe = gesamt == 0 ? 0 : await q.SumAsync(p => p.Anzahl);
        var liste = await q.OrderByDescending(p => p.LetzteZeit).ThenByDescending(p => p.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync();

        // Namen nur für die Anzeige (der Admin darf sie sehen); gespeichert ist die Id.
        var ids = liste.Where(p => p.SchuelerId != null).Select(p => p.SchuelerId!.Value).Distinct().ToList();
        var namen = ids.Count == 0 ? new Dictionary<int, string>()
            : await _db.Schueler.AsNoTracking().Where(s => ids.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Vorname + " " + s.Nachname);

        // Der Aufruf selbst steht im Leseprotokoll (233).
        await _lese.AufzeichnenAsync(User, new LeseZugriff(LeseAnsichten.Leseprotokoll,
            filter.Count == 0 ? "ohne Filter" : string.Join(" · ", filter), SchuelerId: schueler));

        return Ok(new AnsichtenDto(
            liste.Select(p => new AnsichtEintragDto(p.Id, p.Tag, p.ErsteZeit, p.LetzteZeit, p.Anzahl, p.KontoId, p.Akteur,
                p.Rolle, RollenText(p.Rolle) ?? p.Rolle, p.Ansicht, LeseAnsichten.Text(p.Ansicht), LeseAnsichten.IstListe(p.Ansicht),
                p.Gegenstand, p.SchuelerId,
                p.SchuelerId is int x && namen.TryGetValue(x, out var n) ? n : null, p.IstPseudo)).ToList(),
            gesamt,
            aufrufe,
            await _einst.LeseprotokollAnAsync(_db),
            schuelerName,
            LeseAnsichten.Alle.Select(d => new AnsichtArtDto(d.Id, d.Text)).ToList()));
    }

    private static DateOnly? Tag(string? text)
        => DateOnly.TryParseExact((text ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : null;

    /// <summary>Beginn eines Tages (Zeitzone des Programms, PB-1a 310) als UTC.</summary>
    private static DateTime Utc(DateOnly tag) => Ortszeit.TagesbeginnUtc(tag);

    private static string? RollenText(string? rolle)
        => Enum.TryParse<BenutzerRolle>(rolle, out var r) && Enum.IsDefined(r) ? KontenService.RollenText(r) : rolle;
}
