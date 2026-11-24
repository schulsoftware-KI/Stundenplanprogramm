using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P4a (5.3a, Entscheidungen 205, 206): Bereich „Jahresablauf“ (Kontomenü, nur Admin).
//
//  Karten mit Ampel – nichts geschieht von selbst. P4a: Karte „Halbjahreswechsel“, sichtbar ab 4 Wochen vor
//  „2. Halbjahr ab“ bis zum Ende des Schuljahres laut Kalender. Prüfpunkte:
//    1. Grundplan ab dem 2. Halbjahr veröffentlicht? (sonst Hinweis „es gilt weiter …“ – kein Fehler)
//    2. SchILD-Import Abschnitt 2 erfolgt? (Protokoll mit Abschnitt 2, Stichtag, Zahlen)
//    3. Offene Fehlstunden und nicht erfasste Stunden im 1. Halbjahr (Zahlen, Sprung ins Klassenbuch)
//    4. Rückweg nach SchILD für das 1. Halbjahr erzeugt? (Download-Protokoll: seit P4b grün erst mit der Meldung
//       „Datei geschrieben“ für das ganze Halbjahr, Art „SchildRueckwegDatei“; Abruf bzw. Probedatei = gelb)
//    5. Sek-I-Gruppen des Plans am Stichtag ohne Liste (nur Hinweis, Frage F: neuer Plan zum Halbjahr)
//    6. Gruppenlisten, deren Schlüssel im Plan am Stichtag nicht mehr vorkommt (bleiben für die Stunden davor)
//  P4b (210–213): Karte „Rückweg nach SchILD“ (immer sichtbar, Hinweis): Abholcode erzeugen (Knopf öffnet den Dialog in
//  der App), bisherige Übertragungen im Schuljahr.
//  P5a (215–217, 239–242): Karte „Schuljahreswechsel“ mit Jahresabschluss, Wiedereröffnen und „Neues Schuljahr
//  vorbereiten“ – Services/JahresablaufSchuljahr.cs. P5c ergänzt „Archiv und Fristen“.
//
//  „Heute“ = Heute der API (Testdatum, 221). Keine Namen – nur Zahlen, Daten und Bezeichnungen.
// =====================================================================

public static partial class Jahresablauf
{
    /// <summary>So viele Tage vor „2. Halbjahr ab“ erscheint die Karte „Halbjahreswechsel“.</summary>
    public const int VorlaufTage = 28;

    /// <summary>Art im Download-Protokoll für den Rückweg nach SchILD (P4b, Entscheidung 210).</summary>
    public const string ArtSchildRueckweg = "SchildRueckweg";

    public const string Gruen = "gruen", Gelb = "gelb", Rot = "rot", Grau = "grau";

    private const int MaxListe = 60;
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] Wochentage = { "Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag" };

    private static string D(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    private static string Tag(DateOnly d) => $"{Wochentage[(int)d.DayOfWeek]}, {D(d)}";

    public static async Task<JaUebersichtDto> UebersichtAsync(AppDbContext db, SchulWerte w, bool produktiv, string akteur)
    {
        var heute = KlassenbuchService.Heute();
        var kal = await db.Kalender.AsNoTracking().FirstOrDefaultAsync(k => k.Schuljahr == w.Schuljahr);
        var karten = new List<JaKarteDto>
        {
            await HalbjahrAsync(db, w, kal, heute, produktiv, akteur),
            await RueckwegKarteAsync(db, w, kal)
        };
        // P5a: Karte „Schuljahreswechsel“ – oben, solange sie dran ist, sonst unten.
        var schuljahr = await SchuljahrKarteAsync(db, w, heute, produktiv, akteur);
        if (schuljahr.Aktiv) karten.Insert(0, schuljahr);
        else karten.Add(schuljahr);
        return new JaUebersichtDto(heute, Testdatum.Wert != null, w.Schuljahr, kal?.Start, kal?.Ende, karten);
    }

    /// <summary>Schlechteste Ampel: rot vor gelb vor grün; nur graue Punkte = grau.</summary>
    public static string Schlechteste(IEnumerable<string> ampeln)
    {
        var l = ampeln.ToList();
        if (l.Contains(Rot)) return Rot;
        if (l.Contains(Gelb)) return Gelb;
        if (l.Contains(Gruen)) return Gruen;
        return Grau;
    }

    // -----------------------------------------------------------------
    //  Karte „Halbjahreswechsel“
    // -----------------------------------------------------------------

    private static JaKarteDto Leer(string text, DateOnly? stichtag = null, DateOnly? ab = null)
        => new("halbjahr", "Halbjahreswechsel", Grau, text, stichtag, ab, false, Array.Empty<JaPunktDto>());

    private static async Task<JaKarteDto> HalbjahrAsync(AppDbContext db, SchulWerte w, KalenderDb? kal, DateOnly heute,
        bool produktiv, string akteur)
    {
        if (kal == null)
            return Leer($"Für das Schuljahr {w.Schuljahr} gibt es in der App noch keinen Kalender (Desktop: Wocheneditor → " +
                        "„Grundplan veröffentlichen…“).");
        if (FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, w.HalbjahrAb) is not DateOnly h)
            return Leer($"Kein 2. Halbjahr: Der 1. Februar bzw. „2. Halbjahr ab“ (Einstellungen → Schuljahr) liegt nicht im " +
                        $"Schuljahr {w.Schuljahr} laut Kalender ({D(kal.Start)} – {D(kal.Ende)}).");

        var ab = h.AddDays(-VorlaufTage);
        string stichtag = $"Das 2. Halbjahr beginnt am {Tag(h)}" +
                          (w.HalbjahrAb == h ? "" : " (Standard 1. Februar – einstellbar unter Einstellungen → Schuljahr)");
        if (heute < ab)
            return Leer($"{stichtag}. Die Prüfungen zum Halbjahreswechsel erscheinen hier ab dem {D(ab)} (4 Wochen vorher).", h, ab);
        if (heute > kal.Ende)
            return Leer($"Das Schuljahr {w.Schuljahr} ist vorbei (laut Kalender bis {D(kal.Ende)}).", h, ab);

        int tage = h.DayNumber - heute.DayNumber;
        string wann = tage > 1 ? $" – in {tage} Tagen" : tage == 1 ? " – morgen" : tage == 0 ? " – heute" : $" – seit {-tage} Tagen";

        var versionen = await db.GrundplanVersionen.AsNoTracking()
            .Where(v => v.Schuljahr == w.Schuljahr)
            .Select(v => new PlanStand(v.Id, v.Bezeichnung, v.GueltigAb))
            .ToListAsync();
        var (vorher, neu, amStichtag) = Plaene(versionen, h);

        var punkte = new List<JaPunktDto>
        {
            GrundplanPunkt(vorher, neu, h),
            await ImportPunktAsync(db, w.Schuljahr, h, heute, !produktiv),
            await OffenPunktAsync(db, w, kal, h, heute, akteur),
            await RueckwegPunktAsync(db, w, kal, h),
        };
        punkte.AddRange(await GruppenPunkteAsync(db, w.Schuljahr, amStichtag, h));
        return new JaKarteDto("halbjahr", "Halbjahreswechsel", Schlechteste(punkte.Select(p => p.Ampel)), stichtag + wann + ".",
            h, ab, true, punkte);
    }

    public sealed record PlanStand(int Id, string Bezeichnung, DateOnly GueltigAb);

    /// <summary>
    /// Grundplan vor dem Stichtag (der zuletzt beginnende), der erste ab dem Stichtag und der am Stichtag gültige
    /// (wie AktuelleVersion: höchstes „gilt ab“ ≤ Stichtag, bei gleichem Datum die neueste; sonst der erste danach).
    /// </summary>
    public static (PlanStand? Vorher, PlanStand? Neu, PlanStand? AmStichtag) Plaene(IReadOnlyCollection<PlanStand> versionen, DateOnly h)
    {
        var vorher = versionen.Where(v => v.GueltigAb < h)
            .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id).FirstOrDefault();
        var neu = versionen.Where(v => v.GueltigAb >= h)
            .OrderBy(v => v.GueltigAb).ThenByDescending(v => v.Id).FirstOrDefault();
        var am = versionen.Where(v => v.GueltigAb <= h)
                     .OrderByDescending(v => v.GueltigAb).ThenByDescending(v => v.Id).FirstOrDefault()
                 ?? neu;
        return (vorher, neu, am);
    }

    private static string Plan(PlanStand p) => $"„{p.Bezeichnung}“ (ab {D(p.GueltigAb)})";

    // ---- 1. Grundplan ----

    public static JaPunktDto GrundplanPunkt(PlanStand? vorher, PlanStand? neu, DateOnly h)
    {
        const string titel = "Grundplan ab dem 2. Halbjahr";
        if (neu != null && neu.GueltigAb == h)
            return new JaPunktDto("grundplan", titel, Gruen,
                $"Veröffentlicht: {Plan(neu)}." + (vorher != null ? $" Bis zum Vortag gilt {Plan(vorher)}." : ""));
        if (neu != null)
            return new JaPunktDto("grundplan", titel, Gelb,
                $"Ein neuer Grundplan {Plan(neu)} beginnt erst nach dem Stichtag – vom {D(h)} bis zum " +
                $"{D(neu.GueltigAb.AddDays(-1))} gilt " + (vorher != null ? Plan(vorher) : "noch keiner") +
                $". Soll er zum Halbjahr gelten: im Desktop noch einmal „Grundplan veröffentlichen…“ mit „gilt ab“ {D(h)}.");
        if (vorher != null)
            return new JaPunktDto("grundplan", titel, Grau,
                $"Kein eigener Grundplan ab dem 2. Halbjahr – es gilt weiter {Plan(vorher)}. Das ist in Ordnung, wenn der " +
                $"Stundenplan bleibt. Gibt es einen neuen: im Desktop „Grundplan veröffentlichen…“ mit „gilt ab“ {D(h)}.");
        return new JaPunktDto("grundplan", titel, Gelb,
            "Für dieses Schuljahr ist noch kein Grundplan veröffentlicht (Desktop: Wocheneditor → „Grundplan veröffentlichen…“).");
    }

    // ---- 2. SchILD-Import Abschnitt 2 ----

    private static async Task<JaPunktDto> ImportPunktAsync(AppDbContext db, string sj, DateOnly h, DateOnly heute, bool pseudo)
    {
        var ziel = new[] { new JaZielDto("schildImporte", "SchILD-Importe") };
        var p = await db.SchildImportProtokolle.AsNoTracking()
            .Where(x => x.Schuljahr == sj && x.IstPseudo == pseudo && x.Abschnitt == 2)
            .OrderByDescending(x => x.Zeit).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync();
        return ImportPunkt(p == null ? null : new ImportStand(p.Id, p.Zeit, p.GiltAb, p.Neu, p.Klassenwechsel, p.Kurswechsel,
            p.Abgaenge), h, heute, pseudo, ziel);
    }

    public sealed record ImportStand(int Id, DateTime Zeit, DateOnly GiltAb, int Neu, int Klassenwechsel, int Kurswechsel, int Abgaenge);

    public static JaPunktDto ImportPunkt(ImportStand? p, DateOnly h, DateOnly heute, bool pseudo, IReadOnlyList<JaZielDto>? ziel = null)
    {
        const string titel = "SchILD-Import Abschnitt 2";
        string art = pseudo ? " (Pseudodaten)" : "";
        if (p == null)
            return heute >= h
                ? new JaPunktDto("import", titel, Rot,
                    $"Noch kein SchILD-Import für Abschnitt 2{art} – in der App gelten weiter die Klassen-, Kurs- und " +
                    "Gruppenlisten des 1. Halbjahres. Desktop: Wocheneditor → „SchILD-Daten…“ → Export des 2. Halbjahres, " +
                    $"Abschnitt 2, „Änderungen gelten ab“ {D(h)}.", Ziele: ziel)
                : new JaPunktDto("import", titel, Gelb,
                    $"Noch offen{art}: nach dem Halbjahreswechsel in SchILD im Desktop „SchILD-Daten…“ mit Abschnitt 2 und " +
                    $"„Änderungen gelten ab“ {D(h)} importieren.", Ziele: ziel);
        var am = DateOnly.FromDateTime(DateTime.SpecifyKind(p.Zeit, DateTimeKind.Utc).ToLocalTime());
        string zahlen = $"neu {p.Neu}, Klassenwechsel {p.Klassenwechsel}, Kurs-/Gruppenwechsel {p.Kurswechsel}, Abgänge {p.Abgaenge}";
        if (p.GiltAb == h)
            return new JaPunktDto("import", titel, Gruen,
                $"Import Nr. {p.Id} vom {D(am)}{art}, gilt ab {D(p.GiltAb)}: {zahlen}.", Ziele: ziel);
        return new JaPunktDto("import", titel, Gelb,
            $"Import Nr. {p.Id} vom {D(am)}{art} gilt ab {D(p.GiltAb)} – nicht ab dem 2. Halbjahr ({D(h)}): {zahlen}. " +
            (p.GiltAb < h
                ? "Damit gelten die Wechsel schon im 1. Halbjahr. Bitte prüfen."
                : $"Vom {D(h)} bis zum {D(p.GiltAb.AddDays(-1))} gelten noch die Listen des 1. Halbjahres."), Ziele: ziel);
    }

    // ---- 3. Offene Fehlstunden, nicht erfasste Stunden im 1. Halbjahr ----

    private static async Task<JaPunktDto> OffenPunktAsync(AppDbContext db, SchulWerte w, KalenderDb kal, DateOnly h,
        DateOnly heute, string akteur)
    {
        var bis = h.AddDays(-1);
        var bisOffen = bis < heute ? bis : heute;
        int fehlt = 0, versp = 0;
        if (bisOffen >= kal.Start)
        {
            fehlt = await db.Anwesenheiten.CountAsync(a => a.Lektion!.Datum >= kal.Start && a.Lektion.Datum <= bisOffen
                                                          && a.Status == AnwesenheitStatus.Fehlt && a.Bewertung == FehlBewertung.Offen);
            versp = await db.Anwesenheiten.CountAsync(a => a.Lektion!.Datum >= kal.Start && a.Lektion.Datum <= bisOffen
                                                          && a.Status == AnwesenheitStatus.Verspaetet && a.Bewertung == FehlBewertung.Offen);
        }
        var admin = new KbNutzer(akteur, BenutzerRolle.Admin, null, Array.Empty<string>());
        var ne = await FehlzeitenService.NichtErfasstAsync(db, admin, "alle", null, null, w.ErfassungAb, w.ThemaAb, "alle",
            kal.Start, bis);
        return OffenPunkt(fehlt, versp, ne.OhneAnwesenheit, ne.OhneThema, kal.Start, bis, w.ErfassungAb);
    }

    public static JaPunktDto OffenPunkt(int fehlt, int versp, int ohneAnwesenheit, int ohneThema, DateOnly von, DateOnly bis,
        DateOnly erfassungAb)
    {
        const string titel = "Offene Fehlstunden und nicht erfasste Stunden im 1. Halbjahr";
        var ziele = new[]
        {
            new JaZielDto("nichtErfasst", "Nicht erfasst"),
            new JaZielDto("offeneFaelle", "Offene Fälle (Klassen)")
        };
        if (fehlt + versp + ohneAnwesenheit + ohneThema == 0)
            return new JaPunktDto("offen", titel, Gruen,
                $"Keine offenen Fehlstunden oder Verspätungen und keine nicht erfassten Stunden ({D(von)} – {D(bis)}, " +
                $"erfasst ab {D(erfassungAb)}).", Ziele: ziele);
        return new JaPunktDto("offen", titel, Gelb,
            $"Offen (noch nicht entschuldigt oder unentschuldigt): {fehlt} Fehlstunden, {versp} Verspätungen. Nicht erfasst: " +
            $"{ohneAnwesenheit} Stunden ohne Anwesenheit, {ohneThema} ohne Thema ({D(von)} – {D(bis)}, Anwesenheit erfasst ab " +
            $"{D(erfassungAb)}). Offene Stunden kommen nicht in den Rückweg nach SchILD.", Ziele: ziele);
    }

    // ---- 4. Rückweg nach SchILD ----

    private static async Task<JaPunktDto> RueckwegPunktAsync(AppDbContext db, SchulWerte w, KalenderDb kal, DateOnly h)
    {
        var bis = h.AddDays(-1);
        var arten = SchildRueckweg.Arten;
        var liste = await db.DownloadProtokolle.AsNoTracking()
            .Where(p => arten.Contains(p.Art) && p.Von <= bis && p.Bis >= kal.Start)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Select(p => new RueckwegStand(p.Zeit, p.Akteur, p.Von, p.Bis, p.Art))
            .ToListAsync();
        var noetigAb = w.ErfassungAb > kal.Start ? w.ErfassungAb : kal.Start;
        return RueckwegPunkt(liste, noetigAb, bis);
    }

    /// <param name="Art">„SchildRueckweg“ (abgeholt), „SchildRueckwegDatei“ (vollständige Datei), „SchildRueckwegProbe“.</param>
    public sealed record RueckwegStand(DateTime Zeit, string Akteur, DateOnly Von, DateOnly Bis, string Art = ArtSchildRueckweg);

    private static string ArtText(string art) => art switch
    {
        SchildRueckweg.ArtDatei => "Datei",
        SchildRueckweg.ArtProbe => "Probedatei",
        _ => "abgeholt"
    };

    /// <summary>
    /// P4b: grün erst, wenn der Desktop eine vollständige Datei für das ganze 1. Halbjahr gemeldet hat; ein Abruf ohne Meldung
    /// bzw. eine Probedatei ist gelb (ob die Datei in SchILD eingelesen wurde, weiß die App nicht).
    /// </summary>
    /// <param name="halbjahr">P5a: „1. Halbjahr“, „2. Halbjahr“ bzw. „Schuljahr“ (Titel und Text).</param>
    public static JaPunktDto RueckwegPunkt(IReadOnlyList<RueckwegStand> liste, DateOnly noetigAb, DateOnly bis,
        string halbjahr = "1. Halbjahr")
    {
        string titel = $"Rückweg nach SchILD für das {halbjahr}";
        var ziel = new[]
        {
            new JaZielDto("rueckweg", "Fehlstunden für SchILD bereitstellen…"),
            new JaZielDto("downloads", "Download-Protokoll")
        };
        var ganz = liste.FirstOrDefault(p => p.Art == SchildRueckweg.ArtDatei && p.Von <= noetigAb && p.Bis >= bis);
        if (ganz != null)
        {
            var am = DateOnly.FromDateTime(DateTime.SpecifyKind(ganz.Zeit, DateTimeKind.Utc).ToLocalTime());
            return new JaPunktDto("rueckweg", titel, Gruen,
                $"Datei geschrieben am {D(am)} (Abholcode von {ganz.Akteur}) für {D(ganz.Von)} – {D(ganz.Bis)}. Ob sie in SchILD " +
                "eingelesen wurde, weiß die App nicht.", Ziele: ziel);
        }
        if (liste.Count > 0)
            return new JaPunktDto("rueckweg", titel, Gelb,
                $"Noch keine vollständige Datei für das ganze {halbjahr} gemeldet. Bisher: " +
                string.Join(", ", liste.Take(5).Select(p => $"{ArtText(p.Art)} {D(p.Von)} – {D(p.Bis)}")) +
                $". Für das ganze {halbjahr}: {D(noetigAb)} – {D(bis)}, im Desktop ohne Auswahl von Klassen.", Ziele: ziel);
        return new JaPunktDto("rueckweg", titel, Gelb,
            $"Noch nicht übertragen ({D(noetigAb)} – {D(bis)}): Abholcode erzeugen, im Desktop (Wocheneditor → „SchILD-Daten…“ → " +
            "Reiter „Rückweg“) abholen und die Datei schreiben. Vor der ersten echten Übertragung: SchILD sichern und eine " +
            "Probedatei prüfen – bis dahin bleibt die Fehlzeiten-CSV (Klassenbuch → Klassen) der sichere Weg.", Ziele: ziel);
    }

    // ---- Karte „Rückweg nach SchILD“ (P4b) ----

    private static async Task<JaKarteDto> RueckwegKarteAsync(AppDbContext db, SchulWerte w, KalenderDb? kal)
    {
        const string titel = "Rückweg nach SchILD";
        const string text =
            "Fehlstunden einzeln an SchILD übergeben (Datei SchuelerFehlstunden.dat): hier einen Abholcode erzeugen, im Desktop " +
            "abholen (Wocheneditor → „SchILD-Daten…“ → Reiter „Rückweg“) und die Datei schreiben; in SchILD von Hand importieren. " +
            "Übertragen werden entschuldigte (auch beurlaubte) und unentschuldigte Fehlstunden – keine offenen, keine " +
            "Verspätungen. Vor der ersten echten Übertragung die SchILD-Datenbank sichern und eine Probedatei (eine Klasse, " +
            "wenige Tage) prüfen; bis dahin bleibt die Fehlzeiten-CSV der sichere Weg.";
        if (kal == null)
            return new JaKarteDto("rueckweg", titel, Grau, text + " Für das Schuljahr gibt es noch keinen Kalender.", null, null,
                false, Array.Empty<JaPunktDto>());
        var arten = SchildRueckweg.Arten;
        var liste = await db.DownloadProtokolle.AsNoTracking()
            .Where(p => arten.Contains(p.Art) && p.Von <= kal.Ende && p.Bis >= kal.Start)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Select(p => new RueckwegStand(p.Zeit, p.Akteur, p.Von, p.Bis, p.Art))
            .ToListAsync();
        var zeilen = liste.Take(MaxListe).Select(p =>
        {
            var am = DateOnly.FromDateTime(DateTime.SpecifyKind(p.Zeit, DateTimeKind.Utc).ToLocalTime());
            return $"{D(am)}: {ArtText(p.Art)} {D(p.Von)} – {D(p.Bis)} ({p.Akteur})";
        }).ToList();
        var punkt = new JaPunktDto("rueckwegVerlauf", $"Bisher im Schuljahr {w.Schuljahr}", Grau,
            liste.Count == 0
                ? "Noch nichts übertragen."
                : $"{liste.Count} Einträge im Download-Protokoll (abgeholt, Datei bzw. Probedatei), neueste zuerst.",
            zeilen.Count > 0 ? zeilen : null, liste.Count > MaxListe ? (int?)(liste.Count - MaxListe) : null,
            new[]
            {
                new JaZielDto("rueckweg", "Fehlstunden für SchILD bereitstellen…"),
                new JaZielDto("downloads", "Download-Protokoll")
            });
        return new JaKarteDto("rueckweg", titel, Grau, text, null, null, true, new[] { punkt });
    }

    // ---- 5./6. Sek-I-Gruppen (Frage F: neuer Plan zum Halbjahr) ----

    private static async Task<List<JaPunktDto>> GruppenPunkteAsync(AppDbContext db, string sj, PlanStand? plan, DateOnly h)
    {
        if (plan == null)
            return new List<JaPunktDto>
            {
                new JaPunktDto("gruppenOhneListe", "Sek-I-Gruppen ohne Liste", Grau, "Ohne Grundplan gibt es nichts zu prüfen.")
            };
        var slots = await db.GrundplanSlots.AsNoTracking()
            .Where(s => s.VersionId == plan.Id && s.Schuelergruppe != null && s.LerngruppeTyp == "Klasse")
            .Select(s => new { s.Schuelergruppe, s.Art })
            .ToListAsync();
        var schluessel = slots
            .Where(s => !string.IsNullOrWhiteSpace(s.Schuelergruppe) && string.Equals(s.Art, "Unterricht", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Schuelergruppe!.Trim())
            .ToList();
        var gruppen = (await db.Lerngruppen.AsNoTracking()
                .Where(l => l.Typ == LerngruppeTyp.Gruppe && l.Schuljahr == sj)
                .Select(l => new
                {
                    l.Bezeichnung, l.Quelle,
                    AmStichtag = l.Mitglieder.Count(z => (z.GueltigVon == null || z.GueltigVon <= h)
                                                        && (z.GueltigBis == null || z.GueltigBis >= h)),
                    Gesamt = l.Mitglieder.Count()
                })
                .ToListAsync())
            .Select(g => new GruppeStand(g.Bezeichnung, Gueltigkeit.IstSchild(g.Quelle), g.AmStichtag, g.Gesamt))
            .ToList();
        return GruppenPunkte(schluessel, gruppen, plan, h);
    }

    public sealed record GruppeStand(string Bezeichnung, bool AusSchild, int AmStichtag, int Gesamt);

    public static List<JaPunktDto> GruppenPunkte(IEnumerable<string> planSchluessel, IReadOnlyCollection<GruppeStand> gruppen,
        PlanStand plan, DateOnly h)
    {
        var ziel = new[] { new JaZielDto("gruppen", "Gruppenlisten") };
        var imPlan = new SortedSet<string>(planSchluessel.Select(k => k.Trim()).Where(k => k.Length > 0), Cmp);
        var mitListe = new HashSet<string>(gruppen.Where(g => g.AmStichtag > 0).Select(g => g.Bezeichnung.Trim()), Cmp);
        var ohne = imPlan.Where(k => !mitListe.Contains(k)).ToList();
        var weg = gruppen.Where(g => g.Gesamt > 0 && !imPlan.Contains(g.Bezeichnung.Trim()))
            .GroupBy(g => g.Bezeichnung.Trim(), Cmp)
            .Select(g => g.Key + (g.Any(x => x.AusSchild) ? " (SchILD)" : ""))
            .OrderBy(k => k, Cmp)
            .ToList();

        var l = new List<JaPunktDto>();
        l.Add(ohne.Count == 0
            ? new JaPunktDto("gruppenOhneListe", "Sek-I-Gruppen ohne Liste", Gruen,
                $"Alle {imPlan.Count} Sek-I-Gruppen des Grundplans {Plan(plan)} haben am {D(h)} eine Liste (aus SchILD bzw. von " +
                "der Fachlehrkraft).", Ziele: ziel)
            : new JaPunktDto("gruppenOhneListe", "Sek-I-Gruppen ohne Liste", Gelb,
                $"{ohne.Count} von {imPlan.Count} Sek-I-Gruppen des Grundplans {Plan(plan)} haben am {D(h)} keine Liste – weder " +
                "aus SchILD noch von der Fachlehrkraft zusammengestellt. Ihre Stunden lassen sich erst erfassen, wenn die " +
                "Fachlehrkraft die Gruppe unter „Meine Gruppen“ zusammenstellt bzw. der SchILD-Import sie liefert. Nur ein Hinweis.",
                ohne.Take(MaxListe).ToList(), ohne.Count > MaxListe ? ohne.Count - MaxListe : null, ziel));
        l.Add(weg.Count == 0
            ? new JaPunktDto("gruppenWeg", "Gruppenlisten mit wegfallendem Schlüssel", Gruen,
                $"Alle Gruppenlisten gehören zu Gruppen des Grundplans {Plan(plan)}.")
            : new JaPunktDto("gruppenWeg", "Gruppenlisten mit wegfallendem Schlüssel", Grau,
                $"{weg.Count} Gruppenlisten gehören zu Schlüsseln, die es im Grundplan {Plan(plan)} nicht gibt. Sie bleiben für " +
                "die Stunden davor erhalten; ab dem 2. Halbjahr braucht sie niemand mehr. Nur ein Hinweis.",
                weg.Take(MaxListe).ToList(), weg.Count > MaxListe ? weg.Count - MaxListe : null, ziel));
        return l;
    }
}
