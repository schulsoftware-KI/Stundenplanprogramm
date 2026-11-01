using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Hilfen der Kontenverwaltung (Phase 2, Schritt 5): Protokoll, eindeutige
//  Benutzernamen, Zuständigkeit von Klassenleitung/Stufenkoordination.
// =====================================================================

public static class KontenService
{
    public static class Aktion
    {
        public const string Angelegt = "Angelegt";
        public const string Geaendert = "Geändert";
        public const string PasswortGeaendert = "Passwort geändert";
        public const string PasswortZurueckgesetzt = "Passwort zurückgesetzt";
        public const string Gesperrt = "Gesperrt";
        public const string Entsperrt = "Entsperrt";
        public const string Abgemeldet = "Überall abgemeldet";
        public const string Geloescht = "Gelöscht";
    }

    /// <summary>Protokolleintrag vormerken (gespeichert wird mit dem nächsten SaveChanges).</summary>
    public static void Protokoll(AppDbContext db, string akteur, string aktion, Benutzer konto, string? details = null)
        => db.KontoProtokolle.Add(new KontoProtokoll
        {
            Zeit = DateTime.UtcNow,
            Akteur = string.IsNullOrWhiteSpace(akteur) ? "System" : akteur,
            Aktion = aktion,
            KontoId = konto.Id == 0 ? null : konto.Id,
            Konto = konto.Benutzername,
            Details = details,
        });

    /// <summary>Löscht Protokolleinträge, die älter als die Aufbewahrungsfrist sind.</summary>
    public static int ProtokollAufraeumen(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = DateTime.UtcNow.AddDays(-tage);
        return db.KontoProtokolle.Where(p => p.Zeit < grenze).ExecuteDelete();
    }

    public static string NeuerStempel() => Guid.NewGuid().ToString("N");

    /// <summary>Benutzername ohne Rücksicht auf Groß-/Kleinschreibung schon vergeben?</summary>
    public static Task<bool> NameVergebenAsync(AppDbContext db, string name, int? ausser = null)
    {
        var klein = name.Trim().ToLowerInvariant();
        return db.Benutzer.AnyAsync(b => b.Benutzername.ToLower() == klein && (ausser == null || b.Id != ausser));
    }

    /// <summary>
    /// Freier Benutzername aus einem Vorschlag: „mia.mueller", „mia.mueller2", … .
    /// belegt: bereits in diesem Vorgang vergebene Namen (Sammelanlage).
    /// </summary>
    public static async Task<string> FreierNameAsync(AppDbContext db, string vorschlag, ISet<string>? belegt = null)
    {
        for (int i = 1; i < 1000; i++)
        {
            var name = i == 1 ? vorschlag : vorschlag + i;
            if (belegt != null && belegt.Contains(name)) continue;
            if (!await NameVergebenAsync(db, name)) { belegt?.Add(name); return name; }
        }
        return vorschlag + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    /// <summary>
    /// Wie <see cref="FreierNameAsync"/>, aber ohne Datenbankabfrage: vergeben enthält alle
    /// belegten Namen (Vergleich ohne Groß-/Kleinschreibung) und wird ergänzt. Für die Sammelanlage.
    /// </summary>
    public static string FreierName(string vorschlag, ISet<string> vergeben)
    {
        for (int i = 1; i < 1000; i++)
        {
            var name = i == 1 ? vorschlag : vorschlag + i;
            if (vergeben.Add(name)) return name;
        }
        var notfall = vorschlag + "-" + Guid.NewGuid().ToString("N")[..6];
        vergeben.Add(notfall);
        return notfall;
    }

    /// <summary>„05A, 05b ,Q1" → ["05A", "05B", "Q1"] (bereinigt, ohne Doppelte).</summary>
    public static List<string> Zustaendigkeiten(string? text)
        => (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(z => z.ToUpperInvariant())
            .Distinct()
            .ToList();

    public static string? ZustaendigText(string? text)
    {
        var liste = Zustaendigkeiten(text);
        return liste.Count == 0 ? null : string.Join(", ", liste);
    }

    /// <summary>Klasse (Lerngruppe Typ Klasse) und Stufe je Schüler.</summary>
    public static async Task<Dictionary<int, (string Name, string? Klasse, string Stufe)>> SchuelerInfoAsync(
        AppDbContext db, IEnumerable<int> ids)
    {
        var liste = ids.Distinct().ToList();
        if (liste.Count == 0) return new();
        var daten = await db.Schueler
            .Where(s => liste.Contains(s.Id))
            .Select(s => new
            {
                s.Id, s.Vorname, s.Nachname, s.Stufe,
                Klasse = s.Zugehoerigkeiten
                    .Where(z => z.Lerngruppe!.Typ == LerngruppeTyp.Klasse)
                    .Select(z => z.Lerngruppe!.Bezeichnung)
                    .FirstOrDefault()
            })
            .ToListAsync();
        return daten.ToDictionary(d => d.Id, d => ($"{d.Vorname} {d.Nachname}", d.Klasse, d.Stufe));
    }

    /// <summary>Ids der Schüler, die zu einer der Klassen/Stufen gehören („05A" oder „Q1").</summary>
    public static async Task<HashSet<int>> SchuelerIdsAsync(AppDbContext db, IReadOnlyCollection<string> gruppen)
    {
        if (gruppen.Count == 0) return new();
        var g = gruppen.Select(x => x.ToUpper()).ToList();
        var inKlasse = await db.Zugehoerigkeiten
            .Where(z => z.Lerngruppe!.Typ == LerngruppeTyp.Klasse && g.Contains(z.Lerngruppe.Bezeichnung.ToUpper()))
            .Select(z => z.SchuelerId)
            .ToListAsync();
        var inStufe = await db.Schueler
            .Where(s => g.Contains(s.Stufe.ToUpper()))
            .Select(s => s.Id)
            .ToListAsync();
        return inKlasse.Concat(inStufe).ToHashSet();
    }

    /// <summary>Namen, die im Passwort nicht vorkommen dürfen (Benutzername, Kürzel, Vor-/Nachname).</summary>
    public static async Task<List<string?>> VerboteneNamenAsync(AppDbContext db, Benutzer u)
    {
        var namen = new List<string?> { u.Benutzername, u.LehrerKuerzel };
        if (u.SchuelerId is int sid)
        {
            var s = await db.Schueler.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid);
            if (s != null) { namen.Add(s.Vorname); namen.Add(s.Nachname); }
        }
        return namen;
    }

    /// <summary>Anzahl aktiver (nicht gesperrter) Admin-Konten.</summary>
    public static Task<int> AktiveAdminsAsync(AppDbContext db)
        => db.Benutzer.CountAsync(b => b.Rolle == BenutzerRolle.Admin && !b.Gesperrt);

    public static bool IstPersonalRolle(BenutzerRolle r) => r != BenutzerRolle.Schueler;

    public static string RollenText(BenutzerRolle r) => r switch
    {
        BenutzerRolle.Admin => "Verwaltung",
        BenutzerRolle.Lehrer => "Lehrkraft",
        BenutzerRolle.Klassenlehrer => "Klassenleitung",
        BenutzerRolle.Stufenkoordination => "Stufenkoordination",
        BenutzerRolle.Schueler => "Schüler",
        _ => r.ToString()
    };

    /// <summary>Lesbare Kurzbeschreibung der Zuordnung eines Kontos (für Protokoll-Details).</summary>
    public static string Zuordnung(Benutzer u)
    {
        var teile = new List<string> { RollenText(u.Rolle) };
        if (!string.IsNullOrWhiteSpace(u.LehrerKuerzel)) teile.Add("Kürzel " + u.LehrerKuerzel);
        if (!string.IsNullOrWhiteSpace(u.Zustaendig)) teile.Add("zuständig " + u.Zustaendig);
        if (u.SchuelerId is int sid) teile.Add("Schüler-Id " + sid);
        return string.Join(", ", teile);
    }

    /// <summary>Regeltext für die App passend zur Rolle.</summary>
    public static object Regeln(BenutzerRolle rolle) => new
    {
        minLaenge = PasswortRegeln.MindestLaenge(rolle),
        text = PasswortRegeln.RegelText(rolle)
    };
}
