using System.Text.RegularExpressions;
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
        // P6a (225–227): zweiter Faktor – ohne Geheimnis und ohne Codes
        public const string FaktorEingerichtet = "Zweiter Faktor eingerichtet";
        public const string FaktorNeu = "Zweiter Faktor neu eingerichtet";
        public const string FaktorCodes = "Wiederherstellungscodes neu erzeugt";
        public const string FaktorAus = "Zweiter Faktor ausgeschaltet";
        public const string FaktorZurueckgesetzt = "Zweiter Faktor zurückgesetzt";
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

    // ---- P5c-1 (Nacharbeit P5b-2 N3): führende Null und Prüfung gegen die Klassen bzw. Stufen des Schuljahres ----

    private static readonly Regex EinstelligerJahrgang = new(@"^[1-9][A-Z]{0,3}$", RegexOptions.Compiled);

    /// <summary>Wie ZustaendigText, einstellige Jahrgänge mit führender Null: „7D, 7“ → „07D, 07“ („10B“, „Q1“ bleiben).</summary>
    public static string? ZustaendigNormalisieren(string? text)
    {
        var liste = Zustaendigkeiten(text).Select(z => EinstelligerJahrgang.IsMatch(z) ? "0" + z : z).Distinct().ToList();
        return liste.Count == 0 ? null : string.Join(", ", liste);
    }

    /// <summary>Klassen (Lerngruppen vom Typ Klasse) und Stufen aller Lerngruppen je Schuljahr.</summary>
    public static async Task<Dictionary<string, HashSet<string>>> BekannteGruppenAsync(AppDbContext db, IEnumerable<string> jahre)
    {
        var liste = jahre.Where(j => !string.IsNullOrWhiteSpace(j)).Distinct().ToList();
        var ergebnis = liste.ToDictionary(j => j, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        if (liste.Count == 0) return ergebnis;
        var zeilen = await db.Lerngruppen.AsNoTracking()
            .Where(l => liste.Contains(l.Schuljahr))
            .Select(l => new { l.Schuljahr, l.Typ, l.Bezeichnung, l.Stufe })
            .ToListAsync();
        foreach (var g in zeilen)
        {
            if (!ergebnis.TryGetValue(g.Schuljahr, out var menge)) continue;
            if (g.Typ == LerngruppeTyp.Klasse && !string.IsNullOrWhiteSpace(g.Bezeichnung)) menge.Add(g.Bezeichnung.Trim());
            if (!string.IsNullOrWhiteSpace(g.Stufe)) menge.Add(g.Stufe.Trim());
        }
        return ergebnis;
    }

    /// <summary>Einträge der Zuständigkeit, die es unter den bekannten Klassen bzw. Stufen nicht gibt.</summary>
    public static List<string> Unbekannt(string? zustaendig, IReadOnlySet<string> bekannt)
        => Zustaendigkeiten(zustaendig).Where(z => !bekannt.Contains(z)).ToList();

    /// <summary>Text der Rückfrage („Die Klasse 07X gibt es im Schuljahr 2026/27 nicht – trotzdem speichern?“).</summary>
    public static string UnbekanntText(IReadOnlyList<string> unbekannt, string schuljahr, bool keineGruppen, BenutzerRolle rolle)
    {
        string liste = string.Join(", ", unbekannt);
        if (keineGruppen)
            return $"Für das Schuljahr {schuljahr} gibt es in der App noch keine Klassen und Stufen (sie kommen mit dem " +
                   $"SchILD-Import) – „{liste}“ lässt sich deshalb nicht prüfen. Trotzdem speichern?";
        string art = rolle == BenutzerRolle.Stufenkoordination
            ? (unbekannt.Count == 1 ? "Die Stufe" : "Die Stufen")
            : (unbekannt.Count == 1 ? "Die Klasse" : "Die Klassen bzw. Stufen");
        return $"{art} {liste} gibt es im Schuljahr {schuljahr} nicht – trotzdem speichern?";
    }

    /// <summary>Klasse (Lerngruppe Typ Klasse) und Stufe je Schüler – seit 5.2b-1 die Klasse von heute.</summary>
    public static async Task<Dictionary<int, (string Name, string? Klasse, string Stufe)>> SchuelerInfoAsync(
        AppDbContext db, IEnumerable<int> ids)
    {
        var liste = ids.Distinct().ToList();
        if (liste.Count == 0) return new();
        var daten = await db.Schueler
            .Where(s => liste.Contains(s.Id))
            .Select(s => new { s.Id, s.Vorname, s.Nachname, s.Stufe })
            .ToListAsync();
        var klassen = await KlassenbuchService.KlassenAsync(db, liste, "");
        return daten.ToDictionary(d => d.Id,
            d => ($"{d.Vorname} {d.Nachname}", klassen.TryGetValue(d.Id, out var k) ? k : (string?)null, d.Stufe));
    }

    /// <summary>Ids der Schüler, die zu einer der Klassen/Stufen gehören („05A" oder „Q1").</summary>
    public static async Task<HashSet<int>> SchuelerIdsAsync(AppDbContext db, IReadOnlyCollection<string> gruppen)
    {
        if (gruppen.Count == 0) return new();
        var g = gruppen.Select(x => x.ToUpper()).ToList();
        var inKlasse = await db.Zugehoerigkeiten
            .Where(z => z.Lerngruppe!.Typ == LerngruppeTyp.Klasse && g.Contains(z.Lerngruppe.Bezeichnung.ToUpper()))
            .GueltigAm(KlassenbuchService.Heute())          // Phase 5.2b-1: Zuständigkeit nach der Klasse von heute
            .Select(z => z.SchuelerId)
            .ToListAsync();
        var inStufe = await db.Schueler
            .Where(s => g.Contains(s.Stufe.ToUpper()) && (s.Status == null || s.Status != "abgang"))
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
        if (!string.IsNullOrWhiteSpace(u.Zustaendig))
            teile.Add("zuständig " + u.Zustaendig +
                      (string.IsNullOrWhiteSpace(u.ZustaendigSchuljahr) ? "" : $" ({u.ZustaendigSchuljahr})"));   // P5a (219)
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
