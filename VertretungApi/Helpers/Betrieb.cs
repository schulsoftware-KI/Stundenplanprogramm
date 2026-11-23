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

        // P6a (Entscheidung 229): Schuladressen und Adressbeschränkung – auch in der Entwicklung, ein falscher Wert
        // sperrte sonst still aus (bzw. ließe still alle herein).
        var netze = Zugriffsschutz.Lesen(cfg, out var falscheNetze);
        foreach (var n in falscheNetze)
            fehler.Add($"Zugriff:Schuladressen enthält „{n}“ – das ist keine IP-Adresse bzw. kein Netz " +
                       "(Beispiele: 203.0.113.10, 203.0.113.0/24, 2001:db8:1234::/48).");
        bool desktopNurSchule = Zugriffsschutz.Schalter(cfg, "Zugriff:DesktopNurSchule", out var u1);
        bool adminNurSchule = Zugriffsschutz.Schalter(cfg, "Zugriff:AdminNurSchule", out var u2);
        if (u1) fehler.Add("Zugriff:DesktopNurSchule muss true oder false sein.");
        if (u2) fehler.Add("Zugriff:AdminNurSchule muss true oder false sein.");
        if ((desktopNurSchule || adminNurSchule) && netze.Count == 0 && falscheNetze.Count == 0)
            fehler.Add("Zugriff:DesktopNurSchule bzw. Zugriff:AdminNurSchule ist an, aber Zugriff:Schuladressen ist leer – dann " +
                       "käme niemand mehr herein. Die Adressen der Schule eintragen oder die Beschränkung ausschalten.");
        foreach (var n in netze.Where(x => x.Praefix < (x.Laenge == 32 ? 8 : 16)))
            hinweise.Add($"Zugriff:Schuladressen enthält das sehr große Netz {n} – alle Adressen darin gelten als Schule.");
        if (produktiv && (desktopNurSchule || adminNurSchule) && !cfg.GetValue("Proxy:Aktiv", false))
            hinweise.Add("Zugriff: Eine Adressbeschränkung ist an, aber Proxy:Aktiv ist aus. Läuft die API hinter einem Reverse " +
                         "Proxy, kommen alle Anfragen von dessen Adresse – dann Proxy:Aktiv und Proxy:BekannteProxys setzen.");

        // P6a (Entscheidung 230): Werte der Anfragebegrenzung.
        var begrenzung = VertretungApi.Services.BegrenzungsWerte.Lesen(cfg, fehler);
        if (produktiv && (begrenzung.JeKontoJeMinute == 0 || begrenzung.OhneAnmeldungJeMinute == 0 || begrenzung.DesktopJeMinute == 0))
            hinweise.Add("Begrenzung: Mindestens eine Anfragebegrenzung ist ausgeschaltet (0) – im Echtbetrieb nicht empfohlen.");

        // P4a (Entscheidung 221): Testdatum nur in der Entwicklung – im Echtbetrieb startet der Server damit nicht.
        if (!string.IsNullOrWhiteSpace(cfg[Testdatum.Schluessel]))
        {
            if (produktiv)
                fehler.Add($"{Testdatum.Schluessel} (Testdatum) ist im Echtbetrieb nicht erlaubt – den Eintrag bzw. die " +
                           "Umgebungsvariable Entwicklung__Heute entfernen.");
            else
            {
                Testdatum.Lesen(cfg, out var testFehler);
                if (testFehler != null) fehler.Add(testFehler);
            }
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
