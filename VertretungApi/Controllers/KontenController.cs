using System.Globalization;
using System.Text.RegularExpressions;
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
//    GET    api/konten/schild-vorschlaege   – 5.2c-2 (192): Vorschläge aus SchILD (Klassenleitung laut Klassen.dat)
//    POST   api/konten/schild-vorschlaege   – gewählte Vorschläge übernehmen (nur per Knopf, Kontenprotokoll)
//
//  Phase 5.2c-2: Namen der Lehrkräfte (Lehrkraefte.dat) erscheinen nur für den Admin (194); Schülerkonten zeigen
//  „laut SchILD wieder aktiv, aber gesperrt“ und „Benutzername passt nicht mehr zum Namen“ (197) – nichts geschieht
//  automatisch.
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
    /// filter: gesperrt | pflicht (muss Passwort ändern) | nie (nie angemeldet) | verwaist | seit 5.2c-2 wiederaktiv (laut
    /// SchILD aktiv, aber gesperrt) | namegeaendert (Benutzername passt nicht mehr zum Namen laut SchILD).
    /// q: Wörter, die alle in Benutzername, Kürzel, Name, Klasse oder Zuständigkeit vorkommen (Admin auch Name der Lehrkraft).
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
            // 5.2c-2 (197): die genaue Prüfung folgt nach AlsDto
            case "wiederaktiv": query = query.Where(b => b.Rolle == BenutzerRolle.Schueler && b.Gesperrt); break;
            case "namegeaendert": query = query.Where(b => b.Rolle == BenutzerRolle.Schueler); break;
        }

        var dtos = await AlsDto(await query.ToListAsync());
        if (string.Equals(filter, "verwaist", StringComparison.OrdinalIgnoreCase))
            dtos = dtos.Where(d => d.Verwaist).ToList();
        else if (string.Equals(filter, "wiederaktiv", StringComparison.OrdinalIgnoreCase))
            dtos = dtos.Where(d => d.WiederAktiv).ToList();
        else if (string.Equals(filter, "namegeaendert", StringComparison.OrdinalIgnoreCase))
            dtos = dtos.Where(d => d.NameGeaendert).ToList();

        var woerter = (q ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (woerter.Length > 0)
        {
            dtos = dtos.Where(d =>
            {
                var text = $"{d.Benutzername} {d.LehrerKuerzel} {d.Schueler} {d.Klasse} {d.Zustaendig} {d.Lehrkraft}".ToLowerInvariant();
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
        var dto = await AlsDto(u);
        if (!IstAdmin) return Ok(dto);

        // 5.2c-2 (197, Frage D): Vorschlag zum Umbenennen und wer zuletzt gesperrt hat – nur in der Einzelansicht.
        string? vorschlag = null;
        if (dto.NameGeaendert && u.SchuelerId is int sid
            && await _db.Schueler.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid) is Schueler s)
            vorschlag = await KontenService.FreierNameAsync(_db, PasswortRegeln.NameVorschlag(s.Vorname, s.Nachname));
        string? gesperrtDurch = null;
        if (u.Gesperrt)
        {
            var p = await _db.KontoProtokolle.AsNoTracking()
                .Where(x => x.KontoId == u.Id && x.Aktion == Aktion.Gesperrt)
                .OrderByDescending(x => x.Zeit).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
            if (p != null)
                gesperrtDurch = $"{p.Akteur}{(string.IsNullOrWhiteSpace(p.Details) ? "" : " – " + p.Details)} " +
                                $"({p.Zeit.ToLocalTime():dd.MM.yyyy})";
        }
        return Ok(dto with { BenutzernameVorschlag = vorschlag, GesperrtDurch = gesperrtDurch });
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
            .GueltigAm(Heute())                              // Phase 5.2b-1: Klasse von heute
            .Select(z => new { Gruppe = z.Lerngruppe!.Bezeichnung, z.SchuelerId })
            .ToListAsync();
        var mitKlasse = inKlasse.Select(x => x.SchuelerId).ToHashSet();
        // Phase 5.2b-1: Abgänge (Status „abgang“) zählen nicht mehr.
        var ohneKlasse = (await _db.Schueler.Where(s => s.Status == null || s.Status != "abgang")
                .Select(s => new { s.Id, s.Stufe }).ToListAsync())
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
            auswahl.Lehrer.Count, auswahl.Lehrer.Count(kuerzelMitKonto.Contains), auswahl.Grundplan, gruppen,
            Klassenleitungen: (await KlassenleitungenAsync()).Klassen.Count));
    }

    /// <summary>
    /// Sammelanlage. Anlegen = false: nur Vorschau (nichts wird gespeichert). Vorhandene Konten
    /// werden übersprungen. Startpasswörter stehen nur in der Antwort des Anlegens. Seit 5.2c-2: Lehrkräfte mit Name laut
    /// Lehrkraefte.dat; mit KlassenleitungEintragen bekommen neue Konten der Klassenleitung laut SchILD gleich die Rolle
    /// Klassenleitung und ihre Klasse(n) (192).
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
            // 5.2c-2 (192, 194): Namen laut Lehrkraefte.dat; Klassenleitung laut SchILD als Rolle (nur neue Konten).
            var namen = await LehrkraftNamenAsync(auswahl.Lehrer);
            var leitung = dto.KlassenleitungEintragen
                ? KlassenJeKuerzel((await KlassenleitungenAsync()).Klassen)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var k in auswahl.Lehrer)
            {
                if (nur != null && !nur.Contains(k)) continue;
                string? name = namen.TryGetValue(k, out var nm) ? nm : null;
                var vorhanden = personal.FirstOrDefault(p => string.Equals(p.LehrerKuerzel!.Trim(), k, StringComparison.OrdinalIgnoreCase));
                if (vorhanden != null)
                {
                    zeilen.Add(new SammelZeileDto(k, k, null, vorhanden.Benutzername, "vorhanden", null, null, name));
                    continue;
                }
                var nameFehler = PasswortRegeln.BenutzernameFehler(k);
                if (nameFehler != null)
                {
                    zeilen.Add(new SammelZeileDto(k, k, null, k, "konflikt",
                        "Kürzel taugt nicht als Benutzername – Konto bitte einzeln anlegen.", null, name));
                    continue;
                }
                if (!vergeben.Add(k))
                {
                    zeilen.Add(new SammelZeileDto(k, k, null, k, "konflikt",
                        "Benutzername schon an ein anderes Konto vergeben – bitte einzeln anlegen.", null, name));
                    continue;
                }
                string? leitet = leitung.TryGetValue(k, out var klassen) ? KontenService.ZustaendigText(string.Join(",", klassen)) : null;
                zeilen.Add(new SammelZeileDto(k, k, null, k, "neu",
                    leitet != null ? $"Klassenleitung {leitet} (laut SchILD)" : null, null, name));
                neueKonten.Add((new Benutzer
                {
                    Benutzername = k, Rolle = leitet != null ? BenutzerRolle.Klassenlehrer : BenutzerRolle.Lehrer,
                    LehrerKuerzel = k, Zustaendig = leitet,
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
                KontenService.Protokoll(_db, Ich, Aktion.Angelegt, konto, "Sammelanlage; " +
                    (konto.Rolle == BenutzerRolle.Klassenlehrer ? "Klassenleitung laut SchILD; " : "") +
                    KontenService.Zuordnung(konto));
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
    //  5.2c-2 (Entscheidung 192): Vorschläge aus SchILD – Klassenleitung laut Klassen.dat
    //  Nichts geschieht automatisch: übernehmen bzw. entfernen nur per Knopf, jedes Mal ins Kontenprotokoll.
    //  Konten ohne Kürzel (z. B. Testkonten) bleiben unberührt; Verwaltung und Stufenkoordination bekommen nur Hinweise
    //  (ein Konto hat eine Rolle).
    // -----------------------------------------------------------------

    [Authorize(Roles = Rollen.Admin)]
    [HttpGet("schild-vorschlaege")]
    public async Task<IActionResult> SchildVorschlaege() => Ok(await VorschlaegeAsync());

    /// <summary>
    /// Übernimmt die gewählten Vorschläge (Schlüssel aus GET). Gilt nur, was die API jetzt noch genauso vorschlagen würde;
    /// „übernehmen“ vor „entfernen“, damit keine Rolle zwischendurch herabgestuft wird.
    /// </summary>
    [Authorize(Roles = Rollen.Admin)]
    [HttpPost("schild-vorschlaege")]
    public async Task<IActionResult> SchildVorschlaegeAnwenden([FromBody] SchildVorschlaegeAnwendenDto dto)
    {
        var wahl = (dto.Schluessel ?? Array.Empty<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        if (wahl.Count == 0) return BadRequest(new { fehler = "Kein Vorschlag gewählt." });
        if (wahl.Count > SammelMax) return BadRequest(new { fehler = $"Höchstens {SammelMax} Vorschläge auf einmal." });

        var aktuell = (await VorschlaegeAsync()).Vorschlaege
            .Where(v => v.Schluessel.Length > 0).Select(v => v.Schluessel).ToHashSet(StringComparer.Ordinal);
        var reihe = wahl.Where(aktuell.Contains)
            .OrderBy(x => x.StartsWith("u|", StringComparison.Ordinal) ? 0 : 1).ThenBy(x => x, StringComparer.Ordinal).ToList();
        int nichtMehr = wahl.Count - reihe.Count;

        var konten = new Dictionary<int, (Benutzer Konto, string Vorher, List<string> Was)>();
        foreach (var key in reihe)
        {
            var teile = key.Split('|');
            if (teile.Length != 3 || !int.TryParse(teile[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                nichtMehr++;
                continue;
            }
            string klasse = teile[2];
            if (!konten.TryGetValue(id, out var eintrag))
            {
                var gefunden = await _db.Benutzer.FindAsync(id);
                if (gefunden == null) { nichtMehr++; continue; }
                eintrag = (gefunden, KontenService.Zuordnung(gefunden), new List<string>());
                konten[id] = eintrag;
            }
            var u = eintrag.Konto;
            var zust = KontenService.Zustaendigkeiten(u.Zustaendig);
            bool hat = zust.Contains(klasse, StringComparer.OrdinalIgnoreCase);
            if (teile[0] == "u")
            {
                if (u.Rolle == BenutzerRolle.Lehrer)
                {
                    u.Rolle = BenutzerRolle.Klassenlehrer;
                    u.Zustaendig = KontenService.ZustaendigText(klasse);
                }
                else if (u.Rolle == BenutzerRolle.Klassenlehrer && !hat)
                    u.Zustaendig = KontenService.ZustaendigText(string.Join(",", zust.Append(klasse)));
                else { nichtMehr++; continue; }
                eintrag.Was.Add($"Klassenleitung {klasse} laut SchILD übernommen");
            }
            else if (teile[0] == "e" && u.Rolle == BenutzerRolle.Klassenlehrer && hat)
            {
                var rest = zust.Where(z => !string.Equals(z, klasse, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rest.Count == 0)
                {
                    u.Rolle = BenutzerRolle.Lehrer;
                    u.Zustaendig = null;
                }
                else u.Zustaendig = string.Join(", ", rest);
                eintrag.Was.Add($"Zuständigkeit {klasse} laut SchILD entfernt");
            }
            else nichtMehr++;
        }

        var geaendert = new List<string>();
        int angewendet = 0;
        foreach (var (konto, vorher, was) in konten.Values)
        {
            if (was.Count == 0) continue;
            angewendet += was.Count;
            // Rolle und Zuständigkeit stehen im Token: alte Anmeldungen beenden (wie beim Bearbeiten).
            konto.SicherheitsStempel = KontenService.NeuerStempel();
            KontenService.Protokoll(_db, Ich, Aktion.Geaendert, konto,
                string.Join("; ", was) + $"; {vorher} → {KontenService.Zuordnung(konto)}");
            geaendert.Add(konto.Benutzername);
        }
        await _db.SaveChangesAsync();
        return Ok(new SchildVorschlaegeErgebnisDto(angewendet, nichtMehr, geaendert.OrderBy(x => x, Cmp).ToList()));
    }

    /// <summary>Klassen des aktuellen Schuljahrs (Grundplan von heute) und ihre Klassenleitung laut SchILD (Kürzel).</summary>
    private async Task<(string? Schuljahr, int KlassenGesamt, Dictionary<string, List<string>> Klassen)> KlassenleitungenAsync()
    {
        var leer = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var ver = await KlassenbuchService.AktuelleVersionAsync(_db);
        if (ver == null) return (null, 0, leer);
        string sj = ver.Schuljahr;
        // Wie das Stundenblatt: je Bezeichnung die Lerngruppe mit der größten Id.
        var klassen = (await _db.Lerngruppen.AsNoTracking()
                .Where(l => l.Typ == LerngruppeTyp.Klasse && l.Schuljahr == sj)
                .Select(l => new { l.Id, l.Bezeichnung, l.Klassenleitung })
                .ToListAsync())
            .GroupBy(l => l.Bezeichnung.Trim().ToUpperInvariant())
            .Select(g => g.OrderByDescending(x => x.Id).First())
            .ToList();
        foreach (var k in klassen)
        {
            var kuerzel = SchildImportService.KuerzelListe(k.Klassenleitung);
            if (kuerzel.Count > 0) leer[k.Bezeichnung.Trim().ToUpperInvariant()] = kuerzel;
        }
        return (sj, klassen.Count, leer);
    }

    /// <summary>Klassenleitung je Klasse → Klassen je Kürzel.</summary>
    private static Dictionary<string, List<string>> KlassenJeKuerzel(Dictionary<string, List<string>> klassen)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (klasse, kuerzel) in klassen.OrderBy(kv => kv.Key, Cmp))
            foreach (var k in kuerzel)
            {
                if (!d.TryGetValue(k, out var l)) d[k] = l = new List<string>();
                l.Add(klasse);
            }
        return d;
    }

    private async Task<SchildVorschlaegeDto> VorschlaegeAsync()
    {
        var (sj, gesamt, leitung) = await KlassenleitungenAsync();
        var liste = new List<SchildVorschlagDto>();
        if (sj == null) return new SchildVorschlaegeDto(null, 0, 0, liste);

        var personal = await _db.Benutzer.AsNoTracking()
            .Where(b => b.Rolle != BenutzerRolle.Schueler && b.LehrerKuerzel != null)
            .ToListAsync();
        string Kz(Benutzer b) => b.LehrerKuerzel!.Trim();

        // 1. Laut SchILD ist K Klassenleitung von X: Konto mit Kürzel K bekommt Rolle bzw. Zuständigkeit (übernehmen).
        foreach (var (klasse, kuerzel) in leitung.OrderBy(kv => kv.Key, Cmp))
            foreach (var k in kuerzel)
            {
                var treffer = personal.Where(b => string.Equals(Kz(b), k, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(b => b.Benutzername, Cmp).ToList();
                if (treffer.Count == 0)
                {
                    liste.Add(new SchildVorschlagDto("", "keinKonto", null, null, k, klasse,
                        $"Klassenleitung {klasse} ist laut SchILD {k} – dafür gibt es noch kein Konto (Reiter „Sammelanlage“, " +
                        "Lehrkräfte).", null, false));
                    continue;
                }
                foreach (var u in treffer)
                {
                    var zust = KontenService.Zustaendigkeiten(u.Zustaendig);
                    if (u.Rolle == BenutzerRolle.Lehrer)
                        liste.Add(new SchildVorschlagDto($"u|{u.Id}|{klasse}", "uebernehmen", u.Id, u.Benutzername, k, klasse,
                            $"{k} ist laut SchILD Klassenleitung {klasse} – Konto {u.Benutzername} ist bisher Lehrkraft.",
                            $"Rolle Lehrkraft → Klassenleitung, zuständig {klasse}", u.Gesperrt));
                    else if (u.Rolle == BenutzerRolle.Klassenlehrer)
                    {
                        if (zust.Contains(klasse, StringComparer.OrdinalIgnoreCase)) continue;   // passt schon
                        string bisher = zust.Count == 0 ? "–" : string.Join(", ", zust);
                        liste.Add(new SchildVorschlagDto($"u|{u.Id}|{klasse}", "uebernehmen", u.Id, u.Benutzername, k, klasse,
                            $"{k} ist laut SchILD Klassenleitung {klasse} – Konto {u.Benutzername} ist bisher zuständig für {bisher}.",
                            $"zuständig {bisher} → {KontenService.ZustaendigText(string.Join(",", zust.Append(klasse)))}",
                            u.Gesperrt));
                    }
                    else
                        liste.Add(new SchildVorschlagDto("", "hinweis", u.Id, u.Benutzername, k, klasse,
                            $"{k} ist laut SchILD Klassenleitung {klasse} – Konto {u.Benutzername} hat die Rolle " +
                            $"{KontenService.RollenText(u.Rolle)}. Ein Konto hat nur eine Rolle – bei Bedarf von Hand ändern.",
                            null, u.Gesperrt));
                }
            }

        // 2. Konto ist zuständig für X, laut SchILD leitet X aber jemand anderes (entfernen – nur einzeln).
        foreach (var u in personal.Where(b => b.Rolle == BenutzerRolle.Klassenlehrer).OrderBy(b => b.Benutzername, Cmp))
        {
            var zust = KontenService.Zustaendigkeiten(u.Zustaendig);
            foreach (var z in zust)
            {
                if (!leitung.TryGetValue(z, out var laut)) continue;   // keine Angabe aus SchILD: bleibt
                if (laut.Any(k => string.Equals(k, Kz(u), StringComparison.OrdinalIgnoreCase))) continue;
                var rest = zust.Where(x => !string.Equals(x, z, StringComparison.OrdinalIgnoreCase)).ToList();
                liste.Add(new SchildVorschlagDto($"e|{u.Id}|{z}", "entfernen", u.Id, u.Benutzername, Kz(u), z,
                    $"Konto {u.Benutzername} ({Kz(u)}) ist zuständig für {z}, laut SchILD ist die Klassenleitung " +
                    $"{string.Join(", ", laut)}. Ist es eine Stellvertretung von Hand, so lassen.",
                    rest.Count == 0 ? "Rolle Klassenleitung → Lehrkraft" : $"zuständig {string.Join(", ", zust)} → {string.Join(", ", rest)}",
                    u.Gesperrt));
            }
        }

        var rang = new Dictionary<string, int> { ["uebernehmen"] = 0, ["entfernen"] = 1, ["hinweis"] = 2, ["keinKonto"] = 3 };
        return new SchildVorschlaegeDto(sj, gesamt, leitung.Count,
            liste.OrderBy(v => rang[v.Art]).ThenBy(v => v.Klasse, Cmp).ThenBy(v => v.Konto ?? "", Cmp).ToList());
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
        var schuelerIds = konten.Where(k => k.SchuelerId != null).Select(k => k.SchuelerId!.Value).Distinct().ToList();
        var info = await KontenService.SchuelerInfoAsync(_db, schuelerIds);
        // 5.2c-2 (197): Name und Stand laut SchILD für „wieder aktiv, aber gesperrt“ und „Benutzername passt nicht mehr“.
        var stand = schuelerIds.Count == 0
            ? new Dictionary<int, (string Vorname, string Nachname, bool Importiert, bool Abgang)>()
            : (await _db.Schueler.AsNoTracking().Where(x => schuelerIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.Vorname, x.Nachname, x.ExtId, x.Status }).ToListAsync())
                .ToDictionary(x => x.Id, x => (x.Vorname, x.Nachname, Importiert: !string.IsNullOrEmpty(x.ExtId),
                    Abgang: x.Status == "abgang"));
        // 5.2c-2 (194): Namen der Lehrkräfte nur für den Admin.
        var namen = IstAdmin
            ? await LehrkraftNamenAsync(konten.Where(k => k.Rolle != BenutzerRolle.Schueler && k.LehrerKuerzel != null)
                .Select(k => k.LehrerKuerzel!))
            : new Dictionary<string, string>();
        var ich = IchId;
        return konten.Select(u =>
        {
            (string Name, string? Klasse, string Stufe)? s =
                u.SchuelerId is int sid && info.TryGetValue(sid, out var x) ? x : null;
            (string Vorname, string Nachname, bool Importiert, bool Abgang)? st =
                u.Rolle == BenutzerRolle.Schueler && u.SchuelerId is int sid2 && stand.TryGetValue(sid2, out var y) ? y : null;
            bool importiert = st.HasValue && st.Value.Importiert;
            return new KontoDto(
                u.Id, u.Benutzername, u.Rolle.ToString(), KontenService.RollenText(u.Rolle),
                u.LehrerKuerzel, u.Zustaendig, u.SchuelerId,
                s?.Name, s == null ? null : s.Value.Klasse ?? s.Value.Stufe,
                Verwaist: u.Rolle == BenutzerRolle.Schueler && s == null,
                u.Gesperrt, u.MussPasswortAendern, u.AngelegtAm, u.LetzteAnmeldung, u.PasswortGeaendertAm,
                Ich: u.Id == ich,
                Lehrkraft: u.Rolle != BenutzerRolle.Schueler && u.LehrerKuerzel != null
                           && namen.TryGetValue(u.LehrerKuerzel.Trim(), out var n) ? n : null,
                NameGeaendert: importiert && NamePasstNicht(u.Benutzername, st!.Value.Vorname, st.Value.Nachname),
                WiederAktiv: u.Gesperrt && importiert && !st!.Value.Abgang);
        }).ToList();
    }

    // „vorname.nachname“ bzw. „vorname.nachname2“ – so bildet die App Benutzernamen für Schüler (PasswortRegeln.NameVorschlag).
    private static readonly Regex GebildeterName = new(@"^[a-z0-9-]+\.[a-z0-9-]+$", RegexOptions.Compiled);

    /// <summary>
    /// 5.2c-2 (197): Der Benutzername ist nach dem Muster „vorname.nachname“ gebildet, passt aber nicht mehr zum Namen laut
    /// SchILD (z. B. nach einer Namensänderung). Selbst gewählte Namen wie „s05A“ bleiben unberührt.
    /// </summary>
    private static bool NamePasstNicht(string benutzername, string vorname, string nachname)
    {
        var name = benutzername.Trim().ToLowerInvariant();
        if (!GebildeterName.IsMatch(name)) return false;
        var basis = PasswortRegeln.NameVorschlag(vorname, nachname);
        return !Regex.IsMatch(name, "^" + Regex.Escape(basis) + @"\d*$");
    }

    /// <summary>5.2c-2 (194): „Vorname Nachname“ je Kürzel laut Lehrkraefte.dat (echte Namen vor Pseudo-Namen).</summary>
    private async Task<Dictionary<string, string>> LehrkraftNamenAsync(IEnumerable<string> kuerzel)
    {
        var liste = kuerzel.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ergebnis = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (liste.Count == 0) return ergebnis;
        var klein = liste.Select(k => k.ToLower()).ToList();
        var treffer = await _db.Lehrkraefte.AsNoTracking()
            .Where(l => klein.Contains(l.Kuerzel.ToLower()))
            .Select(l => new { l.Kuerzel, l.Vorname, l.Nachname, l.IstPseudo })
            .ToListAsync();
        foreach (var l in treffer.OrderBy(t => t.IstPseudo))
            ergebnis.TryAdd(l.Kuerzel.Trim(), $"{l.Vorname} {l.Nachname}".Trim());
        return ergebnis;
    }

    /// <summary>„Mia Müller" → „Müller Mia" (zum Sortieren nach Nachnamen).</summary>
    private static string? NachnameZuerst(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var i = name.LastIndexOf(' ');
        return i < 0 ? name : name[(i + 1)..] + " " + name[..i];
    }
}
