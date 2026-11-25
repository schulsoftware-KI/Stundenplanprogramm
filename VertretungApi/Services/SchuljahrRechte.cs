using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P5a – 5.3b Rechte am Schuljahr (Entscheidungen 215, 216, 219, 242).
//
//  Abgeschlossenes Schuljahr (Tabelle „Schuljahre“, Status „abgeschlossen“):
//    - nur lesbar – für alle, auch den Admin (Wieder öffnen: Jahresablauf, mit Begründung). Schreibende Aufrufe auf Daten
//      dieses Jahres → 409 mit verständlichem Text; Vertretungen, Grundpläne und SchILD-Importe für das Jahr lehnt die API ab;
//    - nur für den Admin sichtbar (lesen, PDF, Archiv). Alle anderen → 403 bzw. in den Plänen „Schuljahr … abgeschlossen“.
//  Ausgenommen (bewusst): Löschfristen im Aufräumdienst und „Vorhandene Gründe entfernen…“ (Datenschutz geht vor).
//
//  Zuständigkeit je Schuljahr (219, 242): Die Zuständigkeit eines Kontos (Klassenleitung „05A“, Stufenkoordination „Q1“)
//  gilt nur für Daten ihres Schuljahres (Benutzer.ZustaendigSchuljahr). KbNutzer.FuerSchuljahre leert sie für Anfragen auf
//  Daten anderer Schuljahre – die übrigen Rechte (Lehrkraft der Stunde, Admin) bleiben. Ein Zeitraum über zwei Schuljahre
//  zählt nur, wenn beide das Schuljahr der Zuständigkeit sind (also nie).
//
//  Welches Schuljahr Daten haben: Stunden, Einträge und Lerngruppen tragen es; ein Zeitraum hat die Schuljahre der Kalender,
//  die er berührt – ohne Kalender das Schuljahr nach dem Datum (ab 1. August das neue).
// =====================================================================

public static class SchuljahrRechte
{
    public static string NurLesenText(string sj) =>
        $"Das Schuljahr {sj} ist abgeschlossen – sein Klassenbuch ist nur noch lesbar. Ändern geht erst wieder, wenn die " +
        "Verwaltung es öffnet (Kontomenü → „Jahresablauf“).";

    public static string NurAdminText(string sj) =>
        $"Das Schuljahr {sj} ist abgeschlossen – seine Daten sieht nur noch die Verwaltung.";

    /// <summary>Für den Desktop (Vertretungen, Grundplan, SchILD-Import).</summary>
    public static string DesktopText(string sj) =>
        $"Das Schuljahr {sj} ist in der App abgeschlossen – Vertretungen, Grundpläne und SchILD-Importe für dieses Schuljahr " +
        "nimmt die API nicht mehr an. Wieder öffnen kann es die Verwaltung in der App (Kontomenü → „Jahresablauf“, mit Begründung).";

    /// <summary>Hinweis im Plan an Tagen eines abgeschlossenen Schuljahres (nicht Admin).</summary>
    public static string PlanHinweis(string sj) => $"Schuljahr {sj} abgeschlossen";

    // -----------------------------------------------------------------
    //  Lesen der Tabelle
    // -----------------------------------------------------------------

    public static async Task<HashSet<string>> AbgeschlosseneAsync(AppDbContext db, CancellationToken ct = default)
        => (await db.Schuljahre.AsNoTracking()
                .Where(s => s.Status == SchuljahrStatus.Abgeschlossen)
                .Select(s => s.Schuljahr)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

    public static async Task<bool> IstAbgeschlossenAsync(AppDbContext db, string? schuljahr)
    {
        if (string.IsNullOrWhiteSpace(schuljahr)) return false;
        string sj = schuljahr.Trim();
        return await db.Schuljahre.AsNoTracking().AnyAsync(s => s.Schuljahr == sj && s.Status == SchuljahrStatus.Abgeschlossen);
    }

    /// <summary>Das erste abgeschlossene Schuljahr der Liste (sortiert) – oder null.</summary>
    private static async Task<string?> ErstesAbgeschlossenesAsync(AppDbContext db, IEnumerable<string?> jahre)
    {
        var liste = jahre.Where(j => !string.IsNullOrWhiteSpace(j)).Select(j => j!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (liste.Count == 0) return null;
        var zu = await db.Schuljahre.AsNoTracking()
            .Where(s => liste.Contains(s.Schuljahr) && s.Status == SchuljahrStatus.Abgeschlossen)
            .Select(s => s.Schuljahr)
            .ToListAsync();
        return zu.OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault();
    }

    // -----------------------------------------------------------------
    //  Schuljahr von Daten
    // -----------------------------------------------------------------

    /// <summary>Schuljahre, die ein Zeitraum berührt (Kalender); ohne Kalender nach dem Datum. Nie leer.</summary>
    public static async Task<List<string>> JahreAsync(AppDbContext db, DateOnly von, DateOnly bis)
    {
        if (bis < von) (von, bis) = (bis, von);
        var jahre = (await db.Kalender.AsNoTracking()
                .Where(k => k.Start <= bis && k.Ende >= von)
                .Select(k => k.Schuljahr)
                .ToListAsync())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(j => j, StringComparer.Ordinal)
            .ToList();
        if (jahre.Count == 0)
        {
            jahre.Add(EinstellungsKatalog.SchuljahrVon(von));
            var b = EinstellungsKatalog.SchuljahrVon(bis);
            if (b != jahre[0]) jahre.Add(b);
        }
        return jahre;
    }

    /// <summary>Schuljahr eines Tages laut Kalender, sonst nach dem Datum.</summary>
    public static async Task<string> JahrAmAsync(AppDbContext db, DateOnly d)
    {
        var sj = await db.Kalender.AsNoTracking()
            .Where(k => k.Start <= d && k.Ende >= d)
            .OrderByDescending(k => k.Id)
            .Select(k => k.Schuljahr)
            .FirstOrDefaultAsync();
        return sj ?? EinstellungsKatalog.SchuljahrVon(d);
    }

    // -----------------------------------------------------------------
    //  Schreiben (215): nur lesbar
    // -----------------------------------------------------------------

    /// <summary>null = darf geschrieben werden; sonst 409 mit dem Text „… nur noch lesbar“.</summary>
    public static async Task<KbErgebnis?> SchreibsperreAsync(AppDbContext db, IEnumerable<string?> jahre)
        => await ErstesAbgeschlossenesAsync(db, jahre) is string sj ? new KbErgebnis(409, NurLesenText(sj)) : null;

    public static Task<KbErgebnis?> SchreibsperreAsync(AppDbContext db, string? schuljahr)
        => SchreibsperreAsync(db, new[] { schuljahr });

    public static async Task<KbErgebnis?> SchreibsperreZeitraumAsync(AppDbContext db, DateOnly von, DateOnly bis)
        => await SchreibsperreAsync(db, await JahreAsync(db, von, bis));

    /// <summary>P5c-2 (270): für den Desktop, wenn die Daten des Schuljahres gelöscht sind.</summary>
    public static string DesktopGeloeschtText(string sj) =>
        $"Die Daten des Schuljahres {sj} sind in der App gelöscht (Löschfrist) – Vertretungen, Grundpläne und SchILD-Importe " +
        "für dieses Schuljahr nimmt die API nicht mehr an.";

    /// <summary>Für den Desktop: Text, wenn eines der Schuljahre abgeschlossen (P5c-2: bzw. gelöscht) ist – sonst null.</summary>
    public static async Task<string?> DesktopSperreAsync(AppDbContext db, IEnumerable<string?> jahre)
    {
        if (await ErstesAbgeschlossenesAsync(db, jahre) is not string sj) return null;
        bool geloescht = await db.Schuljahre.AsNoTracking().AnyAsync(s => s.Schuljahr == sj && s.GeloeschtAm != null);
        return geloescht ? DesktopGeloeschtText(sj) : DesktopText(sj);
    }

    // -----------------------------------------------------------------
    //  Lesen (216) und Zuständigkeit (219, 242)
    // -----------------------------------------------------------------

    /// <summary>
    /// Nicht Admin und eines der Schuljahre abgeschlossen → 403. Sonst die Person mit der Zuständigkeit, die für diese
    /// Schuljahre gilt (sonst ohne Zuständigkeit).
    /// </summary>
    public static async Task<(KbNutzer U, KbErgebnis? Fehler)> LesenAsync(AppDbContext db, KbNutzer u, IReadOnlyCollection<string> jahre)
    {
        if (!u.IstAdmin && await ErstesAbgeschlossenesAsync(db, jahre) is string sj)
            return (u, new KbErgebnis(403, NurAdminText(sj)));
        return (u.FuerSchuljahre(jahre), null);
    }

    public static async Task<(KbNutzer U, KbErgebnis? Fehler)> LesenAsync(AppDbContext db, KbNutzer u, string schuljahr)
        => await LesenAsync(db, u, new[] { schuljahr });

    public static async Task<(KbNutzer U, KbErgebnis? Fehler)> LesenZeitraumAsync(AppDbContext db, KbNutzer u, DateOnly von, DateOnly bis)
        => await LesenAsync(db, u, await JahreAsync(db, von, bis));

    /// <summary>
    /// Text für ein 403, wenn nur das Schuljahr fehlt: <paramref name="vorher"/> (ohne Einschränkung) wäre für die Klassen
    /// zuständig, die Daten liegen aber in einem anderen Schuljahr – sonst null.
    /// </summary>
    public static string? AnderesJahrText(KbNutzer vorher, IEnumerable<string> klassen)
        => vorher.ZustaendigSchuljahr != null && vorher.Zustaendig.Count > 0
           && KlassenbuchService.ZustaendigFuer(vorher.Zustaendig, klassen.ToList())
            ? $"Deine Zuständigkeit für {string.Join(", ", vorher.Zustaendig)} gilt für das Schuljahr " +
              $"{vorher.ZustaendigSchuljahr} – hier geht es um ein anderes Schuljahr. Bitte einen Zeitraum in " +
              $"{vorher.ZustaendigSchuljahr} wählen."
            : null;

    /// <summary>Für die Pläne: Schuljahre, deren Tage diese Person nicht mehr sieht (Admin: keine).</summary>
    public static async Task<IReadOnlySet<string>> GesperrtFuerAsync(AppDbContext db, bool admin)
        => admin ? new HashSet<string>(StringComparer.Ordinal) : await AbgeschlosseneAsync(db);

    /// <summary>
    /// „Anwesenheit/Thema erfasst ab“ für das Schuljahr eines Tages: das aktuelle Schuljahr aus den Einstellungen; ein anderes
    /// mit den Werten, die beim Vorbereiten des neuen Schuljahres an seiner Zeile gemerkt wurden (217), sonst sein erster
    /// Schultag laut Kalender. Ohne Tag: das aktuelle Schuljahr.
    /// </summary>
    public static async Task<(DateOnly ErfassungAb, DateOnly ThemaAb)> ErfassungAbAsync(AppDbContext db, SchulWerte w, DateOnly? tag)
    {
        if (tag is not DateOnly t) return (w.ErfassungAb, w.ThemaAb);
        var sj = await JahrAmAsync(db, t);
        if (sj == w.Schuljahr) return (w.ErfassungAb, w.ThemaAb);
        var zeile = await db.Schuljahre.AsNoTracking().FirstOrDefaultAsync(s => s.Schuljahr == sj);
        var start = await db.Kalender.AsNoTracking().Where(k => k.Schuljahr == sj).Select(k => (DateOnly?)k.Start).FirstOrDefaultAsync();
        var erfassung = zeile?.ErfassungAb ?? start ?? w.ErfassungAb;
        return (erfassung, zeile?.ThemaAb ?? erfassung);
    }

    /// <summary>
    /// P5a (242): Das Vorjahr des aktuellen Schuljahres, solange es einen Kalender hat und nicht abgeschlossen ist – mit seinem
    /// „Anwesenheit/Thema erfasst ab“ (gemerkt beim Vorbereiten, sonst erster Schultag). Sonst null. „Nicht erfasst“ beginnt
    /// ohne Zeitraum dort (Nachträge bis zum Abschluss).
    /// </summary>
    public static async Task<(string Schuljahr, DateOnly ErfassungAb, DateOnly ThemaAb)?> OffenesVorjahrAsync(AppDbContext db,
        SchulWerte w)
    {
        string vorjahr = Jahresablauf.Verschieben(w.Schuljahr, -1);
        if (vorjahr == w.Schuljahr) return null;
        var start = await db.Kalender.AsNoTracking().Where(k => k.Schuljahr == vorjahr)
            .Select(k => (DateOnly?)k.Start).FirstOrDefaultAsync();
        if (start is not DateOnly s0) return null;
        var zeile = await db.Schuljahre.AsNoTracking().FirstOrDefaultAsync(z => z.Schuljahr == vorjahr);
        if (zeile?.Status == SchuljahrStatus.Abgeschlossen) return null;
        var erfassung = zeile?.ErfassungAb ?? s0;
        return (vorjahr, erfassung, zeile?.ThemaAb ?? erfassung);
    }

    // -----------------------------------------------------------------
    //  Zuständigkeiten ohne Schuljahr festschreiben (219: „ältere Konten gelten bis zum ersten Jahreswechsel weiter“)
    // -----------------------------------------------------------------

    /// <summary>Konten mit Zuständigkeit, aber ohne Schuljahr: bekommen <paramref name="schuljahr"/> (Kontenprotokoll).</summary>
    public static int ZustaendigkeitenFestschreiben(AppDbContext db, string schuljahr, string akteur, string grund)
    {
        var konten = db.Benutzer
            .Where(b => b.Zustaendig != null && b.Zustaendig != "" && (b.ZustaendigSchuljahr == null || b.ZustaendigSchuljahr == ""))
            .ToList();
        Festschreiben(db, konten, schuljahr, akteur, grund);
        if (konten.Count > 0) db.SaveChanges();
        return konten.Count;
    }

    public static async Task<int> ZustaendigkeitenFestschreibenAsync(AppDbContext db, string schuljahr, string akteur, string grund)
    {
        var konten = await db.Benutzer
            .Where(b => b.Zustaendig != null && b.Zustaendig != "" && (b.ZustaendigSchuljahr == null || b.ZustaendigSchuljahr == ""))
            .ToListAsync();
        Festschreiben(db, konten, schuljahr, akteur, grund);
        if (konten.Count > 0) await db.SaveChangesAsync();
        return konten.Count;
    }

    private static void Festschreiben(AppDbContext db, List<Benutzer> konten, string schuljahr, string akteur, string grund)
    {
        foreach (var k in konten)
        {
            k.ZustaendigSchuljahr = schuljahr;
            KontenService.Protokoll(db, akteur, KontenService.Aktion.Geaendert, k,
                $"Zuständigkeit {k.Zustaendig} gilt für das Schuljahr {schuljahr} ({grund})");
        }
    }
}
