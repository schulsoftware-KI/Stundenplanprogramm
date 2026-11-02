using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VertretungApi.Model;

namespace VertretungApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<VertretungsTag> Tage => Set<VertretungsTag>();
    public DbSet<VertretungsEintragDb> Eintraege => Set<VertretungsEintragDb>();
    public DbSet<Benutzer> Benutzer => Set<Benutzer>();
    public DbSet<KontoProtokoll> KontoProtokolle => Set<KontoProtokoll>();   // Phase 2, Schritt 5

    // Klassenbuch (Phase 1A)
    public DbSet<Lerngruppe> Lerngruppen => Set<Lerngruppe>();
    public DbSet<Schueler> Schueler => Set<Schueler>();
    public DbSet<Zugehoerigkeit> Zugehoerigkeiten => Set<Zugehoerigkeit>();
    public DbSet<Lektion> Lektionen => Set<Lektion>();                       // Tabelle KlassenbuchLektionen (Phase 3.1)
    public DbSet<Anwesenheit> Anwesenheiten => Set<Anwesenheit>();           // Phase 3.1
    public DbSet<KlassenbuchProtokoll> KlassenbuchProtokolle => Set<KlassenbuchProtokoll>(); // Phase 3.1

    // Grundplan + Kalender (Phase 2, Schritt 1)
    public DbSet<GrundplanVersion> GrundplanVersionen => Set<GrundplanVersion>();
    public DbSet<GrundplanSlot> GrundplanSlots => Set<GrundplanSlot>();
    public DbSet<KalenderDb> Kalender => Set<KalenderDb>();
    public DbSet<FreieZeitDb> FreieZeiten => Set<FreieZeitDb>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<VertretungsTag>()
            .HasIndex(t => new { t.Schuljahr, t.Datum }).IsUnique();

        b.Entity<VertretungsTag>()
            .HasMany(t => t.Eintraege)
            .WithOne(e => e.Tag!)
            .HasForeignKey(e => e.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Benutzer>()
            .HasIndex(u => u.Benutzername).IsUnique();

        b.Entity<KontoProtokoll>().HasIndex(p => p.Zeit);
        b.Entity<KontoProtokoll>().HasIndex(p => p.KontoId);

        // --- Klassenbuch ---
        b.Entity<Lerngruppe>().HasIndex(l => new { l.Schuljahr, l.Bezeichnung });

        b.Entity<Zugehoerigkeit>()
            .HasOne(z => z.Schueler!)
            .WithMany(s => s.Zugehoerigkeiten)
            .HasForeignKey(z => z.SchuelerId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Zugehoerigkeit>()
            .HasOne(z => z.Lerngruppe!)
            .WithMany(l => l.Mitglieder)
            .HasForeignKey(z => z.LerngruppeId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Zugehoerigkeit>()
            .HasIndex(z => new { z.SchuelerId, z.LerngruppeId, z.Schuljahr }).IsUnique();

        // Phase 3.1: neue Tabelle. Die alte Phase-1A-Tabelle "Lektionen" (eine Lerngruppe je
        // Lektion, nie benutzt) bleibt in bestehenden Entwicklungs-DBs ungenutzt liegen.
        b.Entity<Lektion>().ToTable("KlassenbuchLektionen");
        b.Entity<Lektion>()
            .HasIndex(x => new { x.Datum, x.Stunde, x.GrundUNr, x.GrundLehrer }).IsUnique();
        b.Entity<Lektion>()
            .HasMany(x => x.Anwesenheiten)
            .WithOne(a => a.Lektion!)
            .HasForeignKey(a => a.LektionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Anwesenheit>()
            .HasOne(a => a.Schueler!)
            .WithMany()
            .HasForeignKey(a => a.SchuelerId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<Anwesenheit>().HasIndex(a => new { a.LektionId, a.SchuelerId }).IsUnique();
        b.Entity<Anwesenheit>().HasIndex(a => a.SchuelerId);

        b.Entity<KlassenbuchProtokoll>().HasIndex(p => p.LektionId);
        b.Entity<KlassenbuchProtokoll>().HasIndex(p => p.Gruppe);

        // --- Grundplan (versioniert) ---
        b.Entity<GrundplanVersion>()
            .HasIndex(v => new { v.Schuljahr, v.GueltigAb }).IsUnique();

        b.Entity<GrundplanVersion>()
            .HasMany(v => v.Slots)
            .WithOne(s => s.Version!)
            .HasForeignKey(s => s.VersionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<GrundplanSlot>().HasIndex(s => new { s.VersionId, s.Lehrer });
        b.Entity<GrundplanSlot>().HasIndex(s => new { s.VersionId, s.LerngruppeKey });
        b.Entity<GrundplanSlot>().HasIndex(s => new { s.VersionId, s.WTagIndex, s.Stunde });

        // --- Kalender ---
        b.Entity<KalenderDb>().HasIndex(k => k.Schuljahr).IsUnique();

        b.Entity<KalenderDb>()
            .HasMany(k => k.FreieZeiten)
            .WithOne(f => f.Kalender!)
            .HasForeignKey(f => f.KalenderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Alle DateTime-Werte werden als UTC gespeichert (DateTime.UtcNow). SQLite liefert sie
        // aber ohne Zeitzone zurück (Kind = Unspecified) – im JSON fehlte dann das „Z", und die
        // App zeigte die UTC-Zeit als Ortszeit (eine bzw. zwei Stunden zu früh). Beim Lesen
        // deshalb als UTC kennzeichnen. Betrifft Konten und Kontenprotokoll (Phase 2, Schritt 5);
        // DateTimeOffset/DateOnly sind nicht betroffen. Keine Schemaänderung.
        var alsUtc = new ValueConverter<DateTime, DateTime>(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
                if (prop.ClrType == typeof(DateTime) || prop.ClrType == typeof(DateTime?))
                    prop.SetValueConverter(alsUtc);
    }
}
