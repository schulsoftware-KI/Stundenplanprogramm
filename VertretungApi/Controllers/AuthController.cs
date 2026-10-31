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
//    POST api/auth/seed-konten             – Testkonten anlegen (X-Api-Key)
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

        var gueltigBis = dto.AngemeldetBleiben
            ? DateTime.UtcNow.AddDays(Zahl("Jwt:TageAngemeldetBleiben", 30))
            : DateTime.UtcNow.AddHours(Zahl("Jwt:StundenGueltig", 12));

        return Ok(new
        {
            token = ErzeugeToken(u, gueltigBis),
            gueltigBis,
            benutzername = u.Benutzername,
            rolle = u.Rolle.ToString(),
            lehrerKuerzel = u.LehrerKuerzel,
            schuelerId = u.SchuelerId,
            anzeige = await AnzeigeName(u)
        });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        benutzername = User.Identity?.Name,
        rolle = User.FindFirst(ClaimTypes.Role)?.Value,
        lehrerKuerzel = Rollen.Kuerzel(User)
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
        await _db.SaveChangesAsync();
        return Ok(new { u.Benutzername, abgemeldet = true });
    }

    /// <summary>
    /// Legt Standard-Testkonten an (idempotent). Geschützt per X-Api-Key.
    /// Schülerkonten werden mit einem Pseudo-Schüler verknüpft (vorher /api/seed/pseudodaten);
    /// nach erneutem Seeden der Pseudodaten einfach noch einmal aufrufen – die Konten werden
    /// dann neu verknüpft.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("seed-konten")]
    public async Task<IActionResult> SeedKonten()
    {
        var erwartet = _cfg["Api:PushKey"];
        if (!string.IsNullOrEmpty(erwartet))
        {
            if (!Request.Headers.TryGetValue("X-Api-Key", out var key) || key != erwartet)
                return Unauthorized("Ungültiger oder fehlender X-Api-Key.");
        }

        var standard = new (string Name, string Pw, BenutzerRolle Rolle, string? Kuerzel)[]
        {
            ("admin", "admin123", BenutzerRolle.Admin, null),
            ("L1",    "lehrer123", BenutzerRolle.Lehrer, "L1"),
            ("L2",    "lehrer123", BenutzerRolle.Lehrer, "L2"),
            ("kl05A", "kl123",     BenutzerRolle.Klassenlehrer, "L1"),
        };

        var ergebnis = new List<object>();
        foreach (var k in standard)
        {
            var vorhanden = await _db.Benutzer.AnyAsync(b => b.Benutzername == k.Name);
            if (!vorhanden)
            {
                _db.Benutzer.Add(new Benutzer
                {
                    Benutzername = k.Name,
                    PasswortHash = PasswortHasher.Hash(k.Pw),
                    Rolle = k.Rolle,
                    LehrerKuerzel = k.Kuerzel
                });
            }
            ergebnis.Add(new { k.Name, rolle = k.Rolle.ToString(), angelegt = !vorhanden });
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
                    SchuelerId = s.Id
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

    private static string NeuerStempel() => Guid.NewGuid().ToString("N");

    private static string WarteText(int sekunden)
        => sekunden < 60 ? $"{sekunden} Sekunden"
         : sekunden < 120 ? "einer Minute"
         : $"{(int)Math.Ceiling(sekunden / 60.0)} Minuten";
}
