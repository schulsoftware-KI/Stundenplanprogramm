using Microsoft.EntityFrameworkCore;
using VertretungApi.Model;

namespace VertretungApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<VertretungsTag> Tage => Set<VertretungsTag>();
    public DbSet<VertretungsEintragDb> Eintraege => Set<VertretungsEintragDb>();
    public DbSet<Benutzer> Benutzer => Set<Benutzer>();

    // Klassenbuch (Phase 1A)
    public DbSet<Lerngruppe> Lerngruppen => Set<Lerngruppe>();
    public DbSet<Schueler> Schueler => Set<Schueler>();
    public DbSet<Zugehoerigkeit> Zugehoerigkeiten => Set<Zugehoerigkeit>();
    public DbSet<Lektion> Lektionen => Set<Lektion>();

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

        b.Entity<Lektion>().HasIndex(x => new { x.Datum, x.Stunde, x.LerngruppeId });

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
    }
}
