#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildPruefung – Phase 5.2a: Prüflauf über einen gelesenen Export.
    //  Zahlen (Schüler je Status/Jahrgang, Klassen, Kurse, Zugehörigkeiten)
    //  und Probleme (Verweise ins Leere, doppelte Schüler, Abgleich der
    //  Klassen mit dem Plan). Seit 5.2a-2 zusätzlich die Zuordnung
    //  Kurse/Sek-I-Gruppen/Kürzel/Fächer ↔ Plan (SchildZuordnung). Konnte eine
    //  Datei nicht gelesen werden, entfallen die Prüfungen, die sie brauchen –
    //  statt vieler Folgemeldungen ein Hinweis.
    //
    //  Meldungen (Entscheidungen 87, 91): zu Schülern nur Datei + Zeile +
    //  Grund; Klassen-, Kurs- und Fachbezeichnungen sowie Lehrkräftekürzel
    //  dürfen genannt werden. Der Bericht enthält keine Schülerdaten.
    //  Sendet nichts.
    // =====================================================================

    public sealed class SchildZahl
    {
        public string Bereich { get; set; } = "";
        public string Angabe { get; set; } = "";
        public string Wert { get; set; } = "";
    }

    public sealed class SchildAbschnitt
    {
        public int Jahr { get; set; }
        public int Abschnitt { get; set; }
        public int Schueler { get; set; }
        public string Anzeige => $"{Jahr}/{(Jahr + 1) % 100:00} · Abschnitt {Abschnitt} ({Schueler} Schüler)";
    }

    public sealed class SchildPruefErgebnis
    {
        public int Jahr { get; set; }
        public int Abschnitt { get; set; }
        public List<SchildZahl> Zahlen { get; } = new();
        public List<SchildProblem> Probleme { get; } = new();
        /// <summary>Zuordnung SchILD ↔ Plan (5.2a-2); null, wenn kein Plan oder keine Kurse.dat.</summary>
        public SchildZuordnungErgebnis? Zuordnung { get; set; }

        public int Anzahl(SchildProblemArt art) => Probleme.Count(p => p.Art == art);

        /// <summary>Text zum Kopieren – ohne Schülerdaten (nur Zahlen, Zeilennummern, Bezeichnungen, Kürzel).</summary>
        public string Bericht(SchildExport ex, string profilQuelle, string? planBezeichnung)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"SchILD-Prüflauf vom {DateTime.Now:dd.MM.yyyy HH:mm}");
            sb.AppendLine($"Ordner: {Path.GetFileName(ex.Ordner.TrimEnd('\\', '/'))}");
            sb.AppendLine($"Daten: {(ex.IstPseudo ? "Pseudodaten" : "echte Daten")}");
            sb.AppendLine($"Formatprofil: {profilQuelle}");
            sb.AppendLine($"Abschnitt: {Jahr}/{(Jahr + 1) % 100:00}, Abschnitt {Abschnitt}");
            if (!string.IsNullOrEmpty(planBezeichnung)) sb.AppendLine($"Plan: {planBezeichnung}");
            sb.AppendLine($"Ergebnis: {Anzahl(SchildProblemArt.Fehler)} Fehler, {Anzahl(SchildProblemArt.Warnung)} Warnungen, " +
                          $"{Anzahl(SchildProblemArt.Hinweis)} Hinweise");
            string? bereich = null;
            foreach (var z in Zahlen)
            {
                if (z.Bereich != bereich) { sb.AppendLine(); sb.AppendLine($"== {z.Bereich} =="); bereich = z.Bereich; }
                sb.AppendLine($"{z.Angabe}: {z.Wert}");
            }
            sb.AppendLine();
            sb.AppendLine("== Probleme ==");
            if (Probleme.Count == 0) sb.AppendLine("keine");
            foreach (var p in Probleme.OrderBy(p2 => p2.Art)) sb.AppendLine(p.ToString());
            sb.AppendLine();
            sb.AppendLine("(Der Bericht enthält keine Schülerdaten: nur Zahlen, Zeilennummern, Klassen-, Kurs- und " +
                          "Fachbezeichnungen sowie Lehrkräftekürzel.)");
            return sb.ToString();
        }
    }

    public static class SchildPruefung
    {
        private static readonly HashSet<string> Oberstufe = new(StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };

        /// <summary>Fasst gleichartige Probleme zusammen: eine Meldung mit Anzahl und Beispielzeilen.</summary>
        private sealed class Sammler
        {
            private sealed class Eintrag
            {
                public SchildProblemArt Art; public string Datei = ""; public string? Spalte; public string Grund = "";
                public readonly List<int> Zeilen = new(); public int Anzahl;
            }
            private readonly Dictionary<string, Eintrag> _d = new();
            private readonly List<Eintrag> _folge = new();

            public void Add(SchildProblemArt art, string datei, string? spalte, string grund, int? zeile)
            {
                string k = $"{art}|{datei}|{spalte}|{grund}";
                if (!_d.TryGetValue(k, out var e))
                {
                    e = new Eintrag { Art = art, Datei = datei, Spalte = spalte, Grund = grund };
                    _d[k] = e; _folge.Add(e);
                }
                e.Anzahl++;
                if (zeile != null && e.Zeilen.Count < 6) e.Zeilen.Add(zeile.Value);
            }

            public void Ausgeben(List<SchildProblem> ziel)
            {
                foreach (var e in _folge)
                {
                    string grund = e.Grund;
                    if (e.Anzahl > 1)
                        grund += e.Zeilen.Count > 0
                            ? $" – {e.Anzahl} Zeilen (z. B. {string.Join(", ", e.Zeilen.Take(5))}{(e.Anzahl > 5 ? " …" : "")})"
                            : $" – {e.Anzahl}×";
                    ziel.Add(new SchildProblem
                    {
                        Art = e.Art, Datei = e.Datei, Zeile = e.Zeilen.Count > 0 ? e.Zeilen[0] : null,
                        Spalte = e.Spalte, Grund = grund
                    });
                }
            }
        }

        // ---- Abschnitte ----

        /// <summary>Abschnitte laut Basisdaten (Status 0/2/3/6), häufigster zuerst.</summary>
        public static List<SchildAbschnitt> Abschnitte(SchildExport ex)
        {
            var liste = ex.Schueler
                .Where(s => s.Jahr != null && s.Abschnitt != null && s.Status is 0 or 2 or 3 or 6)
                .GroupBy(s => (s.Jahr!.Value, s.Abschnitt!.Value))
                .Select(g => new SchildAbschnitt { Jahr = g.Key.Item1, Abschnitt = g.Key.Item2, Schueler = g.Count() })
                .OrderByDescending(a => a.Schueler).ThenByDescending(a => a.Jahr).ThenByDescending(a => a.Abschnitt)
                .ToList();
            if (liste.Count == 0)
                liste = ex.Leistungen.GroupBy(l => (l.Jahr, l.Abschnitt))
                    .Select(g => new SchildAbschnitt { Jahr = g.Key.Jahr, Abschnitt = g.Key.Abschnitt, Schueler = 0 })
                    .OrderByDescending(a => a.Jahr).ThenByDescending(a => a.Abschnitt).ToList();
            return liste;
        }

        // ---- Prüflauf ----

        public static SchildPruefErgebnis Pruefe(SchildExport ex, int jahr, int abschnitt, SchildPlanInfo? plan,
                                                 SchildZuordnungDatei? zuordnung = null)
        {
            var e = new SchildPruefErgebnis { Jahr = jahr, Abschnitt = abschnitt };
            var s = new Sammler();
            e.Probleme.AddRange(ex.Probleme);

            string fKl = Name(ex, SchildDateiArt.Klassen), fKu = Name(ex, SchildDateiArt.Kurse);
            string fBa = Name(ex, SchildDateiArt.Basisdaten), fLe = Name(ex, SchildDateiArt.Leistungsdaten);
            string fLp = Name(ex, SchildDateiArt.Lernplattformen);

            void Z(string bereich, string angabe, object wert)
                => e.Zahlen.Add(new SchildZahl { Bereich = bereich, Angabe = angabe, Wert = Convert.ToString(wert, CultureInfo.CurrentCulture) ?? "" });

            // ---------- Dateien ----------
            Z("Dateien", "Art der Daten", ex.IstPseudo ? "Pseudodaten" : "echte Daten");
            foreach (var d in ex.Dateien)
                Z("Dateien", d.Dateiname,
                    !d.Vorhanden ? (d.Pflicht ? "FEHLT" : "nicht vorhanden (optional)")
                    : d.Gelesen ? $"{d.Datenzeilen} Zeilen · {d.Spalten} Spalten · {d.Zeichensatz}"
                    : $"nicht gelesen – {(d.NichtGelesen.Length > 0 ? d.NichtGelesen : "fehlerhaft")}");

            // Nicht gelesene Dateien: die Prüfungen, die sie brauchen, entfallen (ein Hinweis statt Folgemeldungen).
            bool basisOk = ex.Hat(SchildDateiArt.Basisdaten), kurseOk = ex.Hat(SchildDateiArt.Kurse);
            bool leistOk = ex.Hat(SchildDateiArt.Leistungsdaten), klassenOk = ex.Hat(SchildDateiArt.Klassen);
            void Entfaellt(string datei, string was)
                => e.Probleme.Add(new SchildProblem
                {
                    Art = SchildProblemArt.Hinweis, Datei = datei,
                    Grund = $"{was} entfallen, weil {datei} nicht gelesen werden konnte."
                });
            if (!basisOk) Entfaellt(fBa, "Weitere Prüfungen der Schülerdaten");
            if (!klassenOk) Entfaellt(fKl, "Prüfungen der Klassen und der Abgleich der Klassen mit dem Plan");
            if (!kurseOk) Entfaellt(fKu, "Prüfungen der Kurse, die Kurszugehörigkeit der Leistungsdaten und die Zuordnung");
            if (!leistOk) Entfaellt(fLe, "Prüfungen der Kursteilnahme");
            foreach (var art in new[] { SchildDateiArt.KurseLehrkraefte, SchildDateiArt.Lernplattformen, SchildDateiArt.Lehrkraefte,
                                        SchildDateiArt.Faecher, SchildDateiArt.Jahrgaenge })
                if (ex.NichtGelesen(art)) Entfaellt(Name(ex, art), "Prüfungen mit dieser Datei");

            var lehrkraefte = ex.Hat(SchildDateiArt.Lehrkraefte)
                ? new HashSet<string>(ex.Lehrkraefte.Select(l => l.Kuerzel), StringComparer.OrdinalIgnoreCase) : null;
            var faecher = ex.Hat(SchildDateiArt.Faecher)
                ? new HashSet<string>(ex.Faecher.Select(f => f.Kuerzel), StringComparer.OrdinalIgnoreCase) : null;
            var jahrgaengeDatei = ex.Hat(SchildDateiArt.Jahrgaenge)
                ? ex.Jahrgaenge.Select(j => j.Kuerzel).ToList() : null;

            // ---------- Schüler ----------
            var imAbschnitt = ex.Schueler.Where(x => x.Jahr == null || x.Abschnitt == null ||
                                                     (x.Jahr == jahr && x.Abschnitt == abschnitt)).ToList();
            int andere = ex.Schueler.Count - imAbschnitt.Count;
            if (!basisOk) Z("Schüler", fBa, "nicht gelesen – Prüfungen der Schülerdaten entfallen");
            else
            {
                Z("Schüler", "Zeilen in den Basisdaten", ex.Schueler.Count);
                Z("Schüler", "davon im gewählten Abschnitt", imAbschnitt.Count);
                if (andere > 0)
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Hinweis, Datei = fBa,
                        Grund = $"{andere} Schüler stehen in einem anderen Abschnitt (z. B. Abgänge früherer Halbjahre) – " +
                                "sie werden nicht berücksichtigt."
                    });

                (int Status, string Text)[] statusTexte =
                {
                    (2, "aktiv (Status 2)"), (3, "beurlaubt (3)"), (0, "Neuaufnahme (0) – ab Aufnahmedatum"),
                    (6, "extern (6) – nur in Kursen"), (1, "Warteliste (1) – wird nicht übernommen"),
                    (8, "Abschluss (8) – Abgang"), (9, "Abgang ohne Abschluss (9)")
                };
                foreach (var (st, text) in statusTexte)
                {
                    int n = imAbschnitt.Count(x => x.Status == st);
                    if (n > 0 || st == 2) Z("Schüler", text, n);
                }

                var klassenSchueler = imAbschnitt.Where(x => x.Status is 0 or 2 or 3).ToList();
                foreach (var g in klassenSchueler.GroupBy(x => x.Jahrgang.Length > 0 ? x.Jahrgang : "(ohne Jahrgang)", StringComparer.OrdinalIgnoreCase)
                                                 .OrderBy(gr => JahrgangRang(gr.Key, jahrgaengeDatei)).ThenBy(gr => gr.Key))
                    Z("Schüler je Jahrgang (Status 0/2/3)", $"Jahrgang {g.Key}", g.Count());

                foreach (var x in klassenSchueler.Where(x2 => x2.Klasse.Length == 0))
                    s.Add(SchildProblemArt.Warnung, fBa, "Klasse", "Schüler (Status 0/2/3) ohne Klasse", x.Zeile);

                foreach (var g in imAbschnitt.GroupBy(x => x.PersonSchluessel).Where(g2 => g2.Count() > 1))
                {
                    var zeilen = g.Select(x => x.Zeile).OrderBy(z => z).ToList();
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Fehler, Datei = fBa, Zeile = zeilen[0],
                        Grund = $"gleicher Name und Geburtsdatum wie in Zeile {string.Join(", ", zeilen.Skip(1))} – " +
                                "SchILD unterscheidet solche Schüler mit „#Klasse“ am Nachnamen."
                    });
                }
                foreach (var g in imAbschnitt.GroupBy(x => SchildText.PersonSchluessel(x.Nachname, "", x.Vorname, x.Geburtsdatum))
                                             .Where(gr => gr.Count() > 1 && gr.Select(x => x.Zusatz.ToUpperInvariant()).Distinct().Count() == gr.Count()))
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Hinweis, Datei = fBa, Zeile = g.Min(x => x.Zeile),
                        Grund = $"{g.Count()} Schüler mit gleichem Namen und Geburtsdatum werden durch „#Klasse“ unterschieden " +
                                $"(Zeilen {string.Join(", ", g.Select(x => x.Zeile).OrderBy(z => z))})."
                    });
            }
            var klassenSchuelerAlle = imAbschnitt.Where(x => x.Status is 0 or 2 or 3).ToList();

            // ---------- Klassen ----------
            var klassen = ex.Klassen.Where(k => (k.Jahr == null || k.Jahr == jahr) &&
                                                (k.Abschnitt == null || k.Abschnitt == abschnitt)).ToList();
            Z("Klassen", "Klassen im Abschnitt (Klassen.dat)", klassen.Count);
            if (ex.Klassen.Count > 0 && klassen.Count == 0)
                e.Probleme.Add(new SchildProblem
                {
                    Art = SchildProblemArt.Warnung, Datei = fKl,
                    Grund = $"Klassen.dat enthält keine Klassen für {jahr}, Abschnitt {abschnitt}."
                });
            foreach (var g in klassen.GroupBy(k => k.InternBez, StringComparer.OrdinalIgnoreCase).Where(g2 => g2.Count() > 1))
                e.Probleme.Add(new SchildProblem
                {
                    Art = SchildProblemArt.Fehler, Datei = fKl, Zeile = g.Min(k => k.Zeile), Spalte = "InternBez",
                    Grund = $"Klasse „{g.Key}“ doppelt (Zeilen {string.Join(", ", g.Select(k => k.Zeile))})."
                });
            var klassenSet = new HashSet<string>(klassen.Select(k => k.InternBez), StringComparer.OrdinalIgnoreCase);

            if (klassenOk && basisOk)
            {
                foreach (var g in klassenSchuelerAlle.Where(x => x.Klasse.Length > 0 && !klassenSet.Contains(x.Klasse))
                                                 .GroupBy(x => x.Klasse, StringComparer.OrdinalIgnoreCase))
                    foreach (var x in g)
                        s.Add(SchildProblemArt.Fehler, fBa, "Klasse", $"Klasse „{g.Key}“ fehlt in Klassen.dat", x.Zeile);

                var mitSchuelern = new HashSet<string>(klassenSchuelerAlle.Select(x => x.Klasse), StringComparer.OrdinalIgnoreCase);
                var leer = klassen.Where(k => !mitSchuelern.Contains(k.InternBez)).Select(k => k.InternBez).ToList();
                if (leer.Count > 0)
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Hinweis, Datei = fKl,
                        Grund = $"{leer.Count} Klasse(n) ohne Schüler: {Kurzliste(leer)}"
                    });
            }
            if (lehrkraefte != null)
                foreach (var k in klassen.Where(k2 => k2.Klassenlehrer.Length > 0 && !lehrkraefte.Contains(k2.Klassenlehrer)))
                    s.Add(SchildProblemArt.Warnung, fKl, "Klassenlehrer",
                        $"Klassenlehrer „{k.Klassenlehrer}“ fehlt in Lehrkraefte.dat", k.Zeile);

            // ---------- Kurse ----------
            var kurse = ex.Kurse.Where(k => k.Jahr == jahr && k.Abschnitt == abschnitt).ToList();
            var echteKurse = kurse.Where(k => !k.IstKlassenverband).ToList();
            Z("Kurse", "Kurse im Abschnitt (mit Kursbezeichnung)", echteKurse.Count);
            Z("Kurse", "davon Oberstufe (EF/Q1/Q2)", echteKurse.Count(k => k.Jahrgaenge.Any(j => Oberstufe.Contains(j)) || Oberstufe.Contains(k.Klasse)));
            Z("Kurse", "davon jahrgangsübergreifend", echteKurse.Count(k => k.Jahrgaenge.Count != 1));
            Z("Kurse", "davon für eine ganze Klasse (KursBez + Klasse)", echteKurse.Count(k => k.Klasse.Length > 0));
            Z("Kurse", "davon mit mehreren Lehrkräften", echteKurse.Count(k => k.AlleLehrkraefte().Count() > 1));
            Z("Kurse", "Einträge „Klassenverband“ (nur Klasse)", kurse.Count - echteKurse.Count);
            int kurseAndere = ex.Kurse.Count - kurse.Count;
            if (kurseAndere > 0) Z("Kurse", "Zeilen anderer Abschnitte (übersprungen)", kurseAndere);

            foreach (var g in echteKurse.GroupBy(k => k.KursBez, StringComparer.OrdinalIgnoreCase))
            {
                var l = g.OrderBy(k => k.Zeile).ToList();
                for (int i = 0; i < l.Count; i++)
                    for (int j = i + 1; j < l.Count; j++)
                        if (JahrgaengeUeberlappen(l[i], l[j]))
                            e.Probleme.Add(new SchildProblem
                            {
                                Art = SchildProblemArt.Fehler, Datei = fKu, Zeile = l[j].Zeile, Spalte = "KursBez",
                                Grund = $"Kurs „{g.Key}“ (Jahrgang {l[j].JahrgangText}) gibt es schon in Zeile {l[i].Zeile} – " +
                                        "Kursidentität = KursBez + Jahr + Abschnitt + Jahrgang."
                            });
            }
            foreach (var k in kurse.Where(k2 => k2.Klasse.Length > 0 && ex.Hat(SchildDateiArt.Klassen) && !klassenSet.Contains(k2.Klasse)))
                s.Add(SchildProblemArt.Fehler, fKu, "Klasse", $"Klasse „{k.Klasse}“ fehlt in Klassen.dat", k.Zeile);
            var ohneLeiter = echteKurse.Where(k => k.Kursleiter.Length == 0).ToList();
            if (ohneLeiter.Count > 0)
                e.Probleme.Add(new SchildProblem
                {
                    Art = SchildProblemArt.Hinweis, Datei = fKu, Zeile = ohneLeiter[0].Zeile, Spalte = "Kursleiter",
                    Grund = $"{ohneLeiter.Count} Kurs(e) ohne Kursleiter (z. B. Kooperation): " +
                            Kurzliste(ohneLeiter.Select(k => $"{k.KursBez} ({k.JahrgangText})").ToList())
                });
            foreach (var k in kurse)
            {
                if (lehrkraefte != null)
                    foreach (var lk in k.AlleLehrkraefte().Where(x => !lehrkraefte.Contains(x)))
                        s.Add(SchildProblemArt.Warnung, fKu, "Kursleiter/Zusatzkraft", $"Lehrkraft „{lk}“ fehlt in Lehrkraefte.dat", k.Zeile);
                if (faecher != null && !faecher.Contains(k.Fach))
                    s.Add(SchildProblemArt.Warnung, fKu, "Fach", $"Fach „{k.Fach}“ fehlt in Faecher.dat", k.Zeile);
                if (jahrgaengeDatei != null)
                    foreach (var j in k.Jahrgaenge.Where(j2 => !jahrgaengeDatei.Contains(j2, StringComparer.OrdinalIgnoreCase)))
                        s.Add(SchildProblemArt.Warnung, fKu, "Jahrgang", $"Jahrgang „{j}“ fehlt in Jahrgaenge.dat", k.Zeile);
            }

            // ---------- Leistungsdaten / Zugehörigkeiten ----------
            var schuelerNach = new Dictionary<string, SchildSchueler>();
            foreach (var x in imAbschnitt) schuelerNach.TryAdd(x.PersonSchluessel, x);
            var alleNach = new Dictionary<string, SchildSchueler>();
            foreach (var x in ex.Schueler) alleNach.TryAdd(x.PersonSchluessel, x);

            var kursNachBez = echteKurse.GroupBy(k => k.KursBez, StringComparer.OrdinalIgnoreCase)
                                        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var ld = ex.Leistungen.Where(l => l.Jahr == jahr && l.Abschnitt == abschnitt).ToList();
            int ldAndere = ex.Leistungen.Count - ld.Count;

            var mitLeistung = new HashSet<SchildSchueler>();
            var zugehoerig = new HashSet<(SchildSchueler, SchildKurs)>();
            var gesehen = new HashSet<string>();
            int klassenverband = 0;

            foreach (var l in ld)
            {
                SchildSchueler? sch = null;
                if (basisOk)
                {
                    if (!schuelerNach.TryGetValue(l.PersonSchluessel, out sch))
                    {
                        s.Add(SchildProblemArt.Fehler, fLe, null,
                            alleNach.ContainsKey(l.PersonSchluessel)
                                ? "Schüler steht in den Basisdaten in einem anderen Abschnitt"
                                : "Schüler nicht in SchuelerBasisdaten.dat gefunden (Name, Vorname, Geburtsdatum)", l.Zeile);
                        continue;
                    }
                    if (sch.Status == 1) continue;   // Warteliste: nicht übernehmen
                    mitLeistung.Add(sch);
                }

                if (!gesehen.Add($"{l.PersonSchluessel}|{l.Fach.ToUpperInvariant()}|{l.Kurs.ToUpperInvariant()}"))
                    s.Add(SchildProblemArt.Warnung, fLe, null, "doppelte Zeile (gleicher Schüler, gleiches Fach, gleicher Kurs)", l.Zeile);

                if (l.Kurs.Length > 0)
                {
                    // Ohne Basisdaten (Jahrgang unbekannt) oder ohne Kurse.dat keine Kursprüfung – sonst nur Folgemeldungen.
                    if (sch != null && kurseOk)
                    {
                        string jg = sch.Jahrgang;
                        var kand = kursNachBez.TryGetValue(l.Kurs, out var kl)
                            ? kl.Where(k => k.PasstZuJahrgang(jg)).ToList() : new List<SchildKurs>();
                        if (kand.Count == 1) zugehoerig.Add((sch, kand[0]));
                        else if (kand.Count == 0)
                            s.Add(SchildProblemArt.Fehler, fLe, "Kurs",
                                $"Kurs „{l.Kurs}“ für Jahrgang {(jg.Length > 0 ? jg : "(leer)")} nicht in Kurse.dat", l.Zeile);
                        else
                            s.Add(SchildProblemArt.Fehler, fLe, "Kurs",
                                $"Kurs „{l.Kurs}“ für Jahrgang {jg} ist in Kurse.dat mehrdeutig", l.Zeile);
                    }
                }
                else klassenverband++;

                if (lehrkraefte != null && l.Fachlehrer.Length > 0 && !lehrkraefte.Contains(l.Fachlehrer))
                    s.Add(SchildProblemArt.Warnung, fLe, "Fachlehrer", $"Fachlehrer „{l.Fachlehrer}“ fehlt in Lehrkraefte.dat", l.Zeile);
                if (faecher != null && !faecher.Contains(l.Fach))
                    s.Add(SchildProblemArt.Warnung, fLe, "Fach", $"Fach „{l.Fach}“ fehlt in Faecher.dat", l.Zeile);
            }

            // Kurse für eine ganze Klasse (KursBez + Klasse): alle Schüler der Klasse gehören dazu.
            foreach (var k in echteKurse.Where(k2 => k2.Klasse.Length > 0))
                foreach (var x in klassenSchuelerAlle.Where(x2 => SchildText.Gleich(x2.Klasse, k.Klasse)))
                    zugehoerig.Add((x, k));

            Z("Leistungsdaten", "Zeilen im Abschnitt", ld.Count);
            if (ldAndere > 0) Z("Leistungsdaten", "Zeilen anderer Abschnitte (übersprungen)", ldAndere);
            Z("Leistungsdaten", "Unterricht im Klassenverband (Zeilen ohne Kurs)", klassenverband);
            Z("Zugehörigkeiten", "Klassen (Schüler mit Klasse, Status 0/2/3)", klassenSchuelerAlle.Count(x => x.Klasse.Length > 0));
            Z("Zugehörigkeiten", "Kurse (Schüler × Kurs)", zugehoerig.Count);
            Z("Zugehörigkeiten", "Schüler mit mindestens einem Kurs", zugehoerig.Select(z => z.Item1).Distinct().Count());
            var genutzt = new HashSet<SchildKurs>(zugehoerig.Select(z => z.Item2));
            var leereKurse = echteKurse.Where(k => !genutzt.Contains(k)).ToList();
            bool teilnahmeOk = basisOk && leistOk && kurseOk;
            Z("Zugehörigkeiten", "Kurse ohne Teilnehmer", teilnahmeOk ? leereKurse.Count.ToString(CultureInfo.CurrentCulture) : "– (nicht prüfbar)");
            if (teilnahmeOk && leereKurse.Count > 0)
                e.Probleme.Add(new SchildProblem
                {
                    Art = SchildProblemArt.Hinweis, Datei = fKu, Zeile = leereKurse[0].Zeile,
                    Grund = $"{leereKurse.Count} Kurs(e) ohne Teilnehmer in den Leistungsdaten: " +
                            Kurzliste(leereKurse.Select(k => $"{k.KursBez} ({k.JahrgangText})").ToList())
                });

            if (leistOk && basisOk)
            {
                foreach (var x in imAbschnitt.Where(x2 => x2.Status == 2 && !mitLeistung.Contains(x2)))
                    s.Add(SchildProblemArt.Warnung, fBa, null, "aktiver Schüler (Status 2) ohne Leistungsdaten im Abschnitt", x.Zeile);
                var mitKurs = new HashSet<SchildSchueler>(zugehoerig.Select(z => z.Item1));
                foreach (var x in imAbschnitt.Where(x2 => x2.Status == 6 && !mitKurs.Contains(x2)))
                    s.Add(SchildProblemArt.Warnung, fBa, null, "externer Schüler (Status 6) ohne Kurs", x.Zeile);
            }

            // ---------- SchILD-ID (optional) ----------
            if (ex.Hat(SchildDateiArt.Lernplattformen))
            {
                var mitId = new HashSet<SchildSchueler>();
                if (basisOk)
                    foreach (var lp in ex.Lernplattformen)
                    {
                        if (alleNach.TryGetValue(lp.PersonSchluessel, out var x)) mitId.Add(x);
                        else s.Add(SchildProblemArt.Warnung, fLp, null, "Schüler nicht in SchuelerBasisdaten.dat gefunden", lp.Zeile);
                    }
                foreach (var g in ex.Lernplattformen.GroupBy(lp => lp.Id).Where(g2 => g2.Count() > 1))
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Fehler, Datei = fLp, Zeile = g.Min(lp => lp.Zeile), Spalte = "ID",
                        Grund = $"dieselbe SchILD-ID in mehreren Zeilen ({string.Join(", ", g.Select(lp => lp.Zeile))})."
                    });
                int aktiv = imAbschnitt.Count(x => x.Status is 0 or 2 or 3 or 6);
                int mit = imAbschnitt.Count(x => x.Status is 0 or 2 or 3 or 6 && mitId.Contains(x));
                if (basisOk) Z("SchILD-ID", "Schüler (Status 0/2/3/6) mit SchILD-ID", $"{mit} von {aktiv}");
            }

            // ---------- Abgleich mit dem Plan ----------
            if (plan != null)
            {
                // Klassen „5a“ in SchILD = „05A“ im Plan (führende Null, Groß/klein; 5.2a-2)
                var schildSek1 = klassen.Where(k => !Oberstufe.Contains(k.InternBez) && !Oberstufe.Contains(k.Jahrgang))
                                        .Select(k => k.InternBez).ToList();
                var klassenNorm = new HashSet<string>(klassen.Select(k => SchildText.KlasseNorm(k.InternBez)), StringComparer.OrdinalIgnoreCase);
                var nichtImPlan = klassenOk ? schildSek1.Where(k => !plan.Klassen.Contains(SchildText.KlasseNorm(k))).OrderBy(k => k).ToList()
                                            : new List<string>();
                var fehltInSchild = klassenOk ? plan.Klassen.Where(k => !klassenNorm.Contains(SchildText.KlasseNorm(k))).OrderBy(k => k).ToList()
                                              : new List<string>();
                var ohneNull = schildSek1.Where(k => SchildText.KlasseNorm(k) != k.Trim().ToUpperInvariant()
                                                     && plan.Klassen.Contains(SchildText.KlasseNorm(k))).OrderBy(k => k).ToList();
                if (ohneNull.Count > 0)
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Hinweis, Datei = fKl,
                        Grund = $"{ohneNull.Count} Klasse(n) in SchILD ohne führende Null bzw. in Kleinbuchstaben (z. B. „{ohneNull[0]}“) – " +
                                $"werden den Klassen im Plan zugeordnet („{SchildText.KlasseNorm(ohneNull[0])}“)."
                    });
                var jahrgaengeMitSchuelern = new HashSet<string>(klassenSchuelerAlle.Select(x => x.Jahrgang), StringComparer.OrdinalIgnoreCase);
                var stufenFehlen = basisOk ? plan.Stufen.Where(st => !jahrgaengeMitSchuelern.Contains(st)).OrderBy(st => st).ToList()
                                           : new List<string>();

                Z("Abgleich mit dem Plan", "Plan", plan.Bezeichnung);
                Z("Abgleich mit dem Plan", "Klassen im Plan", plan.Klassen.Count);
                Z("Abgleich mit dem Plan", "davon in SchILD", klassenOk ? (plan.Klassen.Count - fehltInSchild.Count).ToString(CultureInfo.CurrentCulture)
                                                                        : "– (Klassen.dat nicht gelesen)");
                if (fehltInSchild.Count > 0)
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Warnung, Datei = fKl,
                        Grund = $"Klassen im Plan „{plan.Bezeichnung}“, die in SchILD fehlen: {Kurzliste(fehltInSchild)}"
                    });
                if (nichtImPlan.Count > 0)
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Hinweis, Datei = fKl,
                        Grund = $"Klassen in SchILD, die im Plan „{plan.Bezeichnung}“ nicht vorkommen: {Kurzliste(nichtImPlan)}"
                    });
                if (stufenFehlen.Count > 0)
                    e.Probleme.Add(new SchildProblem
                    {
                        Art = SchildProblemArt.Warnung, Datei = fBa,
                        Grund = $"Stufen im Plan ohne Schüler in SchILD: {Kurzliste(stufenFehlen)}"
                    });
            }
            else
                e.Probleme.Add(new SchildProblem
                {
                    Art = SchildProblemArt.Hinweis, Datei = "",
                    Grund = "Kein Plan verfügbar – der Abgleich mit dem Stundenplan und die Zuordnung entfallen."
                });

            // ---------- Zuordnung SchILD ↔ Plan (5.2a-2) ----------
            if (plan != null && zuordnung != null && kurseOk)
                e.Zuordnung = SchildZuordnung.Berechne(ex, jahr, abschnitt, plan, zuordnung, e.Probleme, e.Zahlen);

            s.Ausgeben(e.Probleme);
            // Fehler zuerst, innerhalb der Art in Entstehungsreihenfolge.
            var sortiert = e.Probleme.Select((p, i) => (p, i)).OrderBy(t => t.p.Art).ThenBy(t => t.i).Select(t => t.p).ToList();
            e.Probleme.Clear();
            e.Probleme.AddRange(sortiert);
            return e;
        }

        // ---- Hilfen ----

        private static string Name(SchildExport ex, SchildDateiArt art)
            => ex.Info(art)?.Dateiname ?? art.ToString();

        private static bool JahrgaengeUeberlappen(SchildKurs a, SchildKurs b)
            => a.Jahrgaenge.Count == 0 || b.Jahrgaenge.Count == 0 ||
               a.Jahrgaenge.Intersect(b.Jahrgaenge, StringComparer.OrdinalIgnoreCase).Any();

        private static string Kurzliste(List<string> l)
            => string.Join(", ", l.Take(12)) + (l.Count > 12 ? $" … (+{l.Count - 12})" : "");

        /// <summary>Reihenfolge der Jahrgänge: laut Jahrgaenge.dat, sonst Zahlen vor EF/Q1/Q2.</summary>
        private static int JahrgangRang(string jg, List<string>? datei)
        {
            if (datei != null)
            {
                int i = datei.FindIndex(x => SchildText.Gleich(x, jg));
                if (i >= 0) return i;
            }
            if (int.TryParse(jg, out int n)) return 1000 + n;
            return jg.ToUpperInvariant() switch { "EF" => 2011, "Q1" => 2012, "Q2" => 2013, _ => 3000 };
        }
    }
}
