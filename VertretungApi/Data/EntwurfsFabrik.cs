using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VertretungApi.Data;

// =====================================================================
//  Phase 5.1: Fabrik für die EF-Werkzeuge (dotnet ef migrations add …).
//
//  Die Migrations gelten nur für PostgreSQL (Echtbetrieb). Deshalb baut diese Fabrik den
//  Kontext immer mit Npgsql, egal was in appsettings.json steht. Zum Erzeugen einer
//  Migration verbindet sich das Werkzeug nicht mit einer Datenbank – die Verbindungs-
//  angabe unten ist nur ein Platzhalter. (Die API selbst bringt die Datenbank beim Start
//  auf den neuesten Stand; „dotnet ef database update“ ist nicht nötig.)
//
//  SQLite in der Entwicklung nutzt weiter EnsureCreated + Schema-Nachrüstung.
// =====================================================================

public class EntwurfsFabrik : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var verbindung = Environment.GetEnvironmentVariable("VERTRETUNG_ENTWURF_DB")
                         ?? "Host=localhost;Database=vertretung_entwurf;Username=entwurf;Password=entwurf";
        var optionen = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(verbindung)
            .Options;
        return new AppDbContext(optionen);
    }
}
