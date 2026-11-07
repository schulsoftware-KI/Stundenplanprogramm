using Microsoft.EntityFrameworkCore;
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
                log.LogCritical(ex, "Die Datenbank konnte nicht auf den Stand des Programms gebracht werden. {Hinweis}", MigrationsHinweis);
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
            db.Database.EnsureCreated();

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
