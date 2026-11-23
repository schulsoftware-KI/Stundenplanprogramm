using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;
using Aktion = VertretungApi.Services.KontenService.Aktion;

namespace VertretungApi.Controllers;

// =====================================================================
//  P6a – zweiter Faktor des eigenen Kontos (Entscheidungen 225–227), nur Personalkonten:
//
//    GET  api/auth/zweiter-faktor               – Stand: aktiv seit, Wiederherstellungscodes, Pflicht, abschaltbar
//    POST api/auth/zweiter-faktor/beginnen      – { passwort, code? } -> { merker, schluessel, uri } (10 Minuten);
//                                                 ist schon einer aktiv, braucht es zusätzlich einen Code (neues Handy)
//    POST api/auth/zweiter-faktor/einrichten    – { merker, code } -> { codes, anmeldung } – aktiv; alle anderen Geräte
//                                                 sind abgemeldet (neuer Stempel), die Antwort enthält ein neues Token
//    POST api/auth/zweiter-faktor/codes         – { passwort, code } -> { codes } – ersetzt alle Wiederherstellungscodes
//    POST api/auth/zweiter-faktor/ausschalten   – { passwort, code } -> { anmeldung } – nur ohne Pflicht für die Rolle
//
//  Das Geheimnis steht nur im versiegelten Merker und in der Antwort von „beginnen“ (QR-Code und Schlüssel zum
//  Abtippen) – nach der Einrichtung wird es nie wieder angezeigt. Wiederherstellungscodes erscheinen nur einmal.
//  Falsches Passwort bzw. falscher Code: 400 (die App bleibt angemeldet), zählt aber in der Login-Bremse. Alles steht
//  im Kontenprotokoll (ohne Geheimnis und Codes). Zurücksetzen durch einen Admin: KontenController.
// =====================================================================

[ApiController]
[Route("api/auth/zweiter-faktor")]
[Authorize(Roles = Rollen.Personal)]
public class ZweiterFaktorController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LoginBremse _bremse;
    private readonly Schuleinstellungen _einst;
    private readonly Anmeldedienst _anmeldung;

    public ZweiterFaktorController(AppDbContext db, LoginBremse bremse, Schuleinstellungen einst, Anmeldedienst anmeldung)
    {
        _db = db;
        _bremse = bremse;
        _einst = einst;
        _anmeldung = anmeldung;
    }

    public record PruefenDto(string? Passwort, string? Code);
    public record EinrichtenDto(string? Merker, string? Code, bool AngemeldetBleiben = false);
    public record AusschaltenDto(string? Passwort, string? Code, bool AngemeldetBleiben = false);

    [HttpGet]
    public async Task<IActionResult> Stand()
    {
        var u = await IchAsync();
        if (u == null) return Unauthorized();
        return Ok(await StandAsync(u));
    }

    private async Task<object> StandAsync(Benutzer u)
    {
        var pflichten = await _einst.FaktorPflichtAsync(_db);
        bool aktiv = ZweiterFaktor.Aktiv(u);
        int rest = ZweiterFaktor.RestCodes(u);
        bool pflicht = ZweiterFaktor.Pflicht(u.Rolle, pflichten, _anmeldung.Produktiv);
        return new
        {
            aktiv,
            seit = u.ZweiterFaktorSeit,
            restCodes = rest,
            pflicht,
            abschaltbar = aktiv && !pflicht,
            codeNoetig = aktiv && rest > 0,        // „beginnen“ braucht dann zusätzlich einen Code
            faktorEinrichten = ZweiterFaktor.MussEinrichten(u, pflichten, _anmeldung.Produktiv)
        };
    }

    /// <summary>
    /// Einrichtung beginnen: neues Geheimnis (160 Bit) im versiegelten Merker, dazu QR-Inhalt und Schlüssel zum
    /// Abtippen. Ist schon ein zweiter Faktor aktiv (neues Handy), braucht es zusätzlich einen Code bzw.
    /// Wiederherstellungscode – außer es ist keiner mehr übrig.
    /// </summary>
    [HttpPost("beginnen")]
    public async Task<IActionResult> Beginnen([FromBody] PruefenDto dto)
    {
        var u = await IchAsync();
        if (u == null) return Unauthorized();
        bool codeNoetig = ZweiterFaktor.Aktiv(u) && ZweiterFaktor.RestCodes(u) > 0;
        var fehler = Pruefen(u, dto?.Passwort, dto?.Code, codeNoetig);
        if (fehler != null) return fehler;
        await _db.SaveChangesAsync();   // benutzter Code (jeder gilt nur einmal)

        var b32 = Totp.Base32(Totp.NeuesGeheimnis());
        var merker = ZweiterFaktor.Versiegeln(_anmeldung.JwtKey, ZweiterFaktor.ZweckEinrichtung,
            string.Join('|', u.Id.ToString(CultureInfo.InvariantCulture), u.SicherheitsStempel, b32),
            DateTimeOffset.UtcNow + ZweiterFaktor.EinrichtungGueltig);
        var w = await _einst.LesenAsync(_db);
        return Ok(new
        {
            merker,
            schluessel = Totp.Gruppiert(b32),
            uri = Totp.Uri(w.AppName, u.Benutzername, b32),
            aussteller = w.AppName,
            konto = u.Benutzername,
            gueltigSekunden = (int)ZweiterFaktor.EinrichtungGueltig.TotalSeconds
        });
    }

    /// <summary>Einrichtung abschließen: Code aus der App zum neuen Geheimnis → aktiv, 10 Wiederherstellungscodes.</summary>
    [HttpPost("einrichten")]
    public async Task<IActionResult> Einrichten([FromBody] EinrichtenDto dto)
    {
        var u = await IchAsync();
        if (u == null) return Unauthorized();

        var inhalt = ZweiterFaktor.Oeffnen(_anmeldung.JwtKey, ZweiterFaktor.ZweckEinrichtung, dto?.Merker, DateTimeOffset.UtcNow);
        var t = inhalt?.Split('|');
        if (t is not { Length: 3 } || t[0] != u.Id.ToString(CultureInfo.InvariantCulture) || t[1] != u.SicherheitsStempel
            || !Totp.Base32Lesen(t[2], out var geheimnis) || geheimnis.Length != Totp.GeheimnisBytes)
            return BadRequest(new
            {
                fehler = "Die Einrichtung ist abgelaufen (10 Minuten) oder wurde inzwischen ersetzt. Bitte noch einmal beginnen.",
                neuBeginnen = true
            });

        var adresse = HttpContext.Connection.RemoteIpAddress;
        if (_bremse.Gesperrt(u.Benutzername, adresse) is TimeSpan w) return Gebremst(w);
        var schritt = Totp.Pruefen(geheimnis, Totp.Bereinigen(dto!.Code), Totp.Zeitschritt(DateTimeOffset.UtcNow), null);
        if (schritt == null)
        {
            _bremse.Fehlschlag(u.Benutzername, adresse);
            return BadRequest(new
            {
                fehler = "Der Code stimmt nicht. Gib den 6-stelligen Code ein, den die App jetzt zeigt. Stimmt er trotzdem " +
                         "nicht, prüfe, ob Datum und Uhrzeit des Handys automatisch gestellt werden.",
                feld = "code"
            });
        }
        _bremse.Erfolg(u.Benutzername);

        bool warAktiv = ZweiterFaktor.Aktiv(u);
        u.ZweiterFaktorGeheimnis = t[2];
        u.ZweiterFaktorSeit = DateTime.UtcNow;
        u.ZweiterFaktorSchritt = schritt;
        var codes = ZweiterFaktor.CodesSetzen(u);
        u.SicherheitsStempel = KontenService.NeuerStempel();   // andere Geräte abmelden
        KontenService.Protokoll(_db, u.Benutzername, warAktiv ? Aktion.FaktorNeu : Aktion.FaktorEingerichtet, u);
        await _db.SaveChangesAsync();

        return Ok(new { codes, anmeldung = await _anmeldung.AntwortAsync(u, dto.AngemeldetBleiben) });
    }

    /// <summary>Neue Wiederherstellungscodes (Passwort + Code) – die alten gelten nicht mehr.</summary>
    [HttpPost("codes")]
    public async Task<IActionResult> NeueCodes([FromBody] PruefenDto dto)
    {
        var u = await IchAsync();
        if (u == null) return Unauthorized();
        if (!ZweiterFaktor.Aktiv(u)) return BadRequest(new { fehler = "Für dein Konto ist kein zweiter Faktor eingerichtet." });
        var fehler = Pruefen(u, dto?.Passwort, dto?.Code, codeNoetig: true);
        if (fehler != null) return fehler;

        var codes = ZweiterFaktor.CodesSetzen(u);
        KontenService.Protokoll(_db, u.Benutzername, Aktion.FaktorCodes, u);
        await _db.SaveChangesAsync();
        return Ok(new { codes, stand = await StandAsync(u) });
    }

    /// <summary>Ausschalten (Passwort + Code) – nur, wenn für die Rolle keine Pflicht gilt.</summary>
    [HttpPost("ausschalten")]
    public async Task<IActionResult> Ausschalten([FromBody] AusschaltenDto dto)
    {
        var u = await IchAsync();
        if (u == null) return Unauthorized();
        if (!ZweiterFaktor.Aktiv(u)) return BadRequest(new { fehler = "Für dein Konto ist kein zweiter Faktor eingerichtet." });
        var pflichten = await _einst.FaktorPflichtAsync(_db);
        if (ZweiterFaktor.Pflicht(u.Rolle, pflichten, _anmeldung.Produktiv))
            return BadRequest(new
            {
                fehler = "Für dein Konto ist der zweite Faktor Pflicht – ausschalten geht nicht. Für ein neues Handy: „Neu einrichten“."
            });
        var fehler = Pruefen(u, dto?.Passwort, dto?.Code, codeNoetig: true);
        if (fehler != null) return fehler;

        ZweiterFaktor.Entfernen(u);
        u.SicherheitsStempel = KontenService.NeuerStempel();
        KontenService.Protokoll(_db, u.Benutzername, Aktion.FaktorAus, u);
        await _db.SaveChangesAsync();
        return Ok(new { anmeldung = await _anmeldung.AntwortAsync(u, dto!.AngemeldetBleiben) });
    }

    // -----------------------------------------------------------------

    private async Task<Benutzer?> IchAsync()
    {
        var id = Rollen.BenutzerId(User);
        var u = id == null ? null : await _db.Benutzer.FindAsync(id.Value);
        return u != null && ZweiterFaktor.Moeglich(u.Rolle) ? u : null;
    }

    /// <summary>
    /// Passwort (und bei Bedarf Code) prüfen – mit Login-Bremse. null = in Ordnung; ein benutzter Code ist dann schon
    /// vermerkt (speichert der Aufrufer).
    /// </summary>
    private IActionResult? Pruefen(Benutzer u, string? passwort, string? code, bool codeNoetig)
    {
        var adresse = HttpContext.Connection.RemoteIpAddress;
        if (_bremse.Gesperrt(u.Benutzername, adresse) is TimeSpan w) return Gebremst(w);
        if (!PasswortHasher.Verify(passwort ?? "", u.PasswortHash))
        {
            _bremse.Fehlschlag(u.Benutzername, adresse);
            return BadRequest(new { fehler = "Das Passwort stimmt nicht.", feld = "passwort" });
        }
        if (codeNoetig)
        {
            var erg = ZweiterFaktor.Pruefen(u, code, DateTimeOffset.UtcNow);
            if (!erg.Ok)
            {
                _bremse.Fehlschlag(u.Benutzername, adresse);
                return BadRequest(new
                {
                    fehler = "Der Code stimmt nicht. Gib den aktuellen Code aus der App ein – oder einen Wiederherstellungscode.",
                    feld = "code"
                });
            }
            ZweiterFaktor.Anwenden(u, erg);
        }
        _bremse.Erfolg(u.Benutzername);
        return null;
    }

    private IActionResult Gebremst(TimeSpan w)
    {
        int sek = Math.Max(1, (int)Math.Ceiling(w.TotalSeconds));
        Response.Headers.RetryAfter = sek.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            fehler = $"Zu viele Fehlversuche. Bitte in {sek} Sekunden erneut versuchen.",
            sekunden = sek
        });
    }
}
