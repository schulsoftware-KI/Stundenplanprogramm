using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Antwort einer erfolgreichen Anmeldung (Token + Kontodaten für die App) – bis P6a in AuthController, seit P6a
//  gemeinsam für Anmeldung, Passwortänderung und den zweiten Faktor (Einrichten, Ausschalten melden die übrigen
//  Geräte ab und liefern ein neues Token für dieses Gerät).
//
//  P6a (Entscheidungen 226, 228): Admin-Konten ohne „Angemeldet bleiben“ (höchstens „Dauer einer Anmeldung“);
//  dazu Stand des zweiten Faktors, ob er erst eingerichtet werden muss, und die erfolglosen Anmeldeversuche seit der
//  letzten Anmeldung (Hinweis in der App, einmal).
// =====================================================================

/// <summary>Was nur die Anmeldung selbst mitbringt.</summary>
public sealed record AnmeldeZusatz(int Fehlversuche = 0, DateTime? FehlversuchZuletzt = null, int? WiederherstellungRest = null);

public sealed class Anmeldedienst
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly Schuleinstellungen _einst;
    private readonly IHostEnvironment _env;

    public Anmeldedienst(AppDbContext db, IConfiguration cfg, Schuleinstellungen einst, IHostEnvironment env)
    {
        _db = db;
        _cfg = cfg;
        _einst = einst;
        _env = env;
    }

    public bool Produktiv => Betrieb.Produktiv(_env);

    public string JwtKey => _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key fehlt.");

    /// <summary>Antwort von Anmeldung, Passwortänderung und zweitem Faktor: Token + Kontodaten für die App.</summary>
    public async Task<object> AntwortAsync(Benutzer u, bool angemeldetBleiben, AnmeldeZusatz? zusatz = null)
    {
        // P3 (E-1, Entscheidung 200): Dauer aus den Schuleinstellungen; 0 Tage = kein „Angemeldet bleiben“.
        // P6a (226, Frage B): Admin-Konten bleiben nie angemeldet.
        var w = await _einst.LesenAsync(_db);
        bool admin = u.Rolle == BenutzerRolle.Admin;
        bool angeboten = angemeldetBleiben && w.AngemeldetBleibenTage > 0;
        bool bleiben = angeboten && !admin;
        var gueltigBis = bleiben
            ? DateTime.UtcNow.AddDays(w.AngemeldetBleibenTage)
            : DateTime.UtcNow.AddHours(w.SitzungStunden);

        var pflichten = await _einst.FaktorPflichtAsync(_db);
        bool moeglich = ZweiterFaktor.Moeglich(u.Rolle);
        bool aktiv = ZweiterFaktor.Aktiv(u);
        int rest = ZweiterFaktor.RestCodes(u);

        return new
        {
            token = Token(u, gueltigBis),
            gueltigBis,
            angemeldetBleiben = bleiben,
            benutzername = u.Benutzername,
            rolle = u.Rolle.ToString(),
            lehrerKuerzel = u.LehrerKuerzel,
            schuelerId = u.SchuelerId,
            anzeige = await AnzeigeName(u),
            name = await LehrkraftName(u),        // 5.2c-2 (194): eigener Name laut Lehrkraefte.dat (nur fürs Kontomenü)
            zustaendig = KontenService.ZustaendigText(u.Zustaendig),
            mussPasswortAendern = u.MussPasswortAendern,
            passwortRegel = KontenService.Regeln(u.Rolle),
            // ---- P6a ----
            zweiterFaktor = moeglich
                ? new { aktiv, seit = u.ZweiterFaktorSeit, restCodes = rest, pflicht = ZweiterFaktor.Pflicht(u.Rolle, pflichten, Produktiv) }
                : null,
            faktorEinrichten = u.MussPasswortAendern ? null : ZweiterFaktor.MussEinrichten(u, pflichten, Produktiv),
            bleibenAbgelehnt = admin && angeboten,
            fehlversuche = zusatz is { Fehlversuche: > 0 }
                ? new { anzahl = zusatz.Fehlversuche, zuletzt = zusatz.FehlversuchZuletzt }
                : null,
            wiederherstellungRest = zusatz?.WiederherstellungRest
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

    /// <summary>
    /// 5.2c-2 (Entscheidung 194): Name der Lehrkraft laut Lehrkraefte.dat („Anna Schulz“) – nur für das eigene Konto
    /// (Kontomenü „L23 · Anna Schulz“). Schüler und Konten ohne Kürzel: null.
    /// </summary>
    private async Task<string?> LehrkraftName(Benutzer u)
    {
        if (u.Rolle == BenutzerRolle.Schueler || string.IsNullOrWhiteSpace(u.LehrerKuerzel)) return null;
        var k = u.LehrerKuerzel.Trim().ToLower();
        var l = await _db.Lehrkraefte.AsNoTracking()
            .Where(x => x.Kuerzel.ToLower() == k)
            .OrderBy(x => x.IstPseudo).ThenBy(x => x.Id)
            .Select(x => new { x.Vorname, x.Nachname })
            .FirstOrDefaultAsync();
        return l == null ? null : $"{l.Vorname} {l.Nachname}".Trim();
    }

    public string Token(Benutzer u, DateTime gueltigBis)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256);

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
}
