using VertretungApi.Data;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Erzeugt synthetische Stammdaten (Phase 1B): Sek-I-Klassen und
//  Sek-II-Kurse als Lerngruppen, dazu Schüler mit Zuordnungen.
//  Reine Testdaten (IstPseudo=true) – keine echten Personen.
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
