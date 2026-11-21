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
//
//  Phase 5.2b-1: Zugehörigkeiten tragen Quelle „Seed" (ohne Datum = ganzes Schuljahr).
//  WechselAnlegen (nur Entwicklung, seed/pseudodaten?wechsel=1) legt danach einige
//  Wechsel mit von–bis an, damit die Gültigkeit in der App sichtbar wird.
//  Phase 5.2b-2: Der SchILD-Import nutzt ZerlegeKursFach/HaeufigsteLehrkraft für neue Lerngruppen und
//  LoescheVorhandene/SchuelerKontenLoesen für „Seed-Schüler entfernen“.
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
                    zuordnungen.Add(new Zugehoerigkeit { Schueler = s, Lerngruppe = kl, Schuljahr = schuljahr, Quelle = Gueltigkeit.QuelleSeed });
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
                    zuordnungen.Add(new Zugehoerigkeit { Schueler = s, Lerngruppe = g, Schuljahr = schuljahr, Quelle = Gueltigkeit.QuelleSeed });
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
                zuordnungen.Add(new Zugehoerigkeit { Schueler = s, Lerngruppe = kl, Schuljahr = schuljahr, Quelle = Gueltigkeit.QuelleSeed });
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
                    zuordnungen.Add(new Zugehoerigkeit { Schueler = s, Lerngruppe = kurs.Lg, Schuljahr = schuljahr, Quelle = Gueltigkeit.QuelleSeed });
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
    internal static (string FachBasis, Kursart Kursart, string Fach) ZerlegeKursFach(string kursFach)
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

    internal static string? HaeufigsteLehrkraft(IEnumerable<GrundplanSlot> slots)
        => slots.Where(s => !string.IsNullOrWhiteSpace(s.Lehrer))
                .GroupBy(s => s.Lehrer, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault();

    /// <summary>
    /// Entfernt alle Pseudo-Schüler samt Klassenbuchdaten, alle Zugehörigkeiten, die Pseudo-Lerngruppen und alle
    /// Gruppenlisten – wie vor einem neuen Seed. Seit 5.2b-2 auch für „Seed-Schüler entfernen“ (nur Entwicklung).
    /// </summary>
    internal static void LoescheVorhandene(AppDbContext db)
    {
        // Reihenfolge wegen FK: erst Klassenbuch (Phase 3.1) und Zuordnungen, dann
        // Schüler/Lerngruppen. Klassenbuchdaten der Pseudo-Schüler passen nicht zu den neuen
        // Pseudo-Schülern und werden mit gelöscht – ebenso alle Gruppenlisten (Typ Gruppe):
        // die Fachlehrkräfte stellen sie mit den neuen Schülern neu zusammen.
        db.Anwesenheiten.RemoveRange(db.Anwesenheiten.Where(a => a.Schueler!.IstPseudo));
        db.Entschuldigungen.RemoveRange(db.Entschuldigungen.Where(e => e.IstPseudo));   // Phase 3.2
        db.KlassenbuchEintraege.RemoveRange(db.KlassenbuchEintraege.Where(e => e.IstPseudo)); // Phase 3.4
        db.Lektionen.RemoveRange(db.Lektionen.Where(l => l.IstPseudo));
        db.KlassenbuchProtokolle.RemoveRange(db.KlassenbuchProtokolle.Where(p => p.IstPseudo));
        db.Zugehoerigkeiten.RemoveRange(db.Zugehoerigkeiten.Where(z => true));
        db.Schueler.RemoveRange(db.Schueler.Where(s => s.IstPseudo));
        db.Lerngruppen.RemoveRange(db.Lerngruppen.Where(l => l.IstPseudo || l.Typ == LerngruppeTyp.Gruppe));
        db.Lehrkraefte.RemoveRange(db.Lehrkraefte.Where(l => l.IstPseudo));   // Phase 5.2c: Pseudo-Namen der Lehrkräfte
        db.SaveChanges();
    }

    public record Ergebnis(int Schueler, int Klassen, int Kurse, int Zuordnungen);

    /// <summary>
    /// Vor dem Neu-Seeden bzw. „Seed-Schüler entfernen“ (5.2b-2): Schülerkonten, die auf Pseudo-Schüler (oder auf gar
    /// keinen Schüler) zeigen, werden gelöscht – außer den Testkonten s05A/sQ1, die nur gelöst und von
    /// /api/auth/seed-konten neu verknüpft werden. Konten echter Schüler bleiben unberührt.
    /// (Bis 5.2b-1 im StammdatenController.)
    /// </summary>
    public static object SchuelerKontenLoesen(AppDbContext db)
    {
        var testkonten = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "s05A", "sQ1" };
        var echteIds = db.Schueler.Where(x => !x.IstPseudo).Select(x => x.Id).ToHashSet();
        int entfernt = 0, geloest = 0;
        foreach (var k in db.Benutzer.Where(b => b.Rolle == BenutzerRolle.Schueler).ToList())
        {
            if (k.SchuelerId is int sid && echteIds.Contains(sid)) continue;
            if (testkonten.Contains(k.Benutzername))
            {
                if (k.SchuelerId == null) continue;
                k.SchuelerId = null;
                k.SicherheitsStempel = KontenService.NeuerStempel();
                geloest++;
            }
            else
            {
                KontenService.Protokoll(db, "System", KontenService.Aktion.Geloescht, k, "Pseudodaten neu erzeugt");
                db.Benutzer.Remove(k);
                entfernt++;
            }
        }
        db.SaveChanges();
        return new
        {
            entfernt,
            geloest,
            hinweis = geloest > 0 ? "Testkonten s05A/sQ1 neu verknüpfen: POST /api/auth/seed-konten" : null
        };
    }

    // =================================================================
    //  Phase 5.2b-1: Wechsel mit Gültigkeit (nur Entwicklung)
    // =================================================================

    /// <summary>
    /// Legt nach <see cref="GenerierenAusGrundplan"/> Wechsel zum <paramref name="stichtag"/> an: alte
    /// Zugehörigkeit bis zum Vortag, neue ab dem Stichtag. Zwei Klassenwechsel (Sek I), zwei Kurswechsel
    /// (Oberstufe, gleiches Fach, überschneidungsfrei), ein Abgang, eine Neuaufnahme und eine Sek-I-Gruppe,
    /// die bis zum Vortag eine Liste „von Hand" hat und ab dem Stichtag aus SchILD kommt (81). Betroffen sind
    /// nie die ersten Schüler einer Klasse/Stufe (die Testkonten s05A/sQ1 bleiben, wie sie sind).
    /// Liefert je Wechsel eine Zeile für die Antwort.
    /// </summary>
    public static List<string> WechselAnlegen(
        AppDbContext db, GrundplanVersion version, List<GrundplanSlot> slots, DateOnly stichtag, int seed = 12345)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        string sj = version.Schuljahr;
        var vortag = stichtag.AddDays(-1);
        string V = vortag.ToString("dd.MM.yyyy");
        string S = stichtag.ToString("dd.MM.yyyy");
        var meldungen = new List<string>();

        var lerngruppen = db.Lerngruppen.Where(l => l.Schuljahr == sj && l.IstPseudo).ToList();
        var zug = db.Zugehoerigkeiten.Where(z => z.Schuljahr == sj).ToList();
        var schueler = db.Schueler.Where(x => x.IstPseudo).ToList().ToDictionary(x => x.Id);
        string Name(int id) => schueler.TryGetValue(id, out var x) ? $"{x.Vorname} {x.Nachname} (Id {id})" : $"Id {id}";
        var benutzt = new HashSet<int>();

        Zugehoerigkeit Neu(int schuelerId, Lerngruppe lg, DateOnly? von, DateOnly? bis, string quelle)
        {
            var z = new Zugehoerigkeit
            {
                SchuelerId = schuelerId, LerngruppeId = lg.Id, Schuljahr = sj,
                GueltigVon = von, GueltigBis = bis, Quelle = quelle
            };
            db.Zugehoerigkeiten.Add(z);
            zug.Add(z);
            return z;
        }
        // Mitglieder einer Lerngruppe, die am Stichtag gelten – die letzten zuerst.
        List<Zugehoerigkeit> In(Lerngruppe lg) => zug
            .Where(z => z.LerngruppeId == lg.Id && z.GiltAm(stichtag))
            .OrderByDescending(z => z.SchuelerId)
            .ToList();

        var klassen = lerngruppen.Where(l => l.Typ == LerngruppeTyp.Klasse)
            .OrderBy(l => l.Bezeichnung, cmp).ToList();

        // ---- 1. Zwei Klassenwechsel: je Jahrgang mit mindestens zwei Klassen 1. → 2. Klasse ----
        int n = 0;
        foreach (var jg in klassen.GroupBy(k => k.Stufe, cmp).Where(g => g.Count() >= 2))
        {
            if (n >= 2) break;
            var a = jg.ElementAt(0);
            var b = jg.ElementAt(1);
            var z = In(a).FirstOrDefault(x => !benutzt.Contains(x.SchuelerId));
            if (z == null) continue;
            z.GueltigBis = vortag;
            Neu(z.SchuelerId, b, stichtag, null, Gueltigkeit.QuelleSeed);
            benutzt.Add(z.SchuelerId);
            meldungen.Add($"Klassenwechsel: {Name(z.SchuelerId)} – {a.Bezeichnung} bis {V}, ab {S} {b.Bezeichnung}");
            n++;
        }

        // ---- 2. Zwei Kurswechsel (Oberstufe): GK → anderer GK desselben Fachs, überschneidungsfrei ----
        var zeiten = slots
            .Where(x => x.Art == "Unterricht" && x.LerngruppeTyp == "Kurs" && !string.IsNullOrWhiteSpace(x.LerngruppeKey))
            .GroupBy(x => x.LerngruppeKey.Trim(), cmp)
            .ToDictionary(g => g.Key, g => new HashSet<(int, int)>(g.Select(x => (x.WTagIndex, x.Stunde))), cmp);
        HashSet<(int, int)> ZeitenVon(Lerngruppe lg)
            => zeiten.TryGetValue(lg.Bezeichnung.Trim(), out var t) ? t : new HashSet<(int, int)>();
        var kurse = lerngruppen.Where(l => l.Typ == LerngruppeTyp.Kurs).ToList();
        n = 0;
        foreach (var stufe in kurse.Select(k => k.Stufe).Distinct(cmp).OrderBy(x => x, cmp).ToList())
        {
            if (n >= 2) break;
            var stufeKurse = kurse.Where(k => cmp.Equals(k.Stufe, stufe)).ToDictionary(k => k.Id);
            var jeSchueler = zug.Where(z => stufeKurse.ContainsKey(z.LerngruppeId) && z.GiltAm(stichtag))
                .GroupBy(z => z.SchuelerId)
                .OrderByDescending(g => g.Key)
                .ToList();
            bool gefunden = false;
            foreach (var g in jeSchueler)
            {
                if (benutzt.Contains(g.Key)) continue;
                var eigene = g.ToList();
                var eigeneIds = eigene.Select(z => z.LerngruppeId).ToHashSet();
                foreach (var alt in eigene)
                {
                    var x = stufeKurse[alt.LerngruppeId];
                    if (x.Kursart != Kursart.GK) continue;
                    var belegt = new HashSet<(int, int)>();
                    foreach (var o in eigene)
                        if (!ReferenceEquals(o, alt)) belegt.UnionWith(ZeitenVon(stufeKurse[o.LerngruppeId]));
                    var y = stufeKurse.Values
                        .Where(k => k.Id != x.Id && !eigeneIds.Contains(k.Id) && k.Kursart == x.Kursart
                                    && cmp.Equals(k.Fach ?? "", x.Fach ?? "") && !ZeitenVon(k).Overlaps(belegt))
                        .OrderBy(k => k.Bezeichnung, cmp)
                        .FirstOrDefault();
                    if (y == null) continue;
                    alt.GueltigBis = vortag;
                    Neu(g.Key, y, stichtag, null, Gueltigkeit.QuelleSeed);
                    benutzt.Add(g.Key);
                    meldungen.Add($"Kurswechsel {stufe}: {Name(g.Key)} – {x.Bezeichnung} bis {V}, ab {S} {y.Bezeichnung}");
                    gefunden = true;
                    n++;
                    break;
                }
                if (gefunden) break;
            }
        }

        // ---- 3. Ein Abgang: letzte Klasse, alle Zugehörigkeiten enden am Vortag ----
        var letzte = klassen.LastOrDefault();
        var weg = letzte == null ? null : In(letzte).FirstOrDefault(x => !benutzt.Contains(x.SchuelerId));
        if (letzte != null && weg != null)
        {
            foreach (var z in zug.Where(q => q.SchuelerId == weg.SchuelerId && q.GueltigBis == null)) z.GueltigBis = vortag;
            if (schueler.TryGetValue(weg.SchuelerId, out var sa))
            {
                sa.Status = "abgang";
                sa.AbgangAm = vortag;
            }
            benutzt.Add(weg.SchuelerId);
            meldungen.Add($"Abgang: {Name(weg.SchuelerId)} – {letzte.Bezeichnung} bis {V}");
        }

        // ---- 4. Eine Neuaufnahme: erste Klasse ab dem Stichtag ----
        Schueler? neuer = null;
        var erste = klassen.FirstOrDefault();
        if (erste != null)
        {
            var rnd = new Random(seed + 1);
            neuer = new Schueler
            {
                Vorname = Vornamen[rnd.Next(Vornamen.Length)],
                Nachname = Nachnamen[rnd.Next(Nachnamen.Length)],
                Stufe = erste.Stufe,
                IstPseudo = true
            };
            db.Schueler.Add(neuer);
            db.Zugehoerigkeiten.Add(new Zugehoerigkeit
            {
                Schueler = neuer, LerngruppeId = erste.Id, Schuljahr = sj, GueltigVon = stichtag,
                Quelle = Gueltigkeit.QuelleSeed
            });
        }

        // ---- 5. Sek-I-Gruppe: bis zum Vortag Liste „von Hand“, ab dem Stichtag aus SchILD ----
        var gruppe = slots
            .Where(x => x.Art == "Unterricht" && x.LerngruppeTyp == "Klasse" && !string.IsNullOrWhiteSpace(x.Schuelergruppe))
            .GroupBy(x => x.Schuelergruppe!.Trim(), cmp)
            .OrderBy(g => g.Key, cmp)
            .Select(g => new
            {
                Schluessel = g.Key,
                Klassen = g.Select(x => x.LerngruppeKey.Trim()).Distinct(cmp).ToList(),
                Fach = string.Join(", ", g.Select(x => x.Fach.Trim()).Where(f => f.Length > 0).Distinct(cmp).OrderBy(f => f, cmp)),
                Lehrer = g.Select(x => x.Lehrer.Trim()).FirstOrDefault(l => l.Length > 0)
            })
            .FirstOrDefault(g => g.Klassen.Count >= 2 && g.Klassen.All(k => klassen.Any(l => cmp.Equals(l.Bezeichnung, k))));
        if (gruppe != null)
        {
            var poolLg = klassen.Where(l => gruppe.Klassen.Contains(l.Bezeichnung, cmp)).ToList();
            var pool = poolLg.SelectMany(In).Select(z => z.SchuelerId).Distinct().OrderBy(id => id).ToList();
            string ersteKlasse = gruppe.Klassen.OrderBy(k => k, cmp).First();
            var lg = new Lerngruppe
            {
                Typ = LerngruppeTyp.Gruppe,
                Bezeichnung = gruppe.Schluessel,
                Stufe = ersteKlasse.Length >= 2 && char.IsDigit(ersteKlasse[0]) && char.IsDigit(ersteKlasse[1]) ? ersteKlasse[..2] : "",
                Fach = gruppe.Fach,
                LehrerKuerzel = gruppe.Lehrer,
                Schuljahr = sj,
                IstPseudo = true,
                BearbeitetVon = "Seed",
                BearbeitetAm = DateTime.UtcNow,
                Quelle = Gueltigkeit.QuelleSchild,
                SchildAb = stichtag
            };
            db.Lerngruppen.Add(lg);
            int schild = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                db.Zugehoerigkeiten.Add(new Zugehoerigkeit
                {
                    SchuelerId = pool[i], Lerngruppe = lg, Schuljahr = sj, GueltigBis = vortag, Quelle = Gueltigkeit.QuelleHand
                });
                if (i % 2 == 0)
                {
                    db.Zugehoerigkeiten.Add(new Zugehoerigkeit
                    {
                        SchuelerId = pool[i], Lerngruppe = lg, Schuljahr = sj, GueltigVon = stichtag, Quelle = Gueltigkeit.QuelleSchild
                    });
                    schild++;
                }
            }
            meldungen.Add($"Gruppe {gruppe.Schluessel} ({gruppe.Fach}; {string.Join(", ", gruppe.Klassen)}): bis {V} von Hand " +
                          $"alle {pool.Count} Schüler, ab {S} aus SchILD {schild} Schüler (Gruppenliste nur lesen)");
        }

        db.SaveChanges();
        if (neuer != null && erste != null)
            meldungen.Add($"Neuaufnahme: {neuer.Vorname} {neuer.Nachname} (Id {neuer.Id}) – ab {S} in {erste.Bezeichnung}");
        return meldungen;
    }
}
