using System.Text;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Unterrichtsinhalt und Hausaufgaben (Phase 3.3, Entscheidungen 19–30,
//  Nutzer 03.11.2026).
//
//  - Thema (bis 300 Zeichen) und Hausaufgabe (bis 2000 Zeichen, mehrzeilig,
//    mit „fällig am") stehen an der Lektion und werden im Stundenblatt
//    zusammen mit der Anwesenheit gespeichert (eine Version).
//  - Schreiben: haltende und ursprüngliche Lehrkraft, Admin (Entscheidung 22).
//    Klassenleitung/Stufenkoordination lesen nur. Erst ab dem Tag der Stunde.
//  - Fälligkeit: Vorschlag = nächste Stunde desselben Unterrichts laut Plan
//    (Ferien/freie Tage/Entfall übersprungen), sonst eine Woche später.
//  - Sichtbarkeit (Entscheidung 30): je Unterricht (Schuljahr, UNr, Lehrkraft
//    laut Plan) stellt die Lehrkraft laut Plan bzw. Admin ein, ob Schüler die
//    Hausaufgaben sehen; Standard sichtbar; das Thema bleibt immer sichtbar.
//  - Nur Text: Links (https) macht die App klickbar; keine Dateien.
//  - Jede Änderung landet in der Historie der Lektion (alt → neu).
//
//  Unterricht = (UNr, Lehrkraft laut Plan) – wie in den Fehlzeiten („Meine Kurse").
// =====================================================================

public static class UnterrichtInhaltService
{
    public const int MaxThema = 300;
    public const int MaxHausaufgabe = 2000;

    /// <summary>So weit sucht „nächste Stunde" voraus (überbrückt z. B. Herbstferien).</summary>
    private const int SucheTage = 28;

    /// <summary>Höchstens so viele Stunden in einer Themenliste.</summary>
    public const int MaxThemen = 400;

    /// <summary>Schüler: Hausaufgaben, die höchstens so viele Tage überfällig sind, stehen noch in der Liste.</summary>
    public const int VergangeneTage = 7;

    /// <summary>Geprüfte Eingabe. <see cref="NurThemaWennLeer"/>: Doppelstunde – nur das Thema, nie überschreiben.</summary>
    public sealed record InhaltNeu(string? Thema, string? Hausaufgabe, DateOnly? Faellig, bool NurThemaWennLeer = false)
    {
        public bool IstLeer => Thema == null && Hausaufgabe == null;
    }

    // =================================================================
    //  Rechte
    // =================================================================

    /// <summary>Thema/Hausaufgabe schreiben: haltende bzw. ursprüngliche Lehrkraft und Admin (Entscheidung 22).</summary>
    public static bool DarfInhalt(KbNutzer u, PlanStunde st)
        => u.IstAdmin
           || (u.Kuerzel != null && (KlassenbuchService.Gleich(u.Kuerzel, st.Lehrer)
                                     || KlassenbuchService.Gleich(u.Kuerzel, st.GrundLehrer)
                                     || KlassenbuchService.Gleich(u.Kuerzel, st.OriginalLehrer)));

    /// <summary>Sichtbarkeit der Hausaufgaben umstellen: Lehrkraft laut Plan oder Admin (Entscheidung 30).</summary>
    public static bool DarfSichtbarkeit(KbNutzer u, string lehrer)
        => u.IstAdmin || (u.Kuerzel != null && KlassenbuchService.Gleich(u.Kuerzel, lehrer));

    // =================================================================
    //  Text
    // =================================================================

    /// <summary>
    /// Bereinigt eine Eingabe: Steuerzeichen entfernt, Tab → Leerzeichen, Zeilenenden vereinheitlicht.
    /// Einzeilig: Zeilenumbrüche werden Leerzeichen. Mehrzeilig: Leerzeichen am Zeilenende weg,
    /// höchstens eine Leerzeile am Stück. Leer → null. (Die App rechnet genauso – kbNormText.)
    /// </summary>
    public static string? Normalisiert(string? text, bool mehrzeilig)
    {
        if (text == null) return null;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n') sb.Append(mehrzeilig ? '\n' : ' ');
            else if (c == '\t') sb.Append(' ');
            else if (!char.IsControl(c)) sb.Append(c);
        }
        string s;
        if (mehrzeilig)
        {
            var zeilen = new List<string>();
            int leer = 0;
            foreach (var z in sb.ToString().Split('\n'))
            {
                var zeile = z.TrimEnd();
                if (zeile.Length == 0) { if (++leer > 1) continue; }
                else leer = 0;
                zeilen.Add(zeile);
            }
            s = string.Join("\n", zeilen).Trim();
        }
        else
        {
            s = sb.ToString().Trim();
        }
        return s.Length == 0 ? null : s;
    }

    /// <summary>Prüft Thema/Hausaufgabe/Fälligkeit. Ohne Fälligkeit: nächste Stunde laut Plan bzw. eine Woche.</summary>
    public static async Task<(InhaltNeu? Inhalt, string? Fehler)> PruefenAsync(
        AppDbContext db, DateOnly datum, PlanStunde st, KbInhaltSpeichernDto dto)
    {
        string? thema = Normalisiert(dto.Thema, mehrzeilig: false);
        string? ha = Normalisiert(dto.Hausaufgabe, mehrzeilig: true);
        if (thema != null && thema.Length > MaxThema)
            return (null, $"Das Thema ist zu lang (höchstens {MaxThema} Zeichen).");
        if (ha != null && ha.Length > MaxHausaufgabe)
            return (null, $"Die Hausaufgabe ist zu lang (höchstens {MaxHausaufgabe} Zeichen).");

        DateOnly? faellig = null;
        if (ha != null)
        {
            DateOnly f = dto.Faellig ?? await NaechsteStundeAsync(db, datum, st) ?? datum.AddDays(7);
            if (f <= datum) return (null, "Die Hausaufgabe muss nach dem Tag der Stunde fällig sein.");
            if (f > datum.AddDays(366)) return (null, "Das Fälligkeitsdatum liegt mehr als ein Jahr in der Zukunft.");
            faellig = f;
        }
        return (new InhaltNeu(thema, ha, faellig), null);
    }

    /// <summary>Überträgt Thema/Hausaufgabe auf die Lektion; jede Änderung kommt in die Historie.</summary>
    internal static void Anwenden(KbNutzer u, Lektion l, InhaltNeu neu, string? herkunft, List<KlassenbuchProtokoll> historie)
    {
        string Mit(string text) => herkunft == null ? text : $"{text} ({herkunft})";

        if (!string.Equals(l.Thema, neu.Thema, StringComparison.Ordinal))
        {
            historie.Add(Protokoll(u, "Thema", l, l.Thema, Mit(neu.Thema ?? "(gelöscht)")));
            l.Thema = neu.Thema;
        }
        if (neu.NurThemaWennLeer) return;   // Doppelstunde: die Hausaufgabe gilt einmal (Entscheidung 21)

        DateOnly? altFaellig = l.Hausaufgabe == null ? null : l.HausaufgabeFaellig;
        DateOnly? neuFaellig = neu.Hausaufgabe == null ? null : neu.Faellig;
        if (!string.Equals(l.Hausaufgabe, neu.Hausaufgabe, StringComparison.Ordinal) || altFaellig != neuFaellig)
        {
            historie.Add(Protokoll(u, "Hausaufgabe", l, HaText(l.Hausaufgabe, altFaellig),
                Mit(HaText(neu.Hausaufgabe, neuFaellig) ?? "(gelöscht)")));
            l.Hausaufgabe = neu.Hausaufgabe;
            l.HausaufgabeFaellig = neuFaellig;
        }
    }

    private static string? HaText(string? ha, DateOnly? faellig)
        => ha == null ? null : faellig is DateOnly f ? $"{ha} (fällig {f:dd.MM.yyyy})" : ha;

    private static KlassenbuchProtokoll Protokoll(KbNutzer u, string art, Lektion l, string? alt, string? neu) => new()
    {
        Zeit = DateTime.UtcNow,
        Akteur = u.Name,
        Art = art,
        LektionId = l.Id,
        Alt = alt,
        Neu = neu
    };

    // =================================================================
    //  Stundenblatt
    // =================================================================

    /// <summary>Thema/Hausaufgabe einer Stunde mit Rechten, voriger Stunde, Vorschlag „fällig" und Sichtbarkeit.</summary>
    public static async Task<KbInhaltDto> InhaltAsync(AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st, Lektion? lektion)
    {
        bool darf = DarfInhalt(u, st);
        bool zukunft = tag.Datum > KlassenbuchService.Heute();
        bool darfSchreiben = darf && !zukunft;
        string? sperre = darfSchreiben ? null
            : zukunft ? "Thema und Hausaufgabe lassen sich erst ab dem Tag der Stunde eintragen."
            : "Thema und Hausaufgabe trägt die unterrichtende Lehrkraft ein.";

        string lehrer = st.GrundLehrer.Trim().ToUpperInvariant();
        var vorige = await VorigeAsync(db, tag.Datum, st.Stunde, st.UNr, lehrer);
        DateOnly? naechste = darfSchreiben ? await NaechsteStundeAsync(db, tag.Datum, st) : null;
        bool sichtbar = await SichtbarAsync(db, tag.Schuljahr, st.UNr, lehrer);

        return new KbInhaltDto(
            lektion?.Thema,
            lektion?.Hausaufgabe,
            lektion?.Hausaufgabe != null ? lektion.HausaufgabeFaellig : null,
            darfSchreiben, sperre, vorige, naechste,
            st.UNr, lehrer, sichtbar, DarfSichtbarkeit(u, lehrer));
    }

    /// <summary>Letzte Stunde desselben Unterrichts vor dieser, in der Thema oder Hausaufgabe steht.</summary>
    private static Task<KbVorigeDto?> VorigeAsync(AppDbContext db, DateOnly datum, int stunde, int unr, string lehrer)
        => db.Lektionen.AsNoTracking()
            .Where(x => x.UNr == unr && x.GrundLehrer == lehrer
                        && (x.Datum < datum || (x.Datum == datum && x.Stunde < stunde))
                        && (x.Thema != null || x.Hausaufgabe != null))
            .OrderByDescending(x => x.Datum).ThenByDescending(x => x.Stunde)
            .Select(x => new KbVorigeDto(x.Datum, x.Stunde, x.Thema, x.Hausaufgabe,
                x.Hausaufgabe != null ? x.HausaufgabeFaellig : null))
            .FirstOrDefaultAsync();

    /// <summary>
    /// Vorschlag „fällig am" (Entscheidung 25): der nächste Tag, an dem derselbe Unterricht laut Plan
    /// stattfindet – ab dem Folgetag (eine Doppelstunde zählt nicht), ohne Ferien, freie Tage und
    /// veröffentlichten Entfall. null = in den nächsten vier Wochen keine Stunde gefunden.
    /// </summary>
    public static async Task<DateOnly?> NaechsteStundeAsync(AppDbContext db, DateOnly datum, PlanStunde st)
    {
        var tage = await WochenplanService.TageAsync(db, datum.AddDays(1), datum.AddDays(SucheTage));
        foreach (var t in tage)
            if (t.Stunden.Any(x => (x.UNr == st.UNr || x.GrundUNr == st.UNr)
                                   && KlassenbuchService.Gleich(x.GrundLehrer, st.GrundLehrer)
                                   && WochenplanService.KlassenbuchMoeglich(x.Art, x.Status, x.Quellen)))
                return t.Datum;
        return null;
    }

    // =================================================================
    //  Sichtbarkeit je Unterricht (Entscheidung 30)
    // =================================================================

    /// <summary>Sehen Schüler die Hausaufgaben dieses Unterrichts? Ohne Eintrag: ja.</summary>
    public static async Task<bool> SichtbarAsync(AppDbContext db, string schuljahr, int unr, string lehrer)
    {
        string l = lehrer.Trim().ToUpperInvariant();
        var wert = await db.UnterrichtEinstellungen.AsNoTracking()
            .Where(x => x.Schuljahr == schuljahr && x.UNr == unr && x.Lehrer == l)
            .Select(x => (bool?)x.HausaufgabenSichtbar)
            .FirstOrDefaultAsync();
        return wert ?? true;
    }

    /// <summary>Schuljahr laut Kalender am Datum (Standard heute), sonst das der aktuellen Grundplan-Version.</summary>
    public static async Task<string?> SchuljahrAsync(AppDbContext db, DateOnly? datum)
    {
        var kal = await WochenplanService.KalenderAsync(db, datum ?? KlassenbuchService.Heute());
        if (kal != null) return kal.Schuljahr;
        return (await KlassenbuchService.AktuelleVersionAsync(db))?.Schuljahr;
    }

    public static async Task<KbErgebnis> SichtbarkeitSetzenAsync(AppDbContext db, KbNutzer u, KbSichtbarkeitDto dto)
    {
        if (dto.UNr <= 0 || string.IsNullOrWhiteSpace(dto.Lehrer))
            return new KbErgebnis(400, "Der Unterricht fehlt.");
        string lehrer = dto.Lehrer.Trim().ToUpperInvariant();
        if (!DarfSichtbarkeit(u, lehrer))
            return new KbErgebnis(403, "Ob Schüler die Hausaufgaben sehen, stellt die Lehrkraft des Kurses (laut Stundenplan) ein.");
        var schuljahr = await SchuljahrAsync(db, dto.Datum);
        if (schuljahr == null)
            return new KbErgebnis(404, "Für dieses Datum gibt es kein Schuljahr im Kalender.");

        string meldung = dto.Sichtbar
            ? "Schüler sehen die Hausaufgaben dieses Kurses."
            : "Die Hausaufgaben dieses Kurses sind für Schüler ausgeblendet.";
        var e = await db.UnterrichtEinstellungen
            .FirstOrDefaultAsync(x => x.Schuljahr == schuljahr && x.UNr == dto.UNr && x.Lehrer == lehrer);
        bool alt = e?.HausaufgabenSichtbar ?? true;
        if (alt == dto.Sichtbar) return new KbErgebnis(200, Meldung: meldung);

        var jetzt = DateTime.UtcNow;
        if (e == null)
        {
            e = new UnterrichtEinstellung { Schuljahr = schuljahr, UNr = dto.UNr, Lehrer = lehrer };
            db.UnterrichtEinstellungen.Add(e);
        }
        e.HausaufgabenSichtbar = dto.Sichtbar;
        e.GeaendertVon = u.Name;
        e.GeaendertAm = jetzt;
        db.KlassenbuchProtokolle.Add(new KlassenbuchProtokoll
        {
            Zeit = jetzt, Akteur = u.Name, Art = "Sichtbarkeit", Gruppe = $"{dto.UNr}|{lehrer}",
            Alt = alt ? "Hausaufgaben sichtbar" : "Hausaufgaben ausgeblendet",
            Neu = dto.Sichtbar ? "Hausaufgaben sichtbar" : "Hausaufgaben ausgeblendet"
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return new KbErgebnis(409, "Die Einstellung wurde gerade von jemand anderem geändert. Bitte neu laden.", true);
        }
        return new KbErgebnis(200, Meldung: meldung);
    }

    // =================================================================
    //  Themenliste je Unterricht (Entscheidung 28)
    // =================================================================

    /// <summary>Stunden eines Unterrichts mit Thema/Hausaufgabe, neueste zuerst. null = Unterricht unbekannt.</summary>
    public static async Task<KbThemenDto?> ThemenAsync(
        AppDbContext db, KbNutzer u, int unr, string lehrer, DateOnly von, DateOnly bis)
    {
        string l = lehrer.Trim().ToUpperInvariant();
        var zeilen = await db.Lektionen.AsNoTracking()
            .Where(x => x.UNr == unr && x.GrundLehrer == l && x.Datum >= von && x.Datum <= bis)
            .OrderByDescending(x => x.Datum).ThenByDescending(x => x.Stunde)
            .Take(MaxThemen)
            .Select(x => new
            {
                x.Datum, x.Stunde, x.GrundUNr, x.GrundLehrer, x.Lehrer, x.Status, x.Klassen, x.Fach,
                Erfasst = x.ErfasstAm != null, x.Thema, x.Hausaufgabe, x.HausaufgabeFaellig
            })
            .ToListAsync();

        var titel = await UnterrichtTitelAsync(db, unr, l);
        if (titel == null && zeilen.Count == 0) return null;
        titel ??= $"{zeilen[0].Fach} · {zeilen[0].Klassen.Replace(",", ", ")}";

        var schuljahr = await SchuljahrAsync(db, null) ?? "";
        bool sichtbar = await SichtbarAsync(db, schuljahr, unr, l);

        return new KbThemenDto(titel, unr, l, von, bis, sichtbar, DarfSichtbarkeit(u, l),
            zeilen.Select(x => new KbThemaZeileDto(
                    x.Datum, x.Stunde, x.GrundUNr, x.GrundLehrer, x.Lehrer, x.Status,
                    x.Klassen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    x.Erfasst, x.Thema, x.Hausaufgabe, x.Hausaufgabe != null ? x.HausaufgabeFaellig : null))
                .ToList());
    }

    /// <summary>„D · Q2" aus dem heute gültigen Grundplan; null = Unterricht steht nicht (mehr) im Plan.</summary>
    private static async Task<string?> UnterrichtTitelAsync(AppDbContext db, int unr, string lehrer)
    {
        var ver = await KlassenbuchService.AktuelleVersionAsync(db);
        if (ver == null) return null;
        var slots = (await db.GrundplanSlots.AsNoTracking()
                .Where(s => s.VersionId == ver.Id && s.UNr == unr)
                .ToListAsync())
            .Where(s => KlassenbuchService.Gleich(s.Lehrer, lehrer)
                        && string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (slots.Count == 0) return null;
        var klassen = slots.Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        string fach = slots.GroupBy(s => s.Fach.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => f.Count()).First().Key;
        return $"{fach} · {string.Join(", ", klassen)}";
    }

    // =================================================================
    //  Schüler: „Was ist auf?" (Entscheidung 24)
    // =================================================================

    /// <summary>
    /// Hausaufgaben aus den eigenen Stunden der letzten fünf Wochen, die frühestens vor
    /// <see cref="VergangeneTage"/> Tagen fällig waren – nach Fälligkeit sortiert. Ausgeblendete
    /// Unterrichte fehlen (dieselbe Logik wie im Wochenplan).
    /// </summary>
    public static async Task<HaListeDto> HausaufgabenAsync(AppDbContext db, Schueler s)
    {
        var heute = KlassenbuchService.Heute();
        var ab = heute.AddDays(-VergangeneTage);
        var gruppen = await db.Zugehoerigkeiten.AsNoTracking()
            .Where(z => z.SchuelerId == s.Id)
            .Select(z => z.Lerngruppe!.Bezeichnung)
            .ToListAsync();
        var fertig = await WochenplanService.FertigeGruppenAsync(db);
        var plan = await WochenplanService.BerechneAsync(
            db, WochenZielArt.Schueler, $"{s.Vorname} {s.Nachname}", gruppen, heute.AddDays(-35), 6, fertig,
            InhaltLeser.FuerSchueler());

        var liste = plan.Tage
            .Where(t => t.Datum <= heute)
            .SelectMany(t => t.Stunden.Select(st => (t.Datum, St: st)))
            .Where(x => x.St.Hausaufgabe != null && x.St.HausaufgabeFaellig is DateOnly f && f >= ab)
            .Select(x => new HaEintragDto(
                WochenplanService.LektionSchluessel(x.Datum, x.St.Stunde, x.St.GrundUNr, x.St.GrundLehrer),
                x.Datum, x.St.Stunde, x.St.Fach, x.St.Lehrer, x.St.Thema, x.St.Hausaufgabe!, x.St.HausaufgabeFaellig!.Value))
            .OrderBy(h => h.Faellig).ThenBy(h => h.Datum).ThenBy(h => h.Stunde)
            .ToList();
        return new HaListeDto(heute, ab, liste);
    }
}
