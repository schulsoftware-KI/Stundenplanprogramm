using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Klassenbucheinträge (Phase 3.4, Entscheidungen 31–39, Nutzer 04.11.2026).
//
//  Arten: Klasseneintrag (betrifft die Lerngruppe der Stunde) und
//  Schülereintrag (je Person ein eigener Datensatz – Sichtbarkeit und
//  Löschen bleiben pro Person getrennt).
//  Kategorien: Schüler – Lob, Hinweis, Ermahnung, Hausaufgabe vergessen,
//  Material vergessen; Klasse – Vorkommnis, Hinweis, Lob. Text (bis 500
//  Zeichen) Pflicht bei Hinweis, Ermahnung, Vorkommnis. Formale
//  Ordnungsmaßnahmen (§ 53 SchulG NRW) bleiben außen vor.
//
//  Schreiben (34): wer die Stunde hält (auch Vertretung), Lehrkraft laut
//    Plan, Klassenleitung/Stufenkoordination (eigene Klasse/Stufe), Admin –
//    nicht für künftige Stunden. Klassenleitung/Stufenkoordination, die die
//    Stunde nicht halten, schreiben Schülereinträge nur für ihre eigenen
//    Schüler. Ohne Stunde (nur Datum): Schülereinträge von KL/SK/Admin.
//  Ändern/Löschen (34): nur Verfasser und Admin, ohne Frist; Änderungen in
//    die Historie. Beim Löschen verschwinden auch die bisherigen
//    Historienzeilen (sie enthalten den Text); es bleibt nur
//    „Schülereintrag: gelöscht (Kategorie, Datum)“ – ohne Text und Schüler.
//  Lesen (35): Klasseneinträge – Lehrkräfte der Klasse bzw. des Kurses laut
//    Grundplan (dazu die Lehrkräfte der Stunde), Klassenleitung/
//    Stufenkoordination, Admin. Schülereinträge – nur Verfasser,
//    Klassenleitung/Stufenkoordination des Schülers, Admin.
//  Schüler sehen keine Einträge (36). Keine Automatik, nur Zähler (38).
//  Löschfrist (39): "Klassenbuch:EintraegeLoeschenNachTagen" (0 = aus).
// =====================================================================

public static class KlassenbuchEintragService
{
    public const int MaxText = 500;
    public const int MaxSchuelerJeEintrag = 60;
    public const int MaxListe = 1000;
    public const int MaxWeitere = 200;

    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex KlassenMuster = new(@"^\d{2}[A-Za-z]{1,2}$", RegexOptions.Compiled);

    public static readonly KbEintragKategorie[] SchuelerKategorien =
    {
        KbEintragKategorie.Lob, KbEintragKategorie.Hinweis, KbEintragKategorie.Ermahnung,
        KbEintragKategorie.HausaufgabeVergessen, KbEintragKategorie.MaterialVergessen
    };

    public static readonly KbEintragKategorie[] KlassenKategorien =
    {
        KbEintragKategorie.Vorkommnis, KbEintragKategorie.Hinweis, KbEintragKategorie.Lob
    };

    /// <summary>Reihenfolge der Zähler.</summary>
    private static readonly KbEintragKategorie[] ZaehlerReihenfolge =
    {
        KbEintragKategorie.Lob, KbEintragKategorie.Hinweis, KbEintragKategorie.Ermahnung,
        KbEintragKategorie.HausaufgabeVergessen, KbEintragKategorie.MaterialVergessen, KbEintragKategorie.Vorkommnis
    };

    public static bool TextPflicht(KbEintragKategorie k)
        => k is KbEintragKategorie.Hinweis or KbEintragKategorie.Ermahnung or KbEintragKategorie.Vorkommnis;

    public static string KategorieText(KbEintragKategorie k) => k switch
    {
        KbEintragKategorie.Lob => "Lob",
        KbEintragKategorie.Hinweis => "Hinweis",
        KbEintragKategorie.Ermahnung => "Ermahnung",
        KbEintragKategorie.HausaufgabeVergessen => "Hausaufgabe vergessen",
        KbEintragKategorie.MaterialVergessen => "Material vergessen",
        _ => "Vorkommnis"
    };

    /// <summary>Für die Historie: „Ermahnung: stört wiederholt“.</summary>
    private static string Beschreibung(KbEintragKategorie k, string? text)
        => text == null ? KategorieText(k) : $"{KategorieText(k)}: {text}";

    private static string ArtText(KbEintragArt a) => a == KbEintragArt.Klasse ? "Klasseneintrag" : "Schülereintrag";

    // =================================================================
    //  Rechte
    // =================================================================

    /// <summary>
    /// Angemeldete Person samt den Klassen (Sek I) und Kursen, die sie laut heute gültigem
    /// Grundplan unterrichtet – für das Lesen von Klasseneinträgen (Entscheidung 35).
    /// </summary>
    internal sealed record Leser(KbNutzer U, HashSet<string> Klassen, HashSet<string> Kurse);

    internal static async Task<Leser> LeserAsync(AppDbContext db, KbNutzer u)
    {
        var klassen = new HashSet<string>(Cmp);
        var kurse = new HashSet<string>(Cmp);
        if (u.Kuerzel != null && !u.IstAdmin)
        {
            var ver = await KlassenbuchService.AktuelleVersionAsync(db);
            if (ver != null)
            {
                var slots = await db.GrundplanSlots.AsNoTracking()
                    .Where(s => s.VersionId == ver.Id)
                    .Select(s => new { s.Lehrer, s.Art, s.Klasse, s.LerngruppeKey, s.LerngruppeTyp })
                    .ToListAsync();
                foreach (var s in slots)
                {
                    if (!KlassenbuchService.Gleich(s.Lehrer, u.Kuerzel)
                        || !string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase)) continue;
                    var kl = (s.Klasse ?? "").Trim();
                    if (KlassenMuster.IsMatch(kl)) klassen.Add(kl);
                    if (string.Equals(s.LerngruppeTyp, "Kurs", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(s.LerngruppeKey))
                        kurse.Add(s.LerngruppeKey.Trim());
                }
            }
        }
        return new Leser(u, klassen, kurse);
    }

    /// <summary>Ändern und Löschen: nur Verfasser und Admin (Entscheidung 34).</summary>
    public static bool DarfAendern(KbNutzer u, KlassenbuchEintrag e)
        => u.IstAdmin || KlassenbuchService.Gleich(e.VerfasstVon, u.Name);

    /// <summary>Lesen nach Entscheidung 35.</summary>
    private static bool DarfLesen(Leser l, KlassenbuchEintrag e, IReadOnlyDictionary<int, SInfo> info)
    {
        var u = l.U;
        if (DarfAendern(u, e)) return true;
        if (e.Art == KbEintragArt.Schueler)
        {
            if (!u.IstKlassenleitungOderKoordination || e.SchuelerId is not int sid) return false;
            return info.TryGetValue(sid, out var s) && FehlzeitenService.IstLeitungFuer(u, s.Klasse, s.Stufe);
        }
        if (u.Kuerzel != null && (KlassenbuchService.Gleich(u.Kuerzel, e.Lehrer) || KlassenbuchService.Gleich(u.Kuerzel, e.GrundLehrer)))
            return true;
        var klassen = Liste(e.Klassen);
        if (u.IstKlassenleitungOderKoordination && KlassenbuchService.ZustaendigFuer(u.Zustaendig, klassen)) return true;
        if (klassen.Any(k => KlassenMuster.IsMatch(k) && l.Klassen.Contains(k))) return true;
        return Liste(e.Lerngruppen).Any(g => l.Kurse.Contains(g));
    }

    /// <summary>Hält bzw. hielt die Person diese Stunde (oder ist Admin)?</summary>
    private static bool HaeltStunde(KbNutzer u, PlanStunde st)
        => u.IstAdmin || (u.Kuerzel != null &&
            (KlassenbuchService.Gleich(u.Kuerzel, st.Lehrer) || KlassenbuchService.Gleich(u.Kuerzel, st.GrundLehrer)
             || KlassenbuchService.Gleich(u.Kuerzel, st.OriginalLehrer)));

    // =================================================================
    //  Schülerdaten
    // =================================================================

    private sealed record SInfo(string Name, string? Klasse, string Stufe, bool IstPseudo);

    private static async Task<Dictionary<int, SInfo>> SchuelerInfoAsync(AppDbContext db, IEnumerable<int> ids)
    {
        var liste = ids.Distinct().ToList();
        if (liste.Count == 0) return new Dictionary<int, SInfo>();
        var personen = await db.Schueler.AsNoTracking()
            .Where(s => liste.Contains(s.Id))
            .Select(s => new { s.Id, s.Vorname, s.Nachname, s.Stufe, s.IstPseudo })
            .ToListAsync();
        var kal = await WochenplanService.KalenderAsync(db, KlassenbuchService.Heute());
        var klassen = await KlassenbuchService.KlassenAsync(db, liste, kal?.Schuljahr ?? "");
        return personen.ToDictionary(p => p.Id,
            p => new SInfo($"{p.Vorname} {p.Nachname}", klassen.TryGetValue(p.Id, out var k) ? k : null, p.Stufe, p.IstPseudo));
    }

    private static KeEintragDto Dto(KlassenbuchEintrag e, KbNutzer u, IReadOnlyDictionary<int, SInfo> info)
    {
        SInfo? s = e.SchuelerId is int sid && info.TryGetValue(sid, out var x) ? x : null;
        return new KeEintragDto(
            e.Id, e.Art.ToString(), e.Kategorie.ToString(), e.Text, e.Datum, e.Stunde, e.GrundUNr, e.GrundLehrer,
            e.Lehrer, e.Fach, Liste(e.Klassen), e.SchuelerId, s?.Name,
            s == null ? null : s.Klasse ?? (string.IsNullOrWhiteSpace(s.Stufe) ? null : s.Stufe),
            e.VerfasstVon, e.VerfasstAm, e.GeaendertVon, e.GeaendertAm, e.Version, DarfAendern(u, e));
    }

    // =================================================================
    //  Stunde (Stundenblatt)
    // =================================================================

    private static IQueryable<KlassenbuchEintrag> StundeQuery(AppDbContext db, DateOnly datum, PlanStunde st)
    {
        string grundLehrer = st.GrundLehrer.Trim().ToUpperInvariant();
        int stunde = st.Stunde;
        int unr = st.GrundUNr;
        return db.KlassenbuchEintraege.Where(e => e.Datum == datum && e.Stunde == stunde
                                                  && e.GrundUNr == unr && e.GrundLehrer == grundLehrer);
    }

    /// <summary>
    /// Schüler, für die die Person in dieser Stunde Schülereinträge schreiben darf: Teilnehmer der
    /// Stunde (und Schüler mit erfasster Anwesenheit, die nicht mehr zur Liste gehören). Wer die Stunde
    /// nicht hält (Klassenleitung/Stufenkoordination), nur für die eigenen Schüler.
    /// </summary>
    private static async Task<(HashSet<int> Erlaubt, HashSet<int> Pool)> ErlaubteSchuelerAsync(
        AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st)
    {
        var (liste, _, _) = await KlassenbuchService.TeilnehmerAsync(db, u, tag, st);
        string grundLehrer = st.GrundLehrer.Trim().ToUpperInvariant();
        var ausserhalb = await db.Anwesenheiten.AsNoTracking()
            .Where(a => a.Lektion!.Datum == tag.Datum && a.Lektion.Stunde == st.Stunde
                        && a.Lektion.GrundUNr == st.GrundUNr && a.Lektion.GrundLehrer == grundLehrer)
            .Select(a => a.SchuelerId)
            .ToListAsync();

        var pool = liste.Select(t => t.Id).Concat(ausserhalb).ToHashSet();
        if (HaeltStunde(u, st)) return (pool, pool);
        if (!u.IstKlassenleitungOderKoordination) return (new HashSet<int>(), pool);

        var erlaubt = new HashSet<int>();
        foreach (var t in liste)
            if (FehlzeitenService.IstLeitungFuer(u, t.Klasse, null)) erlaubt.Add(t.Id);
        var rest = pool.Where(id => !erlaubt.Contains(id)).ToList();
        if (rest.Count > 0)
        {
            var info = await SchuelerInfoAsync(db, rest);
            foreach (var (id, s) in info)
                if (FehlzeitenService.IstLeitungFuer(u, s.Klasse, s.Stufe)) erlaubt.Add(id);
        }
        return (erlaubt, pool);
    }

    /// <summary>Einträge einer Stunde, soweit die Person sie lesen darf, und ihre Schreibrechte.</summary>
    public static async Task<KeStundeDto> StundeAsync(
        AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st, string? meldung = null)
    {
        var alle = await StundeQuery(db, tag.Datum, st).AsNoTracking().ToListAsync();
        var info = await SchuelerInfoAsync(db, alle.Where(e => e.SchuelerId != null).Select(e => e.SchuelerId!.Value));
        var leser = await LeserAsync(db, u);

        bool zukunft = tag.Datum > KlassenbuchService.Heute();
        bool darf = KlassenbuchService.DarfStunde(u, st) && !zukunft;
        var erlaubt = darf ? (await ErlaubteSchuelerAsync(db, u, tag, st)).Erlaubt : new HashSet<int>();

        var eintraege = alle
            .Where(e => DarfLesen(leser, e, info))
            .Select(e => Dto(e, u, info))
            .OrderBy(e => e.Art == nameof(KbEintragArt.Klasse) ? 0 : 1)
            .ThenBy(e => e.Schueler ?? "", KlassenbuchService.NamenCmp)
            .ThenBy(e => e.VerfasstAm)
            .ToList();

        return new KeStundeDto(darf, zukunft ? "Einträge lassen sich erst ab dem Tag der Stunde schreiben." : null,
            erlaubt.OrderBy(i => i).ToList(), eintraege, meldung);
    }

    /// <summary>Neuer Klasseneintrag bzw. Schülereintrag (für einen oder mehrere Schüler) in einer Stunde.</summary>
    public static async Task<KbErgebnis> AnlegenAsync(
        AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st, KeNeuStundeDto dto)
    {
        if (tag.Datum > KlassenbuchService.Heute())
            return new KbErgebnis(400, "Einträge lassen sich erst ab dem Tag der Stunde schreiben.");
        if (!Enum.TryParse(dto.Art?.Trim(), true, out KbEintragArt art) || !Enum.IsDefined(art))
            return new KbErgebnis(400, "Bitte wählen: Eintrag für die Klasse oder für Schüler.");
        var (kategorie, text, fehler) = Pruefen(art, dto.Kategorie, dto.Text);
        if (fehler != null) return new KbErgebnis(400, fehler);

        var neue = new List<KlassenbuchEintrag>();
        var jetzt = DateTime.UtcNow;
        if (art == KbEintragArt.Klasse)
        {
            bool pseudo = await db.Schueler.AsNoTracking().AnyAsync(s => s.IstPseudo);
            neue.Add(NeuInStunde(u, tag, st, art, kategorie, text, null, pseudo, jetzt));
        }
        else
        {
            var ids = (dto.SchuelerIds ?? new List<int>()).Distinct().ToList();
            if (ids.Count == 0) return new KbErgebnis(400, "Bitte mindestens eine Schülerin bzw. einen Schüler wählen.");
            if (ids.Count > MaxSchuelerJeEintrag)
                return new KbErgebnis(400, $"Höchstens {MaxSchuelerJeEintrag} Schüler auf einmal.");
            var (erlaubt, pool) = await ErlaubteSchuelerAsync(db, u, tag, st);
            if (ids.Any(id => !pool.Contains(id)))
                return new KbErgebnis(400, "Mindestens eine Schülerin bzw. ein Schüler gehört nicht zu dieser Stunde.");
            if (ids.Any(id => !erlaubt.Contains(id)))
                return new KbErgebnis(403, "Für Schüler anderer Klassen schreibt die Lehrkraft der Stunde bzw. deren Klassenleitung den Eintrag.");
            var pseudoJe = await db.Schueler.AsNoTracking()
                .Where(s => ids.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.IstPseudo);
            foreach (var id in ids)
                neue.Add(NeuInStunde(u, tag, st, art, kategorie, text, id, pseudoJe.TryGetValue(id, out var p) && p, jetzt));
        }

        await SpeichernMitHistorieAsync(db, u, neue, jetzt);
        string meldung = art == KbEintragArt.Klasse ? "Klasseneintrag gespeichert."
            : neue.Count == 1 ? "Eintrag gespeichert."
            : $"Eintrag für {neue.Count} Schüler gespeichert.";
        return new KbErgebnis(200, Meldung: meldung);
    }

    private static KlassenbuchEintrag NeuInStunde(
        KbNutzer u, TagKontext tag, PlanStunde st, KbEintragArt art, KbEintragKategorie kategorie, string? text,
        int? schuelerId, bool pseudo, DateTime jetzt) => new()
    {
        Art = art,
        Kategorie = kategorie,
        Text = text,
        SchuelerId = schuelerId,
        Datum = tag.Datum,
        Stunde = st.Stunde,
        GrundUNr = st.GrundUNr,
        GrundLehrer = st.GrundLehrer.Trim().ToUpperInvariant(),
        UNr = st.UNr,
        Lehrer = st.Lehrer,
        Fach = st.Fach,
        Klassen = string.Join(",", st.Klassen),
        Lerngruppen = string.Join(",", st.Gruppen),
        Schuljahr = tag.Schuljahr,
        VerfasstVon = u.Name,
        VerfasstAm = jetzt,
        Version = 1,
        IstPseudo = pseudo
    };

    /// <summary>
    /// Schülereintrag ohne Stunde – nur Datum (Klassenleitung/Stufenkoordination des Schülers, Admin),
    /// z. B. für Vorfälle in der Pause oder auf einem Ausflug.
    /// </summary>
    public static async Task<KbErgebnis> AnlegenOhneStundeAsync(AppDbContext db, KbNutzer u, int schuelerId, KeNeuOhneStundeDto dto)
    {
        var info = await SchuelerInfoAsync(db, new[] { schuelerId });
        if (!info.TryGetValue(schuelerId, out var s)) return new KbErgebnis(404, "Schüler nicht gefunden.");
        if (!FehlzeitenService.IstLeitungFuer(u, s.Klasse, s.Stufe))
            return new KbErgebnis(403, "Einträge ohne Stunde schreiben die Klassenleitung bzw. Stufenkoordination und die Verwaltung.");
        var heute = KlassenbuchService.Heute();
        if (dto.Datum > heute) return new KbErgebnis(400, "Das Datum liegt in der Zukunft.");
        if (dto.Datum < heute.AddDays(-366)) return new KbErgebnis(400, "Das Datum liegt mehr als ein Jahr zurück.");
        var (kategorie, text, fehler) = Pruefen(KbEintragArt.Schueler, dto.Kategorie, dto.Text);
        if (fehler != null) return new KbErgebnis(400, fehler);

        var kal = await WochenplanService.KalenderAsync(db, dto.Datum);
        var jetzt = DateTime.UtcNow;
        var e = new KlassenbuchEintrag
        {
            Art = KbEintragArt.Schueler,
            Kategorie = kategorie,
            Text = text,
            SchuelerId = schuelerId,
            Datum = dto.Datum,
            Klassen = s.Klasse ?? s.Stufe,
            Schuljahr = kal?.Schuljahr ?? "",
            VerfasstVon = u.Name,
            VerfasstAm = jetzt,
            Version = 1,
            IstPseudo = s.IstPseudo
        };
        await SpeichernMitHistorieAsync(db, u, new List<KlassenbuchEintrag> { e }, jetzt);
        return new KbErgebnis(200, Meldung: "Eintrag gespeichert.");
    }

    private static async Task SpeichernMitHistorieAsync(AppDbContext db, KbNutzer u, List<KlassenbuchEintrag> neue, DateTime jetzt)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        db.KlassenbuchEintraege.AddRange(neue);
        await db.SaveChangesAsync();   // Ids für die Historie
        db.KlassenbuchProtokolle.AddRange(neue.Select(e => Protokoll(u, "Eintrag", e, null,
            Beschreibung(e.Kategorie, e.Text), jetzt)));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    // =================================================================
    //  Ändern, löschen, Historie
    // =================================================================

    public static async Task<(KbErgebnis Ergebnis, KeEintragDto? Eintrag)> AendernAsync(
        AppDbContext db, KbNutzer u, int id, KeAendernDto dto)
    {
        var e = await db.KlassenbuchEintraege.FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return (new KbErgebnis(404, "Diesen Eintrag gibt es nicht (mehr)."), null);
        if (!DarfAendern(u, e))
            return (new KbErgebnis(403, "Ändern dürfen nur die Verfasserin bzw. der Verfasser und die Verwaltung."), null);
        if (dto.Version != e.Version)
            return (new KbErgebnis(409, e.GeaendertVon != null
                ? $"Der Eintrag wurde inzwischen von {e.GeaendertVon} geändert. Bitte neu laden."
                : "Der Eintrag hat sich inzwischen geändert. Bitte neu laden.", true), null);
        var (kategorie, text, fehler) = Pruefen(e.Art, dto.Kategorie, dto.Text);
        if (fehler != null) return (new KbErgebnis(400, fehler), null);

        var info = await SchuelerInfoAsync(db, e.SchuelerId is int sid ? new[] { sid } : Array.Empty<int>());
        if (kategorie == e.Kategorie && string.Equals(text, e.Text, StringComparison.Ordinal))
            return (new KbErgebnis(200, Meldung: "Keine Änderung."), Dto(e, u, info));

        var jetzt = DateTime.UtcNow;
        string alt = Beschreibung(e.Kategorie, e.Text);
        e.Kategorie = kategorie;
        e.Text = text;
        e.GeaendertVon = u.Name;
        e.GeaendertAm = jetzt;
        e.Version++;
        db.KlassenbuchProtokolle.Add(Protokoll(u, "Eintrag", e, alt, Beschreibung(kategorie, text), jetzt));
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return (new KbErgebnis(409, "Der Eintrag wurde gerade von jemand anderem geändert. Bitte neu laden.", true), null);
        }
        return (new KbErgebnis(200, Meldung: "Eintrag geändert."), Dto(e, u, info));
    }

    /// <summary>
    /// Löscht einen Eintrag (Verfasser, Admin). Auch die bisherigen Historienzeilen des Eintrags
    /// verschwinden – sie enthalten den Text. Übrig bleibt nur „gelöscht (Kategorie, Datum)“.
    /// </summary>
    public static async Task<KbErgebnis> LoeschenAsync(AppDbContext db, KbNutzer u, int id)
    {
        var e = await db.KlassenbuchEintraege.FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return new KbErgebnis(404, "Diesen Eintrag gibt es nicht (mehr).");
        if (!DarfAendern(u, e))
            return new KbErgebnis(403, "Löschen dürfen nur die Verfasserin bzw. der Verfasser und die Verwaltung.");

        var jetzt = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync();
        var alteZeilen = await db.KlassenbuchProtokolle.Where(p => p.EintragId == id).ToListAsync();
        db.KlassenbuchProtokolle.RemoveRange(alteZeilen);
        db.KlassenbuchProtokolle.Add(new KlassenbuchProtokoll
        {
            Zeit = jetzt,
            Akteur = u.Name,
            Art = "EintragGeloescht",
            EintragId = e.Id,
            Gruppe = StundenSchluessel(e),
            SchuelerId = null,   // bewusst ohne Schüler (ein falsch zugeordneter Eintrag ist wirklich weg)
            Alt = ArtText(e.Art),
            Neu = $"gelöscht ({KategorieText(e.Kategorie)}, {e.Datum:dd.MM.yyyy})",
            IstPseudo = e.IstPseudo
        });
        db.KlassenbuchEintraege.Remove(e);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new KbErgebnis(200, Meldung: "Eintrag gelöscht.");
    }

    /// <summary>Änderungshistorie eines Eintrags (nur wer ihn lesen darf). null = nicht gefunden bzw. nicht erlaubt.</summary>
    public static async Task<List<KbHistorieDto>?> HistorieAsync(AppDbContext db, KbNutzer u, int id)
    {
        var e = await db.KlassenbuchEintraege.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return null;
        var info = await SchuelerInfoAsync(db, e.SchuelerId is int sid ? new[] { sid } : Array.Empty<int>());
        if (!DarfLesen(await LeserAsync(db, u), e, info)) return null;
        var zeilen = await db.KlassenbuchProtokolle.AsNoTracking()
            .Where(p => p.EintragId == id)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(200)
            .ToListAsync();
        return zeilen.Select(p => new KbHistorieDto(p.Zeit, p.Akteur, p.Art,
                p.SchuelerId is int s && info.TryGetValue(s, out var x) ? x.Name : null, p.Alt, p.Neu))
            .ToList();
    }

    /// <summary>
    /// Historienzeilen der Einträge einer Stunde für die Änderungshistorie des Stundenblatts – nur,
    /// was die Person lesen darf. Gelöschte Klasseneinträge sieht jede Person, die das Stundenblatt
    /// öffnen darf; gelöschte Schülereinträge (ohne Schüler, ohne Text) die Person, die gelöscht hat,
    /// die Klassenleitung bzw. Stufenkoordination der Stunde und die Verwaltung.
    /// </summary>
    internal static async Task<List<KlassenbuchProtokoll>> HistorieDerStundeAsync(
        AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st)
    {
        string key = WochenplanService.LektionSchluessel(tag.Datum, st.Stunde, st.GrundUNr, st.GrundLehrer);
        var zeilen = await db.KlassenbuchProtokolle.AsNoTracking()
            .Where(p => p.Gruppe == key && (p.Art == "Eintrag" || p.Art == "EintragGeloescht"))
            .ToListAsync();
        if (zeilen.Count == 0) return zeilen;

        var eintraege = await StundeQuery(db, tag.Datum, st).AsNoTracking().ToDictionaryAsync(e => e.Id);
        var info = await SchuelerInfoAsync(db, eintraege.Values.Where(e => e.SchuelerId != null).Select(e => e.SchuelerId!.Value));
        var leser = await LeserAsync(db, u);
        bool leitung = u.IstAdmin || (u.IstKlassenleitungOderKoordination && KlassenbuchService.ZustaendigFuer(u.Zustaendig, st.Klassen));

        return zeilen.Where(p =>
        {
            if (p.Art == "EintragGeloescht")
                return p.Alt == ArtText(KbEintragArt.Klasse) || leitung || KlassenbuchService.Gleich(p.Akteur, u.Name);
            return p.EintragId is int eid && eintraege.TryGetValue(eid, out var e) && DarfLesen(leser, e, info);
        }).ToList();
    }

    private static string? StundenSchluessel(KlassenbuchEintrag e)
        => e.Stunde is int s && e.GrundUNr is int unr && e.GrundLehrer != null
            ? WochenplanService.LektionSchluessel(e.Datum, s, unr, e.GrundLehrer)
            : null;

    private static KlassenbuchProtokoll Protokoll(
        KbNutzer u, string art, KlassenbuchEintrag e, string? alt, string? neu, DateTime jetzt) => new()
    {
        Zeit = jetzt,
        Akteur = u.Name,
        Art = art,
        EintragId = e.Id,
        Gruppe = StundenSchluessel(e),
        SchuelerId = e.SchuelerId,
        Alt = alt,
        Neu = neu,
        IstPseudo = e.IstPseudo
    };

    // =================================================================
    //  Listen (Entscheidung 37)
    // =================================================================

    /// <summary>
    /// Einträge eines Schülers: Klassenleitung/Stufenkoordination/Admin alle, sonst nur die eigenen.
    /// Mit <paramref name="unr"/>/<paramref name="unterrichtLehrer"/>: nur dieser Unterricht.
    /// </summary>
    public static async Task<KeListeDto?> SchuelerAsync(
        AppDbContext db, KbNutzer u, int id, DateOnly von, DateOnly bis, int? unr, string? unterrichtLehrer)
    {
        var info = await SchuelerInfoAsync(db, new[] { id });
        if (!info.TryGetValue(id, out var s)) return null;
        bool leitung = FehlzeitenService.IstLeitungFuer(u, s.Klasse, s.Stufe);

        var q = db.KlassenbuchEintraege.AsNoTracking()
            .Where(e => e.SchuelerId == id && e.Datum >= von && e.Datum <= bis);
        bool gefiltert = unr is int && !string.IsNullOrWhiteSpace(unterrichtLehrer);
        if (gefiltert)
        {
            int un = unr!.Value;
            string l = unterrichtLehrer!.Trim().ToUpperInvariant();
            q = q.Where(e => e.UNr == un && e.GrundLehrer == l);
        }
        var alle = await q.ToListAsync();
        var leser = await LeserAsync(db, u);
        var sichtbar = alle.Where(e => DarfLesen(leser, e, info)).ToList();
        return ListeDto(s.Name, von, bis, !leitung, leitung && !gefiltert, sichtbar, u, info, null, null);
    }

    /// <summary>Alle Einträge einer Klasse bzw. Stufe (Rechte prüft der Controller: KL/SK zuständig, Admin).</summary>
    public static async Task<KeListeDto> KlasseAsync(AppDbContext db, KbNutzer u, string klasse, DateOnly von, DateOnly bis)
    {
        string k = klasse.Trim().ToUpperInvariant();
        var lgIds = (await db.Lerngruppen.AsNoTracking().Where(l => l.Typ == LerngruppeTyp.Klasse).ToListAsync())
            .Where(l => KlassenbuchService.Gleich(l.Bezeichnung, k))
            .Select(l => l.Id)
            .ToList();
        bool istStufe = lgIds.Count == 0;
        List<int> ids = !istStufe
            ? await db.Zugehoerigkeiten.AsNoTracking().Where(z => lgIds.Contains(z.LerngruppeId))
                .Select(z => z.SchuelerId).Distinct().ToListAsync()
            : await db.Schueler.AsNoTracking().Where(x => x.Stufe.ToUpper() == k).Select(x => x.Id).ToListAsync();

        var schuelerEintraege = ids.Count == 0
            ? new List<KlassenbuchEintrag>()
            : await db.KlassenbuchEintraege.AsNoTracking()
                .Where(e => e.Art == KbEintragArt.Schueler && e.SchuelerId != null && ids.Contains(e.SchuelerId.Value)
                            && e.Datum >= von && e.Datum <= bis)
                .ToListAsync();
        var filter = new List<string> { k };
        var klassenEintraege = (await db.KlassenbuchEintraege.AsNoTracking()
                .Where(e => e.Art == KbEintragArt.Klasse && e.Datum >= von && e.Datum <= bis)
                .ToListAsync())
            .Where(e => KlassenbuchService.ZustaendigFuer(filter, Liste(e.Klassen)))
            .ToList();

        var alle = schuelerEintraege.Concat(klassenEintraege).ToList();
        var info = await SchuelerInfoAsync(db, alle.Where(e => e.SchuelerId != null).Select(e => e.SchuelerId!.Value));
        var leser = await LeserAsync(db, u);
        var sichtbar = alle.Where(e => DarfLesen(leser, e, info)).ToList();
        string titel = istStufe
            ? (k.Length == 2 && char.IsDigit(k[0]) && char.IsDigit(k[1]) ? $"Jahrgang {k}" : $"Stufe {k}")
            : $"Klasse {k}";
        return ListeDto(titel, von, bis, false, false, sichtbar, u, info, null, null);
    }

    /// <summary>
    /// Einträge eines Unterrichts (UNr + Lehrkraft laut Plan; Rechte prüft der Controller) und –
    /// als „weitere“ – Klasseneinträge seiner Sek-I-Klassen aus anderen Stunden (Entscheidung 35:
    /// Lehrkräfte der Klasse lesen Klasseneinträge). null = Unterricht unbekannt.
    /// </summary>
    public static async Task<KeListeDto?> UnterrichtAsync(
        AppDbContext db, KbNutzer u, int unr, string lehrer, DateOnly von, DateOnly bis)
    {
        string l = lehrer.Trim().ToUpperInvariant();
        var (titel, sekI) = await UnterrichtInfoAsync(db, unr, l);
        var eigene = await db.KlassenbuchEintraege.AsNoTracking()
            .Where(e => e.UNr == unr && e.GrundLehrer == l && e.Datum >= von && e.Datum <= bis)
            .ToListAsync();
        if (titel == null && eigene.Count == 0) return null;

        var weitere = new List<KlassenbuchEintrag>();
        if (sekI.Count > 0)
        {
            weitere = (await db.KlassenbuchEintraege.AsNoTracking()
                    .Where(e => e.Art == KbEintragArt.Klasse && e.Datum >= von && e.Datum <= bis
                                && !(e.UNr == unr && e.GrundLehrer == l))
                    .ToListAsync())
                .Where(e => Liste(e.Klassen).Any(k => sekI.Contains(k)))
                .ToList();
        }

        var info = await SchuelerInfoAsync(db, eigene.Where(e => e.SchuelerId != null).Select(e => e.SchuelerId!.Value));
        var leser = await LeserAsync(db, u);
        var sichtbar = eigene.Where(e => DarfLesen(leser, e, info)).ToList();
        var weitereDto = weitere
            .Where(e => DarfLesen(leser, e, info))
            .OrderByDescending(e => e.Datum).ThenByDescending(e => e.Stunde ?? 0).ThenByDescending(e => e.VerfasstAm)
            .Take(MaxWeitere)
            .Select(e => Dto(e, u, info))
            .ToList();
        if (titel == null)
        {
            var e0 = eigene.OrderByDescending(e => e.Datum).First();
            titel = $"{e0.Fach} · {e0.Klassen.Replace(",", ", ")}";
        }
        return ListeDto(titel, von, bis, !u.IstAdmin, false, sichtbar, u, info, weitereDto,
            sekI.OrderBy(k => k, Cmp).ToList());
    }

    /// <summary>„M · 05A“ und die Sek-I-Klassen eines Unterrichts laut heute gültigem Grundplan.</summary>
    private static async Task<(string? Titel, HashSet<string> SekI)> UnterrichtInfoAsync(AppDbContext db, int unr, string lehrer)
    {
        var sekI = new HashSet<string>(Cmp);
        var ver = await KlassenbuchService.AktuelleVersionAsync(db);
        if (ver == null) return (null, sekI);
        var slots = (await db.GrundplanSlots.AsNoTracking()
                .Where(s => s.VersionId == ver.Id && s.UNr == unr)
                .ToListAsync())
            .Where(s => KlassenbuchService.Gleich(s.Lehrer, lehrer)
                        && string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (slots.Count == 0) return (null, sekI);
        var klassen = slots.Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0)
            .Distinct(Cmp).OrderBy(k => k, Cmp).ToList();
        foreach (var k in klassen.Where(k => KlassenMuster.IsMatch(k))) sekI.Add(k);
        string fach = slots.GroupBy(s => s.Fach.Trim(), Cmp).OrderByDescending(f => f.Count()).First().Key;
        return ($"{fach} · {string.Join(", ", klassen)}", sekI);
    }

    private static KeListeDto ListeDto(
        string titel, DateOnly von, DateOnly bis, bool nurEigene, bool darfOhneStunde,
        List<KlassenbuchEintrag> eintraege, KbNutzer u, IReadOnlyDictionary<int, SInfo> info,
        IReadOnlyList<KeEintragDto>? weitere, IReadOnlyList<string>? weitereKlassen)
    {
        var zaehler = ZaehlerReihenfolge
            .Select(k => new KeZaehlerDto(k.ToString(), eintraege.Count(e => e.Kategorie == k)))
            .Where(z => z.Anzahl > 0)
            .ToList();
        var sortiert = eintraege
            .OrderByDescending(e => e.Datum).ThenByDescending(e => e.Stunde ?? 0).ThenByDescending(e => e.VerfasstAm)
            .ToList();
        var dtos = sortiert.Take(MaxListe).Select(e => Dto(e, u, info)).ToList();
        return new KeListeDto(titel, von, bis, nurEigene, darfOhneStunde, zaehler, dtos,
            sortiert.Count > dtos.Count, weitere, weitereKlassen);
    }

    // =================================================================
    //  Prüfen, Löschfrist, Hilfen
    // =================================================================

    private static (KbEintragKategorie Kategorie, string? Text, string? Fehler) Pruefen(
        KbEintragArt art, string? kategorie, string? text)
    {
        if (!Enum.TryParse(kategorie?.Trim(), true, out KbEintragKategorie k) || !Enum.IsDefined(k))
            return (default, null, "Bitte eine Kategorie wählen.");
        var erlaubt = art == KbEintragArt.Klasse ? KlassenKategorien : SchuelerKategorien;
        if (!erlaubt.Contains(k))
            return (default, null, art == KbEintragArt.Klasse
                ? $"„{KategorieText(k)}“ gibt es nur für Schülereinträge."
                : $"„{KategorieText(k)}“ gibt es nur für Klasseneinträge.");
        var t = UnterrichtInhaltService.Normalisiert(text, mehrzeilig: true);
        if (t != null && t.Length > MaxText) return (k, null, $"Der Text ist zu lang (höchstens {MaxText} Zeichen).");
        if (t == null && TextPflicht(k)) return (k, null, $"Bitte bei „{KategorieText(k)}“ kurz sachlich beschreiben, worum es geht.");
        return (k, t, null);
    }

    /// <summary>
    /// Löschfrist (Entscheidung 39, vorbereitet – Standard aus): Einträge, deren Datum länger als
    /// <paramref name="tage"/> Tage zurückliegt, samt ihrer Historie löschen. 0 = nichts löschen.
    /// </summary>
    public static int Aufraeumen(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = KlassenbuchService.Heute().AddDays(-tage);
        var zeit = DateTime.UtcNow.AddDays(-tage);
        var ids = db.KlassenbuchEintraege.Where(e => e.Datum < grenze).Select(e => e.Id).ToList();
        foreach (var teil in ids.Chunk(500))
            db.KlassenbuchProtokolle.Where(p => p.EintragId != null && teil.Contains(p.EintragId.Value)).ExecuteDelete();
        db.KlassenbuchProtokolle.Where(p => (p.Art == "Eintrag" || p.Art == "EintragGeloescht") && p.Zeit < zeit).ExecuteDelete();
        return db.KlassenbuchEintraege.Where(e => e.Datum < grenze).ExecuteDelete();
    }

    private static List<string> Liste(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
