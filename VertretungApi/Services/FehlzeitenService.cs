using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Fehlzeiten und Entschuldigungen (Phase 3.2).
//
//  Zählung: Fehlstunden = „fehlt" + „beurlaubt" (beurlaubt = entschuldigt);
//  Verspätungen getrennt (Anzahl + Minuten); „schulische Veranstaltung" ist
//  keine Fehlzeit. Jede Fehlstunde/Verspätung ist offen, entschuldigt oder
//  unentschuldigt (Entscheidungen 13–18, Nutzer 02.11.2026):
//   - Klassenleitung (Sek I) / Stufenkoordination (Sek II) / Admin: alles –
//     Zeiträume entschuldigen (decken auch später nachgetragene Stunden ab),
//     einzelne Stunden bewerten, Entschuldigungen löschen. Ihre Bewertung ist
//     „festgelegt".
//   - Kurslehrkraft der Oberstufe (laut Plan, nicht die Vertretung): in den
//     eigenen Kursstunden entschuldigt/offen/unentschuldigt – solange nicht
//     festgelegt.
//   - Übrige Lehrkräfte: sehen nur Einträge aus ihren eigenen Stunden.
//   - Schüler: eigene Fehlzeiten (lesen).
//  „Unentschuldigt" nur von Hand, ohne Frist.
//
//  Nicht erfasste Stunden: ab „Klassenbuch:ErfassungAb", offen ab dem
//  Folgetag; Lehrkraft = eigene gehaltene Stunden, KL/SK = Stunden ihrer
//  Klasse/Stufe, Admin = alle (Filter Lehrkraft/Klasse).
//  Phase 3.3: dazu Stunden ohne Thema (ab „Klassenbuch:ThemaAb", ohne
//  Betreuung) – Marke „Thema fehlt", Filter alle | anwesenheit | thema.
//
//  Phase 4.1 (Auswertungen am Bildschirm, Entscheidungen 40–43, Nutzer
//  04.11.2026):
//   - Zeiträume: 1. Halbjahr, 2. Halbjahr, Schuljahr (+ eigener Zeitraum in
//     der PWA). Das 2. Halbjahr beginnt laut „Klassenbuch:HalbjahrAb"
//     (2027-02-01); liegt die Einstellung nicht im Schuljahr, gilt der
//     1. Februar. Standard ist das laufende Halbjahr.
//   - Soll = erfasste Stunden im Zeitraum, in denen der Schüler laut Liste
//     dabei sein sollte (Klasse/Kurs, bei Sek-I-Gruppen nur die Mitglieder),
//     dazu jede erfasste Stunde, in der er einen Eintrag hat. Fehlquote =
//     Fehlstunden ÷ Soll – nur Anzeige, keine Schwellen (Entscheidung 42).
//   - Listen tragen eine Gesamtzeile; offene Fälle je Klasse/Stufe über das
//     ganze Schuljahr, älteste zuerst (Entscheidung 43, ohne Frist/Automatik).
// =====================================================================

public static class FehlzeitenService
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] OberstufenStufen = { "EF", "Q1", "Q2" };

    /// <summary>Höchstens so viele nicht erfasste Stunden in einer Antwort.</summary>
    public const int MaxNichtErfasst = 500;

    /// <summary>Eine Fehlstunde/Verspätung … mit den Daten ihrer Lektion.</summary>
    private sealed record Zeile(
        int Id, int SchuelerId, DateOnly Datum, int Stunde, int UNr, int GrundUNr, string Fach,
        string Lehrer, string GrundLehrer, string Klassen, AnwesenheitStatus Status, int? Minuten,
        FehlBewertung Bewertung, FehlGrund? Grund, int? EntschuldigungId, string? BewertetVon,
        DateTime? BewertetAm, bool Festgelegt);

    // =================================================================
    //  Zeitraum, Rechte, Texte
    // =================================================================

    /// <summary>Standard: das laufende Schuljahr laut Kalender (sonst die letzten 12 Monate).</summary>
    public static async Task<(DateOnly Von, DateOnly Bis)> ZeitraumAsync(AppDbContext db, DateOnly? von, DateOnly? bis)
    {
        var heute = KlassenbuchService.Heute();
        var kal = await WochenplanService.KalenderAsync(db, heute);
        var v = von ?? kal?.Start ?? heute.AddDays(-365);
        var b = bis ?? kal?.Ende ?? heute;
        return b < v ? (b, v) : (v, b);
    }

    /// <summary>
    /// Phase 4.1 (Entscheidung 40): wählbare Zeiträume des laufenden Schuljahres.
    /// Ohne Kalender: nur die letzten 12 Monate.
    /// </summary>
    public static async Task<FzZeitraeumeDto> ZeitraeumeAsync(AppDbContext db, DateOnly? halbjahrAb)
    {
        var heute = KlassenbuchService.Heute();
        var kal = await WochenplanService.KalenderAsync(db, heute);
        if (kal == null)
            return new FzZeitraeumeDto(null, heute, "sj",
                new List<FzZeitraumDto> { new("sj", "Letzte 12 Monate", heute.AddDays(-365), heute) });

        var liste = new List<FzZeitraumDto>();
        var hj = HalbjahrAb(kal.Start, kal.Ende, halbjahrAb);
        if (hj is DateOnly h)
        {
            liste.Add(new FzZeitraumDto("hj1", "1. Halbjahr", kal.Start, h.AddDays(-1)));
            liste.Add(new FzZeitraumDto("hj2", "2. Halbjahr", h, kal.Ende));
        }
        liste.Add(new FzZeitraumDto("sj", "Schuljahr", kal.Start, kal.Ende));
        string standard = hj is DateOnly h2 ? (heute < h2 ? "hj1" : "hj2") : "sj";
        return new FzZeitraeumeDto(kal.Schuljahr, heute, standard, liste);
    }

    /// <summary>
    /// Erster Tag des 2. Halbjahres: die Einstellung, wenn sie im Schuljahr liegt, sonst der
    /// 1. Februar des Schuljahres; null, wenn auch der nicht hineinfällt.
    /// </summary>
    public static DateOnly? HalbjahrAb(DateOnly start, DateOnly ende, DateOnly? einstellung)
    {
        if (einstellung is DateOnly e && e > start && e <= ende) return e;
        var februar = new DateOnly(start.Year + 1, 2, 1);
        return februar > start && februar <= ende ? februar : null;
    }

    public static bool IstOberstufe(string? klassen)
        => (klassen ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(k => OberstufenStufen.Contains(k, Cmp));

    /// <summary>Klassenleitung/Stufenkoordination für Klasse oder Stufe des Schülers – oder Admin.</summary>
    public static bool IstLeitungFuer(KbNutzer u, string? klasse, string? stufe)
    {
        if (u.IstAdmin) return true;
        if (!u.IstKlassenleitungOderKoordination) return false;
        var werte = new[] { klasse, stufe }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
        return KlassenbuchService.ZustaendigFuer(u.Zustaendig, werte);
    }

    private static bool IstKurslehrer(KbNutzer u, string grundLehrer, string klassen)
        => u.Kuerzel != null && KlassenbuchService.Gleich(grundLehrer, u.Kuerzel) && IstOberstufe(klassen);

    public static string GrundText(FehlGrund g) => g switch
    {
        FehlGrund.Krankheit => "Krankheit",
        FehlGrund.Arzttermin => "Arzttermin",
        FehlGrund.Familiaer => "familiär",
        _ => "sonstiges"
    };

    public static string BewertungText(FehlBewertung b, FehlGrund? g) => b switch
    {
        FehlBewertung.Entschuldigt => g is FehlGrund grund ? $"entschuldigt ({GrundText(grund)})" : "entschuldigt",
        FehlBewertung.Unentschuldigt => "unentschuldigt",
        _ => "offen"
    };

    private static async Task<List<Zeile>> LadeAsync(IQueryable<Anwesenheit> q)
        => await q.AsNoTracking()
            .Select(a => new Zeile(
                a.Id, a.SchuelerId, a.Lektion!.Datum, a.Lektion.Stunde, a.Lektion.UNr, a.Lektion.GrundUNr,
                a.Lektion.Fach, a.Lektion.Lehrer, a.Lektion.GrundLehrer, a.Lektion.Klassen, a.Status, a.Minuten,
                a.Bewertung, a.Grund, a.EntschuldigungId, a.BewertetVon, a.BewertetAm, a.Festgelegt))
            .ToListAsync();

    private static IQueryable<Anwesenheit> ImZeitraum(AppDbContext db, DateOnly von, DateOnly bis)
        => db.Anwesenheiten.Where(a => a.Lektion!.Datum >= von && a.Lektion.Datum <= bis);

    private static FzSummeDto Summe(IEnumerable<Zeile> zeilen)
    {
        int fehl = 0, ent = 0, offen = 0, unent = 0, versp = 0, minuten = 0, vOffen = 0, vUnent = 0, veranst = 0;
        foreach (var z in zeilen)
        {
            switch (z.Status)
            {
                case AnwesenheitStatus.Fehlt:
                    fehl++;
                    if (z.Bewertung == FehlBewertung.Entschuldigt) ent++;
                    else if (z.Bewertung == FehlBewertung.Unentschuldigt) unent++;
                    else offen++;
                    break;
                case AnwesenheitStatus.Beurlaubt:
                    fehl++;
                    ent++;
                    break;
                case AnwesenheitStatus.Verspaetet:
                    versp++;
                    minuten += z.Minuten ?? 0;
                    if (z.Bewertung == FehlBewertung.Offen) vOffen++;
                    else if (z.Bewertung == FehlBewertung.Unentschuldigt) vUnent++;
                    break;
                case AnwesenheitStatus.Schulveranstaltung:
                    veranst++;
                    break;
            }
        }
        return new FzSummeDto(fehl, ent, offen, unent, versp, minuten, vOffen, vUnent, veranst);
    }

    /// <summary>Phase 4.3 (CSV-Export): dieselbe Zählung für Werte aus einer eigenen Abfrage.</summary>
    internal static FzSummeDto Summe(IEnumerable<(AnwesenheitStatus Status, int? Minuten, FehlBewertung Bewertung)> werte)
        => Summe(werte.Select(w => new Zeile(0, 0, default, 0, 0, 0, "", "", "", "",
            w.Status, w.Minuten, w.Bewertung, null, null, null, null, false)));

    /// <summary>Klasse (Sek I) bzw. Stufe (Sek II) eines Schülers für Rechte und Anzeige.</summary>
    private static async Task<(Schueler? S, string? Klasse)> SchuelerMitKlasseAsync(AppDbContext db, int id)
    {
        var s = await db.Schueler.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return (null, null);
        var kal = await WochenplanService.KalenderAsync(db, KlassenbuchService.Heute());
        var klassen = await KlassenbuchService.KlassenAsync(db, new List<int> { id }, kal?.Schuljahr ?? "");
        return (s, klassen.TryGetValue(id, out var k) ? k : null);
    }

    // =================================================================
    //  Rechte / Auswahl
    // =================================================================

    public static async Task<FzRechteDto> RechteAsync(AppDbContext db, KbNutzer u, DateOnly erfassungAb, bool berichteNurAdmin = false)
    {
        List<string> klassen = u.IstAdmin
            ? await AlleKlassenUndStufenAsync(db)
            : u.IstKlassenleitungOderKoordination ? u.Zustaendig.ToList() : new List<string>();
        return new FzRechteDto(u.Kuerzel != null, u.IstAdmin, klassen, erfassungAb, u.IstAdmin || !berichteNurAdmin);
    }

    /// <summary>
    /// Alle Klassen (Lerngruppen vom Typ Klasse) und Oberstufen-Stufen mit Schülern – Sek-I-Klassen
    /// zuerst. Auswahl des Admins; Phase 4.3 auch für den CSV-Export „alle Klassen".
    /// </summary>
    internal static async Task<List<string>> AlleKlassenUndStufenAsync(AppDbContext db)
    {
        var namen = await db.Lerngruppen.AsNoTracking()
            .Where(l => l.Typ == LerngruppeTyp.Klasse)
            .Select(l => l.Bezeichnung)
            .ToListAsync();
        var stufen = await db.Schueler.AsNoTracking()
            .Select(x => x.Stufe)
            .Distinct()
            .ToListAsync();
        return namen
            .Concat(stufen.Where(st => OberstufenStufen.Contains(st, Cmp)))
            .Select(k => k.Trim().ToUpperInvariant())
            .Where(k => k.Length > 0)
            .Distinct(Cmp)
            .OrderBy(k => k.Length > 0 && char.IsDigit(k[0]) ? 0 : 1)
            .ThenBy(k => k, Cmp)
            .ToList();
    }

    /// <summary>„Meine Kurse": Unterrichte der Lehrkraft im heute gültigen Grundplan (mit Klassenbuch).</summary>
    public static async Task<List<FzUnterrichtDto>> UnterrichteAsync(AppDbContext db, string kuerzel)
    {
        var ver = await KlassenbuchService.AktuelleVersionAsync(db);
        if (ver == null) return new List<FzUnterrichtDto>();
        var slots = (await db.GrundplanSlots.AsNoTracking().Where(s => s.VersionId == ver.Id).ToListAsync())
            .Where(s => KlassenbuchService.Gleich(s.Lehrer, kuerzel)
                        && string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)
                        && s.LerngruppeTyp is "Klasse" or "Kurs")
            .ToList();
        return slots
            .GroupBy(s => s.UNr)
            .Select(g =>
            {
                var klassen = g.Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0)
                    .Distinct(Cmp).OrderBy(k => k, Cmp).ToList();
                string fach = g.GroupBy(s => s.Fach.Trim(), Cmp).OrderByDescending(f => f.Count()).First().Key;
                return new FzUnterrichtDto(g.Key, kuerzel.Trim().ToUpperInvariant(), fach, klassen,
                    klassen.Any(k => OberstufenStufen.Contains(k, Cmp)));
            })
            .OrderBy(x => x.Oberstufe)
            .ThenBy(x => string.Join(",", x.Klassen), Cmp)
            .ThenBy(x => x.Fach, Cmp)
            .ToList();
    }

    // =================================================================
    //  Listen
    // =================================================================

    /// <summary>Schüler einer Klasse (Sek I) bzw. – ohne Klasse dieses Namens – einer Stufe (Sek II).</summary>
    internal static async Task<(List<int> Ids, bool IstStufe)> KlassenSchuelerAsync(AppDbContext db, string klasse)
    {
        string k = klasse.Trim();
        var lgIds = (await db.Lerngruppen.AsNoTracking().Where(l => l.Typ == LerngruppeTyp.Klasse).ToListAsync())
            .Where(l => KlassenbuchService.Gleich(l.Bezeichnung, k))
            .Select(l => l.Id)
            .ToList();
        if (lgIds.Count > 0)
            return (await db.Zugehoerigkeiten.AsNoTracking()
                .Where(z => lgIds.Contains(z.LerngruppeId))
                .Select(z => z.SchuelerId).Distinct().ToListAsync(), false);

        string gross = k.ToUpperInvariant();
        return (await db.Schueler.AsNoTracking()
            .Where(x => x.Stufe.ToUpper() == gross)
            .Select(x => x.Id).ToListAsync(), true);
    }

    private static string GruppenTitel(string klasse, bool istStufe)
        => istStufe ? $"Stufe {klasse.Trim().ToUpperInvariant()}" : $"Klasse {klasse.Trim().ToUpperInvariant()}";

    /// <summary>Alle Schüler einer Klasse bzw. Stufe mit ihren Summen und Soll-Stunden.</summary>
    public static async Task<FzListeDto> KlasseAsync(AppDbContext db, string klasse, DateOnly von, DateOnly bis)
    {
        string k = klasse.Trim();
        var (ids, istStufe) = await KlassenSchuelerAsync(db, k);

        var zeilen = ids.Count == 0
            ? new List<Zeile>()
            : await LadeAsync(ImZeitraum(db, von, bis).Where(a => ids.Contains(a.SchuelerId)));
        var soll = await SollAsync(db, ids, von, bis, klasse: k);
        var liste = await ZeilenJeSchuelerAsync(db, ids, zeilen, istStufe ? null : k.ToUpperInvariant(), soll);
        return new FzListeDto(GruppenTitel(k, istStufe), von, bis, null, liste, Summe(zeilen), liste.Sum(x => x.Soll));
    }

    /// <summary>
    /// Teilnehmer eines Unterrichts (UNr + Lehrkraft laut Plan) mit ihren Summen in diesem Unterricht.
    /// null = Unterricht unbekannt (weder im Grundplan noch in erfassten Stunden).
    /// </summary>
    public static async Task<FzListeDto?> UnterrichtAsync(
        AppDbContext db, KbNutzer u, int unr, string lehrer, DateOnly von, DateOnly bis)
    {
        string l = lehrer.Trim().ToUpperInvariant();
        var (titel, mitglieder) = await UnterrichtInfoAsync(db, u, unr, l);

        var zeilen = await LadeAsync(ImZeitraum(db, von, bis)
            .Where(a => a.Lektion!.UNr == unr && a.Lektion.GrundLehrer == l));
        int erfasst = await db.Lektionen.AsNoTracking()
            .CountAsync(x => x.UNr == unr && x.GrundLehrer == l && x.Datum >= von && x.Datum <= bis && x.ErfasstAm != null);
        if (titel == null && zeilen.Count == 0 && erfasst == 0) return null;

        var ids = mitglieder.Concat(zeilen.Select(z => z.SchuelerId)).Distinct().ToList();
        var soll = await SollAsync(db, ids, von, bis, unr: unr, grundLehrer: l);
        var liste = await ZeilenJeSchuelerAsync(db, ids, zeilen, null, soll);
        if (titel == null)
        {
            var z0 = zeilen.OrderByDescending(z => z.Datum).FirstOrDefault();
            titel = z0 == null ? $"Unterricht {unr}" : $"{z0.Fach} · {z0.Klassen.Replace(",", ", ")}";
        }
        return new FzListeDto(titel, von, bis, erfasst, liste, Summe(zeilen), liste.Sum(x => x.Soll));
    }

    /// <summary>Titel („M L1 · Q1") und aktuelle Teilnehmer eines Unterrichts laut heute gültigem Grundplan.</summary>
    private static async Task<(string? Titel, List<int> Mitglieder)> UnterrichtInfoAsync(
        AppDbContext db, KbNutzer u, int unr, string lehrer)
    {
        var ver = await KlassenbuchService.AktuelleVersionAsync(db);
        if (ver == null) return (null, new List<int>());
        var slots = (await db.GrundplanSlots.AsNoTracking()
                .Where(s => s.VersionId == ver.Id && s.UNr == unr)
                .ToListAsync())
            .Where(s => KlassenbuchService.Gleich(s.Lehrer, lehrer)
                        && string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (slots.Count == 0) return (null, new List<int>());

        var klassen = slots.Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0)
            .Distinct(Cmp).OrderBy(k => k, Cmp).ToList();
        string fach = slots.GroupBy(s => s.Fach.Trim(), Cmp).OrderByDescending(f => f.Count()).First().Key;

        var quellen = WochenplanService.QuellenAus(slots);
        var st = new PlanStunde(0, unr, unr, fach, lehrer, lehrer, null, null, "Unterricht", "Normal", null,
            klassen, quellen.Select(q => q.Key).Distinct(Cmp).ToList(), quellen);
        var tag = new TagKontext(KlassenbuchService.Heute(), ver.Schuljahr, ver.Id,
            new List<PlanStunde>(), new List<FreieKlasse>());
        var (liste, _, _) = await KlassenbuchService.TeilnehmerAsync(db, u, tag, st);
        return ($"{fach} · {string.Join(", ", klassen)}", liste.Select(t => t.Id).ToList());
    }

    private static async Task<List<FzZeileDto>> ZeilenJeSchuelerAsync(
        AppDbContext db, List<int> ids, List<Zeile> zeilen, string? klasseFest, Dictionary<int, SollInfo> soll)
    {
        if (ids.Count == 0) return new List<FzZeileDto>();
        var personen = await db.Schueler.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Vorname, x.Nachname, x.Stufe })
            .ToListAsync();
        var klassen = klasseFest == null
            ? await KlassenbuchService.KlassenAsync(db, ids, "")
            : new Dictionary<int, string>();
        var jeSchueler = zeilen.GroupBy(z => z.SchuelerId).ToDictionary(g => g.Key, g => g.ToList());
        return personen
            .Select(p => new FzZeileDto(p.Id, p.Vorname, p.Nachname,
                klasseFest ?? (klassen.TryGetValue(p.Id, out var k) ? k : p.Stufe),
                Summe(jeSchueler.TryGetValue(p.Id, out var z) ? z : new List<Zeile>()),
                soll.TryGetValue(p.Id, out var si) ? si.Gesamt : 0))
            .OrderBy(x => x.Nachname, KlassenbuchService.NamenCmp)
            .ThenBy(x => x.Vorname, KlassenbuchService.NamenCmp)
            .ThenBy(x => x.Id)
            .ToList();
    }

    // =================================================================
    //  Schüler
    // =================================================================

    /// <summary>
    /// Fehlzeiten eines Schülers. <paramref name="u"/> = null: der Schüler selbst (nur lesen).
    /// Lehrkräfte ohne Zuständigkeit sehen nur Einträge aus ihren eigenen Stunden.
    /// Mit <paramref name="unr"/>/<paramref name="unterrichtLehrer"/>: nur dieser Unterricht.
    /// </summary>
    public static async Task<FzSchuelerDto?> SchuelerAsync(
        AppDbContext db, KbNutzer? u, int id, DateOnly von, DateOnly bis, int? unr, string? unterrichtLehrer)
    {
        var (s, klasse) = await SchuelerMitKlasseAsync(db, id);
        if (s == null) return null;

        bool leitung = u != null && IstLeitungFuer(u, klasse, s.Stufe);
        var q = ImZeitraum(db, von, bis).Where(a => a.SchuelerId == id);
        string? unterricht = null;
        if (unr is int un && !string.IsNullOrWhiteSpace(unterrichtLehrer))
        {
            string l = unterrichtLehrer.Trim().ToUpperInvariant();
            q = q.Where(a => a.Lektion!.UNr == un && a.Lektion.GrundLehrer == l);
            unterricht = u == null ? null : (await UnterrichtInfoAsync(db, u, un, l)).Titel;
        }
        var zeilen = await LadeAsync(q);

        bool nurEigene = u != null && !leitung;
        if (nurEigene)
        {
            var k = u!.Kuerzel;
            zeilen = k == null
                ? new List<Zeile>()
                : zeilen.Where(z => KlassenbuchService.Gleich(z.GrundLehrer, k) || KlassenbuchService.Gleich(z.Lehrer, k)).ToList();
        }
        if (unterricht == null && unr != null && zeilen.Count > 0)
            unterricht = $"{zeilen[0].Fach} · {zeilen[0].Klassen.Replace(",", ", ")}";

        var eintraege = zeilen
            .OrderByDescending(z => z.Datum).ThenByDescending(z => z.Stunde)
            .Select(z => EintragDto(z, u, leitung))
            .ToList();

        // Phase 4.1: Soll-Stunden (Fehlquote) – mit denselben Filtern wie die Fehlzeiten.
        bool nurUnterricht = unr != null && !string.IsNullOrWhiteSpace(unterrichtLehrer);
        var sollJe = await SollAsync(db, new[] { id }, von, bis,
            klasse: nurUnterricht ? null : (klasse ?? s.Stufe),
            unr: unr, grundLehrer: unterrichtLehrer,
            nurLehrer: nurEigene ? (u!.Kuerzel ?? "") : null);
        var soll = sollJe.TryGetValue(id, out var si) ? si : new SollInfo();

        var faecher = zeilen
            .GroupBy(z => (z.UNr, Lehrer: z.GrundLehrer.ToUpperInvariant()))
            .Select(g => new FzFachDto(
                g.OrderByDescending(z => z.Datum).First().Fach, g.Key.Lehrer, Summe(g),
                soll.JeUnterricht.TryGetValue((g.Key.UNr, g.Key.Lehrer), out var n) ? n : 0))
            .OrderBy(f => f.Fach, Cmp).ThenBy(f => f.Lehrer, Cmp)
            .ToList();

        var entschuldigungen = new List<FzEntschuldigungDto>();
        if (u == null || leitung)
        {
            var liste = await db.Entschuldigungen.AsNoTracking()
                .Where(e => e.SchuelerId == id && e.Bis >= von && e.Von <= bis)
                .OrderByDescending(e => e.Von).ThenByDescending(e => e.Id)
                .ToListAsync();
            entschuldigungen = liste.Select(e => new FzEntschuldigungDto(
                    e.Id, e.Von, e.Bis, e.Grund.ToString(), e.ErfasstVon, e.ErfasstAm,
                    zeilen.Count(z => z.EntschuldigungId == e.Id)))
                .ToList();
        }

        return new FzSchuelerDto(s.Id, s.Vorname, s.Nachname, klasse ?? s.Stufe, von, bis, nurEigene, unterricht,
            leitung, Summe(zeilen), faecher, eintraege, entschuldigungen, soll.Gesamt);
    }

    private static FzEintragDto EintragDto(Zeile z, KbNutzer? u, bool leitung)
    {
        bool fehlzeit = z.Status is AnwesenheitStatus.Fehlt or AnwesenheitStatus.Verspaetet;
        string? bewertung = z.Status switch
        {
            AnwesenheitStatus.Beurlaubt => FehlBewertung.Entschuldigt.ToString(),
            AnwesenheitStatus.Fehlt or AnwesenheitStatus.Verspaetet => z.Bewertung.ToString(),
            _ => null
        };

        bool darf = false;
        string? sperre = null;
        if (u != null && fehlzeit)
        {
            if (leitung) darf = true;
            else if (IstKurslehrer(u, z.GrundLehrer, z.Klassen))
            {
                darf = !z.Festgelegt;
                if (z.Festgelegt)
                    sperre = z.BewertetVon != null ? $"festgelegt von {z.BewertetVon}" : "festgelegt";
            }
        }

        return new FzEintragDto(
            z.Id, z.Datum, z.Stunde, z.Fach, z.Lehrer, z.GrundLehrer, z.GrundUNr, z.Klassen.Replace(",", ", "),
            z.Status.ToString(), z.Minuten, bewertung,
            fehlzeit && z.Bewertung == FehlBewertung.Entschuldigt ? z.Grund?.ToString() : null,
            fehlzeit ? z.EntschuldigungId : null,
            fehlzeit ? z.BewertetVon : null, fehlzeit ? z.BewertetAm : null,
            fehlzeit && z.Festgelegt, darf, sperre);
    }

    // =================================================================
    //  Entschuldigen und bewerten
    // =================================================================

    /// <summary>Zeitraum-Entschuldigung eintragen (Klassenleitung/Stufenkoordination/Admin).</summary>
    public static async Task<KbErgebnis> EntschuldigenAsync(AppDbContext db, KbNutzer u, int schuelerId, FzEntschuldigungNeuDto dto)
    {
        var (s, klasse) = await SchuelerMitKlasseAsync(db, schuelerId);
        if (s == null) return new KbErgebnis(404, "Schüler nicht gefunden.");
        if (!IstLeitungFuer(u, klasse, s.Stufe))
            return new KbErgebnis(403, "Zeiträume entschuldigen dürfen nur die Klassenleitung bzw. Stufenkoordination und die Verwaltung.");
        if (!Enum.TryParse(dto.Grund?.Trim(), true, out FehlGrund grund) || !Enum.IsDefined(grund))
            return new KbErgebnis(400, "Bitte einen Grund wählen.");
        if (dto.Bis < dto.Von) return new KbErgebnis(400, "„Bis“ liegt vor „von“.");
        if (dto.Bis.DayNumber - dto.Von.DayNumber > 366) return new KbErgebnis(400, "Der Zeitraum ist länger als ein Jahr.");

        var jetzt = DateTime.UtcNow;
        var e = new Entschuldigung
        {
            SchuelerId = s.Id, Von = dto.Von, Bis = dto.Bis, Grund = grund,
            ErfasstVon = u.Name, ErfasstAm = jetzt, IstPseudo = s.IstPseudo
        };
        db.Entschuldigungen.Add(e);
        await db.SaveChangesAsync();

        var betroffen = await db.Anwesenheiten
            .Where(a => a.SchuelerId == s.Id && a.Lektion!.Datum >= dto.Von && a.Lektion.Datum <= dto.Bis
                        && (a.Status == AnwesenheitStatus.Fehlt || a.Status == AnwesenheitStatus.Verspaetet))
            .ToListAsync();
        foreach (var a in betroffen)
        {
            a.Bewertung = FehlBewertung.Entschuldigt;
            a.Grund = grund;
            a.EntschuldigungId = e.Id;
            a.BewertetVon = u.Name;
            a.BewertetAm = jetzt;
            a.Festgelegt = true;
        }

        db.KlassenbuchProtokolle.Add(new KlassenbuchProtokoll
        {
            Zeit = jetzt, Akteur = u.Name, Art = "Entschuldigung", SchuelerId = s.Id, IstPseudo = s.IstPseudo,
            Neu = $"entschuldigt {dto.Von:dd.MM.yyyy}–{dto.Bis:dd.MM.yyyy} ({GrundText(grund)}), {betroffen.Count} Stunde(n)"
        });
        await db.SaveChangesAsync();

        return new KbErgebnis(200, Meldung: betroffen.Count == 1
            ? "Entschuldigung eingetragen – 1 Stunde entschuldigt."
            : $"Entschuldigung eingetragen – {betroffen.Count} Stunden entschuldigt.");
    }

    /// <summary>
    /// Entschuldigung löschen: Die abgedeckten Stunden werden wieder „offen" – außer ein anderer
    /// Zeitraum deckt sie ebenfalls ab.
    /// </summary>
    public static async Task<KbErgebnis> EntschuldigungLoeschenAsync(AppDbContext db, KbNutzer u, int id)
    {
        var e = await db.Entschuldigungen.FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return new KbErgebnis(404, "Entschuldigung nicht gefunden.");
        var (s, klasse) = await SchuelerMitKlasseAsync(db, e.SchuelerId);
        if (s == null || !IstLeitungFuer(u, klasse, s.Stufe))
            return new KbErgebnis(403, "Entschuldigungen löschen dürfen nur die Klassenleitung bzw. Stufenkoordination und die Verwaltung.");

        var jetzt = DateTime.UtcNow;
        var andere = await db.Entschuldigungen.AsNoTracking()
            .Where(x => x.SchuelerId == e.SchuelerId && x.Id != e.Id)
            .ToListAsync();
        var betroffen = await db.Anwesenheiten.Include(a => a.Lektion)
            .Where(a => a.EntschuldigungId == e.Id)
            .ToListAsync();
        foreach (var a in betroffen)
        {
            var ersatz = andere
                .Where(x => x.Von <= a.Lektion!.Datum && a.Lektion.Datum <= x.Bis)
                .OrderByDescending(x => x.ErfasstAm)
                .FirstOrDefault();
            a.Bewertung = ersatz != null ? FehlBewertung.Entschuldigt : FehlBewertung.Offen;
            a.Grund = ersatz?.Grund;
            a.EntschuldigungId = ersatz?.Id;
            a.BewertetVon = u.Name;
            a.BewertetAm = jetzt;
            a.Festgelegt = ersatz != null;
        }

        db.Entschuldigungen.Remove(e);
        db.KlassenbuchProtokolle.Add(new KlassenbuchProtokoll
        {
            Zeit = jetzt, Akteur = u.Name, Art = "Entschuldigung", SchuelerId = e.SchuelerId, IstPseudo = e.IstPseudo,
            Alt = $"entschuldigt {e.Von:dd.MM.yyyy}–{e.Bis:dd.MM.yyyy} ({GrundText(e.Grund)})",
            Neu = "gelöscht"
        });
        await db.SaveChangesAsync();
        return new KbErgebnis(200, Meldung: "Entschuldigung gelöscht.");
    }

    /// <summary>
    /// Eine Fehlstunde/Verspätung bewerten. KL/SK/Admin: alles (dann „festgelegt", außer bei
    /// „offen"). Kurslehrkraft der Oberstufe: eigene Kursstunden, solange nicht festgelegt.
    /// Eine Einzelbewertung löst die Stunde von einem Entschuldigungs-Zeitraum.
    /// </summary>
    public static async Task<KbErgebnis> BewertenAsync(AppDbContext db, KbNutzer u, int anwesenheitId, FzBewertungDto dto)
    {
        var a = await db.Anwesenheiten.Include(x => x.Lektion).FirstOrDefaultAsync(x => x.Id == anwesenheitId);
        if (a == null || a.Lektion == null) return new KbErgebnis(404, "Eintrag nicht gefunden.");
        if (a.Status is not (AnwesenheitStatus.Fehlt or AnwesenheitStatus.Verspaetet))
            return new KbErgebnis(400, "Nur Fehlstunden und Verspätungen lassen sich entschuldigen.");

        if (!Enum.TryParse(dto.Bewertung?.Trim(), true, out FehlBewertung bewertung) || !Enum.IsDefined(bewertung))
            return new KbErgebnis(400, $"Unbekannte Bewertung „{dto.Bewertung}“.");
        FehlGrund? grund = null;
        if (bewertung == FehlBewertung.Entschuldigt)
        {
            if (!Enum.TryParse(dto.Grund?.Trim(), true, out FehlGrund g) || !Enum.IsDefined(g))
                return new KbErgebnis(400, "Bitte einen Grund wählen.");
            grund = g;
        }

        var (s, klasse) = await SchuelerMitKlasseAsync(db, a.SchuelerId);
        if (s == null) return new KbErgebnis(404, "Schüler nicht gefunden.");
        bool leitung = IstLeitungFuer(u, klasse, s.Stufe);
        bool kurslehrer = !leitung && IstKurslehrer(u, a.Lektion.GrundLehrer, a.Lektion.Klassen);
        if (!leitung && !kurslehrer)
            return new KbErgebnis(403, "Diese Stunde darfst du nicht bewerten.");
        if (kurslehrer && a.Festgelegt)
            return new KbErgebnis(409, a.BewertetVon != null
                ? $"Die Stunde wurde von {a.BewertetVon} festgelegt und kann hier nicht mehr geändert werden."
                : "Die Stunde ist festgelegt und kann hier nicht mehr geändert werden.");

        string alt = BewertungText(a.Bewertung, a.Grund);
        string neu = BewertungText(bewertung, grund);
        bool festgelegt = leitung && bewertung != FehlBewertung.Offen;
        if (alt == neu && a.EntschuldigungId == null && a.Festgelegt == festgelegt)
            return new KbErgebnis(200);

        var jetzt = DateTime.UtcNow;
        a.Bewertung = bewertung;
        a.Grund = grund;
        a.EntschuldigungId = null;
        a.BewertetVon = u.Name;
        a.BewertetAm = jetzt;
        a.Festgelegt = festgelegt;

        db.KlassenbuchProtokolle.Add(new KlassenbuchProtokoll
        {
            Zeit = jetzt, Akteur = u.Name, Art = "Bewertung", LektionId = a.LektionId, SchuelerId = a.SchuelerId,
            Alt = alt, Neu = neu, IstPseudo = s.IstPseudo
        });
        await db.SaveChangesAsync();
        return new KbErgebnis(200, Meldung: $"Als {neu} gespeichert.");
    }

    // =================================================================
    //  Phase 4.1: Soll-Stunden (Fehlquote) und offene Fälle
    // =================================================================

    /// <summary>Soll-Stunden eines Schülers: gesamt und je Unterricht (gehaltene UNr, Lehrkraft laut Plan).</summary>
    internal sealed class SollInfo
    {
        public int Gesamt;
        public readonly Dictionary<(int UNr, string Lehrer), int> JeUnterricht = new();
    }

    private sealed record LektionKurz(int Id, int UNr, int GrundUNr, string Lehrer, string GrundLehrer, string Lerngruppen);

    private static IEnumerable<string> Teile(string? liste)
        => (liste ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Soll-Stunden (Entscheidung 42): erfasste Lektionen im Zeitraum, in denen der Schüler laut
    /// Teilnehmerliste dabei sein sollte – Klasse bzw. Kurs; bei Sek-I-Gruppen (Schülergruppe laut
    /// Grundplan, z. B. Religion, WP) nur die Mitglieder der Gruppe – und jede erfasste Lektion, in
    /// der er einen Eintrag hat (auch wenn er nicht mehr zur Liste gehört). Die Teilnahme wird wie im
    /// Stundenblatt aus den heutigen Zuordnungen bestimmt.
    /// Filter: <paramref name="klasse"/> (Lektionen dieser Klasse/Stufe), <paramref name="unr"/> +
    /// <paramref name="grundLehrer"/> (ein Unterricht), <paramref name="nurLehrer"/> (nur Stunden,
    /// die diese Lehrkraft gehalten hat bzw. laut Plan hätte halten sollen).
    /// </summary>
    internal static async Task<Dictionary<int, SollInfo>> SollAsync(
        AppDbContext db, IEnumerable<int> ids, DateOnly von, DateOnly bis,
        string? klasse = null, int? unr = null, string? grundLehrer = null, string? nurLehrer = null)
    {
        var ergebnis = ids.Distinct().ToDictionary(i => i, _ => new SollInfo());
        if (ergebnis.Count == 0) return ergebnis;
        var idListe = ergebnis.Keys.ToList();

        var lq = db.Lektionen.AsNoTracking()
            .Where(l => l.ErfasstAm != null && l.Datum >= von && l.Datum <= bis);
        var aq = db.Anwesenheiten.AsNoTracking()
            .Where(a => idListe.Contains(a.SchuelerId)
                        && a.Lektion!.ErfasstAm != null && a.Lektion.Datum >= von && a.Lektion.Datum <= bis);
        if (unr is int un && !string.IsNullOrWhiteSpace(grundLehrer))
        {
            string gl = grundLehrer.Trim().ToUpperInvariant();
            lq = lq.Where(l => l.UNr == un && l.GrundLehrer == gl);
            aq = aq.Where(a => a.Lektion!.UNr == un && a.Lektion.GrundLehrer == gl);
        }
        if (!string.IsNullOrWhiteSpace(klasse))
        {
            string kl = klasse.Trim().ToUpperInvariant();
            lq = lq.Where(l => l.Klassen.ToUpper().Contains(kl));
        }

        var lektionen = await lq
            .Select(l => new LektionKurz(l.Id, l.UNr, l.GrundUNr, l.Lehrer, l.GrundLehrer, l.Lerngruppen))
            .ToListAsync();
        var mitEintrag = await aq
            .Select(a => new
            {
                a.SchuelerId,
                L = new LektionKurz(a.LektionId, a.Lektion!.UNr, a.Lektion.GrundUNr, a.Lektion.Lehrer,
                    a.Lektion.GrundLehrer, a.Lektion.Lerngruppen)
            })
            .ToListAsync();

        if (nurLehrer != null)
        {
            bool Eigene(LektionKurz l) => KlassenbuchService.Gleich(l.GrundLehrer, nurLehrer) || KlassenbuchService.Gleich(l.Lehrer, nurLehrer);
            lektionen = lektionen.Where(Eigene).ToList();
            mitEintrag = mitEintrag.Where(e => Eigene(e.L)).ToList();
        }

        // Mitgliedschaften: Klasse/Kurs (Schlüssel = Bezeichnung) und Sek-I-Gruppen (Bezeichnung = Schülergruppe).
        var mitglied = await db.Zugehoerigkeiten.AsNoTracking()
            .Where(z => idListe.Contains(z.SchuelerId))
            .Select(z => new { z.SchuelerId, z.Lerngruppe!.Typ, z.Lerngruppe.Bezeichnung })
            .ToListAsync();
        var jeSchluessel = new Dictionary<string, List<int>>(Cmp);
        var jeGruppe = new Dictionary<string, List<int>>(Cmp);
        foreach (var m in mitglied)
        {
            var ziel = m.Typ == LerngruppeTyp.Gruppe ? jeGruppe : jeSchluessel;
            string key = (m.Bezeichnung ?? "").Trim();
            if (key.Length == 0) continue;
            if (!ziel.TryGetValue(key, out var l)) ziel[key] = l = new List<int>();
            l.Add(m.SchuelerId);
        }

        // Schülergruppen laut Grundplan je (UNr, Lehrkraft, Klasse) – Sek-I-Teilgruppen.
        var gruppenSlots = (await db.GrundplanSlots.AsNoTracking()
                .Where(x => x.Schuelergruppe != null && x.Schuelergruppe != "")
                .Select(x => new { x.UNr, x.Lehrer, x.LerngruppeTyp, x.LerngruppeKey, x.Schuelergruppe })
                .ToListAsync())
            .Where(x => string.IsNullOrWhiteSpace(x.LerngruppeTyp)
                        || string.Equals(x.LerngruppeTyp.Trim(), "Klasse", StringComparison.OrdinalIgnoreCase));
        var gruppenJe = new Dictionary<string, HashSet<string>>(Cmp);
        foreach (var x in gruppenSlots)
        {
            string key = $"{x.UNr}|{(x.Lehrer ?? "").Trim()}|{(x.LerngruppeKey ?? "").Trim()}";
            if (!gruppenJe.TryGetValue(key, out var set)) gruppenJe[key] = set = new HashSet<string>(Cmp);
            set.Add(x.Schuelergruppe!.Trim());
        }
        HashSet<string> GruppenVon(LektionKurz l, string key)
        {
            var erg = new HashSet<string>(Cmp);
            foreach (var k in new[] { $"{l.UNr}|{l.GrundLehrer.Trim()}|{key}", $"{l.GrundUNr}|{l.GrundLehrer.Trim()}|{key}",
                                      $"{l.UNr}|{l.Lehrer.Trim()}|{key}" })
                if (gruppenJe.TryGetValue(k, out var set)) erg.UnionWith(set);
            return erg;
        }

        var teilJe = new Dictionary<int, (LektionKurz L, HashSet<int> Teil)>();
        foreach (var l in lektionen)
        {
            var teil = new HashSet<int>();
            foreach (var key in Teile(l.Lerngruppen))
            {
                var gruppen = GruppenVon(l, key);
                if (gruppen.Count > 0)
                {
                    foreach (var g in gruppen)
                        if (jeGruppe.TryGetValue(g, out var mg)) teil.UnionWith(mg);
                }
                else if (jeSchluessel.TryGetValue(key, out var mk))
                {
                    teil.UnionWith(mk);
                }
            }
            teilJe[l.Id] = (l, teil);
        }
        foreach (var e in mitEintrag)
        {
            if (!teilJe.TryGetValue(e.L.Id, out var x))
            {
                x = (e.L, new HashSet<int>());
                teilJe[e.L.Id] = x;
            }
            x.Teil.Add(e.SchuelerId);
        }

        foreach (var (l, teil) in teilJe.Values)
        {
            var u = (l.UNr, l.GrundLehrer.Trim().ToUpperInvariant());
            foreach (var id in teil)
            {
                if (!ergebnis.TryGetValue(id, out var info)) continue;
                info.Gesamt++;
                info.JeUnterricht[u] = (info.JeUnterricht.TryGetValue(u, out var n) ? n : 0) + 1;
            }
        }
        return ergebnis;
    }

    /// <summary>
    /// Offene Fälle (Entscheidung 43): Schüler einer Klasse/Stufe mit offenen Fehlstunden oder
    /// Verspätungen – über das ganze Schuljahr bis heute, die ältesten zuerst. Keine Frist,
    /// nichts wird automatisch unentschuldigt.
    /// </summary>
    public static async Task<FzOffenDto> OffeneFaelleAsync(AppDbContext db, string klasse)
    {
        var heute = KlassenbuchService.Heute();
        var kal = await WochenplanService.KalenderAsync(db, heute);
        var von = kal?.Start ?? heute.AddDays(-365);
        string k = klasse.Trim();
        var (ids, istStufe) = await KlassenSchuelerAsync(db, k);
        var offen = ids.Count == 0
            ? new List<Zeile>()
            : await LadeAsync(ImZeitraum(db, von, heute)
                .Where(a => ids.Contains(a.SchuelerId)
                            && (a.Status == AnwesenheitStatus.Fehlt || a.Status == AnwesenheitStatus.Verspaetet)
                            && a.Bewertung == FehlBewertung.Offen));

        var jeSchueler = offen.GroupBy(z => z.SchuelerId).ToDictionary(g => g.Key, g => g.ToList());
        var betroffen = jeSchueler.Keys.ToList();
        var personen = betroffen.Count == 0
            ? new List<(int Id, string Vorname, string Nachname, string Stufe)>()
            : (await db.Schueler.AsNoTracking()
                    .Where(x => betroffen.Contains(x.Id))
                    .Select(x => new { x.Id, x.Vorname, x.Nachname, x.Stufe })
                    .ToListAsync())
                .Select(x => (x.Id, x.Vorname, x.Nachname, x.Stufe))
                .ToList();
        var klassen = istStufe ? await KlassenbuchService.KlassenAsync(db, betroffen, kal?.Schuljahr ?? "")
            : new Dictionary<int, string>();

        var liste = personen
            .Select(p =>
            {
                var z = jeSchueler[p.Id];
                var aelteste = z.Min(x => x.Datum);
                return new FzOffenZeileDto(p.Id, p.Vorname, p.Nachname,
                    istStufe ? (klassen.TryGetValue(p.Id, out var kl) ? kl : p.Stufe) : k.ToUpperInvariant(),
                    z.Count(x => x.Status == AnwesenheitStatus.Fehlt),
                    z.Count(x => x.Status == AnwesenheitStatus.Verspaetet),
                    aelteste, z.Max(x => x.Datum), heute.DayNumber - aelteste.DayNumber);
            })
            .OrderBy(x => x.Aelteste)
            .ThenBy(x => x.Nachname, KlassenbuchService.NamenCmp)
            .ThenBy(x => x.Vorname, KlassenbuchService.NamenCmp)
            .ThenBy(x => x.Id)
            .ToList();
        return new FzOffenDto(GruppenTitel(k, istStufe), von, heute, liste);
    }

    // =================================================================
    //  Nicht erfasste Stunden
    // =================================================================

    /// <param name="ansicht">ich (eigene gehaltene Stunden) | zustaendig (Klasse/Stufe) | alle (Admin).</param>
    /// <param name="art">
    /// Phase 3.3 (Entscheidung 20): alle | anwesenheit (ohne Anwesenheit) | thema (ohne Thema; ab
    /// <paramref name="themaAb"/>, Betreuung ausgenommen).
    /// </param>
    public static async Task<FzNichtErfasstDto> NichtErfasstAsync(
        AppDbContext db, KbNutzer u, string ansicht, string? lehrer, string? klasse,
        DateOnly erfassungAb, DateOnly themaAb, string art, DateOnly? von, DateOnly? bis)
    {
        bool mitAnwesenheit = art is "alle" or "anwesenheit";
        bool mitThema = art is "alle" or "thema";
        var ab = mitAnwesenheit && mitThema ? (erfassungAb < themaAb ? erfassungAb : themaAb)
            : mitThema ? themaAb : erfassungAb;

        var gestern = KlassenbuchService.Heute().AddDays(-1);   // offen ab dem Folgetag
        var start = von is DateOnly v && v > ab ? v : ab;
        var ende = bis is DateOnly b && b < gestern ? b : gestern;
        if (ende < start)
            return new FzNichtErfasstDto(start, ende, 0, Array.Empty<FzNeLehrerDto>(), Array.Empty<FzNeStundeDto>(), false,
                0, 0, erfassungAb, themaAb);

        var tage = await WochenplanService.TageAsync(db, start, ende);
        var lektionen = (await db.Lektionen.AsNoTracking()
                .Where(l => l.Datum >= start && l.Datum <= ende)
                .Select(l => new { l.Datum, l.Stunde, l.GrundUNr, l.GrundLehrer, Erfasst = l.ErfasstAm != null, HatThema = l.Thema != null })
                .ToListAsync())
            .GroupBy(l => WochenplanService.LektionSchluessel(l.Datum, l.Stunde, l.GrundUNr, l.GrundLehrer), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Erfasst: g.Any(x => x.Erfasst), HatThema: g.Any(x => x.HatThema)), StringComparer.Ordinal);
        var fertig = await WochenplanService.FertigeGruppenAsync(db);
        var klasseFilter = string.IsNullOrWhiteSpace(klasse) ? null : new List<string> { klasse.Trim() };

        var treffer = new List<FzNeStundeDto>();
        foreach (var tag in tage)
        {
            foreach (var st in tag.Stunden)
            {
                if (!WochenplanService.KlassenbuchMoeglich(st.Art, st.Status, st.Quellen)) continue;

                bool passt = ansicht switch
                {
                    "ich" => u.Kuerzel != null && KlassenbuchService.Gleich(st.Lehrer, u.Kuerzel),
                    "zustaendig" => u.IstKlassenleitungOderKoordination
                                    && KlassenbuchService.ZustaendigFuer(u.Zustaendig, st.Klassen),
                    "alle" => u.IstAdmin
                              && (string.IsNullOrWhiteSpace(lehrer) || KlassenbuchService.Gleich(st.Lehrer, lehrer))
                              && (klasseFilter == null || KlassenbuchService.ZustaendigFuer(klasseFilter, st.Klassen)),
                    _ => false
                };
                if (!passt) continue;

                lektionen.TryGetValue(WochenplanService.LektionSchluessel(tag.Datum, st.Stunde, st.GrundUNr, st.GrundLehrer), out var l);
                bool anwesenheitFehlt = mitAnwesenheit && tag.Datum >= erfassungAb && !l.Erfasst;
                // Betreuung/Aufsicht braucht kein Thema (Entscheidung 20).
                bool themaFehlt = mitThema && tag.Datum >= themaAb && st.Status != "Betreuung" && !l.HatThema;
                if (!anwesenheitFehlt && !themaFehlt) continue;

                bool gruppeFehlt = st.Quellen.Any(q => q.Typ == "Klasse" && q.Schuelergruppe != null
                    && !fertig.Contains(WochenplanService.GruppenSchluessel(tag.Schuljahr, q.Schuelergruppe)));
                treffer.Add(new FzNeStundeDto(tag.Datum, st.Stunde, st.GrundUNr, st.GrundLehrer, st.Fach,
                    st.Lehrer, st.OriginalLehrer, st.Klassen, st.Status, gruppeFehlt, anwesenheitFehlt, themaFehlt));
            }
        }

        var proLehrer = treffer
            .GroupBy(t => t.Lehrer.Trim().ToUpperInvariant())
            .Select(g => new FzNeLehrerDto(g.Key, g.Count()))
            .OrderByDescending(x => x.Anzahl).ThenBy(x => x.Lehrer, Cmp)
            .ToList();
        var stunden = treffer
            .OrderByDescending(t => t.Datum).ThenBy(t => t.Stunde).ThenBy(t => t.Lehrer, Cmp)
            .Take(MaxNichtErfasst)
            .ToList();
        return new FzNichtErfasstDto(start, ende, treffer.Count, proLehrer, stunden, treffer.Count > stunden.Count,
            treffer.Count(t => t.AnwesenheitFehlt), treffer.Count(t => t.ThemaFehlt), erfassungAb, themaAb);
    }
}
