using System.Data.Common;
using System.Net;
using System.Text;

namespace VertretungApi.Helpers;

// =====================================================================
//  Produktionsmodus (Phase 5.1).
//
//  Produktiv ist jede Umgebung außer "Development" (ASPNETCORE_ENVIRONMENT). Ohne Angabe
//  startet ASP.NET Core als "Production" – der sichere Standard. Visual Studio bzw.
//  `dotnet run` setzen über Properties/launchSettings.json "Development"; das Profil
//  "Produktionstest" startet lokal im Produktionsmodus.
//
//  Vor dem Start prüft Pruefen(), ob die Konfiguration für den Echtbetrieb taugt:
//    Fehler   -> der Server startet nicht (verständliche Meldung auf der Konsole),
//    Hinweise -> stehen beim Start im Log.
//  Schlüssel und Passwörter gehören nie ins (öffentliche) Repo, sondern in Umgebungs-
//  variablen (z. B. Jwt__Key, Api__PushKey, ConnectionStrings__Postgres) oder in die nicht
//  eingecheckte Datei appsettings.Production.json (Vorlage: appsettings.Production.Beispiel.json).
// =====================================================================

public static class Betrieb
{
    // Werte aus dem Repo bzw. der Vorlage – im Echtbetrieb nie gültig.
    private static readonly string[] Platzhalter =
    {
        "change-me", "bitte-aendern", "bitte aendern", "dev-geheimschluessel", "hier-eigen", "hier eigen"
    };

    public const int MinJwtBytes = 32;
    public const int MinPushKey = 24;

    public sealed record Ergebnis(List<string> Fehler, List<string> Hinweise);

    /// <summary>Echtbetrieb = jede Umgebung außer Development.</summary>
    public static bool Produktiv(IHostEnvironment env) => !env.IsDevelopment();

    public static bool IstPostgres(IConfiguration cfg)
        => string.Equals(cfg["Database:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prüft die Konfiguration. Proxy-Angaben immer, alles andere nur im Echtbetrieb.</summary>
    public static Ergebnis Pruefen(IConfiguration cfg, bool produktiv)
    {
        var fehler = new List<string>();
        var hinweise = new List<string>();

        // Proxy-Adressen müssen lesbar sein – sonst würde die Weitergabe der Absenderadresse
        // still nicht greifen (Login-Bremse träfe alle gemeinsam).
        if (cfg.GetValue("Proxy:Aktiv", false))
        {
            var proxys = BekannteProxys(cfg, out var ungueltig);
            foreach (var u in ungueltig)
                fehler.Add($"Proxy:BekannteProxys enthält „{u}“ – das ist keine IP-Adresse.");
            if (proxys.Count == 0 && ungueltig.Count == 0)
                hinweise.Add("Proxy:Aktiv ist an, aber Proxy:BekannteProxys ist leer – nur ein Proxy auf demselben Rechner (localhost) wird anerkannt.");
        }

        if (!produktiv) return new Ergebnis(fehler, hinweise);

        // --- Schlüssel ---
        var jwt = cfg["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwt))
            fehler.Add("Jwt:Key fehlt (Umgebungsvariable Jwt__Key oder appsettings.Production.json).");
        else if (Encoding.UTF8.GetByteCount(jwt) < MinJwtBytes)
            fehler.Add($"Jwt:Key ist zu kurz (mindestens {MinJwtBytes} Zeichen, empfohlen 64 zufällige Zeichen).");
        else if (IstPlatzhalter(jwt))
            fehler.Add("Jwt:Key ist noch der Wert aus dem Repo bzw. der Vorlage – einen eigenen zufälligen Schlüssel setzen.");

        var push = cfg["Api:PushKey"];
        if (string.IsNullOrWhiteSpace(push))
            fehler.Add("Api:PushKey fehlt (Umgebungsvariable Api__PushKey oder appsettings.Production.json). Ohne ihn könnte jeder Pläne und Stammdaten hochladen.");
        else if (push.Length < MinPushKey)
            fehler.Add($"Api:PushKey ist zu kurz (mindestens {MinPushKey} Zeichen).");
        else if (IstPlatzhalter(push))
            fehler.Add("Api:PushKey ist noch der Wert aus dem Repo bzw. der Vorlage – einen eigenen zufälligen Schlüssel setzen.");

        if (!string.IsNullOrWhiteSpace(jwt) && jwt == push)
            fehler.Add("Jwt:Key und Api:PushKey müssen verschieden sein.");

        // --- Zugang ---
        if (cfg.GetValue<bool>("Woche:OhneLogin"))
            fehler.Add("Woche:OhneLogin ist im Echtbetrieb nicht erlaubt – Pläne nur mit Anmeldung.");

        // --- Datenbank ---
        if (IstPostgres(cfg))
        {
            var conn = cfg.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(conn))
                fehler.Add("ConnectionStrings:Postgres fehlt (Umgebungsvariable ConnectionStrings__Postgres oder appsettings.Production.json).");
            else
            {
                DbConnectionStringBuilder? teile = new DbConnectionStringBuilder();
                try { teile.ConnectionString = conn; }
                catch (ArgumentException) { fehler.Add("ConnectionStrings:Postgres ist nicht lesbar (Schreibweise prüfen)."); teile = null; }
                if (teile != null)
                {
                    var pw = Wert(teile, "Password") ?? Wert(teile, "Pwd");
                    if (pw != null && (pw == "postgres" || IstPlatzhalter(pw)))
                        fehler.Add("ConnectionStrings:Postgres nutzt noch das Passwort aus dem Repo bzw. der Vorlage – ein eigenes Datenbankpasswort setzen.");
                    if (string.Equals(Wert(teile, "Include Error Detail"), "true", StringComparison.OrdinalIgnoreCase))
                        hinweise.Add("ConnectionStrings:Postgres enthält „Include Error Detail=true“ – dann können Datenbankwerte (Namen) in Fehlermeldungen im Log landen. Im Echtbetrieb entfernen.");
                }
            }
        }
        else
            hinweise.Add("Die Datenbank ist SQLite. Für den Echtbetrieb ist PostgreSQL vorgesehen (Database:Provider = Postgres).");

        var hosts = cfg["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(hosts) || hosts.Trim() == "*")
            hinweise.Add("AllowedHosts ist „*“ – im Echtbetrieb auf die eigene Adresse beschränken (z. B. „klassenbuch.meine-schule.de;localhost“).");

        return new Ergebnis(fehler, hinweise);
    }

    /// <summary>IP-Adressen aus Proxy:BekannteProxys (ungültige Einträge in <paramref name="ungueltig"/>).</summary>
    public static List<IPAddress> BekannteProxys(IConfiguration cfg, out List<string> ungueltig)
    {
        var liste = new List<IPAddress>();
        ungueltig = new List<string>();
        foreach (var eintrag in cfg.GetSection("Proxy:BekannteProxys").Get<string[]>() ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(eintrag)) continue;
            if (IPAddress.TryParse(eintrag.Trim(), out var a)) liste.Add(a);
            else ungueltig.Add(eintrag.Trim());
        }
        return liste;
    }

    /// <summary>Gibt die Fehler der Startprüfung gut lesbar auf der Konsole aus.</summary>
    public static void FehlerAusgeben(IEnumerable<string> fehler)
    {
        var e = Console.Error;
        e.WriteLine();
        e.WriteLine("==========================================================================");
        e.WriteLine(" VertretungApi startet nicht: Die Einstellungen taugen nicht für den Echtbetrieb.");
        e.WriteLine("==========================================================================");
        foreach (var f in fehler) e.WriteLine(" - " + f);
        e.WriteLine();
        e.WriteLine(" Schlüssel erzeugen (PowerShell):");
        e.WriteLine("   $b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)");
        e.WriteLine(" Zum Entwickeln stattdessen im Modus Development starten (Visual Studio bzw. dotnet run).");
        e.WriteLine(" Siehe README, Abschnitt „Produktionsmodus (Phase 5.1)“.");
        e.WriteLine();
    }

    private static bool IstPlatzhalter(string wert)
    {
        foreach (var p in Platzhalter)
            if (wert.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? Wert(DbConnectionStringBuilder b, string schluessel)
        => b.TryGetValue(schluessel, out var v) ? v?.ToString() : null;
}
