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
// =====================================================================

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
    public static async Task<WochenplanDto> BerechneAsync(
        AppDbContext db, WochenZielArt art, string anzeige, IEnumerable<string> werte,
        DateOnly datum, int wochen)
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

            var stunden = EffektiverTag(verSlots, iso, vt?.Eintraege, teilFrei)
                .Where(st => Passt(st, art, zielWerte))
                .OrderBy(st => st.Stunde).ThenBy(st => st.Fach, Cmp).ThenBy(st => st.Lehrer, Cmp)
                .Select(ZuDto)
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
        public string Art = "Unterricht";
        public string Status = "Normal";
        public string? Info;
        public bool Geaendert;
        public readonly HashSet<string> Gruppen = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Klassen = new(StringComparer.OrdinalIgnoreCase);
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

        foreach (var e in eintraege)
        {
            string orig = !string.IsNullOrWhiteSpace(e.OriginalLehrer) ? e.OriginalLehrer! : (e.Lehrer ?? "");
            var eKlassen = Liste(e.Klassen);
            string status = string.IsNullOrWhiteSpace(e.Status) ? e.Art.ToString() : e.Status;

            // Grundplan-Stunde suchen: exakt über die UNr, sonst über die Klassen.
            Std? ziel = null;
            if (e.GrundUNr.HasValue)
                ziel = stunden.FirstOrDefault(st =>
                    st.Stunde == e.Stunde && st.GrundUNr == e.GrundUNr.Value && Gleich(st.GrundLehrer, orig));
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
                        if (!string.IsNullOrWhiteSpace(e.Raum)) ziel.Raum = e.Raum;
                        Markiere(ziel, status, "Raumwechsel");
                    }
                    break;

                default:
                {
                    // Vertreten, Betreuung, EVA, Zusammenlegung, VerlegtHin (und Unbekanntes):
                    // eine Lehrkraft hält die Stunde – ggf. eine andere als laut Grundplan.
                    bool zusaetzlich = ziel == null;
                    var x = ziel ?? NeueAusEintrag(stunden, e, orig, eKlassen);

                    string neuerLehrer = e.Lehrer ?? "";
                    if (neuerLehrer.Length > 0 && !Gleich(neuerLehrer, x.Lehrer))
                    {
                        x.OriginalLehrer ??= x.Lehrer;
                        x.Lehrer = neuerLehrer;
                    }

                    if (status == "VerlegtHin")
                    {
                        // Hier wird die (von woanders) verlegte Stunde gehalten.
                        if (!string.IsNullOrWhiteSpace(e.Fach)) x.Fach = e.Fach;
                        if (e.UNr.HasValue && e.UNr.Value > 0) x.UNr = e.UNr.Value;
                        ErgaenzeGruppen(x, e.UNr, x.Lehrer, eKlassen, gruppenJeUNr);
                        Markiere(x, status, "verlegt");
                    }
                    else
                    {
                        if (zusaetzlich) ErgaenzeGruppen(x, e.UNr ?? e.GrundUNr, orig, eKlassen, gruppenJeUNr);
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
            // Fachraum (z. B. Bio-Raum) vor Stammraum der Klasse.
            Raum = slots.Select(sl => sl.Fachraum).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
                   ?? slots.Select(sl => sl.Stammraum).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
        };
        foreach (var slot in slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.LerngruppeKey)) x.Gruppen.Add(slot.LerngruppeKey);
            if (!string.IsNullOrWhiteSpace(slot.Klasse)) x.Klassen.Add(slot.Klasse.Trim());
        }
        return x;
    }

    /// <summary>Stunde, die im Grundplan nicht vorkommt (z. B. Verlegung auf eine freie Stunde).</summary>
    private static Std NeueAusEintrag(List<Std> stunden, VertretungsEintragDb e, string orig, List<string> eKlassen)
    {
        var x = new Std
        {
            Stunde = e.Stunde,
            UNr = e.UNr ?? e.GrundUNr ?? 0,
            GrundUNr = e.GrundUNr ?? 0,
            Fach = e.Fach ?? "",
            Lehrer = orig,
            GrundLehrer = orig,
            Raum = e.Raum
        };
        foreach (var k in eKlassen) x.Klassen.Add(k);
        stunden.Add(x);
        return x;
    }

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

    private static bool Passt(Std x, WochenZielArt art, HashSet<string> werte)
    {
        // Vertretungsbereitschaft (VBer) und UV-Stunden sind Dienste der Lehrkraft,
        // kein Unterricht einer Lerngruppe.
        bool bereitschaft = x.Art is "VBer" or "UV";
        return art switch
        {
            WochenZielArt.Lehrer => werte.Contains(x.Lehrer)
                                    || (x.OriginalLehrer != null && werte.Contains(x.OriginalLehrer)),
            WochenZielArt.Klasse => !bereitschaft && (x.Klassen.Overlaps(werte) || x.Gruppen.Overlaps(werte)),
            _ => !bereitschaft && x.Gruppen.Overlaps(werte)
        };
    }

    private static WochenStundeDto ZuDto(Std x) => new(
        x.Stunde,
        x.UNr,
        x.Fach,
        x.Lehrer,
        string.IsNullOrWhiteSpace(x.OriginalLehrer) || Gleich(x.OriginalLehrer, x.Lehrer) ? null : x.OriginalLehrer,
        x.Raum,
        x.Gruppen.OrderBy(g => g, Cmp).ToList(),
        x.Klassen.OrderBy(k => k, Cmp).ToList(),
        x.Art,
        x.Status,
        x.Info,
        x.Geaendert);

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
