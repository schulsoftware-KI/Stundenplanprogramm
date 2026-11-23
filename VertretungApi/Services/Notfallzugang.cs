using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Notfallzugang (Phase 5.1): Admin-Konto über die Konsole anlegen bzw. zurücksetzen.
//
//    dotnet VertretungApi.dll admin-anlegen [benutzername]      (Echtbetrieb)
//    dotnet run -- admin-anlegen [benutzername]                 (Entwicklung)
//
//  Im Echtbetrieb gibt es keine Testkonten (seed-konten ist gesperrt). So entsteht das
//  erste Admin-Konto – und so kommt die Schule wieder herein, wenn das Admin-Passwort
//  vergessen oder das Konto gesperrt ist. Wer den Befehl ausführen kann, hat ohnehin
//  Zugriff auf Server und Datenbank.
//
//  - Fehlt das Konto, wird es als Admin angelegt; ein vorhandenes Admin-Konto bekommt ein
//    neues Startpasswort und wird entsperrt. Konten anderer Rollen werden nicht angefasst.
//  - Das Startpasswort steht nur auf der Konsole (nicht im Log, nicht in der Datenbank);
//    bei der ersten Anmeldung muss ein eigenes Passwort festgelegt werden.
//  - Alle bisherigen Anmeldungen des Kontos enden; das Kontenprotokoll vermerkt „Konsole“.
//  - P6a (Entscheidung 227): setzt auch den zweiten Faktor zurück (Handy des einzigen Admins verloren). Im
//    Echtbetrieb muss er nach dem eigenen Passwort neu eingerichtet werden (Pflicht für Admins, 177).
// =====================================================================

public static class Notfallzugang
{
    public const string Befehl = "admin-anlegen";

    /// <summary>Rückgabe = Exit-Code (0 = in Ordnung).</summary>
    public static async Task<int> AusfuehrenAsync(IServiceProvider dienste, string? benutzername)
    {
        var name = string.IsNullOrWhiteSpace(benutzername) ? "admin" : benutzername.Trim();
        var namensFehler = PasswortRegeln.BenutzernameFehler(name);
        if (namensFehler != null)
        {
            Console.Error.WriteLine(namensFehler);
            return 2;
        }

        using var scope = dienste.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var klein = name.ToLowerInvariant();
        var u = await db.Benutzer.FirstOrDefaultAsync(b => b.Benutzername.ToLower() == klein);
        if (u != null && u.Rolle != BenutzerRolle.Admin)
        {
            Console.Error.WriteLine($"Das Konto „{u.Benutzername}“ ist kein Admin-Konto und wird hier nicht verändert. " +
                                   "Bitte einen anderen Benutzernamen angeben, z. B.: admin-anlegen admin2");
            return 2;
        }

        var jetzt = DateTime.UtcNow;
        var startPasswort = PasswortRegeln.StartPasswort();
        bool neu = u == null;
        bool warGesperrt = u?.Gesperrt ?? false;
        bool hatteFaktor = u != null && ZweiterFaktor.Aktiv(u);
        if (u == null)
        {
            u = new Benutzer { Benutzername = name, Rolle = BenutzerRolle.Admin, AngelegtAm = jetzt };
            db.Benutzer.Add(u);
        }
        u.PasswortHash = PasswortHasher.Hash(startPasswort);
        u.MussPasswortAendern = true;
        u.Gesperrt = false;
        u.PasswortGeaendertAm = jetzt;
        u.SicherheitsStempel = KontenService.NeuerStempel();
        ZweiterFaktor.Entfernen(u);    // P6a (227)
        await db.SaveChangesAsync();   // erst speichern, damit das Protokoll die Konto-Id kennt

        KontenService.Protokoll(db, "Konsole",
            neu ? KontenService.Aktion.Angelegt : KontenService.Aktion.PasswortZurueckgesetzt, u,
            "Notfallzugang (admin-anlegen)" + (warGesperrt ? "; entsperrt" : "") +
            (hatteFaktor ? "; zweiter Faktor zurückgesetzt" : ""));
        await db.SaveChangesAsync();

        Console.WriteLine();
        Console.WriteLine(neu
            ? $"Admin-Konto „{u.Benutzername}“ angelegt."
            : $"Admin-Konto „{u.Benutzername}“: Passwort zurückgesetzt{(warGesperrt ? ", Konto entsperrt" : "")}" +
              $"{(hatteFaktor ? ", zweiter Faktor zurückgesetzt" : "")}, alle Anmeldungen beendet.");
        Console.WriteLine($"Startpasswort (wird nur jetzt angezeigt): {startPasswort}");
        Console.WriteLine("Bei der ersten Anmeldung muss ein eigenes Passwort festgelegt werden – im Echtbetrieb danach " +
                          "auch der zweite Faktor (Authenticator-App).");
        Console.WriteLine();
        return 0;
    }
}
