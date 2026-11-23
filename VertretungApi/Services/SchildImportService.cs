using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Phase 5.2b-2 (Entscheidungen 78–83, 166–170, Punkte A, D, E, H): SchILD-Import.
//
//  Der Desktop schickt Schüler mit HMAC-Prüfsummen, Namen, Jahrgang, Status und den
//  Plan-Schlüsseln ihrer Klasse und ihrer zugeordneten Kurse/Gruppen. Probelauf und Import
//  rechnen mit DEMSELBEN Code (78): Der Probelauf liefert die Vorschau, der Import führt die
//  vorgemerkten Änderungen aus – in einer Transaktion und nur, wenn die Prüfsumme der Vorschau
//  noch stimmt (Datenbankstand + Nutzlast).
//
//  Erkennung: zuerst die Prüfsumme der SchILD-ID, dann die aus Name + Geburtsdatum (85, 167).
//  Wechsel (169): Die alte Zugehörigkeit endet am Vortag von „gilt ab“, die neue beginnt dann;
//  beginnt die alte erst an „gilt ab“ oder später, wird sie ersetzt. Unveränderte Zeilen bleiben.
//  Gruppen, die ab jetzt aus SchILD kommen (81): Lerngruppe.Quelle = SchILD, SchildAb = „gilt ab“,
//  die Liste der Fachlehrkraft endet am Vortag. Eine Sek-I-Gruppe OHNE Liste der Fachlehrkraft
//  bekommt ihre Liste ab Schuljahresbeginn (genauer: seit dem Eintritt in die jetzige Klasse) –
//  sonst blieben frühere Stunden leer und ließen sich nicht mehr zusammenstellen.
//  Abgänge (80, E) nur nach Bestätigung: Zugehörigkeiten enden am Vortag, Status „abgang“,
//  Schülerkonto gesperrt (Kontenprotokoll). Klassenbuchdaten werden nie geändert.
//
//  Datenschutz: Die Vorschau nennt Namen (nötig zum Bestätigen) – sie geht nur an den Desktop.
//  Bericht, Importprotokoll, Fehlermeldungen und Log enthalten keine Namen.
//
//  Phase 5.2c-1 (Entscheidungen 186–190):
//  - „Dieselbe Person?“: Ein Schüler des Exports ohne Treffer und ein Datensatz „fehlt im Export“
//    bilden ein Paar, wenn Vorname + Geburtsdatum (V1) oder Nachname + Geburtsdatum (N1) gleich sind
//    oder der Name in derselben Klasse bzw. demselben Jahrgang gleich ist. Verschiedene SchILD-IDs
//    schließen aus. Vorausgewählt nur V1/N1 + gleiche Klasse/Jahrgang + eindeutig. Zusammenführen =
//    der bisherige Datensatz bekommt die neue Identität (Konto und Klassenbuchdaten bleiben), danach
//    läuft der normale Abgleich. Die Wahl kommt als Liste „paare“ und gehört zur Prüfsumme der Vorschau.
//  - Kennung des Schlüssels: Weicht sie vom letzten Import derselben Art ab, sperrt die Vorschau.
//  - Neu-Zuordnung (Schlüssel verloren): nur wenn kein Schüler über die Prüfsummen erkannt wird; Paare
//    über Name + Klasse bzw. Jahrgang (eindeutig = vorausgewählt, Namensgleiche nur von Hand, gleicher
//    Name in einer anderen Klasse desselben Jahrgangs nur von Hand); zugeordnete Datensätze bekommen
//    alle neuen Prüfsummen.
//
//  Phase 5.2c-2 (Entscheidungen 191–197):
//  - Klassenleitung laut Klassen.dat (Plan-Kürzel) wird an der Klassen-Lerngruppe gespeichert; Änderungen
//    erscheinen als Hinweis. Die Konten ändern sich nicht – Vorschläge macht die Kontenverwaltung (192).
//  - Namen der Lehrkräfte (Kürzel, Nachname, Vorname) nur für Kürzel des Grundplans; mit Lehrkraefte.dat wird die
//    Liste dieser Art ganz ersetzt (193). Sichtbar nur für den Admin und die Lehrkraft selbst (194).
//  - Neu-Zuordnung: Namensgleiche bilden Gruppen (Zeilen untereinander); je Gruppe ein Vorschlag (passender Eintritt,
//    dann die meisten gemeinsamen Kurse) – nur, wenn er eindeutig ist. Angewendet wird er erst nach Bestätigung.
//
//  P4a (5.3a, Entscheidung 206): Warnungen zum Halbjahr – keine Sperren; der Desktop fragt vor dem Import nach:
//  Abschnitt 2 mit „gilt ab“ vor „2. Halbjahr ab“, Abschnitt 1 mit „gilt ab“ im 2. Halbjahr, Abschnitt 2 ab dem
//  2. Halbjahr ohne eigenen Grundplan (dazu, wenn erkennbar: unbekannte Plan-Schlüssel = SchILD liefert schon die Kurse
//  des 2. Halbjahres). Sie stehen in „warnungen“ und als „WARNUNG:“ im Bericht.
// =====================================================================

public static class SchildImportService
{
    public const string AkteurKonto = "SchILD-Import";
    public const int MaxSchueler = 20000;
    public const int MaxSchluessel = 5000;

    private static readonly Regex PruefsummeMuster = new(@"^[A-Za-z0-9+/=_\-]{16,128}$", RegexOptions.Compiled);
    private static readonly Regex KennungMuster = new(@"^[0-9A-F]{4}-[0-9A-F]{4}$", RegexOptions.Compiled);
    private static readonly Regex Leerraum = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Kategorien der Vorschau (Schlüssel für den Desktop).</summary>
    public static class Kategorie
    {
        public const string Neu = "neu";
        public const string NameGeaendert = "nameGeaendert";
        public const string Klassenwechsel = "klassenwechsel";
        public const string Kurswechsel = "kurswechsel";
        public const string StatusGeaendert = "statusGeaendert";
        public const string AbgangLautSchild = "abgangLautSchild";
        public const string FehltImExport = "fehltImExport";
        // Phase 5.2c-1
        public const string DieselbePerson = "dieselbePerson";
        public const string Zuordnung = "zuordnung";
        public const string Zusammengefuehrt = "zusammengefuehrt";

        public static readonly string[] Reihenfolge =
        {
            AbgangLautSchild, FehltImExport, DieselbePerson, Zuordnung, Neu, Zusammengefuehrt, NameGeaendert,
            Klassenwechsel, Kurswechsel, StatusGeaendert
        };
    }

    /// <summary>SchILD-Status → Status in der Datenbank (Entscheidung 80); null = nicht erlaubt.</summary>
    public static string? StatusText(int schildStatus) => schildStatus switch
    {
        0 or 2 => "aktiv",
        3 => "beurlaubt",
        6 => "extern",
        8 or 9 => "abgang",
        _ => null
    };

    /// <summary>Leerzeichen am Rand weg, leere Angaben = null (vor <see cref="Pruefen"/>).</summary>
    public static void Normalisieren(SchildImportDto dto)
    {
        dto.Schuljahr = (dto.Schuljahr ?? "").Trim();
        dto.Plan = Leer(dto.Plan);
        dto.Vorschau = Leer(dto.Vorschau);
        dto.Akteur = Leer(dto.Akteur);
        dto.PlanSchluessel = dto.PlanSchluessel?
            .Select(Leer).Where(k => k != null).Select(k => k!).ToList();
        dto.Kennung = Leer(dto.Kennung)?.ToUpperInvariant();
        if (dto.Paare != null)
            foreach (var p in dto.Paare.Where(x => x != null))
                p.ExtId = (p.ExtId ?? "").Trim();
        // 5.2c-2 (191, 193)
        if (dto.Klassenleitungen != null)
            foreach (var k in dto.Klassenleitungen.Where(x => x != null))
            {
                k.Klasse = Leerraum.Replace((k.Klasse ?? "").Trim(), " ").ToUpperInvariant();
                k.Kuerzel = KuerzelText(k.Kuerzel) ?? "";
            }
        if (dto.Lehrkraefte != null)
            foreach (var l in dto.Lehrkraefte.Where(x => x != null))
            {
                l.Kuerzel = (l.Kuerzel ?? "").Trim();
                l.Nachname = Leerraum.Replace((l.Nachname ?? "").Trim(), " ");
                l.Vorname = Leerraum.Replace((l.Vorname ?? "").Trim(), " ");
            }
        if (dto.Schueler == null) return;
        foreach (var s in dto.Schueler)
        {
            if (s == null) continue;
            s.ExtId = (s.ExtId ?? "").Trim();
            s.ExtIdSchild = Leer(s.ExtIdSchild);
            s.ExtIdVornameGeb = Leer(s.ExtIdVornameGeb);
            s.ExtIdNachnameGeb = Leer(s.ExtIdNachnameGeb);
            s.Nachname = Regex.Replace((s.Nachname ?? "").Trim(), @"\s+", " ");
            s.Vorname = Regex.Replace((s.Vorname ?? "").Trim(), @"\s+", " ");
            s.Stufe = (s.Stufe ?? "").Trim().ToUpperInvariant();
            s.Klasse = Leer(s.Klasse)?.ToUpperInvariant();
            s.Kurse = s.Kurse?.Select(Leer).Where(k => k != null).Select(k => k!).ToList();
        }
    }

    /// <summary>
    /// Prüft die Form der Nutzlast. Meldungen nennen nur die Nummer des Schülers in der Liste – keine
    /// Namen und keine Prüfsummen (Entscheidung 87).
    /// </summary>
    public static List<string> Pruefen(SchildImportDto dto)
    {
        var f = new List<string>();
        if (dto.Schuljahr.Length == 0 || dto.Schuljahr.Length > 20) f.Add("Schuljahr fehlt.");
        if (dto.GiltAb == default) f.Add("„Änderungen gelten ab“ (giltAb) fehlt.");
        if (dto.Abschnitt < 1 || dto.Abschnitt > 4) f.Add("Abschnitt muss 1 bis 4 sein.");
        if (!dto.Probelauf && dto.Vorschau == null) f.Add("Beim Import fehlt die Prüfsumme der Vorschau (vorschau).");
        if ((dto.PlanSchluessel?.Count ?? 0) > MaxSchluessel) f.Add($"Höchstens {MaxSchluessel} Plan-Schlüssel.");
        if (dto.PlanSchluessel?.Any(k => k.Length > 100) == true) f.Add("Ein Plan-Schlüssel ist länger als 100 Zeichen.");
        if (dto.Kennung != null && !KennungMuster.IsMatch(dto.Kennung))
            f.Add("Die Kennung des Schlüssels (kennung) hat ein ungültiges Format (erwartet „1A2B-3C4D“).");
        if (dto.Neuzuordnung && dto.Kennung == null) f.Add("Bei einer Neu-Zuordnung fehlt die Kennung des Schlüssels.");
        if (dto.Paare != null && (dto.Paare.Count > MaxSchueler
                                  || dto.Paare.Any(p => p == null || p.Id <= 0 || !PruefsummeMuster.IsMatch(p.ExtId))))
            f.Add("Die gewählten Paare (paare) sind unvollständig oder fehlerhaft.");
        // 5.2c-2 (191, 193): Klassenleitungen und Namen der Lehrkräfte – Meldungen ohne Namen.
        if (dto.Klassenleitungen != null)
        {
            if (dto.Klassenleitungen.Count > MaxSchluessel) f.Add($"Höchstens {MaxSchluessel} Klassenleitungen.");
            else if (dto.Klassenleitungen.Any(k => k == null || k.Klasse.Length == 0 || k.Klasse.Length > 20
                                                   || k.Kuerzel.Length == 0 || k.Kuerzel.Length > 100))
                f.Add("Die Klassenleitungen (klassenleitungen) sind unvollständig oder fehlerhaft.");
            else if (dto.Klassenleitungen.GroupBy(k => k.Klasse, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                f.Add("Eine Klasse steht mehrfach in den Klassenleitungen.");
        }
        if (dto.Lehrkraefte != null)
        {
            if (dto.Lehrkraefte.Count > MaxSchluessel) f.Add($"Höchstens {MaxSchluessel} Lehrkräfte.");
            else if (dto.Lehrkraefte.Any(l => l == null || l.Kuerzel.Length == 0 || l.Kuerzel.Length > 20
                                              || l.Kuerzel.Contains(',') || l.Nachname.Length == 0
                                              || l.Nachname.Length > 100 || l.Vorname.Length > 100))
                f.Add("Die Namen der Lehrkräfte (lehrkraefte) sind unvollständig oder fehlerhaft.");
            else if (dto.Lehrkraefte.GroupBy(l => l.Kuerzel, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                f.Add("Ein Kürzel steht mehrfach in den Namen der Lehrkräfte.");
        }
        if (dto.Schueler == null || dto.Schueler.Count == 0)
        {
            f.Add("Keine Schüler übermittelt.");
            return f;
        }
        if (dto.Schueler.Count > MaxSchueler) f.Add($"Höchstens {MaxSchueler} Schüler je Import.");

        int nr = 0, gemeldet = 0;
        foreach (var s in dto.Schueler)
        {
            nr++;
            if (gemeldet >= 20) { f.Add("… weitere Fehler nicht aufgeführt."); break; }
            var vorher = f.Count;
            if (s == null) { f.Add($"Schüler Nr. {nr}: leer."); gemeldet++; continue; }
            if (!PruefsummeMuster.IsMatch(s.ExtId))
                f.Add($"Schüler Nr. {nr}: Prüfsumme (extId) fehlt oder hat ein ungültiges Format.");
            if ((s.ExtIdSchild != null && !PruefsummeMuster.IsMatch(s.ExtIdSchild))
                || (s.ExtIdVornameGeb != null && !PruefsummeMuster.IsMatch(s.ExtIdVornameGeb))
                || (s.ExtIdNachnameGeb != null && !PruefsummeMuster.IsMatch(s.ExtIdNachnameGeb)))
                f.Add($"Schüler Nr. {nr}: eine weitere Prüfsumme hat ein ungültiges Format.");
            if (s.Nachname.Length == 0 || s.Vorname.Length == 0 || s.Nachname.Length > 100 || s.Vorname.Length > 100)
                f.Add($"Schüler Nr. {nr}: Nachname bzw. Vorname fehlt oder ist zu lang.");
            if (s.Stufe.Length == 0 || s.Stufe.Length > 10) f.Add($"Schüler Nr. {nr}: Jahrgang (stufe) fehlt.");
            if (s.Klasse != null && s.Klasse.Length > 20) f.Add($"Schüler Nr. {nr}: Klasse ist zu lang.");
            if (StatusText(s.Status) == null)
                f.Add(s.Status == 1
                    ? $"Schüler Nr. {nr}: Status 1 (Warteliste) wird nicht übertragen."
                    : $"Schüler Nr. {nr}: unbekannter Status {s.Status} (erlaubt: 0, 2, 3, 6, 8, 9).");
            if (s.Kurse != null && (s.Kurse.Count > 100 || s.Kurse.Any(k => k.Length > 100)))
                f.Add($"Schüler Nr. {nr}: zu viele oder zu lange Kurs-Schlüssel.");
            if (f.Count > vorher) gemeldet++;
        }
        return f;
    }

    /// <summary>Rechnet Abgleich und Vorschau (schreibt nichts). Der Import ruft danach <see cref="SchildImportLauf.Schreiben"/>.</summary>
    /// <param name="halbjahrAb">P4a (206): Einstellung „2. Halbjahr ab“ (null = Standard 1. Februar).</param>
    public static async Task<SchildImportLauf> BerechnenAsync(AppDbContext db, SchildImportDto dto, bool produktiv, DateOnly heute,
        DateOnly? halbjahrAb = null)
    {
        var lauf = new SchildImportLauf(dto, produktiv, heute, halbjahrAb);
        await lauf.RechnenAsync(db);
        return lauf;
    }

    internal static string? Leer(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static string Datum(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    internal static string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    internal static string Sha(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Name zum Vergleichen (5.2c): wie die Eingabe der Prüfsumme – NFC, Leerzeichen zusammengefasst, groß.</summary>
    internal static string NameNorm(string? s)
        => Leerraum.Replace((s ?? "").Normalize(NormalizationForm.FormC).Trim(), " ").ToUpperInvariant();

    /// <summary>5.2c-2: Kürzel-Liste „L23,l41 ; L23“ → „L23, l41“ (ohne Doppelte, Groß-/Kleinschreibung egal); leer = null.</summary>
    public static string? KuerzelText(string? s)
    {
        var teile = KuerzelListe(s);
        return teile.Count == 0 ? null : string.Join(", ", teile);
    }

    /// <summary>5.2c-2: Kürzel einer Liste (Komma, Semikolon oder Leerzeichen getrennt), ohne Doppelte.</summary>
    public static List<string> KuerzelListe(string? s)
        => (s ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Grund, aus dem die Vorschau den Import sperrt (Entscheidung 168).</summary>
public sealed record SchildSperre(string Art, string Text);

/// <summary>Eine Zeile der Vorschau (nur für den Desktop; enthält Namen).</summary>
public sealed class SchildVorschauZeile
{
    public string Kategorie { get; init; } = "";
    /// <summary>Id in der Datenbank (bei neuen Schülern leer) – damit bestätigt der Desktop Abgänge.</summary>
    public int? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Klasse { get; init; }
    public string Stufe { get; init; } = "";
    public string? Aenderung { get; init; }
    public bool Vorausgewaehlt { get; init; }
    public bool KontoWirdGesperrt { get; init; }
    public string? Hinweis { get; set; }
    /// <summary>5.2c-1: bei Paaren („dieselbe Person?“, Neu-Zuordnung) die Prüfsumme des Schülers im Export.</summary>
    public string? PartnerExtId { get; init; }
    /// <summary>5.2c-1: Paar ist in dieser Vorschau angewendet (zusammengeführt bzw. zugeordnet).</summary>
    public bool Gewaehlt { get; init; }
    /// <summary>5.2c-2: Nummer der Gruppe von Paaren, die voneinander abhängen (Namensgleiche); leer = eindeutiges Paar.</summary>
    public int? Gruppe { get; init; }
    /// <summary>5.2c-2: Bei der Neu-Zuordnung gehört das Paar zum Vorschlag seiner Gruppe („Vorschlag übernehmen“).</summary>
    public bool Vorschlag { get; init; }
}

/// <summary>Ein Plan-Schlüssel des Grundplans, der am „gilt ab“ gilt.</summary>
internal sealed record SchildPlanEintrag(LerngruppeTyp Typ, string Schluessel, string Stufe, string? Fach, Kursart Kursart, string? Lehrer)
{
    public string Id => $"{Typ}|{Schluessel.ToUpperInvariant()}";
}

/// <summary>Klassen, Kurse und Sek-I-Gruppen einer Grundplan-Version (wie Seed und Gruppeneditor).</summary>
internal sealed class SchildPlanIndex
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public Dictionary<string, SchildPlanEintrag> Klassen { get; } = new(Cmp);
    public Dictionary<string, SchildPlanEintrag> Kurse { get; } = new(Cmp);
    public Dictionary<string, SchildPlanEintrag> Gruppen { get; } = new(Cmp);

    public static SchildPlanIndex Aus(List<GrundplanSlot> slots)
    {
        var p = new SchildPlanIndex();
        var unterricht = slots
            .Where(s => string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(s.LerngruppeKey))
            .ToList();

        foreach (var g in unterricht.Where(s => s.LerngruppeTyp == "Klasse")
                     .GroupBy(s => s.LerngruppeKey.Trim(), Cmp))
            p.Klassen[g.Key] = new SchildPlanEintrag(LerngruppeTyp.Klasse, g.Key, Jahrgang(g.Key), null, Kursart.Keine,
                PseudoDatenGenerator.HaeufigsteLehrkraft(g));

        foreach (var g in unterricht.Where(s => s.LerngruppeTyp == "Kurs")
                     .GroupBy(s => s.LerngruppeKey.Trim(), Cmp))
        {
            var erster = g.First();
            var (_, kursart, fach) = PseudoDatenGenerator.ZerlegeKursFach(erster.Fach);
            p.Kurse[g.Key] = new SchildPlanEintrag(LerngruppeTyp.Kurs, g.Key, erster.Klasse.Trim().ToUpperInvariant(),
                fach, kursart, PseudoDatenGenerator.HaeufigsteLehrkraft(g));
        }

        foreach (var g in unterricht.Where(s => s.LerngruppeTyp == "Klasse" && !string.IsNullOrWhiteSpace(s.Schuelergruppe))
                     .GroupBy(s => s.Schuelergruppe!.Trim(), Cmp))
        {
            var klassen = g.Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0)
                .Distinct(Cmp).OrderBy(k => k, Cmp).ToList();
            string fach = string.Join(", ", g.Select(s => s.Fach.Trim()).Where(f => f.Length > 0)
                .Distinct(Cmp).OrderBy(f => f, Cmp));
            string? lehrer = g.Select(s => s.Lehrer.Trim()).Where(l => l.Length > 0)
                .Distinct(Cmp).OrderBy(l => l, Cmp).FirstOrDefault();
            p.Gruppen[g.Key] = new SchildPlanEintrag(LerngruppeTyp.Gruppe, g.Key, Jahrgang(klassen.FirstOrDefault() ?? ""),
                fach, Kursart.Keine, lehrer);
        }
        return p;
    }

    public SchildPlanEintrag? Klasse(string key) => Klassen.TryGetValue(key, out var e) ? e : null;

    /// <summary>Kurs (Oberstufe) bzw. Sek-I-Gruppe zu einem Schlüssel der Kursliste.</summary>
    public SchildPlanEintrag? KursOderGruppe(string key)
        => Kurse.TryGetValue(key, out var k) ? k : Gruppen.TryGetValue(key, out var g) ? g : null;

    private static string Jahrgang(string klasse)
        => klasse.Length >= 2 && char.IsDigit(klasse[0]) && char.IsDigit(klasse[1]) ? klasse[..2] : "";
}

/// <summary>
/// Ein Abgleich (Probelauf oder Import). Rechnet alles im Speicher und merkt die Änderungen als
/// Aktionen vor; <see cref="Schreiben"/> führt sie aus (der Aufrufer speichert und bestätigt die Transaktion).
/// </summary>
public sealed class SchildImportLauf
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    private readonly SchildImportDto _dto;
    private readonly bool _produktiv;
    private readonly DateOnly _heute;
    private readonly DateOnly _giltAb;
    private readonly DateOnly _vortag;
    private readonly string _sj;
    private readonly DateTime _jetzt = DateTime.UtcNow;
    private DateOnly _kalStart;

    // Vorgemerkte Änderungen (nur der Import führt sie aus).
    private readonly List<Action<AppDbContext>> _schreiben = new();
    private readonly Dictionary<int, Action<AppDbContext>> _abgaenge = new();
    private readonly HashSet<int> _abgangLautSchildIds = new();
    private readonly HashSet<int> _geaenderteIds = new();
    private readonly HashSet<Zugehoerigkeit> _beendet = new();
    private readonly List<(int Id, string ExtId)> _angewendetePaare = new();   // 5.2c-1: Teil der Prüfsumme der Vorschau
    private string? _letzteKennung;                                             // Kennung beim letzten Import dieser Art
    private readonly DateOnly? _halbjahrAb;                                     // P4a: Einstellung „2. Halbjahr ab“

    public SchildImportLauf(SchildImportDto dto, bool produktiv, DateOnly heute, DateOnly? halbjahrAb = null)
    {
        _dto = dto;
        _produktiv = produktiv;
        _heute = heute;
        _halbjahrAb = halbjahrAb;
        _giltAb = dto.GiltAb;
        _vortag = dto.GiltAb.AddDays(-1);
        _sj = dto.Schuljahr;
    }

    public List<SchildSperre> Sperren { get; } = new();
    public List<string> Hinweise { get; } = new();
    /// <summary>P4a (206): Warnungen zum Halbjahr – der Desktop fragt vor dem Import nach (keine Sperre).</summary>
    public List<string> Warnungen { get; } = new();
    public bool SeedEntfernenMoeglich { get; private set; }
    public bool Gesperrt => Sperren.Count > 0;
    public string? GrundplanBezeichnung { get; private set; }
    public DateOnly? GrundplanAb { get; private set; }
    public string Stand { get; private set; } = "";
    public string VorschauPruefsumme { get; private set; } = "";
    public List<SchildVorschauZeile> Zeilen { get; } = new();

    public int ImExport, Neu, NameGeaendert, Klassenwechsel, Kurswechsel, StatusGeaendert,
        AbgangLautSchild, FehltImExport, Unveraendert, AbgangNichtInDatenbank;
    // 5.2c-1: Paare „dieselbe Person?“ (normal) bzw. der Neu-Zuordnung
    public int DieselbePerson, Zusammengefuehrt, ZuordnungPaare, NeuZugeordnet;
    // 5.2c-2: Klassenleitungen (erstmals eingetragen, geändert), Namen der Lehrkräfte; Gruppen der Neu-Zuordnung
    public int KlassenleitungNeu, KlassenleitungGeaendert, LehrkraefteNeu, LehrkraefteGeaendert, LehrkraefteEntfernt,
        LehrkraefteGesamt, PaarGruppen, PaarGruppenMitVorschlag;

    public List<(string Schluessel, string Art, int Schueler)> Unbekannt { get; } = new();
    public List<(string Typ, string Schluessel)> NeueLerngruppen { get; } = new();
    public List<(string Typ, string Schluessel, int VonHand, int AusSchild, bool AbSchuljahresbeginn)> Uebernahmen { get; } = new();

    /// <summary>Darf diese Schüler-Id beim Import als Abgang bestätigt werden?</summary>
    public bool IstAbgangKandidat(int id) => _abgaenge.ContainsKey(id);

    // ----------------------------------------------------------------
    //  Eingang je Schüler der Nutzlast
    // ----------------------------------------------------------------

    private sealed class Eingang
    {
        public SchildSchuelerDto D = null!;
        public int Nr;
        public string Status = "aktiv";
        public bool Abgang;
        public SchildPlanEintrag? Klasse;
        public bool KlasseUnbekannt;
        public List<SchildPlanEintrag> Kurse = new();
        /// <summary>Unbekannte Schlüssel: vorhandene Zeilen dazu bleiben unverändert (übersprungen).</summary>
        public HashSet<string> Geschuetzt = new(StringComparer.OrdinalIgnoreCase);
        public Schueler? Treffer;
        public bool UeberSchild;
        /// <summary>5.2c-1: über ein gewähltes Paar gefunden – Text des Signals („gleicher Vorname und Geburtsdatum“).</summary>
        public string? Paar;
        /// <summary>5.2c-1: Hinweise zu nicht gewählten Paaren (für die Zeile „neu“).</summary>
        public List<string> PaarHinweise = new();
    }

    // ----------------------------------------------------------------
    //  Rechnen
    // ----------------------------------------------------------------

    internal async Task RechnenAsync(AppDbContext db)
    {
        var dto = _dto;
        ImExport = dto.Schueler!.Count;

        // ---- 1. Umgebung (83) ----
        if (!_produktiv && !dto.Pseudo)
            Sperre("Umgebung", "Die Entwicklungs-API nimmt nur Pseudodaten an (Entscheidung 83). Echte SchILD-Daten " +
                               "werden nur im Echtbetrieb importiert.");
        if (_produktiv && dto.Pseudo)
            Sperre("Umgebung", "Der Echtbetrieb nimmt keine Pseudodaten an (Entscheidung 83).");
        if (Gesperrt) return;

        // ---- 2. Kalender und Grundplan am „gilt ab“ ----
        var kal = await db.Kalender.AsNoTracking().FirstOrDefaultAsync(k => k.Schuljahr == _sj);
        if (kal == null)
        {
            var vorhanden = await db.Kalender.AsNoTracking().Select(k => k.Schuljahr).ToListAsync();
            Sperre("Kalender", $"Für das Schuljahr „{_sj}“ gibt es keinen Kalender. " + (vorhanden.Count > 0
                ? $"Vorhanden: {string.Join(", ", vorhanden.Select(v => "„" + v + "“"))}."
                : "Zuerst den Grundplan veröffentlichen."));
            return;
        }
        _kalStart = kal.Start;
        if (_giltAb < kal.Start || _giltAb > kal.Ende)
            Sperre("Kalender", $"„Änderungen gelten ab“ ({D(_giltAb)}) liegt nicht im Schuljahr {_sj} " +
                               $"({D(kal.Start)} bis {D(kal.Ende)}).");

        var version = await db.GrundplanVersionen.AsNoTracking()
            .Where(v => v.Schuljahr == _sj && v.GueltigAb <= _giltAb)
            .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync();
        if (version == null)
        {
            Sperre("Grundplan", $"Am {D(_giltAb)} gilt noch kein Grundplan – zuerst „Grundplan veröffentlichen…“ " +
                                "(ab Schuljahresbeginn).");
            return;
        }
        GrundplanBezeichnung = version.Bezeichnung;
        GrundplanAb = version.GueltigAb;
        if (dto.Plan != null && !Cmp.Equals(dto.Plan, version.Bezeichnung.Trim()))
            Hinweise.Add($"Zugeordnet wurde mit der Plan-Spalte „{dto.Plan}“, am {D(_giltAb)} gilt aber der Grundplan " +
                         $"„{version.Bezeichnung}“ (ab {D(version.GueltigAb)}).");

        // ---- 2a. P4a (206): Abschnitt und Stichtag zum 2. Halbjahr – Warnungen, keine Sperre ----
        bool ohneNeuenPlan = HalbjahrPruefen(kal, version);

        var slots = await db.GrundplanSlots.AsNoTracking().Where(s => s.VersionId == version.Id).ToListAsync();
        var plan = SchildPlanIndex.Aus(slots);

        // ---- 3. Datenbankstand (geändert wird nur beim Import, über die vorgemerkten Aktionen) ----
        var schueler = await db.Schueler.ToListAsync();
        var lerngruppen = await db.Lerngruppen.Where(l => l.Schuljahr == _sj).ToListAsync();
        var zug = await db.Zugehoerigkeiten.Where(z => z.Schuljahr == _sj).ToListAsync();
        var konten = await db.Benutzer
            .Where(b => b.Rolle == BenutzerRolle.Schueler && b.SchuelerId != null)
            .ToListAsync();
        var protokolle = await db.SchildImportProtokolle.AsNoTracking().Where(p => p.Schuljahr == _sj).ToListAsync();
        // 5.2c-1 (189): Kennung des Schlüssels beim letzten Import dieser Art (über alle Schuljahre).
        _letzteKennung = await db.SchildImportProtokolle.AsNoTracking()
            .Where(p => p.IstPseudo == dto.Pseudo && p.SchluesselKennung != null)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Select(p => p.SchluesselKennung).FirstOrDefaultAsync();
        // 5.2c-2 (193): Namen der Lehrkräfte dieser Art (werden mit Lehrkraefte.dat ganz ersetzt).
        var lehrkraefte = await db.Lehrkraefte.Where(l => l.IstPseudo == dto.Pseudo).ToListAsync();

        Stand = StandPruefsumme(version, schueler, lerngruppen, zug, protokolle, lehrkraefte);
        VorschauPruefsumme = SchildImportService.Sha(Stand + "|" + NutzlastPruefsumme(dto));

        if (dto.Pseudo && schueler.Any(s => !s.IstPseudo))
            Sperre("Datenbank", "Die Datenbank enthält echte Schüler – Pseudodaten werden nicht dazu importiert.");
        if (!dto.Pseudo && schueler.Any(s => s.IstPseudo))
            Sperre("Datenbank", "Die Datenbank enthält noch Pseudo-Schüler. Vor dem ersten echten Import müssen sie " +
                                "entfernt werden („Pseudodaten entfernen“, Echtbetrieb-Paket 5.5).");
        int seed = schueler.Count(s => s.IstPseudo && string.IsNullOrEmpty(s.ExtId));
        if (seed > 0)
        {
            SeedEntfernenMoeglich = !_produktiv;
            Sperre("SeedSchueler", $"In der Datenbank stehen {seed} Schüler aus seed/pseudodaten. Sie lassen sich nicht " +
                                   "mit importierten Schülern mischen." + (_produktiv ? "" :
                                   " „Seed-Schüler entfernen“ löscht sie samt ihren Klassenbuchdaten (wie ein neuer Seed); " +
                                   "danach die Vorschau neu berechnen."));
        }
        if (Gesperrt) return;

        var lgById = lerngruppen.ToDictionary(l => l.Id);
        var zugJe = zug.Where(z => lgById.ContainsKey(z.LerngruppeId))
            .GroupBy(z => z.SchuelerId)
            .ToDictionary(g => g.Key, g => g.ToList());
        List<Zugehoerigkeit> ZeilenVon(int id) => zugJe.TryGetValue(id, out var l) ? l : new List<Zugehoerigkeit>();

        // ---- 4. Plan-Schlüssel der Nutzlast auflösen ----
        var unbekannt = new Dictionary<string, (string Art, int Anzahl)>(Cmp);
        var alleSchluessel = new HashSet<string>(Cmp);
        void MerkeUnbekannt(string key, string art, int anzahl)
        {
            var bisher = unbekannt.TryGetValue(key, out var u) ? u : (art, 0);
            unbekannt[key] = (bisher.Item1, bisher.Item2 + anzahl);
        }

        var eingaenge = new List<Eingang>();
        int nr = 0;
        foreach (var p in dto.Schueler)
        {
            var e = new Eingang { D = p, Nr = ++nr, Status = SchildImportService.StatusText(p.Status) ?? "aktiv" };
            e.Abgang = e.Status == "abgang";
            if (!e.Abgang)
            {
                if (p.Klasse != null && e.Status != "extern")      // Extern (6): nur Kurse (80)
                {
                    alleSchluessel.Add(p.Klasse);
                    e.Klasse = plan.Klasse(p.Klasse);
                    if (e.Klasse == null)
                    {
                        e.KlasseUnbekannt = true;
                        MerkeUnbekannt(p.Klasse, "Klasse", 1);
                    }
                }
                foreach (var key in (p.Kurse ?? new List<string>()).Distinct(Cmp))
                {
                    alleSchluessel.Add(key);
                    var k = plan.KursOderGruppe(key);
                    if (k != null) e.Kurse.Add(k);
                    else
                    {
                        e.Geschuetzt.Add(key);
                        MerkeUnbekannt(key, "Kurs/Gruppe", 1);
                    }
                }
            }
            eingaenge.Add(e);
        }

        // Verwaltet = alle Lerngruppen, deren Liste ab jetzt aus SchILD kommt (Kopf + Schüler).
        var verwaltet = new Dictionary<string, SchildPlanEintrag>(StringComparer.Ordinal);
        foreach (var key in (dto.PlanSchluessel ?? new List<string>()).Distinct(Cmp))
        {
            alleSchluessel.Add(key);
            var k = plan.Klasse(key) ?? plan.KursOderGruppe(key);
            if (k != null) verwaltet[k.Id] = k;
            else MerkeUnbekannt(key, "Liste der SchILD-Gruppen", 0);
        }
        foreach (var e in eingaenge)
        {
            if (e.Klasse != null) verwaltet[e.Klasse.Id] = e.Klasse;
            foreach (var k in e.Kurse) verwaltet[k.Id] = k;
        }
        foreach (var (key, u) in unbekannt.OrderBy(x => x.Key, Cmp))
            Unbekannt.Add((key, u.Art, u.Anzahl));
        if (alleSchluessel.Count > 0 && unbekannt.Count * 10 > alleSchluessel.Count)
            Sperre("UnbekannteSchluessel", $"{unbekannt.Count} von {alleSchluessel.Count} Plan-Schlüsseln gibt es im Grundplan " +
                                           $"am {D(_giltAb)} nicht (mehr als 10 %) – passt die Plan-Spalte? Grundplan zuerst " +
                                           "veröffentlichen.");
        // P4a (206): zum 2. Halbjahr ohne eigenen Grundplan und dazu unbekannte Schlüssel – SchILD liefert dann vermutlich
        // schon die Kurse des 2. Halbjahres, im Plan stehen noch die des 1. Halbjahres.
        if (ohneNeuenPlan && Unbekannt.Count > 0)
            Warnungen.Add($"{Unbekannt.Count} Plan-Schlüssel der Zuordnung gibt es im Grundplan „{version.Bezeichnung}“ nicht – " +
                          "vermutlich stehen dort noch die Kurse des 1. Halbjahres, SchILD liefert schon die des 2. Halbjahres. " +
                          "Dann zuerst den neuen Grundplan veröffentlichen.");

        // ---- 5. Doppelte Prüfsummen in der Nutzlast ----
        foreach (var g in eingaenge.Where(e => e.D.ExtIdSchild != null)
                     .GroupBy(e => e.D.ExtIdSchild!, StringComparer.Ordinal).Where(gr => gr.Count() > 1).Take(5))
            Sperre("DoppeltePruefsumme", $"Die Schüler Nr. {string.Join(", ", g.Select(x => x.Nr))} haben dieselbe SchILD-ID.");
        foreach (var g in eingaenge.GroupBy(e => e.D.ExtId, StringComparer.Ordinal)
                     .Where(gr => gr.Count() > 1 && gr.Any(x => x.D.ExtIdSchild == null)).Take(5))
            Sperre("DoppeltePruefsumme", $"Die Schüler Nr. {string.Join(", ", g.Select(x => x.Nr))} haben dieselbe Prüfsumme " +
                                         "(gleicher Name und gleiches Geburtsdatum) und keine SchILD-ID.");

        // ---- 6. Erkennung: zuerst SchILD-ID, dann Name + Geburtsdatum (85, 167) ----
        var kandidaten = schueler.Where(s => !string.IsNullOrEmpty(s.ExtId) && s.IstPseudo == dto.Pseudo).ToList();

        // 5.2c-1 (189/190): Kennung des Schlüssels und Voraussetzungen der Neu-Zuordnung. Diese Sperren beenden die
        // Rechnung sofort (mit einem fremden Schlüssel wären alle Zeilen „neu“ + „fehlt“); die älteren Sperren nicht.
        int sperrenVorher = Sperren.Count;
        if (dto.Neuzuordnung && kandidaten.Count == 0)
            Sperre("Neuzuordnung", "In der Datenbank stehen noch keine importierten Schüler dieser Art – eine Neu-Zuordnung " +
                                   "ist nicht nötig; ein normaler Import genügt.");
        if (kandidaten.Count > 0 && _letzteKennung != null && dto.Kennung != null)
        {
            bool gleich = string.Equals(_letzteKennung, dto.Kennung, StringComparison.OrdinalIgnoreCase);
            if (!dto.Neuzuordnung && !gleich)
                Sperre("Schluessel", $"Der Schlüssel dieses Rechners (Kennung {dto.Kennung}) ist ein anderer als beim letzten " +
                                     $"Import (Kennung {_letzteKennung}). Mit einem anderen Schlüssel werden die Schüler nicht " +
                                     "wiedererkannt. Abhilfe: „Schlüssel einlesen…“ mit der Sicherung – nur wenn der bisherige " +
                                     "Schlüssel verloren ist: „Schlüssel verloren – neu zuordnen…“.");
            if (dto.Neuzuordnung && gleich)
                Sperre("Neuzuordnung", $"Der Schlüssel dieses Rechners (Kennung {dto.Kennung}) ist derselbe wie beim letzten " +
                                       "Import – eine Neu-Zuordnung ist nicht nötig.");
        }
        if (Sperren.Count > sperrenVorher) return;
        var nachSchild = kandidaten.Where(s => s.ExtIdSchild != null)
            .GroupBy(s => s.ExtIdSchild!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var nachExt = kandidaten.GroupBy(s => s.ExtId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var vergeben = new HashSet<int>();
        bool dbDoppelt = false;
        foreach (var e in eingaenge.Where(x => x.D.ExtIdSchild != null))
        {
            if (!nachSchild.TryGetValue(e.D.ExtIdSchild!, out var l)) continue;
            if (l.Count > 1) { dbDoppelt = true; continue; }
            if (!vergeben.Add(l[0].Id)) continue;
            e.Treffer = l[0];
            e.UeberSchild = true;
        }
        foreach (var e in eingaenge.Where(x => x.Treffer == null))
        {
            if (!nachExt.TryGetValue(e.D.ExtId, out var l)) continue;
            // Eine andere SchILD-ID heißt: eine andere Person mit gleichem Namen und Geburtsdatum.
            var passend = l.Where(s => !vergeben.Contains(s.Id) && (s.ExtIdSchild == null || e.D.ExtIdSchild == null)).ToList();
            if (passend.Count > 1) { dbDoppelt = true; continue; }
            if (passend.Count == 0) continue;
            vergeben.Add(passend[0].Id);
            e.Treffer = passend[0];
        }
        if (dbDoppelt)
            Sperre("DoppeltePruefsumme", "In der Datenbank stehen mehrere Schüler mit derselben Prüfsumme – sie lassen sich " +
                                         "nicht eindeutig zuordnen. Bitte die Datenbank prüfen lassen (zwei Datensätze " +
                                         "nachträglich zusammenführen geht noch nicht).");
        if (dto.Neuzuordnung)
        {
            int erkannt = eingaenge.Count(x => x.Treffer != null);
            if (erkannt > 0)
            {
                Sperre("Neuzuordnung", $"{erkannt} Schüler werden mit dem Schlüssel dieses Rechners schon erkannt – der Schlüssel " +
                                       "passt, eine Neu-Zuordnung ist nicht nötig („Vorschau berechnen“ ohne Neu-Zuordnung).");
                return;
            }
        }

        // ---- 6b. Paare (5.2c-1): „dieselbe Person?“ (186–188) bzw. Neu-Zuordnung über Name + Klasse (190) ----
        PaareBilden(eingaenge, kandidaten, vergeben, ZeilenVon, lgById);

        // ---- 7. Lerngruppen: vorhandene finden, fehlende anlegen, Übernahmen (81) ----
        var vorhandeneLg = new Dictionary<string, Lerngruppe>(StringComparer.Ordinal);
        foreach (var g in lerngruppen.GroupBy(l => $"{l.Typ}|{l.Bezeichnung.Trim().ToUpperInvariant()}"))
        {
            // Gruppen wie der Gruppeneditor (kleinste Id), Klassen/Kurse wie das Stundenblatt (größte Id).
            var liste = g.OrderBy(l => l.Id).ToList();
            vorhandeneLg[g.Key] = liste[0].Typ == LerngruppeTyp.Gruppe ? liste[0] : liste[^1];
            if (liste.Count > 1)
                Hinweise.Add($"Die Lerngruppe „{liste[0].Bezeichnung}“ gibt es {liste.Count}-mal – der Import nimmt die " +
                             $"mit der Id {vorhandeneLg[g.Key].Id}.");
        }

        var ziel = new Dictionary<string, Lerngruppe>(StringComparer.Ordinal);
        var wechseltHeute = new HashSet<string>(StringComparer.Ordinal);       // neu angelegt oder übernommen
        var neuAngelegt = new HashSet<string>(StringComparer.Ordinal);
        var abSchuljahresbeginn = new HashSet<string>(StringComparer.Ordinal); // Sek-I-Gruppe ohne Liste von Hand
        var vonHand = new Dictionary<string, int>(StringComparer.Ordinal);
        var zielAnzahl = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, p) in verwaltet.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!vorhandeneLg.TryGetValue(id, out var lg))
            {
                lg = NeueLerngruppe(p);
                var neu = lg;
                _schreiben.Add(d => d.Lerngruppen.Add(neu));
                NeueLerngruppen.Add((TypText(p.Typ), p.Schluessel));
                wechseltHeute.Add(id);
                neuAngelegt.Add(id);
                if (p.Typ == LerngruppeTyp.Gruppe) abSchuljahresbeginn.Add(id);
            }
            else if (!Gueltigkeit.IstSchild(lg.Quelle) || lg.SchildAb == null)
            {
                wechseltHeute.Add(id);
                var uebernommen = lg;
                _schreiben.Add(_ =>
                {
                    uebernommen.Quelle = Gueltigkeit.QuelleSchild;
                    uebernommen.SchildAb = uebernommen.SchildAb is DateOnly ab && ab <= _giltAb ? ab : _giltAb;
                });
                if (p.Typ != LerngruppeTyp.Klasse)
                {
                    // Die Liste der Fachlehrkraft (bzw. ältere Zeilen) endet am Vortag (81).
                    var eigene = zug.Where(z => z.LerngruppeId == lg.Id && !Gueltigkeit.IstSchild(z.Quelle)).ToList();
                    var offen = eigene.Where(OffenAb).ToList();
                    foreach (var z in offen) Ende(z);
                    vonHand[id] = offen.Select(z => z.SchuelerId).Distinct().Count();
                    if (p.Typ == LerngruppeTyp.Gruppe && eigene.Count == 0) abSchuljahresbeginn.Add(id);
                }
            }
            ziel[id] = lg;
        }

        // SchILD-Gruppen, die nicht mehr zugeordnet sind: ihre SchILD-Zeilen enden (Hinweis). Unbekannte Schlüssel
        // (nicht im Grundplan) bleiben dagegen, wie sie sind.
        foreach (var lg in lerngruppen.Where(l => l.Typ != LerngruppeTyp.Klasse && Gueltigkeit.IstSchild(l.Quelle)
                                                 && !verwaltet.ContainsKey($"{l.Typ}|{l.Bezeichnung.Trim().ToUpperInvariant()}")
                                                 && !unbekannt.ContainsKey(l.Bezeichnung.Trim())))
        {
            int n = zug.Count(z => z.LerngruppeId == lg.Id && Gueltigkeit.IstSchild(z.Quelle) && OffenAb(z));
            if (n > 0)
                Hinweise.Add($"„{lg.Bezeichnung}“ kam bisher aus SchILD, steht aber nicht mehr in der Zuordnung – die " +
                             $"Zugehörigkeiten von {n} Schülern enden am {D(_vortag)}; die Liste bleibt nur lesbar.");
        }

        // ---- 7b. Klassenleitung laut Klassen.dat (5.2c-2, Entscheidung 191) ----
        KlassenleitungVormerken(plan, ziel);

        // ---- 7c. Namen der Lehrkräfte aus Lehrkraefte.dat (5.2c-2, Entscheidung 193) ----
        LehrkraefteVormerken(slots, lehrkraefte);

        // ---- 8. Gefundene Schüler: Änderungen ermitteln ----
        var getroffen = new HashSet<int>();
        var neueZeilen = new List<SchildVorschauZeile>();
        foreach (var e in eingaenge.Where(x => x.Treffer != null))
        {
            var s = e.Treffer!;
            getroffen.Add(s.Id);
            var zeilen = ZeilenVon(s.Id);
            string statusAlt = string.IsNullOrEmpty(s.Status) ? "aktiv" : s.Status!;
            string altName = $"{s.Vorname} {s.Nachname}";

            // 5.2c-1 (190): bei der Neu-Zuordnung bekommt jeder zugeordnete Datensatz alle neuen Prüfsummen – auch ein
            // schon abgegangener und einer, dessen Abgang nicht bestätigt wird.
            if (_dto.Neuzuordnung)
            {
                var d0 = e.D;
                _schreiben.Add(_ => Identitaet(s, d0, ersetzen: true));
            }

            if (e.Abgang)
            {
                if (statusAlt == "abgang") { Unveraendert++; continue; }
                AbgangLautSchild++;
                _abgangLautSchildIds.Add(s.Id);
                Zeilen.Add(new SchildVorschauZeile
                {
                    Kategorie = SchildImportService.Kategorie.AbgangLautSchild, Id = s.Id, Name = Name(e.D),
                    Klasse = KlasseAktuell(zeilen, lgById), Stufe = e.D.Stufe,
                    Aenderung = $"Status {e.D.Status} laut SchILD" + (e.D.Status == 8 ? " (Abschluss)" : " (Abgang)"),
                    Vorausgewaehlt = true, KontoWirdGesperrt = konten.Any(k => k.SchuelerId == s.Id && !k.Gesperrt)
                });
                AbgangVormerken(s, zeilen, konten, e.D);
                continue;
            }

            var texte = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Merke(string kat, string text)
            {
                if (!texte.TryGetValue(kat, out var l)) texte[kat] = l = new List<string>();
                l.Add(text);
            }

            if (e.Paar != null && !_dto.Neuzuordnung)
                Merke(SchildImportService.Kategorie.Zusammengefuehrt, $"bisher {altName} – {e.Paar} (zusammengeführt)");
            if (e.UeberSchild && !string.Equals(s.ExtId, e.D.ExtId, StringComparison.Ordinal))
            {
                string alt = $"{s.Vorname} {s.Nachname}";
                Merke(SchildImportService.Kategorie.NameGeaendert, alt == Name(e.D)
                    ? "Geburtsdatum bzw. Schreibweise geändert (über die SchILD-ID erkannt)"
                    : $"bisher {alt} (über die SchILD-ID erkannt)");
            }
            if (statusAlt != e.Status)
                Merke(SchildImportService.Kategorie.StatusGeaendert, $"{StatusAnzeige(statusAlt)} → {StatusAnzeige(e.Status)}"
                    + (statusAlt == "abgang" && konten.Any(k => k.SchuelerId == s.Id && k.Gesperrt)
                        ? " (das Konto bleibt gesperrt – der Admin kann es entsperren)" : ""));
            if (!Cmp.Equals(s.Stufe, e.D.Stufe))
                Merke(SchildImportService.Kategorie.StatusGeaendert, $"Jahrgang {s.Stufe} → {e.D.Stufe}");

            DateOnly start = Start(e.D);

            // -- Klasse --
            var klZeilen = zeilen.Where(z => lgById[z.LerngruppeId].Typ == LerngruppeTyp.Klasse && OffenAb(z)).ToList();
            var klAktuell = klZeilen.Where(z => z.GiltAm(_giltAb)).OrderByDescending(z => z.GueltigVon ?? DateOnly.MinValue).FirstOrDefault()
                            ?? klZeilen.OrderBy(z => z.GueltigVon ?? DateOnly.MinValue).FirstOrDefault();
            string? klAlt = klAktuell == null ? null : lgById[klAktuell.LerngruppeId].Bezeichnung.Trim();
            bool klasseGewechselt = false;
            if (!e.KlasseUnbekannt)
            {
                bool vorhanden = false;
                foreach (var z in klZeilen)
                {
                    if (e.Klasse != null && Cmp.Equals(lgById[z.LerngruppeId].Bezeichnung.Trim(), e.Klasse.Schluessel))
                    {
                        vorhanden = true;
                        Vorziehen(z, start);
                    }
                    else Ende(z);
                }
                if (e.Klasse != null && !vorhanden) NeueZeile(s, ziel[e.Klasse.Id], start);
                string? klNeu = e.Klasse?.Schluessel;
                if (!Cmp.Equals(klAlt ?? "", klNeu ?? ""))
                {
                    klasseGewechselt = true;
                    Merke(SchildImportService.Kategorie.Klassenwechsel,
                        $"{klAlt ?? "—"} → {klNeu ?? (e.Status == "extern" ? "— (extern, nur Kurse)" : "—")}"
                        + (start > _giltAb ? $" ab {D(start)}" : ""));
                }
            }

            // -- Kurse und Gruppen (nur Zeilen aus SchILD; die Listen von Hand enden auf Gruppenebene) --
            DateOnly? StartFuer(SchildPlanEintrag p)
                => p.Typ == LerngruppeTyp.Gruppe && abSchuljahresbeginn.Contains(p.Id) && !klasseGewechselt
                   && start == _giltAb && klAktuell != null
                    ? klAktuell.GueltigVon          // seit dem Eintritt in die jetzige Klasse (leer = Schuljahresbeginn)
                    : start;

            var schildZeilen = zeilen.Where(z => lgById[z.LerngruppeId].Typ != LerngruppeTyp.Klasse
                                                 && Gueltigkeit.IstSchild(z.Quelle) && OffenAb(z)).ToList();
            var plus = new SortedSet<string>(Cmp);
            var minus = new SortedSet<string>(Cmp);
            foreach (var z in schildZeilen)
            {
                var lg = lgById[z.LerngruppeId];
                string key = lg.Bezeichnung.Trim();
                var p = e.Kurse.FirstOrDefault(k => k.Typ == lg.Typ && Cmp.Equals(k.Schluessel, key));
                if (p != null) Vorziehen(z, StartFuer(p));
                else if (e.Geschuetzt.Contains(key)) { /* unbekannter Schlüssel: bleibt, wie er ist */ }
                else
                {
                    Ende(z);
                    if (!wechseltHeute.Contains($"{lg.Typ}|{key.ToUpperInvariant()}")) minus.Add(key);
                }
            }
            foreach (var p in e.Kurse)
            {
                zielAnzahl[p.Id] = (zielAnzahl.TryGetValue(p.Id, out var n) ? n : 0) + 1;
                bool hat = schildZeilen.Any(z => lgById[z.LerngruppeId].Typ == p.Typ
                                                 && Cmp.Equals(lgById[z.LerngruppeId].Bezeichnung.Trim(), p.Schluessel));
                if (hat) continue;
                NeueZeile(s, ziel[p.Id], StartFuer(p));
                if (!wechseltHeute.Contains(p.Id)) plus.Add(p.Schluessel);
            }
            if (plus.Count + minus.Count > 0)
                Merke(SchildImportService.Kategorie.Kurswechsel,
                    string.Join(", ", plus.Select(k => "+ " + k).Concat(minus.Select(k => "– " + k)))
                    + (start > _giltAb && plus.Count > 0 ? $" (ab {D(start)})" : ""));

            // -- Personendaten übernehmen (immer: Prüfsummen, Name, Jahrgang, Status) --
            var dtoS = e.D;
            string statusNeu = e.Status;
            _schreiben.Add(_ =>
            {
                Identitaet(s, dtoS);
                s.Status = statusNeu;
                s.AbgangAm = null;
                s.ImportAm = _jetzt;
            });

            if (texte.Count == 0) { Unveraendert++; continue; }
            if (texte.ContainsKey(SchildImportService.Kategorie.NameGeaendert) || texte.ContainsKey(SchildImportService.Kategorie.StatusGeaendert)
                || texte.ContainsKey(SchildImportService.Kategorie.Zusammengefuehrt))
                _geaenderteIds.Add(s.Id);
            foreach (var (kat, l) in texte)
            {
                switch (kat)
                {
                    case SchildImportService.Kategorie.NameGeaendert: NameGeaendert++; break;
                    case SchildImportService.Kategorie.Klassenwechsel: Klassenwechsel++; break;
                    case SchildImportService.Kategorie.Kurswechsel: Kurswechsel++; break;
                    case SchildImportService.Kategorie.StatusGeaendert: StatusGeaendert++; break;
                }
                Zeilen.Add(new SchildVorschauZeile
                {
                    Kategorie = kat, Id = s.Id, Name = Name(e.D), Klasse = e.Klasse?.Schluessel ?? klAlt, Stufe = e.D.Stufe,
                    Aenderung = string.Join("; ", l)
                });
            }
        }

        // ---- 9. Neue Schüler ----
        foreach (var e in eingaenge.Where(x => x.Treffer == null))
        {
            if (e.Abgang) { AbgangNichtInDatenbank++; continue; }
            var s = new Schueler { IstPseudo = dto.Pseudo };
            Identitaet(s, e.D);
            s.Status = e.Status;
            s.ImportAm = _jetzt;
            _schreiben.Add(d => d.Schueler.Add(s));
            DateOnly start = Start(e.D);
            if (e.Klasse != null) NeueZeile(s, ziel[e.Klasse.Id], start);
            foreach (var p in e.Kurse)
            {
                zielAnzahl[p.Id] = (zielAnzahl.TryGetValue(p.Id, out var n) ? n : 0) + 1;
                NeueZeile(s, ziel[p.Id], start);
            }
            Neu++;
            var teile = new List<string>();
            if (e.Status != "aktiv") teile.Add(StatusAnzeige(e.Status));
            if (start > _giltAb) teile.Add($"ab {D(start)}");
            var zeile = new SchildVorschauZeile
            {
                Kategorie = SchildImportService.Kategorie.Neu, Name = Name(e.D), Klasse = e.Klasse?.Schluessel,
                Stufe = e.D.Stufe, Aenderung = teile.Count == 0 ? null : string.Join(", ", teile),
                Hinweis = e.PaarHinweise.Count == 0 ? null : string.Join("; ", e.PaarHinweise.Distinct())
            };
            neueZeilen.Add(zeile);
        }

        // ---- 10. Fehlt im Export (nicht vorausgewählt, 80) ----
        var basis = kandidaten.Where(s => s.Status != "abgang" && ZeilenVon(s.Id).Any(OffenAb)).ToList();
        var fehlt = basis.Where(s => !getroffen.Contains(s.Id)).OrderBy(s => s.Nachname, Cmp).ThenBy(s => s.Vorname, Cmp).ToList();
        var fehltZeilen = new List<SchildVorschauZeile>();
        foreach (var s in fehlt)
        {
            FehltImExport++;
            var zeilen = ZeilenVon(s.Id);
            string? klasse = KlasseAktuell(zeilen, lgById);
            var zeile = new SchildVorschauZeile
            {
                Kategorie = SchildImportService.Kategorie.FehltImExport, Id = s.Id, Name = $"{s.Vorname} {s.Nachname}",
                Klasse = klasse, Stufe = s.Stufe, Aenderung = "nicht im Export, kein Abgang gemeldet",
                Vorausgewaehlt = false, KontoWirdGesperrt = konten.Any(k => k.SchuelerId == s.Id && !k.Gesperrt)
            };
            // 5.2c-1: nicht gewählte Paare („dieselbe Person?“ bzw. Neu-Zuordnung) als Hinweis.
            if (_paarHinweiseDb.TryGetValue(s.Id, out var ph)) zeile.Hinweis = string.Join("; ", ph.Distinct());
            fehltZeilen.Add(zeile);
            AbgangVormerken(s, zeilen, konten, null);
        }
        Zeilen.AddRange(neueZeilen);
        Zeilen.AddRange(fehltZeilen);

        // ---- 11. Schutz vor Teil-Export (80, 168) ----
        if (basis.Count > 0 && fehlt.Count * 4 > basis.Count)
            Sperre("TeilExport", $"{fehlt.Count} von {basis.Count} Schülern der Datenbank fehlen im Export (mehr als ein " +
                                 "Viertel) – ist der Export vollständig? (Entscheidung 80)");
        var exportKlassen = new HashSet<string>(eingaenge.Select(e => e.D.Klasse).Where(k => k != null).Select(k => k!), Cmp);
        var exportStufen = new HashSet<string>(eingaenge.Select(e => e.D.Stufe), Cmp);
        var basisIds = basis.Select(s => s.Id).ToHashSet();
        var fehlendeKlassen = lerngruppen
            .Where(l => l.Typ == LerngruppeTyp.Klasse && !exportKlassen.Contains(l.Bezeichnung.Trim())
                        && zug.Any(z => z.LerngruppeId == l.Id && basisIds.Contains(z.SchuelerId) && OffenAb(z)))
            .Select(l => l.Bezeichnung.Trim()).Distinct(Cmp).OrderBy(k => k, Cmp).ToList();
        var fehlendeStufen = basis.Select(s => s.Stufe.Trim()).Where(st => st.Length > 0 && !exportStufen.Contains(st))
            .Distinct(Cmp).OrderBy(k => k, Cmp).ToList();
        if (fehlendeKlassen.Count + fehlendeStufen.Count > 0)
            Sperre("TeilExport", "Im Export fehlen ganze " + string.Join(" und ", new[]
                {
                    fehlendeKlassen.Count > 0 ? $"Klassen ({string.Join(", ", fehlendeKlassen)})" : null,
                    fehlendeStufen.Count > 0 ? $"Jahrgänge ({string.Join(", ", fehlendeStufen)})" : null
                }.Where(x => x != null)) + " – ist der Export vollständig? (Entscheidung 80)");

        // ---- 12. „Gilt ab“ nicht vor dem letzten Import (D) ----
        if (kandidaten.Count > 0)
        {
            var letzter = protokolle.Where(p => p.IstPseudo == dto.Pseudo).OrderByDescending(p => p.GiltAb).FirstOrDefault();
            if (letzter != null && _giltAb < letzter.GiltAb)
                Sperre("GiltAb", $"„Änderungen gelten ab“ ({D(_giltAb)}) liegt vor dem Stichtag des letzten Imports " +
                                 $"({D(letzter.GiltAb)}) – frühestens {D(letzter.GiltAb)} wählen.");
        }
        if (_giltAb <= _heute)
        {
            int erfasst = await db.Lektionen.CountAsync(l => l.Schuljahr == _sj && l.ErfasstAm != null
                                                             && l.Datum >= _giltAb && l.Datum <= _heute);
            if (erfasst > 0)
                Hinweise.Add($"Seit dem {D(_giltAb)} sind schon {erfasst} Stunden erfasst. Ihre Listen richten sich nach dem " +
                             "Import; wer dort eine Anwesenheit hat, bleibt sichtbar.");
        }
        if (_giltAb > _heute && AbgangLautSchild + FehltImExport > 0)
            Hinweise.Add($"Konten bestätigter Abgänge werden schon beim Import gesperrt; die Zugehörigkeiten enden am {D(_vortag)}.");
        if (AbgangNichtInDatenbank > 0)
            Hinweise.Add($"{AbgangNichtInDatenbank} Schüler mit Abgang laut SchILD (Status 8/9) sind nicht in der Datenbank " +
                         "und werden nicht angelegt.");
        if (Unbekannt.Count > 0)
            Hinweise.Add($"{Unbekannt.Count} Plan-Schlüssel gibt es im Grundplan nicht – sie werden übersprungen, vorhandene " +
                         "Zugehörigkeiten dazu bleiben unverändert.");

        int neueGruppen = neuAngelegt.Count(id => verwaltet[id].Typ == LerngruppeTyp.Gruppe);
        if (kandidaten.Count > 0 && neueGruppen > 0)
            Hinweise.Add($"{neueGruppen} Sek-I-Gruppen kommen neu aus SchILD; ihre Liste gilt ab Schuljahresbeginn (genauer: " +
                         "seit dem Eintritt in die jetzige Klasse), damit frühere Stunden nicht leer bleiben.");
        foreach (var id in verwaltet.Keys.Where(k => wechseltHeute.Contains(k) && !neuAngelegt.Contains(k))
                     .OrderBy(k => k, StringComparer.Ordinal))
        {
            var p = verwaltet[id];
            if (p.Typ == LerngruppeTyp.Klasse) continue;
            Uebernahmen.Add((TypText(p.Typ), p.Schluessel, vonHand.TryGetValue(id, out var h) ? h : 0,
                zielAnzahl.TryGetValue(id, out var a) ? a : 0, abSchuljahresbeginn.Contains(id)));
        }

        var rang = SchildImportService.Kategorie.Reihenfolge.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        var sortiert = Zeilen.OrderBy(z => rang.TryGetValue(z.Kategorie, out var r) ? r : 99)
            .ThenBy(z => _paarFolge.TryGetValue(z, out var pf) ? pf : 0)    // 5.2c-2: Paare in ihrer Reihenfolge (Gruppen)
            .ThenBy(z => z.Klasse ?? z.Stufe, Cmp).ThenBy(z => z.Name, Cmp).ToList();
        Zeilen.Clear();
        Zeilen.AddRange(sortiert);
    }

    // ----------------------------------------------------------------
    //  P4a (206): Halbjahr
    // ----------------------------------------------------------------

    /// <summary>
    /// Warnungen, wenn Abschnitt und „Änderungen gelten ab“ nicht zu „2. Halbjahr ab“ passen bzw. zum 2. Halbjahr kein
    /// eigener Grundplan gilt. true = Abschnitt 2 ab dem 2. Halbjahr, am Stichtag gilt aber noch ein Plan von davor.
    /// Nur Abschnitt 1 und 2 (Halbjahre) – Schulen mit vier Abschnitten bekommen keine Warnung.
    /// </summary>
    private bool HalbjahrPruefen(KalenderDb kal, GrundplanVersion version)
    {
        if (FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, _halbjahrAb) is not DateOnly h) return false;
        int a = _dto.Abschnitt;
        if (a == 2 && _giltAb < h)
            Warnungen.Add($"Abschnitt 2 gehört zum 2. Halbjahr, „Änderungen gelten ab“ ({D(_giltAb)}) liegt aber vor dem " +
                          $"2. Halbjahr ({D(h)}). Dann gälten Kurs- und Gruppenwechsel des 2. Halbjahres schon im 1. Halbjahr – " +
                          $"meist ist der {D(h)} richtig.");
        else if (a == 1 && _giltAb >= h)
            Warnungen.Add($"Abschnitt 1 gehört zum 1. Halbjahr, „Änderungen gelten ab“ ({D(_giltAb)}) liegt aber im 2. Halbjahr " +
                          $"(ab {D(h)}). Ist das ein älterer Export? Für das 2. Halbjahr in SchILD den Abschnitt 2 exportieren und " +
                          "im Feld „Abschnitt“ wählen.");
        if (a == 2 && _giltAb >= h && version.GueltigAb < h)
        {
            Warnungen.Add($"Zum 2. Halbjahr ({D(h)}) ist kein eigener Grundplan veröffentlicht – am {D(_giltAb)} gilt weiter " +
                          $"„{version.Bezeichnung}“ (ab {D(version.GueltigAb)}). Gibt es zum Halbjahr einen neuen Stundenplan: " +
                          $"zuerst im Wocheneditor „Grundplan veröffentlichen…“ mit „gilt ab“ {D(h)}, dann die Vorschau neu " +
                          "berechnen. Bleibt der Plan, ist das in Ordnung.");
            return true;
        }
        return false;
    }

    // ----------------------------------------------------------------
    //  5.2c-1: Paare – „dieselbe Person?“ (186–188) bzw. Neu-Zuordnung (190)
    // ----------------------------------------------------------------

    private readonly Dictionary<int, List<string>> _paarHinweiseDb = new();
    /// <summary>5.2c-2: Reihenfolge der Paar-Zeilen (Gruppen untereinander) – gilt auch nach dem Sortieren der Vorschau.</summary>
    private readonly Dictionary<SchildVorschauZeile, int> _paarFolge = new();

    private sealed record PaarKandidat(Eingang E, Schueler S, string Signal, bool Stark, bool GleicheGruppe, string GruppeDb);

    /// <summary>
    /// Bildet die Paare, wendet die Vorauswahl bzw. die gewählten Paare der Nutzlast an (gefundener Schüler =
    /// bisheriger Datensatz) und legt je Paar eine Zeile an. Danach gehört die Liste der angewendeten Paare zur
    /// Prüfsumme der Vorschau – beim Import schickt der Desktop genau diese Liste.
    /// </summary>
    private void PaareBilden(List<Eingang> eingaenge, List<Schueler> kandidaten, HashSet<int> vergeben,
        Func<int, List<Zugehoerigkeit>> zeilenVon, Dictionary<int, Lerngruppe> lgById)
    {
        PaareRechnen(eingaenge, kandidaten, vergeben, zeilenVon, lgById);
        var b = new StringBuilder("Q");
        foreach (var (id, ext) in _angewendetePaare.OrderBy(x => x.Id).ThenBy(x => x.ExtId, StringComparer.Ordinal))
            b.Append('|').Append(id).Append(':').Append(ext);
        VorschauPruefsumme = SchildImportService.Sha(VorschauPruefsumme + "|" + b);
    }

    private void PaareRechnen(List<Eingang> eingaenge, List<Schueler> kandidaten, HashSet<int> vergeben,
        Func<int, List<Zugehoerigkeit>> zeilenVon, Dictionary<int, Lerngruppe> lgById)
    {
        bool neuZu = _dto.Neuzuordnung;
        var offeneDb = kandidaten.Where(s => !vergeben.Contains(s.Id)
                                             && (neuZu || (s.Status != "abgang" && zeilenVon(s.Id).Any(OffenAb)))).ToList();
        var offeneEx = eingaenge.Where(e => e.Treffer == null && (neuZu || !e.Abgang)).ToList();
        if (offeneDb.Count == 0 || offeneEx.Count == 0) return;

        static string NameKey(string nach, string vor) => SchildImportService.NameNorm(nach) + "\n" + SchildImportService.NameNorm(vor);
        var gruppeDb = offeneDb.ToDictionary(s => s.Id, s => (KlasseAktuell(zeilenVon(s.Id), lgById) ?? s.Stufe).Trim());
        string GruppeEx(Eingang e) => ((e.Status != "extern" ? e.D.Klasse : null) ?? e.D.Stufe).Trim();

        Dictionary<string, List<Schueler>> Index(Func<Schueler, string?> key)
        {
            var d = new Dictionary<string, List<Schueler>>(StringComparer.Ordinal);
            foreach (var s in offeneDb)
            {
                var k = key(s);
                if (k == null) continue;
                if (!d.TryGetValue(k, out var l)) d[k] = l = new List<Schueler>();
                l.Add(s);
            }
            return d;
        }
        var nachNameGruppe = Index(s => NameKey(s.Nachname, s.Vorname) + "|" + gruppeDb[s.Id].ToUpperInvariant());
        var nachNameStufe = Index(s => NameKey(s.Nachname, s.Vorname) + "|" + s.Stufe.Trim().ToUpperInvariant());
        var nachV1 = Index(s => s.ExtIdVornameGeb);
        var nachN1 = Index(s => s.ExtIdNachnameGeb);
        List<Schueler> Treffer(Dictionary<string, List<Schueler>> d, string? k)
            => k != null && d.TryGetValue(k, out var l) ? l : new List<Schueler>();

        var paare = new List<PaarKandidat>();
        foreach (var e in offeneEx)
        {
            string gEx = GruppeEx(e);
            string nk = NameKey(e.D.Nachname, e.D.Vorname);
            var gesehen = new HashSet<int>();
            if (neuZu)
            {
                foreach (var s in Treffer(nachNameGruppe, nk + "|" + gEx.ToUpperInvariant()))
                    if (gesehen.Add(s.Id))
                        paare.Add(new PaarKandidat(e, s, "gleicher Name, gleiche " + (e.D.Klasse != null ? "Klasse" : "Stufe"),
                            true, true, gruppeDb[s.Id]));
                // Gleicher Name in einer anderen Klasse desselben Jahrgangs nur, wenn es in der eigenen Klasse keinen gibt
                // (sonst wären Namensgleiche in Parallelklassen nie eindeutig).
                if (gesehen.Count > 0) continue;
                foreach (var s in Treffer(nachNameStufe, nk + "|" + e.D.Stufe.Trim().ToUpperInvariant()))
                    if (gesehen.Add(s.Id))
                        paare.Add(new PaarKandidat(e, s, $"gleicher Name, andere Klasse ({gruppeDb[s.Id]} → {gEx})",
                            false, false, gruppeDb[s.Id]));
                continue;
            }

            var v1 = Treffer(nachV1, e.D.ExtIdVornameGeb).Select(s => s.Id).ToHashSet();
            var n1 = Treffer(nachN1, e.D.ExtIdNachnameGeb).Select(s => s.Id).ToHashSet();
            var nm = Treffer(nachNameGruppe, nk + "|" + gEx.ToUpperInvariant()).Select(s => s.Id).ToHashSet();
            foreach (var s in offeneDb.Where(x => v1.Contains(x.Id) || n1.Contains(x.Id) || nm.Contains(x.Id)))
            {
                // Verschiedene SchILD-IDs = zwei Personen (186).
                if (s.ExtIdSchild != null && e.D.ExtIdSchild != null
                    && !string.Equals(s.ExtIdSchild, e.D.ExtIdSchild, StringComparison.Ordinal)) continue;
                bool gleich = Cmp.Equals(gruppeDb[s.Id], gEx);
                string signal = v1.Contains(s.Id) ? "gleicher Vorname und Geburtsdatum"
                              : n1.Contains(s.Id) ? "gleicher Nachname und Geburtsdatum"
                              : "gleicher Name in " + (e.D.Klasse != null ? "derselben Klasse" : "demselben Jahrgang") +
                                ", anderes Geburtsdatum";
                if (!gleich) signal += $", Klasse {gruppeDb[s.Id]} → {gEx}";
                paare.Add(new PaarKandidat(e, s, signal, v1.Contains(s.Id) || n1.Contains(s.Id), gleich, gruppeDb[s.Id]));
            }
        }
        if (paare.Count == 0) return;

        var jeEx = paare.GroupBy(p => p.E).ToDictionary(g => g.Key, g => g.Count());
        var jeDb = paare.GroupBy(p => p.S.Id).ToDictionary(g => g.Key, g => g.Count());
        bool Eindeutig(PaarKandidat p) => jeEx[p.E] == 1 && jeDb[p.S.Id] == 1;
        bool Vorauswahl(PaarKandidat p) => p.Stark && p.GleicheGruppe && Eindeutig(p);

        // Wahl: ohne Liste die Vorauswahl der API, sonst genau die gewählten Paare (je Seite höchstens eines).
        List<PaarKandidat> gewuenscht;
        if (_dto.Paare == null) gewuenscht = paare.Where(Vorauswahl).ToList();
        else
        {
            var wahl = new HashSet<(int, string)>(_dto.Paare.Select(x => (x.Id, x.ExtId)));
            gewuenscht = paare.Where(p => wahl.Contains((p.S.Id, p.E.D.ExtId))).ToList();
            int passend = gewuenscht.Select(p => (p.S.Id, p.E.D.ExtId)).Distinct().Count();
            if (passend < wahl.Count)
                Hinweise.Add($"{wahl.Count - passend} gewählte Paare passen nicht (mehr) zu dieser Vorschau und werden nicht angewendet.");
        }
        var angewendet = new HashSet<PaarKandidat>();
        var belegtEx = new HashSet<Eingang>();
        var belegtDb = new HashSet<int>();
        int doppelt = 0;
        foreach (var p in gewuenscht.OrderBy(x => x.E.Nr).ThenBy(x => x.S.Id))
        {
            if (belegtEx.Contains(p.E) || belegtDb.Contains(p.S.Id)) { doppelt++; continue; }
            belegtEx.Add(p.E);
            belegtDb.Add(p.S.Id);
            angewendet.Add(p);
            p.E.Treffer = p.S;
            p.E.Paar = p.Signal;
            vergeben.Add(p.S.Id);
            _angewendetePaare.Add((p.S.Id, p.E.D.ExtId));
        }
        if (doppelt > 0)
            Hinweise.Add($"{doppelt} gewählte Paare überschneiden sich mit einem anderen (ein Schüler kann nur einen Partner " +
                         "haben) – angewendet wird jeweils das erste.");

        // Bei unsicheren Paaren helfen der Vergleich der Kurse/Gruppen und der Eintritt (bisher ↔ im Export) beim Entscheiden.
        string KurseVergleich(PaarKandidat p)
        {
            var (gemeinsam, n) = KursZahlen(p, zeilenVon, lgById);
            string kurse = n == 0 ? "" : $" · gemeinsame Kurse/Gruppen: {gemeinsam} von {n}";
            // Eintritt: bisher seit … (Beginn der ersten Zeile; leer = Schuljahresbeginn) ↔ im Export Neuaufnahme ab …
            var vonListe = zeilenVon(p.S.Id).Select(z => z.GueltigVon).ToList();
            DateOnly? seit = vonListe.Count == 0 || vonListe.Any(v => v == null) ? null : vonListe.Min();
            string bisherSeit = vonListe.Count == 0 ? "" : seit is DateOnly sd ? $"bisher ab {D(sd)}" : "bisher seit Schuljahresbeginn";
            string imExport = p.E.D.Status == 0 && p.E.D.Aufnahme is DateOnly auf ? $"im Export Neuaufnahme ab {D(auf)}" : "";
            string eintritt = string.Join(", ", new[] { bisherSeit, imExport }.Where(x => x.Length > 0));
            return kurse + (eintritt.Length > 0 ? " · " + eintritt : "");
        }

        // 5.2c-2: Gruppen voneinander abhängiger Paare (Namensgleiche) – ihre Zeilen stehen untereinander, je Schüler des
        // Exports zusammen. Bei der Neu-Zuordnung bekommt jede Gruppe einen Vorschlag, wenn er eindeutig ist.
        var gruppeVon = Komponenten(paare);
        var komponenten = paare.GroupBy(x => gruppeVon[x]).ToDictionary(g => g.Key, g => g.ToList());
        var vorschlagPaare = new HashSet<PaarKandidat>();
        var mitVorschlag = new HashSet<int>();
        if (neuZu)
            foreach (var (komp, liste) in komponenten)
            {
                var best = BesterVorschlag(liste, zeilenVon, lgById);
                if (best == null) continue;
                vorschlagPaare.UnionWith(best);
                mitVorschlag.Add(komp);
            }
        // Reihenfolge: Gruppe (an der Stelle ihres ersten Paars nach Klasse und Name), darin Export-Nr., dann Datensatz.
        var ersteStelle = new Dictionary<int, int>();
        int stelle = 0;
        foreach (var x in paare.OrderBy(y => y.E.D.Klasse ?? y.E.D.Stufe, Cmp).ThenBy(y => Name(y.E.D), Cmp).ThenBy(y => y.S.Id))
            ersteStelle.TryAdd(gruppeVon[x], stelle++);
        var reihe = paare.OrderBy(x => ersteStelle[gruppeVon[x]]).ThenBy(x => x.E.Nr).ThenBy(x => x.S.Id).ToList();

        string kat = neuZu ? SchildImportService.Kategorie.Zuordnung : SchildImportService.Kategorie.DieselbePerson;
        string ansicht = neuZu ? "Neu-Zuordnung" : "dieselbe Person?";
        var gruppenNr = new Dictionary<int, int>();
        foreach (var p in reihe)
        {
            bool an = angewendet.Contains(p);
            int komp = gruppeVon[p];
            int? gruppe = null;
            if (komponenten[komp].Count > 1)
            {
                if (!gruppenNr.TryGetValue(komp, out int gn)) gruppenNr[komp] = gn = gruppenNr.Count + 1;
                gruppe = gn;
            }
            string? hinweis = Eindeutig(p) ? null
                : neuZu && gruppe != null && !mitVorschlag.Contains(komp)
                    ? "mehrere mögliche Partner – kein eindeutiger Vorschlag (gleich gut): bitte in SchILD nachsehen"
                    : "mehrere mögliche Partner – höchstens einen wählen";
            var zeile = new SchildVorschauZeile
            {
                Kategorie = kat, Id = p.S.Id, PartnerExtId = p.E.D.ExtId, Name = Name(p.E.D),
                Klasse = p.E.Klasse?.Schluessel ?? p.E.D.Klasse, Stufe = p.E.D.Stufe,
                Aenderung = $"bisher „{p.S.Vorname} {p.S.Nachname}“ ({p.GruppeDb}) – {p.Signal}" +
                            (Eindeutig(p) && p.Stark ? "" : KurseVergleich(p)) +
                            // Nachbesserung 21.11.: bei mehreren Partnern unterscheidbar machen, welche Zeilen zusammengehören
                            (Eindeutig(p) ? "" : $" · Datensatz Nr. {p.S.Id} ↔ Export Nr. {p.E.Nr}"),
                Vorausgewaehlt = Vorauswahl(p), Gewaehlt = an,
                Hinweis = hinweis, Gruppe = gruppe, Vorschlag = vorschlagPaare.Contains(p)
            };
            _paarFolge[zeile] = _paarFolge.Count + 1;
            Zeilen.Add(zeile);
            if (an) continue;
            p.E.PaarHinweise.Add($"möglicherweise dieselbe Person wie „{p.S.Vorname} {p.S.Nachname}“ – siehe „{ansicht}“");
            if (!_paarHinweiseDb.TryGetValue(p.S.Id, out var l)) _paarHinweiseDb[p.S.Id] = l = new List<string>();
            l.Add($"möglicherweise dieselbe Person wie „{Name(p.E.D)}“ – siehe „{ansicht}“");
        }
        PaarGruppen = komponenten.Count(kv => kv.Value.Count > 1);
        PaarGruppenMitVorschlag = komponenten.Count(kv => kv.Value.Count > 1 && mitVorschlag.Contains(kv.Key));

        int mehrdeutig = paare.Count(p => !Eindeutig(p));
        if (neuZu)
        {
            ZuordnungPaare = paare.Count;
            NeuZugeordnet = angewendet.Count;
            Hinweise.Add($"Neu-Zuordnung über Name + Klasse: {angewendet.Count} von {offeneDb.Count} bisherigen Schülern zugeordnet" +
                         (mehrdeutig > 0 ? $"; {mehrdeutig} Paare sind nicht eindeutig (Namensgleiche bzw. andere Klasse – von Hand wählen)" : "") +
                         ". Zugeordnete Schüler bekommen die Prüfsummen des neuen Schlüssels; Konten und Klassenbuchdaten bleiben.");
            if (PaarGruppen > 0)
                Hinweise.Add($"{PaarGruppen} Gruppen Namensgleicher (Zeilen untereinander, Spalte „Hinweis“: Gruppe) – für " +
                             $"{PaarGruppenMitVorschlag} gibt es einen eindeutigen Vorschlag (passender Eintritt, dann die meisten " +
                             "gemeinsamen Kurse): Knopf „Vorschlag übernehmen“ setzt die Häkchen, danach „Vorschau berechnen“." +
                             (PaarGruppen > PaarGruppenMitVorschlag
                                 ? $" {PaarGruppen - PaarGruppenMitVorschlag} Gruppen sind gleich gut – dort bitte in SchILD nachsehen."
                                 : ""));
        }
        else
        {
            DieselbePerson = paare.Count;
            Zusammengefuehrt = angewendet.Count;
            Hinweise.Add($"{paare.Count} Paare „dieselbe Person?“ (Schüler im Export ohne Treffer ↔ Datensatz, der im Export fehlt) – " +
                         $"{angewendet.Count} werden zusammengeführt: der bisherige Datensatz bekommt Name und Prüfsummen aus dem " +
                         "Export, Konto und Klassenbuchdaten bleiben. Häkchen ändern und „Vorschau berechnen“ rechnet neu.");
        }
    }

    /// <summary>5.2c-2: Gemeinsame Kurse/Gruppen eines Paars (bisher offen ↔ im Export) und die größere der beiden Zahlen.</summary>
    private (int Gemeinsam, int Von) KursZahlen(PaarKandidat p, Func<int, List<Zugehoerigkeit>> zeilenVon,
        Dictionary<int, Lerngruppe> lgById)
    {
        var bisher = zeilenVon(p.S.Id).Where(z => OffenAb(z) && lgById[z.LerngruppeId].Typ != LerngruppeTyp.Klasse)
            .Select(z => lgById[z.LerngruppeId].Bezeichnung.Trim()).ToHashSet(Cmp);
        var export = p.E.Kurse.Select(k => k.Schluessel).ToHashSet(Cmp);
        return (bisher.Count(export.Contains), Math.Max(bisher.Count, export.Count));
    }

    /// <summary>
    /// 5.2c-2: Passt der Eintritt? +1 = ja (Neuaufnahme ab demselben Tag bzw. beide seit Schuljahresbeginn), −1 = nein
    /// (Neuaufnahme im Export, bisher seit Schuljahresbeginn), 0 = unklar.
    /// </summary>
    private int Eintritt(PaarKandidat p, Func<int, List<Zugehoerigkeit>> zeilenVon)
    {
        var von = zeilenVon(p.S.Id).Select(z => z.GueltigVon).ToList();
        if (von.Count == 0) return 0;
        DateOnly? seit = von.Any(v => v == null) ? null : von.Min();
        bool abBeginn = seit == null || seit <= _kalStart;
        if (p.E.D.Status == 0 && p.E.D.Aufnahme is DateOnly auf && auf > _kalStart)
            return seit == auf ? 1 : abBeginn ? -1 : 0;
        return abBeginn ? 1 : 0;
    }

    /// <summary>5.2c-2: Paare, die über einen gemeinsamen Schüler (Export oder Datenbank) zusammenhängen → Nummer der Gruppe.</summary>
    private static Dictionary<PaarKandidat, int> Komponenten(List<PaarKandidat> paare)
    {
        var nachEx = paare.GroupBy(x => x.E).ToDictionary(g => g.Key, g => g.ToList());
        var nachDb = paare.GroupBy(x => x.S.Id).ToDictionary(g => g.Key, g => g.ToList());
        var komp = new Dictionary<PaarKandidat, int>();
        int n = 0;
        foreach (var start in paare)
        {
            if (komp.ContainsKey(start)) continue;
            var stapel = new Stack<PaarKandidat>();
            stapel.Push(start);
            komp[start] = n;
            while (stapel.Count > 0)
            {
                var q = stapel.Pop();
                foreach (var r in nachEx[q.E].Concat(nachDb[q.S.Id]))
                    if (komp.TryAdd(r, n)) stapel.Push(r);
            }
            n++;
        }
        return komp;
    }

    /// <summary>
    /// 5.2c-2: Vorschlag für eine Gruppe der Neu-Zuordnung – die Auswahl (je Schüler höchstens ein Partner) mit den meisten
    /// Paaren, bei Gleichstand mit dem besten Wert: passender Eintritt vor gemeinsamen Kursen vor gleicher Klasse. Paare mit
    /// unpassendem Eintritt werden nie vorgeschlagen. null = kein eindeutiger Vorschlag (zwei Auswahlen gleich gut, zu groß).
    /// </summary>
    private List<PaarKandidat>? BesterVorschlag(List<PaarKandidat> gruppe, Func<int, List<Zugehoerigkeit>> zeilenVon,
        Dictionary<int, Lerngruppe> lgById)
    {
        var kanten = gruppe.Where(x => Eintritt(x, zeilenVon) >= 0).ToList();
        if (kanten.Count == 0 || kanten.Count > 16) return null;
        var wert = kanten.ToDictionary(x => x,
            x => Eintritt(x, zeilenVon) * 100 + KursZahlen(x, zeilenVon, lgById).Gemeinsam * 2 + (x.GleicheGruppe ? 1 : 0));
        (int Anzahl, int Wert) best = (0, int.MinValue);
        int gleichGut = 0;
        List<PaarKandidat>? bestListe = null;
        var wahl = new List<PaarKandidat>();
        var exBelegt = new HashSet<Eingang>();
        var dbBelegt = new HashSet<int>();

        void Suche(int i, int summe)
        {
            if (i == kanten.Count)
            {
                if (wahl.Count == 0) return;
                if (wahl.Count > best.Anzahl || (wahl.Count == best.Anzahl && summe > best.Wert))
                {
                    best = (wahl.Count, summe);
                    bestListe = wahl.ToList();
                    gleichGut = 1;
                }
                else if (wahl.Count == best.Anzahl && summe == best.Wert) gleichGut++;
                return;
            }
            Suche(i + 1, summe);                                   // Paar i nicht nehmen
            var k = kanten[i];
            if (exBelegt.Contains(k.E) || dbBelegt.Contains(k.S.Id)) return;
            exBelegt.Add(k.E);
            dbBelegt.Add(k.S.Id);
            wahl.Add(k);
            Suche(i + 1, summe + wert[k]);                         // Paar i nehmen
            wahl.RemoveAt(wahl.Count - 1);
            exBelegt.Remove(k.E);
            dbBelegt.Remove(k.S.Id);
        }

        Suche(0, 0);
        return gleichGut == 1 ? bestListe : null;
    }

    // ----------------------------------------------------------------
    //  5.2c-2: Klassenleitung (191) und Namen der Lehrkräfte (193)
    // ----------------------------------------------------------------

    /// <summary>
    /// Klassenleitung laut Klassen.dat an den Klassen-Lerngruppen vormerken. Klassen ohne Angabe behalten ihren Wert.
    /// Änderungen erscheinen als Hinweis (nur Kürzel). Die Konten ändern sich nicht – Vorschläge macht die Kontenverwaltung.
    /// </summary>
    private void KlassenleitungVormerken(SchildPlanIndex plan, Dictionary<string, Lerngruppe> ziel)
    {
        var liste = _dto.Klassenleitungen;
        if (liste == null) return;
        var aenderungen = new List<string>();
        int ohneKlasse = 0;
        foreach (var k in liste.OrderBy(x => x.Klasse, Cmp))
        {
            var p = plan.Klasse(k.Klasse);
            if (p == null || !ziel.TryGetValue(p.Id, out var lg)) { ohneKlasse++; continue; }
            string? alt = SchildImportService.KuerzelText(lg.Klassenleitung);
            string neu = k.Kuerzel;
            if (Cmp.Equals(alt ?? "", neu)) continue;
            if (alt == null) KlassenleitungNeu++;
            else aenderungen.Add($"Klassenleitung {p.Schluessel}: {alt} → {neu}");
            var klasse = lg;
            _schreiben.Add(_ => klasse.Klassenleitung = neu);
        }
        KlassenleitungGeaendert = aenderungen.Count;
        if (KlassenleitungNeu > 0)
            Hinweise.Add($"Klassenleitung laut SchILD (Klassen.dat) für {KlassenleitungNeu} Klassen erstmals eingetragen.");
        foreach (var a in aenderungen.Take(12)) Hinweise.Add(a);
        if (aenderungen.Count > 12) Hinweise.Add($"… und {aenderungen.Count - 12} weitere Klassenleitungen geändert.");
        if (KlassenleitungNeu + KlassenleitungGeaendert > 0)
            Hinweise.Add("Die Konten ändern sich dadurch nicht. Vorschläge für Rolle und Zuständigkeit: App → Kontomenü → " +
                         "„Konten verwalten“ → Reiter „SchILD“.");
        if (ohneKlasse > 0)
            Hinweise.Add($"{ohneKlasse} Klassenleitungen gehören zu keiner Klasse des Grundplans und werden übersprungen.");
    }

    /// <summary>
    /// Namen der Lehrkräfte vormerken: nur Kürzel des Grundplans (Datensparsamkeit); die Liste dieser Art wird ganz ersetzt –
    /// wer fehlt, wird gelöscht. Ohne Lehrkraefte.dat (null bzw. leer) bleibt der Bestand. Hinweise nur mit Kürzeln.
    /// </summary>
    private void LehrkraefteVormerken(List<GrundplanSlot> slots, List<Lehrkraft> bisher)
    {
        var liste = _dto.Lehrkraefte;
        if (liste == null) return;
        if (liste.Count == 0)
        {
            Hinweise.Add("Lehrkraefte.dat enthält keine Lehrkraft des Grundplans – die gespeicherten Namen bleiben.");
            return;
        }
        var imPlan = new HashSet<string>(slots.Where(s => !string.Equals(s.Art, "Sperre", StringComparison.OrdinalIgnoreCase))
            .Select(s => (s.Lehrer ?? "").Trim()).Where(x => x.Length > 0), Cmp);
        var neuListe = liste.Where(x => imPlan.Contains(x.Kuerzel)).ToList();
        int ausserhalb = liste.Count - neuListe.Count;
        var jeKuerzel = bisher.GroupBy(x => x.Kuerzel.Trim(), Cmp)
            .ToDictionary(g => g.Key, g => g.OrderBy(y => y.Id).ToList(), Cmp);
        var bleiben = new HashSet<int>();
        var geaendert = new List<string>();
        foreach (var neu in neuListe.OrderBy(x => x.Kuerzel, Cmp))
        {
            if (jeKuerzel.TryGetValue(neu.Kuerzel, out var vorhanden))
            {
                var t = vorhanden[0];
                bleiben.Add(t.Id);
                if (t.Nachname == neu.Nachname && t.Vorname == neu.Vorname) continue;
                geaendert.Add(t.Kuerzel.Trim());
                string nach = neu.Nachname, vor = neu.Vorname;
                _schreiben.Add(_ =>
                {
                    t.Nachname = nach;
                    t.Vorname = vor;
                    t.ImportAm = _jetzt;
                });
            }
            else
            {
                LehrkraefteNeu++;
                var n = new Lehrkraft
                {
                    Kuerzel = neu.Kuerzel, Nachname = neu.Nachname, Vorname = neu.Vorname, IstPseudo = _dto.Pseudo,
                    ImportAm = _jetzt
                };
                _schreiben.Add(d => d.Lehrkraefte.Add(n));
            }
        }
        var weg = bisher.Where(x => !bleiben.Contains(x.Id)).ToList();
        if (weg.Count > 0) _schreiben.Add(d => d.Lehrkraefte.RemoveRange(weg));
        LehrkraefteGesamt = neuListe.Count;
        LehrkraefteGeaendert = geaendert.Count;
        LehrkraefteEntfernt = weg.Count;
        Hinweise.Add($"Namen der Lehrkräfte (Lehrkraefte.dat, nur Lehrkräfte des Grundplans): {neuListe.Count} – neu " +
                     $"{LehrkraefteNeu}, geändert {geaendert.Count}" +
                     (geaendert.Count > 0 ? $" ({string.Join(", ", geaendert.Take(12))}{(geaendert.Count > 12 ? " …" : "")})" : "") +
                     $", entfernt {weg.Count}. Sichtbar nur für die Verwaltung (Konten) und die Lehrkraft selbst.");
        if (ausserhalb > 0)
            Hinweise.Add($"{ausserhalb} Lehrkräfte aus Lehrkraefte.dat unterrichten laut Grundplan nicht – ihre Namen werden " +
                         "nicht übernommen.");
    }

    // ----------------------------------------------------------------
    //  Schreiben (nur Import)
    // ----------------------------------------------------------------

    /// <summary>
    /// Führt die vorgemerkten Änderungen und die bestätigten Abgänge aus und legt das Importprotokoll an.
    /// Gespeichert wird vom Aufrufer (eine Transaktion).
    /// </summary>
    public SchildImportProtokoll Schreiben(AppDbContext db, IReadOnlyCollection<int> abgaenge, string akteur)
    {
        foreach (var a in _schreiben) a(db);
        int bestaetigt = 0, lautSchild = 0;
        foreach (var id in abgaenge)
        {
            if (!_abgaenge.TryGetValue(id, out var a)) continue;
            a(db);
            bestaetigt++;
            if (_abgangLautSchildIds.Contains(id)) lautSchild++;
        }
        var p = new SchildImportProtokoll
        {
            Zeit = _jetzt,
            Akteur = akteur,
            Schuljahr = _sj,
            Abschnitt = _dto.Abschnitt,
            GiltAb = _giltAb,
            IstPseudo = _dto.Pseudo,
            Neu = Neu,
            Geaendert = _geaenderteIds.Count,
            Klassenwechsel = Klassenwechsel,
            Kurswechsel = Kurswechsel,
            Abgaenge = bestaetigt,
            Unveraendert = Unveraendert,
            Uebersprungen = AbgangNichtInDatenbank + (AbgangLautSchild - lautSchild),
            StandPruefsumme = Stand,
            SchluesselKennung = _dto.Kennung,
            NeuZuordnung = _dto.Neuzuordnung,
            KennungVorher = _dto.Neuzuordnung ? _letzteKennung : null,
            Zusammengefuehrt = Zusammengefuehrt,
            NeuZugeordnet = NeuZugeordnet,
            // P3 (Kleinigkeit a): Klassenleitung und Namen der Lehrkräfte – dieselben Zahlen wie in Bericht und Log
            KlassenleitungKlassen = _dto.Klassenleitungen?.Count,
            KlassenleitungNeu = KlassenleitungNeu,
            KlassenleitungGeaendert = KlassenleitungGeaendert,
            Lehrkraefte = _dto.Lehrkraefte is { Count: > 0 } ? LehrkraefteGesamt : null,
            LehrkraefteNeu = LehrkraefteNeu,
            LehrkraefteGeaendert = LehrkraefteGeaendert,
            LehrkraefteEntfernt = LehrkraefteEntfernt
        };
        db.SchildImportProtokolle.Add(p);
        return p;
    }

    // ----------------------------------------------------------------
    //  Antworten
    // ----------------------------------------------------------------

    public object Zahlen() => new
    {
        imExport = ImExport,
        neu = Neu,
        nameGeaendert = NameGeaendert,
        klassenwechsel = Klassenwechsel,
        kurswechsel = Kurswechsel,
        statusGeaendert = StatusGeaendert,
        abgangLautSchild = AbgangLautSchild,
        fehltImExport = FehltImExport,
        unveraendert = Unveraendert,
        abgangNichtInDatenbank = AbgangNichtInDatenbank,
        unbekannteSchluessel = Unbekannt.Count,
        neueLerngruppen = NeueLerngruppen.Count,
        gruppenAusSchild = Uebernahmen.Count,
        dieselbePerson = DieselbePerson,
        zusammengefuehrt = Zusammengefuehrt,
        zuordnungPaare = ZuordnungPaare,
        neuZugeordnet = NeuZugeordnet,
        // 5.2c-2
        klassenleitungNeu = KlassenleitungNeu,
        klassenleitungGeaendert = KlassenleitungGeaendert,
        lehrkraefte = LehrkraefteGesamt,
        lehrkraefteNeu = LehrkraefteNeu,
        lehrkraefteGeaendert = LehrkraefteGeaendert,
        lehrkraefteEntfernt = LehrkraefteEntfernt,
        paarGruppen = PaarGruppen,
        paarGruppenMitVorschlag = PaarGruppenMitVorschlag
    };

    /// <summary>Vorschau für den Desktop (Probelauf, Sperre oder veraltete Vorschau).</summary>
    public object Antwort(bool probelauf) => new
    {
        probelauf,
        schuljahr = _sj,
        abschnitt = _dto.Abschnitt,
        giltAb = _giltAb,
        pseudo = _dto.Pseudo,
        neuzuordnung = _dto.Neuzuordnung,
        kennung = _dto.Kennung,
        kennungLetzterImport = _letzteKennung,
        grundplan = GrundplanBezeichnung,
        grundplanAb = GrundplanAb,
        gesperrt = Gesperrt,
        sperren = Sperren,
        seedEntfernenMoeglich = SeedEntfernenMoeglich,
        hinweise = Hinweise,
        warnungen = Warnungen,   // P4a (206)
        zahlen = Zahlen(),
        schueler = Zeilen,
        unbekannteSchluessel = Unbekannt.Select(u => new { schluessel = u.Schluessel, art = u.Art, schueler = u.Schueler }),
        neueLerngruppen = NeueLerngruppen.Select(n => new { typ = n.Typ, schluessel = n.Schluessel }),
        gruppenAusSchild = Uebernahmen.Select(u => new
        {
            typ = u.Typ, schluessel = u.Schluessel, vonHand = u.VonHand, ausSchild = u.AusSchild,
            abSchuljahresbeginn = u.AbSchuljahresbeginn
        }),
        stand = Stand,
        vorschau = VorschauPruefsumme,
        bericht = Bericht(null)
    };

    public object ImportAntwort(SchildImportProtokoll p) => new
    {
        probelauf = false,
        importiert = true,
        protokollId = p.Id,
        zeit = p.Zeit,
        schuljahr = _sj,
        abschnitt = _dto.Abschnitt,
        giltAb = _giltAb,
        pseudo = _dto.Pseudo,
        zahlen = Zahlen(),
        abgaengeBestaetigt = p.Abgaenge,
        uebersprungen = p.Uebersprungen,
        neuzuordnung = p.NeuZuordnung,
        kennung = p.SchluesselKennung,
        hinweise = Hinweise,
        warnungen = Warnungen,   // P4a (206)
        bericht = Bericht(p)
    };

    /// <summary>Bericht ohne Namen (Zahlen, Gruppenbezeichnungen) – für „Bericht kopieren“.</summary>
    public string Bericht(SchildImportProtokoll? p)
    {
        var b = new StringBuilder();
        b.AppendLine(p == null ? "SchILD-Import – Vorschau (Probelauf)" : $"SchILD-Import – ausgeführt (Protokoll Nr. {p.Id})");
        b.AppendLine($"Schuljahr {_sj} · Abschnitt {_dto.Abschnitt} · Änderungen gelten ab {D(_giltAb)} · " +
                     (_dto.Pseudo ? "Pseudodaten" : "echte Daten"));
        if (GrundplanBezeichnung != null && GrundplanAb is DateOnly ga)
            b.AppendLine($"Grundplan „{GrundplanBezeichnung}“ (gültig ab {D(ga)})");
        if (_dto.Kennung != null)
            b.AppendLine($"Schlüssel: Kennung {_dto.Kennung}" + (_letzteKennung != null && !_dto.Neuzuordnung
                ? (string.Equals(_letzteKennung, _dto.Kennung, StringComparison.OrdinalIgnoreCase) ? " (wie beim letzten Import)" : $" (letzter Import: {_letzteKennung})")
                : ""));
        if (_dto.Neuzuordnung)
            b.AppendLine("NEU-ZUORDNUNG über Name + Klasse (Schlüssel verloren)" +
                         (_letzteKennung != null ? $" – bisher Kennung {_letzteKennung}, neu {_dto.Kennung}" : ""));
        foreach (var s in Sperren) b.AppendLine("GESPERRT: " + s.Text);
        foreach (var w in Warnungen) b.AppendLine("WARNUNG: " + w);   // P4a (206)
        b.AppendLine($"Schüler im Export: {ImExport}");
        if (_dto.Neuzuordnung)
            b.AppendLine($"  neu zugeordnet (Name + Klasse): {NeuZugeordnet} (mögliche Paare: {ZuordnungPaare})");
        else if (DieselbePerson > 0)
            b.AppendLine($"  dieselbe Person? {DieselbePerson} Paare – zusammengeführt: {Zusammengefuehrt}");
        b.AppendLine($"  neu: {Neu}");
        b.AppendLine($"  Name bzw. Geburtsdatum geändert: {NameGeaendert}");
        b.AppendLine($"  Klassenwechsel: {Klassenwechsel}");
        b.AppendLine($"  Kurs-/Gruppenwechsel: {Kurswechsel}");
        b.AppendLine($"  Status bzw. Jahrgang geändert: {StatusGeaendert}");
        b.AppendLine($"  Abgang laut SchILD: {AbgangLautSchild}" + (p == null ? " (vorausgewählt)" : ""));
        b.AppendLine($"  fehlt im Export: {FehltImExport}" + (p == null ? " (nicht vorausgewählt)" : ""));
        b.AppendLine($"  unverändert: {Unveraendert}");
        if (AbgangNichtInDatenbank > 0) b.AppendLine($"  Abgang laut SchILD, nicht in der Datenbank: {AbgangNichtInDatenbank}");
        if (p != null) b.AppendLine($"Abgänge bestätigt: {p.Abgaenge} · übersprungen: {p.Uebersprungen}");
        // 5.2c-2: Gruppen der Neu-Zuordnung, Klassenleitungen, Namen der Lehrkräfte (nur Zahlen)
        if (_dto.Neuzuordnung && PaarGruppen > 0)
            b.AppendLine($"Gruppen Namensgleicher: {PaarGruppen} (mit eindeutigem Vorschlag: {PaarGruppenMitVorschlag})");
        if (_dto.Klassenleitungen != null)
            b.AppendLine($"Klassenleitung (Klassen.dat): {_dto.Klassenleitungen.Count} Klassen – erstmals eingetragen " +
                         $"{KlassenleitungNeu}, geändert {KlassenleitungGeaendert}");
        if (_dto.Lehrkraefte is { Count: > 0 })
            b.AppendLine($"Namen der Lehrkräfte (Lehrkraefte.dat): {LehrkraefteGesamt} – neu {LehrkraefteNeu}, geändert " +
                         $"{LehrkraefteGeaendert}, entfernt {LehrkraefteEntfernt}");
        if (NeueLerngruppen.Count > 0)
            b.AppendLine("Neue Lerngruppen: " + string.Join(", ", NeueLerngruppen.GroupBy(n => n.Typ)
                .Select(g => $"{g.Count()} {g.Key}")));
        if (Uebernahmen.Count > 0)
        {
            b.AppendLine($"Kurse/Gruppen ab {D(_giltAb)} aus SchILD ({Uebernahmen.Count}):");
            foreach (var u in Uebernahmen.Take(60))
                b.AppendLine($"  {u.Schluessel} ({u.Typ}): aus SchILD {u.AusSchild}" +
                             (u.VonHand > 0 ? $", bisher von Hand {u.VonHand} (enden am {D(_vortag)})" : "") +
                             (u.AbSchuljahresbeginn ? ", Liste ab Schuljahresbeginn" : ""));
            if (Uebernahmen.Count > 60) b.AppendLine($"  … und {Uebernahmen.Count - 60} weitere");
        }
        if (Unbekannt.Count > 0)
        {
            b.AppendLine($"Unbekannte Plan-Schlüssel (übersprungen, {Unbekannt.Count}):");
            foreach (var u in Unbekannt.Take(40)) b.AppendLine($"  {u.Schluessel} ({u.Art}, {u.Schueler} Schüler)");
            if (Unbekannt.Count > 40) b.AppendLine($"  … und {Unbekannt.Count - 40} weitere");
        }
        if (Hinweise.Count > 0)
        {
            b.AppendLine("Hinweise:");
            foreach (var h in Hinweise) b.AppendLine("  - " + h);
        }
        if (p == null && VorschauPruefsumme.Length >= 12) b.AppendLine($"Prüfsumme der Vorschau: {VorschauPruefsumme[..12]}…");
        return b.ToString().TrimEnd();
    }

    // ----------------------------------------------------------------
    //  Hilfen
    // ----------------------------------------------------------------

    private void Sperre(string art, string text) => Sperren.Add(new SchildSperre(art, text));

    private bool OffenAb(Zugehoerigkeit z) => z.GueltigBis == null || z.GueltigBis >= _giltAb;

    /// <summary>Beginn der neuen Zeilen: „gilt ab“, bei Neuaufnahme (Status 0) das spätere Aufnahmedatum.</summary>
    private DateOnly Start(SchildSchuelerDto d)
        => d.Status == 0 && d.Aufnahme is DateOnly auf && auf > _giltAb ? auf : _giltAb;

    /// <summary>Alte Zeile endet am Vortag; beginnt sie erst an „gilt ab“ oder später, wird sie ersetzt (169).</summary>
    private void Ende(Zugehoerigkeit z) => _schreiben.Add(d => Beenden(d, z));

    private void Beenden(AppDbContext d, Zugehoerigkeit z)
    {
        if (!_beendet.Add(z)) return;
        if ((z.GueltigVon ?? _kalStart) >= _giltAb) d.Zugehoerigkeiten.Remove(z);
        else if (z.GueltigBis == null || z.GueltigBis > _vortag) z.GueltigBis = _vortag;
    }

    /// <summary>Eine bleibende Zeile, die erst später beginnt, beginnt schon am neuen Start.</summary>
    private void Vorziehen(Zugehoerigkeit z, DateOnly? von)
    {
        bool frueher = von == null ? z.GueltigVon != null : z.GueltigVon is DateOnly v && v > von.Value;
        if (frueher) _schreiben.Add(_ => z.GueltigVon = von);
    }

    private void NeueZeile(Schueler s, Lerngruppe lg, DateOnly? von)
        => _schreiben.Add(d => d.Zugehoerigkeiten.Add(new Zugehoerigkeit
        {
            Schueler = s, Lerngruppe = lg, Schuljahr = _sj, GueltigVon = von, Quelle = Gueltigkeit.QuelleSchild
        }));

    /// <summary>Bestätigter Abgang (80, E): alle offenen Zeilen enden am Vortag, Status „abgang“, Konto gesperrt.</summary>
    private void AbgangVormerken(Schueler s, List<Zugehoerigkeit> zeilen, List<Benutzer> konten, SchildSchuelerDto? dtoS)
    {
        var offen = zeilen.Where(OffenAb).ToList();
        var eigeneKonten = konten.Where(k => k.SchuelerId == s.Id).ToList();
        _abgaenge[s.Id] = d =>
        {
            foreach (var z in offen) Beenden(d, z);
            if (dtoS != null) Identitaet(s, dtoS);
            s.Status = "abgang";
            s.AbgangAm = _vortag;
            if (dtoS != null) s.ImportAm = _jetzt;
            foreach (var k in eigeneKonten.Where(konto => !konto.Gesperrt))
            {
                k.Gesperrt = true;
                k.SicherheitsStempel = KontenService.NeuerStempel();
                KontenService.Protokoll(d, SchildImportService.AkteurKonto, KontenService.Aktion.Gesperrt, k,
                    "Abgang laut SchILD-Import");
            }
        };
    }

    /// <param name="ersetzen">5.2c-1 (Neu-Zuordnung): alle Prüfsummen ersetzen, auch durch „leer“ – die alten stammen
    /// vom verlorenen Schlüssel und würden nie wieder passen.</param>
    private static void Identitaet(Schueler s, SchildSchuelerDto d, bool ersetzen = false)
    {
        s.Nachname = d.Nachname;
        s.Vorname = d.Vorname;
        s.Stufe = d.Stufe;
        s.ExtId = d.ExtId;
        if (ersetzen || d.ExtIdSchild != null) s.ExtIdSchild = d.ExtIdSchild;
        if (ersetzen || d.ExtIdVornameGeb != null) s.ExtIdVornameGeb = d.ExtIdVornameGeb;
        if (ersetzen || d.ExtIdNachnameGeb != null) s.ExtIdNachnameGeb = d.ExtIdNachnameGeb;
    }

    private Lerngruppe NeueLerngruppe(SchildPlanEintrag p) => new()
    {
        Typ = p.Typ,
        Bezeichnung = p.Schluessel,
        Stufe = p.Stufe,
        Fach = p.Fach,
        Kursart = p.Kursart,
        LehrerKuerzel = p.Lehrer,
        Schuljahr = _sj,
        IstPseudo = _dto.Pseudo,
        BearbeitetVon = p.Typ == LerngruppeTyp.Gruppe ? SchildImportService.AkteurKonto : null,
        BearbeitetAm = p.Typ == LerngruppeTyp.Gruppe ? _jetzt : null,
        Quelle = Gueltigkeit.QuelleSchild,
        SchildAb = _giltAb
    };

    private string? KlasseAktuell(List<Zugehoerigkeit> zeilen, Dictionary<int, Lerngruppe> lgById)
    {
        var kl = zeilen.Where(x => lgById[x.LerngruppeId].Typ == LerngruppeTyp.Klasse).ToList();
        var zeile = kl.FirstOrDefault(x => x.GiltAm(_giltAb))
                    ?? kl.OrderByDescending(x => x.GueltigBis ?? DateOnly.MaxValue).ThenByDescending(x => x.GueltigVon ?? DateOnly.MinValue)
                        .FirstOrDefault();
        return zeile == null ? null : lgById[zeile.LerngruppeId].Bezeichnung.Trim();
    }

    private static string Name(SchildSchuelerDto d) => $"{d.Vorname} {d.Nachname}";

    private static string TypText(LerngruppeTyp t) => t switch
    {
        LerngruppeTyp.Klasse => "Klasse",
        LerngruppeTyp.Kurs => "Kurs",
        _ => "Gruppe"
    };

    private static string StatusAnzeige(string status) => status switch
    {
        "beurlaubt" => "beurlaubt",
        "extern" => "extern",
        "abgang" => "abgegangen",
        _ => "aktiv"
    };

    private static string D(DateOnly d) => SchildImportService.Datum(d);

    /// <summary>Prüfsumme des Datenbankstands (78): Schüler, Lerngruppen (seit 5.2c-2 mit Klassenleitung) und
    /// Zugehörigkeiten des Schuljahrs, Grundplan, Importe, seit 5.2c-2 die Namen der Lehrkräfte dieser Art.</summary>
    private static string StandPruefsumme(GrundplanVersion v, List<Schueler> schueler, List<Lerngruppe> lerngruppen,
        List<Zugehoerigkeit> zug, List<SchildImportProtokoll> protokolle, List<Lehrkraft> lehrkraefte)
    {
        var b = new StringBuilder();
        b.Append("V|").Append(v.Id).Append('|').Append(v.Erstellt.UtcTicks).Append('\n');
        foreach (var s in schueler.OrderBy(x => x.Id))
            b.Append("S|").Append(s.Id).Append('|').Append(s.ExtId).Append('|').Append(s.ExtIdSchild).Append('|')
             .Append(s.Nachname).Append('|').Append(s.Vorname).Append('|').Append(s.Stufe).Append('|')
             .Append(s.Status).Append('|').Append(SchildImportService.Iso(s.AbgangAm)).Append('|')
             .Append(s.IstPseudo ? '1' : '0').Append('\n');
        foreach (var l in lerngruppen.OrderBy(x => x.Id))
            b.Append("L|").Append(l.Id).Append('|').Append((int)l.Typ).Append('|').Append(l.Bezeichnung).Append('|')
             .Append(l.Quelle).Append('|').Append(SchildImportService.Iso(l.SchildAb)).Append('|')
             .Append(l.Klassenleitung).Append('\n');
        foreach (var t in lehrkraefte.OrderBy(x => x.Id))
            b.Append("T|").Append(t.Id).Append('|').Append(t.Kuerzel).Append('|').Append(t.Nachname).Append('|')
             .Append(t.Vorname).Append('\n');
        foreach (var z in zug.OrderBy(x => x.Id))
            b.Append("Z|").Append(z.Id).Append('|').Append(z.SchuelerId).Append('|').Append(z.LerngruppeId).Append('|')
             .Append(SchildImportService.Iso(z.GueltigVon)).Append('|').Append(SchildImportService.Iso(z.GueltigBis)).Append('|')
             .Append(z.Quelle).Append('\n');
        b.Append("P|").Append(protokolle.Count).Append('|').Append(protokolle.Count == 0 ? 0 : protokolle.Max(p => p.Id));
        return SchildImportService.Sha(b.ToString());
    }

    /// <summary>Prüfsumme der Nutzlast (ohne Probelauf/Vorschau/Abgänge/Akteur/Paare) – gleiche Daten, gleiche Summe. Die
    /// angewendeten Paare (5.2c-1) kommen in <see cref="PaareBilden"/> dazu.</summary>
    private static string NutzlastPruefsumme(SchildImportDto dto)
    {
        var b = new StringBuilder();
        b.Append("K|").Append(dto.Schuljahr).Append('|').Append(dto.Abschnitt).Append('|')
         .Append(SchildImportService.Iso(dto.GiltAb)).Append('|').Append(dto.Pseudo ? '1' : '0').Append('|')
         .Append(dto.Plan).Append('|').Append(dto.Kennung).Append('|').Append(dto.Neuzuordnung ? '1' : '0').Append('\n');
        foreach (var k in (dto.PlanSchluessel ?? new List<string>()).Select(x => x.ToUpperInvariant()).Distinct()
                     .OrderBy(x => x, StringComparer.Ordinal))
            b.Append("H|").Append(k).Append('\n');
        foreach (var s in dto.Schueler!.OrderBy(x => x.ExtId, StringComparer.Ordinal)
                     .ThenBy(x => x.ExtIdSchild ?? "", StringComparer.Ordinal))
            b.Append("S|").Append(s.ExtId).Append('|').Append(s.ExtIdSchild).Append('|').Append(s.ExtIdVornameGeb).Append('|')
             .Append(s.ExtIdNachnameGeb).Append('|').Append(s.Nachname).Append('|').Append(s.Vorname).Append('|')
             .Append(s.Stufe).Append('|').Append(s.Klasse).Append('|').Append(s.Status).Append('|')
             .Append(SchildImportService.Iso(s.Aufnahme)).Append('|')
             .Append(string.Join(",", (s.Kurse ?? new List<string>()).Select(x => x.ToUpperInvariant()).Distinct()
                 .OrderBy(x => x, StringComparer.Ordinal)))
             .Append('\n');
        // 5.2c-2: Klassenleitungen und Namen der Lehrkräfte (null = keine Angabe, leer = leere Liste).
        if (dto.Klassenleitungen != null)
        {
            b.Append("KL\n");
            foreach (var k in dto.Klassenleitungen.OrderBy(x => x.Klasse, StringComparer.Ordinal))
                b.Append("C|").Append(k.Klasse).Append('|').Append(k.Kuerzel.ToUpperInvariant()).Append('\n');
        }
        if (dto.Lehrkraefte != null)
        {
            b.Append("LK\n");
            foreach (var l in dto.Lehrkraefte.OrderBy(x => x.Kuerzel.ToUpperInvariant(), StringComparer.Ordinal))
                b.Append("T|").Append(l.Kuerzel).Append('|').Append(l.Nachname).Append('|').Append(l.Vorname).Append('\n');
        }
        return SchildImportService.Sha(b.ToString());
    }
}
