using System.Text.RegularExpressions;
using VertretungApi.Data;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Erzeugt synthetische Stammdaten: Lerngruppen (Klassen + Kurse) und
//  Schüler mit Zuordnungen. Reine Testdaten (IstPseudo=true) – keine
//  echten Personen.
//
//  Zwei Wege:
//   - GenerierenAusGrundplan (Phase 2, Schritt 2, Standard sobald ein
//     Grundplan gepusht ist): Klassen und Kurse sind GENAU die Lerngruppen-
//     Schlüssel des Grundplans ("05A", "BIG1_EF_L68" …). Sek-II-Schüler
//     wählen Kurse überschneidungsfrei (EF 11 Kurse, Q1/Q2 2 LK + 8 weitere),
//     damit ihr individueller Wochenplan stimmig ist.
//   - Generieren (Phase 1B): frei erfundene Klassen/Kurse, falls noch kein
//     Grundplan vorliegt.
// =====================================================================

public static class PseudoDatenGenerator
{
    private static readonly string[] Vornamen =
    {
        "Mia","Emma","Hannah","Lena","Sophie","Marie","Lea","Anna","Laura","Emily",
        "Ben","Paul","Leon","Finn","Luca","Noah","Elias","Jonas","Luis","Felix",
        "Clara","Nele","Ida","Mila","Lina","Jana","Sarah","Johanna","Pia","Greta",
        "Max","Tim","Jan","Niklas","David","Moritz","Julian","Tom","Erik","Henry",
        "Amir","Leyla","Yusuf","Aylin","Mohammed","Fatima","Nino","Sofia","Ivan","Marta"
    };

    private static readonly string[] Nachnamen =
    {
        "Müller","Schmidt","Schneider","Fischer","Weber","Meyer","Wagner","Becker","Schulz","Hoffmann",
        "Koch","Richter","Bauer","Klein","Wolf","Schröder","Neumann","Schwarz","Zimmermann","Braun",
        "Krüger","Hofmann","Hartmann","Lange","Werner","Krause","Lehmann","Schmitt","Köhler","Walter",
        "Yilmaz","Kaya","Demir","Nguyen","Rossi","Kowalski","Nowak","Petrov","Ibrahim","Okafor"
    };

    // Fächer der Oberstufe (Kurse). LK nur für eine Teilmenge.
    private static readonly string[] OberstufeFaecher =
    { "D","M","E","BI","CH","PH","IF","GE","EK","SW","PA","KU","MU","SP","F","GES" };
    private static readonly string[] LkFaecher =
    { "D","M","E","BI","GE","SW","PA" };

    public static Ergebnis Generieren(AppDbContext db, string schuljahr, int seed = 12345)
    {
        LoescheVorhandene(db);
        var rnd = new Random(seed);

        string LKuerzel() => "L" + rnd.Next(1, 121);
        Schueler NeuerSchueler(string stufe) => new()
        {
            Vorname = Vornamen[rnd.Next(Vornamen.Length)],
            Nachname = Nachnamen[rnd.Next(Nachnamen.Length)],
            Stufe = stufe,
            IstPseudo = true
        };

        var klassen = new List<Lerngruppe>();
        var kurse = new List<Lerngruppe>();
        var schueler = new List<Schueler>();
        var zuordnungen = new List<Zugehoerigkeit>();

        // -------- Sek I: Jahrgänge 5–10, je 3 Parallelklassen, ~24 Schüler --------
        foreach (int jg in Enumerable.Range(5, 6))          // 5..10
        {
            string stufe = jg.ToString("00");               // "05".."10"
            foreach (char p in new[] { 'A', 'B', 'C' })
            {
                var kl = new Lerngruppe
                {
                    Typ = LerngruppeTyp.Klasse,
                    Bezeichnung = $"{stufe}{p}",
                    Stufe = stufe,
                    LehrerKuerzel = LKuerzel(),
                    Schuljahr = schuljahr,
                    IstPseudo = true
                };
                klassen.Add(kl);
                for (int i = 0; i < 24; i++)
                {
                    var s = NeuerSchueler(stufe);
                    schueler.Add(s);
                    zuordnungen.Add(new Zugehoerigkeit { Schueler = s, Lerngruppe = kl, Schuljahr = schuljahr });
                }
            }
        }

        // -------- Sek II: EF/Q1/Q2, Kurssystem --------
        foreach (string stufe in new[] { "EF", "Q1", "Q2" })
        {
            // Kurse je Fach: 2 GK; für LK-Fächer zusätzlich 1 LK.
            var gkNachFach = new Dictionary<string, List<Lerngruppe>>();
            var lkKurse = new List<Lerngruppe>();

            foreach (var fach in OberstufeFaecher)
            {
                var liste = new List<Lerngruppe>();
                for (int n = 1; n <= 2; n++)
                {
                    var gk = new Lerngruppe
                    {
                        Typ = LerngruppeTyp.Kurs, Bezeichnung = $"{stufe} {fach} GK{n}",
                        Stufe = stufe, Fach = fach, Kursart = Kursart.GK,
                        LehrerKuerzel = LKuerzel(), Schuljahr = schuljahr, IstPseudo = true
                    };
                    kurse.Add(gk); liste.Add(gk);
                }
                gkNachFach[fach] = liste;

                if (LkFaecher.Contains(fach))
                {
                    var lk = new Lerngruppe
                    {
                        Typ = LerngruppeTyp.Kurs, Bezeichnung = $"{stufe} {fach} LK1",
                        Stufe = stufe, Fach = fach, Kursart = Kursart.LK,
                        LehrerKuerzel = LKuerzel(), Schuljahr = schuljahr, IstPseudo = true
                    };
                    kurse.Add(lk); lkKurse.Add(lk);
                }
            }

            // 60 Schüler je Jahrgang, je ~10 Kurse (2 LK + Pflicht-GK D/M/E + Auffüllung).
            for (int i = 0; i < 60; i++)
            {
                var s = NeuerSchueler(stufe);
                schueler.Add(s);
                var gewaehlt = new HashSet<Lerngruppe>();
                var belegteFaecher = new HashSet<string>();

                // 2 LK aus verschiedenen Fächern
                foreach (var lk in lkKurse.OrderBy(_ => rnd.Next()))
                {
                    if (gewaehlt.Count >= 2) break;
                    if (belegteFaecher.Add(lk.Fach!)) gewaehlt.Add(lk);
                }
                // Pflicht-GK D, M, E (falls nicht schon als LK)
                foreach (var f in new[] { "D", "M", "E" })
                {
                    if (belegteFaecher.Contains(f)) continue;
                    var gk = gkNachFach[f][rnd.Next(gkNachFach[f].Count)];
                    gewaehlt.Add(gk); belegteFaecher.Add(f);
                }
                // Auffüllen auf 10 Kurse mit weiteren GK
                var alleGk = gkNachFach.Values.SelectMany(x => x).OrderBy(_ => rnd.Next()).ToList();
                foreach (var gk in alleGk)
                {
                    if (gewaehlt.Count >= 10) break;
                    if (belegteFaecher.Contains(gk.Fach!)) continue;   // je Fach nur ein Kurs
                    gewaehlt.Add(gk); belegteFaecher.Add(gk.Fach!);
                }

                foreach (var g in gewaehlt)
                    zuordnungen.Add(new Zugehoerigkeit { Schueler = s, Lerngruppe = g, Schuljahr = schuljahr });
            }
        }

        db.Lerngruppen.AddRange(klassen);
        db.Lerngruppen.AddRange(kurse);
        db.Schueler.AddRange(schueler);
        db.Zugehoerigkeiten.AddRange(zuordnungen);
        db.SaveChanges();

        return new Ergebnis(schueler.Count, klassen.Count, kurse.Count, zuordnungen.Count);
    }

    // =================================================================
    //  Pseudodaten aus dem gepushten Grundplan (Phase 2, Schritt 2)
    // =================================================================

    private static readonly Regex KursFachMuster =
        new(@"^(?<fach>.+?)\s+(?<art>Ver|[GLV])\s*(?<nr>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Pflichtfächer der Oberstufe, die jede Person zuerst belegt (falls angeboten).</summary>
    private static readonly string[] OberstufePflicht = { "D", "M", "E" };

    private sealed class KursInfo
    {
        public Lerngruppe Lg = null!;
        public string FachBasis = "";                       // Fach ohne Kursnummer; Vertiefungskurse eigen
        public HashSet<(int Tag, int Stunde)> Zeiten = new();
        public int Belegt;                                   // Anzahl zugeordneter Schüler
    }

    /// <summary>
    /// Erzeugt Lerngruppen und Schüler passend zu einer Grundplan-Version. Klassen =
    /// Slots vom Typ „Klasse", Kurse = Slots vom Typ „Kurs" (Schlüssel = Schülergruppe).
    /// „Sonstige" Gruppen (AGs, Förderkurse …) werden nicht angelegt.
    /// </summary>
    public static Ergebnis GenerierenAusGrundplan(
        AppDbContext db, GrundplanVersion version, List<GrundplanSlot> slots, int seed = 12345)
    {
        LoescheVorhandene(db);
        var rnd = new Random(seed);
        string schuljahr = version.Schuljahr;

        Schueler NeuerSchueler(string stufe) => new()
        {
            Vorname = Vornamen[rnd.Next(Vornamen.Length)],
            Nachname = Nachnamen[rnd.Next(Nachnamen.Length)],
            Stufe = stufe,
            IstPseudo = true
        };

        var unterricht = slots
            .Where(s => s.Art == "Unterricht" && !string.IsNullOrWhiteSpace(s.LerngruppeKey))
            .ToList();

        var klassen = new List<Lerngruppe>();
        var kurse = new List<KursInfo>();
        var schueler = new List<Schueler>();
        var zuordnungen = new List<Zugehoerigkeit>();

        // -------- Klassen (Sek I) --------
        foreach (var g in unterricht.Where(sl => sl.LerngruppeTyp == "Klasse")
                     .GroupBy(sl => sl.LerngruppeKey.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(gr => gr.Key, StringComparer.OrdinalIgnoreCase))
        {
            string key = g.Key;
            string stufe = key.Length >= 2 && char.IsDigit(key[0]) && char.IsDigit(key[1]) ? key[..2] : "";
            klassen.Add(new Lerngruppe
            {
                Typ = LerngruppeTyp.Klasse,
                Bezeichnung = key,
                Stufe = stufe,
                LehrerKuerzel = HaeufigsteLehrkraft(g),     // als „Klassenleitung" plausibel
                Schuljahr = schuljahr,
                IstPseudo = true
            });
        }

        // -------- Kurse (Sek II) --------
        foreach (var g in unterricht.Where(sl => sl.LerngruppeTyp == "Kurs")
                     .GroupBy(sl => sl.LerngruppeKey.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(gr => gr.Key, StringComparer.OrdinalIgnoreCase))
        {
            var erster = g.First();
            var (fachBasis, kursart, fach) = ZerlegeKursFach(erster.Fach);
            kurse.Add(new KursInfo
            {
                Lg = new Lerngruppe
                {
                    Typ = LerngruppeTyp.Kurs,
                    Bezeichnung = g.Key,
                    Stufe = erster.Klasse.Trim().ToUpperInvariant(),   // EF / Q1 / Q2
                    Fach = fach,
                    Kursart = kursart,
                    LehrerKuerzel = HaeufigsteLehrkraft(g),
                    Schuljahr = schuljahr,
                    IstPseudo = true
                },
                FachBasis = fachBasis,
                Zeiten = g.Select(sl => (sl.WTagIndex, sl.Stunde)).ToHashSet()
            });
        }

        // -------- Schüler Sek I: 24–28 je Klasse --------
        foreach (var kl in klassen)
        {
            int anzahl = 24 + rnd.Next(0, 5);
            for (int i = 0; i < anzahl; i++)
            {
                var s = NeuerSchueler(kl.Stufe);
                schueler.Add(s);
                zuordnungen.Add(new Zugehoerigkeit { Schueler = s, Lerngruppe = kl, Schuljahr = schuljahr });
            }
        }

        // -------- Schüler Sek II: Kurse überschneidungsfrei wählen --------
        foreach (var stufeGruppe in kurse.GroupBy(ki => ki.Lg.Stufe).OrderBy(gr => gr.Key))
        {
            var stufeKurse = stufeGruppe.ToList();
            bool qPhase = stufeGruppe.Key is "Q1" or "Q2";
            int zielKurse = qPhase ? 10 : 11;
            // So viele Schüler, dass ein Kurs im Mittel ~20 Teilnehmende hat.
            int anzahl = Math.Clamp((int)Math.Round(stufeKurse.Count * 20.0 / zielKurse), 20, 150);

            var nachFach = stufeKurse
                .GroupBy(ki => ki.FachBasis, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(gr => gr.Key, gr => gr.ToList(), StringComparer.OrdinalIgnoreCase);
            var lks = stufeKurse.Where(ki => ki.Lg.Kursart == Kursart.LK).ToList();

            for (int i = 0; i < anzahl; i++)
            {
                var s = NeuerSchueler(stufeGruppe.Key);
                schueler.Add(s);

                var belegt = new HashSet<(int Tag, int Stunde)>();
                var faecher = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var gewaehlt = new List<KursInfo>();

                bool Versuche(KursInfo k)
                {
                    if (faecher.Contains(k.FachBasis) || k.Zeiten.Overlaps(belegt)) return false;
                    gewaehlt.Add(k);
                    faecher.Add(k.FachBasis);
                    belegt.UnionWith(k.Zeiten);
                    return true;
                }

                // Q1/Q2: zwei Leistungskurse; weniger volle Kurse bevorzugt (gleichmäßige Größen).
                if (qPhase)
                    foreach (var lk in lks.OrderBy(ki => ki.Belegt).ThenBy(_ => rnd.Next()))
                    {
                        if (gewaehlt.Count >= 2) break;
                        Versuche(lk);
                    }

                // Pflichtfächer zuerst, dann die übrigen Fächer in zufälliger Reihenfolge.
                var fachReihenfolge = OberstufePflicht
                    .Concat(nachFach.Keys.OrderBy(_ => rnd.Next()))
                    .ToList();
                foreach (var fach in fachReihenfolge)
                {
                    if (gewaehlt.Count >= zielKurse) break;
                    if (!nachFach.TryGetValue(fach, out var angebote)) continue;
                    foreach (var angebot in angebote.Where(ki => ki.Lg.Kursart != Kursart.LK)
                                                   .OrderBy(ki => ki.Belegt).ThenBy(_ => rnd.Next()))
                        if (Versuche(angebot)) break;
                }

                foreach (var kurs in gewaehlt)
                {
                    kurs.Belegt++;
                    zuordnungen.Add(new Zugehoerigkeit { Schueler = s, Lerngruppe = kurs.Lg, Schuljahr = schuljahr });
                }
            }
        }

        db.Lerngruppen.AddRange(klassen);
        db.Lerngruppen.AddRange(kurse.Select(ki => ki.Lg));
        db.Schueler.AddRange(schueler);
        db.Zugehoerigkeiten.AddRange(zuordnungen);
        db.SaveChanges();

        return new Ergebnis(schueler.Count, klassen.Count, kurse.Count, zuordnungen.Count);
    }

    /// <summary>
    /// Zerlegt ein Kursfach: "BI G1" -> (BI, GK), "D L2" -> (D, LK), "E Ver1" -> (E-VK, VK).
    /// Ohne erkennbares Muster (z. B. "PSF_A") ist das ganze Fach die Basis.
    /// </summary>
    private static (string FachBasis, Kursart Kursart, string Fach) ZerlegeKursFach(string kursFach)
    {
        string f = kursFach.Trim();
        var m = KursFachMuster.Match(f);
        if (!m.Success) return (f, Kursart.Keine, f);

        string fach = m.Groups["fach"].Value.Trim();
        var art = m.Groups["art"].Value.ToUpperInvariant() switch
        {
            "G" => Kursart.GK,
            "L" => Kursart.LK,
            _ => Kursart.VK                                   // "V" / "Ver"
        };
        // Vertiefungskurse zählen als eigenes „Fach", damit sie zusätzlich wählbar sind.
        return (art == Kursart.VK ? fach + "-VK" : fach, art, fach);
    }

    private static string? HaeufigsteLehrkraft(IEnumerable<GrundplanSlot> slots)
        => slots.Where(s => !string.IsNullOrWhiteSpace(s.Lehrer))
                .GroupBy(s => s.Lehrer, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault();

    private static void LoescheVorhandene(AppDbContext db)
    {
        // Reihenfolge wegen FK: erst Zuordnungen/Lektionen, dann Schüler/Lerngruppen.
        db.Zugehoerigkeiten.RemoveRange(db.Zugehoerigkeiten.Where(z => true));
        db.Lektionen.RemoveRange(db.Lektionen.Where(l => l.IstPseudo));
        db.Schueler.RemoveRange(db.Schueler.Where(s => s.IstPseudo));
        db.Lerngruppen.RemoveRange(db.Lerngruppen.Where(l => l.IstPseudo));
        db.SaveChanges();
    }

    public record Ergebnis(int Schueler, int Klassen, int Kurse, int Zuordnungen);
}
