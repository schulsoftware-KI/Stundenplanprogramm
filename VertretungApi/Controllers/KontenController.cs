using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;
using Aktion = VertretungApi.Services.KontenService.Aktion;

namespace VertretungApi.Controllers;

// =====================================================================
//  Kontenverwaltung (Phase 2, Schritt 5)
//
//    GET    api/konten/rechte               – was darf ich hier? (Admin / Klassenleitung / Stufenkoord.)
//    GET    api/konten?q=&rolle=&filter=    – Kontenliste (Klassenleitung/Stufenkoord.: nur Schülerkonten
//                                             der eigenen Klasse/Stufe)
//    GET    api/konten/{id}                 – ein Konto
//    POST   api/konten/{id}/passwort        – Passwort zurücksetzen -> Startpasswort (einmalig)
//    POST   api/konten/{id}/sperren | entsperren
//  nur Admin:
//    POST   api/konten                      – Konto anlegen -> Startpasswort (einmalig)
//    PUT    api/konten/{id}                 – Benutzername/Rolle/Kürzel/Zuständigkeit/Schüler ändern
//    POST   api/konten/{id}/abmelden
//    DELETE api/konten/{id}
//    GET    api/konten/protokoll?konto=&limit=
//    GET    api/konten/sammel/uebersicht    – Lehrkräfte, Klassen/Stufen mit Konto-Zahlen
//    POST   api/konten/sammel               – Sammelanlage (Vorschau bzw. Anlegen)
//
//  Grundsätze: Startpasswörter werden nur einmal ausgeliefert und nie im Klartext gespeichert;
//  das Konto muss danach ein eigenes Passwort festlegen. Jede Änderung erneuert den
//  Sicherheitsstempel (alte Anmeldungen enden) und landet im Protokoll. Das letzte aktive
//  Admin-Konto lässt sich nicht sperren, löschen oder herabstufen; das eigene Konto wird hier
//  nicht gesperrt/gelöscht/zurückgesetzt (dafür gibt es „Passwort ändern").
// =====================================================================

[ApiController]
[Route("api/konten")]
[Authorize(Roles = Rollen.KontenSehen)]
public class KontenController : ControllerBase
{
    private const int SammelMax = 2000;

    private readonly AppDbContext _db;
    private readonly LoginBremse _bremse;

    public KontenController(AppDbContext db, LoginBremse bremse)
    {
        _db = db;
        _bremse = bremse;
    }

    private string Ich => User.Identity?.Name ?? "";
    private int? IchId => Rollen.BenutzerId(User);
    private bool IstAdmin => User.IsInRole(Rollen.Admin);

    private static readonly StringComparer Cmp = StringComparer.Create(CultureInfo.InvariantCulture, true);

    // -----------------------------------------------------------------
    //  Lesen
    // -----------------------------------------------------------------

    [HttpGet("rechte")]
    public async Task<IActionResult> Rechte()
        => Ok(new KontenRechteDto(IstAdmin, true, true, await MeineZustaendigkeit()));

    /// <summary>
    /// Kontenliste. rolle: Admin|Lehrer|Klassenlehrer|Stufenkoordination|Schueler|Personal;
    /// filter: gesperrt | pflicht (muss Passwort ändern) | nie (nie angemeldet) | verwaist.
    /// q: Wörter, die alle in Benutzername, Kürzel, Name, Klasse oder Zuständigkeit vorkommen.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Liste([FromQuery] string? q, [FromQuery] string? rolle,
        [FromQuery] string? filter, [FromQuery] int limit = 100)
    {
        var query = _db.Benutzer.AsNoTracking().AsQueryable();

        var sichtbar = await SichtbareSchuelerIds();
        if (sichtbar != null)
        {
            var ids = sichtbar.ToList();
            query = query.Where(b => b.Rolle == BenutzerRolle.Schueler && b.SchuelerId != null
                                     && ids.Contains(b.SchuelerId.Value));
        }

        if (!string.IsNullOrWhiteSpace(rolle))
        {
            if (rolle.Equals("Personal", StringComparison.OrdinalIgnoreCase))
                query = query.Where(b => b.Rolle != BenutzerRolle.Schueler);
            else if (Enum.TryParse<BenutzerRolle>(rolle, true, out var r) && Enum.IsDefined(r))
                query = query.Where(b => b.Rolle == r);
        }

        switch ((filter ?? "").ToLowerInvariant())
        {
            case "gesperrt": query = query.Where(b => b.Gesperrt); break;
            case "pflicht": query = query.Where(b => b.MussPasswortAendern); break;
            case "nie": query = query.Where(b => b.LetzteAnmeldung == null); break;
        }

        var dtos = await AlsDto(await query.ToListAsync());
        if (string.Equals(filter, "verwaist", StringComparison.OrdinalIgnoreCase))
            dtos = dtos.Where(d => d.Verwaist).ToList();

        var woerter = (q ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (woerter.Length > 0)
        {
            dtos = dtos.Where(d =>
            {
                var text = $"{d.Benutzername} {d.LehrerKuerzel} {d.Schueler} {d.Klasse} {d.Zustaendig}".ToLowerInvariant();
                return woerter.All(text.Contains);
            }).ToList();
        }

        dtos = dtos
            .OrderBy(d => d.Rolle == nameof(BenutzerRolle.Schueler) ? 1 : 0)
            .ThenBy(d => d.Rolle == nameof(BenutzerRolle.Schueler) ? d.Klasse ?? "~" : "", Cmp)
            .ThenBy(d => d.Rolle == nameof(BenutzerRolle.Schueler) ? NachnameZuerst(d.Schueler) ?? d.Benutzername : d.Benutzername, Cmp)
            .ToList();

        int max = Math.Clamp(limit, 1, 1000);
        return Ok(new KontenListeDto(dtos.Take(max).ToList(), dtos.Count, dtos.Count > max));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Einzeln(int id)
    {
        var u = await _db.Benutzer.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (u == null) return NichtGefunden();
        if (!await DarfSehen(u)) return KeinRecht();
        return Ok(await AlsDto(u));
    }

    // -----------------------------------------------------------------
    //  Passwort zurücksetzen (Admin; Klassenleitung/Stufenkoordination für ihre Schüler)
    // -----------------------------------------------------------------

    [HttpPost("{id:int}/passwort")]
    public async Task<IActionResult> Zuruecksetzen(int id)
    {
        var u = await _db.Benutzer.FindAsync(id);
        if (u == null) return NichtGefunden();
        if (u.Id == IchId)
            return BadRequest(new { fehler = "Das eigene Passwort bitte über „Passwort ändern“ im Kontomenü ändern." });
        if (!await DarfSehen(u)) return KeinRecht();

        var pw = PasswortRegeln.StartPasswort();
        u.PasswortHash = PasswortHasher.Hash(pw);
        u.MussPasswortAendern = true;
        u.PasswortGeaendertAm = DateTime.UtcNow;
        u.SicherheitsStempel = KontenService.NeuerStempel();
        _bremse.Erfolg(u.Benutzername);      // Sperre nach Fehlversuchen aufheben
        KontenService.Protokoll(_db, Ich, Aktion.PasswortZurueckgesetzt, u);
        await _db.SaveChangesAsync();

        return Ok(new StartPasswortDto(await AlsDto(u), pw));
    }

    // -----------------------------------------------------------------
    //  Anlegen / Ändern (Admin)
    // -----------------------------------------------------------------

    [Authorize(Roles = Rollen.Admin)]
    [HttpPost]
    public async Task<IActionResult> Anlegen([FromBody] NeuesKontoDto dto)
    {
        if (!RolleLesen(dto.Rolle, out var rolle)) return BadRequest(new { fehler = "Unbekannte Rolle.", feld = "rolle" });

        var u = new Benutzer { AngelegtAm = DateTime.UtcNow, MussPasswortAendern = true };
        var fehler = await ZuordnungSetzen(u, rolle, dto.LehrerKuerzel, dto.SchuelerId, dto.Zustaendig, null);
        if (fehler != null) return fehler;

        var name = (dto.Benutzername ?? "").Trim();
        if (name.Length == 0)
        {
            if (rolle == BenutzerRolle.Schueler)
            {
                var s = await _db.Schueler.AsNoTracking().FirstAsync(x => x.Id == u.SchuelerId);
                name = await KontenService.FreierNameAsync(_db, PasswortRegeln.NameVorschlag(s.Vorname, s.Nachname));
            }
            else if (!string.IsNullOrWhiteSpace(u.LehrerKuerzel))
                name = u.LehrerKuerzel;
            else
                return BadRequest(new { fehler = "Bitte einen Benutzernamen angeben.", feld = "benutzername" });
        }
        var nameFehler = PasswortRegeln.BenutzernameFehler(name);
        if (nameFehler != null) return BadRequest(new { fehler = nameFehler, feld = "benutzername" });
        if (await KontenService.NameVergebenAsync(_db, name))
            return Conflict(new { fehler = $"Den Benutzernamen „{name}“ gibt es schon.", feld = "benutzername" });

        var pw = PasswortRegeln.StartPasswort();
        u.Benutzername = name;
        u.PasswortHash = PasswortHasher.Hash(pw);
        u.PasswortGeaendertAm = DateTime.UtcNow;
        _db.Benutzer.Add(u);
        await _db.SaveChangesAsync();                       // Id fürs Protokoll
        KontenService.Protokoll(_db, Ich, Aktion.Angelegt, u, KontenService.Zuordnung(u));
        await _db.SaveChangesAsync();

        return Ok(new StartPasswortDto(await AlsDto(u), pw));
    }

    [Authorize(Roles = Rollen.Admin)]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Aendern(int id, [FromBody] KontoAendernDto dto)
    {
        var u = await _db.Benutzer.FindAsync(id);
        if (u == null) return NichtGefunden();
        if (!RolleLesen(dto.Rolle, out var rolle)) return BadRequest(new { fehler = "Unbekannte Rolle.", feld = "rolle" });

        if ((u.Rolle == BenutzerRolle.Schueler) != (rolle == BenutzerRolle.Schueler))
            return BadRequest(new
            {
                fehler = "Ein Schülerkonto lässt sich nicht in ein Konto für Lehrkräfte bzw. Verwaltung umwandeln " +
                         "(und umgekehrt). Bitte ein neues Konto anlegen.",
                feld = "rolle"
            });
        if (u.Rolle == BenutzerRolle.Admin && rolle != BenutzerRolle.Admin)
        {
            if (u.Id == IchId)
                return BadRequest(new { fehler = "Die eigene Admin-Rolle kannst du nicht entziehen.", feld = "rolle" });
            if (!u.Gesperrt && await KontenService.AktiveAdminsAsync(_db) <= 1)
                return BadRequest(new { fehler = "Das ist das letzte aktive Admin-Konto.", feld = "rolle" });
        }

        var vorher = KontenService.Zuordnung(u);
        var alterName = u.Benutzername;

        var fehler = await ZuordnungSetzen(u, rolle, dto.LehrerKuerzel, dto.SchuelerId, dto.Zustaendig, u.Id);
        if (fehler != null) return fehler;

        var name = (dto.Benutzername ?? "").Trim();
        if (name.Length == 0) name = alterName;
        if (name != alterName)
        {
            var nameFehler = PasswortRegeln.BenutzernameFehler(name);
            if (nameFehler != null) return BadRequest(new { fehler = nameFehler, feld = "benutzername" });
            if (await KontenService.NameVergebenAsync(_db, name, u.Id))
                return Conflict(new { fehler = $"Den Benutzernamen „{name}“ gibt es schon.", feld = "benutzername" });
            u.Benutzername = name;
        }

        var nachher = KontenService.Zuordnung(u);
        var details = new List<string>();
        if (name != alterName) details.Add($"Benutzername {alterName} → {name}");
        if (vorher != nachher) details.Add($"{vorher} → {nachher}");

        if (details.Count > 0)
        {
            // Rolle, Kürzel und Name stehen im Token: alte Anmeldungen beenden.
            u.SicherheitsStempel = KontenService.NeuerStempel();
            KontenService.Protokoll(_db, Ich, Aktion.Geaendert, u, string.Join("; ", details));
            await _db.SaveChangesAsync();
        }
        return Ok(await AlsDto(u));
    }

    // -----------------------------------------------------------------
    //  Sperren/Entsperren (Admin; Klassenleitung/Stufenkoordination für ihre Schüler),
    //  Abmelden, Löschen (Admin)
    // -----------------------------------------------------------------

    [HttpPost("{id:int}/sperren")]
    public async Task<IActionResult> Sperren(int id)
    {
        var u = await _db.Benutzer.FindAsync(id);
        if (u == null) return NichtGefunden();
        if (u.Id == IchId) return BadRequest(new { fehler = "Das eigene Konto kannst du nicht sperren." });
        if (!await DarfSehen(u)) return KeinRecht();
        if (u.Gesperrt) return Ok(await AlsDto(u));
        if (u.Rolle == BenutzerRolle.Admin && await KontenService.AktiveAdminsAsync(_db) <= 1)
            return BadRequest(new { fehler = "Das letzte aktive Admin-Konto kann nicht gesperrt werden." });

        u.Gesperrt = true;
        u.SicherheitsStempel = KontenService.NeuerStempel();
        KontenService.Protokoll(_db, Ich, Aktion.Gesperrt, u);
        await _db.SaveChangesAsync();
        return Ok(await AlsDto(u));
    }

    [HttpPost("{id:int}/entsperren")]
    public async Task<IActionResult> Entsperren(int id)
    {
        var u = await _db.Benutzer.FindAsync(id);
        if (u == null) return NichtGefunden();
        if (!await DarfSehen(u)) return KeinRecht();
        if (!u.Gesperrt) return Ok(await AlsDto(u));

        u.Gesperrt = false;
        _bremse.Erfolg(u.Benutzername);
        KontenService.Protokoll(_db, Ich, Aktion.Entsperrt, u);
        await _db.SaveChangesAsync();
        return Ok(await AlsDto(u));
    }

    /// <summary>Alle Anmeldungen des Kontos beenden (z. B. Handy verloren). Passwort bleibt.</summary>
    [Authorize(Roles = Rollen.Admin)]
    [HttpPost("{id:int}/abmelden")]
    public async Task<IActionResult> Abmelden(int id)
    {
        var u = await _db.Benutzer.FindAsync(id);
        if (u == null) return NichtGefunden();
        if (u.Id == IchId)
            return BadRequest(new { fehler = "Für das eigene Konto bitte „Auf allen Geräten abmelden“ im Kontomenü nutzen." });

        u.SicherheitsStempel = KontenService.NeuerStempel();
        KontenService.Protokoll(_db, Ich, Aktion.Abgemeldet, u);
        await _db.SaveChangesAsync();
        return Ok(await AlsDto(u));
    }

    [Authorize(Roles = Rollen.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Loeschen(int id)
    {
        var u = await _db.Benutzer.FindAsync(id);
        if (u == null) return NichtGefunden();
        if (u.Id == IchId) return BadRequest(new { fehler = "Das eigene Konto kannst du nicht löschen." });
        if (u.Rolle == BenutzerRolle.Admin && !u.Gesperrt && await KontenService.AktiveAdminsAsync(_db) <= 1)
            return BadRequest(new { fehler = "Das letzte aktive Admin-Konto kann nicht gelöscht werden." });

        KontenService.Protokoll(_db, Ich, Aktion.Geloescht, u, KontenService.Zuordnung(u));
        _db.Benutzer.Remove(u);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Roles = Rollen.Admin)]
    [HttpGet("protokoll")]
    public async Task<IActionResult> Protokoll([FromQuery] int? konto, [FromQuery] int limit = 200)
    {
        var q = _db.KontoProtokolle.AsNoTracking().AsQueryable();
        if (konto is int k) q = q.Where(p => p.KontoId == k);
        var liste = await q.OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(p => new ProtokollDto(p.Id, p.Zeit, p.Akteur, p.Aktion, p.KontoId, p.Konto, p.Details))
            .ToListAsync();
        return Ok(liste);
    }

    // -----------------------------------------------------------------
    //  Sammelanlage (Admin)
    // -----------------------------------------------------------------

    [Authorize(Roles = Rollen.Admin)]
    [HttpGet("sammel/uebersicht")]
    public async Task<IActionResult> SammelUebersicht()
    {
        var auswahl = await WochenplanService.AuswahlAsync(_db, Heute());
        var kuerzelMitKonto = await PersonalKuerzelAsync();

        var inKlasse = await _db.Zugehoerigkeiten
            .Where(z => z.Lerngruppe!.Typ == LerngruppeTyp.Klasse)
            .Select(z => new { Gruppe = z.Lerngruppe!.Bezeichnung, z.SchuelerId })
            .ToListAsync();
        var mitKlasse = inKlasse.Select(x => x.SchuelerId).ToHashSet();
        var ohneKlasse = (await _db.Schueler.Select(s => new { s.Id, s.Stufe }).ToListAsync())
            .Where(s => !mitKlasse.Contains(s.Id) && !string.IsNullOrWhiteSpace(s.Stufe))
            .Select(s => new { Gruppe = s.Stufe.Trim().ToUpperInvariant(), SchuelerId = s.Id });
        var mitKonto = (await _db.Benutzer
                .Where(b => b.Rolle == BenutzerRolle.Schueler && b.SchuelerId != null)
                .Select(b => b.SchuelerId!.Value).ToListAsync())
            .ToHashSet();

        var gruppen = inKlasse.GroupBy(x => x.Gruppe, Cmp).Select(g => (g, "Klasse"))
            .Concat(ohneKlasse.GroupBy(x => x.Gruppe, Cmp).Select(g => (g, "Stufe")))
            .Select(t =>
            {
                var ids = t.g.Select(x => x.SchuelerId).Distinct().ToList();
                return new SammelGruppeDto(t.g.Key, t.Item2, ids.Count, ids.Count(mitKonto.Contains));
            })
            .OrderBy(g => g.Gruppe, Cmp)
            .ToList();

        return Ok(new SammelUebersichtDto(
            auswahl.Lehrer.Count, auswahl.Lehrer.Count(kuerzelMitKonto.Contains), auswahl.Grundplan, gruppen));
    }

    /// <summary>
    /// Sammelanlage. Anlegen = false: nur Vorschau (nichts wird gespeichert). Vorhandene Konten
    /// werden übersprungen. Startpasswörter stehen nur in der Antwort des Anlegens.
    /// </summary>
    [Authorize(Roles = Rollen.Admin)]
    [HttpPost("sammel")]
    public async Task<IActionResult> Sammel([FromBody] SammelDto dto)
    {
        bool lehrer = string.Equals(dto.Art, "Lehrer", StringComparison.OrdinalIgnoreCase);
        bool schueler = string.Equals(dto.Art, "Schueler", StringComparison.OrdinalIgnoreCase);
        if (!lehrer && !schueler) return BadRequest(new { fehler = "Art muss „Lehrer“ oder „Schueler“ sein." });

        var nur = dto.Nur == null ? null
            : new HashSet<string>(dto.Nur.Select(x => (x ?? "").Trim()), StringComparer.OrdinalIgnoreCase);

        // Alle belegten Namen vorab laden (ohne Groß-/Kleinschreibung).
        var vergeben = new HashSet<string>(await _db.Benutzer.Select(b => b.Benutzername).ToListAsync(),
            StringComparer.OrdinalIgnoreCase);

        var zeilen = new List<SammelZeileDto>();
        var neueKonten = new List<(Benutzer Konto, int Zeile)>();
        string? grundplan = null;

        if (lehrer)
        {
            var auswahl = await WochenplanService.AuswahlAsync(_db, Heute());
            grundplan = auswahl.Grundplan;
            if (grundplan == null)
                return BadRequest(new { fehler = "Es ist noch kein Grundplan veröffentlicht – daraus stammen die Lehrerkürzel." });

            var personal = await _db.Benutzer.AsNoTracking()
                .Where(b => b.Rolle != BenutzerRolle.Schueler && b.LehrerKuerzel != null)
                .Select(b => new { b.Benutzername, b.LehrerKuerzel })
                .ToListAsync();

            foreach (var k in auswahl.Lehrer)
            {
                if (nur != null && !nur.Contains(k)) continue;
                var vorhanden = personal.FirstOrDefault(p => string.Equals(p.LehrerKuerzel!.Trim(), k, StringComparison.OrdinalIgnoreCase));
                if (vorhanden != null)
                {
                    zeilen.Add(new SammelZeileDto(k, k, null, vorhanden.Benutzername, "vorhanden", null, null));
                    continue;
                }
                var nameFehler = PasswortRegeln.BenutzernameFehler(k);
                if (nameFehler != null)
                {
                    zeilen.Add(new SammelZeileDto(k, k, null, k, "konflikt",
                        "Kürzel taugt nicht als Benutzername – Konto bitte einzeln anlegen.", null));
                    continue;
                }
                if (!vergeben.Add(k))
                {
                    zeilen.Add(new SammelZeileDto(k, k, null, k, "konflikt",
                        "Benutzername schon an ein anderes Konto vergeben – bitte einzeln anlegen.", null));
                    continue;
                }
                zeilen.Add(new SammelZeileDto(k, k, null, k, "neu", null, null));
                neueKonten.Add((new Benutzer
                {
                    Benutzername = k, Rolle = BenutzerRolle.Lehrer, LehrerKuerzel = k,
                    AngelegtAm = DateTime.UtcNow, MussPasswortAendern = true
                }, zeilen.Count - 1));
            }
        }
        else
        {
            var gruppen = (dto.Gruppen ?? Array.Empty<string>())
                .Select(g => (g ?? "").Trim().ToUpperInvariant()).Where(g => g.Length > 0).Distinct().ToList();
            if (gruppen.Count == 0) return BadRequest(new { fehler = "Bitte mindestens eine Klasse oder Stufe wählen." });

            var ids = (await KontenService.SchuelerIdsAsync(_db, gruppen)).ToList();
            var liste = await _db.Schueler.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new { s.Id, s.Vorname, s.Nachname, s.Stufe }).ToListAsync();
            var info = await KontenService.SchuelerInfoAsync(_db, ids);
            var konten = (await _db.Benutzer.AsNoTracking()
                    .Where(b => b.Rolle == BenutzerRolle.Schueler && b.SchuelerId != null)
                    .Select(b => new { Id = b.SchuelerId!.Value, b.Benutzername }).ToListAsync())
                .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Benutzername);

            foreach (var s in liste
                         .OrderBy(s => info.TryGetValue(s.Id, out var i) ? i.Klasse ?? i.Stufe : s.Stufe, Cmp)
                         .ThenBy(s => s.Nachname, Cmp).ThenBy(s => s.Vorname, Cmp))
            {
                var schluessel = s.Id.ToString(CultureInfo.InvariantCulture);
                if (nur != null && !nur.Contains(schluessel)) continue;
                var gruppe = info.TryGetValue(s.Id, out var si) ? si.Klasse ?? si.Stufe : s.Stufe;
                var anzeige = $"{s.Nachname}, {s.Vorname}";
                if (konten.TryGetValue(s.Id, out var vorhandenerName))
                {
                    zeilen.Add(new SammelZeileDto(schluessel, anzeige, gruppe, vorhandenerName, "vorhanden", null, null));
                    continue;
                }
                var name = KontenService.FreierName(PasswortRegeln.NameVorschlag(s.Vorname, s.Nachname), vergeben);
                zeilen.Add(new SammelZeileDto(schluessel, anzeige, gruppe, name, "neu", null, null));
                neueKonten.Add((new Benutzer
                {
                    Benutzername = name, Rolle = BenutzerRolle.Schueler, SchuelerId = s.Id,
                    AngelegtAm = DateTime.UtcNow, MussPasswortAendern = true
                }, zeilen.Count - 1));
            }
        }

        if (neueKonten.Count > SammelMax)
            return BadRequest(new { fehler = $"Zu viele Konten auf einmal ({neueKonten.Count}). Bitte in Teilen anlegen (höchstens {SammelMax})." });

        int angelegt = 0;
        if (dto.Anlegen && neueKonten.Count > 0)
        {
            // PBKDF2 ist absichtlich langsam – bei vielen Konten parallel rechnen.
            var passwoerter = neueKonten.Select(_ => PasswortRegeln.StartPasswort()).ToArray();
            var hashes = new string[passwoerter.Length];
            Parallel.For(0, passwoerter.Length, i => hashes[i] = PasswortHasher.Hash(passwoerter[i]));

            for (int i = 0; i < neueKonten.Count; i++)
            {
                var (konto, zeile) = neueKonten[i];
                konto.PasswortHash = hashes[i];
                konto.PasswortGeaendertAm = DateTime.UtcNow;
                _db.Benutzer.Add(konto);
                zeilen[zeile] = zeilen[zeile] with { Status = "angelegt", StartPasswort = passwoerter[i] };
            }
            await _db.SaveChangesAsync();
            foreach (var (konto, _) in neueKonten)
                KontenService.Protokoll(_db, Ich, Aktion.Angelegt, konto, "Sammelanlage; " + KontenService.Zuordnung(konto));
            await _db.SaveChangesAsync();
            angelegt = neueKonten.Count;
        }

        return Ok(new SammelErgebnisDto(
            lehrer ? "Lehrer" : "Schueler",
            zeilen,
            Neu: zeilen.Count(z => z.Status == "neu"),
            Vorhanden: zeilen.Count(z => z.Status == "vorhanden"),
            Angelegt: angelegt,
            Konflikte: zeilen.Count(z => z.Status == "konflikt"),
            Grundplan: grundplan));
    }

    // -----------------------------------------------------------------
    //  Hilfen
    // -----------------------------------------------------------------

    private static DateOnly Heute() => DateOnly.FromDateTime(DateTime.Now);

    private IActionResult NichtGefunden() => NotFound(new { fehler = "Konto nicht gefunden." });

    private IActionResult KeinRecht() => StatusCode(StatusCodes.Status403Forbidden,
        new { fehler = "Für dieses Konto hast du keine Berechtigung." });

    private static bool RolleLesen(string? text, out BenutzerRolle rolle)
        => Enum.TryParse(text, true, out rolle) && Enum.IsDefined(rolle) && !int.TryParse(text, out _);

    private async Task<List<string>> MeineZustaendigkeit()
    {
        var id = IchId;
        if (id == null) return new();
        var z = await _db.Benutzer.AsNoTracking().Where(b => b.Id == id.Value)
            .Select(b => b.Zustaendig).FirstOrDefaultAsync();
        return KontenService.Zustaendigkeiten(z);
    }

    /// <summary>Schüler, deren Konten die angemeldete Person sieht (null = alle; Admin).</summary>
    private async Task<HashSet<int>?> SichtbareSchuelerIds()
        => IstAdmin ? null : await KontenService.SchuelerIdsAsync(_db, await MeineZustaendigkeit());

    private async Task<bool> DarfSehen(Benutzer u)
    {
        if (IstAdmin) return true;
        if (u.Rolle != BenutzerRolle.Schueler || u.SchuelerId is not int sid) return false;
        var ids = await SichtbareSchuelerIds();
        return ids != null && ids.Contains(sid);
    }

    private async Task<HashSet<string>> PersonalKuerzelAsync()
        => (await _db.Benutzer
                .Where(b => b.Rolle != BenutzerRolle.Schueler && b.LehrerKuerzel != null)
                .Select(b => b.LehrerKuerzel!).ToListAsync())
            .Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Setzt Rolle und Zuordnung (Kürzel / Zuständigkeit / Schüler) und prüft sie.
    /// Liefert eine Fehlerantwort oder null. ausser = eigene Konto-Id beim Ändern.
    /// </summary>
    private async Task<IActionResult?> ZuordnungSetzen(Benutzer u, BenutzerRolle rolle, string? kuerzel,
        int? schuelerId, string? zustaendig, int? ausser)
    {
        if (rolle == BenutzerRolle.Schueler)
        {
            if (schuelerId is not int sid)
                return BadRequest(new { fehler = "Bitte die Schülerin bzw. den Schüler auswählen.", feld = "schueler" });
            if (!await _db.Schueler.AnyAsync(s => s.Id == sid))
                return BadRequest(new { fehler = "Diese Schülerin bzw. dieser Schüler wurde nicht gefunden.", feld = "schueler" });
            var schon = await _db.Benutzer
                .Where(b => b.Rolle == BenutzerRolle.Schueler && b.SchuelerId == sid && (ausser == null || b.Id != ausser))
                .Select(b => b.Benutzername).FirstOrDefaultAsync();
            if (schon != null)
                return Conflict(new { fehler = $"Für diese Person gibt es schon das Konto „{schon}“.", feld = "schueler" });
            u.SchuelerId = sid;
            u.LehrerKuerzel = null;
            u.Zustaendig = null;
        }
        else
        {
            var k = (kuerzel ?? "").Trim();
            if (k.Length == 0 && rolle != BenutzerRolle.Admin)
                return BadRequest(new { fehler = "Bitte das Lehrerkürzel angeben.", feld = "kuerzel" });
            if (k.Length > 20)
                return BadRequest(new { fehler = "Das Kürzel ist zu lang.", feld = "kuerzel" });
            u.LehrerKuerzel = k.Length == 0 ? null : k;
            u.SchuelerId = null;

            if (rolle is BenutzerRolle.Klassenlehrer or BenutzerRolle.Stufenkoordination)
            {
                var z = KontenService.ZustaendigText(zustaendig);
                if (z == null)
                    return BadRequest(new
                    {
                        fehler = rolle == BenutzerRolle.Klassenlehrer
                            ? "Bitte die Klasse angeben (z. B. 05A)."
                            : "Bitte die Stufe angeben (z. B. Q1).",
                        feld = "zustaendig"
                    });
                u.Zustaendig = z;
            }
            else
            {
                u.Zustaendig = null;
            }
        }
        u.Rolle = rolle;
        return null;
    }

    private async Task<KontoDto> AlsDto(Benutzer u) => (await AlsDto(new List<Benutzer> { u }))[0];

    private async Task<List<KontoDto>> AlsDto(List<Benutzer> konten)
    {
        var info = await KontenService.SchuelerInfoAsync(_db,
            konten.Where(k => k.SchuelerId != null).Select(k => k.SchuelerId!.Value));
        var ich = IchId;
        return konten.Select(u =>
        {
            (string Name, string? Klasse, string Stufe)? s =
                u.SchuelerId is int sid && info.TryGetValue(sid, out var x) ? x : null;
            return new KontoDto(
                u.Id, u.Benutzername, u.Rolle.ToString(), KontenService.RollenText(u.Rolle),
                u.LehrerKuerzel, u.Zustaendig, u.SchuelerId,
                s?.Name, s == null ? null : s.Value.Klasse ?? s.Value.Stufe,
                Verwaist: u.Rolle == BenutzerRolle.Schueler && s == null,
                u.Gesperrt, u.MussPasswortAendern, u.AngelegtAm, u.LetzteAnmeldung, u.PasswortGeaendertAm,
                Ich: u.Id == ich);
        }).ToList();
    }

    /// <summary>„Mia Müller" → „Müller Mia" (zum Sortieren nach Nachnamen).</summary>
    private static string? NachnameZuerst(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var i = name.LastIndexOf(' ');
        return i < 0 ? name : name[(i + 1)..] + " " + name[..i];
    }
}
