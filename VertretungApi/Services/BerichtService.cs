using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Klassenbuch und Kursheft als PDF (Phase 4.2, Entscheidungen 44–47 und 50,
//  Nutzer 04.11.2026). Dieser Dienst stellt die Daten zusammen; das PDF baut
//  Services/KlassenbuchPdf.cs (QuestPDF).
//
//  Inhalt (Entscheidung 44): chronologisch, je Unterrichtstag ein Block mit den
//  Stunden der Klasse/Stufe bzw. des Unterrichts laut effektivem Plan – auch
//  Entfall/Verlegung und nicht erfasste Stunden (sichtbare Lücke). Je Stunde:
//  Fach, Lehrkraft (Vertretung „L9 für L2"), Status, Thema, Hausaufgabe mit
//  Fälligkeit, Fehlende/Verspätete/Beurlaubte/Schulveranstaltung namentlich,
//  „erfasst von/am", „geändert von/am", Klasseneinträge. Anhang: Fehlzeiten je
//  Schüler im Zeitraum (wie die Konferenzübersicht, mit Fehlquote).
//  Schülereinträge (Entscheidung 45) nur auf Wunsch und nur für Schüler, für
//  die die druckende Person Klassenleitung/Stufenkoordination bzw. Admin ist.
//
//  Wer druckt was (Entscheidung 46) – niemand mehr, als er am Bildschirm lesen
//  darf: Klassenbuch einer Klasse/Stufe: zuständige KL/SK, Admin. Kursheft eines
//  Unterrichts: Lehrkraft laut Plan, zuständige KL/SK, Admin. Vertretungs-
//  lehrkräfte und Schüler drucken nicht. „Berichte:NurAdmin" (Standard false)
//  beschränkt Downloads auf Admin (Entscheidung 50, vorbereitet für Phase 5).
//
//  Zeitraum: wie in der PWA gewählt, endet spätestens heute (künftige Stunden
//  haben noch kein Klassenbuch).
// =====================================================================

/// <summary>Kopf des Berichts (auf jeder Seite).</summary>
public sealed record BerichtKopf(
    string Art,                        // "Klassenbuch" | "Kursheft"
    string Titel,                      // z. B. "Klasse 05A" bzw. "M · Q1"
    string? Zusatz,                    // z. B. "Lehrkraft L1"
    string? Schuljahr,
    DateOnly Von,
    DateOnly Bis,
    string ErstelltVon,
    DateTime ErstelltAm,               // Ortszeit des Servers
    bool MitSchuelerEintraegen);

/// <summary>Eine Stunde im Bericht.</summary>
public sealed record BerichtStunde(
    int Stunde,
    string Fach,
    string Lehrer,                     // "L1" bzw. "L9 für L2"
    string? Klassen,                   // nur wenn nötig (mehrere Klassen bzw. Klassenbuch einer Stufe)
    string? Status,                    // "Vertretung", "entfällt", "verlegt" …; null = normal
    bool MitKlassenbuch,               // false: Entfall/Verlegung/frei – keine Erfassung
    bool Erfasst,
    string? Thema,
    string? Hausaufgabe,
    DateOnly? Faellig,
    IReadOnlyList<string> Fehlt,
    IReadOnlyList<string> Verspaetet,  // "Albers, Anna (10 Min.)"
    IReadOnlyList<string> Beurlaubt,
    IReadOnlyList<string> Schulveranstaltung,
    string? ErfasstText,               // "erfasst von L1, 02.11. 08:15"
    string? GeaendertText,             // "geändert von admin, 03.11. 10:02"
    IReadOnlyList<string> Klasseneintraege);

public sealed record BerichtTag(DateOnly Datum, string? Hinweis, IReadOnlyList<BerichtStunde> Stunden);

public sealed record BerichtSchuelerEintrag(
    DateOnly Datum, int? Stunde, string? Fach, string Schueler, string Kategorie, string? Text, string Von);

public sealed record Bericht(
    BerichtKopf Kopf,
    IReadOnlyList<BerichtTag> Tage,
    int StundenMitKlassenbuch,
    int NichtErfasst,
    FzListeDto? Fehlzeiten,
    IReadOnlyList<BerichtSchuelerEintrag>? SchuelerEintraege);

/// <summary>Unterricht einer Klasse/Stufe (Auswahl „Kursheft" im Dialog).</summary>
public sealed record BerichtUnterrichtDto(int UNr, string Lehrer, string Fach, IReadOnlyList<string> Klassen);

public static class BerichtService
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] Wt = { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };

    // =================================================================
    //  Rechte
    // =================================================================

    /// <summary>Klassenbuch einer Klasse bzw. Stufe: zuständige KL/SK, Admin.</summary>
    public static bool DarfKlassenbuch(KbNutzer u, string klasse)
        => u.IstAdmin || (u.IstKlassenleitungOderKoordination
                          && KlassenbuchService.ZustaendigFuer(u.Zustaendig, new[] { klasse.Trim() }));

    /// <summary>Kursheft: Lehrkraft laut Plan, KL/SK einer Klasse/Stufe des Unterrichts, Admin.</summary>
    public static bool DarfKursheft(KbNutzer u, string lehrer, IEnumerable<string> klassen)
        => u.IstAdmin
           || (u.Kuerzel != null && KlassenbuchService.Gleich(u.Kuerzel, lehrer))
           || (u.IstKlassenleitungOderKoordination && KlassenbuchService.ZustaendigFuer(u.Zustaendig, klassen));

    // =================================================================
    //  Auswahl
    // =================================================================

    /// <summary>Unterrichte einer Klasse bzw. Stufe im heute gültigen Grundplan (für „Kursheft").</summary>
    public static async Task<List<BerichtUnterrichtDto>> UnterrichteAsync(AppDbContext db, string klasse)
    {
        var ver = await KlassenbuchService.AktuelleVersionAsync(db);
        if (ver == null) return new List<BerichtUnterrichtDto>();
        var filter = new List<string> { klasse.Trim() };
        var slots = (await db.GrundplanSlots.AsNoTracking().Where(s => s.VersionId == ver.Id).ToListAsync())
            .Where(s => string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)
                        && s.LerngruppeTyp is "Klasse" or "Kurs"
                        && KlassenbuchService.ZustaendigFuer(filter, new[] { s.Klasse }))
            .ToList();
        return slots
            .GroupBy(s => (s.UNr, Lehrer: s.Lehrer.Trim().ToUpperInvariant()))
            .Select(g => new BerichtUnterrichtDto(
                g.Key.UNr, g.Key.Lehrer,
                g.GroupBy(s => s.Fach.Trim(), Cmp).OrderByDescending(f => f.Count()).First().Key,
                g.Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0).Distinct(Cmp)
                    .OrderBy(k => k, Cmp).ToList()))
            .OrderBy(x => x.Fach, Cmp).ThenBy(x => x.Lehrer, Cmp)
            .ToList();
    }

    /// <summary>Titel ("M · Q1") und Klassen eines Unterrichts laut heute gültigem Grundplan; null = unbekannt.</summary>
    public static async Task<(string Fach, List<string> Klassen)?> UnterrichtInfoAsync(AppDbContext db, int unr, string lehrer)
    {
        var ver = await KlassenbuchService.AktuelleVersionAsync(db);
        var slots = ver == null
            ? new List<GrundplanSlot>()
            : (await db.GrundplanSlots.AsNoTracking().Where(s => s.VersionId == ver.Id && s.UNr == unr).ToListAsync())
                .Where(s => KlassenbuchService.Gleich(s.Lehrer, lehrer)
                            && string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (slots.Count > 0)
            return (slots.GroupBy(s => s.Fach.Trim(), Cmp).OrderByDescending(f => f.Count()).First().Key,
                slots.Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0).Distinct(Cmp)
                    .OrderBy(k => k, Cmp).ToList());

        // Nicht mehr im Plan: aus der letzten erfassten Stunde.
        string l = lehrer.Trim().ToUpperInvariant();
        var lk = await db.Lektionen.AsNoTracking()
            .Where(x => x.UNr == unr && x.GrundLehrer == l)
            .OrderByDescending(x => x.Datum)
            .Select(x => new { x.Fach, x.Klassen })
            .FirstOrDefaultAsync();
        if (lk == null) return null;
        return (lk.Fach, lk.Klassen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    // =================================================================
    //  Bericht zusammenstellen
    // =================================================================

    /// <summary>
    /// Klassenbuch einer Klasse (Sek I) bzw. Stufe (Sek II). Rechte prüft der Aufrufer.
    /// <paramref name="tage"/>: schon berechnete Plantage (Phase 4.3, Jahresarchiv) – sonst werden sie geladen.
    /// </summary>
    public static async Task<Bericht> KlassenbuchAsync(
        AppDbContext db, KbNutzer u, string klasse, DateOnly von, DateOnly bis, bool schuelerEintraege,
        IReadOnlyList<TagKontext>? tage = null)
    {
        string k = klasse.Trim().ToUpperInvariant();
        var filter = new List<string> { k };
        var bisEff = BisHoechstensHeute(bis);
        var fehlzeiten = await FehlzeitenService.KlasseAsync(db, k, von, bisEff);

        var kopf = new BerichtKopf("Klassenbuch", fehlzeiten.Titel, null, await SchuljahrAsync(db, von),
            von, bisEff, u.Name, DateTime.Now, false);
        var lektionen = await db.Lektionen.AsNoTracking().Include(x => x.Anwesenheiten)
            .Where(x => x.Datum >= von && x.Datum <= bisEff && x.Klassen.ToUpper().Contains(k))
            .ToListAsync();

        var bericht = await BaueAsync(db, kopf, von, bisEff, lektionen,
            st => string.Equals(st.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)
                  && KlassenbuchService.ZustaendigFuer(filter, st.Klassen),
            klassenZeigen: st => st.Klassen.Count > 1,
            freiFuer: k,
            vorgegeben: tage);

        if (schuelerEintraege && (u.IstAdmin || u.IstKlassenleitungOderKoordination))
        {
            var ids = fehlzeiten.Schueler.Where(z => FehlzeitenService.IstLeitungFuer(u, z.Klasse, z.Klasse))
                .Select(z => z.Id).ToList();
            var eintraege = await SchuelerEintraegeAsync(db, ids, von, bisEff, null, null);
            bericht = bericht with { Kopf = bericht.Kopf with { MitSchuelerEintraegen = true }, SchuelerEintraege = eintraege };
        }
        return bericht with { Fehlzeiten = fehlzeiten };
    }

    /// <summary>
    /// Kursheft eines Unterrichts (UNr + Lehrkraft laut Plan). Rechte prüft der Aufrufer.
    /// <paramref name="tage"/>: schon berechnete Plantage (Phase 4.3, Jahresarchiv) – sonst werden sie geladen.
    /// </summary>
    public static async Task<Bericht> KursheftAsync(
        AppDbContext db, KbNutzer u, int unr, string lehrer, string fach, IReadOnlyList<string> klassen,
        DateOnly von, DateOnly bis, bool schuelerEintraege, IReadOnlyList<TagKontext>? tage = null)
    {
        string l = lehrer.Trim().ToUpperInvariant();
        var bisEff = BisHoechstensHeute(bis);
        string titel = klassen.Count > 0 ? $"{fach} · {string.Join(", ", klassen)}" : fach;
        var kopf = new BerichtKopf("Kursheft", titel, $"Lehrkraft laut Plan: {l}", await SchuljahrAsync(db, von),
            von, bisEff, u.Name, DateTime.Now, false);
        var lektionen = await db.Lektionen.AsNoTracking().Include(x => x.Anwesenheiten)
            .Where(x => x.Datum >= von && x.Datum <= bisEff && x.GrundLehrer == l && (x.GrundUNr == unr || x.UNr == unr))
            .ToListAsync();

        var bericht = await BaueAsync(db, kopf, von, bisEff, lektionen,
            st => string.Equals(st.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)
                  && KlassenbuchService.Gleich(st.GrundLehrer, l) && (st.GrundUNr == unr || st.UNr == unr),
            klassenZeigen: _ => false,
            freiFuer: null,
            vorgegeben: tage);

        var fehlzeiten = await FehlzeitenService.UnterrichtAsync(db, u, unr, l, von, bisEff);
        if (schuelerEintraege && fehlzeiten != null && (u.IstAdmin || u.IstKlassenleitungOderKoordination))
        {
            var ids = fehlzeiten.Schueler.Where(z => FehlzeitenService.IstLeitungFuer(u, z.Klasse, z.Klasse))
                .Select(z => z.Id).ToList();
            var eintraege = await SchuelerEintraegeAsync(db, ids, von, bisEff, unr, l);
            bericht = bericht with { Kopf = bericht.Kopf with { MitSchuelerEintraegen = true }, SchuelerEintraege = eintraege };
        }
        return bericht with { Fehlzeiten = fehlzeiten };
    }

    private static DateOnly BisHoechstensHeute(DateOnly bis)
    {
        var heute = KlassenbuchService.Heute();
        return bis > heute ? heute : bis;
    }

    private static async Task<string?> SchuljahrAsync(AppDbContext db, DateOnly d)
        => (await WochenplanService.KalenderAsync(db, d))?.Schuljahr
           ?? (await WochenplanService.KalenderAsync(db, KlassenbuchService.Heute()))?.Schuljahr;

    /// <summary>Tage und Stunden aus dem effektiven Plan, ergänzt um die erfassten Lektionen.</summary>
    private static async Task<Bericht> BaueAsync(
        AppDbContext db, BerichtKopf kopf, DateOnly von, DateOnly bis, List<Lektion> lektionen,
        Func<PlanStunde, bool> passt, Func<PlanStunde, bool> klassenZeigen, string? freiFuer,
        IReadOnlyList<TagKontext>? vorgegeben = null)
    {
        IReadOnlyList<TagKontext> tage = bis < von
            ? new List<TagKontext>()
            : vorgegeben != null
                ? vorgegeben.Where(t => t.Datum >= von && t.Datum <= bis).ToList()
                : await WochenplanService.TageAsync(db, von, bis);
        var jeSchluessel = lektionen
            .GroupBy(x => WochenplanService.LektionSchluessel(x.Datum, x.Stunde, x.GrundUNr, x.GrundLehrer), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ErfasstAm != null).ThenByDescending(x => x.Id).First(),
                StringComparer.Ordinal);

        // Namen aller Schüler mit Abweichungen
        var ids = lektionen.SelectMany(x => x.Anwesenheiten).Select(a => a.SchuelerId).Distinct().ToList();
        var namen = ids.Count == 0
            ? new Dictionary<int, string>()
            : (await db.Schueler.AsNoTracking().Where(s => ids.Contains(s.Id))
                    .Select(s => new { s.Id, s.Vorname, s.Nachname }).ToListAsync())
                .ToDictionary(s => s.Id, s => $"{s.Nachname}, {s.Vorname}");

        // Klasseneinträge der Stunden im Zeitraum
        var klassenEintraege = (await db.KlassenbuchEintraege.AsNoTracking()
                .Where(e => e.Art == KbEintragArt.Klasse && e.Stunde != null && e.Datum >= von && e.Datum <= bis)
                .ToListAsync())
            .GroupBy(e => WochenplanService.LektionSchluessel(e.Datum, e.Stunde!.Value, e.GrundUNr ?? 0, e.GrundLehrer ?? ""),
                StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.VerfasstAm).ToList(), StringComparer.Ordinal);

        var ergebnis = new List<BerichtTag>();
        int mitKb = 0, offen = 0;
        foreach (var tag in tage)
        {
            string? hinweis = null;
            if (freiFuer != null)
            {
                var frei = tag.FreieKlassen.FirstOrDefault(f => KlassenbuchService.ZustaendigFuer(new[] { freiFuer }, new[] { f.Klasse }));
                if (frei != null) hinweis = $"{frei.Klasse}: {frei.Text}";
            }

            var stunden = new List<BerichtStunde>();
            foreach (var st in tag.Stunden.Where(passt).OrderBy(x => x.Stunde).ThenBy(x => x.Fach, Cmp))
            {
                bool kb = WochenplanService.KlassenbuchMoeglich(st.Art, st.Status, st.Quellen);
                jeSchluessel.TryGetValue(WochenplanService.LektionSchluessel(tag.Datum, st.Stunde, st.GrundUNr, st.GrundLehrer), out var lk);
                klassenEintraege.TryGetValue(WochenplanService.LektionSchluessel(tag.Datum, st.Stunde, st.GrundUNr, st.GrundLehrer), out var ke);
                bool erfasst = lk?.ErfasstAm != null;
                if (kb)
                {
                    mitKb++;
                    if (!erfasst) offen++;
                }

                var anw = lk?.Anwesenheiten ?? new List<Anwesenheit>();
                List<string> Namen(AnwesenheitStatus s) => anw.Where(a => a.Status == s)
                    .Select(a => (namen.TryGetValue(a.SchuelerId, out var n) ? n : $"Schüler {a.SchuelerId}")
                                 + (s == AnwesenheitStatus.Verspaetet && a.Minuten is int m ? $" ({m} Min.)" : ""))
                    .OrderBy(n => n, KlassenbuchService.NamenCmp)
                    .ToList();

                string lehrer = st.OriginalLehrer != null && !KlassenbuchService.Gleich(st.OriginalLehrer, st.Lehrer)
                    ? $"{st.Lehrer} für {st.OriginalLehrer}"
                    : st.Lehrer;

                stunden.Add(new BerichtStunde(
                    st.Stunde, st.Fach, lehrer,
                    klassenZeigen(st) ? string.Join(", ", st.Klassen) : null,
                    StatusText(st.Status, st.Info),
                    kb, erfasst,
                    lk?.Thema, lk?.Hausaufgabe, lk?.Hausaufgabe != null ? lk.HausaufgabeFaellig : null,
                    Namen(AnwesenheitStatus.Fehlt), Namen(AnwesenheitStatus.Verspaetet),
                    Namen(AnwesenheitStatus.Beurlaubt), Namen(AnwesenheitStatus.Schulveranstaltung),
                    erfasst ? $"erfasst von {lk!.ErfasstVon}, {Zeit(lk.ErfasstAm!.Value)}" : null,
                    erfasst && lk!.GeaendertAm != null && (lk.GeaendertAm != lk.ErfasstAm || lk.GeaendertVon != lk.ErfasstVon)
                        ? $"geändert von {lk.GeaendertVon}, {Zeit(lk.GeaendertAm.Value)}" : null,
                    (ke ?? new List<KlassenbuchEintrag>())
                        .Select(e => $"{KlassenbuchEintragService.KategorieText(e.Kategorie)}"
                                     + (string.IsNullOrWhiteSpace(e.Text) ? "" : $": {e.Text}") + $" ({e.VerfasstVon})")
                        .ToList()));
            }
            if (stunden.Count > 0 || hinweis != null)
                ergebnis.Add(new BerichtTag(tag.Datum, hinweis, stunden));
        }
        return new Bericht(kopf, ergebnis, mitKb, offen, null, null);
    }

    /// <summary>Schülereinträge (Entscheidung 45) – im Kursheft nur die aus diesem Unterricht.</summary>
    private static async Task<List<BerichtSchuelerEintrag>> SchuelerEintraegeAsync(
        AppDbContext db, List<int> ids, DateOnly von, DateOnly bis, int? unr, string? grundLehrer)
    {
        if (ids.Count == 0) return new List<BerichtSchuelerEintrag>();
        var q = db.KlassenbuchEintraege.AsNoTracking()
            .Where(e => e.Art == KbEintragArt.Schueler && e.SchuelerId != null && ids.Contains(e.SchuelerId.Value)
                        && e.Datum >= von && e.Datum <= bis);
        if (unr is int un && grundLehrer != null)
            q = q.Where(e => e.GrundLehrer == grundLehrer && (e.GrundUNr == un || e.UNr == un));
        var liste = await q.Select(e => new
            {
                e.Datum, e.Stunde, e.Fach, e.Kategorie, e.Text, e.VerfasstVon, e.VerfasstAm,
                Vorname = e.Schueler!.Vorname, Nachname = e.Schueler.Nachname
            })
            .ToListAsync();
        return liste
            .OrderBy(e => e.Datum).ThenBy(e => e.Stunde ?? 0).ThenBy(e => e.VerfasstAm)
            .Select(e => new BerichtSchuelerEintrag(e.Datum, e.Stunde, string.IsNullOrWhiteSpace(e.Fach) ? null : e.Fach,
                $"{e.Nachname}, {e.Vorname}", KlassenbuchEintragService.KategorieText(e.Kategorie), e.Text, e.VerfasstVon))
            .ToList();
    }

    /// <summary>Download-Protokoll nach der Frist des Kontenprotokolls aufräumen (Entscheidung 50).</summary>
    public static int ProtokollAufraeumen(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = DateTime.UtcNow.AddDays(-tage);
        return db.DownloadProtokolle.Where(p => p.Zeit < grenze).ExecuteDelete();
    }

    // =================================================================
    //  Texte
    // =================================================================

    private static string? StatusText(string status, string? info) => status switch
    {
        "Normal" => null,
        "Vertreten" => "Vertretung",
        "Entfall" => "entfällt",
        "VerlegtWeg" => string.IsNullOrWhiteSpace(info) ? "verlegt" : $"verlegt ({info})",
        "VerlegtHin" => string.IsNullOrWhiteSpace(info) ? "verlegt hierher" : $"verlegt hierher ({info})",
        "Betreuung" => "Betreuung",
        "EVA" => "EVA",
        "Zusammenlegung" => "Zusammenlegung",
        "Klausur" => "Klausur",
        "Raumvertretung" => null,
        "Frei" => string.IsNullOrWhiteSpace(info) ? "frei" : $"frei ({info})",
        _ => status
    };

    /// <summary>„02.11. 08:15" in Ortszeit des Servers (gespeichert wird UTC).</summary>
    private static string Zeit(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM. HH:mm");

    /// <summary>„Mo 02.11.2026"</summary>
    public static string TagText(DateOnly d) => $"{Wt[(int)d.DayOfWeek]} {d:dd.MM.yyyy}";

    /// <summary>Dateiname ohne Sonderzeichen, z. B. "Klassenbuch_05A_2026-08-26_2026-11-05.pdf".</summary>
    public static string Dateiname(BerichtKopf k)
    {
        var teile = new List<string> { k.Art };
        teile.AddRange(k.Titel.Replace("Klasse ", "").Replace("Stufe ", "")
            .Split(new[] { ' ', '·', ',' }, StringSplitOptions.RemoveEmptyEntries));
        return SichererName(string.Join("_", teile) + $"_{k.Von:yyyy-MM-dd}_{k.Bis:yyyy-MM-dd}", ".pdf");
    }

    /// <summary>Nur ASCII-Buchstaben, Ziffern, „_" und „-" (Phase 4.3 auch für CSV und Zip).</summary>
    public static string SichererName(string roh, string endung)
    {
        var sauber = new string(roh.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        while (sauber.Contains("__")) sauber = sauber.Replace("__", "_");
        sauber = sauber.Trim('_');
        return (sauber.Length == 0 ? "Datei" : sauber) + endung;
    }
}
