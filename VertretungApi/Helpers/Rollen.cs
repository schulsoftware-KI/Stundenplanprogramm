using System.Security.Claims;

namespace VertretungApi.Helpers;

// =====================================================================
//  Rollen, Claims und Zugriffsrichtlinien (Phase 2, Schritt 4).
//
//  Personal  = Admin, Lehrer, Klassenlehrer, Stufenkoordination.
//  PlanLesen = Wochen-/Tagespläne von Lehrkräften, Klassen und Kursen:
//              nur Personal – außer in appsettings steht "Woche:OhneLogin": true,
//              dann für alle (auch ohne Anmeldung). Schüler sehen nur /api/woche/ich.
//  Kontenverwaltung (Schritt 5): Admin alles; Klassenleitung/Stufenkoordination
//              dürfen Passwörter der Schülerkonten ihrer Klasse/Stufe zurücksetzen.
// =====================================================================

public static class Rollen
{
    public const string Admin = "Admin";
    public const string Schueler = "Schueler";

    /// <summary>Für [Authorize(Roles = Rollen.Personal)].</summary>
    public const string Personal = "Admin,Lehrer,Klassenlehrer,Stufenkoordination";

    /// <summary>Für [Authorize(Roles = Rollen.KontenSehen)]: Kontenliste/Zurücksetzen (Schritt 5).</summary>
    public const string KontenSehen = "Admin,Klassenlehrer,Stufenkoordination";

    /// <summary>Name der Richtlinie für Plan-Abfragen (siehe Program.cs).</summary>
    public const string PlanLesen = "PlanLesen";

    /// <summary>Claim mit dem Lehrerkürzel (wie bisher).</summary>
    public const string KuerzelClaim = "lehrerKuerzel";

    /// <summary>Claim mit dem Sicherheitsstempel des Kontos (Widerruf von Tokens).</summary>
    public const string StempelClaim = "stempel";

    /// <summary>
    /// Wird in Program.cs je Anfrage aus der Datenbank gesetzt, solange das Konto ein
    /// Startpasswort bzw. zurückgesetztes Passwort hat. Dann sind nur die Pfade in
    /// <see cref="ErlaubtBeiPasswortPflicht"/> erlaubt.
    /// </summary>
    public const string PasswortPflichtClaim = "passwortAendern";

    private static readonly string[] PflichtPfade =
    {
        "/api/auth/passwort", "/api/auth/me", "/api/auth/optionen", "/api/auth/login",
        "/api/auth/ueberall-abmelden",
    };

    public static bool ErlaubtBeiPasswortPflicht(PathString pfad)
        => PflichtPfade.Any(p => pfad.Equals(p, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] PersonalListe = Personal.Split(',');

    public static bool IstPersonal(ClaimsPrincipal u) => PersonalListe.Any(u.IsInRole);

    public static string? Kuerzel(ClaimsPrincipal u) => u.FindFirst(KuerzelClaim)?.Value;

    public static int? BenutzerId(ClaimsPrincipal u)
        => int.TryParse(u.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
