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

namespace VertretungApi.Controllers;

// =====================================================================
//  Auth (Phase 1C): Login → JWT. Rollen Admin/Lehrer/Schüler/
//  Klassenlehrer/Stufenkoordination. seed-konten legt Testkonten an.
// =====================================================================

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public AuthController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public record LoginDto(string Benutzername, string Passwort);

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var u = await _db.Benutzer.FirstOrDefaultAsync(b => b.Benutzername == dto.Benutzername);
        if (u == null || !PasswortHasher.Verify(dto.Passwort, u.PasswortHash))
            return Unauthorized("Benutzername oder Passwort falsch.");

        var token = ErzeugeToken(u);
        return Ok(new
        {
            token,
            benutzername = u.Benutzername,
            rolle = u.Rolle.ToString(),
            lehrerKuerzel = u.LehrerKuerzel
        });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        benutzername = User.Identity?.Name,
        rolle = User.FindFirst(ClaimTypes.Role)?.Value,
        lehrerKuerzel = User.FindFirst("lehrerKuerzel")?.Value
    });

    /// <summary>Legt Standard-Testkonten an (idempotent). Geschützt per X-Api-Key.</summary>
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
        await _db.SaveChangesAsync();
        return Ok(ergebnis);
    }

    private string ErzeugeToken(Benutzer u)
    {
        var key = _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key fehlt.");
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, u.Benutzername),
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Role, u.Rolle.ToString()),
        };
        if (!string.IsNullOrWhiteSpace(u.LehrerKuerzel))
            claims.Add(new Claim("lehrerKuerzel", u.LehrerKuerzel));

        double stunden = double.TryParse(_cfg["Jwt:StundenGueltig"], out var h) ? h : 12;
        var token = new JwtSecurityToken(
            issuer: _cfg["Jwt:Issuer"],
            audience: _cfg["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(stunden),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
