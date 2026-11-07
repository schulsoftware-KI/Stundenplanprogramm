using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VertretungApi.Services;

namespace VertretungApi.Data;

// =====================================================================
//  Datenbank beim Start vorbereiten (Phase 5.1, vorher direkt in Program.cs).
//
//  PostgreSQL mit Migrations (Ordner Data/Migrations, Echtbetrieb):
//      Migrate() – legt die Datenbank an bzw. bringt sie auf den Stand des Programms.
//      Nach jeder Änderung am Datenmodell muss im Projektordner eine Migration erzeugt
//      werden:  dotnet ef migrations add <Name> -o Data/Migrations
//      Fehlt sie, bricht Migrate() mit „pending changes“ ab – der Start schlägt dann
//      mit einem Hinweis fehl, statt mit einem unvollständigen Schema zu laufen.
//  PostgreSQL ohne Migrations: nur in der Entwicklung (EnsureCreated, mit Warnung);
//      im Echtbetrieb startet die API dann nicht.
//  SQLite (Entwicklung): EnsureCreated + Schema-Nachrüstung wie bisher.
//
//  Danach wie bisher: Protokolle nach Frist aufräumen, Löschfrist für Einträge.
//
//  Nachtrag 5.1 (Test 07.11.2026): Scheitert der Start an PostgreSQL, nennt das Log die Ursache
//  (Anmeldung abgelehnt, Datenbank fehlt, fehlende Rechte, Dienst nicht erreichbar …) statt immer
//  den Migrations-Hinweis – siehe Ursache(). Das Passwort erscheint dabei nie im Log.
// =====================================================================

public static class DatenbankStart
{
    private const string MigrationsHinweis =
        "Abhilfe: im Ordner VertretungApi „dotnet ef migrations add <Name> -o Data/Migrations“ ausführen, neu bauen und " +
        "starten (README, Abschnitt „Produktionsmodus (Phase 5.1)“). Wurde die Datenbank vorher ohne Migrations angelegt, " +
        "eine neue, leere Datenbank verwenden.";

    /// <summary>false = Start abbrechen (Grund steht im Log).</summary>
    public static bool Ausfuehren(WebApplication app, bool produktiv)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Datenbank");

        bool postgres = db.Database.IsNpgsql();
        bool mitMigrations = postgres && db.Database.GetMigrations().Any();

        if (mitMigrations)
        {
            try
            {
                var offen = db.Database.GetPendingMigrations().ToList();
                db.Database.Migrate();
                if (offen.Count > 0)
                    log.LogInformation("Datenbank auf den neuesten Stand gebracht: {Migrationen}", string.Join(", ", offen));
                else
                    log.LogInformation("Datenbank ist auf dem neuesten Stand.");
            }
            catch (Exception ex)
            {
                var (grund, abhilfe) = Ursache(ex, db.Database.GetConnectionString());
                log.LogCritical(ex, "{Grund} {Abhilfe}", grund, abhilfe);
                return false;
            }
        }
        else if (postgres && produktiv)
        {
            log.LogCritical("Für PostgreSQL im Echtbetrieb fehlen die Migrations (Ordner Data/Migrations). {Hinweis}",
                MigrationsHinweis.Replace("<Name>", "Grundlinie"));
            return false;
        }
        else
        {
            if (postgres)
                log.LogWarning("PostgreSQL ohne Migrations: Das Schema wird per EnsureCreated angelegt (nur zum Ausprobieren in der Entwicklung).");
            try
            {
                db.Database.EnsureCreated();
            }
            catch (Exception ex)
            {
                if (postgres)
                {
                    var (grund, abhilfe) = Ursache(ex, db.Database.GetConnectionString());
                    log.LogCritical(ex, "{Grund} {Abhilfe}", grund, abhilfe);
                }
                else
                {
                    log.LogCritical(ex, "Die SQLite-Datenbank konnte nicht angelegt bzw. geöffnet werden. " +
                        "Abhilfe: Pfad in ConnectionStrings:Sqlite und die Schreibrechte im Ordner prüfen.");
                }
                return false;
            }

            // Phase 2, Schritt 5: fehlende Tabellen/Spalten in einer bestehenden Entwicklungs-DB
            // ergänzen, damit sie nicht bei jeder Schemaänderung gelöscht werden muss.
            if (app.Configuration.GetValue("Database:SchemaNachruesten", true))
            {
                try
                {
                    foreach (var m in SchemaNachruestung.Ausfuehren(db))
                        log.LogInformation("Schema nachgerüstet: {Meldung}", m);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Schema-Nachrüstung fehlgeschlagen. Abhilfe: vertretung.db löschen, API neu starten, Seeds erneut ausführen.");
                }
            }
        }

        Aufraeumen(db, app.Configuration, log);
        return true;
    }

    // -----------------------------------------------------------------
    //  Nachtrag 5.1: Ursache eines Startfehlers mit PostgreSQL und passende Abhilfe.
    //  Ausgewertet werden PostgresException.SqlState (Fehlercodes des Servers) und
    //  Verbindungsfehler (SocketException/TimeoutException in der InnerException-Kette).
    //  Alles andere (z. B. fehlende Migration nach Modelländerung) → Migrations-Hinweis.
    // -----------------------------------------------------------------

    internal static (string Grund, string Abhilfe) Ursache(Exception ex, string? verbindung)
    {
        var z = Ziel(verbindung);
        var kette = Kette(ex).ToList();
        string? code = kette.OfType<PostgresException>().FirstOrDefault()?.SqlState;

        switch (code)
        {
            case "28P01":   // invalid_password – auch bei unbekanntem Benutzer
            case "28000":   // invalid_authorization_specification – z. B. kein Eintrag in pg_hba.conf
                return ($"PostgreSQL hat die Anmeldung abgelehnt (Fehler {code}; {z.Text}).", AnmeldeHinweis(z));
            case "3D000":   // invalid_catalog_name
                return ($"Die Datenbank „{z.Datenbank}“ gibt es auf {z.Host}:{z.Port} nicht (Fehler 3D000).",
                    "Abhilfe: in der „SQL Shell (psql)“ als postgres anlegen: " +
                    $"CREATE DATABASE {z.Datenbank} OWNER {z.Benutzer}; – oder den Namen hinter „Database=“ in " +
                    "ConnectionStrings:Postgres prüfen.");
            case "42501":   // insufficient_privilege
                return ($"Dem Benutzer „{z.Benutzer}“ fehlen in der Datenbank „{z.Datenbank}“ die Rechte, " +
                        "Tabellen anzulegen (Fehler 42501).",
                    "Abhilfe: Die Datenbank muss diesem Benutzer gehören (ab PostgreSQL 15 darf sonst nur der Eigentümer " +
                    "im Schema „public“ Tabellen anlegen). In der „SQL Shell (psql)“ als postgres: " +
                    $"ALTER DATABASE {z.Datenbank} OWNER TO {z.Benutzer};");
            case "42P07":   // duplicate_table – Tabellen stammen nicht aus den Migrations
                return ($"In der Datenbank „{z.Datenbank}“ gibt es schon Tabellen, die nicht über die Migrations " +
                        "angelegt wurden, z. B. früher per EnsureCreated (Fehler 42P07).",
                    "Abhilfe: eine neue, leere Datenbank verwenden (CREATE DATABASE … OWNER …;) und in " +
                    "ConnectionStrings:Postgres eintragen. Stecken in der alten schon Daten, die gebraucht werden, " +
                    "vorher sichern (README, Abschnitt „Sicherung“).");
            case "57P03":   // cannot_connect_now – Server startet oder fährt herunter
                return ($"PostgreSQL nimmt gerade keine Verbindungen an ({z.Host}:{z.Port}; Fehler 57P03).",
                    "Abhilfe: einen Moment warten und die API erneut starten.");
        }

        if (code == null && kette.Any(e => e is SocketException))
            return ($"Keine Verbindung zu PostgreSQL auf {z.Host}:{z.Port}.", DienstHinweis);

        // Zeitüberschreitung: beim Verbinden (Server hängt/nicht erreichbar) oder bei einer sehr langen Migration.
        if (code == null && kette.Any(e => e is TimeoutException))
            return ($"PostgreSQL auf {z.Host}:{z.Port} hat nicht rechtzeitig geantwortet.",
                DienstHinweis + " Bricht eine lange Migration ab, in ConnectionStrings:Postgres „Command Timeout=300“ " +
                "ergänzen und erneut starten.");

        // Npgsql meldet ein fehlendes Passwort ohne Servercode („No password has been provided …“).
        if (code == null && kette.OfType<NpgsqlException>()
                .Any(e => e.Message.Contains("password", StringComparison.OrdinalIgnoreCase)))
            return ($"Für die Anmeldung bei PostgreSQL fehlt das Passwort ({z.Text}).", AnmeldeHinweis(z));

        return ("Die Datenbank konnte nicht auf den Stand des Programms gebracht werden.", MigrationsHinweis);
    }

    private const string DienstHinweis =
        "Abhilfe: Läuft PostgreSQL? Unter Windows in der App „Dienste“ den Dienst „postgresql-x64-17“ " +
        "(bzw. die installierte Version) starten, unter Linux „systemctl status postgresql“. " +
        "Sonst „Host=“ und „Port=“ in ConnectionStrings:Postgres prüfen.";

    private static string AnmeldeHinweis(ZielInfo z) =>
        "Abhilfe: Benutzername und Passwort in ConnectionStrings:Postgres prüfen (appsettings.Production.json bzw. " +
        "Umgebungsvariable ConnectionStrings__Postgres – eine gesetzte Umgebungsvariable geht der Datei vor). " +
        "Das Passwort nur aus Buchstaben, Ziffern und Bindestrichen wählen; neu setzen in der „SQL Shell (psql)“ " +
        $"als postgres: ALTER ROLE {z.Benutzer} WITH PASSWORD 'neues-passwort'; (README, „Probelauf mit PostgreSQL“).";

    private sealed record ZielInfo(string Host, int Port, string Datenbank, string Benutzer)
    {
        public string Text => $"Datenbank „{Datenbank}“ auf {Host}:{Port}, Benutzer „{Benutzer}“";
    }

    /// <summary>Host, Port, Datenbank und Benutzer aus der Verbindungsangabe – nie das Passwort.</summary>
    private static ZielInfo Ziel(string? verbindung)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(verbindung ?? "");
            string host = string.IsNullOrWhiteSpace(b.Host) ? "localhost" : b.Host;
            string benutzer = string.IsNullOrWhiteSpace(b.Username) ? "?" : b.Username;
            string datenbank = string.IsNullOrWhiteSpace(b.Database) ? benutzer : b.Database;   // Npgsql-Standard
            return new ZielInfo(host, b.Port, datenbank, benutzer);
        }
        catch
        {
            return new ZielInfo("?", 0, "?", "?");
        }
    }

    /// <summary>Die Ausnahme und ihre InnerExceptions (begrenzt, falls eine Kette sich selbst enthält).</summary>
    private static IEnumerable<Exception> Kette(Exception ex)
    {
        Exception? e = ex;
        for (int i = 0; e != null && i < 20; i++, e = e.InnerException)
            yield return e;
    }

    private static void Aufraeumen(AppDbContext db, IConfiguration cfg, ILogger log)
    {
        // Protokoll der Kontenverwaltung nur begrenzt aufbewahren (Datensparsamkeit).
        try
        {
            int tage = cfg.GetValue("Konten:ProtokollTage", 365);
            int geloescht = KontenService.ProtokollAufraeumen(db, tage);
            if (geloescht > 0) log.LogInformation("{Anzahl} alte Protokolleinträge gelöscht.", geloescht);
            // Phase 4.2: Download-Protokoll mit derselben Frist.
            int downloads = BerichtService.ProtokollAufraeumen(db, tage);
            if (downloads > 0) log.LogInformation("{Anzahl} alte Einträge im Download-Protokoll gelöscht.", downloads);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kontenprotokoll konnte nicht aufgeräumt werden.");
        }

        // Phase 3.4: Löschfrist für Klassenbucheinträge – vorbereitet, Standard aus (0).
        // Die Frist legt die Schule mit der/dem Datenschutzbeauftragten fest.
        try
        {
            int tage = cfg.GetValue("Klassenbuch:EintraegeLoeschenNachTagen", 0);
            int geloescht = KlassenbuchEintragService.Aufraeumen(db, tage);
            if (geloescht > 0) log.LogInformation("{Anzahl} Klassenbucheinträge nach Ablauf der Frist gelöscht.", geloescht);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Alte Klassenbucheinträge konnten nicht gelöscht werden.");
        }
    }
}
