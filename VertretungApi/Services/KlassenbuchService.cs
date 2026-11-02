using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Klassenbuch (Phase 3.1): Stundenblatt, Anwesenheit, Änderungshistorie,
//  Gruppenlisten.
//
//  Lektion = effektive Stunde eines Tages (WochenplanService.TagAsync),
//  Schlüssel (Datum, Stunde, GrundUNr, GrundLehrer). Teilnehmer je Quelle:
//    Klasse ohne Schülergruppe -> alle Schüler der Klasse,
//    Klasse mit Schülergruppe  -> Mitglieder der zusammengestellten Gruppe
//                                 (sonst „offen": erst zusammenstellen),
//    Kurs                      -> Mitglieder des Kurses,
//    Sonstige (ohne Klassen)   -> keine Schüler, kein Klassenbuch.
//  Schüler einer Klasse, die an dem Tag klassenbezogen frei hat (z. B.
//  Klassenfahrt), stehen nicht in der Liste.
//
//  Rechte (Entscheidungen 01.11.2026):
//    Lesen und Schreiben: haltende Lehrkraft (auch Vertretung/EVA/Verlegung),
//    ursprüngliche Lehrkraft, Klassenleitung/Stufenkoordination (zuständig
//    für eine Klasse/Stufe der Stunde), Admin. Keine Erfassung für künftige
//    Tage. Keine Änderungsfrist; jede Änderung landet in der Historie.
//    Gruppen zusammenstellen: Lehrkräfte der Gruppe laut Grundplan + Admin;
//    ansehen zusätzlich Klassenleitung/Stufenkoordination der Pool-Klassen.
//
//  Festgelegt (Nachtrag 3.2, Nutzer 02.11.2026): Eine Fehlstunde/Verspätung,
//  die Klassenleitung/Stufenkoordination/Admin EINZELN bewertet haben, kann
//  nur noch von ihnen geändert werden – auch nicht über die Anwesenheit
//  (z. B. auf „anwesend"). Über einen Entschuldigungs-Zeitraum entschuldigte
//  Stunden bleiben für die Lehrkraft änderbar.
//
//  Doppelstunde (Nachtrag 3.1, Nutzer 02.11.2026): Folgt direkt dieselbe
//  Stunde (gleiche UNr + Lehrkraft laut Grundplan), kann die Erfassung der
//  1. Stunde beim Speichern in die 2. übernommen werden – „verspätet" wird
//  dort zu „anwesend"; eine schon erfasste 2. Stunde wird nie überschrieben.
// =====================================================================

/// <summary>Angemeldete Person (aus der Datenbank, nicht nur aus dem Token).</summary>
public sealed record KbNutzer(string Name, BenutzerRolle Rolle, string? Kuerzel, IReadOnlyList<string> Zustaendig)
{
    public bool IstAdmin => Rolle == BenutzerRolle.Admin;
    public bool IstKlassenleitungOderKoordination =>
        Rolle is BenutzerRolle.Klassenlehrer or BenutzerRolle.Stufenkoordination;
}

/// <summary>Sek-I-Gruppe laut Grundplan (Slots mit dieser Schülergruppe und Klassenangabe).</summary>
public sealed record GrundplanGruppe(
    string Schluessel, string Fach, string Schuljahr, IReadOnlyList<string> Klassen, IReadOnlyList<string> Lehrer);

/// <summary>Ergebnis einer schreibenden Aktion: 200 = gespeichert, sonst HTTP-Status + Text.</summary>
public sealed record KbErgebnis(int Status, string? Fehler = null, bool Konflikt = false, string? Meldung = null)
{
    public static readonly KbErgebnis Ok = new(200);
}

public static class KlassenbuchService
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    internal static readonly StringComparer NamenCmp = DeutscheSortierung();

    /// <summary>Namen deutsch sortieren (Ö bei O); ohne Sprachdaten auf dem Server ordinal.</summary>
    private static StringComparer DeutscheSortierung()
    {
        try { return StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), CompareOptions.IgnoreCase); }
        catch (CultureNotFoundException) { return StringComparer.OrdinalIgnoreCase; }
    }

    /// <summary>Höchstens so viele Minuten Verspätung.</summary>
    public const int MaxMinuten = 240;

    internal sealed record Teilnehmer(int Id, string Vorname, string Nachname, string? Klasse, bool IstPseudo);

    public static DateOnly Heute() => DateOnly.FromDateTime(DateTime.Now);

    // =================================================================
    //  Rechte
    // =================================================================

    public static bool DarfStunde(KbNutzer u, PlanStunde st)
    {
        if (u.IstAdmin) return true;
        if (u.Kuerzel != null &&
            (Gleich(u.Kuerzel, st.Lehrer) || Gleich(u.Kuerzel, st.GrundLehrer) || Gleich(u.Kuerzel, st.OriginalLehrer)))
            return true;
        return u.IstKlassenleitungOderKoordination && ZustaendigFuer(u.Zustaendig, st.Klassen);
    }

    /// <summary>
    /// Zuständigkeit „05A" passt zur Klasse 05A, „Q1" zur Stufe Q1; eine Jahrgangsangabe „05"
    /// (z. B. Erprobungsstufe) passt zu allen Klassen 05….
    /// </summary>
    public static bool ZustaendigFuer(IReadOnlyList<string> zustaendig, IEnumerable<string> klassen)
        => klassen.Any(k => zustaendig.Any(z =>
            Gleich(z, k) ||
            (z.Length == 2 && char.IsDigit(z[0]) && char.IsDigit(z[1]) &&
             k.Trim().StartsWith(z, StringComparison.OrdinalIgnoreCase))));

    public static bool IstGruppenLehrer(KbNutzer u, GrundplanGruppe g)
        => u.IstAdmin || (u.Kuerzel != null && g.Lehrer.Any(l => Gleich(l, u.Kuerzel)));

    public static bool DarfGruppeSehen(KbNutzer u, GrundplanGruppe g)
        => IstGruppenLehrer(u, g) || (u.IstKlassenleitungOderKoordination && ZustaendigFuer(u.Zustaendig, g.Klassen));

    // =================================================================
    //  Stunde
    // =================================================================

    /// <summary>Stunde eines Tages über den Schlüssel der Lektion.</summary>
    public static PlanStunde? FindeStunde(TagKontext tag, int stunde, int grundUNr, string grundLehrer)
    {
        var passend = tag.Stunden
            .Where(x => x.Stunde == stunde && x.GrundUNr == grundUNr && Gleich(x.GrundLehrer, grundLehrer))
            .ToList();
        return passend.FirstOrDefault(x => WochenplanService.KlassenbuchMoeglich(x.Art, x.Status, x.Quellen))
               ?? passend.FirstOrDefault();
    }

    /// <summary>null = die Stunde hat ein Klassenbuch; sonst der Grund, warum nicht.</summary>
    public static string? KeinKlassenbuchGrund(PlanStunde st)
    {
        if (!string.Equals(st.Art, "Unterricht", StringComparison.OrdinalIgnoreCase))
            return "Das ist kein Unterricht – dafür gibt es kein Klassenbuch.";
        switch (st.Status)
        {
            case "Entfall": return "Die Stunde entfällt – dafür gibt es kein Klassenbuch.";
            case "VerlegtWeg": return "Die Stunde wurde verlegt – das Klassenbuch steht bei der neuen Stunde.";
            case "Frei": return "Unterrichtsfrei – dafür gibt es kein Klassenbuch.";
        }
        if (!WochenplanService.KlassenbuchMoeglich(st.Art, st.Status, st.Quellen))
            return "Diese Gruppe hat keine Schülerliste und kein Klassenbuch.";
        return null;
    }

    /// <summary>
    /// Doppelstunde: die direkt folgende Stunde desselben Unterrichts (gleiche UNr und Lehrkraft
    /// laut Grundplan) – nur, wenn sie ein Klassenbuch hat (nicht entfallen, nicht verlegt).
    /// </summary>
    public static PlanStunde? Folgestunde(TagKontext tag, PlanStunde st)
    {
        if (st.GrundUNr <= 0) return null;
        var f = FindeStunde(tag, st.Stunde + 1, st.GrundUNr, st.GrundLehrer);
        return f != null && WochenplanService.KlassenbuchMoeglich(f.Art, f.Status, f.Quellen) ? f : null;
    }

    /// <summary>Stundenblatt: Kopf, Rechte, Erfassungsstand und Teilnehmer mit ihrem Status.</summary>
    public static async Task<KbStundeDto> StundenblattAsync(
        AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st, string? meldung = null)
    {
        var (liste, offen, hinweise) = await TeilnehmerAsync(db, u, tag, st);
        var lektion = await LektionQuery(db, tag.Datum, st).AsNoTracking()
            .Include(l => l.Anwesenheiten)
            .FirstOrDefaultAsync();
        var eintraege = lektion?.Anwesenheiten.ToDictionary(a => a.SchuelerId) ?? new Dictionary<int, Anwesenheit>();

        var teilnehmer = new List<KbTeilnehmerDto>();
        var inListe = new HashSet<int>();
        foreach (var t in liste)
        {
            inListe.Add(t.Id);
            eintraege.TryGetValue(t.Id, out var a);
            bool fest = a != null && EinzelnFestgelegt(a);
            teilnehmer.Add(new KbTeilnehmerDto(t.Id, t.Vorname, t.Nachname, t.Klasse,
                (a?.Status ?? AnwesenheitStatus.Anwesend).ToString(), a?.Minuten, false,
                a == null ? null : BewertungText(a),
                fest, fest ? a!.BewertetVon : null,
                fest && !FehlzeitenService.IstLeitungFuer(u, t.Klasse, null)));
        }

        // Einträge von Schülern, die nicht (mehr) zur Liste gehören (z. B. Gruppe geändert):
        // bleiben sichtbar und änderbar.
        var ausserhalb = eintraege.Keys.Where(id => !inListe.Contains(id)).ToList();
        if (ausserhalb.Count > 0)
        {
            var personen = await db.Schueler.AsNoTracking()
                .Where(s => ausserhalb.Contains(s.Id))
                .Select(s => new { s.Id, s.Vorname, s.Nachname, s.Stufe })
                .ToListAsync();
            var klassen = await KlassenAsync(db, ausserhalb, tag.Schuljahr);
            foreach (var p in personen)
            {
                var a = eintraege[p.Id];
                string? kl = klassen.TryGetValue(p.Id, out var k) ? k : null;
                bool fest = EinzelnFestgelegt(a);
                teilnehmer.Add(new KbTeilnehmerDto(p.Id, p.Vorname, p.Nachname,
                    kl ?? p.Stufe, a.Status.ToString(), a.Minuten, true,
                    BewertungText(a),
                    fest, fest ? a.BewertetVon : null,
                    fest && !FehlzeitenService.IstLeitungFuer(u, kl, p.Stufe)));
            }
        }

        teilnehmer = teilnehmer
            .OrderBy(t => t.Nachname, NamenCmp).ThenBy(t => t.Vorname, NamenCmp).ThenBy(t => t.Id)
            .ToList();

        string? sperre = null;
        if (tag.Datum > Heute())
            sperre = "Die Anwesenheit kann erst ab dem Tag der Stunde erfasst werden.";
        else if (offen.Count > 0)
            sperre = offen.Count == 1
                ? "Die Gruppe ist noch nicht zusammengestellt."
                : "Die Gruppen sind noch nicht zusammengestellt.";

        bool darfSchreiben = DarfStunde(u, st) && sperre == null;

        // Doppelstunde: nur anbieten, wer auch die 2. Stunde schreiben darf.
        KbFolgeDto? folgeDto = null;
        var folge = darfSchreiben ? Folgestunde(tag, st) : null;
        if (folge != null && DarfStunde(u, folge))
        {
            var fl = await LektionQuery(db, tag.Datum, folge).AsNoTracking()
                .Select(l => new { l.ErfasstVon, l.ErfasstAm })
                .FirstOrDefaultAsync();
            folgeDto = new KbFolgeDto(folge.Stunde, fl != null, fl?.ErfasstVon, fl?.ErfasstAm);
        }

        return new KbStundeDto(
            tag.Datum, st.Stunde, st.GrundUNr, st.GrundLehrer, st.UNr, st.Fach, st.Lehrer, st.OriginalLehrer,
            st.Raum, st.Status, st.Info, st.Klassen, st.Gruppen,
            darfSchreiben, sperre,
            lektion?.Version ?? 0, lektion?.ErfasstVon, lektion?.ErfasstAm, lektion?.GeaendertVon, lektion?.GeaendertAm,
            teilnehmer, offen, hinweise, folgeDto, meldung);
    }

    /// <summary>
    /// Speichert alle Abweichungen einer Lektion. Wer nicht in <c>Eintraege</c> steht (oder dort
    /// „Anwesend" hat), war anwesend. Die Version muss zum geladenen Stundenblatt passen – sonst
    /// hat inzwischen jemand anderes gespeichert (409, Konflikt).
    /// </summary>
    public static async Task<KbErgebnis> SpeichernAsync(
        AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st, KbSpeichernDto dto)
    {
        if (tag.Datum > Heute())
            return new KbErgebnis(400, "Die Anwesenheit kann erst ab dem Tag der Stunde erfasst werden.");

        var (liste, offen, _) = await TeilnehmerAsync(db, u, tag, st);
        if (offen.Count > 0)
            return new KbErgebnis(409, "Die Gruppe dieser Stunde ist noch nicht zusammengestellt.");

        // Neue Abweichungen lesen und prüfen.
        var neu = new Dictionary<int, (AnwesenheitStatus Status, int? Minuten)>();
        foreach (var e in dto.Eintraege ?? new List<KbEintragDto>())
        {
            if (!Enum.TryParse(e.Status?.Trim(), true, out AnwesenheitStatus status) || !Enum.IsDefined(status))
                return new KbErgebnis(400, $"Unbekannter Status „{e.Status}“.");
            if (status == AnwesenheitStatus.Anwesend)
            {
                neu.Remove(e.SchuelerId);
                continue;
            }
            int? minuten = null;
            if (status == AnwesenheitStatus.Verspaetet)
            {
                if (e.Minuten is not int m || m < 1 || m > MaxMinuten)
                    return new KbErgebnis(400, $"Bitte bei „verspätet“ die Minuten angeben (1–{MaxMinuten}).");
                minuten = m;
            }
            neu[e.SchuelerId] = (status, minuten);
        }

        var ergebnis = await SpeichereLektionAsync(db, u, tag, st, liste, neu, dto.Version, null);
        if (ergebnis.Status != 200 || !dto.FolgestundeUebernehmen) return ergebnis;

        // ---- Doppelstunde: Erfassung in die folgende Stunde übernehmen ----
        var folge = Folgestunde(tag, st);
        if (folge == null || !DarfStunde(u, folge))
            return ergebnis with { Meldung = "Gespeichert. Die folgende Stunde konnte nicht mit erfasst werden." };

        var (fListe, fOffen, _) = await TeilnehmerAsync(db, u, tag, folge);
        if (fOffen.Count > 0)
            return ergebnis with { Meldung = $"Gespeichert. Die {folge.Stunde}. Stunde wurde nicht erfasst (Gruppe noch nicht zusammengestellt)." };

        // „Verspätet" gilt nur für die 1. Stunde; die übrigen Status werden übernommen.
        var fIds = fListe.Select(t => t.Id).ToHashSet();
        var fNeu = neu
            .Where(kv => kv.Value.Status != AnwesenheitStatus.Verspaetet && fIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Version 0 erwartet: Eine schon erfasste 2. Stunde wird nicht überschrieben.
        var f = await SpeichereLektionAsync(db, u, tag, folge, fListe, fNeu, 0, $"übernommen aus der {st.Stunde}. Stunde");
        return ergebnis with
        {
            Meldung = f.Status == 200
                ? $"Anwesenheit für die {st.Stunde}. und {folge.Stunde}. Stunde gespeichert."
                : $"Gespeichert. Die {folge.Stunde}. Stunde war schon erfasst und wurde nicht überschrieben."
        };
    }

    /// <summary>
    /// Speichert die Abweichungen <paramref name="neu"/> einer Lektion (Transaktion, Historie,
    /// Versionsprüfung). <paramref name="herkunft"/>: Vermerk in der Historie, z. B. bei Übernahme
    /// aus der 1. Stunde einer Doppelstunde.
    /// </summary>
    private static async Task<KbErgebnis> SpeichereLektionAsync(
        AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st, List<Teilnehmer> liste,
        Dictionary<int, (AnwesenheitStatus Status, int? Minuten)> neu, int version, string? herkunft)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var lektion = await LektionQuery(db, tag.Datum, st).Include(l => l.Anwesenheiten).FirstOrDefaultAsync();
            int aktuell = lektion?.Version ?? 0;
            if (version != aktuell)
            {
                string text = lektion != null && lektion.GeaendertVon != null
                    ? $"Die Anwesenheit wurde inzwischen von {lektion.GeaendertVon} gespeichert. Bitte neu laden."
                    : "Das Stundenblatt hat sich inzwischen geändert. Bitte neu laden.";
                return new KbErgebnis(409, text, true);
            }

            var erlaubt = liste.Select(t => t.Id).ToHashSet();
            if (lektion != null) erlaubt.UnionWith(lektion.Anwesenheiten.Select(a => a.SchuelerId));
            if (neu.Keys.Any(id => !erlaubt.Contains(id)))
                return new KbErgebnis(400, "Mindestens eine Schülerin bzw. ein Schüler gehört nicht zu dieser Stunde.");

            // Nachtrag 3.2: einzeln festgelegte Fehlstunden ändert nur KL/SK/Admin.
            if (lektion != null)
            {
                foreach (var f in lektion.Anwesenheiten.Where(EinzelnFestgelegt))
                {
                    var (sNeuF, mNeuF) = neu.TryGetValue(f.SchuelerId, out var nf) ? nf : (AnwesenheitStatus.Anwesend, (int?)null);
                    if (sNeuF == f.Status && mNeuF == f.Minuten) continue;
                    if (await LeitungFuerSchuelerAsync(db, u, f.SchuelerId, liste)) continue;
                    var name = await db.Schueler.AsNoTracking().Where(x => x.Id == f.SchuelerId)
                        .Select(x => x.Vorname + " " + x.Nachname).FirstOrDefaultAsync();
                    return new KbErgebnis(403,
                        $"Die Fehlstunde von {name ?? "einem Schüler"} wurde von {f.BewertetVon ?? "der Klassenleitung bzw. Stufenkoordination"} " +
                        "festgelegt. Ändern kann sie nur die Klassenleitung bzw. Stufenkoordination.");
                }
            }

            var jetzt = DateTime.UtcNow;
            bool pseudo = liste.Any(t => t.IstPseudo);
            bool neuAngelegt = lektion == null;
            if (lektion == null)
            {
                lektion = new Lektion
                {
                    Datum = tag.Datum,
                    Stunde = st.Stunde,
                    GrundUNr = st.GrundUNr,
                    GrundLehrer = st.GrundLehrer.Trim().ToUpperInvariant(),
                    Schuljahr = tag.Schuljahr,
                    ErfasstVon = u.Name,
                    ErfasstAm = jetzt,
                    IstPseudo = pseudo
                };
                db.Lektionen.Add(lektion);
                await db.SaveChangesAsync();   // Id für Anwesenheit und Historie
            }

            var alt = lektion.Anwesenheiten.ToDictionary(a => a.SchuelerId);
            var historie = new List<KlassenbuchProtokoll>();

            // Phase 3.2: Entschuldigungen, die diesen Tag abdecken – neue Fehlstunden und
            // Verspätungen gelten dann gleich als entschuldigt.
            var neuIds = neu.Keys.ToList();
            var abdeckend = neuIds.Count == 0
                ? new Dictionary<int, Entschuldigung>()
                : (await db.Entschuldigungen.AsNoTracking()
                        .Where(e => neuIds.Contains(e.SchuelerId) && e.Von <= tag.Datum && e.Bis >= tag.Datum)
                        .ToListAsync())
                    .GroupBy(e => e.SchuelerId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.ErfasstAm).First());
            if (neuAngelegt)
            {
                string text = neu.Count == 0 ? "alle anwesend" : $"erfasst, {neu.Count} Abweichung(en)";
                historie.Add(Protokoll(u, "Erfasst", lektion, null, null,
                    herkunft == null ? text : $"{herkunft}: {text}"));
            }

            foreach (var id in alt.Keys.Union(neu.Keys).ToList())
            {
                alt.TryGetValue(id, out var a);
                var (sAlt, mAlt) = a == null ? (AnwesenheitStatus.Anwesend, (int?)null) : (a.Status, a.Minuten);
                bool hatNeu = neu.TryGetValue(id, out var n);
                var (sNeu, mNeu) = hatNeu ? n : (AnwesenheitStatus.Anwesend, (int?)null);
                if (sAlt == sNeu && mAlt == mNeu) continue;

                historie.Add(Protokoll(u, "Anwesenheit", lektion, id,
                    neuAngelegt ? null : StatusText(sAlt, mAlt), StatusText(sNeu, mNeu)));

                if (!hatNeu)
                {
                    if (a != null) db.Anwesenheiten.Remove(a);
                }
                else if (a != null)
                {
                    a.Status = sNeu;
                    a.Minuten = mNeu;
                    a.GeaendertVon = u.Name;
                    a.GeaendertAm = jetzt;
                    if (a.Bewertung == FehlBewertung.Offen && a.EntschuldigungId == null)
                        ZeitraumAnwenden(a, abdeckend);
                }
                else
                {
                    var neueA = new Anwesenheit
                    {
                        LektionId = lektion.Id,
                        SchuelerId = id,
                        Status = sNeu,
                        Minuten = mNeu,
                        GeaendertVon = u.Name,
                        GeaendertAm = jetzt
                    };
                    ZeitraumAnwenden(neueA, abdeckend);
                    db.Anwesenheiten.Add(neueA);
                }
            }

            // Nichts geändert (erneut „Speichern" ohne Änderung): Version bleibt.
            if (!neuAngelegt && historie.Count == 0)
            {
                await tx.CommitAsync();
                return KbErgebnis.Ok;
            }

            if (lektion.ErfasstAm == null)
            {
                lektion.ErfasstVon = u.Name;
                lektion.ErfasstAm = jetzt;
            }
            // Momentaufnahme der Stunde (für spätere Auswertungen).
            lektion.UNr = st.UNr;
            lektion.Fach = st.Fach;
            lektion.Lehrer = st.Lehrer;
            lektion.Status = st.Status;
            lektion.Lerngruppen = string.Join(",", st.Gruppen);
            lektion.Klassen = string.Join(",", st.Klassen);
            lektion.GeaendertVon = u.Name;
            lektion.GeaendertAm = jetzt;
            lektion.Version = aktuell + 1;
            lektion.IstPseudo = lektion.IstPseudo || pseudo;
            foreach (var p in historie) p.IstPseudo = lektion.IstPseudo;

            db.KlassenbuchProtokolle.AddRange(historie);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return KbErgebnis.Ok;
        }
        catch (DbUpdateException)
        {
            // Zwei Personen haben dieselbe Lektion gleichzeitig zum ersten Mal gespeichert.
            db.ChangeTracker.Clear();
            return new KbErgebnis(409, "Das Stundenblatt wurde gerade von jemand anderem gespeichert. Bitte neu laden.", true);
        }
    }

    /// <summary>Änderungshistorie einer Lektion, neueste zuerst.</summary>
    public static async Task<List<KbHistorieDto>> HistorieAsync(AppDbContext db, TagKontext tag, PlanStunde st)
    {
        var lektion = await LektionQuery(db, tag.Datum, st).AsNoTracking().FirstOrDefaultAsync();
        if (lektion == null) return new List<KbHistorieDto>();

        var eintraege = await db.KlassenbuchProtokolle.AsNoTracking()
            .Where(p => p.LektionId == lektion.Id)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(500)
            .ToListAsync();
        var ids = eintraege.Where(p => p.SchuelerId != null).Select(p => p.SchuelerId!.Value).Distinct().ToList();
        var namen = await db.Schueler.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Vorname + " " + s.Nachname);

        return eintraege.Select(p => new KbHistorieDto(
                p.Zeit, p.Akteur, p.Art,
                p.SchuelerId is int sid ? (namen.TryGetValue(sid, out var n) ? n : $"Schüler {sid}") : null,
                p.Alt, p.Neu))
            .ToList();
    }

    /// <summary>
    /// Von Klassenleitung/Stufenkoordination/Admin einzeln bewertet (nicht über einen Zeitraum):
    /// Die Fach- bzw. Kurslehrkraft kann die Zeile nicht mehr ändern.
    /// </summary>
    public static bool EinzelnFestgelegt(Anwesenheit a)
        => a.Festgelegt && a.EntschuldigungId == null
           && a.Status is AnwesenheitStatus.Fehlt or AnwesenheitStatus.Verspaetet;

    /// <summary>Ist die angemeldete Person Klassenleitung/Stufenkoordination dieses Schülers (oder Admin)?</summary>
    private static async Task<bool> LeitungFuerSchuelerAsync(AppDbContext db, KbNutzer u, int schuelerId, List<Teilnehmer> liste)
    {
        if (u.IstAdmin) return true;
        if (!u.IstKlassenleitungOderKoordination) return false;
        var t = liste.FirstOrDefault(x => x.Id == schuelerId);
        if (t != null && FehlzeitenService.IstLeitungFuer(u, t.Klasse, null)) return true;
        var stufe = await db.Schueler.AsNoTracking().Where(x => x.Id == schuelerId).Select(x => x.Stufe).FirstOrDefaultAsync();
        var klassen = await KlassenAsync(db, new List<int> { schuelerId }, "");
        return FehlzeitenService.IstLeitungFuer(u, klassen.TryGetValue(schuelerId, out var k) ? k : null, stufe);
    }

    /// <summary>Fehlstunde/Verspätung, die ein Entschuldigungs-Zeitraum abdeckt: als entschuldigt markieren.</summary>
    private static void ZeitraumAnwenden(Anwesenheit a, Dictionary<int, Entschuldigung> abdeckend)
    {
        if (a.Status is not (AnwesenheitStatus.Fehlt or AnwesenheitStatus.Verspaetet)) return;
        if (!abdeckend.TryGetValue(a.SchuelerId, out var e)) return;
        a.Bewertung = FehlBewertung.Entschuldigt;
        a.Grund = e.Grund;
        a.EntschuldigungId = e.Id;
        a.BewertetVon = e.ErfasstVon;
        a.BewertetAm = DateTime.UtcNow;
        a.Festgelegt = true;   // Zeitraum-Entschuldigungen tragen nur KL/SK/Admin ein
    }

    /// <summary>
    /// Bewertung für die Anzeige: Beurlaubt = entschuldigt, schulische Veranstaltung = keine
    /// Fehlzeit (null); sonst die gespeicherte Bewertung (Offen | Entschuldigt | Unentschuldigt).
    /// </summary>
    public static string? BewertungText(Anwesenheit a) => a.Status switch
    {
        AnwesenheitStatus.Beurlaubt => FehlBewertung.Entschuldigt.ToString(),
        AnwesenheitStatus.Fehlt or AnwesenheitStatus.Verspaetet => a.Bewertung.ToString(),
        _ => null
    };

    public static string StatusText(AnwesenheitStatus s, int? minuten) => s switch
    {
        AnwesenheitStatus.Fehlt => "fehlt",
        AnwesenheitStatus.Verspaetet => minuten is int m ? $"verspätet ({m} Min.)" : "verspätet",
        AnwesenheitStatus.Beurlaubt => "beurlaubt",
        AnwesenheitStatus.Schulveranstaltung => "schulische Veranstaltung",
        _ => "anwesend"
    };

    // =================================================================
    //  Teilnehmer
    // =================================================================

    internal static async Task<(List<Teilnehmer> Liste, List<KbOffeneGruppeDto> Offen, List<string> Hinweise)>
        TeilnehmerAsync(AppDbContext db, KbNutzer u, TagKontext tag, PlanStunde st)
    {
        var hinweise = new List<string>();
        var offen = new List<KbOffeneGruppeDto>();
        var lerngruppen = await db.Lerngruppen.AsNoTracking().ToListAsync();

        Lerngruppe? Finde(LerngruppeTyp typ, string key)
            => Bevorzugt(lerngruppen.Where(l => l.Typ == typ && Gleich(l.Bezeichnung, key)), tag.Schuljahr);

        var lgIds = new HashSet<int>();
        var offeneSchluessel = new List<string>();
        foreach (var q in st.Quellen)
        {
            if (q.Typ == "Klasse" && q.Schuelergruppe != null)
            {
                var g = Finde(LerngruppeTyp.Gruppe, q.Schuelergruppe);
                if (g != null) lgIds.Add(g.Id);
                else if (!offeneSchluessel.Contains(q.Schuelergruppe, Cmp)) offeneSchluessel.Add(q.Schuelergruppe);
            }
            else if (q.Typ == "Klasse")
            {
                var k = Finde(LerngruppeTyp.Klasse, q.Key);
                if (k != null) lgIds.Add(k.Id);
            }
            else if (q.Typ == "Kurs")
            {
                var k = Finde(LerngruppeTyp.Kurs, q.Key);
                if (k != null) lgIds.Add(k.Id);
            }
            // Sonstige (ohne Klassenangabe): keine Schüler.
        }

        foreach (var sg in offeneSchluessel)
        {
            var g = await GruppeAusGrundplanAsync(db, sg, tag.VersionId);
            var klassen = g?.Klassen ?? st.Quellen
                .Where(q => Gleich(q.Schuelergruppe, sg)).Select(q => q.Key).Distinct(Cmp).ToList();
            var lehrer = g?.Lehrer ?? new List<string> { st.GrundLehrer };
            offen.Add(new KbOffeneGruppeDto(g?.Schluessel ?? sg, g?.Fach ?? st.Fach, klassen, lehrer,
                g != null && IstGruppenLehrer(u, g)));
        }

        var mitglieder = lgIds.Count == 0
            ? new List<Teilnehmer>()
            : (await db.Zugehoerigkeiten.AsNoTracking()
                    .Where(z => lgIds.Contains(z.LerngruppeId))
                    .Select(z => new { z.SchuelerId, z.Schueler!.Vorname, z.Schueler.Nachname, z.Schueler.Stufe, z.Schueler.IstPseudo })
                    .ToListAsync())
                .GroupBy(z => z.SchuelerId)
                .Select(g => g.First())
                .Select(z => new Teilnehmer(z.SchuelerId, z.Vorname, z.Nachname, z.Stufe, z.IstPseudo))
                .ToList();

        // Klasse (Sek I) bzw. Stufe (Sek II) für Anzeige und „klassenbezogen frei".
        var klassenJeSchueler = await KlassenAsync(db, mitglieder.Select(m => m.Id).ToList(), tag.Schuljahr);
        var frei = tag.FreieKlassen.ToDictionary(f => f.Klasse, f => f.Text, Cmp);
        var ausgelassen = new Dictionary<string, (string Text, int Anzahl)>(Cmp);
        var liste = new List<Teilnehmer>();
        foreach (var m in mitglieder)
        {
            string? klasse = klassenJeSchueler.TryGetValue(m.Id, out var k) ? k : null;
            string anzeige = klasse ?? m.Klasse ?? "";   // m.Klasse enthält hier die Stufe
            string? freiSchluessel = klasse != null && frei.ContainsKey(klasse) ? klasse
                : !string.IsNullOrWhiteSpace(m.Klasse) && frei.ContainsKey(m.Klasse) ? m.Klasse
                : null;
            if (freiSchluessel != null)
            {
                var bisher = ausgelassen.TryGetValue(freiSchluessel, out var x) ? x.Anzahl : 0;
                ausgelassen[freiSchluessel] = (frei[freiSchluessel], bisher + 1);
                continue;
            }
            liste.Add(m with { Klasse = anzeige.Length > 0 ? anzeige : null });
        }

        foreach (var (kl, info) in ausgelassen.OrderBy(a => a.Key, Cmp))
            hinweise.Add($"{kl} hat heute frei ({info.Text}) – {info.Anzahl} Schüler stehen deshalb nicht in der Liste.");
        if (liste.Count == 0 && offen.Count == 0 && ausgelassen.Count == 0)
            hinweise.Add("Zu dieser Stunde sind keine Schülerinnen und Schüler hinterlegt (Klassen- bzw. Kurslisten fehlen).");

        return (liste, offen, hinweise);
    }

    /// <summary>Klasse (Lerngruppe Typ Klasse) je Schüler, bevorzugt im angegebenen Schuljahr.</summary>
    internal static async Task<Dictionary<int, string>> KlassenAsync(AppDbContext db, List<int> schuelerIds, string schuljahr)
    {
        if (schuelerIds.Count == 0) return new Dictionary<int, string>();
        var zeilen = await db.Zugehoerigkeiten.AsNoTracking()
            .Where(z => schuelerIds.Contains(z.SchuelerId) && z.Lerngruppe!.Typ == LerngruppeTyp.Klasse)
            .Select(z => new { z.SchuelerId, z.Lerngruppe!.Bezeichnung, z.Lerngruppe.Schuljahr })
            .ToListAsync();
        return zeilen
            .GroupBy(z => z.SchuelerId)
            .ToDictionary(g => g.Key,
                g => g.OrderByDescending(z => z.Schuljahr == schuljahr).First().Bezeichnung);
    }

    // =================================================================
    //  Gruppen (Sek-I-Teilgruppen laut Schülergruppe)
    // =================================================================

    /// <summary>
    /// Gruppe laut Grundplan: alle Unterrichts-Slots mit dieser Schülergruppe und einer Klasse
    /// (Typ Klasse). null = keine Sek-I-Gruppe (z. B. Oberstufenkurs oder unbekannt).
    /// Ohne <paramref name="versionId"/> gilt die heute gültige Grundplan-Version.
    /// </summary>
    public static async Task<GrundplanGruppe?> GruppeAusGrundplanAsync(AppDbContext db, string schluessel, int? versionId = null)
    {
        var ver = versionId is int vid
            ? await db.GrundplanVersionen.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid)
            : await AktuelleVersionAsync(db);
        if (ver == null) return null;

        string key = schluessel.Trim();
        var slots = (await GruppenSlotsAsync(db, ver.Id))
            .Where(s => Gleich(s.Schuelergruppe, key))
            .ToList();
        return slots.Count == 0 ? null : AusSlots(slots, ver.Schuljahr);
    }

    /// <summary>„Meine Gruppen": Gruppen, die die Lehrkraft unterrichtet (Admin: alle).</summary>
    public static async Task<List<KbGruppeKurzDto>> GruppenListeAsync(AppDbContext db, KbNutzer u)
    {
        var ver = await AktuelleVersionAsync(db);
        if (ver == null) return new List<KbGruppeKurzDto>();

        var gruppen = (await GruppenSlotsAsync(db, ver.Id))
            .GroupBy(s => s.Schuelergruppe!.Trim(), Cmp)
            .Select(g => AusSlots(g.ToList(), ver.Schuljahr))
            .Where(g => u.IstAdmin || IstGruppenLehrer(u, g))
            .ToList();

        var fertig = (await db.Lerngruppen.AsNoTracking()
                .Where(l => l.Typ == LerngruppeTyp.Gruppe && l.Schuljahr == ver.Schuljahr)
                .Select(l => new { l.Bezeichnung, l.BearbeitetVon, l.BearbeitetAm, Anzahl = l.Mitglieder.Count })
                .ToListAsync())
            .GroupBy(l => l.Bezeichnung, Cmp)
            .ToDictionary(g => g.Key, g => g.First(), Cmp);

        return gruppen
            .Select(g =>
            {
                fertig.TryGetValue(g.Schluessel, out var f);
                return new KbGruppeKurzDto(g.Schluessel, g.Fach, g.Klassen, g.Lehrer,
                    f != null, f?.Anzahl ?? 0, f?.BearbeitetVon, f?.BearbeitetAm);
            })
            .OrderBy(g => g.Zusammengestellt)          // offene zuerst
            .ThenBy(g => g.Klassen.FirstOrDefault() ?? "", Cmp)
            .ThenBy(g => g.Fach, Cmp)
            .ThenBy(g => g.Schluessel, Cmp)
            .ToList();
    }

    /// <summary>Gruppe mit Pool (alle Schüler der genannten Klassen) und Mitgliedern.</summary>
    public static async Task<KbGruppeDto> GruppeAsync(AppDbContext db, KbNutzer u, GrundplanGruppe g)
    {
        var lg = await GruppeLerngruppeAsync(db, g, mitMitgliedern: false);
        var mitglieder = lg == null
            ? new HashSet<int>()
            : (await db.Zugehoerigkeiten.AsNoTracking().Where(z => z.LerngruppeId == lg.Id)
                .Select(z => z.SchuelerId).ToListAsync()).ToHashSet();

        var pool = await PoolAsync(db, g, mitglieder);
        return new KbGruppeDto(
            g.Schluessel, g.Fach, g.Schuljahr, g.Klassen, g.Lehrer,
            lg != null, lg?.BearbeitetVon, lg?.BearbeitetAm,
            IstGruppenLehrer(u, g),
            pool.Select(p => new KbPoolDto(p.Id, p.Vorname, p.Nachname, p.Klasse ?? "", mitglieder.Contains(p.Id)))
                .OrderBy(p => p.Klasse, Cmp).ThenBy(p => p.Nachname, NamenCmp).ThenBy(p => p.Vorname, NamenCmp)
                .ThenBy(p => p.Id)
                .ToList());
    }

    /// <summary>
    /// Stellt die Gruppe zusammen (bzw. ändert sie): Mitglieder = genau <paramref name="ids"/>.
    /// Erlaubt sind Schüler des Pools und bisherige Mitglieder. Jede Änderung kommt in die Historie.
    /// </summary>
    public static async Task<KbErgebnis> GruppeSpeichernAsync(AppDbContext db, KbNutzer u, GrundplanGruppe g, List<int> ids)
    {
        var lg = await GruppeLerngruppeAsync(db, g, mitMitgliedern: true);
        var bisher = lg?.Mitglieder.Select(z => z.SchuelerId).ToHashSet() ?? new HashSet<int>();
        var pool = await PoolAsync(db, g, bisher);
        var erlaubt = pool.Select(p => p.Id).ToHashSet();
        var neu = ids.Distinct().ToHashSet();
        if (neu.Any(id => !erlaubt.Contains(id)))
            return new KbErgebnis(400, "Mindestens eine Schülerin bzw. ein Schüler gehört zu keiner Klasse dieser Gruppe.");

        var jetzt = DateTime.UtcNow;
        bool pseudo = pool.Any(p => p.IstPseudo);
        var historie = new List<KlassenbuchProtokoll>();
        if (lg == null)
        {
            string ersteKlasse = g.Klassen.FirstOrDefault() ?? "";
            lg = new Lerngruppe
            {
                Typ = LerngruppeTyp.Gruppe,
                Bezeichnung = g.Schluessel,
                Stufe = ersteKlasse.Length >= 2 && char.IsDigit(ersteKlasse[0]) && char.IsDigit(ersteKlasse[1])
                    ? ersteKlasse[..2] : "",
                Fach = g.Fach,
                LehrerKuerzel = g.Lehrer.FirstOrDefault(),
                Schuljahr = g.Schuljahr,
                IstPseudo = pseudo
            };
            db.Lerngruppen.Add(lg);
            historie.Add(GruppenProtokoll(u, g, null, null, $"zusammengestellt ({neu.Count} Schüler)", pseudo));
        }

        foreach (var z in lg.Mitglieder.Where(z => !neu.Contains(z.SchuelerId)).ToList())
        {
            db.Zugehoerigkeiten.Remove(z);
            historie.Add(GruppenProtokoll(u, g, z.SchuelerId, "Mitglied", "entfernt", pseudo));
        }
        foreach (var id in neu.Where(id => !bisher.Contains(id)))
        {
            db.Zugehoerigkeiten.Add(new Zugehoerigkeit { SchuelerId = id, Lerngruppe = lg, Schuljahr = g.Schuljahr });
            historie.Add(GruppenProtokoll(u, g, id, null, "aufgenommen", pseudo));
        }

        lg.BearbeitetVon = u.Name;
        lg.BearbeitetAm = jetzt;
        lg.Fach = g.Fach;
        lg.IstPseudo = lg.IstPseudo || pseudo;
        db.KlassenbuchProtokolle.AddRange(historie);
        await db.SaveChangesAsync();
        return KbErgebnis.Ok;
    }

    private static async Task<Lerngruppe?> GruppeLerngruppeAsync(AppDbContext db, GrundplanGruppe g, bool mitMitgliedern)
    {
        IQueryable<Lerngruppe> q = db.Lerngruppen
            .Where(l => l.Typ == LerngruppeTyp.Gruppe && l.Schuljahr == g.Schuljahr);
        if (mitMitgliedern) q = q.Include(l => l.Mitglieder);
        else q = q.AsNoTracking();
        return (await q.ToListAsync())
            .Where(l => Gleich(l.Bezeichnung, g.Schluessel))
            .OrderBy(l => l.Id)
            .FirstOrDefault();
    }

    /// <summary>Pool einer Gruppe: alle Schüler der genannten Klassen (+ bisherige Mitglieder).</summary>
    private static async Task<List<Teilnehmer>> PoolAsync(AppDbContext db, GrundplanGruppe g, ISet<int> mitglieder)
    {
        var klassenLg = (await db.Lerngruppen.AsNoTracking()
                .Where(l => l.Typ == LerngruppeTyp.Klasse)
                .ToListAsync())
            .Where(l => g.Klassen.Contains(l.Bezeichnung, Cmp))
            .GroupBy(l => l.Bezeichnung, Cmp)
            .Select(gr => Bevorzugt(gr, g.Schuljahr)!)
            .ToList();
        var lgIds = klassenLg.Select(l => l.Id).ToList();
        var namen = klassenLg.ToDictionary(l => l.Id, l => l.Bezeichnung);

        var zeilen = lgIds.Count == 0
            ? new List<Teilnehmer>()
            : (await db.Zugehoerigkeiten.AsNoTracking()
                    .Where(z => lgIds.Contains(z.LerngruppeId))
                    .Select(z => new { z.SchuelerId, z.LerngruppeId, z.Schueler!.Vorname, z.Schueler.Nachname, z.Schueler.IstPseudo })
                    .ToListAsync())
                .GroupBy(z => z.SchuelerId)
                .Select(gr => gr.First())
                .Select(z => new Teilnehmer(z.SchuelerId, z.Vorname, z.Nachname, namen[z.LerngruppeId], z.IstPseudo))
                .ToList();

        // Bisherige Mitglieder, die nicht (mehr) in einer der Klassen sind, bleiben sichtbar.
        var fehlend = mitglieder.Where(id => zeilen.All(z => z.Id != id)).ToList();
        if (fehlend.Count > 0)
        {
            var klassen = await KlassenAsync(db, fehlend, g.Schuljahr);
            var personen = await db.Schueler.AsNoTracking()
                .Where(s => fehlend.Contains(s.Id))
                .Select(s => new { s.Id, s.Vorname, s.Nachname, s.Stufe, s.IstPseudo })
                .ToListAsync();
            zeilen.AddRange(personen.Select(p => new Teilnehmer(p.Id, p.Vorname, p.Nachname,
                klassen.TryGetValue(p.Id, out var k) ? k : p.Stufe, p.IstPseudo)));
        }
        return zeilen;
    }

    /// <summary>Unterrichts-Slots einer Version mit Schülergruppe und Klassenangabe (Sek-I-Gruppen).</summary>
    private static async Task<List<GrundplanSlot>> GruppenSlotsAsync(AppDbContext db, int versionId)
        => (await db.GrundplanSlots.AsNoTracking()
                .Where(s => s.VersionId == versionId && s.Schuelergruppe != null && s.LerngruppeTyp == "Klasse")
                .ToListAsync())
            .Where(s => !string.IsNullOrWhiteSpace(s.Schuelergruppe)
                        && string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static GrundplanGruppe AusSlots(List<GrundplanSlot> slots, string schuljahr) => new(
        slots[0].Schuelergruppe!.Trim(),
        string.Join(", ", slots.Select(s => s.Fach.Trim()).Where(f => f.Length > 0).Distinct(Cmp).OrderBy(f => f, Cmp)),
        schuljahr,
        slots.Select(s => s.Klasse.Trim().ToUpperInvariant()).Where(k => k.Length > 0)
            .Distinct(Cmp).OrderBy(k => k, Cmp).ToList(),
        slots.Select(s => s.Lehrer.Trim()).Where(l => l.Length > 0)
            .Distinct(Cmp).OrderBy(l => l, Cmp).ToList());

    /// <summary>Grundplan-Version, die heute gilt (sonst die erste des Schuljahres bzw. die neueste).</summary>
    internal static async Task<GrundplanVersion?> AktuelleVersionAsync(AppDbContext db)
    {
        var heute = Heute();
        var kal = await db.Kalender.AsNoTracking()
            .Where(k => k.Start <= heute && k.Ende >= heute)
            .OrderByDescending(k => k.Id)
            .FirstOrDefaultAsync();
        if (kal != null)
        {
            var imJahr = await db.GrundplanVersionen.AsNoTracking()
                             .Where(v => v.Schuljahr == kal.Schuljahr && v.GueltigAb <= heute)
                             .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
                             .FirstOrDefaultAsync()
                         ?? await db.GrundplanVersionen.AsNoTracking()
                             .Where(v => v.Schuljahr == kal.Schuljahr)
                             .OrderBy(v => v.GueltigAb).ThenBy(v => v.Id)
                             .FirstOrDefaultAsync();
            if (imJahr != null) return imJahr;
        }
        return await db.GrundplanVersionen.AsNoTracking()
            .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync();
    }

    // =================================================================
    //  Hilfen
    // =================================================================

    private static IQueryable<Lektion> LektionQuery(AppDbContext db, DateOnly datum, PlanStunde st)
    {
        string grundLehrer = st.GrundLehrer.Trim().ToUpperInvariant();
        return db.Lektionen.Where(l => l.Datum == datum && l.Stunde == st.Stunde
                                       && l.GrundUNr == st.GrundUNr && l.GrundLehrer == grundLehrer);
    }

    private static KlassenbuchProtokoll Protokoll(
        KbNutzer u, string art, Lektion lektion, int? schuelerId, string? alt, string? neu) => new()
    {
        Zeit = DateTime.UtcNow,
        Akteur = u.Name,
        Art = art,
        LektionId = lektion.Id,
        SchuelerId = schuelerId,
        Alt = alt,
        Neu = neu
    };

    private static KlassenbuchProtokoll GruppenProtokoll(
        KbNutzer u, GrundplanGruppe g, int? schuelerId, string? alt, string? neu, bool pseudo) => new()
    {
        Zeit = DateTime.UtcNow,
        Akteur = u.Name,
        Art = "Gruppe",
        Gruppe = g.Schluessel,
        SchuelerId = schuelerId,
        Alt = alt,
        Neu = neu,
        IstPseudo = pseudo
    };

    private static Lerngruppe? Bevorzugt(IEnumerable<Lerngruppe> kandidaten, string schuljahr)
        => kandidaten
            .OrderByDescending(l => l.Schuljahr == schuljahr)
            .ThenByDescending(l => l.Id)
            .FirstOrDefault();

    internal static bool Gleich(string? a, string? b)
        => a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
