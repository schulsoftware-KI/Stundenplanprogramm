#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildGenerator – Phase 5.2a: Pseudo-Export im Format von SchILD-NRW 3.
    //  Erzeugt aus dem Grundplan (dieselben Schlüssel wie „Grundplan
    //  veröffentlichen…“) die Dateien Klassen.dat, Kurse.dat (neues Format),
    //  SchuelerBasisdaten.dat, SchuelerLeistungsdaten.dat, Lehrkraefte.dat,
    //  Faecher.dat, Jahrgaenge.dat und optional SchuelerLernplattformen.dat –
    //  volle Spaltenzahl laut Beschreibung, nicht gelesene Pflichtspalten mit
    //  unverfänglichen Werten (Statistik-Kürzel plausibel, ungeprüft – keine
    //  SchILD-Testumgebung, Entscheidung 84). Fiktive Namen und Geburtsdaten.
    //
    //  - Oberstufe: Kurswahl überschneidungsfrei wie der bisherige Seed
    //    (EF 11 Kurse, Q1/Q2 2 LK + 8).
    //  - Sek I (Entscheidung 92): Gruppen, die sich für eine Klasse zeitlich
    //    überschneiden (Religion, WP), werden aufgeteilt – je Schüler genau
    //    eine; eine Gruppe allein für eine Klasse = ganze Klasse; eine Gruppe
    //    allein über mehrere Klassen (Chor, AG) = Auswahl (~20).
    //    Variante „gemischt“: Jg. 05–07 ohne Kurs (nur Fach + Fachlehrer,
    //    Entscheidung 86), Jg. 08–10 mit Kurs.
    //  - Absichtliche Stolpersteine für die Zuordnung (5.2a-2): abweichende
    //    Fachkürzel, ein abweichendes Lehrkräftekürzel, Kursbezeichnungen in
    //    SchILD-Art, ein Kooperationskurs ohne Kursleiter, ein #Klasse-Paar,
    //    einzelne Status 0/1/3/6/9; seit 5.2a-2 ein Projektkurs ohne
    //    Gegenstück im Plan (testet „keine Zuordnung“).
    //  - Markerdatei PSEUDODATEN.json mit Prüfsummen (Entscheidung 90).
    // =====================================================================

    public enum SchildSek1Variante { Gemischt, MitKurs, OhneKurs }

    public enum SchildZielordnerStand { Leer, AlterPseudoExport, Fremd }

    public sealed class SchildGeneratorOptionen
    {
        public string Zielordner { get; set; } = "";
        public bool Utf8MitBom { get; set; }
        public bool MitIds { get; set; } = true;
        public SchildSek1Variante Sek1 { get; set; } = SchildSek1Variante.Gemischt;
        public int Startwert { get; set; } = 42;
        public int Abschnitt { get; set; } = 1;
    }

    public sealed class SchildGeneratorErgebnis
    {
        public int Schueler { get; set; }
        public int Klassen { get; set; }
        public int KurseSek2 { get; set; }
        public int KurseSek1 { get; set; }
        public int GruppenOhneKurs { get; set; }
        public int Leistungszeilen { get; set; }
        public int Lehrkraefte { get; set; }
        public int Faecher { get; set; }
        public List<string> Dateien { get; } = new();
        public List<string> Stolpersteine { get; } = new();
    }

    public static class SchildGenerator
    {
        private static readonly HashSet<string> Oberstufe = new(StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };
        private static readonly Regex KlassenMuster = new(@"^\d{2}[A-Za-z]{1,2}$", RegexOptions.Compiled);
        private static readonly Regex KursFach = new(@"^(?<f>\S+)\s+(?<a>Ver|V|G|L|K|Z)\s*(?<n>\d+)\D*$",
                                                     RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EndZiffern = new(@"^(?<f>.*?\D)(?<n>\d+)$", RegexOptions.Compiled);

        /// <summary>So heißen einige Plan-Fächer in SchILD (simulierte Abweichung).</summary>
        private static readonly Dictionary<string, string> FachInSchild = new(StringComparer.OrdinalIgnoreCase)
        {
            ["P"] = "PH", ["C"] = "CH", ["I0"] = "IF", ["I"] = "IF"
        };

        private static readonly HashSet<string> ReligionPhilo = new(StringComparer.OrdinalIgnoreCase)
        { "ER", "KR", "PP", "IR", "JR", "OR", "HR", "PL" };

        private static readonly Dictionary<string, string> FachNamen = new(StringComparer.OrdinalIgnoreCase)
        {
            ["D"] = "Deutsch", ["M"] = "Mathematik", ["E"] = "Englisch", ["F"] = "Französisch", ["L"] = "Latein",
            ["S"] = "Spanisch", ["BI"] = "Biologie", ["CH"] = "Chemie", ["PH"] = "Physik", ["IF"] = "Informatik",
            ["GE"] = "Geschichte", ["EK"] = "Erdkunde", ["SW"] = "Sozialwissenschaften", ["PK"] = "Politik",
            ["PA"] = "Pädagogik", ["PL"] = "Philosophie", ["PP"] = "Praktische Philosophie",
            ["ER"] = "Evangelische Religionslehre", ["KR"] = "Katholische Religionslehre", ["KU"] = "Kunst",
            ["MU"] = "Musik", ["SP"] = "Sport", ["WP"] = "Wahlpflicht", ["NW"] = "Naturwissenschaften"
        };

        private static readonly string[] VornamenW =
        {
            "Mia", "Emma", "Hannah", "Lena", "Sophie", "Marie", "Lea", "Anna", "Laura", "Emily", "Clara", "Nele",
            "Ida", "Mila", "Lina", "Jana", "Sarah", "Johanna", "Pia", "Greta", "Leyla", "Aylin", "Fatima", "Sofia",
            "Marta", "Zoë", "Charlotte", "Frieda", "Paula", "Romy", "Elif", "Amelie", "Luisa", "Jule", "Merle", "Ronja"
        };
        private static readonly string[] VornamenM =
        {
            "Ben", "Paul", "Leon", "Finn", "Luca", "Noah", "Elias", "Jonas", "Luis", "Felix", "Max", "Tim", "Jan",
            "Niklas", "David", "Moritz", "Julian", "Tom", "Erik", "Henry", "Amir", "Yusuf", "Mohammed", "Nino",
            "Ivan", "Jörn", "Mats", "Ole", "Emil", "Anton", "Malte", "Can", "Theo", "Karl", "Matteo", "Jakob"
        };
        private static readonly string[] Nachnamen =
        {
            "Müller", "Schmidt", "Schneider", "Fischer", "Weber", "Meyer", "Wagner", "Becker", "Schulz", "Hoffmann",
            "Koch", "Richter", "Bauer", "Klein", "Wolf", "Schröder", "Neumann", "Schwarz", "Zimmermann", "Braun",
            "Krüger", "Hofmann", "Hartmann", "Lange", "Werner", "Krause", "Lehmann", "Schmitt", "Köhler", "Walter",
            "Yilmaz", "Kaya", "Demir", "Nguyen", "Rossi", "Kowalski", "Nowak", "Petrov", "Ibrahim", "Okafor",
            "Peters", "Jung", "Möller", "König", "Huber", "Kaiser", "Fuchs", "Scholz", "Lang", "Weiß",
            "Jansen", "Brandt", "Haas", "Schreiber", "Graf", "Dietrich", "Ziegler", "Kuhn", "Pohl", "Engel",
            "Horn", "Busch", "Bergmann", "Thomas", "Voigt", "Sauer", "Arnold", "Wolff", "Pfeiffer", "Çelik",
            "Sommer", "Winter", "Brinkmann", "Heinrich", "Seidel", "Vogel", "Otto", "Günther", "Böhm", "Lorenz"
        };
        private static readonly string[] LehrerVornamenW =
        { "Andrea", "Birgit", "Claudia", "Daniela", "Eva", "Frauke", "Gabriele", "Heike", "Ines", "Julia", "Katrin", "Sabine" };
        private static readonly string[] LehrerVornamenM =
        { "Andreas", "Bernd", "Christian", "Dirk", "Frank", "Georg", "Holger", "Jens", "Klaus", "Markus", "Stefan", "Thomas" };

        // ---- interne Strukturen ----

        private sealed class Zaehler
        {
            public readonly Dictionary<string, int> D = new(StringComparer.OrdinalIgnoreCase);
            public void Plus(string k, int anzahl = 1) { if (k.Length == 0) return; D.TryGetValue(k, out int n); D[k] = n + anzahl; }
            public string Haupt() => D.OrderByDescending(kv => kv.Value)
                                      .ThenBy(kv => kv.Key.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                                      .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                      .Select(kv => kv.Key).FirstOrDefault() ?? "";
            public List<string> Rest() { string h = Haupt(); return D.Keys.Where(k => !k.Equals(h, StringComparison.OrdinalIgnoreCase))
                                                                       .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(); }
        }

        private sealed class FachInfo
        {
            public string PlanFach = "";
            public readonly Zaehler Lehrer = new();
            public readonly HashSet<(int, int)> Zeiten = new();
        }

        /// <summary>Unterricht im Klassenverband, zusammengefasst je SchILD-Fach.</summary>
        private sealed class KvFach
        {
            public string SchildFach = "";
            public readonly Zaehler Lehrer = new();
            public readonly HashSet<(int, int)> Zeiten = new();
        }

        private sealed class KlasseInfo
        {
            public string Name = "";
            public string Jahrgang = "";
            public int JgNum;
            public readonly Zaehler Lehrer = new();
            public readonly Dictionary<string, FachInfo> Faecher = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<PSchueler> Schueler = new();
            public readonly List<KvFach> Kv = new();
        }

        private sealed class GruppeInfo
        {
            public string Key = "";
            public readonly Zaehler Fach = new();
            public readonly Zaehler Lehrer = new();
            public readonly SortedSet<string> Klassen = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<(int, int)> Zeiten = new();
            public readonly List<PSchueler> Mitglieder = new();
            public bool MitKurs;
            public string KursBez = "";
            public string KursKlasse = "";
            public string SchildFach = "";
            public List<string> Jahrgaenge = new();
            public string Kursart = "";
        }

        private sealed class KursInfo
        {
            public string Key = "";
            public readonly Zaehler StufeZ = new();
            public readonly Zaehler FachZ = new();
            public readonly Zaehler Lehrer = new();
            public readonly HashSet<(int, int)> Zeiten = new();
            public string Stufe = "";
            public string Basis = "";
            public string Art = "";       // GK | LK | VTF | ZK | ""
            public int Nr;
            public string SchildFach = "";
            public string WahlFach = "";  // Fach für „je Fach nur ein Kurs“ (Vertiefung eigenes Fach)
            public string KursBez = "";
            public bool Kooperation;
            public int Belegt;
        }

        private sealed class PLeistung
        {
            public string Fach = "", Lehrer = "", Kursart = "", Kurs = "", Zusatz = "", Jahrgang = "";
            public int Wst;
        }

        private sealed class PSchueler
        {
            public string Nachname = "", Zusatz = "", Vorname = "", Geschlecht = "";
            public DateOnly Geb;
            public int Status = 2;
            public DateOnly? Aufnahme;
            public string Jahrgang = "", Klasse = "";
            public int JgNum;
            public long Id;
            public string Stammschule = "";
            public readonly List<PLeistung> Leistungen = new();
            /// <summary>Fächer, die der Schüler ohne Kurs hat (Klassenverband, Gruppen ohne Kurs).</summary>
            public readonly HashSet<string> OhneKurs = new(StringComparer.OrdinalIgnoreCase);
            public string NameFeld => Zusatz.Length > 0 ? $"{Nachname}#{Zusatz}" : Nachname;
        }

        // =================================================================
        //  Zielordner
        // =================================================================

        public static SchildZielordnerStand PruefeZielordner(string ordner)
        {
            if (!Directory.Exists(ordner)) return SchildZielordnerStand.Leer;
            if (Directory.GetFiles(ordner, "*.dat").Length == 0) return SchildZielordnerStand.Leer;
            return SchildPseudoMarker.Pruefe(ordner, out _) ? SchildZielordnerStand.AlterPseudoExport : SchildZielordnerStand.Fremd;
        }

        // =================================================================
        //  Erzeugen
        // =================================================================

        public static SchildGeneratorErgebnis Erzeuge(GrundplanPushDto plan, string schuljahr, SchildGeneratorOptionen opt)
        {
            string ziel = opt.Zielordner;
            var stand = PruefeZielordner(ziel);
            if (stand == SchildZielordnerStand.Fremd)
                throw new InvalidOperationException(
                    "Der Zielordner enthält .dat-Dateien, die nicht aus einem Pseudo-Export stammen. " +
                    "Bitte einen leeren Ordner wählen – echte Exporte werden nie überschrieben.");
            Directory.CreateDirectory(ziel);
            if (stand == SchildZielordnerStand.AlterPseudoExport)
            {
                var alt = SchildPseudoMarker.Laden(ziel);
                foreach (var name in alt?.Dateien.Keys.ToList() ?? new List<string>())
                {
                    string p = Path.Combine(ziel, name);
                    if (File.Exists(p)) File.Delete(p);
                }
                File.Delete(Path.Combine(ziel, SchildPseudoMarker.DateiName));
            }

            var erg = new SchildGeneratorErgebnis();
            var rnd = new Random(opt.Startwert);
            int jahr = Jahr(schuljahr);
            int abschnitt = opt.Abschnitt is 1 or 2 ? opt.Abschnitt : 1;
            string J = jahr.ToString(CultureInfo.InvariantCulture), A = abschnitt.ToString(CultureInfo.InvariantCulture);

            // ---------- Grundplan auswerten ----------
            var klassen = new Dictionary<string, KlasseInfo>(StringComparer.OrdinalIgnoreCase);
            var gruppen = new Dictionary<string, GruppeInfo>(StringComparer.OrdinalIgnoreCase);
            var kurse = new Dictionary<string, KursInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var sl in plan.Slots)
            {
                if (sl.Art != "Unterricht" || string.IsNullOrWhiteSpace(sl.Lehrer) || string.IsNullOrWhiteSpace(sl.LerngruppeKey)) continue;
                var zeit = (sl.WTagIndex, sl.Stunde);
                string lk = sl.Lehrer.Trim();
                if (sl.LerngruppeTyp == "Klasse" && KlassenMuster.IsMatch(sl.LerngruppeKey))
                {
                    string kn = sl.LerngruppeKey.Trim().ToUpperInvariant();
                    if (!klassen.TryGetValue(kn, out var kl))
                    {
                        kl = new KlasseInfo { Name = kn, Jahrgang = kn[..2] };
                        kl.JgNum = int.Parse(kl.Jahrgang, CultureInfo.InvariantCulture);
                        klassen[kn] = kl;
                    }
                    kl.Lehrer.Plus(lk);
                    if (!string.IsNullOrWhiteSpace(sl.Schuelergruppe))
                    {
                        string sg = sl.Schuelergruppe!.Trim();
                        if (!gruppen.TryGetValue(sg, out var g)) gruppen[sg] = g = new GruppeInfo { Key = sg };
                        g.Klassen.Add(kn); g.Fach.Plus(sl.Fach.Trim()); g.Lehrer.Plus(lk); g.Zeiten.Add(zeit);
                    }
                    else
                    {
                        string fach = sl.Fach.Trim();
                        if (!kl.Faecher.TryGetValue(fach, out var fi)) kl.Faecher[fach] = fi = new FachInfo { PlanFach = fach };
                        fi.Lehrer.Plus(lk); fi.Zeiten.Add(zeit);
                    }
                }
                else if (sl.LerngruppeTyp == "Kurs")
                {
                    string stufe = (sl.Klasse ?? "").Trim().ToUpperInvariant();
                    if (!Oberstufe.Contains(stufe)) continue;
                    string key = sl.LerngruppeKey.Trim();
                    if (!kurse.TryGetValue(key, out var k)) kurse[key] = k = new KursInfo { Key = key };
                    k.StufeZ.Plus(stufe); k.FachZ.Plus(sl.Fach.Trim()); k.Lehrer.Plus(lk); k.Zeiten.Add(zeit);
                }
            }
            if (klassen.Count == 0 && kurse.Count == 0)
                throw new InvalidOperationException("Der Plan enthält keine Klassen oder Oberstufenkurse mit Unterricht.");

            // ---------- Kürzel und Fächer „wie in SchILD“ ----------
            var krz = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string K(string planKrz) => planKrz.Length == 0 ? "" : krz.TryGetValue(planKrz, out var s) ? s : planKrz;
            // Simulierte Fachabweichung (P → PH …). Nutzt der Plan für ein Fach zwei Kürzel (Beispielplan: „P“ und „PH“),
            // gibt es in SchILD nur eines – genau das muss die Zuordnung (5.2a-2) können. Doppelte Zeilen entstehen nicht:
            // der Klassenverband wird je SchILD-Fach zusammengefasst, Gruppen bekommen bei Kollision einen Kurs.
            var fachAbweichung = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string SF(string basis, bool oberstufe)
            {
                string b = basis.ToUpperInvariant();
                if (!FachInSchild.TryGetValue(b, out var s)) return b;
                fachAbweichung[$"„{b}“ ({(oberstufe ? "Oberstufe" : "Sek I")})"] = s;
                return s;
            }

            // Klassenverband je Klasse und SchILD-Fach zusammenfassen (eine Leistungsdaten-Zeile je Fach).
            foreach (var kl in klassen.Values)
                foreach (var gr in kl.Faecher.Values.GroupBy(fi => SF(ZerlegePlanFach(fi.PlanFach).Basis, false), StringComparer.OrdinalIgnoreCase)
                                                    .OrderBy(gr2 => gr2.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var kv = new KvFach { SchildFach = gr.Key };
                    foreach (var fi in gr)
                    {
                        foreach (var le in fi.Lehrer.D) kv.Lehrer.Plus(le.Key, le.Value);
                        kv.Zeiten.UnionWith(fi.Zeiten);
                    }
                    kl.Kv.Add(kv);
                }

            foreach (var k in kurse.Values)
            {
                k.Stufe = k.StufeZ.Haupt();
                var (basis, art, nr) = ZerlegePlanFach(k.FachZ.Haupt());
                k.Basis = basis; k.Art = art.Length > 0 ? art : "GK"; k.Nr = nr;
                k.SchildFach = SF(basis, true);
                k.WahlFach = k.Art == "VTF" ? k.SchildFach + "-VTF" : k.SchildFach;
            }

            // Ein Kürzel weicht absichtlich ab: Kursleiter des ersten Oberstufenkurses (sonst Klassenlehrer).
            string? abweichend = kurse.Values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => k.Lehrer.Haupt()).FirstOrDefault(x => x.Length > 0)
                                 ?? klassen.Values.OrderBy(kl => kl.Name).Select(kl => kl.Lehrer.Haupt()).FirstOrDefault(x => x.Length > 0);
            if (abweichend != null)
            {
                var alle = new HashSet<string>(plan.Slots.Select(sl => sl.Lehrer.Trim()), StringComparer.OrdinalIgnoreCase);
                string neu = abweichend + "S";
                while (alle.Contains(neu)) neu += "S";
                krz[abweichend] = neu;
                erg.Stolpersteine.Add($"Lehrkraft „{abweichend}“ heißt in SchILD „{neu}“.");
            }

            // Kursbezeichnungen in SchILD-Art: <Fach>-<G|L|V|Z><n>, n laufend je Stufe/Fach/Art nach Plan-Nummer.
            foreach (var g in kurse.Values.GroupBy(k => (k.Stufe, k.SchildFach, k.Art)))
            {
                int n = 0;
                foreach (var k in g.OrderBy(k2 => k2.Nr).ThenBy(k2 => k2.Key, StringComparer.OrdinalIgnoreCase))
                {
                    n++;
                    string a = k.Art switch { "LK" => "L", "VTF" => "V", "ZK" => "Z", _ => "G" };
                    k.KursBez = $"{k.SchildFach}-{a}{n}";
                }
            }
            erg.Stolpersteine.Add("Oberstufenkurse heißen wie in SchILD (z. B. „D-G1“), nicht wie die Grundplan-Schlüssel; " +
                                  "die Nummern folgen der Reihenfolge, nicht immer der Nummer im Plan.");
            var koop = kurse.Values.Where(k => k.Stufe == "Q2" && k.Lehrer.D.Count == 1)
                                   .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (koop != null)
            {
                koop.Kooperation = true;
                erg.Stolpersteine.Add($"Kurs „{koop.KursBez}“ (Q2) ohne Kursleiter (Kooperationskurs, Schulnr gefüllt).");
            }

            // ---------- Schüler ----------
            var schluessel = new HashSet<string>();
            var schueler = new List<PSchueler>();
            PSchueler Neu(string jahrgang, string klasse, int jgNum)
            {
                for (int versuch = 0; ; versuch++)
                {
                    bool w = rnd.Next(2) == 0;
                    string vn = w ? VornamenW[rnd.Next(VornamenW.Length)] : VornamenM[rnd.Next(VornamenM.Length)];
                    string nn = Nachnamen[rnd.Next(Nachnamen.Length)];
                    int startJahr = jahr - 6 - jgNum - (rnd.Next(20) == 0 ? 1 : 0);
                    var geb = new DateOnly(startJahr, 7, 1).AddDays(rnd.Next(365));
                    if (!schluessel.Add(SchildText.PersonSchluessel(nn, "", vn, geb)) && versuch < 50) continue;
                    var s = new PSchueler
                    {
                        Nachname = nn, Vorname = vn, Geschlecht = w ? "w" : "m", Geb = geb,
                        Jahrgang = jahrgang, Klasse = klasse, JgNum = jgNum
                    };
                    schueler.Add(s);
                    return s;
                }
            }

            var klassenListe = klassen.Values.OrderBy(kl => kl.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var erste06 = klassenListe.FirstOrDefault(kl => kl.JgNum == 6 && kl.Kv.Count > 0) ?? klassenListe.FirstOrDefault(kl => kl.Kv.Count > 0);
            var erste08 = klassenListe.FirstOrDefault(kl => kl.JgNum == 8 && kl.Kv.Count > 0) ?? klassenListe.LastOrDefault(kl => kl.Kv.Count > 0);
            var ohneUnterricht = klassenListe.Where(kl => kl.Kv.Count == 0).Select(kl => kl.Name).ToList();
            if (ohneUnterricht.Count > 0)
                erg.Stolpersteine.Add($"Ohne Schüler (im Plan kein eigener Klassenunterricht): {string.Join(", ", ohneUnterricht)}.");
            foreach (var kl in klassenListe)
            {
                if (kl.Kv.Count == 0) continue;
                int anzahl = 24 + rnd.Next(0, 5);
                for (int i = 0; i < anzahl; i++) kl.Schueler.Add(Neu(kl.Jahrgang, kl.Name, kl.JgNum));
                if (kl == erste06)
                {
                    var s0 = Neu(kl.Jahrgang, kl.Name, kl.JgNum);
                    s0.Status = 0; s0.Aufnahme = DateOnly.FromDateTime(DateTime.Today.AddDays(14));
                    kl.Schueler.Add(s0);
                }
                if (kl == erste08 && kl.Schueler.Count > 3) kl.Schueler[3].Status = 9;
            }
            if (klassenListe.Any(kl => kl.JgNum == 5))
            {
                var s1 = Neu("05", "", 5); s1.Status = 1;   // Warteliste: ohne Klasse, ohne Leistungsdaten
            }

            // ---------- Sek I: Unterricht im Klassenverband ----------
            foreach (var kl in klassenListe)
                foreach (var kv in kl.Kv)
                {
                    string leiter = K(kv.Lehrer.Haupt()), zusatz = K(kv.Lehrer.Rest().FirstOrDefault() ?? "");
                    foreach (var s in kl.Schueler)
                    {
                        s.Leistungen.Add(new PLeistung { Fach = kv.SchildFach, Lehrer = leiter, Kursart = "PUK", Wst = kv.Zeiten.Count, Zusatz = zusatz });
                        s.OhneKurs.Add(kv.SchildFach);
                    }
                }

            // ---------- Sek I: Gruppen aufteilen (Entscheidung 92) ----------
            var gruppenJeKlasse = klassenListe.ToDictionary(kl => kl.Name,
                kl => gruppen.Values.Where(g => g.Klassen.Contains(kl.Name)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var komponenten = gruppenJeKlasse.ToDictionary(kv => kv.Key, kv => Komponenten(kv.Value), StringComparer.OrdinalIgnoreCase);
            var parallel = new HashSet<GruppeInfo>(komponenten.Values.SelectMany(l => l).Where(c => c.Count >= 2).SelectMany(c => c));

            foreach (var kl in klassenListe)
                foreach (var komp in komponenten[kl.Name])
                {
                    if (komp.Count >= 2)
                    {
                        foreach (var s in kl.Schueler)
                            komp.OrderBy(g => g.Mitglieder.Count).ThenBy(_ => rnd.Next()).First().Mitglieder.Add(s);
                    }
                    else
                    {
                        var g = komp[0];
                        if (g.Klassen.Count == 1 || parallel.Contains(g)) g.Mitglieder.AddRange(kl.Schueler);
                        else
                        {
                            int pool = g.Klassen.Sum(n => klassen.TryGetValue(n, out var k2) ? k2.Schueler.Count : 0);
                            double p = pool > 0 ? Math.Min(1.0, 20.0 / pool) : 0;
                            foreach (var s in kl.Schueler) if (rnd.NextDouble() < p) g.Mitglieder.Add(s);
                        }
                    }
                }

            // Kursangaben der Sek-I-Gruppen
            var gruppenListe = gruppen.Values.OrderBy(g2 => g2.Key, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var g in gruppenListe)
            {
                g.SchildFach = SF(ZerlegePlanFach(g.Fach.Haupt()).Basis, false);
                g.Jahrgaenge = g.Klassen.Select(n => n[..2]).Distinct().OrderBy(x => x).ToList();
                int minJg = g.Jahrgaenge.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).DefaultIfEmpty(5).Min();
                g.MitKurs = opt.Sek1 == SchildSek1Variante.MitKurs || (opt.Sek1 == SchildSek1Variante.Gemischt && minJg >= 8);
                g.Kursart = ReligionPhilo.Contains(g.SchildFach) ? "PUK" : minJg >= 8 ? "WPII" : "PUK";
            }
            // Ohne Kurs ist eine Gruppe nur über Klasse + Fach + Fachlehrer erkennbar. Hat ein Mitglied das Fach schon
            // ohne Kurs (Klassenverband oder andere Gruppe), bekommt die Gruppe trotzdem einen Kurs.
            var erzwungen = new List<string>();
            foreach (var g in gruppenListe.Where(g2 => !g2.MitKurs))
            {
                if (g.Mitglieder.Any(s => s.OhneKurs.Contains(g.SchildFach))) { g.MitKurs = true; erzwungen.Add(g.Key); continue; }
                foreach (var s in g.Mitglieder) s.OhneKurs.Add(g.SchildFach);
            }
            if (erzwungen.Count > 0)
                erg.Stolpersteine.Add($"{erzwungen.Count} Sek-I-Gruppe(n) als Kurs statt ohne Kurs, weil ihr Fach für dieselben " +
                                      "Schüler auch im Klassenverband oder in einer anderen Gruppe ohne Kurs vorkommt " +
                                      $"(z. B. {string.Join(", ", erzwungen.Take(3))}).");

            var vergeben = new List<(string Bez, List<string> Jg)>();
            foreach (var g in gruppenListe)
            {
                if (!g.MitKurs) continue;
                int nr = ZerlegePlanFach(g.Fach.Haupt()).Nr;

                if (g.Klassen.Count == 1 && !parallel.Contains(g))
                {
                    g.KursKlasse = g.Klassen.First();
                    g.KursBez = $"{g.SchildFach}-{g.KursKlasse}";
                }
                else
                {
                    string bez = g.SchildFach + (nr > 0 ? nr.ToString(CultureInfo.InvariantCulture) : "");
                    string kand = bez;
                    for (char c = 'b'; vergeben.Any(v => v.Bez.Equals(kand, StringComparison.OrdinalIgnoreCase) && v.Jg.Intersect(g.Jahrgaenge).Any()); c++)
                        kand = bez + c;
                    g.KursBez = kand;
                }
                vergeben.Add((g.KursBez, g.Jahrgaenge));
            }
            var mehrfach = vergeben.GroupBy(v => v.Bez, StringComparer.OrdinalIgnoreCase)
                                   .Where(gr => gr.Select(v => string.Join(",", v.Jg)).Distinct().Count() > 1)
                                   .Select(gr => gr.Key).Take(3).ToList();
            if (mehrfach.Count > 0)
                erg.Stolpersteine.Add($"Gleiche Kursbezeichnung in mehreren Jahrgängen: {string.Join(", ", mehrfach)} " +
                                      "(Kursidentität = KursBez + Jahr + Abschnitt + Jahrgang).");

            foreach (var g in gruppen.Values)
            {
                string leiter = K(g.Lehrer.Haupt()), zusatz = K(g.Lehrer.Rest().FirstOrDefault() ?? "");
                foreach (var s in g.Mitglieder.Distinct())
                    s.Leistungen.Add(new PLeistung
                    {
                        Fach = g.SchildFach, Lehrer = leiter, Kursart = g.Kursart, Kurs = g.MitKurs ? g.KursBez : "",
                        Wst = g.Zeiten.Count, Zusatz = zusatz
                    });
            }
            erg.KurseSek1 = gruppen.Values.Count(g => g.MitKurs);
            erg.GruppenOhneKurs = gruppen.Values.Count(g => !g.MitKurs);
            erg.Stolpersteine.Add(opt.Sek1 switch
            {
                SchildSek1Variante.MitKurs => "Sek-I-Gruppen (Religion, WP …) alle als Kurse.",
                SchildSek1Variante.OhneKurs => "Sek-I-Gruppen (Religion, WP …) alle ohne Kurs – nur Fach + Fachlehrer.",
                _ => "Sek-I-Gruppen: Jahrgang 05–07 ohne Kurs (nur Fach + Fachlehrer), 08–10 als Kurse."
            });

            // ---------- Oberstufe: Kurswahl wie der bisherige Seed ----------
            foreach (var stufeGruppe in kurse.Values.GroupBy(k => k.Stufe).OrderBy(gr => gr.Key))
            {
                string stufe = stufeGruppe.Key;
                int jgNum = stufe switch { "EF" => 11, "Q1" => 12, _ => 13 };
                var stufeKurse = stufeGruppe.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList();
                bool qPhase = stufe is "Q1" or "Q2";
                int zielKurse = qPhase ? 10 : 11;
                int anzahl = Math.Clamp((int)Math.Round(stufeKurse.Count * 20.0 / zielKurse), 20, 150);
                var nachFach = stufeKurse.GroupBy(k => k.WahlFach, StringComparer.OrdinalIgnoreCase)
                                         .ToDictionary(gr => gr.Key, gr => gr.ToList(), StringComparer.OrdinalIgnoreCase);
                var lks = stufeKurse.Where(k => k.Art == "LK").ToList();

                void Waehle(PSchueler s, int zielAnzahl, bool mitLk)
                {
                    var belegt = new HashSet<(int, int)>();
                    var belegteFaecher = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var gewaehlt = new List<KursInfo>();
                    bool Versuche(KursInfo k)
                    {
                        if (belegteFaecher.Contains(k.WahlFach) || k.Zeiten.Overlaps(belegt)) return false;
                        gewaehlt.Add(k); belegteFaecher.Add(k.WahlFach); belegt.UnionWith(k.Zeiten);
                        return true;
                    }
                    if (mitLk)
                        foreach (var lk in lks.OrderBy(k => k.Belegt).ThenBy(_ => rnd.Next()))
                        {
                            if (gewaehlt.Count >= 2) break;
                            Versuche(lk);
                        }
                    var reihenfolge = new[] { "D", "M", "E" }.Concat(nachFach.Keys.OrderBy(_ => rnd.Next())).ToList();
                    foreach (var fach in reihenfolge)
                    {
                        if (gewaehlt.Count >= zielAnzahl) break;
                        if (!nachFach.TryGetValue(fach, out var angebote)) continue;
                        foreach (var angebot in angebote.Where(k => k.Art != "LK").OrderBy(k => k.Belegt).ThenBy(_ => rnd.Next()))
                            if (Versuche(angebot)) break;
                    }
                    int nLk = 0, nGks = 0;
                    foreach (var k in gewaehlt)
                    {
                        k.Belegt++;
                        string kursart = k.Art switch
                        {
                            "LK" => ++nLk == 1 ? "LK1" : "LK2",
                            "VTF" => "VTF",
                            "ZK" => "ZK",
                            _ => (qPhase ? nGks++ < 2 : k.SchildFach is "D" or "M" or "E") ? "GKS" : "GKM"
                        };
                        var weitere = k.Lehrer.Rest();
                        s.Leistungen.Add(new PLeistung
                        {
                            Fach = k.SchildFach, Lehrer = k.Kooperation ? "" : K(k.Lehrer.Haupt()), Kursart = kursart,
                            Kurs = k.KursBez, Wst = k.Zeiten.Count, Zusatz = K(weitere.FirstOrDefault() ?? ""), Jahrgang = stufe
                        });
                    }
                }

                for (int i = 0; i < anzahl; i++) Waehle(Neu(stufe, stufe, jgNum), zielKurse, qPhase);
                if (stufe == "EF")
                    for (int i = 0; i < 2; i++) { var s3 = Neu(stufe, stufe, jgNum); s3.Status = 3; }   // beurlaubt (z. B. Auslandsjahr)
                if (stufe == "Q1")
                {
                    var s6 = Neu(stufe, stufe, jgNum); s6.Status = 6; s6.Stammschule = "165432";       // Kooperationsschüler
                    Waehle(s6, 3, false);
                }
            }
            erg.KurseSek2 = kurse.Count;

            // ---------- Projektkurs ohne Gegenstück im Plan (Stolperstein 5.2a-2: „keine Zuordnung“) ----------
            (string KursBez, string Fach, string Leiter, string Stufe)? projekt = null;
            string? pjStufe = kurse.Values.Any(k => k.Stufe == "Q1") ? "Q1"
                            : kurse.Values.Select(k => k.Stufe).Distinct().OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
            var pjBasis = pjStufe == null ? null
                : kurse.Values.Where(k => k.Stufe == pjStufe && !k.Kooperation && k.Art == "GK")
                              .OrderBy(k => k.KursBez, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (pjBasis != null)
            {
                var pj = (KursBez: $"{pjBasis.SchildFach}-PJ1", Fach: pjBasis.SchildFach, Leiter: K(pjBasis.Lehrer.Haupt()), Stufe: pjBasis.Stufe);
                projekt = pj;
                foreach (var teilnehmer in schueler.Where(x => x.Klasse == pj.Stufe && x.Status == 2).Take(12))
                    teilnehmer.Leistungen.Add(new PLeistung
                    {
                        Fach = pj.Fach, Lehrer = pj.Leiter, Kursart = "PJK", Kurs = pj.KursBez, Wst = 2, Jahrgang = pj.Stufe
                    });
                erg.Stolpersteine.Add($"Projektkurs „{pj.KursBez}“ ({pj.Stufe}, Kursart PJK) ohne Gegenstück im Plan – im Reiter " +
                                      "„Zuordnung“ auf „keine Zuordnung“ setzen.");
            }

            // ---------- #Klasse-Paar ----------
            var paarJg = klassenListe.GroupBy(kl => kl.JgNum).FirstOrDefault(gr => gr.Count() >= 2);
            if (paarJg != null)
            {
                var a = paarJg.ElementAt(0).Schueler.FirstOrDefault(x => x.Status == 2);
                var b = paarJg.ElementAt(1).Schueler.FirstOrDefault(x => x.Status == 2);
                if (a != null && b != null)
                {
                    b.Nachname = a.Nachname; b.Vorname = a.Vorname; b.Geb = a.Geb; b.Geschlecht = a.Geschlecht;
                    a.Zusatz = a.Klasse; b.Zusatz = b.Klasse;
                    erg.Stolpersteine.Add($"Zwei Schüler mit gleichem Namen und Geburtsdatum in {a.Klasse} und {b.Klasse} " +
                                          "(„#Klasse“ am Nachnamen).");
                }
            }
            erg.Stolpersteine.Add("Einzelne Status: 0 Neuaufnahme (Aufnahme in 14 Tagen), 1 Warteliste, 3 beurlaubt (2×, ohne " +
                                  "Leistungsdaten), 6 extern (Q1, nur Kurse), 9 Abgang ohne Abschluss.");
            foreach (var kv in fachAbweichung)
                erg.Stolpersteine.Add($"Fach {kv.Key} im Plan heißt in SchILD „{kv.Value}“.");

            long id = 4711;
            foreach (var s in schueler.Where(x => x.Status != 1)) s.Id = id++;

            // ---------- Dateien schreiben ----------
            var std = SchildProfil.Standard();
            string Datei(SchildDateiArt art) => std.Fuer(art).Datei;
            void Schreibe(string name, string[] kopf, IEnumerable<string[]> zeilen)
            {
                var sb = new StringBuilder();
                sb.Append(string.Join("|", kopf)).Append("\r\n");
                foreach (var z in zeilen)
                {
                    var zellen = new string[kopf.Length];
                    for (int i = 0; i < kopf.Length; i++) zellen[i] = i < z.Length ? SchildText.Zelle(z[i], "|") : "";
                    sb.Append(string.Join("|", zellen)).Append("\r\n");
                }
                byte[] bytes;
                if (opt.Utf8MitBom)
                {
                    var enc = new UTF8Encoding(true);
                    bytes = enc.GetPreamble().Concat(enc.GetBytes(sb.ToString())).ToArray();
                }
                else bytes = SchildText.KodiereCp1252(sb.ToString());
                File.WriteAllBytes(Path.Combine(ziel, name), bytes);
                erg.Dateien.Add(name);
            }
            string D(DateOnly d) => SchildText.DatumText(d);

            // Klassen.dat
            var stufenMitSchuelern = schueler.Where(x => Oberstufe.Contains(x.Klasse)).Select(x => x.Klasse)
                                             .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var klassenZeilen = new List<string[]>();
            foreach (var kl in klassenListe)
            {
                string folge = kl.JgNum < 10 ? $"{kl.JgNum + 1:00}{kl.Name[2..]}" : stufenMitSchuelern.Contains("EF") ? "EF" : "";
                if (kl.JgNum < 10 && !klassen.ContainsKey(folge)) folge = "";
                klassenZeilen.Add(new[] { kl.Name, kl.Name, "", kl.Jahrgang, folge, K(kl.Lehrer.Haupt()), "", "", "GY", "", J, A });
            }
            foreach (var st in stufenMitSchuelern)
            {
                var z = new Zaehler();
                foreach (var k in kurse.Values.Where(k2 => k2.Stufe == st && !k2.Kooperation)) z.Plus(k.Lehrer.Haupt());
                string folge = st == "EF" ? "Q1" : st == "Q1" ? "Q2" : "";
                klassenZeilen.Add(new[] { st, st, "", st, folge, K(z.Haupt()), "", "", "GY", "", J, A });
            }
            Schreibe(Datei(SchildDateiArt.Klassen),
                new[] { "InternBez", "StatistikBez", "SonstigeBez", "Jahrgang", "Folgeklasse", "Klassenlehrer", "OrgForm",
                        "Klassenart", "Gliederung", "Fachklasse", "Jahr", "Abschnitt" }, klassenZeilen);
            erg.Klassen = klassenZeilen.Count;

            // Kurse.dat (neues Format)
            var kursZeilen = new List<(string[] Werte, List<(string Krz, int Wst)> Zk)>();
            foreach (var kl in klassenListe)   // Klassenverband (nur Klasse)
                foreach (var kv in kl.Kv)
                    kursZeilen.Add((new[] { "", kl.Name, J, A, "", kv.SchildFach, "",
                                            kv.Zeiten.Count.ToString(CultureInfo.InvariantCulture), "", K(kv.Lehrer.Haupt()), "N", "" },
                                    kv.Lehrer.Rest().Select(x => (K(x), kv.Zeiten.Count)).ToList()));
            foreach (var g in gruppen.Values.Where(g2 => g2.MitKurs).OrderBy(g2 => g2.KursBez, StringComparer.OrdinalIgnoreCase))
                kursZeilen.Add((new[] { g.KursBez, g.KursKlasse, J, A, string.Join(",", g.Jahrgaenge), g.SchildFach, "",
                                        g.Zeiten.Count.ToString(CultureInfo.InvariantCulture), "", K(g.Lehrer.Haupt()), "N", "" },
                                g.Lehrer.Rest().Select(x => (K(x), g.Zeiten.Count)).ToList()));
            foreach (var k in kurse.Values.OrderBy(k2 => k2.Stufe).ThenBy(k2 => k2.KursBez, StringComparer.OrdinalIgnoreCase))
                kursZeilen.Add((new[] { k.KursBez, "", J, A, k.Stufe, k.SchildFach, k.Art,
                                        k.Zeiten.Count.ToString(CultureInfo.InvariantCulture), "",
                                        k.Kooperation ? "" : K(k.Lehrer.Haupt()), "N", k.Kooperation ? "165432" : "" },
                                k.Kooperation ? new List<(string, int)>() : k.Lehrer.Rest().Select(x => (K(x), k.Zeiten.Count)).ToList()));
            if (projekt is { } pkurs)
                kursZeilen.Add((new[] { pkurs.KursBez, "", J, A, pkurs.Stufe, pkurs.Fach, "PJK", "2", "", pkurs.Leiter, "N", "" },
                                new List<(string, int)>()));
            int maxZk = Math.Max(1, kursZeilen.Select(z => z.Zk.Count).DefaultIfEmpty(0).Max());
            var kursKopf = new List<string> { "KursBez", "Klasse", "Jahr", "Abschnitt", "Jahrgang", "Fach", "Kursart", "Wochenstd.",
                                              "Wochenstd. KL", "Kursleiter", "Epochenunterricht", "Schulnr", "Wochenstd. ZK", "Zusatzkraft" };
            for (int i = 2; i <= maxZk; i++) { kursKopf.Add("Wochenstd ZK"); kursKopf.Add("Weitere Zusatzkraft"); }
            Schreibe(Datei(SchildDateiArt.Kurse), kursKopf.ToArray(), kursZeilen.Select(z =>
            {
                var werte = z.Werte.ToList();
                foreach (var (zkKrz, zkWst) in z.Zk) { werte.Add(zkWst.ToString(CultureInfo.InvariantCulture)); werte.Add(zkKrz); }
                return werte.ToArray();
            }));

            // SchuelerBasisdaten.dat (50 Spalten)
            string[] basisKopf =
            {
                "Nachname", "Vorname", "Geburtsdatum", "Geschlecht", "Status", "PLZ", "Ort", "Straße", "Aussiedler",
                "1. Staatsang.", "Konfession", "StatistikKrz Konfession", "Aufnahmedatum", "Abmeldedatum Religionsunterricht",
                "Anmeldedatum Religionsunterricht", "Schulpflicht erf.", "Reform-Pädagogik", "Nr. Stammschule", "Jahr",
                "Abschnitt", "Jahrgang", "Klasse", "Schulgliederung", "OrgForm", "Klassenart", "Fachklasse", "Noch frei",
                "Verpflichtung Sprachförderkurs", "Teilnahme Sprachförderkurs", "Einschulungsjahr", "Übergangsempf. JG5",
                "Jahr Wechsel S1", "1. Schulform S1", "Jahr Wechsel S2", "Förderschwerpunkt", "2. Förderschwerpunkt",
                "Schwerstbehinderung", "Autist", "LS Schulnr.", "LS Schulform", "Herkunft", "LS Entlassdatum", "LS Jahrgang",
                "LS Versetzung", "LS Reformpädagogik", "LS Gliederung", "LS Fachklasse", "LS Abschluss", "Abschluss",
                "Schulnr. neue Schule"
            };
            var sortiert = schueler.OrderBy(x => x.Klasse.Length == 0 ? "~" : x.Klasse, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(x => x.Nachname, StringComparer.CurrentCulture).ThenBy(x => x.Vorname, StringComparer.CurrentCulture).ToList();
            Schreibe(Datei(SchildDateiArt.Basisdaten), basisKopf, sortiert.Select(x =>
            {
                var z = new string[50];
                for (int i = 0; i < z.Length; i++) z[i] = "";
                var aufnahme = x.Aufnahme ?? new DateOnly(Math.Min(jahr, jahr - (x.JgNum - 5)), 8, 1);
                z[0] = x.NameFeld; z[1] = x.Vorname; z[2] = D(x.Geb); z[3] = x.Geschlecht;
                z[4] = x.Status.ToString(CultureInfo.InvariantCulture); z[8] = "N"; z[12] = D(aufnahme); z[15] = "N";
                z[17] = x.Stammschule; z[18] = J; z[19] = A; z[20] = x.Jahrgang; z[21] = x.Klasse; z[22] = "GY";
                z[27] = "N"; z[28] = "N"; z[29] = (x.Geb.Year + 6 + (x.Geb.Month >= 7 ? 1 : 0)).ToString(CultureInfo.InvariantCulture);
                z[36] = "N"; z[37] = "N";
                return z;
            }));
            erg.Schueler = schueler.Count;

            // SchuelerLeistungsdaten.dat (19 Spalten) – keine Noten, keine Fehlstunden
            string[] leistKopf =
            {
                "Nachname", "Vorname", "Geburtsdatum", "Jahr", "Abschnitt", "Fach", "Fachlehrer", "Kursart", "Kurs", "Note",
                "Abiturfach", "Wochenstd.", "Externe Schulnr.", "Zusatzkraft", "Wochenstd. ZK", "Jahrgang", "Jahrgänge",
                "Fehlstd.", "unentsch. Fehlstd."
            };
            var leistZeilen = new List<string[]>();
            foreach (var x in sortiert.Where(x2 => x2.Status != 1))
                foreach (var l in x.Leistungen)
                    leistZeilen.Add(new[]
                    {
                        x.NameFeld, x.Vorname, D(x.Geb), J, A, l.Fach, l.Lehrer, l.Kursart, l.Kurs, "", "",
                        l.Wst.ToString(CultureInfo.InvariantCulture), "", l.Zusatz,
                        l.Zusatz.Length > 0 ? l.Wst.ToString(CultureInfo.InvariantCulture) : "", l.Jahrgang, "", "", ""
                    });
            Schreibe(Datei(SchildDateiArt.Leistungsdaten), leistKopf, leistZeilen);
            erg.Leistungszeilen = leistZeilen.Count;

            // SchuelerLernplattformen.dat (optional, SchILD-ID)
            if (opt.MitIds)
                Schreibe(Datei(SchildDateiArt.Lernplattformen),
                    new[] { "Nachname", "Vorname", "Geburtsdatum", "ID", "Lernplattform", "Benutzername", "Initialkennwort" },
                    sortiert.Where(x => x.Id > 0).Select(x => new[]
                    {
                        x.NameFeld, x.Vorname, D(x.Geb), x.Id.ToString(CultureInfo.InvariantCulture), "Lernplattform (Pseudo)",
                        "pseudo" + x.Id.ToString(CultureInfo.InvariantCulture), "------"
                    }));

            // Lehrkraefte.dat (23 Spalten) – nur fiktive Namen, keine Adressen/Geburtsdaten
            var lehrerSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in klassenZeilen) if (z[5].Length > 0) lehrerSet.Add(z[5]);
            foreach (var z in kursZeilen) { if (z.Werte[9].Length > 0) lehrerSet.Add(z.Werte[9]); foreach (var zk in z.Zk) if (zk.Krz.Length > 0) lehrerSet.Add(zk.Krz); }
            foreach (var z in leistZeilen) { if (z[6].Length > 0) lehrerSet.Add(z[6]); if (z[13].Length > 0) lehrerSet.Add(z[13]); }
            int nrL = 0;
            Schreibe(Datei(SchildDateiArt.Lehrkraefte),
                new[] { "InternKrz", "StatistikKrz", "Anrede", "Titel", "Nachname", "Vorname", "Geschlecht", "Geburtsdatum",
                        "Staatsang", "PLZ", "Ort", "Straße", "Tel. Festnetz", "Tel. mobil", "E-Mail", "Rechtsverhältnis",
                        "Beschäftigungsart", "Einsatzstatus", "Pflichtstunden-Soll", "Schulleitung", "Statistik-relevant",
                        "Stammschulnr.", "dienstl. E-Mail" },
                lehrerSet.Select(l =>
                {
                    nrL++;
                    bool w = rnd.Next(2) == 0;
                    var z = new string[23];
                    for (int i = 0; i < z.Length; i++) z[i] = "";
                    z[0] = l; z[1] = "P" + nrL.ToString("000", CultureInfo.InvariantCulture); z[2] = w ? "Frau" : "Herr";
                    z[4] = Nachnamen[rnd.Next(Nachnamen.Length)];
                    z[5] = w ? LehrerVornamenW[rnd.Next(LehrerVornamenW.Length)] : LehrerVornamenM[rnd.Next(LehrerVornamenM.Length)];
                    z[6] = w ? "w" : "m"; z[20] = "J";
                    return z;
                }).ToList());
            erg.Lehrkraefte = lehrerSet.Count;

            // Faecher.dat (9 Spalten)
            var fachSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in kursZeilen) fachSet.Add(z.Werte[5]);
            foreach (var z in leistZeilen) fachSet.Add(z[5]);
            int nrF = 0;
            Schreibe(Datei(SchildDateiArt.Faecher),
                new[] { "InternKrz", "StatistikKrz", "Bezeichnung", "BezeichnungZeugnis", "BezeichnungÜZeugnis",
                        "Unterrichtssprache", "Sortierung S1", "Sortierung S2", "Gewichtung" },
                fachSet.Where(f => f.Length > 0).Select(f =>
                {
                    nrF++;
                    string bez = FachNamen.TryGetValue(f, out var n) ? n : f;
                    string stat = new string(f.Where(char.IsLetter).Take(2).ToArray()).ToUpperInvariant();
                    return new[] { f, stat.Length > 0 ? stat : f, bez, bez, "", "D", nrF.ToString(CultureInfo.InvariantCulture),
                                   nrF.ToString(CultureInfo.InvariantCulture), "" };
                }).ToList());
            erg.Faecher = fachSet.Count;

            // Jahrgaenge.dat (3 Spalten, chronologisch)
            var jahrgaenge = klassenListe.Select(kl => kl.Jahrgang).Distinct().OrderBy(x => x).Concat(stufenMitSchuelern).ToList();
            Schreibe(Datei(SchildDateiArt.Jahrgaenge), new[] { "InternKrz", "StatistikKrz", "Gliederung" },
                jahrgaenge.Select(j => new[] { j, j, "GY" }));

            // Marker zuletzt (Prüfsummen über die fertigen Dateien)
            SchildPseudoMarker.Schreiben(ziel, new SchildPseudoMarker.Marker
            {
                ErzeugtAm = DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                Plan = plan.Bezeichnung ?? "",
                Schuljahr = schuljahr,
                Abschnitt = abschnitt,
                Startwert = opt.Startwert,
                Zeichensatz = opt.Utf8MitBom ? "UTF-8 mit BOM" : "Windows-1252 (ANSI)",
                Sek1Variante = opt.Sek1.ToString(),
                Stolpersteine = erg.Stolpersteine.ToList()
            }, erg.Dateien);
            return erg;
        }

        // =================================================================
        //  Hilfen
        // =================================================================

        /// <summary>„2026/27“ → 2026; sonst Jahr des laufenden Schuljahres.</summary>
        public static int Jahr(string schuljahr)
        {
            var m = Regex.Match(schuljahr ?? "", @"(\d{4})");
            if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var heute = DateTime.Today;
            return heute.Month >= 8 ? heute.Year : heute.Year - 1;
        }

        /// <summary>
        /// Plan-Fach zerlegen: „D G1“ → (D, GK, 1), „M L2“ → (M, LK, 2), „E V1“/„E Ver1“ → (E, VTF, 1),
        /// „IF K1“ → (IF, "", 1), „PP1“ → (PP, "", 1), „ER“ → (ER, "", 0).
        /// </summary>
        public static (string Basis, string Art, int Nr) ZerlegePlanFach(string fach)
        {
            string f = Regex.Replace((fach ?? "").Trim(), @"\s+", " ");
            var m = KursFach.Match(f);
            if (m.Success)
            {
                string art = m.Groups["a"].Value.ToUpperInvariant() switch
                {
                    "G" => "GK", "L" => "LK", "V" or "VER" => "VTF", "Z" => "ZK", _ => ""
                };
                return (m.Groups["f"].Value.ToUpperInvariant(), art,
                        int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture));
            }
            int sp = f.IndexOf(' ');
            if (sp > 0)
            {
                var z = Regex.Match(f[(sp + 1)..], @"(\d+)");
                return (f[..sp].ToUpperInvariant(), "", z.Success ? int.Parse(z.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
            }
            var e = EndZiffern.Match(f);
            if (e.Success && e.Groups["f"].Value.Length > 0)
                return (e.Groups["f"].Value.ToUpperInvariant(), "", int.Parse(e.Groups["n"].Value, CultureInfo.InvariantCulture));
            return (f.ToUpperInvariant(), "", 0);
        }

        /// <summary>Zerlegt Gruppen in Komponenten, die sich zeitlich (direkt oder über andere) überschneiden.</summary>
        private static List<List<GruppeInfo>> Komponenten(List<GruppeInfo> gruppen)
        {
            var erg = new List<List<GruppeInfo>>();
            var gesehen = new HashSet<GruppeInfo>();
            foreach (var start in gruppen)
            {
                if (!gesehen.Add(start)) continue;
                var komp = new List<GruppeInfo> { start };
                var stapel = new Stack<GruppeInfo>();
                stapel.Push(start);
                while (stapel.Count > 0)
                {
                    var g = stapel.Pop();
                    foreach (var h in gruppen)
                        if (!gesehen.Contains(h) && g.Zeiten.Overlaps(h.Zeiten)) { gesehen.Add(h); komp.Add(h); stapel.Push(h); }
                }
                erg.Add(komp);
            }
            return erg;
        }
    }
}
