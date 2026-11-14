using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Wochenplan (Phase 2, Schritt 2): setzt für beliebige Wochen den
//  effektiven Plan zusammen aus
//    1. der Grundplan-Version, die am Tag gilt (größtes GueltigAb <= Datum
//       im Schuljahr des Kalenders),
//    2. dem Schuljahreskalender (Ferien/Feiertage ganz frei; Einträge mit
//       Klassenliste machen nur die Stunden dieser Klassen frei),
//    3. den veröffentlichten Vertretungstagen (/api/plan/push) als Overlay.
//
//  Zuordnung Vertretung -> Grundplan-Stunde über (Stunde, GrundUNr, Lehrkraft);
//  ältere Pushes ohne UNr ersatzweise über (Stunde, Lehrkraft, Klassen).
//  Gefiltert wird zum Schluss nach Ziel: Lehrkraft (effektiv ODER ursprünglich),
//  Klasse (Klassen-Rohwert, z. B. "05A" oder "Q1"), Lerngruppe(n) (Schlüssel aus
//  dem Grundplan, z. B. "BIG1_EF_L68"; für Schüler alle ihre Lerngruppen).
//
//  Phase 3.1 (Klassenbuch):
//   - Jede Stunde kennt ihre Teilnehmer-„Quellen" (Klasse, Kurs, Gruppe laut
//     Schülergruppe der UV). Daraus entstehen im Klassenbuch die Teilnehmerlisten.
//   - Schülerplan: Ist eine Sek-I-Gruppe (Religion, WP, AG …) zusammengestellt,
//     sieht ein Schüler die Stunde nur noch, wenn er in der Gruppe ist; sonst
//     wie bisher alle Schüler der genannten Klassen.
//   - Jede Stunde meldet GrundUNr/GrundLehrer (Schlüssel der Lektion), ob es ein
//     Klassenbuch gibt und ob die Anwesenheit schon erfasst ist.
//   - TagAsync liefert die effektiven Stunden eines Tages für das Klassenbuch.
//
//  Phase 3.3 (Thema/Hausaufgabe): Jede Stunde trägt Thema und Hausaufgabe,
//  wenn die anfragende Person sie lesen darf (InhaltLeser):
//    Schüler: eigene Stunden (der Plan enthält nur diese); die Hausaufgabe nur,
//             wenn der Unterricht sie nicht ausgeblendet hat (Entscheidung 30).
//    Personal: Admin; haltende/ursprüngliche Lehrkraft; Klassenleitung bzw.
//             Stufenkoordination der Klasse/Stufe; Lehrkräfte, die die Klasse
//             bzw. Stufe laut Grundplan unterrichten (Entscheidung 23).
//    Ohne Anmeldung: nichts.
// =====================================================================

/// <summary>
/// Wer im Wochenplan Thema und Hausaufgabe der Stunden lesen darf (Phase 3.3).
/// <see cref="Zustaendig"/> nur bei Klassenleitung/Stufenkoordination.
/// </summary>
public sealed record InhaltLeser(bool Admin, string? Kuerzel, IReadOnlyList<string> Zustaendig, bool Schueler)
{
    public static InhaltLeser FuerSchueler() => new(false, null, Array.Empty<string>(), true);

    public static InhaltLeser FuerPersonal(KbNutzer u) => new(
        u.IstAdmin, u.Kuerzel,
        u.IstKlassenleitungOderKoordination ? u.Zustaendig : Array.Empty<string>(),
        false);

    internal bool Aktiv => Admin || Schueler || Kuerzel != null || Zustaendig.Count > 0;
}

/// <summary>
/// Herkunft der Teilnehmer einer Stunde (aus einem Grundplan-Slot):
/// Typ Klasse (Key "05A", ggf. mit Schülergruppe = Sek-I-Teilgruppe), Kurs (Key =
/// Kurs-Schlüssel) oder Sonstige (ohne Klassenangabe: keine Schüler, kein Klassenbuch).
/// </summary>
public sealed record PlanQuelle(string Typ, string Key, string Klasse, string? Schuelergruppe);

/// <summary>Effektive Stunde eines Tages (für das Klassenbuch).</summary>
public sealed record PlanStunde(
    int Stunde, int UNr, int GrundUNr, string Fach, string Lehrer, string GrundLehrer,
    string? OriginalLehrer, string? Raum, string Art, string Status, string? Info,
    IReadOnlyList<string> Klassen, IReadOnlyList<string> Gruppen, IReadOnlyList<PlanQuelle> Quellen);

/// <summary>Klassenbezogen freie Klasse/Stufe an einem Tag (z. B. Klassenfahrt).</summary>
public sealed record FreieKlasse(string Klasse, string Text);

/// <summary>Ein Unterrichtstag mit seinen effektiven Stunden (Phase 3.1).</summary>
public sealed record TagKontext(
    DateOnly Datum, string Schuljahr, int VersionId,
    IReadOnlyList<PlanStunde> Stunden, IReadOnlyList<FreieKlasse> FreieKlassen);

public enum WochenZielArt { Lehrer, Klasse, Gruppe, Schueler }

public static class WochenplanService
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] Kurz = { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" };
    private static readonly Regex KlassenMuster = new(@"^\d{2}[A-Za-z]{1,2}$", RegexOptions.Compiled);

    /// <summary>Montag der Woche, in der <paramref name="d"/> liegt.</summary>
    public static DateOnly Montag(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>ISO-Wochentag: 1 = Montag … 7 = Sonntag (wie GrundplanSlot.WTagIndex).</summary>
    public static int IsoTag(DateOnly d) => ((int)d.DayOfWeek + 6) % 7 + 1;

    /// <param name="werte">Lehrer: Kürzel · Klasse: Klassenname · Gruppe/Schüler: Lerngruppen-Schlüssel.</param>
    /// <param name="wochen">Anzahl Wochen ab der Woche von <paramref name="datum"/> (1–6).</param>
    /// <param name="fertigeGruppen">
    /// Nur Schülerplan (Phase 3.1): zusammengestellte Sek-I-Gruppen als "Schuljahr|SCHLÜSSEL"
    /// (siehe <see cref="FertigeGruppenAsync"/>).
    /// </param>
    /// <param name="leser">Phase 3.3: wer Thema/Hausaufgabe lesen darf; null = niemand.</param>
    public static async Task<WochenplanDto> BerechneAsync(
        AppDbContext db, WochenZielArt art, string anzeige, IEnumerable<string> werte,
        DateOnly datum, int wochen, ISet<string>? fertigeGruppen = null, InhaltLeser? leser = null)
    {
        wochen = Math.Clamp(wochen, 1, 6);
        var von = Montag(datum);
        var bis = von.AddDays(7 * wochen - 1);
        var zielWerte = new HashSet<string>(
            werte.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()), Cmp);

        // ---- Daten der Wochen laden (wenige Abfragen, der Rest im Speicher) ----
        var kalender = await db.Kalender.Include(k => k.FreieZeiten)
            .Where(k => k.Start <= bis && k.Ende >= von)
            .ToListAsync();
        var schuljahre = kalender.Select(k => k.Schuljahr).Distinct().ToList();

        var versionen = schuljahre.Count == 0
            ? new List<GrundplanVersion>()
            : await db.GrundplanVersionen
                .Where(v => schuljahre.Contains(v.Schuljahr) && v.GueltigAb <= bis)
                .ToListAsync();

        var tage = new List<(DateOnly Datum, int Iso, KalenderDb? Kal, GrundplanVersion? Ver)>();
        for (var d = von; d <= bis; d = d.AddDays(1))
        {
            var kal = kalender.FirstOrDefault(k => k.Start <= d && d <= k.Ende);
            GrundplanVersion? ver = kal == null ? null : versionen
                .Where(v => v.Schuljahr == kal.Schuljahr && v.GueltigAb <= d)
                .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
                .FirstOrDefault();
            tage.Add((d, IsoTag(d), kal, ver));
        }

        var verIds = tage.Where(t => t.Ver != null).Select(t => t.Ver!.Id).Distinct().ToList();
        var alleSlots = verIds.Count == 0
            ? new List<GrundplanSlot>()
            : await db.GrundplanSlots.Where(s => verIds.Contains(s.VersionId)).ToListAsync();
        var slotsJeVersion = alleSlots.GroupBy(s => s.VersionId).ToDictionary(g => g.Key, g => g.ToList());

        var vertretungsTage = await db.Tage.Include(t => t.Eintraege)
            .Where(t => t.Datum >= von && t.Datum <= bis)
            .ToListAsync();

        // Phase 3.1: Lektionen mit erfasster Anwesenheit (für die Markierung im Plan);
        // Phase 3.3: dazu Thema/Hausaufgabe (nur für berechtigte Leser).
        bool mitInhalt = leser != null && leser.Aktiv;
        var lektionen = await db.Lektionen.AsNoTracking()
            .Where(l => l.Datum >= von && l.Datum <= bis
                        && (l.ErfasstAm != null || (mitInhalt && (l.Thema != null || l.Hausaufgabe != null))))
            .Select(l => new
            {
                l.Datum, l.Stunde, l.GrundUNr, l.GrundLehrer, Erfasst = l.ErfasstAm != null,
                l.Thema, l.Hausaufgabe, l.HausaufgabeFaellig
            })
            .ToListAsync();
        var erfasst = new HashSet<string>(StringComparer.Ordinal);
        var inhalte = new Dictionary<string, StundenInhalt>(StringComparer.Ordinal);
        foreach (var l in lektionen)
        {
            string key = LektionSchluessel(l.Datum, l.Stunde, l.GrundUNr, l.GrundLehrer);
            if (l.Erfasst) erfasst.Add(key);
            if (mitInhalt && (!string.IsNullOrWhiteSpace(l.Thema) || !string.IsNullOrWhiteSpace(l.Hausaufgabe)))
                inhalte[key] = new StundenInhalt(
                    string.IsNullOrWhiteSpace(l.Thema) ? null : l.Thema,
                    string.IsNullOrWhiteSpace(l.Hausaufgabe) ? null : l.Hausaufgabe,
                    string.IsNullOrWhiteSpace(l.Hausaufgabe) ? null : l.HausaufgabeFaellig);
        }

        // Schüler: Unterrichte, deren Hausaufgaben die Lehrkraft ausgeblendet hat.
        var verborgen = new HashSet<string>(StringComparer.Ordinal);
        if (mitInhalt && leser!.Schueler && inhalte.Count > 0)
        {
            var aus = await db.UnterrichtEinstellungen.AsNoTracking()
                .Where(x => !x.HausaufgabenSichtbar)
                .Select(x => new { x.Schuljahr, x.UNr, x.Lehrer })
                .ToListAsync();
            foreach (var a in aus) verborgen.Add(UnterrichtSchluessel(a.Schuljahr, a.UNr, a.Lehrer));
        }

        // Personal: Klassen/Stufen, die die Lehrkraft laut Grundplan unterrichtet (je Version).
        var lehrerKlassen = new Dictionary<int, HashSet<string>>();
        string? leserKuerzel = leser?.Kuerzel;
        HashSet<string> KlassenDerLehrkraft(int verId, List<GrundplanSlot> slots)
        {
            if (lehrerKlassen.TryGetValue(verId, out var k)) return k;
            k = new HashSet<string>(Cmp);
            if (leserKuerzel != null)
                foreach (var slot in slots)
                    if (Gleich(slot.Lehrer, leserKuerzel)
                        && string.Equals(slot.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(slot.Klasse))
                        k.Add(slot.Klasse.Trim());
            lehrerKlassen[verId] = k;
            return k;
        }

        // ---- Tag für Tag zusammensetzen ----
        var ergebnis = new List<WochenTagDto>();
        foreach (var (d, iso, kal, ver) in tage)
        {
            string wtag = Kurz[iso - 1];

            if (kal == null)
            {
                if (iso <= 5) ergebnis.Add(LeererTag(d, wtag, "AusserhalbSchuljahr", null));
                continue;
            }
            if (!RasterTage(kal).Contains(iso))
            {
                // Wochenende nicht auflisten; ein fehlender Werktag (z. B. 4-Tage-Raster) als frei.
                if (iso <= 5) ergebnis.Add(LeererTag(d, wtag, "Frei", "kein Unterrichtstag"));
                continue;
            }

            var ganzFrei = kal.FreieZeiten
                .Where(f => f.Von <= d && d <= f.Bis && string.IsNullOrWhiteSpace(f.Klassen))
                .OrderBy(f => f.Von)
                .FirstOrDefault();
            if (ganzFrei != null)
            {
                ergebnis.Add(LeererTag(d, wtag, "Frei", FreiText(ganzFrei)));
                continue;
            }

            if (ver == null || !slotsJeVersion.TryGetValue(ver.Id, out var verSlots))
            {
                ergebnis.Add(LeererTag(d, wtag, "KeinGrundplan", null));
                continue;
            }

            var teilFrei = kal.FreieZeiten
                .Where(f => f.Von <= d && d <= f.Bis && !string.IsNullOrWhiteSpace(f.Klassen))
                .ToList();

            // Klassenbezogen frei (z. B. Klassenfahrt): Für die betroffene Klasse – bzw. einen
            // Schüler dieser Klasse – ist der ganze Tag frei, auch klassenübergreifende Stunden.
            // Lehrkräfte sehen dagegen weiterhin ihre Stunden (nur rein betroffene als „Frei").
            var freiFuerZiel = art == WochenZielArt.Lehrer ? null : teilFrei.FirstOrDefault(f =>
                Liste(f.Klassen).Any(k => zielWerte.Contains(k) &&
                                          (art == WochenZielArt.Klasse || KlassenMuster.IsMatch(k))));
            if (freiFuerZiel != null)
            {
                ergebnis.Add(LeererTag(d, wtag, "Frei", FreiText(freiFuerZiel)));
                continue;
            }

            // Veröffentlichter Vertretungstag: bevorzugt aus demselben Schuljahr.
            var vt = vertretungsTage.Where(t => t.Datum == d && t.Schuljahr == kal.Schuljahr)
                         .OrderByDescending(t => t.Id).FirstOrDefault()
                     ?? vertretungsTage.Where(t => t.Datum == d)
                         .OrderByDescending(t => t.Id).FirstOrDefault();

            string sj = kal.Schuljahr;
            bool GruppeFertig(string sg) =>
                fertigeGruppen != null && fertigeGruppen.Contains(GruppenSchluessel(sj, sg));

            // Phase 3.3: Thema/Hausaufgabe, soweit die anfragende Person sie lesen darf.
            int versionId = ver.Id;
            StundenInhalt? InhaltFuer(Std x)
            {
                if (!mitInhalt || !inhalte.TryGetValue(LektionSchluessel(d, x.Stunde, x.GrundUNr, x.GrundLehrer), out var i))
                    return null;
                if (leser!.Schueler)
                    return verborgen.Contains(UnterrichtSchluessel(sj, x.UNr, x.GrundLehrer)) ? i with { Hausaufgabe = null, Faellig = null } : i;
                bool darf = leser.Admin
                            || (leser.Kuerzel != null && (Gleich(leser.Kuerzel, x.Lehrer) || Gleich(leser.Kuerzel, x.GrundLehrer)
                                                          || Gleich(leser.Kuerzel, x.OriginalLehrer)))
                            || (leser.Zustaendig.Count > 0 && KlassenbuchService.ZustaendigFuer(leser.Zustaendig, x.Klassen))
                            || (leser.Kuerzel != null && x.Klassen.Overlaps(KlassenDerLehrkraft(versionId, verSlots)));
                return darf ? i : null;
            }

            var stunden = EffektiverTag(verSlots, iso, vt?.Eintraege, teilFrei)
                .Where(st => Passt(st, art, zielWerte, GruppeFertig))
                .OrderBy(st => st.Stunde).ThenBy(st => st.Fach, Cmp).ThenBy(st => st.Lehrer, Cmp)
                .Select(st => ZuDto(st, erfasst.Contains(LektionSchluessel(d, st.Stunde, st.GrundUNr, st.GrundLehrer)),
                    InhaltFuer(st)))
                .ToList();

            // Alle Stunden des Ziels wegen einer freien Zeit entfallen (z. B. Q2 an Abiturtagen):
            // als freier Tag ausweisen – außer für Lehrkräfte (die haben trotzdem Dienst).
            if (art != WochenZielArt.Lehrer && stunden.Count > 0 && stunden.All(st => st.Status == "Frei"))
            {
                ergebnis.Add(LeererTag(d, wtag, "Frei", stunden[0].Info));
                continue;
            }

            ergebnis.Add(new WochenTagDto(
                d, wtag, "Unterricht", null, ver.Id,
                $"{ver.Bezeichnung} (gültig ab {ver.GueltigAb:dd.MM.yyyy})",
                vt != null, stunden));
        }

        return new WochenplanDto(ZielName(art), anzeige, von, bis, ergebnis);
    }

    // =================================================================
    //  Klassenbuch (Phase 3.1): ein Tag, Schlüssel, zusammengestellte Gruppen
    // =================================================================

    /// <summary>
    /// Effektive Stunden eines Tages (Grundplan × Kalender × Vertretungen) für das Klassenbuch.
    /// null = kein Unterrichtstag (außerhalb des Schuljahres, kein Rastertag, ganz frei,
    /// kein Grundplan). Klassenbezogen freie Zeiten stehen in <see cref="TagKontext.FreieKlassen"/>.
    /// </summary>
    public static async Task<TagKontext?> TagAsync(AppDbContext db, DateOnly d)
        => (await TageAsync(db, d, d)).FirstOrDefault();

    /// <summary>
    /// Unterrichtstage von <paramref name="von"/> bis <paramref name="bis"/> mit ihren effektiven
    /// Stunden (Phase 3.2, z. B. für „nicht erfasste Stunden"). Tage ohne Unterricht (Wochenende,
    /// Ferien, ganz frei, kein Grundplan) fehlen in der Liste. Lädt Kalender, Grundplan-Versionen
    /// und Vertretungstage nur einmal für den ganzen Zeitraum.
    /// </summary>
    public static async Task<List<TagKontext>> TageAsync(AppDbContext db, DateOnly von, DateOnly bis)
    {
        var ergebnis = new List<TagKontext>();
        if (bis < von) return ergebnis;

        var kalender = await db.Kalender.AsNoTracking().Include(k => k.FreieZeiten)
            .Where(k => k.Start <= bis && k.Ende >= von)
            .ToListAsync();
        if (kalender.Count == 0) return ergebnis;

        var schuljahre = kalender.Select(k => k.Schuljahr).Distinct().ToList();
        var versionen = await db.GrundplanVersionen.AsNoTracking()
            .Where(v => schuljahre.Contains(v.Schuljahr) && v.GueltigAb <= bis)
            .ToListAsync();
        var vertretungsTage = await db.Tage.AsNoTracking().Include(t => t.Eintraege)
            .Where(t => t.Datum >= von && t.Datum <= bis)
            .ToListAsync();
        var slotsJeVersion = new Dictionary<int, List<GrundplanSlot>>();

        for (var d = von; d <= bis; d = d.AddDays(1))
        {
            var kal = kalender.Where(k => k.Start <= d && d <= k.Ende).OrderByDescending(k => k.Id).FirstOrDefault();
            if (kal == null) continue;

            int iso = IsoTag(d);
            if (!RasterTage(kal).Contains(iso)) continue;
            if (kal.FreieZeiten.Any(f => f.Von <= d && d <= f.Bis && string.IsNullOrWhiteSpace(f.Klassen)))
                continue;

            var ver = versionen
                .Where(v => v.Schuljahr == kal.Schuljahr && v.GueltigAb <= d)
                .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
                .FirstOrDefault();
            if (ver == null) continue;

            if (!slotsJeVersion.TryGetValue(ver.Id, out var slots))
            {
                slots = await db.GrundplanSlots.AsNoTracking().Where(s => s.VersionId == ver.Id).ToListAsync();
                slotsJeVersion[ver.Id] = slots;
            }

            // Veröffentlichter Vertretungstag: bevorzugt aus demselben Schuljahr (wie BerechneAsync).
            var vt = vertretungsTage.Where(t => t.Datum == d)
                .OrderByDescending(t => t.Schuljahr == kal.Schuljahr)
                .ThenByDescending(t => t.Id)
                .FirstOrDefault();

            var teilFrei = kal.FreieZeiten
                .Where(f => f.Von <= d && d <= f.Bis && !string.IsNullOrWhiteSpace(f.Klassen))
                .ToList();

            var stunden = EffektiverTag(slots, iso, vt?.Eintraege, teilFrei)
                .Select(x => new PlanStunde(
                    x.Stunde, x.UNr, x.GrundUNr, x.Fach, x.Lehrer, x.GrundLehrer,
                    string.IsNullOrWhiteSpace(x.OriginalLehrer) || Gleich(x.OriginalLehrer, x.Lehrer) ? null : x.OriginalLehrer,
                    x.Raum, x.Art, x.Status, x.Info,
                    x.Klassen.OrderBy(k => k, Cmp).ToList(),
                    x.Gruppen.OrderBy(g => g, Cmp).ToList(),
                    x.Quellen.ToList()))
                .ToList();

            var freieKlassen = teilFrei
                .SelectMany(f => Liste(f.Klassen).Select(k => new FreieKlasse(k, FreiText(f))))
                .GroupBy(f => f.Klasse, Cmp)
                .Select(g => g.First())
                .ToList();

            ergebnis.Add(new TagKontext(d, kal.Schuljahr, ver.Id, stunden, freieKlassen));
        }
        return ergebnis;
    }

    /// <summary>Kalender (Schuljahr), in dem <paramref name="d"/> liegt – oder null.</summary>
    public static Task<KalenderDb?> KalenderAsync(AppDbContext db, DateOnly d)
        => db.Kalender.AsNoTracking()
            .Where(k => k.Start <= d && k.Ende >= d)
            .OrderByDescending(k => k.Id)
            .FirstOrDefaultAsync();

    /// <summary>
    /// Gibt es zu dieser Stunde ein Klassenbuch? Nur Unterricht, der stattfindet (nicht entfallen,
    /// verlegt oder frei), mit Schülern aus einer Klasse oder einem Kurs. Gruppen ohne
    /// Klassenangabe („Sonstige", z. B. „AG2-6") haben keine Schüler und kein Klassenbuch.
    /// </summary>
    public static bool KlassenbuchMoeglich(string art, string status, IEnumerable<PlanQuelle> quellen)
        => string.Equals(art, "Unterricht", StringComparison.OrdinalIgnoreCase)
           && status is not ("Entfall" or "VerlegtWeg" or "Frei")
           && quellen.Any(q => q.Typ is "Klasse" or "Kurs");

    /// <summary>Schlüssel einer Lektion (auch für die „erfasst"-Markierung im Plan).</summary>
    public static string LektionSchluessel(DateOnly datum, int stunde, int grundUNr, string grundLehrer)
        => $"{datum:yyyy-MM-dd}|{stunde}|{grundUNr}|{grundLehrer.Trim().ToUpperInvariant()}";

    /// <summary>Schlüssel eines Unterrichts (Phase 3.3): "Schuljahr|UNr|LEHRKRAFT" (Lehrkraft laut Plan).</summary>
    public static string UnterrichtSchluessel(string schuljahr, int unr, string lehrer)
        => $"{schuljahr.Trim()}|{unr}|{lehrer.Trim().ToUpperInvariant()}";

    /// <summary>Schlüssel einer zusammengestellten Gruppe: "Schuljahr|SCHÜLERGRUPPE".</summary>
    public static string GruppenSchluessel(string schuljahr, string schuelergruppe)
        => schuljahr.Trim() + "|" + schuelergruppe.Trim().ToUpperInvariant();

    /// <summary>Alle zusammengestellten Sek-I-Gruppen (Lerngruppe Typ Gruppe) als "Schuljahr|SCHLÜSSEL".</summary>
    public static async Task<HashSet<string>> FertigeGruppenAsync(AppDbContext db)
        => (await db.Lerngruppen.AsNoTracking()
                .Where(l => l.Typ == LerngruppeTyp.Gruppe)
                .Select(l => new { l.Schuljahr, l.Bezeichnung })
                .ToListAsync())
            .Select(l => GruppenSchluessel(l.Schuljahr, l.Bezeichnung))
            .ToHashSet(StringComparer.Ordinal);

    // =================================================================
    //  Auswahllisten (Phase 2, Schritt 3): Lehrkräfte, Klassen, Kurse …
    // =================================================================

    /// <summary>
    /// Liefert, was sich in der PWA auswählen lässt – aus der Grundplan-Version, die am
    /// <paramref name="datum"/> gilt. Liegt das Datum vor dem ersten Grundplan des
    /// Schuljahres, wird dessen erste Version genommen; ohne Grundplan sind alle Listen leer.
    /// </summary>
    public static async Task<AuswahlDto> AuswahlAsync(AppDbContext db, DateOnly datum)
    {
        var kal = await db.Kalender
            .Where(k => k.Start <= datum && k.Ende >= datum)
            .OrderByDescending(k => k.Id)
            .FirstOrDefaultAsync();

        IQueryable<GrundplanVersion> q = db.GrundplanVersionen;
        if (kal != null) q = q.Where(v => v.Schuljahr == kal.Schuljahr);

        var ver = await q.Where(v => v.GueltigAb <= datum)
                      .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
                      .FirstOrDefaultAsync()
                  ?? await q.OrderBy(v => v.GueltigAb).ThenBy(v => v.Id)
                      .FirstOrDefaultAsync();

        if (ver == null)
            return new AuswahlDto(null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<AuswahlGruppeDto>(), Array.Empty<AuswahlGruppeDto>());

        // „Sperre" (ufrei-Blocker) ist kein Plan-Eintrag und bleibt außen vor.
        var slots = (await db.GrundplanSlots.Where(s => s.VersionId == ver.Id).ToListAsync())
            .Where(s => !string.Equals(s.Art, "Sperre", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var unterricht = slots
            .Where(s => string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(s.LerngruppeKey))
            .ToList();

        var lehrer = slots.Select(s => s.Lehrer.Trim()).Where(l => l.Length > 0)
            .Distinct(Cmp).OrderBy(l => l, Cmp).ToList();

        var klassen = unterricht.Where(s => s.LerngruppeTyp == "Klasse")
            .Select(s => s.LerngruppeKey.Trim()).Distinct(Cmp).OrderBy(k => k, Cmp).ToList();

        var stufen = unterricht.Where(s => s.LerngruppeTyp == "Kurs")
            .Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0)
            .Distinct(Cmp).OrderBy(k => k, Cmp).ToList();

        List<AuswahlGruppeDto> Gruppiere(string typ) => unterricht
            .Where(s => s.LerngruppeTyp == typ)
            .GroupBy(s => s.LerngruppeKey.Trim(), Cmp)
            .Select(g => new AuswahlGruppeDto(
                g.Key,
                g.Select(s => s.Klasse.Trim()).FirstOrDefault(k => k.Length > 0) ?? "",
                g.GroupBy(s => s.Fach, Cmp).OrderByDescending(f => f.Count()).First().Key,
                string.Join(", ", g.Select(s => s.Lehrer.Trim()).Where(l => l.Length > 0).Distinct(Cmp))))
            .OrderBy(a => a.Stufe, Cmp).ThenBy(a => a.Fach, Cmp).ThenBy(a => a.Schluessel, Cmp)
            .ToList();

        return new AuswahlDto(
            $"{ver.Bezeichnung} (gültig ab {ver.GueltigAb:dd.MM.yyyy})",
            lehrer, klassen, stufen, Gruppiere("Kurs"), Gruppiere("Sonstige"));
    }

    // =================================================================
    //  Ein Tag: Grundplan -> klassenbezogen frei -> Vertretungen
    // =================================================================

    /// <summary>Arbeitsobjekt für eine Stunde (wird beim Overlay verändert).</summary>
    private sealed class Std
    {
        public int Stunde;
        public int UNr;                 // effektiv (bei „verlegt hin": die verlegte UNr)
        public int GrundUNr;            // laut Grundplan – für die Zuordnung der Vertretungen
        public string Fach = "";
        public string Lehrer = "";      // effektiv
        public string GrundLehrer = ""; // laut Grundplan – für die Zuordnung der Vertretungen
        public string? OriginalLehrer;
        public string? Raum;
        public string? OriginalRaum;    // gesetzt, wenn eine Raumänderung den Raum ersetzt hat
        public string Art = "Unterricht";
        public string Status = "Normal";
        public string? Info;
        public bool Geaendert;
        public readonly HashSet<string> Gruppen = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Klassen = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PlanQuelle> Quellen = new();   // Phase 3.1: Herkunft der Teilnehmer
    }

    private static List<Std> EffektiverTag(
        List<GrundplanSlot> verSlots, int iso,
        List<VertretungsEintragDb>? eintraege, List<FreieZeitDb> teilFrei)
    {
        // 1) Grundplan des Wochentags: Slots je (Stunde, UNr, Lehrkraft, Fach) zu einer
        //    Stunde bündeln (eine Stunde kann mehrere Klassen/Lerngruppen haben).
        //    „Sperre" (ufrei-Blocker) ist kein Unterricht und fällt heraus.
        var stunden = verSlots
            .Where(s => s.WTagIndex == iso && !string.Equals(s.Art, "Sperre", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => (s.Stunde, s.UNr, Lehrer: s.Lehrer.ToUpperInvariant(), s.Fach))
            .Select(g => NeueStunde(g.ToList()))
            .ToList();

        // 2) Klassenbezogene freie Zeiten (z. B. Klassenfahrt, Abiturprüfungen):
        //    Stunden, deren Klassen ALLE frei haben, entfallen.
        foreach (var f in teilFrei)
        {
            var frei = new HashSet<string>(Liste(f.Klassen), Cmp);
            foreach (var std in stunden.Where(st => st.Klassen.Count > 0 && st.Klassen.IsSubsetOf(frei)))
                Markiere(std, "Frei", FreiText(f));
        }

        if (eintraege == null || eintraege.Count == 0) return stunden;

        // 3) Veröffentlichte Änderungen des Tages darüberlegen.
        //    Lerngruppen je (UNr, Lehrkraft) der ganzen Version – für verlegte bzw.
        //    zusätzlich auftauchende Stunden (Schienen-UNr enthalten mehrere Kurse).
        var gruppenJeUNr = verSlots
            .Where(s => !string.IsNullOrWhiteSpace(s.LerngruppeKey))
            .GroupBy(s => UNrSchluessel(s.UNr, s.Lehrer))
            .ToDictionary(g => g.Key, g => g.Select(s => s.LerngruppeKey).Distinct(Cmp).ToList());
        // Phase 3.1: dieselbe Zuordnung für die Teilnehmer-Quellen (Klassenbuch).
        var quellenJeUNr = verSlots
            .Where(s => !string.Equals(s.Art, "Sperre", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => UNrSchluessel(s.UNr, s.Lehrer))
            .ToDictionary(g => g.Key, g => QuellenAus(g));

        // U-1a (Entscheidung 133): Grundplan-Stunden, die schon eine Lehrkraft aus einem Eintrag
        // bekommen haben – mit ihrem Stand davor. Bekommt dieselbe Stunde eine zweite Vertretung
        // mit anderer Lehrkraft (Gruppe geteilt, z. B. 05B an L88, 05D an L57), wird daraus eine
        // eigene Stunde, statt die erste zu überschreiben.
        var vorher = new Dictionary<Std, Std>();

        foreach (var e in eintraege)
        {
            string orig = !string.IsNullOrWhiteSpace(e.OriginalLehrer) ? e.OriginalLehrer! : (e.Lehrer ?? "");
            var eKlassen = Liste(e.Klassen);
            string status = string.IsNullOrWhiteSpace(e.Status) ? e.Art.ToString() : e.Status;
            bool klausur = status == "Klausur";

            // Grundplan-Stunde suchen: exakt über die UNr, sonst über die Klassen.
            // Klausur nur exakt (UNr + Lehrkraft des Kurses) – über die Klassen träfe die
            // Aufsicht sonst eine eigene Stunde derselben Stufe.
            Std? ziel = null;
            if (e.GrundUNr.HasValue)
                ziel = stunden.FirstOrDefault(st =>
                    st.Stunde == e.Stunde && st.GrundUNr == e.GrundUNr.Value && Gleich(st.GrundLehrer, orig));
            if (!klausur)
                ziel ??= stunden.FirstOrDefault(st =>
                    st.Stunde == e.Stunde && Gleich(st.GrundLehrer, orig) &&
                    (eKlassen.Count == 0 || st.Klassen.Overlaps(eKlassen)));

            switch (status)
            {
                case "Entfall":
                case "VerlegtWeg":
                    if (ziel != null)
                        Markiere(ziel, status, status == "Entfall" ? "entfällt" : "verlegt");
                    break;

                case "Raumvertretung":
                    if (ziel != null)
                    {
                        if (!string.IsNullOrWhiteSpace(e.Raum) && !Gleich(e.Raum, ziel.Raum))
                        {
                            ziel.OriginalRaum ??= ziel.Raum;
                            ziel.Raum = e.Raum;
                        }
                        Markiere(ziel, status, "Raumwechsel");
                    }
                    break;

                default:
                {
                    // Vertreten, Betreuung, EVA, Zusammenlegung, Klausur, VerlegtHin (und Unbekanntes):
                    // eine Lehrkraft hält die Stunde – ggf. eine andere als laut Grundplan.
                    string neuerLehrer = (e.Lehrer ?? "").Trim();
                    bool verlegtHin = status == "VerlegtHin";

                    // 133: zweite Vertretung derselben Grundplan-Stunde mit anderer Lehrkraft.
                    if (ziel != null && !verlegtHin && vorher.TryGetValue(ziel, out var basis)
                        && !Gleich(neuerLehrer, ziel.Lehrer))
                        ziel = Teilstunde(stunden, basis, neuerLehrer, orig);
                    else if (ziel != null && !verlegtHin && !vorher.ContainsKey(ziel))
                        vorher[ziel] = Kopie(ziel);

                    bool zusaetzlich = ziel == null;
                    var x = ziel ?? NeueAusEintrag(stunden, e, orig, eKlassen);

                    if (neuerLehrer.Length > 0 && !Gleich(neuerLehrer, x.Lehrer))
                    {
                        x.OriginalLehrer ??= x.Lehrer;
                        x.Lehrer = neuerLehrer;
                    }
                    else if (neuerLehrer.Length == 0 && !zusaetzlich && !verlegtHin && !string.IsNullOrWhiteSpace(e.OriginalLehrer))
                    {
                        // U-1a: ausdrücklich abwesend, aber niemand übernimmt (Untis „Plus“ → EVA,
                        // Klausur ohne Aufsicht): die abwesende Lehrkraft erscheint gestrichen.
                        x.OriginalLehrer ??= x.Lehrer;
                        x.Lehrer = "";
                    }

                    if (verlegtHin)
                    {
                        // Hier wird die (von woanders) verlegte Stunde gehalten.
                        if (!string.IsNullOrWhiteSpace(e.Fach)) x.Fach = FachNorm(e.Fach);
                        if (e.UNr.HasValue && e.UNr.Value > 0) x.UNr = e.UNr.Value;
                        ErgaenzeGruppen(x, e.UNr, x.Lehrer, eKlassen, gruppenJeUNr);
                        // Teilnehmer sind die der verlegten Stunde.
                        SetzeQuellen(x, e.UNr, x.Lehrer, eKlassen, quellenJeUNr);
                        UebernimmWechsel(x, e, eKlassen, klausur: false);
                        Markiere(x, status, "verlegt");
                    }
                    else
                    {
                        if (zusaetzlich)
                        {
                            ErgaenzeGruppen(x, e.UNr ?? e.GrundUNr, orig, eKlassen, gruppenJeUNr);
                            SetzeQuellen(x, e.UNr ?? e.GrundUNr, orig, eKlassen, quellenJeUNr);
                        }
                        else
                        {
                            UebernimmWechsel(x, e, eKlassen, klausur);
                        }
                        Markiere(x, status, InfoText(status));
                    }
                    break;
                }
            }
        }
        return stunden;
    }

    private static Std NeueStunde(List<GrundplanSlot> slots)
    {
        var f = slots[0];
        var x = new Std
        {
            Stunde = f.Stunde,
            UNr = f.UNr,
            GrundUNr = f.UNr,
            Fach = f.Fach,
            Lehrer = f.Lehrer,
            GrundLehrer = f.Lehrer,
            Art = string.IsNullOrWhiteSpace(f.Art) ? "Unterricht" : f.Art,
            // U-1a (R3): zugewiesener Raum aus dem Raumplan; sonst wie bisher Fachraum
            // (z. B. Bio-Raum) vor Stammraum der Klasse.
            Raum = slots.Select(sl => sl.Raum).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
                   ?? slots.Select(sl => sl.Fachraum).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
                   ?? slots.Select(sl => sl.Stammraum).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
        };
        foreach (var slot in slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.LerngruppeKey)) x.Gruppen.Add(slot.LerngruppeKey);
            if (!string.IsNullOrWhiteSpace(slot.Klasse)) x.Klassen.Add(slot.Klasse.Trim());
        }
        x.Quellen.AddRange(QuellenAus(slots));
        return x;
    }

    /// <summary>Teilnehmer-Quellen aus Grundplan-Slots (ohne Doppelte).</summary>
    internal static List<PlanQuelle> QuellenAus(IEnumerable<GrundplanSlot> slots)
    {
        var ergebnis = new List<PlanQuelle>();
        var gesehen = new HashSet<string>(Cmp);
        foreach (var s in slots)
        {
            string key = (s.LerngruppeKey ?? "").Trim();
            if (key.Length == 0) continue;
            string typ = string.IsNullOrWhiteSpace(s.LerngruppeTyp) ? "Klasse" : s.LerngruppeTyp.Trim();
            string? sg = string.IsNullOrWhiteSpace(s.Schuelergruppe) ? null : s.Schuelergruppe.Trim();
            if (!gesehen.Add($"{typ}|{key}|{sg}")) continue;
            ergebnis.Add(new PlanQuelle(typ, key, (s.Klasse ?? "").Trim(), sg));
        }
        return ergebnis;
    }

    /// <summary>
    /// Quellen einer verlegten bzw. zusätzlichen Stunde: aus dem Grundplan der UNr dieser
    /// Lehrkraft; ohne Treffer aus den Klassen des Vertretungseintrags (nur echte Klassen).
    /// </summary>
    private static void SetzeQuellen(
        Std x, int? unr, string lehrer, List<string> eKlassen, Dictionary<string, List<PlanQuelle>> quellenJeUNr)
    {
        if (unr.HasValue && quellenJeUNr.TryGetValue(UNrSchluessel(unr.Value, lehrer), out var q) && q.Count > 0)
        {
            x.Quellen.Clear();
            x.Quellen.AddRange(q);
            return;
        }
        if (x.Quellen.Count > 0) return;
        foreach (var k in eKlassen.Where(k => KlassenMuster.IsMatch(k)).Distinct(Cmp))
            x.Quellen.Add(new PlanQuelle("Klasse", k.ToUpperInvariant(), k, null));
    }

    /// <summary>Stunde, die im Grundplan nicht vorkommt (z. B. Verlegung auf eine freie Stunde).</summary>
    private static Std NeueAusEintrag(List<Std> stunden, VertretungsEintragDb e, string orig, List<string> eKlassen)
    {
        var x = new Std
        {
            Stunde = e.Stunde,
            UNr = e.UNr ?? e.GrundUNr ?? 0,
            GrundUNr = e.GrundUNr ?? 0,
            Fach = FachNorm(e.Fach),
            Lehrer = orig,
            GrundLehrer = orig,
            Raum = e.Raum
        };
        foreach (var k in eKlassen) x.Klassen.Add(k);
        stunden.Add(x);
        return x;
    }

    /// <summary>
    /// U-1a (Entscheidung 132): Fach, Klassen und Raum aus dem Eintrag übernehmen – nur, wenn der
    /// Desktop sie ausdrücklich als geändert meldet (Untis: Vertretungsfach, -klassen, -raum).
    /// Ältere Desktop-Versionen und das Vertretungsmodul senden die Werte der Grundplan-Zelle mit
    /// (mit anderer Schreibweise bzw. dem Raum „Stammraum vor Fachraum“); die bleiben unbeachtet.
    /// Bei einer Klausur gelten Fach (Kurs) und Raum immer.
    /// </summary>
    private static void UebernimmWechsel(Std x, VertretungsEintragDb e, List<string> eKlassen, bool klausur)
    {
        if ((e.FachGeaendert == true || klausur) && !string.IsNullOrWhiteSpace(e.Fach))
            x.Fach = FachNorm(e.Fach);
        if ((e.RaumGeaendert == true || klausur) && !string.IsNullOrWhiteSpace(e.Raum) && !Gleich(e.Raum, x.Raum))
        {
            x.OriginalRaum ??= x.Raum;
            x.Raum = e.Raum!.Trim();
        }
        if (e.KlassenGeaendert == true && eKlassen.Count > 0)
            SetzeKlassen(x, eKlassen);
    }

    /// <summary>
    /// Klassen einer Stunde ersetzen (z. B. Untis-Vertretungsklassen): weggefallene Klassen
    /// verlieren ihren Klassen-Schlüssel und ihre Klassen-Quelle, neue Klassen bekommen beides.
    /// Kurs- und Gruppenschlüssel bleiben.
    /// </summary>
    private static void SetzeKlassen(Std x, List<string> neu)
    {
        var soll = new HashSet<string>(neu.Select(k => k.Trim()).Where(k => k.Length > 0), Cmp);
        if (soll.Count == 0 || soll.SetEquals(x.Klassen)) return;

        foreach (var k in x.Klassen.Where(k => !soll.Contains(k)).ToList())
        {
            x.Klassen.Remove(k);
            if (KlassenMuster.IsMatch(k)) x.Gruppen.Remove(k);
            x.Quellen.RemoveAll(q => q.Typ == "Klasse" && Gleich(q.Klasse, k));
        }
        foreach (var k in soll.Where(k => !x.Klassen.Contains(k)).ToList())
        {
            x.Klassen.Add(k);
            if (!KlassenMuster.IsMatch(k)) continue;
            x.Gruppen.Add(k.ToUpperInvariant());
            if (!x.Quellen.Any(q => q.Typ == "Klasse" && Gleich(q.Key, k)))
                x.Quellen.Add(new PlanQuelle("Klasse", k.ToUpperInvariant(), k, null));
        }
    }

    /// <summary>Unabhängige Kopie einer Stunde (Stand vor der ersten Vertretung, Entscheidung 133).</summary>
    private static Std Kopie(Std s)
    {
        var k = new Std
        {
            Stunde = s.Stunde, UNr = s.UNr, GrundUNr = s.GrundUNr, Fach = s.Fach,
            Lehrer = s.Lehrer, GrundLehrer = s.GrundLehrer, OriginalLehrer = s.OriginalLehrer,
            Raum = s.Raum, OriginalRaum = s.OriginalRaum, Art = s.Art, Status = s.Status,
            Info = s.Info, Geaendert = s.Geaendert
        };
        foreach (var g in s.Gruppen) k.Gruppen.Add(g);
        foreach (var kl in s.Klassen) k.Klassen.Add(kl);
        k.Quellen.AddRange(s.Quellen);
        return k;
    }

    /// <summary>
    /// Entscheidung 133: weitere Vertretung derselben Grundplan-Stunde als eigene Stunde (Stand vor
    /// der ersten Vertretung). Damit das Klassenbuch beide Hälften getrennt führt, bekommt die neue
    /// Stunde einen eigenen Lektionsschlüssel: als GrundLehrer die vertretende Lehrkraft, sonst
    /// „Kürzel·2“ (bzw. ·3 …), falls der Schlüssel schon vergeben ist.
    /// </summary>
    private static Std Teilstunde(List<Std> stunden, Std basis, string neuerLehrer, string orig)
    {
        var t = Kopie(basis);
        bool Frei(string grundLehrer) => !stunden.Any(st =>
            st.Stunde == t.Stunde && st.GrundUNr == t.GrundUNr && Gleich(st.GrundLehrer, grundLehrer));
        string schluessel = neuerLehrer;
        for (int i = 2; schluessel.Length == 0 || !Frei(schluessel); i++)
            schluessel = $"{orig.Trim()}·{i}";
        t.GrundLehrer = schluessel;
        stunden.Add(t);
        return t;
    }

    /// <summary>Fach ohne doppelte Leerzeichen („D  G1“ aus Untis bzw. der UV → „D G1“ wie im Grundplan).</summary>
    private static string FachNorm(string? fach)
        => Regex.Replace((fach ?? "").Trim(), @"\s+", " ");

    private static void ErgaenzeGruppen(
        Std x, int? unr, string lehrer, List<string> eKlassen, Dictionary<string, List<string>> gruppenJeUNr)
    {
        if (unr.HasValue && gruppenJeUNr.TryGetValue(UNrSchluessel(unr.Value, lehrer), out var keys))
            foreach (var k in keys) x.Gruppen.Add(k);
        foreach (var k in eKlassen)
        {
            x.Klassen.Add(k);
            if (KlassenMuster.IsMatch(k)) x.Gruppen.Add(k.ToUpperInvariant());
        }
    }

    private static bool Passt(Std x, WochenZielArt art, HashSet<string> werte, Func<string, bool> gruppeFertig)
    {
        // Vertretungsbereitschaft (VBer), UV-Stunden und Pseudofächer (PSF…, Art „Pseudo")
        // sind Dienste der Lehrkraft, kein Unterricht einer Lerngruppe.
        bool bereitschaft = x.Art is "VBer" or "UV" or "Pseudo";
        return art switch
        {
            WochenZielArt.Lehrer => werte.Contains(x.Lehrer)
                                    || (x.OriginalLehrer != null && werte.Contains(x.OriginalLehrer)),
            WochenZielArt.Klasse => !bereitschaft && (x.Klassen.Overlaps(werte) || x.Gruppen.Overlaps(werte)),
            WochenZielArt.Schueler => !bereitschaft && PasstSchueler(x, werte, gruppeFertig),
            _ => !bereitschaft && x.Gruppen.Overlaps(werte)
        };
    }

    /// <summary>
    /// Schülerplan (Phase 3.1): Eine Stunde mit zusammengestellter Sek-I-Gruppe (Religion, WP …)
    /// sieht nur, wer in der Gruppe ist; sonst wie bisher alle Schüler der genannten Klasse(n).
    /// </summary>
    private static bool PasstSchueler(Std x, HashSet<string> werte, Func<string, bool> gruppeFertig)
    {
        if (x.Quellen.Count == 0) return x.Gruppen.Overlaps(werte);
        foreach (var q in x.Quellen)
        {
            if (q.Typ == "Klasse" && q.Schuelergruppe != null && gruppeFertig(q.Schuelergruppe))
            {
                if (werte.Contains(q.Schuelergruppe)) return true;
            }
            else if (werte.Contains(q.Key))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Thema/Hausaufgabe einer Lektion für den Wochenplan (Phase 3.3).</summary>
    private sealed record StundenInhalt(string? Thema, string? Hausaufgabe, DateOnly? Faellig);

    private static WochenStundeDto ZuDto(Std x, bool erfasst, StundenInhalt? inhalt) => new(
        x.Stunde,
        x.UNr,
        x.Fach,
        x.Lehrer,
        string.IsNullOrWhiteSpace(x.OriginalLehrer) || Gleich(x.OriginalLehrer, x.Lehrer) ? null : x.OriginalLehrer,
        x.Raum,
        string.IsNullOrWhiteSpace(x.OriginalRaum) || Gleich(x.OriginalRaum, x.Raum) ? null : x.OriginalRaum,
        x.Gruppen.OrderBy(g => g, Cmp).ToList(),
        x.Klassen.OrderBy(k => k, Cmp).ToList(),
        x.Art,
        x.Status,
        x.Info,
        x.Geaendert,
        x.GrundUNr,
        x.GrundLehrer,
        KlassenbuchMoeglich(x.Art, x.Status, x.Quellen),
        erfasst,
        inhalt?.Thema,
        inhalt?.Hausaufgabe,
        inhalt?.Faellig);

    // =================================================================
    //  Hilfsfunktionen
    // =================================================================

    private static void Markiere(Std x, string status, string? info)
    {
        x.Status = status;
        x.Info = info;
        x.Geaendert = true;
    }

    private static string InfoText(string status) => status switch
    {
        "Vertreten" => "Vertretung",
        "Betreuung" => "Betreuung/Aufsicht",
        "EVA" => "Eigenverantwortliches Arbeiten",
        "Zusammenlegung" => "Zusammenlegung",
        "Klausur" => "Klausur",
        _ => "geändert"
    };

    private static WochenTagDto LeererTag(DateOnly d, string wtag, string status, string? hinweis)
        => new(d, wtag, status, hinweis, null, null, false, Array.Empty<WochenStundeDto>());

    private static string FreiText(FreieZeitDb f)
        => string.IsNullOrWhiteSpace(f.Bezeichnung) ? f.Typ : f.Bezeichnung;

    private static string ZielName(WochenZielArt art) => art switch
    {
        WochenZielArt.Lehrer => "lehrer",
        WochenZielArt.Klasse => "klasse",
        WochenZielArt.Gruppe => "gruppe",
        _ => "schueler"
    };

    private static string UNrSchluessel(int unr, string lehrer) => unr + "|" + lehrer.Trim().ToUpperInvariant();

    private static bool Gleich(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<string> Liste(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Unterrichtstage laut Kalender (ISO 1–7); leer = Mo–Fr.</summary>
    private static HashSet<int> RasterTage(KalenderDb kal)
    {
        var set = new HashSet<int>();
        foreach (var t in Liste(kal.RasterTage))
        {
            int i = IsoAusName(t);
            if (i > 0) set.Add(i);
        }
        if (set.Count == 0)
            for (int i = 1; i <= 5; i++) set.Add(i);
        return set;
    }

    private static int IsoAusName(string name)
    {
        string t = name.Trim().ToLowerInvariant();
        if (t.StartsWith("mo")) return 1;
        if (t.StartsWith("di")) return 2;
        if (t.StartsWith("mi")) return 3;
        if (t.StartsWith("do")) return 4;
        if (t.StartsWith("fr")) return 5;
        if (t.StartsWith("sa")) return 6;
        if (t.StartsWith("so")) return 7;
        return 0;
    }
}
