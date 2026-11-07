using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;

namespace VertretungApi.Controllers;

// =====================================================================
//  Auth (Phase 1C, erweitert in Phase 2 Schritt 4):
//    GET  api/auth/optionen                – braucht die App eine Anmeldung? (offen)
//    POST api/auth/login                   – Login -> JWT (mit „angemeldet bleiben")
//    GET  api/auth/me                      – wer bin ich (JWT)
//    POST api/auth/ueberall-abmelden       – eigene Tokens auf allen Geräten ungültig machen
//    POST api/auth/abmelden/{benutzername} – dasselbe für ein anderes Konto (Admin)
//    POST api/auth/passwort                – eigenes Passwort ändern (Schritt 5) -> neues JWT
//    POST api/auth/seed-konten             – Testkonten anlegen (X-Api-Key, nur Entwicklung)
//
//  Schritt 5: Gesperrte Konten können sich nicht anmelden. Konten mit Startpasswort bzw.
//  zurückgesetztem Passwort (MussPasswortAendern) dürfen nach der Anmeldung nur das
//  Passwort ändern (Sperre in Program.cs). Kontenverwaltung: KontenController.
//
//  Jedes Token trägt den Sicherheitsstempel des Kontos; Program.cs vergleicht ihn bei
//  jeder Anfrage mit der Datenbank. Neuer Stempel = alle alten Tokens ungültig.
// =====================================================================

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly LoginBremse _bremse;

    // Vergleichs-Hash für unbekannte Benutzernamen: Die Antwort dauert dann genauso lange
    // wie bei einem falschen Passwort, verrät also nicht, ob es das Konto gibt.
    private static readonly string Attrappe = PasswortHasher.Hash(Guid.NewGuid().ToString());

    public AuthController(AppDbContext db, IConfiguration cfg, LoginBremse bremse)
    {
        _db = db;
        _cfg = cfg;
        _bremse = bremse;
    }

    public record LoginDto(string Benutzername, string Passwort, bool AngemeldetBleiben = false);

    /// <summary>Ob die Pläne nur mit Anmeldung abrufbar sind (Schalter "Woche:OhneLogin").</summary>
    [AllowAnonymous]
    [HttpGet("optionen")]
    public IActionResult Optionen() => Ok(new
    {
        anmeldungNoetig = !_cfg.GetValue<bool>("Woche:OhneLogin")
    });

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var name = (dto.Benutzername ?? "").Trim();
        var adresse = HttpContext.Connection.RemoteIpAddress;

        var warten = _bremse.Gesperrt(name, adresse);
        if (warten is TimeSpan w)
        {
            int sek = Math.Max(1, (int)Math.Ceiling(w.TotalSeconds));
            Response.Headers.RetryAfter = sek.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                fehler = $"Zu viele Fehlversuche. Bitte in {WarteText(sek)} erneut versuchen.",
                sekunden = sek
            });
        }

        // Benutzername ohne Groß-/Kleinschreibung („l1" = „L1").
        var klein = name.ToLowerInvariant();
        var u = await _db.Benutzer.FirstOrDefaultAsync(b => b.Benutzername.ToLower() == klein);
        bool ok = PasswortHasher.Verify(dto.Passwort ?? "", u?.PasswortHash ?? Attrappe) && u != null;
        if (!ok || u == null)
        {
            _bremse.Fehlschlag(name, adresse);
            return Unauthorized(new { fehler = "Benutzername oder Passwort falsch." });
        }
        _bremse.Erfolg(name);

        // Erst nach richtigem Passwort verraten, dass das Konto gesperrt ist.
        if (u.Gesperrt)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                fehler = "Dieses Konto ist gesperrt. Bitte wende dich an das Sekretariat.",
                gesperrt = true
            });

        u.LetzteAnmeldung = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(await LoginAntwort(u, dto.AngemeldetBleiben));
    }

    // string? statt string: Leere Eingaben bekommen so unsere verständliche Meldung statt der Standard-Validierung.
    public record PasswortDto(string? AltesPasswort, string? NeuesPasswort, bool AngemeldetBleiben = false);

    /// <summary>
    /// Eigenes Passwort ändern (Phase 2, Schritt 5). Danach sind alle bisherigen Anmeldungen
    /// (andere Geräte) beendet; die Antwort enthält ein neues Token für dieses Gerät.
    /// Falsches bisheriges Passwort -> 400 (nicht 401, damit die App angemeldet bleibt),
    /// zählt aber für die Login-Bremse.
    /// </summary>
    [Authorize]
    [HttpPost("passwort")]
    public async Task<IActionResult> PasswortAendern([FromBody] PasswortDto dto)
    {
        var id = Rollen.BenutzerId(User);
        var u = id == null ? null : await _db.Benutzer.FindAsync(id.Value);
        if (u == null) return Unauthorized();
        var adresse = HttpContext.Connection.RemoteIpAddress;

        var warten = _bremse.Gesperrt(u.Benutzername, adresse);
        if (warten is TimeSpan w)
        {
            int sek = Math.Max(1, (int)Math.Ceiling(w.TotalSeconds));
            Response.Headers.RetryAfter = sek.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                fehler = $"Zu viele Fehlversuche. Bitte in {WarteText(sek)} erneut versuchen.",
                sekunden = sek
            });
        }

        var alt = dto.AltesPasswort ?? "";
        if (!PasswortHasher.Verify(alt, u.PasswortHash))
        {
            _bremse.Fehlschlag(u.Benutzername, adresse);
            return BadRequest(new { fehler = "Das bisherige Passwort stimmt nicht.", feld = "alt" });
        }
        _bremse.Erfolg(u.Benutzername);

        var fehler = PasswortRegeln.Pruefen(dto.NeuesPasswort, u.Rolle,
            await KontenService.VerboteneNamenAsync(_db, u), alt);
        if (fehler != null) return BadRequest(new { fehler, feld = "neu" });

        u.PasswortHash = PasswortHasher.Hash(dto.NeuesPasswort!);   // von Pruefen als nicht leer bestätigt
        u.MussPasswortAendern = false;
        u.PasswortGeaendertAm = DateTime.UtcNow;
        u.SicherheitsStempel = NeuerStempel();       // andere Geräte abmelden
        KontenService.Protokoll(_db, u.Benutzername, KontenService.Aktion.PasswortGeaendert, u);
        await _db.SaveChangesAsync();

        return Ok(await LoginAntwort(u, dto.AngemeldetBleiben));
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        benutzername = User.Identity?.Name,
        rolle = User.FindFirst(ClaimTypes.Role)?.Value,
        lehrerKuerzel = Rollen.Kuerzel(User),
        mussPasswortAendern = User.HasClaim(Rollen.PasswortPflichtClaim, "1")
    });

    /// <summary>Meldet das eigene Konto auf allen Geräten ab (auch auf diesem).</summary>
    [Authorize]
    [HttpPost("ueberall-abmelden")]
    public async Task<IActionResult> UeberallAbmelden()
    {
        var id = Rollen.BenutzerId(User);
        var u = id == null ? null : await _db.Benutzer.FindAsync(id.Value);
        if (u == null) return Unauthorized();
        u.SicherheitsStempel = NeuerStempel();
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Meldet ein anderes Konto auf allen Geräten ab (z. B. verlorenes Handy). Nur Admin.</summary>
    [Authorize(Roles = Rollen.Admin)]
    [HttpPost("abmelden/{benutzername}")]
    public async Task<IActionResult> KontoAbmelden(string benutzername)
    {
        var klein = benutzername.Trim().ToLowerInvariant();
        var u = await _db.Benutzer.FirstOrDefaultAsync(b => b.Benutzername.ToLower() == klein);
        if (u == null) return NotFound(new { fehler = "Konto nicht gefunden." });
        u.SicherheitsStempel = NeuerStempel();
        KontenService.Protokoll(_db, User.Identity?.Name ?? "", KontenService.Aktion.Abgemeldet, u);
        await _db.SaveChangesAsync();
        return Ok(new { u.Benutzername, abgemeldet = true });
    }

    /// <summary>
    /// Legt Standard-Testkonten an (idempotent). Geschützt per X-Api-Key.
    /// Schülerkonten werden mit einem Pseudo-Schüler verknüpft (vorher /api/seed/pseudodaten);
    /// nach erneutem Seeden der Pseudodaten einfach noch einmal aufrufen – die Konten werden
    /// dann neu verknüpft.
    /// </summary>
    // Phase 5.1: nur in der Entwicklung (im Echtbetrieb 404) und nur, solange keine echten
    // Schüler in der Datenbank stehen – Testkonten mit bekannten Passwörtern gehören nie in
    // eine Datenbank mit echten Daten. Das erste Admin-Konto im Echtbetrieb entsteht über
    // den Befehl „admin-anlegen“ (Services/Notfallzugang.cs).
    [NurEntwicklung]
    [AllowAnonymous]
    [HttpPost("seed-konten")]
    public async Task<IActionResult> SeedKonten()
    {
        if (!ApiSchluessel.Gueltig(Request, _cfg))      // Phase 5.1: Vergleich in konstanter Zeit
            return Unauthorized(ApiSchluessel.Fehlertext);
        if (await _db.Schueler.AnyAsync(x => !x.IstPseudo))
            return Conflict(new { fehler = "Es sind echte Schülerdaten vorhanden – Testkonten werden nicht angelegt." });

        // Testkonten mit bewusst einfachen Passwörtern (nur Entwicklung, ohne Pflicht zum Ändern).
        var standard = new (string Name, string Pw, BenutzerRolle Rolle, string? Kuerzel, string? Zustaendig)[]
        {
            ("admin", "admin123",  BenutzerRolle.Admin, null, null),
            ("L1",    "lehrer123", BenutzerRolle.Lehrer, "L1", null),
            ("L2",    "lehrer123", BenutzerRolle.Lehrer, "L2", null),
            ("kl05A", "kl123",     BenutzerRolle.Klassenlehrer, "L1", "05A"),
            ("koQ1",  "ko123",     BenutzerRolle.Stufenkoordination, "L2", "Q1"),   // Schritt 5
        };

        var ergebnis = new List<object>();
        foreach (var k in standard)
        {
            var konto = await _db.Benutzer.FirstOrDefaultAsync(b => b.Benutzername == k.Name);
            if (konto == null)
            {
                _db.Benutzer.Add(new Benutzer
                {
                    Benutzername = k.Name,
                    PasswortHash = PasswortHasher.Hash(k.Pw),
                    Rolle = k.Rolle,
                    LehrerKuerzel = k.Kuerzel,
                    Zustaendig = k.Zustaendig,
                    AngelegtAm = DateTime.UtcNow
                });
            }
            else if (k.Zustaendig != null && string.IsNullOrWhiteSpace(konto.Zustaendig))
            {
                konto.Zustaendig = k.Zustaendig;   // ältere Testkonten (vor Schritt 5) nachtragen
            }
            ergebnis.Add(new { k.Name, rolle = k.Rolle.ToString(), angelegt = konto == null, zustaendig = k.Zustaendig });
        }

        // Pseudo-Schülerkonten: ein Schüler der Klasse 05A (Sek I) und einer der Q1 (Sek II).
        var schuelerKonten = new (string Name, string Pw, string Gruppe)[]
        {
            ("s05A", "schueler123", "05A"),
            ("sQ1",  "schueler123", "Q1"),
        };
        foreach (var k in schuelerKonten)
        {
            var s = await FindePseudoSchueler(k.Gruppe);
            var konto = await _db.Benutzer.FirstOrDefaultAsync(b => b.Benutzername == k.Name);
            if (s == null)
            {
                ergebnis.Add(new
                {
                    k.Name, rolle = "Schueler", angelegt = false,
                    hinweis = $"Kein Pseudo-Schüler für {k.Gruppe} gefunden – zuerst /api/seed/pseudodaten ausführen."
                });
                continue;
            }

            bool neu = konto == null;
            bool neuVerknuepft = false;
            if (konto == null)
            {
                _db.Benutzer.Add(new Benutzer
                {
                    Benutzername = k.Name,
                    PasswortHash = PasswortHasher.Hash(k.Pw),
                    Rolle = BenutzerRolle.Schueler,
                    SchuelerId = s.Id,
                    AngelegtAm = DateTime.UtcNow
                });
            }
            else if (konto.SchuelerId != s.Id)
            {
                konto.SchuelerId = s.Id;
                konto.SicherheitsStempel = NeuerStempel();   // alte Anmeldungen zeigten den alten Schüler
                neuVerknuepft = true;
            }
            ergebnis.Add(new
            {
                k.Name, rolle = "Schueler", angelegt = neu, neuVerknuepft,
                schueler = $"{s.Vorname} {s.Nachname} ({k.Gruppe}, Id {s.Id})"
            });
        }

        await _db.SaveChangesAsync();
        return Ok(ergebnis);
    }

    // -----------------------------------------------------------------

    /// <summary>Erster Pseudo-Schüler einer Klasse (z. B. "05A") bzw. einer Stufe (z. B. "Q1").</summary>
    private async Task<Schueler?> FindePseudoSchueler(string gruppe)
    {
        var inKlasse = await _db.Zugehoerigkeiten
            .Where(z => z.Lerngruppe!.Typ == LerngruppeTyp.Klasse && z.Lerngruppe.Bezeichnung == gruppe
                        && z.Schueler!.IstPseudo)
            .OrderBy(z => z.SchuelerId)
            .Select(z => z.Schueler!)
            .FirstOrDefaultAsync();
        if (inKlasse != null) return inKlasse;

        return await _db.Schueler
            .Where(s => s.IstPseudo && s.Stufe == gruppe && s.Zugehoerigkeiten.Any())
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>Antwort von Login und Passwortänderung: Token + Kontodaten für die App.</summary>
    private async Task<object> LoginAntwort(Benutzer u, bool angemeldetBleiben)
    {
        var gueltigBis = angemeldetBleiben
            ? DateTime.UtcNow.AddDays(Zahl("Jwt:TageAngemeldetBleiben", 30))
            : DateTime.UtcNow.AddHours(Zahl("Jwt:StundenGueltig", 12));

        return new
        {
            token = ErzeugeToken(u, gueltigBis),
            gueltigBis,
            benutzername = u.Benutzername,
            rolle = u.Rolle.ToString(),
            lehrerKuerzel = u.LehrerKuerzel,
            schuelerId = u.SchuelerId,
            anzeige = await AnzeigeName(u),
            zustaendig = KontenService.ZustaendigText(u.Zustaendig),
            mussPasswortAendern = u.MussPasswortAendern,
            passwortRegel = KontenService.Regeln(u.Rolle)
        };
    }

    private async Task<string> AnzeigeName(Benutzer u)
    {
        if (u.Rolle == BenutzerRolle.Schueler && u.SchuelerId is int sid)
        {
            var s = await _db.Schueler.FindAsync(sid);
            if (s != null) return $"{s.Vorname} {s.Nachname}";
        }
        return string.IsNullOrWhiteSpace(u.LehrerKuerzel) ? u.Benutzername : u.LehrerKuerzel;
    }

    private string ErzeugeToken(Benutzer u, DateTime gueltigBis)
    {
        var key = _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key fehlt.");
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, u.Benutzername),
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Role, u.Rolle.ToString()),
            new(Rollen.StempelClaim, u.SicherheitsStempel),
        };
        if (!string.IsNullOrWhiteSpace(u.LehrerKuerzel))
            claims.Add(new Claim(Rollen.KuerzelClaim, u.LehrerKuerzel));

        var token = new JwtSecurityToken(
            issuer: _cfg["Jwt:Issuer"],
            audience: _cfg["Jwt:Audience"],
            claims: claims,
            expires: gueltigBis,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private double Zahl(string schluessel, double ersatz)
        => double.TryParse(_cfg[schluessel], System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out var z) && z > 0 ? z : ersatz;

    private static string NeuerStempel() => KontenService.NeuerStempel();

    private static string WarteText(int sekunden)
        => sekunden < 60 ? $"{sekunden} Sekunden"
         : sekunden < 120 ? "einer Minute"
         : $"{(int)Math.Ceiling(sekunden / 60.0)} Minuten";
}
