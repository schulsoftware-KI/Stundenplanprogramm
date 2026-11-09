#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildLeser – Phase 5.2a: liest einen Ordner mit SchILD-NRW-3-Text-
    //  dateien (.dat, Pipe, Header) nach dem Formatprofil.
    //
    //  Datensparsamkeit (Phase5_Vorschlag §1.5, Entscheidung 87):
    //   - Je Zeile werden NUR die Felder aus dem Profil übernommen; alle
    //     anderen Zellen werden sofort verworfen.
    //   - Meldungen nennen Datei, Zeile, Spalte und Grund – nie einen Wert
    //     aus einer Schülerdatei. (Klassen-, Kurs- und Fachbezeichnungen sowie
    //     Lehrkräftekürzel nennt erst die Prüfung, Entscheidung 91.)
    //   - Dateien, die nicht im Profil stehen (z. B. SchuelerZusatzdaten.dat),
    //     werden nicht geöffnet.
    //  Der Leser sendet nichts und schreibt nichts.
    // =====================================================================

    public static class SchildLeser
    {
        /// <summary>Höchstzahl gleichartiger Meldungen je Datei/Spalte/Grund; der Rest wird zusammengefasst.</summary>
        private const int MaxGleicheMeldungen = 15;

        private static readonly HashSet<int> GueltigeStatus = new() { 0, 1, 2, 3, 6, 8, 9 };

        // ---- Tabelle: eine gelesene Datei, nur benötigte Felder ----

        private sealed class Datenzeile
        {
            public int Nr;
            public readonly Dictionary<string, string> Werte = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<string>> Mehrfach = new(StringComparer.OrdinalIgnoreCase);
            public string W(string feld) => Werte.TryGetValue(feld, out var v) ? v : "";
        }

        private sealed class Tabelle
        {
            public string Datei = "";
            public readonly List<Datenzeile> Zeilen = new();
            /// <summary>Feld → Spaltenüberschrift, wie sie in der Datei steht (für Meldungen).</summary>
            public readonly Dictionary<string, string> Kopf = new(StringComparer.OrdinalIgnoreCase);
            public bool Hat(string feld) => Kopf.ContainsKey(feld);
            public string Sp(string feld) => Kopf.TryGetValue(feld, out var k) ? k : feld;
        }

        /// <summary>Sammelt Meldungen und fasst Wiederholungen zusammen.</summary>
        private sealed class Melder
        {
            private readonly SchildExport _ex;
            private readonly Dictionary<string, (int Anzahl, SchildProblemArt Art, string Datei, string? Spalte, string Grund)> _zaehler = new();
            public Melder(SchildExport ex) { _ex = ex; }

            public void Melde(SchildProblemArt art, string datei, int? zeile, string? spalte, string grund)
            {
                string k = $"{art}|{datei}|{spalte}|{grund}";
                _zaehler.TryGetValue(k, out var z);
                z = (z.Anzahl + 1, art, datei, spalte, grund);
                _zaehler[k] = z;
                if (z.Anzahl <= MaxGleicheMeldungen) _ex.Problem(art, datei, zeile, spalte, grund);
            }

            public void Abschluss()
            {
                foreach (var z in _zaehler.Values.Where(v => v.Anzahl > MaxGleicheMeldungen))
                    _ex.Problem(z.Art, z.Datei, null, z.Spalte,
                        $"… und {z.Anzahl - MaxGleicheMeldungen} weitere Zeilen mit demselben Problem ({z.Grund})");
            }
        }

        // =================================================================
        //  Einstieg
        // =================================================================

        public static SchildExport Lies(string ordner, SchildProfil profil)
        {
            var ex = new SchildExport { Ordner = ordner };
            var melder = new Melder(ex);

            if (!Directory.Exists(ordner))
            {
                ex.Problem(SchildProblemArt.Fehler, "", null, null, "Der Ordner existiert nicht.");
                return ex;
            }

            ex.IstPseudo = SchildPseudoMarker.Pruefe(ordner, out string pseudoText);
            ex.PseudoText = pseudoText;

            // Alle Dateien des Ordners (das Profil darf auch andere Endungen nennen); geöffnet werden nur die aus dem Profil.
            var vorhanden = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Directory.GetFiles(ordner)) vorhanden.TryAdd(Path.GetFileName(p), p);

            // .dat-Dateien, die nicht im Profil stehen: nicht öffnen, nur darauf hinweisen.
            var bekannt = new HashSet<string>(
                Enum.GetValues<SchildDateiArt>().Select(a => profil.Fuer(a).Datei), StringComparer.OrdinalIgnoreCase);
            foreach (var name in vorhanden.Keys.Where(n => n.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && !bekannt.Contains(n))
                                               .OrderBy(n => n))
                ex.Problem(SchildProblemArt.Hinweis, name, null, null,
                    "Datei wird nicht gelesen (nicht benötigt). Beim nächsten Export in SchILD nicht auswählen – " +
                    "Datensparsamkeit.");

            LiesKlassen(ex, profil, vorhanden, melder);
            LiesKurse(ex, profil, vorhanden, melder);
            LiesKurseLehrkraefte(ex, profil, vorhanden, melder);
            LiesBasisdaten(ex, profil, vorhanden, melder);
            LiesLeistungsdaten(ex, profil, vorhanden, melder);
            LiesLernplattformen(ex, profil, vorhanden, melder);
            LiesLehrkraefte(ex, profil, vorhanden, melder);
            LiesFaecher(ex, profil, vorhanden, melder);
            LiesJahrgaenge(ex, profil, vorhanden, melder);

            melder.Abschluss();
            return ex;
        }

        // =================================================================
        //  Allgemeines Lesen einer Datei
        // =================================================================

        /// <param name="pflicht">Felder, deren Spalte im Kopf stehen MUSS (sonst wird die Datei nicht gelesen).</param>
        /// <param name="optional">Felder, deren Spalte fehlen darf (dann leer).</param>
        /// <param name="mehrfach">Felder, die in mehreren Spalten vorkommen dürfen (alle Werte).</param>
        private static Tabelle? LiesTabelle(SchildExport ex, SchildProfil profil, SchildDateiArt art,
            Dictionary<string, string> vorhanden, Melder melder,
            string[] pflicht, string[] optional, string[]? mehrfach = null)
        {
            var dp = profil.Fuer(art);
            var info = new SchildDateiInfo { Art = art, Dateiname = dp.Datei, Pflicht = dp.Pflicht };
            ex.Dateien.Add(info);

            if (!vorhanden.TryGetValue(dp.Datei, out var pfad))
            {
                if (dp.Pflicht)
                    ex.Problem(SchildProblemArt.Fehler, dp.Datei, null, null,
                        "Datei fehlt (Pflichtdatei). Dateinamen müssen exakt stimmen.");
                return null;
            }
            info.Vorhanden = true;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(pfad); }
            catch (Exception e)
            {
                ex.Problem(SchildProblemArt.Fehler, dp.Datei, null, null,
                    "Datei kann nicht gelesen werden (" + e.GetType().Name + "; in einem anderen Programm geöffnet?).");
                return null;
            }

            string text = SchildText.Dekodiere(bytes, profil.Zeichensatz, out string zs, out string? zsHinweis);
            info.Zeichensatz = zs;
            if (zsHinweis != null) ex.Problem(SchildProblemArt.Hinweis, dp.Datei, null, null, zsHinweis);

            var zeilen = SchildText.Zeilen(text);
            if (zeilen.Count == 0)
            {
                ex.Problem(SchildProblemArt.Fehler, dp.Datei, null, null, "Datei ist leer (keine Kopfzeile).");
                return null;
            }

            string trenn = string.IsNullOrEmpty(profil.Trennzeichen) ? "|" : profil.Trennzeichen;
            var kopf = zeilen[0].Split(trenn);
            info.Spalten = kopf.Length;
            if (kopf.Length < 2)
            {
                ex.Problem(SchildProblemArt.Fehler, dp.Datei, 1, null,
                    $"Kopfzeile enthält kein Trennzeichen „{trenn}“ – falsches Format oder Trennzeichen?");
                return null;
            }

            var normKopf = kopf.Select(SchildText.NormHeader).ToArray();
            var tab = new Tabelle { Datei = dp.Datei };
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var indexMehrfach = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            int Finde(string feld)
            {
                foreach (var name in profil.Namen(art, feld))
                {
                    int i = Array.IndexOf(normKopf, SchildText.NormHeader(name));
                    if (i >= 0) return i;
                }
                return -1;
            }

            bool ok = true;
            foreach (var feld in pflicht)
            {
                int i = Finde(feld);
                if (i < 0)
                {
                    ex.Problem(SchildProblemArt.Fehler, dp.Datei, 1, profil.Namen(art, feld)[0],
                        "Spalte fehlt in der Kopfzeile.");
                    ok = false;
                }
                else { index[feld] = i; tab.Kopf[feld] = kopf[i].Trim(); }
            }
            if (!ok) return null;

            foreach (var feld in optional)
            {
                int i = Finde(feld);
                if (i >= 0) { index[feld] = i; tab.Kopf[feld] = kopf[i].Trim(); }
            }
            foreach (var feld in mehrfach ?? Array.Empty<string>())
            {
                var namen = profil.Namen(art, feld).Select(SchildText.NormHeader).ToHashSet();
                var liste = Enumerable.Range(0, normKopf.Length).Where(i => namen.Contains(normKopf[i])).ToList();
                if (liste.Count > 0) { indexMehrfach[feld] = liste; tab.Kopf[feld] = kopf[liste[0]].Trim(); }
            }

            // Übliche Spaltenzahl der Datenzeilen: Manche Programme hängen an jede Zeile ein Trennzeichen an (leere
            // Zusatzspalte) – das ist erlaubt. Mehr Spalten als üblich bedeuten ein Trennzeichen im Text: dann wären
            // alle folgenden Werte verschoben, auch wenn die letzten Spalten leer sind → Zeile wird übersprungen.
            int ueblich = kopf.Length;
            var laengen = new Dictionary<int, int>();
            for (int z = 1; z < zeilen.Count; z++)
            {
                if (zeilen[z].Trim().Length == 0) continue;
                int n = Anzahl(zeilen[z], trenn) + 1;
                laengen.TryGetValue(n, out int c);
                laengen[n] = c + 1;
            }
            if (laengen.Count > 0)
            {
                int haeufigste = laengen.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
                if (haeufigste > kopf.Length)
                {
                    ueblich = haeufigste;
                    ex.Problem(SchildProblemArt.Hinweis, dp.Datei, null, null,
                        "Die Zeilen haben am Ende zusätzliche Trennzeichen (leere Spalten) – werden ignoriert.");
                }
            }

            int leer = 0, kurz = 0, ersteKurz = 0;
            for (int z = 1; z < zeilen.Count; z++)
            {
                int nr = z + 1;
                string zeile = zeilen[z];
                if (zeile.Trim().Length == 0) { leer++; continue; }

                var zellen = zeile.Split(trenn);
                if (zellen.Length > kopf.Length)
                {
                    bool nurLeer = zellen.Length <= ueblich;
                    for (int k = kopf.Length; nurLeer && k < zellen.Length; k++)
                        if (zellen[k].Trim().Length > 0) nurLeer = false;
                    if (!nurLeer)
                    {
                        melder.Melde(SchildProblemArt.Fehler, dp.Datei, nr, null,
                            "Zeile hat mehr Spalten als die übrigen Zeilen (Trennzeichen „" + trenn +
                            "“ im Text?) – Zeile übersprungen.");
                        continue;
                    }
                }
                else if (zellen.Length < kopf.Length)
                {
                    kurz++;
                    if (ersteKurz == 0) ersteKurz = nr;
                }

                var dz = new Datenzeile { Nr = nr };
                foreach (var kv in index)
                    dz.Werte[kv.Key] = kv.Value < zellen.Length ? SchildText.Wert(zellen[kv.Value]) : "";
                foreach (var kv in indexMehrfach)
                    dz.Mehrfach[kv.Key] = kv.Value.Where(i => i < zellen.Length)
                                                  .Select(i => SchildText.Wert(zellen[i]))
                                                  .Where(v => v.Length > 0).ToList();
                tab.Zeilen.Add(dz);
                // zellen (mit allen übrigen Spalten) wird hier verworfen.
            }

            info.Datenzeilen = tab.Zeilen.Count;
            if (leer > 0)
                ex.Problem(SchildProblemArt.Hinweis, dp.Datei, null, null, $"{leer} leere Zeile(n) übersprungen.");
            if (kurz > 0)
                ex.Problem(SchildProblemArt.Hinweis, dp.Datei, ersteKurz, null,
                    $"{kurz} Zeile(n) haben weniger Spalten als die Kopfzeile; fehlende Spalten gelten als leer.");
            return tab;
        }

        private static int Anzahl(string s, string teil)
        {
            int n = 0;
            for (int i = s.IndexOf(teil, StringComparison.Ordinal); i >= 0; i = s.IndexOf(teil, i + teil.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        // ---- Feldhilfen (melden ohne Inhalt) ----

        private static bool Pflichtwert(Tabelle t, Datenzeile z, string feld, Melder m, out string wert)
        {
            wert = z.W(feld);
            if (wert.Length > 0) return true;
            m.Melde(SchildProblemArt.Fehler, t.Datei, z.Nr, t.Sp(feld), "Pflichtangabe ist leer – Zeile übersprungen.");
            return false;
        }

        private static bool PflichtDatum(Tabelle t, Datenzeile z, string feld, Melder m, out DateOnly datum)
        {
            datum = default;
            if (!Pflichtwert(t, z, feld, m, out string w)) return false;
            if (SchildText.TryDatum(w, out datum)) return true;
            m.Melde(SchildProblemArt.Fehler, t.Datei, z.Nr, t.Sp(feld), "kein gültiges Datum (TT.MM.JJJJ) – Zeile übersprungen.");
            return false;
        }

        private static bool PflichtZahl(Tabelle t, Datenzeile z, string feld, Melder m, out int zahl)
        {
            zahl = 0;
            if (!Pflichtwert(t, z, feld, m, out string w)) return false;
            if (SchildText.TryGanzzahl(w, out zahl)) return true;
            m.Melde(SchildProblemArt.Fehler, t.Datei, z.Nr, t.Sp(feld), "keine ganze Zahl – Zeile übersprungen.");
            return false;
        }

        private static int? OptZahl(Tabelle t, Datenzeile z, string feld, Melder m)
        {
            string w = z.W(feld);
            if (w.Length == 0) return null;
            if (SchildText.TryGanzzahl(w, out int zahl)) return zahl;
            m.Melde(SchildProblemArt.Warnung, t.Datei, z.Nr, t.Sp(feld), "keine ganze Zahl – wird ignoriert.");
            return null;
        }

        private static bool Person(Tabelle t, Datenzeile z, Melder m, SchildPersonZeile p)
        {
            p.Zeile = z.Nr;
            if (!Pflichtwert(t, z, "Nachname", m, out string nn)) return false;
            if (!Pflichtwert(t, z, "Vorname", m, out string vn)) return false;
            if (!PflichtDatum(t, z, "Geburtsdatum", m, out var geb)) return false;
            var (name, zusatz) = SchildText.TrenneZusatz(nn);
            if (name.Length == 0)
            {
                m.Melde(SchildProblemArt.Fehler, t.Datei, z.Nr, t.Sp("Nachname"), "Pflichtangabe ist leer – Zeile übersprungen.");
                return false;
            }
            p.Nachname = name; p.Zusatz = zusatz; p.Vorname = vn; p.Geburtsdatum = geb;
            return true;
        }

        private static List<string> Liste(string wert)
            => wert.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        // =================================================================
        //  Die einzelnen Dateien
        // =================================================================

        private static void LiesKlassen(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.Klassen, v, m,
                new[] { "InternBez" }, new[] { "Jahrgang", "Folgeklasse", "Klassenlehrer", "Jahr", "Abschnitt" });
            if (t == null) return;
            foreach (var z in t.Zeilen)
            {
                if (!Pflichtwert(t, z, "InternBez", m, out string bez)) continue;
                ex.Klassen.Add(new SchildKlasse
                {
                    Zeile = z.Nr, InternBez = bez, Jahrgang = z.W("Jahrgang"), Folgeklasse = z.W("Folgeklasse"),
                    Klassenlehrer = z.W("Klassenlehrer"), Jahr = OptZahl(t, z, "Jahr", m), Abschnitt = OptZahl(t, z, "Abschnitt", m)
                });
            }
        }

        private static void LiesKurse(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.Kurse, v, m,
                new[] { "KursBez", "Jahr", "Abschnitt", "Fach" },
                new[] { "Klasse", "Jahrgang", "Kursart", "Kursleiter", "Klassenunterricht" },
                new[] { "Zusatzkraft" });
            if (t == null) return;

            bool alt = !t.Hat("Klasse") && t.Hat("Klassenunterricht");
            if (!t.Hat("Klasse") && !alt)
            {
                ex.Problem(SchildProblemArt.Fehler, t.Datei, 1, "Klasse",
                    "Spalte fehlt in der Kopfzeile (neues Format) – und „Klassenunterricht“ (altes Format) fehlt auch.");
                return;
            }
            if (alt)
                ex.Problem(SchildProblemArt.Hinweis, t.Datei, null, null,
                    "Altes Kurse.dat-Format erkannt (Spalte „Klassenunterricht“) – wird mitgelesen.");

            foreach (var z in t.Zeilen)
            {
                string kursBez = z.W("KursBez"), klasse = alt ? "" : z.W("Klasse");
                if (alt && z.W("Klassenunterricht").StartsWith("J", StringComparison.OrdinalIgnoreCase))
                { klasse = kursBez; kursBez = ""; }
                if (kursBez.Length == 0 && klasse.Length == 0)
                {
                    m.Melde(SchildProblemArt.Fehler, t.Datei, z.Nr, t.Sp("KursBez"),
                        "KursBez und Klasse sind beide leer – Zeile übersprungen.");
                    continue;
                }
                if (!PflichtZahl(t, z, "Jahr", m, out int jahr)) continue;
                if (!PflichtZahl(t, z, "Abschnitt", m, out int abschnitt)) continue;
                if (!Pflichtwert(t, z, "Fach", m, out string fach)) continue;

                ex.Kurse.Add(new SchildKurs
                {
                    Zeile = z.Nr, KursBez = kursBez, Klasse = klasse, Jahr = jahr, Abschnitt = abschnitt,
                    Jahrgaenge = Liste(z.W("Jahrgang")), Fach = fach, Kursart = z.W("Kursart"),
                    Kursleiter = z.W("Kursleiter"),
                    Zusatzkraefte = z.Mehrfach.TryGetValue("Zusatzkraft", out var zk) ? zk : new List<string>(),
                    AltesFormat = alt
                });
            }
        }

        private static void LiesKurseLehrkraefte(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.KurseLehrkraefte, v, m,
                new[] { "KursBez", "Lehrkraft" }, new[] { "Jahr", "Abschnitt", "Jahrgang" });
            if (t == null) return;
            foreach (var z in t.Zeilen)
            {
                if (!Pflichtwert(t, z, "KursBez", m, out string bez)) continue;
                if (!Pflichtwert(t, z, "Lehrkraft", m, out string lk)) continue;
                var kl = new SchildKursLehrkraft
                {
                    Zeile = z.Nr, KursBez = bez, Lehrkraft = lk, Jahr = OptZahl(t, z, "Jahr", m),
                    Abschnitt = OptZahl(t, z, "Abschnitt", m), Jahrgang = z.W("Jahrgang")
                };
                ex.KursLehrkraefte.Add(kl);

                var treffer = ex.Kurse.Where(k => SchildText.Gleich(k.KursBez, bez)
                                                  && (kl.Jahr == null || k.Jahr == kl.Jahr)
                                                  && (kl.Abschnitt == null || k.Abschnitt == kl.Abschnitt)
                                                  && (kl.Jahrgang.Length == 0 || k.PasstZuJahrgang(kl.Jahrgang))).ToList();
                if (treffer.Count == 0)
                    m.Melde(SchildProblemArt.Warnung, t.Datei, z.Nr, t.Sp("KursBez"), "Kurs nicht in Kurse.dat gefunden.");
                foreach (var k in treffer)
                    if (!k.AlleLehrkraefte().Any(x => SchildText.Gleich(x, lk))) k.WeitereLehrkraefte.Add(lk);
            }
        }

        private static void LiesBasisdaten(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.Basisdaten, v, m,
                new[] { "Nachname", "Vorname", "Geburtsdatum", "Status" },
                new[] { "Aufnahmedatum", "Jahr", "Abschnitt", "Jahrgang", "Klasse" });
            if (t == null) return;
            if (!t.Hat("Klasse") || !t.Hat("Jahrgang"))
                ex.Problem(SchildProblemArt.Warnung, t.Datei, 1, null,
                    "Spalte „Klasse“ oder „Jahrgang“ fehlt – Schüler lassen sich keiner Klasse zuordnen.");

            foreach (var z in t.Zeilen)
            {
                var s = new SchildSchueler();
                if (!Person(t, z, m, s)) continue;
                if (!PflichtZahl(t, z, "Status", m, out int status)) continue;
                if (!GueltigeStatus.Contains(status))
                {
                    m.Melde(SchildProblemArt.Fehler, t.Datei, z.Nr, t.Sp("Status"),
                        "unbekannter Status (erlaubt: 0, 1, 2, 3, 6, 8, 9) – Zeile übersprungen.");
                    continue;
                }
                s.Status = status;
                s.Jahr = OptZahl(t, z, "Jahr", m);
                s.Abschnitt = OptZahl(t, z, "Abschnitt", m);
                s.Jahrgang = z.W("Jahrgang");
                s.Klasse = z.W("Klasse");

                // Aufnahmedatum nur bei Neuaufnahmen (Entscheidung 80) – sonst nicht einmal ansehen.
                if (status == 0)
                {
                    string w = z.W("Aufnahmedatum");
                    if (w.Length == 0)
                        m.Melde(SchildProblemArt.Warnung, t.Datei, z.Nr, t.Sp("Aufnahmedatum"),
                            "Neuaufnahme (Status 0) ohne Aufnahmedatum.");
                    else if (SchildText.TryDatum(w, out var auf)) s.Aufnahmedatum = auf;
                    else
                        m.Melde(SchildProblemArt.Warnung, t.Datei, z.Nr, t.Sp("Aufnahmedatum"),
                            "kein gültiges Datum (TT.MM.JJJJ).");
                }
                ex.Schueler.Add(s);
            }
        }

        private static void LiesLeistungsdaten(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.Leistungsdaten, v, m,
                new[] { "Nachname", "Vorname", "Geburtsdatum", "Jahr", "Abschnitt", "Fach" },
                new[] { "Fachlehrer", "Kursart", "Kurs" });
            if (t == null) return;
            if (!t.Hat("Kurs"))
                ex.Problem(SchildProblemArt.Warnung, t.Datei, 1, "Kurs",
                    "Spalte fehlt – alle Zeilen gelten als Unterricht im Klassenverband.");

            foreach (var z in t.Zeilen)
            {
                var l = new SchildLeistung();
                if (!Person(t, z, m, l)) continue;
                if (!PflichtZahl(t, z, "Jahr", m, out int jahr)) continue;
                if (!PflichtZahl(t, z, "Abschnitt", m, out int abschnitt)) continue;
                if (!Pflichtwert(t, z, "Fach", m, out string fach)) continue;
                l.Jahr = jahr; l.Abschnitt = abschnitt; l.Fach = fach;
                l.Fachlehrer = z.W("Fachlehrer"); l.Kursart = z.W("Kursart"); l.Kurs = z.W("Kurs");
                ex.Leistungen.Add(l);
            }
        }

        private static void LiesLernplattformen(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.Lernplattformen, v, m,
                new[] { "Nachname", "Vorname", "Geburtsdatum", "ID" }, Array.Empty<string>());
            if (t == null) return;
            // Lernplattform, Benutzername und Initialkennwort stehen nicht im Profil → nie gelesen (Entscheidung 85).
            foreach (var z in t.Zeilen)
            {
                var lp = new SchildLernplattform();
                if (!Person(t, z, m, lp)) continue;
                if (!Pflichtwert(t, z, "ID", m, out string w)) continue;
                if (!SchildText.TryLang(w, out long id))
                {
                    m.Melde(SchildProblemArt.Fehler, t.Datei, z.Nr, t.Sp("ID"), "keine ganze Zahl – Zeile übersprungen.");
                    continue;
                }
                lp.Id = id;
                ex.Lernplattformen.Add(lp);
            }
        }

        private static void LiesLehrkraefte(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.Lehrkraefte, v, m,
                new[] { "InternKrz" }, new[] { "Nachname", "Vorname" });
            if (t == null) return;
            foreach (var z in t.Zeilen)
            {
                if (!Pflichtwert(t, z, "InternKrz", m, out string krz)) continue;
                ex.Lehrkraefte.Add(new SchildLehrkraft { Zeile = z.Nr, Kuerzel = krz, Nachname = z.W("Nachname"), Vorname = z.W("Vorname") });
            }
        }

        private static void LiesFaecher(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.Faecher, v, m, new[] { "InternKrz" }, new[] { "Bezeichnung" });
            if (t == null) return;
            foreach (var z in t.Zeilen)
            {
                if (!Pflichtwert(t, z, "InternKrz", m, out string krz)) continue;
                ex.Faecher.Add(new SchildFach { Zeile = z.Nr, Kuerzel = krz, Bezeichnung = z.W("Bezeichnung") });
            }
        }

        private static void LiesJahrgaenge(SchildExport ex, SchildProfil p, Dictionary<string, string> v, Melder m)
        {
            var t = LiesTabelle(ex, p, SchildDateiArt.Jahrgaenge, v, m, new[] { "InternKrz" }, Array.Empty<string>());
            if (t == null) return;
            foreach (var z in t.Zeilen)
            {
                if (!Pflichtwert(t, z, "InternKrz", m, out string krz)) continue;
                ex.Jahrgaenge.Add(new SchildJahrgang { Zeile = z.Nr, Kuerzel = krz });
            }
        }
    }
}
