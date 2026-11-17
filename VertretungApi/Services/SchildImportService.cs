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
// =====================================================================

public static class SchildImportService
{
    public const string AkteurKonto = "SchILD-Import";
    public const int MaxSchueler = 20000;
    public const int MaxSchluessel = 5000;

    private static readonly Regex PruefsummeMuster = new(@"^[A-Za-z0-9+/=_\-]{16,128}$", RegexOptions.Compiled);

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

        public static readonly string[] Reihenfolge =
            { AbgangLautSchild, FehltImExport, Neu, NameGeaendert, Klassenwechsel, Kurswechsel, StatusGeaendert };
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
    public static async Task<SchildImportLauf> BerechnenAsync(AppDbContext db, SchildImportDto dto, bool produktiv, DateOnly heute)
    {
        var lauf = new SchildImportLauf(dto, produktiv, heute);
        await lauf.RechnenAsync(db);
        return lauf;
    }

    internal static string? Leer(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static string Datum(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    internal static string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    internal static string Sha(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
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

    public SchildImportLauf(SchildImportDto dto, bool produktiv, DateOnly heute)
    {
        _dto = dto;
        _produktiv = produktiv;
        _heute = heute;
        _giltAb = dto.GiltAb;
        _vortag = dto.GiltAb.AddDays(-1);
        _sj = dto.Schuljahr;
    }

    public List<SchildSperre> Sperren { get; } = new();
    public List<string> Hinweise { get; } = new();
    public bool SeedEntfernenMoeglich { get; private set; }
    public bool Gesperrt => Sperren.Count > 0;
    public string? GrundplanBezeichnung { get; private set; }
    public DateOnly? GrundplanAb { get; private set; }
    public string Stand { get; private set; } = "";
    public string VorschauPruefsumme { get; private set; } = "";
    public List<SchildVorschauZeile> Zeilen { get; } = new();

    public int ImExport, Neu, NameGeaendert, Klassenwechsel, Kurswechsel, StatusGeaendert,
        AbgangLautSchild, FehltImExport, Unveraendert, AbgangNichtInDatenbank;

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

        Stand = StandPruefsumme(version, schueler, lerngruppen, zug, protokolle);
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
                                         "nicht eindeutig zuordnen (Zusammenführen kommt mit 5.2c).");

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

        // ---- 8. Gefundene Schüler: Änderungen ermitteln ----
        var getroffen = new HashSet<int>();
        var neueZeilen = new List<SchildVorschauZeile>();
        var neueOhneId = new List<(SchildVorschauZeile Zeile, string? Klasse, string Stufe)>();
        foreach (var e in eingaenge.Where(x => x.Treffer != null))
        {
            var s = e.Treffer!;
            getroffen.Add(s.Id);
            var zeilen = ZeilenVon(s.Id);
            string statusAlt = string.IsNullOrEmpty(s.Status) ? "aktiv" : s.Status!;

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
            if (texte.ContainsKey(SchildImportService.Kategorie.NameGeaendert) || texte.ContainsKey(SchildImportService.Kategorie.StatusGeaendert))
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
                Stufe = e.D.Stufe, Aenderung = teile.Count == 0 ? null : string.Join(", ", teile)
            };
            neueZeilen.Add(zeile);
            if (e.D.ExtIdSchild == null) neueOhneId.Add((zeile, e.Klasse?.Schluessel, e.D.Stufe));
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
            // Ohne SchILD-ID: geänderter Name erscheint als „neu“ + „fehlt“ (Zusammenführen kommt mit 5.2c).
            if (s.ExtIdSchild == null)
            {
                var paar = neueOhneId.Where(n => klasse != null ? Cmp.Equals(n.Klasse ?? "", klasse)
                                                                 : n.Klasse == null && Cmp.Equals(n.Stufe, s.Stufe)).ToList();
                if (paar.Count > 0)
                {
                    zeile.Hinweis = $"möglicherweise dieselbe Person wie „{string.Join("“, „", paar.Select(n => n.Zeile.Name))}“ " +
                                    "(neu) – Zusammenführen kommt mit 5.2c";
                    foreach (var n in paar)
                        n.Zeile.Hinweis = $"möglicherweise dieselbe Person wie „{zeile.Name}“ (fehlt im Export) – " +
                                          "Zusammenführen kommt mit 5.2c";
                }
            }
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
            .ThenBy(z => z.Klasse ?? z.Stufe, Cmp).ThenBy(z => z.Name, Cmp).ToList();
        Zeilen.Clear();
        Zeilen.AddRange(sortiert);
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
            StandPruefsumme = Stand
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
        gruppenAusSchild = Uebernahmen.Count
    };

    /// <summary>Vorschau für den Desktop (Probelauf, Sperre oder veraltete Vorschau).</summary>
    public object Antwort(bool probelauf) => new
    {
        probelauf,
        schuljahr = _sj,
        abschnitt = _dto.Abschnitt,
        giltAb = _giltAb,
        pseudo = _dto.Pseudo,
        grundplan = GrundplanBezeichnung,
        grundplanAb = GrundplanAb,
        gesperrt = Gesperrt,
        sperren = Sperren,
        seedEntfernenMoeglich = SeedEntfernenMoeglich,
        hinweise = Hinweise,
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
        hinweise = Hinweise,
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
        foreach (var s in Sperren) b.AppendLine("GESPERRT: " + s.Text);
        b.AppendLine($"Schüler im Export: {ImExport}");
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

    private static void Identitaet(Schueler s, SchildSchuelerDto d)
    {
        s.Nachname = d.Nachname;
        s.Vorname = d.Vorname;
        s.Stufe = d.Stufe;
        s.ExtId = d.ExtId;
        if (d.ExtIdSchild != null) s.ExtIdSchild = d.ExtIdSchild;
        if (d.ExtIdVornameGeb != null) s.ExtIdVornameGeb = d.ExtIdVornameGeb;
        if (d.ExtIdNachnameGeb != null) s.ExtIdNachnameGeb = d.ExtIdNachnameGeb;
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

    /// <summary>Prüfsumme des Datenbankstands (78): Schüler, Lerngruppen und Zugehörigkeiten des Schuljahrs, Grundplan, Importe.</summary>
    private static string StandPruefsumme(GrundplanVersion v, List<Schueler> schueler, List<Lerngruppe> lerngruppen,
        List<Zugehoerigkeit> zug, List<SchildImportProtokoll> protokolle)
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
             .Append(l.Quelle).Append('|').Append(SchildImportService.Iso(l.SchildAb)).Append('\n');
        foreach (var z in zug.OrderBy(x => x.Id))
            b.Append("Z|").Append(z.Id).Append('|').Append(z.SchuelerId).Append('|').Append(z.LerngruppeId).Append('|')
             .Append(SchildImportService.Iso(z.GueltigVon)).Append('|').Append(SchildImportService.Iso(z.GueltigBis)).Append('|')
             .Append(z.Quelle).Append('\n');
        b.Append("P|").Append(protokolle.Count).Append('|').Append(protokolle.Count == 0 ? 0 : protokolle.Max(p => p.Id));
        return SchildImportService.Sha(b.ToString());
    }

    /// <summary>Prüfsumme der Nutzlast (ohne Probelauf/Vorschau/Abgänge/Akteur) – gleiche Daten, gleiche Summe.</summary>
    private static string NutzlastPruefsumme(SchildImportDto dto)
    {
        var b = new StringBuilder();
        b.Append("K|").Append(dto.Schuljahr).Append('|').Append(dto.Abschnitt).Append('|')
         .Append(SchildImportService.Iso(dto.GiltAb)).Append('|').Append(dto.Pseudo ? '1' : '0').Append('|')
         .Append(dto.Plan).Append('\n');
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
        return SchildImportService.Sha(b.ToString());
    }
}
