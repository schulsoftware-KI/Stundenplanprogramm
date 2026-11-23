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
    //  SchildFolgestand – Phase 5.2b-2b-2 (Entscheidung 171, 5.2b_Vorschlag §5.6):
    //  „Folgestand erzeugen“ macht aus einem gültigen Pseudo-Ordner einen neuen
    //  Ordner <Name>_Folge1 (_Folge2 …) mit typischen Änderungen zwischen zwei
    //  SchILD-Exporten – zum Testen von Vorschau und Import mit dem Desktop.
    //  Nur Pseudodaten: Der Ausgangsordner muss eine gültige Markierung
    //  (PSEUDODATEN.json, alle Prüfsummen stimmen) haben; echte Exporte werden
    //  weder gelesen noch verändert.
    //
    //  Änderungen (einzeln abwählbar, fester Startwert → wiederholbar):
    //   - 3 Abgänge (Status 9); der erste ist der erste Schüler der ersten Klasse
    //     in der Datei – in der Entwicklung meist mit dem Testkonto „s05A“
    //     verknüpft (prüft die Kontosperre)
    //   - 2 Klassenwechsel Sek I (Leistungsdaten wie ein Mitschüler der neuen Klasse)
    //   - 3 Kurswechsel Oberstufe (gleiches Fach, Grundkurs, gleiche Stufe; ohne
    //     Überschneidung mit den übrigen Kursen laut Stundenplan)
    //   - 1 Gruppenwechsel Sek I (parallele Gruppe derselben Klasse, z. B. Religion)
    //   - 1 Namensänderung und 1 korrigiertes Geburtsdatum (beide mit SchILD-ID)
    //   - 2 Neuaufnahmen (Status 0, Aufnahme in zwei Wochen; Sek I und Oberstufe)
    //   - 1 Schüler fehlt ohne Abgang
    //   - wahlweise: eine ganze Klasse fehlt (prüft die Sperre „Teil-Export“)
    //  Seit 5.2c-1 (Entscheidungen 186–190, Standard aus):
    //   - „Änderungen ohne SchILD-ID“: die Schüler mit geändertem Namen bzw.
    //     Geburtsdatum fehlen in SchuelerLernplattformen.dat – die Vorschau erkennt
    //     sie dann nur als Paar „dieselbe Person?“ (Teil-Prüfsummen bzw. Name +
    //     Klasse). Die Datei bleibt sonst gleich (ganz ohne SchILD-ID wären die
    //     beiden Schüler des „#Klasse“-Paars nicht zu unterscheiden – Sperre).
    //   - 1 Vorname korrigiert (Sek I), 1 Nachname geändert + Klassenwechsel (Sek I),
    //     1 Neuaufnahme mit demselben Namen wie ein Mitschüler derselben Klasse
    //     (Namensgleiche – für die Neu-Zuordnung über Name + Klasse)
    //  Seit 5.2c-2 (Entscheidungen 191, 193, Standard aus; ändern keine Schüler):
    //   - „Klassenleitung wechselt“: eine Sek-I-Klasse bekommt in Klassen.dat eine
    //     andere Klassenleitung – möglichst die Lehrkraft mit dem Plan-Kürzel „L2“
    //     (Testkonten L2 und koQ1), sonst eine, die noch keine Klasse leitet
    //   - „Lehrkraft heißt anders“: ein Nachname in Lehrkraefte.dat ändert sich –
    //     möglichst bei Plan-Kürzel „L1“ (Testkonten L1 und kl05A)
    //
    //  Seit P4a (5.3a, Entscheidung 207): „Abschnitt 2 (Halbjahreswechsel)“ – eine
    //  eigene Art von Folgestand (die übrigen Häkchen gelten dann nicht): gleiche
    //  Schüler, Abschnitt 2 in Basis- und Leistungsdaten, Kurse.dat, Klassen.dat und
    //  KurseLehrkraefte.dat; 2 Klassenwechsel Sek I, 3 Kurswechsel EF, 2 Abwahlen
    //  (Grundkurs) in Q1/Q2, 3 Wechsel zwischen Religion/PP-Gruppen und 2 zwischen
    //  anderen parallelen Sek-I-Gruppen (WP), 1 Abgang (Status 9) und 1 Neuaufnahme
    //  zum 2. Halbjahr (Status 2, Aufnahme 1. Februar). Nur aus Abschnitt 1.
    //
    //  Damit die Zuordnung SchILD ↔ Plan gleich bleibt: nur feste Zuordnungen
    //  (eindeutig bzw. von Hand, nicht „gemeinsam“), jede Liste behält genug
    //  Teilnehmer (auch aus derselben Klasse), und neue Zeilen in den
    //  Leistungsdaten sind Kopien der Zeilen eines anderen Schülers (gleiche
    //  Kombination aus Fach, Lehrkraft und Kurs). Alle übrigen Zeilen und
    //  Dateien bleiben byte-gleich.
    //
    //  Das Ergebnis nennt die Änderungen (mit Pseudo-Namen – nur im Fenster und
    //  in FOLGESTAND.txt im neuen Ordner) und die Zahlen, die die Vorschau zeigen
    //  sollte, wenn genau der Ausgangsordner importiert ist. Der Marker des neuen
    //  Ordners nennt die Änderungen nur als Zahlen.
    // =====================================================================

    public sealed class SchildFolgestandOptionen
    {
        public bool Abgaenge { get; set; } = true;
        public bool Klassenwechsel { get; set; } = true;
        public bool Kurswechsel { get; set; } = true;
        public bool Gruppenwechsel { get; set; } = true;
        public bool Namensaenderung { get; set; } = true;
        public bool Geburtsdatum { get; set; } = true;
        public bool Neuaufnahmen { get; set; } = true;
        public bool FehltOhneAbgang { get; set; } = true;
        public bool KlasseFehlt { get; set; }
        public int Startwert { get; set; } = 171;
        // 5.2c-1 (Standard aus, damit Folgestand 1 aus 5.2b wiederholbar bleibt)
        public bool OhneSchildId { get; set; }
        public bool Vorname { get; set; }
        public bool NameUndKlasse { get; set; }
        public bool Namensgleiche { get; set; }
        // 5.2c-2 (Standard aus): Klassen.dat bzw. Lehrkraefte.dat – ändern keine Schüler
        public bool KlassenleitungWechselt { get; set; }
        public bool LehrkraftHeisstAnders { get; set; }
        // P4a (207): Folgestand „Abschnitt 2“ – eigene Art, die übrigen Häkchen gelten dann nicht
        public bool Abschnitt2 { get; set; }
    }

    public sealed class SchildFolgestandErgebnis
    {
        public string Ordner { get; set; } = "";
        public string Ausgangsordner { get; set; } = "";
        public int Nummer { get; set; }
        /// <summary>Änderungen mit Pseudo-Namen (nur Anzeige im Fenster und FOLGESTAND.txt).</summary>
        public List<string> Aenderungen { get; } = new();
        /// <summary>Änderungen nur als Zahlen (für den Marker und das Log).</summary>
        public List<string> Uebersicht { get; } = new();
        /// <summary>Gewählt, aber nicht möglich (ohne Namen).</summary>
        public List<string> NichtMoeglich { get; } = new();
        public string? FehlendeKlasse { get; set; }
        /// <summary>Der Ausgangsordner ist selbst ein Folgestand (Erwartung dann nur ungefähr, siehe <see cref="ErwartungText"/>).</summary>
        public bool AusFolgestand { get; set; }

        // Erwartete Vorschau, wenn genau der Ausgangsordner importiert ist
        public int ImExport { get; set; }
        public int Neu { get; set; }
        public int NameGeaendert { get; set; }
        public int Klassenwechsel { get; set; }
        public int Kurswechsel { get; set; }
        public int AbgangLautSchild { get; set; }
        public int AbgangNichtInDb { get; set; }
        public int FehltImExport { get; set; }
        public int Unveraendert { get; set; }
        // 5.2c-1: Paare „dieselbe Person?“ (ohne SchILD-ID)
        public bool OhneSchildId { get; set; }
        public int Paare { get; set; }
        public int PaareVorausgewaehlt { get; set; }
        /// <summary>Nicht vorausgewählte Paare: erscheinen zusätzlich als „neu“ und „fehlt im Export“ (bis zum Häkchen).</summary>
        public int NeuPaare { get; set; }
        public int FehltPaare { get; set; }
        /// <summary>Wechsel, die erst nach dem Häkchen bei einem nicht vorausgewählten Paar erscheinen.</summary>
        public int KlassenwechselPaar { get; set; }
        public int KurswechselPaar { get; set; }
        // 5.2c-2: erwartete Hinweise (nur Kürzel)
        /// <summary>„07B: L57 → L2“ – Klassenleitung laut Klassen.dat gewechselt.</summary>
        public string? KlassenleitungText { get; set; }
        /// <summary>Plan-Kürzel der Lehrkraft mit anderem Nachnamen.</summary>
        public string? LehrkraftText { get; set; }
        /// <summary>P4a (207): Folgestand „Abschnitt 2“ (Halbjahreswechsel).</summary>
        public bool Abschnitt2 { get; set; }

        public string ErwartungText()
        {
            var sb = new StringBuilder();
            if (Abschnitt2)
                sb.AppendLine("Abschnitt 2 (Halbjahreswechsel): im Prüflauf „Abschnitt 2“ wählen; „Änderungen gelten ab“ = erster Tag " +
                              "des 2. Halbjahres (Vorschlag der API beim ersten Import von Abschnitt 2). Gilt dann noch der Grundplan " +
                              "von vorher, warnt die Vorschau („kein eigener Grundplan“) – zum Test denselben Plan mit „gilt ab“ = " +
                              "2. Halbjahr noch einmal veröffentlichen.");
            sb.AppendLine($"Erwartet in der Vorschau, wenn genau „{Ausgangsordner}“ importiert ist (alle Abgänge bestätigt):");
            sb.AppendLine($"Schüler im Export {ImExport} · " +
                          (Paare > 0 ? $"dieselbe Person? {Paare} (vorausgewählt {PaareVorausgewaehlt}) · zusammengeführt " +
                                       $"{PaareVorausgewaehlt} · " : "") +
                          $"neu {Neu + NeuPaare} · Name bzw. Geburtsdatum geändert {NameGeaendert} · " +
                          $"Klassenwechsel {Klassenwechsel} · Kurs-/Gruppenwechsel {Kurswechsel} · Status bzw. Jahrgang geändert 0 · " +
                          $"Abgang laut SchILD {AbgangLautSchild} · fehlt im Export {FehltImExport} · unverändert {Unveraendert} · " +
                          $"Abgang laut SchILD, nicht in der Datenbank {AbgangNichtInDb}.");
            if (Paare > PaareVorausgewaehlt)
                sb.AppendLine($"Nach Häkchen bei allen {Paare} Paaren und „Vorschau berechnen“: zusammengeführt {Paare} · neu {Neu} · " +
                              $"fehlt im Export {FehltImExport - FehltPaare} · Klassenwechsel {Klassenwechsel + KlassenwechselPaar} · " +
                              $"Kurs-/Gruppenwechsel {Kurswechsel + KurswechselPaar} (die übrigen Zahlen bleiben).");
            if (OhneSchildId)
                sb.AppendLine("Änderungen ohne SchILD-ID: Namens- und Datumsänderungen erscheinen als Paare „dieselbe Person?“ " +
                              "statt als „Name bzw. Geburtsdatum geändert“.");
            if (KlassenleitungText != null || LehrkraftText != null)
                sb.AppendLine("Ist der Ausgangsordner schon mit 5.2c-2 importiert (Klassenleitungen und Namen der Lehrkräfte " +
                              "gespeichert), zeigen die Hinweise der Vorschau:" +
                              (KlassenleitungText != null ? $" „Klassenleitung {KlassenleitungText}“ (die Konten ändern sich nicht)" : "") +
                              (KlassenleitungText != null && LehrkraftText != null ? " und" : "") +
                              (LehrkraftText != null ? $" „Namen der Lehrkräfte … geändert 1 ({LehrkraftText})“" : "") +
                              ". Sonst erscheinen alle als „erstmals eingetragen“ bzw. „neu“.");
            if (AusFolgestand)
                sb.AppendLine($"Der Ausgangsordner ist selbst ein Folgestand: Von den {AbgangNichtInDb} Schülern mit Status 8/9 daraus zählen " +
                              "die, deren Abgang schon importiert ist, bei „unverändert“ statt bei „nicht in der Datenbank“; Schüler, " +
                              "deren „fehlt im Export“ früher nicht bestätigt wurde, kommen bei „fehlt im Export“ dazu.");
            if (FehlendeKlasse != null)
                sb.AppendLine($"Die Klasse {FehlendeKlasse} fehlt ganz – die Vorschau muss gesperrt sein („Im Export fehlen ganze Klassen " +
                              $"({FehlendeKlasse})“); dieser Stand lässt sich nicht importieren.");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Text für das Fenster bzw. FOLGESTAND.txt (enthält Pseudo-Namen).</summary>
        public string Text()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Folgestand {Nummer} aus „{Ausgangsordner}“" + (Abschnitt2 ? " – Abschnitt 2 (Halbjahreswechsel)" : "") +
                          " (nur Pseudodaten – keine echten Personen):");
            sb.AppendLine(Ordner);
            sb.AppendLine();
            sb.AppendLine("Änderungen:");
            foreach (var a in Aenderungen) sb.AppendLine("• " + a);
            if (Aenderungen.Count == 0) sb.AppendLine("• keine");
            if (NichtMoeglich.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Nicht möglich:");
                foreach (var n in NichtMoeglich) sb.AppendLine("• " + n);
            }
            sb.AppendLine();
            sb.Append(ErwartungText());
            return sb.ToString();
        }
    }

    public static class SchildFolgestand
    {
        public const string TextDatei = "FOLGESTAND.txt";

        private static readonly Regex FolgeName = new(@"^(?<b>.+)_Folge(?<n>\d+)$", RegexOptions.Compiled);
        private static readonly HashSet<string> Oberstufe = new(StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };
        /// <summary>P4a (207): Religion und Ersatzfächer (wie SchildGenerator.ReligionPhilo).</summary>
        private static readonly HashSet<string> Religion = new(StringComparer.OrdinalIgnoreCase)
            { "ER", "KR", "PP", "IR", "JR", "OR", "HR", "PL" };

        /// <summary>Nächster freier Ordner neben dem Ausgangsordner: „…_Folge1“, „…_Folge2“ …</summary>
        public static string Zielordner(string quelle, out int nummer)
        {
            string q = (quelle ?? "").TrimEnd('\\', '/');
            string eltern = Path.GetDirectoryName(q) ?? "";
            string name = Path.GetFileName(q);
            var m = FolgeName.Match(name);
            string basis = m.Success ? m.Groups["b"].Value : name;
            int n = m.Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) + 1 : 1;
            while (Directory.Exists(Path.Combine(eltern, $"{basis}_Folge{n}"))) n++;
            nummer = n;
            return Path.Combine(eltern, $"{basis}_Folge{n}");
        }

        /// <summary>
        /// Erzeugt den Folgestand. Voraussetzung: gültiger Pseudo-Ordner, Prüflauf ohne Fehler, Zuordnung möglich
        /// (Plan des Prüflaufs) – sonst InvalidOperationException mit verständlichem Text.
        /// </summary>
        public static SchildFolgestandErgebnis Erzeuge(SchildExport ex, SchildPruefErgebnis erg, SchildPlanInfo plan,
                                                       string ziel, int nummer, SchildFolgestandOptionen opt)
        {
            if (!ex.IstPseudo || !SchildPseudoMarker.Pruefe(ex.Ordner, out _))
                throw new InvalidOperationException("Der Ordner ist kein gültiger Pseudo-Export (die Prüfsummen der Markierung " +
                                                    "stimmen nicht) – ein Folgestand entsteht nur aus Pseudodaten.");
            if (erg.Anzahl(SchildProblemArt.Fehler) > 0)
                throw new InvalidOperationException($"Der Prüflauf hat {erg.Anzahl(SchildProblemArt.Fehler)} Fehler – erst beheben.");
            var zu = erg.Zuordnung;
            if (zu == null || !zu.Moeglich)
                throw new InvalidOperationException("Ohne Zuordnung SchILD ↔ Plan ist kein Folgestand möglich (Reiter „Zuordnung“).");
            if (Directory.Exists(ziel) && Directory.EnumerateFileSystemEntries(ziel).Any())
                throw new InvalidOperationException("Der Zielordner ist nicht leer:\n" + ziel);
            var marker = SchildPseudoMarker.Laden(ex.Ordner)
                         ?? throw new InvalidOperationException("Die Pseudo-Markierung ist nicht lesbar.");

            var lauf = new Lauf(ex, erg, zu, plan, opt, marker);
            lauf.Rechne();
            bool neu = !Directory.Exists(ziel);
            try { lauf.Schreibe(ziel, nummer); }
            catch
            {
                if (neu && Directory.Exists(ziel))
                {
                    try { Directory.Delete(ziel, true); } catch { /* bleibt liegen – der Nutzer sieht die Meldung */ }
                }
                throw;
            }
            return lauf.E;
        }

        // =================================================================
        //  Rohdatei: alle Spalten, unverändert bis auf die geänderten Zellen
        // =================================================================

        private sealed class Roh
        {
            public string Name = "";
            public bool Bom;
            public string[] Kopf = Array.Empty<string>();
            public readonly List<string[]> Zeilen = new();
            public readonly HashSet<int> Loesch = new();
            public readonly Dictionary<int, List<string[]>> Danach = new();

            public int Sp(string kopf)
            {
                string n = SchildText.NormHeader(kopf);
                for (int i = 0; i < Kopf.Length; i++)
                    if (SchildText.NormHeader(Kopf[i]) == n) return i;
                return -1;
            }

            public int SpPflicht(string kopf)
            {
                int i = Sp(kopf);
                if (i < 0) throw new InvalidOperationException($"{Name}: Spalte „{kopf}“ fehlt.");
                return i;
            }

            public static string W(string[] z, int sp) => sp >= 0 && sp < z.Length ? SchildText.Wert(z[sp]) : "";

            public void Setze(int zeile, int sp, string wert)
            {
                if (sp < 0) return;
                Zeilen[zeile] = Mit(Zeilen[zeile], Kopf.Length, (sp, wert));
            }

            public void Entferne(int zeile) => Loesch.Add(zeile);

            public void Einfuegen(int nach, string[] zeile)
            {
                if (!Danach.TryGetValue(nach, out var l)) Danach[nach] = l = new List<string[]>();
                l.Add(zeile);
            }
        }

        /// <summary>Kopie einer Zeile (mindestens <paramref name="laenge"/> Zellen) mit geänderten Zellen (Spalte &lt; 0 = übergehen).</summary>
        private static string[] Mit(string[] z, int laenge, params (int Sp, string Wert)[] werte)
        {
            int n = Math.Max(z.Length, laenge);
            foreach (var (sp, _) in werte) if (sp >= n) n = sp + 1;
            var neu = new string[n];
            for (int i = 0; i < n; i++) neu[i] = i < z.Length ? z[i] : "";
            foreach (var (sp, wert) in werte)
                if (sp >= 0) neu[sp] = SchildText.Zelle(wert, "|");
            return neu;
        }

        private static Roh Lies(string ordner, string name)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(ordner, name));
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            // Der Generator schreibt UTF-8 mit BOM oder Windows-1252 – genau so wird zurückgelesen (verlustfrei).
            string text = bom ? new UTF8Encoding(false, false).GetString(bytes, 3, bytes.Length - 3)
                              : SchildText.DekodiereCp1252(bytes, 0);
            var zeilen = SchildText.Zeilen(text);
            if (zeilen.Count == 0) throw new InvalidOperationException($"{name} ist leer.");
            var r = new Roh { Name = name, Bom = bom, Kopf = zeilen[0].Split('|') };
            for (int i = 1; i < zeilen.Count; i++) r.Zeilen.Add(zeilen[i].Split('|'));
            return r;
        }

        private static void Schreibe(string ordner, Roh r)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join("|", r.Kopf)).Append("\r\n");
            for (int i = 0; i < r.Zeilen.Count; i++)
            {
                if (!r.Loesch.Contains(i)) sb.Append(string.Join("|", r.Zeilen[i])).Append("\r\n");
                if (r.Danach.TryGetValue(i, out var l))
                    foreach (var z in l) sb.Append(string.Join("|", z)).Append("\r\n");
            }
            byte[] bytes;
            if (r.Bom)
            {
                var enc = new UTF8Encoding(true);
                bytes = enc.GetPreamble().Concat(enc.GetBytes(sb.ToString())).ToArray();
            }
            else bytes = SchildText.KodiereCp1252(sb.ToString());
            File.WriteAllBytes(Path.Combine(ordner, r.Name), bytes);
        }

        // =================================================================
        //  Schüler (aus den Basisdaten des Abschnitts)
        // =================================================================

        private sealed class Person
        {
            public string Key = "";
            public int Basis;
            public string Nachname = "", Zusatz = "", Vorname = "";
            public DateOnly Geb;
            public int Status;
            /// <summary>Klasse normiert („05A“, „EF“) bzw. wie in der Datei.</summary>
            public string Klasse = "", KlasseRoh = "", Jahrgang = "";
            public bool Oberstufe;
            public long Id;

            public string NameFeld => Zusatz.Length > 0 ? $"{Nachname}#{Zusatz}" : Nachname;
            public string Anzeige => $"{Vorname} {NameFeld}";
            public string Jg => Klasse.Length >= 2 ? Klasse[..2] : Klasse;

            /// <summary>Oberstufe: „EF“, „Q1“, „Q2“ (aus dem Jahrgang, sonst der Klasse); Sek I: die Klasse.</summary>
            public string Stufe
            {
                get
                {
                    if (!Oberstufe) return Klasse;
                    string j = Jahrgang.Trim().ToUpperInvariant();
                    return SchildFolgestand.Oberstufe.Contains(j) ? j : Klasse;
                }
            }
        }

        // =================================================================
        //  Ein Lauf
        // =================================================================

        private sealed class Lauf
        {
            private readonly SchildExport _ex;
            private readonly SchildZuordnungErgebnis _zu;
            private readonly SchildPlanInfo _plan;
            private readonly SchildFolgestandOptionen _opt;
            private readonly SchildPseudoMarker.Marker _marker;
            private readonly int _jahr, _abschnitt;
            private readonly Random _rnd;
            public readonly SchildFolgestandErgebnis E = new();

            private readonly Roh _ba, _le;
            private readonly Roh? _lp;
            private Roh? _kl, _lk;   // 5.2c-2: Klassen.dat bzw. Lehrkraefte.dat – nur gelesen und geschrieben, wenn geändert
            private readonly List<Roh> _weitere = new();   // P4a (207): Kurse.dat usw. mit Abschnitt 2
            private readonly int bN, bV, bG, bGe, bS, bAuf, bJ, bA, bJg, bKl, bEin;
            private readonly int lN, lV, lG, lJ, lA, lFach, lLehrer, lArt, lKurs;
            private readonly int pN, pV, pG, pId, pBen;

            private readonly List<Person> _personen = new();
            private readonly Dictionary<string, Person> _nach = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<int>> _leistung = new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _lpZeile = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<SchildZuordnungZeile>> _zeilenVon = new(StringComparer.Ordinal);
            /// <summary>Alle Personenschlüssel und alle Schlüssel ohne #Klasse (Eingabe der Prüfsumme) – auch die neuen.</summary>
            private readonly HashSet<string> _alleKeys = new(StringComparer.Ordinal);
            private readonly HashSet<string> _extKeys = new(StringComparer.Ordinal);
            private long _maxId;

            private readonly HashSet<string> _benutzt = new(StringComparer.Ordinal);    // geändert oder Vorlage
            private readonly HashSet<string> _geaendert = new(StringComparer.Ordinal);  // bleibt, ändert sich aber
            private readonly HashSet<string> _abgaenge = new(StringComparer.Ordinal);
            private readonly HashSet<string> _entfernt = new(StringComparer.Ordinal);
            private readonly HashSet<string> _klassenBeteiligt = new(StringComparer.Ordinal);
            private readonly Dictionary<SchildZuordnungZeile, int> _weg = new();
            private readonly Dictionary<(SchildZuordnungZeile, string), int> _wegKlasse = new();

            public Lauf(SchildExport ex, SchildPruefErgebnis erg, SchildZuordnungErgebnis zu, SchildPlanInfo plan,
                        SchildFolgestandOptionen opt, SchildPseudoMarker.Marker marker)
            {
                _ex = ex; _zu = zu; _plan = plan; _opt = opt; _marker = marker;
                _jahr = erg.Jahr; _abschnitt = erg.Abschnitt;
                _rnd = new Random(opt.Startwert);
                E.Ausgangsordner = Path.GetFileName(ex.Ordner.TrimEnd('\\', '/'));
                E.AusFolgestand = marker.Folgestand > 0;

                var std = SchildProfil.Standard();
                string ba = std.Fuer(SchildDateiArt.Basisdaten).Datei, le = std.Fuer(SchildDateiArt.Leistungsdaten).Datei,
                       lp = std.Fuer(SchildDateiArt.Lernplattformen).Datei;
                if (!marker.Dateien.ContainsKey(ba) || !marker.Dateien.ContainsKey(le))
                    throw new InvalidOperationException($"Im Pseudo-Export fehlen {ba} bzw. {le}.");
                _ba = Lies(ex.Ordner, ba);
                _le = Lies(ex.Ordner, le);
                _lp = marker.Dateien.ContainsKey(lp) ? Lies(ex.Ordner, lp) : null;
                E.OhneSchildId = opt.OhneSchildId;

                bN = _ba.SpPflicht("Nachname"); bV = _ba.SpPflicht("Vorname"); bG = _ba.SpPflicht("Geburtsdatum");
                bS = _ba.SpPflicht("Status"); bKl = _ba.SpPflicht("Klasse"); bJg = _ba.SpPflicht("Jahrgang");
                bGe = _ba.Sp("Geschlecht"); bAuf = _ba.Sp("Aufnahmedatum"); bJ = _ba.Sp("Jahr"); bA = _ba.Sp("Abschnitt");
                bEin = _ba.Sp("Einschulungsjahr");
                lN = _le.SpPflicht("Nachname"); lV = _le.SpPflicht("Vorname"); lG = _le.SpPflicht("Geburtsdatum");
                lJ = _le.SpPflicht("Jahr"); lA = _le.SpPflicht("Abschnitt"); lFach = _le.SpPflicht("Fach");
                lLehrer = _le.SpPflicht("Fachlehrer"); lArt = _le.SpPflicht("Kursart"); lKurs = _le.SpPflicht("Kurs");
                if (_lp != null)
                {
                    pN = _lp.SpPflicht("Nachname"); pV = _lp.SpPflicht("Vorname"); pG = _lp.SpPflicht("Geburtsdatum");
                    pId = _lp.SpPflicht("ID"); pBen = _lp.Sp("Benutzername");
                }
                else { pN = pV = pG = pId = pBen = -1; }
            }

            // ---------------------------------------------------------------
            //  Einlesen
            // ---------------------------------------------------------------

            private static string? Schluessel(string[] z, int iN, int iV, int iG, out string nachname, out string zusatz,
                                              out string vorname, out DateOnly geb)
            {
                (nachname, zusatz) = SchildText.TrenneZusatz(Roh.W(z, iN));
                vorname = Roh.W(z, iV);
                if (nachname.Length == 0 || vorname.Length == 0 || !SchildText.TryDatum(Roh.W(z, iG), out geb))
                {
                    geb = default;
                    return null;
                }
                return SchildText.PersonSchluessel(nachname, zusatz, vorname, geb);
            }

            private bool ImAbschnitt(string[] z, int iJ, int iA)
            {
                string j = Roh.W(z, iJ), a = Roh.W(z, iA);
                if (j.Length > 0 && (!SchildText.TryGanzzahl(j, out int jv) || jv != _jahr)) return false;
                if (a.Length > 0 && (!SchildText.TryGanzzahl(a, out int av) || av != _abschnitt)) return false;
                return true;
            }

            private void Einlesen()
            {
                for (int i = 0; i < _ba.Zeilen.Count; i++)
                {
                    var z = _ba.Zeilen[i];
                    string? key = Schluessel(z, bN, bV, bG, out var nn, out var zus, out var vn, out var geb);
                    if (key == null) continue;
                    _alleKeys.Add(key);
                    _extKeys.Add(SchildText.PersonSchluessel(nn, "", vn, geb));
                    if (!ImAbschnitt(z, bJ, bA) || !SchildText.TryGanzzahl(Roh.W(z, bS), out int status)) continue;
                    string klRoh = Roh.W(z, bKl);
                    var p = new Person
                    {
                        Key = key, Basis = i, Nachname = nn, Zusatz = zus, Vorname = vn, Geb = geb, Status = status,
                        KlasseRoh = klRoh, Klasse = SchildText.KlasseNorm(klRoh), Jahrgang = Roh.W(z, bJg)
                    };
                    p.Oberstufe = Oberstufe.Contains(p.Klasse) || Oberstufe.Contains(p.Jahrgang.Trim());
                    if (_nach.TryAdd(key, p)) _personen.Add(p);
                }
                for (int i = 0; i < _le.Zeilen.Count; i++)
                {
                    var z = _le.Zeilen[i];
                    string? key = Schluessel(z, lN, lV, lG, out _, out _, out _, out _);
                    if (key == null || !ImAbschnitt(z, lJ, lA)) continue;
                    if (!_leistung.TryGetValue(key, out var l)) _leistung[key] = l = new List<int>();
                    l.Add(i);
                }
                if (_lp != null)
                    for (int i = 0; i < _lp.Zeilen.Count; i++)
                    {
                        var z = _lp.Zeilen[i];
                        string? key = Schluessel(z, pN, pV, pG, out _, out _, out _, out _);
                        if (key == null || !SchildText.TryLang(Roh.W(z, pId), out long id)) continue;
                        if (id > _maxId) _maxId = id;
                        if (!_lpZeile.TryAdd(key, i)) continue;
                        if (_nach.TryGetValue(key, out var p)) p.Id = id;
                    }
                foreach (var u in _zu.Zeilen)
                {
                    if (!SchildNutzlast.WirdUebertragen(u)) continue;
                    foreach (var k in u.Teiln.Keys)
                    {
                        if (!_zeilenVon.TryGetValue(k, out var l)) _zeilenVon[k] = l = new List<SchildZuordnungZeile>();
                        l.Add(u);
                    }
                }
            }

            // ---------------------------------------------------------------
            //  Hilfen
            // ---------------------------------------------------------------

            private List<int> Leistungen(string key) => _leistung.TryGetValue(key, out var l) ? l : new List<int>();

            private List<SchildZuordnungZeile> ZeilenVon(string key)
                => _zeilenVon.TryGetValue(key, out var l) ? l : new List<SchildZuordnungZeile>();

            private HashSet<string> PlanVon(string key)
                => new(ZeilenVon(key).Select(u => u.Plan!), StringComparer.OrdinalIgnoreCase);

            private HashSet<(int, int)> Zeiten(string? planKey)
            {
                var z = new HashSet<(int, int)>();
                var p = _plan.Kurs(planKey);
                if (p != null) foreach (var t in p.Zeiten) z.Add((t.Tag, t.Stunde));
                return z;
            }

            /// <summary>Mitgliedschaft über eine Zeile der Leistungsdaten (nicht über die Klasse eines Klassen-Kurses).</summary>
            private static bool UeberLeistung(SchildZuordnungZeile u) => u.Art == SchildZuordnungArt.OhneKurs || u.Klasse.Length == 0;

            /// <summary>Feste Zuordnung, die nicht vom Stundenplan einzelner Teilnehmer abhängt (eindeutig bzw. von Hand).</summary>
            private static bool Fest(SchildZuordnungZeile u)
                => SchildNutzlast.WirdUebertragen(u) && !u.Gemeinsam && u.GemeinsamMit.Length == 0
                   && (u.Status == SchildZuordnungStatus.VonHand || u.Kandidaten.Count == 1);

            private bool Frei(Person p) => p.Status == 2 && p.Id > 0 && p.Zusatz.Length == 0 && !_benutzt.Contains(p.Key);

            private T? Zufall<T>(List<T> liste) where T : class => liste.Count == 0 ? null : liste[_rnd.Next(liste.Count)];

            private List<T> Gemischt<T>(IEnumerable<T> liste) => liste.Select(x => (X: x, R: _rnd.Next())).OrderBy(t => t.R)
                                                                      .Select(t => t.X).ToList();

            /// <summary>Kann p diese Listen verlassen, ohne dass eine zu klein wird (Zuordnung bleibt gleich)?</summary>
            private bool Entbehrlich(Person p, SchildZuordnungZeile? nur = null)
            {
                foreach (var u in ZeilenVon(p.Key))
                {
                    if ((nur != null && u != nur) || !UeberLeistung(u)) continue;
                    int weg = _weg.TryGetValue(u, out var w) ? w : 0;
                    if (u.Teiln.Count - weg < 3) return false;
                    if (!p.Oberstufe && p.Klasse.Length > 0)
                    {
                        int wk = _wegKlasse.TryGetValue((u, p.Klasse), out var x) ? x : 0;
                        int gleich = u.Teiln.Values.Count(s => SchildText.KlasseNorm(s.Klasse) == p.Klasse);
                        if (gleich - wk < 2) return false;
                    }
                }
                return true;
            }

            private void Verlaesst(Person p, SchildZuordnungZeile? nur = null)
            {
                foreach (var u in ZeilenVon(p.Key))
                {
                    if (nur != null && u != nur) continue;
                    _weg[u] = (_weg.TryGetValue(u, out var w) ? w : 0) + 1;
                    if (p.Klasse.Length > 0)
                        _wegKlasse[(u, p.Klasse)] = (_wegKlasse.TryGetValue((u, p.Klasse), out var x) ? x : 0) + 1;
                }
            }

            /// <summary>Erste Zeile der Leistungsdaten von p, über die p zu u gehört.</summary>
            private int? ZeileFuer(Person p, SchildZuordnungZeile u)
            {
                foreach (int i in Leistungen(p.Key))
                {
                    var z = _le.Zeilen[i];
                    bool passt = u.Art == SchildZuordnungArt.OhneKurs
                        ? Roh.W(z, lKurs).Length == 0 && SchildText.Gleich(Roh.W(z, lFach), u.Fach)
                          && SchildText.Gleich(Roh.W(z, lLehrer), u.Fachlehrer)
                        : SchildText.Gleich(Roh.W(z, lKurs), u.KursBez);
                    if (passt) return i;
                }
                return null;
            }

            /// <summary>Überschneiden sich die übrigen Kurse/Gruppen von p (ohne <paramref name="ohne"/>) nicht mit diesen Zeiten?</summary>
            private bool OhneUeberschneidung(Person p, SchildZuordnungZeile ohne, HashSet<(int, int)> zeiten)
                => zeiten.Count > 0 && !ZeilenVon(p.Key).Where(u => u != ohne).Any(u => Zeiten(u.Plan).Overlaps(zeiten));

            private Person? P(string key) => _nach.TryGetValue(key, out var p) ? p : null;

            private static string D(DateOnly d) => SchildText.DatumText(d);

            private static string Bez(SchildZuordnungZeile u) => u.Art == SchildZuordnungArt.OhneKurs ? u.Schild : u.KursBez;

            /// <summary>Name, Vorname und Geburtsdatum in allen Dateien ändern (Name ohne #Klasse).</summary>
            private void NeueIdentitaet(Person p, string nachname, string vorname, DateOnly geb)
            {
                _ba.Setze(p.Basis, bN, nachname);
                _ba.Setze(p.Basis, bV, vorname);
                _ba.Setze(p.Basis, bG, D(geb));
                foreach (int i in Leistungen(p.Key))
                {
                    _le.Setze(i, lN, nachname);
                    _le.Setze(i, lV, vorname);
                    _le.Setze(i, lG, D(geb));
                }
                if (_lp != null && _lpZeile.TryGetValue(p.Key, out int ip))
                {
                    _lp.Setze(ip, pN, nachname);
                    _lp.Setze(ip, pV, vorname);
                    _lp.Setze(ip, pG, D(geb));
                }
            }

            /// <summary>
            /// 5.2c-1: Wie erkennt die Vorschau eine Änderung von Name bzw. Geburtsdatum? Mit SchILD-ID als „Name bzw.
            /// Geburtsdatum geändert“, ohne als Paar „dieselbe Person?“ (vorausgewählt oder nicht).
            /// </summary>
            private void Erkannt(Person p, bool vorausgewaehlt, int klassenwechsel, int kurswechsel)
            {
                if (_opt.OhneSchildId)
                {
                    // Zeile in SchuelerLernplattformen.dat weg – für diesen Schüler gibt es dann keine SchILD-ID.
                    if (_lp != null && _lpZeile.TryGetValue(p.Key, out int ip)) _lp.Entferne(ip);
                    E.Paare++;
                    if (vorausgewaehlt) { E.PaareVorausgewaehlt++; return; }
                    E.NeuPaare++;
                    E.FehltPaare++;
                    E.KlassenwechselPaar += klassenwechsel;
                    E.KurswechselPaar += kurswechsel;
                    return;
                }
                E.NameGeaendert++;
                E.Klassenwechsel += klassenwechsel;
                E.Kurswechsel += kurswechsel;
            }

            private string Erkennung(string signal, bool vorausgewaehlt)
                => _opt.OhneSchildId
                    ? $"ohne SchILD-ID: Paar „dieselbe Person?“ über {signal}, " + (vorausgewaehlt ? "vorausgewählt" : "nicht vorausgewählt")
                    : "gleiche SchILD-ID";

            private string[] AlsPerson(string[] z, Person p)
                => Mit(z, _le.Kopf.Length, (lN, p.NameFeld), (lV, p.Vorname), (lG, D(p.Geb)));

            private bool NeuerSchluessel(string nachname, string vorname, DateOnly geb)
            {
                string k = SchildText.PersonSchluessel(nachname, "", vorname, geb);
                return !_alleKeys.Contains(k) && !_extKeys.Contains(k);
            }

            private void Merke(string nachname, string vorname, DateOnly geb)
            {
                string k = SchildText.PersonSchluessel(nachname, "", vorname, geb);
                _alleKeys.Add(k);
                _extKeys.Add(k);
            }

            private void Entferne(Person p)
            {
                _ba.Entferne(p.Basis);
                foreach (int i in Leistungen(p.Key)) _le.Entferne(i);
                if (_lp != null && _lpZeile.TryGetValue(p.Key, out int ip)) _lp.Entferne(ip);
                _entfernt.Add(p.Key);
                _benutzt.Add(p.Key);
            }

            // ---------------------------------------------------------------
            //  Rechnen
            // ---------------------------------------------------------------

            public void Rechne()
            {
                Einlesen();
                if (_personen.Count == 0)
                    throw new InvalidOperationException($"In {_ba.Name} stehen keine Schüler im Abschnitt {_jahr}/{_abschnitt}.");
                // P4a (207): Folgestand „Abschnitt 2“ – eigene Art (die übrigen Häkchen gelten nicht)
                if (_opt.Abschnitt2)
                {
                    RechneAbschnitt2();
                    Erwartung();
                    return;
                }
                bool mitId = _lp != null && _personen.Any(p => p.Id > 0);

                // Reihenfolge fest (Startwert) – die Abgänge zuerst, damit der erste Schüler der ersten Klasse frei ist.
                if (_opt.Abgaenge) Abgaenge();
                if (_opt.Klassenwechsel) Klassenwechsel();
                if (_opt.Kurswechsel) Kurswechsel();
                if (_opt.Gruppenwechsel) Gruppenwechsel();
                if (_opt.Namensaenderung)
                {
                    if (mitId) Namensaenderung();
                    else E.NichtMoeglich.Add("Namensänderung: ohne SchuelerLernplattformen.dat (SchILD-ID) nicht sinnvoll.");
                }
                if (_opt.Geburtsdatum)
                {
                    if (mitId) Geburtsdatum();
                    else E.NichtMoeglich.Add("Korrigiertes Geburtsdatum: ohne SchuelerLernplattformen.dat (SchILD-ID) nicht sinnvoll.");
                }
                // 5.2c-1
                if (_opt.Vorname)
                {
                    if (mitId) Vorname();
                    else E.NichtMoeglich.Add("Vorname korrigiert: ohne SchuelerLernplattformen.dat im Ausgangsordner nicht sinnvoll.");
                }
                if (_opt.NameUndKlasse)
                {
                    if (mitId) NameUndKlasse();
                    else E.NichtMoeglich.Add("Nachname + Klassenwechsel: ohne SchuelerLernplattformen.dat im Ausgangsordner nicht sinnvoll.");
                }
                if (_opt.Neuaufnahmen) Neuaufnahmen();
                if (_opt.Namensgleiche) Namensgleiche();
                if (_opt.FehltOhneAbgang) FehltOhneAbgang();
                if (_opt.KlasseFehlt) KlasseFehlt();
                // 5.2c-2: zuletzt, damit der Zufall der übrigen Änderungen gleich bleibt (Folgestände aus 5.2b/5.2c-1 wiederholbar)
                if (_opt.KlassenleitungWechselt) KlassenleitungWechselt();
                if (_opt.LehrkraftHeisstAnders) LehrkraftHeisstAnders();
                if (_opt.OhneSchildId && E.Paare > 0) E.Uebersicht.Add($"{E.Paare} Änderungen ohne SchILD-ID");
                Erwartung();
            }

            /// <summary>Erwartete Zahlen der Vorschau (Ausgangsordner importiert, alle Abgänge bestätigt).</summary>
            private void Erwartung()
            {
                int imDb = _personen.Count(p => p.Status is 0 or 2 or 3 or 6);
                int fehltEntfernt = _entfernt.Count(k => _nach[k].Status is 0 or 2 or 3 or 6);
                E.AbgangLautSchild = _abgaenge.Count;
                E.AbgangNichtInDb = _personen.Count(p => p.Status is 8 or 9 && !_entfernt.Contains(p.Key));
                E.Unveraendert = imDb - fehltEntfernt - E.AbgangLautSchild - _geaendert.Count;
                // Nicht vorausgewählte Paare: der bisherige Datensatz „fehlt“ zusätzlich (er zählt schon bei den geänderten).
                E.FehltImExport = fehltEntfernt + E.FehltPaare;
                E.ImExport = _personen.Count(p => p.Status != 1 && !_entfernt.Contains(p.Key)) + E.Neu;
            }

            private void Abgaenge()
            {
                int vorher = _abgaenge.Count;
                var sek1 = _personen.Where(p => !p.Oberstufe && p.Klasse.Length > 0).ToList();
                string? ersteKlasse = sek1.Select(p => p.Klasse).Distinct().OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
                Person? erster = ersteKlasse == null ? null
                    : sek1.Where(p => p.Klasse == ersteKlasse && p.Status == 2 && p.Id > 0 && !_benutzt.Contains(p.Key))
                          .OrderBy(p => p.Basis).FirstOrDefault(p => Entbehrlich(p));
                if (erster != null)
                    Abgang(erster, " – erster Schüler der ersten Klasse in der Datei (in der Entwicklung meist mit dem Testkonto " +
                                   $"„s{erster.Klasse}“ verknüpft: das Konto wird beim Bestätigen gesperrt)");
                var zweiter = Zufall(sek1.Where(p => Frei(p) && (erster == null || p.Jg != erster.Jg) && Entbehrlich(p)).ToList());
                if (zweiter != null) Abgang(zweiter, "");
                var dritter = Zufall(_personen.Where(p => Frei(p) && p.Oberstufe && Entbehrlich(p)).ToList())
                              ?? Zufall(sek1.Where(p => Frei(p) && Entbehrlich(p)).ToList());
                if (dritter != null) Abgang(dritter, "");
                int n = _abgaenge.Count - vorher;
                if (n < 3) E.NichtMoeglich.Add($"Abgänge: nur {n} von 3 möglich.");
                if (n > 0) E.Uebersicht.Add($"{n} Abgang/Abgänge (Status 9)");
            }

            private void Abgang(Person p, string zusatz)
            {
                _ba.Setze(p.Basis, bS, "9");
                _benutzt.Add(p.Key);
                _abgaenge.Add(p.Key);
                Verlaesst(p);
                E.Aenderungen.Add($"Abgang (Status 9): {p.Anzeige} ({p.Stufe}){zusatz}");
            }

            private void Klassenwechsel()
            {
                var jgKlassen = _personen.Where(p => !p.Oberstufe && p.Klasse.Length >= 2)
                    .GroupBy(p => p.Jg, StringComparer.Ordinal)
                    .Select(g => (Jg: g.Key, Klassen: g.Select(p => p.Klasse).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList()))
                    .Where(t => t.Klassen.Count >= 2).OrderBy(t => t.Jg, StringComparer.Ordinal).ToList();
                var benutzteJg = new HashSet<string>(StringComparer.Ordinal);
                int erledigt = 0;
                for (int versuch = 0; versuch < 40 && erledigt < 2 && jgKlassen.Count > 0; versuch++)
                {
                    var auswahl = jgKlassen.Where(t => !benutzteJg.Contains(t.Jg)).ToList();
                    if (auswahl.Count == 0) auswahl = jgKlassen;
                    var jk = auswahl[_rnd.Next(auswahl.Count)];
                    var s = Zufall(_personen.Where(p => Frei(p) && !p.Oberstufe && p.Jg == jk.Jg && Entbehrlich(p)
                                                        && Leistungen(p.Key).Count > 0).ToList());
                    if (s == null) continue;
                    var andere = jk.Klassen.Where(k => k != s.Klasse).ToList();
                    string b = andere[_rnd.Next(andere.Count)];
                    var r = Zufall(_personen.Where(p => p.Klasse == b && p.Status == 2 && !_benutzt.Contains(p.Key)
                                                        && Leistungen(p.Key).Count > 0).ToList());
                    if (r == null) continue;

                    var alt = Leistungen(s.Key);
                    int pos = alt.Min();
                    _ba.Setze(s.Basis, bKl, r.KlasseRoh);
                    foreach (int i in alt) _le.Entferne(i);
                    foreach (int i in Leistungen(r.Key)) _le.Einfuegen(pos, AlsPerson(_le.Zeilen[i], s));

                    var vorher = PlanVon(s.Key);
                    var nachher = PlanVon(r.Key);
                    string gruppen = "";
                    if (!vorher.SetEquals(nachher))
                    {
                        E.Kurswechsel++;
                        var plus = nachher.Where(k => !vorher.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                        var minus = vorher.Where(k => !nachher.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                        gruppen = "; Kurs-/Gruppenwechsel: " + string.Join(", ", plus.Select(k => "+ " + k).Concat(minus.Select(k => "– " + k)));
                    }
                    Verlaesst(s);
                    _benutzt.Add(s.Key); _benutzt.Add(r.Key);
                    _geaendert.Add(s.Key);
                    _klassenBeteiligt.Add(s.Klasse); _klassenBeteiligt.Add(b);
                    benutzteJg.Add(jk.Jg);
                    E.Klassenwechsel++;
                    erledigt++;
                    E.Aenderungen.Add($"Klassenwechsel: {s.Anzeige} {s.Klasse} → {SchildText.KlasseNorm(b)} (Leistungsdaten wie " +
                                      $"{r.Anzeige}){gruppen}");
                }
                if (erledigt < 2) E.NichtMoeglich.Add($"Klassenwechsel: nur {erledigt} von 2 möglich.");
                if (erledigt > 0) E.Uebersicht.Add($"{erledigt} Klassenwechsel Sek I");
            }

            private void Kurswechsel()
            {
                var oZeilen = _zu.Zeilen.Where(u => u.Art == SchildZuordnungArt.Oberstufe && Fest(u) && u.Plan != null
                                                    && SchildZuordnung.KursartNorm(u.Kursart) == "GK").ToList();
                var stufen = new[] { "EF", "Q1", "Q2" };
                int erledigt = 0;
                for (int runde = 0; runde < 2 && erledigt < 3; runde++)
                    foreach (var st in stufen)
                    {
                        if (erledigt >= 3) break;
                        if (EinKurswechsel(st, oZeilen)) erledigt++;
                    }
                if (erledigt < 3) E.NichtMoeglich.Add($"Kurswechsel Oberstufe: nur {erledigt} von 3 möglich.");
                if (erledigt > 0) E.Uebersicht.Add($"{erledigt} Kurswechsel Oberstufe");
            }

            private bool EinKurswechsel(string stufe, List<SchildZuordnungZeile> oZeilen)
            {
                var kandidaten = Gemischt(_personen.Where(p => Frei(p) && p.Oberstufe && p.Stufe == stufe)).Take(80).ToList();
                foreach (var s in kandidaten)
                {
                    var plaene = PlanVon(s.Key);
                    foreach (var uAlt in Gemischt(ZeilenVon(s.Key).Where(u => oZeilen.Contains(u))))
                    {
                        if (!Entbehrlich(s, uAlt)) continue;
                        int? ia = ZeileFuer(s, uAlt);
                        if (ia == null) continue;
                        var passend = oZeilen.Where(u => u != uAlt && SchildText.Gleich(u.Fach, uAlt.Fach) && u.Jg.SetEquals(uAlt.Jg)
                                                         && !plaene.Contains(u.Plan!)
                                                         && OhneUeberschneidung(s, uAlt, Zeiten(u.Plan)));
                        foreach (var uNeu in Gemischt(passend))
                        {
                            Person? r = null;
                            int? ir = null;
                            foreach (var k in uNeu.Teiln.Keys.OrderBy(k2 => k2, StringComparer.Ordinal))
                            {
                                var x = P(k);
                                if (x == null || x.Key == s.Key) continue;
                                ir = ZeileFuer(x, uNeu);
                                if (ir != null) { r = x; break; }
                            }
                            if (r == null || ir == null) continue;

                            string kursart = Roh.W(_le.Zeilen[ia.Value], lArt);
                            var neu = Mit(AlsPerson(_le.Zeilen[ir.Value], s), _le.Kopf.Length, (lArt, kursart));
                            _le.Entferne(ia.Value);
                            _le.Einfuegen(ia.Value, neu);
                            Verlaesst(s, uAlt);
                            _benutzt.Add(s.Key);
                            _geaendert.Add(s.Key);
                            E.Kurswechsel++;
                            E.Aenderungen.Add($"Kurswechsel {stufe}: {s.Anzeige} „{uAlt.KursBez}“ → „{uNeu.KursBez}“ " +
                                              $"(Plan {uAlt.Plan} → {uNeu.Plan})");
                            return true;
                        }
                    }
                }
                return false;
            }

            private void Gruppenwechsel()
            {
                if (EinGruppenwechsel(_ => true, "Gruppenwechsel")) E.Uebersicht.Add("1 Gruppenwechsel Sek I");
                else E.NichtMoeglich.Add("Gruppenwechsel Sek I: keine zwei parallelen Gruppen mit fester Zuordnung in derselben Klasse gefunden.");
            }

            /// <summary>
            /// Ein Wechsel zwischen zwei parallelen Sek-I-Gruppen derselben Klasse (beide mit fester Zuordnung, beide
            /// <paramref name="passt"/>). Seit P4a einzeln aufrufbar – mit „alle“ wie bisher (gleiche Zufallsfolge).
            /// </summary>
            private bool EinGruppenwechsel(Func<SchildZuordnungZeile, bool> passt, string art)
            {
                var zeilen = _zu.Zeilen.Where(u => u.Art != SchildZuordnungArt.Oberstufe && UeberLeistung(u) && Fest(u) && u.Plan != null
                                                   && passt(u))
                                       .ToList();
                var klassen = Gemischt(_personen.Where(p => !p.Oberstufe && p.Klasse.Length > 0).Select(p => p.Klasse)
                                                .Distinct().OrderBy(k => k, StringComparer.Ordinal));
                foreach (var c in klassen)
                {
                    var inC = zeilen.Where(u => u.TKlassen.Contains(c)).ToList();
                    foreach (var uA in Gemischt(inC))
                    {
                        var zA = Zeiten(uA.Plan);
                        if (zA.Count == 0) continue;
                        foreach (var uB in Gemischt(inC.Where(u => u != uA && !SchildText.Gleich(u.Plan, uA.Plan))))
                        {
                            var zB = Zeiten(uB.Plan);
                            if (!zB.Overlaps(zA)) continue;   // nur parallele Gruppen (z. B. Religion)
                            var sKand = uA.Teiln.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => P(k))
                                .Where(x => x != null && Frei(x) && x.Klasse == c && !uB.Teiln.ContainsKey(x.Key)
                                            && !PlanVon(x.Key).Contains(uB.Plan!) && Entbehrlich(x, uA)
                                            && ZeileFuer(x, uA) != null && OhneUeberschneidung(x, uA, zB))
                                .Select(x => x!).ToList();
                            if (sKand.Count == 0) continue;
                            var rKand = uB.Teiln.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => P(k))
                                .Where(x => x != null && x.Klasse == c && ZeileFuer(x, uB) != null)
                                .Select(x => x!).ToList();
                            if (rKand.Count == 0) continue;
                            var s = sKand[_rnd.Next(sKand.Count)];
                            var r = rKand[_rnd.Next(rKand.Count)];
                            int ia = ZeileFuer(s, uA)!.Value, ib = ZeileFuer(r, uB)!.Value;
                            _le.Entferne(ia);
                            _le.Einfuegen(ia, AlsPerson(_le.Zeilen[ib], s));
                            Verlaesst(s, uA);
                            _benutzt.Add(s.Key); _benutzt.Add(r.Key);
                            _geaendert.Add(s.Key);
                            _klassenBeteiligt.Add(c);
                            E.Kurswechsel++;
                            E.Aenderungen.Add($"{art}: {s.Anzeige} ({c}) {uA.Plan} → {uB.Plan} (SchILD: {Bez(uA)} → {Bez(uB)}; " +
                                              $"Zeile wie {r.Anzeige})");
                            return true;
                        }
                    }
                }
                return false;
            }

            private void Namensaenderung()
            {
                var s = Zufall(_personen.Where(p => Frei(p) && !p.Oberstufe).ToList()) ?? Zufall(_personen.Where(Frei).ToList());
                if (s == null) { E.NichtMoeglich.Add("Namensänderung: kein passender Schüler."); return; }
                for (int versuch = 0; versuch < 200; versuch++)
                {
                    string nn = SchildGenerator.Nachnamen[_rnd.Next(SchildGenerator.Nachnamen.Length)];
                    if (SchildText.Gleich(nn, s.Nachname) || !NeuerSchluessel(nn, s.Vorname, s.Geb)) continue;
                    NeueIdentitaet(s, nn, s.Vorname, s.Geb);
                    Merke(nn, s.Vorname, s.Geb);
                    _benutzt.Add(s.Key);
                    _geaendert.Add(s.Key);
                    Erkannt(s, true, 0, 0);
                    E.Aenderungen.Add($"Namensänderung: {s.Anzeige} → {s.Vorname} {nn} ({s.Stufe}, " +
                                      $"{Erkennung("Vorname + Geburtsdatum", true)})");
                    E.Uebersicht.Add("1 Namensänderung" + (_opt.OhneSchildId ? "" : " (mit SchILD-ID)"));
                    return;
                }
                E.NichtMoeglich.Add("Namensänderung: kein freier Name gefunden.");
            }

            private void Geburtsdatum()
            {
                var s = Zufall(_personen.Where(p => Frei(p) && p.Oberstufe).ToList()) ?? Zufall(_personen.Where(Frei).ToList());
                if (s == null) { E.NichtMoeglich.Add("Geburtsdatum: kein passender Schüler."); return; }
                for (int versuch = 0; versuch < 50; versuch++)
                {
                    var geb = s.Geb.AddDays(1 + _rnd.Next(9));
                    if (!NeuerSchluessel(s.Nachname, s.Vorname, geb)) continue;
                    NeueIdentitaet(s, s.Nachname, s.Vorname, geb);
                    Merke(s.Nachname, s.Vorname, geb);
                    _benutzt.Add(s.Key);
                    _geaendert.Add(s.Key);
                    Erkannt(s, false, 0, 0);
                    E.Aenderungen.Add($"Geburtsdatum korrigiert: {s.Anzeige} ({s.Stufe}) {D(s.Geb)} → {D(geb)} " +
                                      $"({Erkennung("Name + " + (s.Oberstufe ? "Jahrgang" : "Klasse"), false)})");
                    E.Uebersicht.Add("1 korrigiertes Geburtsdatum" + (_opt.OhneSchildId ? "" : " (mit SchILD-ID)"));
                    return;
                }
                E.NichtMoeglich.Add("Geburtsdatum: kein freies Datum gefunden.");
            }

            private void Neuaufnahmen()
            {
                var aufnahme = DateOnly.FromDateTime(DateTime.Today.AddDays(14));
                int n = 0;
                var sek1 = Zufall(_personen.Where(p => !p.Oberstufe && p.Klasse.Length > 0 && p.Status == 2 && p.Id > 0
                                                       && !_benutzt.Contains(p.Key) && Leistungen(p.Key).Count > 0).ToList());
                if (sek1 != null && Neuaufnahme(sek1, aufnahme)) n++;
                var ober = Zufall(_personen.Where(p => p.Oberstufe && p.Stufe == "EF" && p.Status == 2 && p.Id > 0
                                                       && !_benutzt.Contains(p.Key) && Leistungen(p.Key).Count > 0).ToList())
                           ?? Zufall(_personen.Where(p => p.Oberstufe && p.Status == 2 && p.Id > 0
                                                          && !_benutzt.Contains(p.Key) && Leistungen(p.Key).Count > 0).ToList());
                if (ober != null && Neuaufnahme(ober, aufnahme)) n++;
                if (n < 2) E.NichtMoeglich.Add($"Neuaufnahmen: nur {n} von 2 möglich.");
                if (n > 0) E.Uebersicht.Add($"{n} Neuaufnahme(n) (Status 0, Aufnahme {D(aufnahme)})");
            }

            /// <param name="gleicherName">5.2c-1: Name und Vorname von <paramref name="r"/> übernehmen (Namensgleiche).</param>
            /// <param name="status">P4a: „2“ = Neuaufnahme zum 2. Halbjahr (schon aktiv), sonst „0“.</param>
            private bool Neuaufnahme(Person r, DateOnly aufnahme, bool gleicherName = false, string status = "0")
            {
                for (int versuch = 0; versuch < 200; versuch++)
                {
                    bool w = gleicherName ? SchildGenerator.VornamenW.Contains(r.Vorname) : _rnd.Next(2) == 0;
                    string vn = gleicherName ? r.Vorname
                              : w ? SchildGenerator.VornamenW[_rnd.Next(SchildGenerator.VornamenW.Length)]
                                  : SchildGenerator.VornamenM[_rnd.Next(SchildGenerator.VornamenM.Length)];
                    string nn = gleicherName ? r.Nachname : SchildGenerator.Nachnamen[_rnd.Next(SchildGenerator.Nachnamen.Length)];
                    var geb = r.Geb.AddDays(_rnd.Next(-150, 151));
                    if (!NeuerSchluessel(nn, vn, geb)) continue;
                    Merke(nn, vn, geb);
                    long id = ++_maxId;
                    int einschulung = geb.Year + 6 + (geb.Month >= 7 ? 1 : 0);
                    _ba.Einfuegen(r.Basis, Mit(_ba.Zeilen[r.Basis], _ba.Kopf.Length,
                        (bN, nn), (bV, vn), (bG, D(geb)), (bGe, w ? "w" : "m"), (bS, status), (bAuf, D(aufnahme)),
                        (bEin, einschulung.ToString(CultureInfo.InvariantCulture))));
                    var zeilen = Leistungen(r.Key);
                    int nach = zeilen.Max();
                    foreach (int i in zeilen)
                        _le.Einfuegen(nach, Mit(_le.Zeilen[i], _le.Kopf.Length, (lN, nn), (lV, vn), (lG, D(geb))));
                    if (_lp != null && _lpZeile.TryGetValue(r.Key, out int ip))
                    {
                        string ids = id.ToString(CultureInfo.InvariantCulture);
                        _lp.Einfuegen(ip, Mit(_lp.Zeilen[ip], _lp.Kopf.Length, (pN, nn), (pV, vn), (pG, D(geb)), (pId, ids),
                                              (pBen, "pseudo" + ids)));
                    }
                    _benutzt.Add(r.Key);
                    _klassenBeteiligt.Add(r.Klasse);
                    E.Neu++;
                    E.Aenderungen.Add($"Neuaufnahme (Status {status}, Aufnahme {D(aufnahme)}): {vn} {nn} ({r.Stufe}) – Kurse/Gruppen wie " +
                                      $"{r.Anzeige}" + (gleicherName
                                          ? $" – Namensgleiche: derselbe Name wie {r.Anzeige} in derselben Klasse, Geburtsdatum " +
                                            $"{D(geb)} statt {D(r.Geb)}"
                                          : ""));
                    return true;
                }
                return false;
            }

            // ---- 5.2c-1: Vorname korrigiert, Nachname + Klassenwechsel, Namensgleiche ----

            private void Vorname()
            {
                var s = Zufall(_personen.Where(p => Frei(p) && !p.Oberstufe && p.Klasse.Length > 0).ToList())
                        ?? Zufall(_personen.Where(Frei).ToList());
                if (s == null) { E.NichtMoeglich.Add("Vorname korrigiert: kein passender Schüler."); return; }
                var liste = SchildGenerator.VornamenW.Contains(s.Vorname) ? SchildGenerator.VornamenW
                          : SchildGenerator.VornamenM.Contains(s.Vorname) ? SchildGenerator.VornamenM
                          : SchildGenerator.VornamenW.Concat(SchildGenerator.VornamenM).ToArray();
                for (int versuch = 0; versuch < 200; versuch++)
                {
                    string vn = liste[_rnd.Next(liste.Length)];
                    if (SchildText.Gleich(vn, s.Vorname) || !NeuerSchluessel(s.Nachname, vn, s.Geb)) continue;
                    string vorher = s.Anzeige;
                    NeueIdentitaet(s, s.Nachname, vn, s.Geb);
                    Merke(s.Nachname, vn, s.Geb);
                    _benutzt.Add(s.Key);
                    _geaendert.Add(s.Key);
                    Erkannt(s, true, 0, 0);
                    E.Aenderungen.Add($"Vorname korrigiert: {vorher} → {vn} {s.NameFeld} ({s.Stufe}, " +
                                      $"{Erkennung("Nachname + Geburtsdatum", true)})");
                    E.Uebersicht.Add("1 korrigierter Vorname");
                    return;
                }
                E.NichtMoeglich.Add("Vorname korrigiert: kein freier Vorname gefunden.");
            }

            /// <summary>Nachname ändert sich und der Schüler wechselt zugleich in eine Parallelklasse (Sek I).</summary>
            private void NameUndKlasse()
            {
                var jgKlassen = _personen.Where(p => !p.Oberstufe && p.Klasse.Length >= 2)
                    .GroupBy(p => p.Jg, StringComparer.Ordinal)
                    .Select(g => (Jg: g.Key, Klassen: g.Select(p => p.Klasse).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList()))
                    .Where(x => x.Klassen.Count >= 2).OrderBy(x => x.Jg, StringComparer.Ordinal).ToList();
                for (int versuch = 0; versuch < 40 && jgKlassen.Count > 0; versuch++)
                {
                    var jk = jgKlassen[_rnd.Next(jgKlassen.Count)];
                    var s = Zufall(_personen.Where(p => Frei(p) && !p.Oberstufe && p.Jg == jk.Jg && Entbehrlich(p)
                                                        && Leistungen(p.Key).Count > 0).ToList());
                    if (s == null) continue;
                    var andere = jk.Klassen.Where(k => k != s.Klasse).ToList();
                    string b = andere[_rnd.Next(andere.Count)];
                    var r = Zufall(_personen.Where(p => p.Klasse == b && p.Status == 2 && !_benutzt.Contains(p.Key)
                                                        && Leistungen(p.Key).Count > 0).ToList());
                    if (r == null) continue;
                    string? nn = null;
                    for (int n = 0; n < 200 && nn == null; n++)
                    {
                        string kand = SchildGenerator.Nachnamen[_rnd.Next(SchildGenerator.Nachnamen.Length)];
                        if (!SchildText.Gleich(kand, s.Nachname) && NeuerSchluessel(kand, s.Vorname, s.Geb)) nn = kand;
                    }
                    if (nn == null) continue;

                    string vorher = s.Anzeige, klAlt = s.Klasse;
                    var plaeneVorher = PlanVon(s.Key);
                    var plaeneNachher = PlanVon(r.Key);
                    var alt = Leistungen(s.Key);
                    int pos = alt.Min();
                    _ba.Setze(s.Basis, bKl, r.KlasseRoh);
                    _ba.Setze(s.Basis, bN, nn);
                    if (_lp != null && _lpZeile.TryGetValue(s.Key, out int ip)) _lp.Setze(ip, pN, nn);
                    foreach (int i in alt) _le.Entferne(i);
                    s.Nachname = nn;   // AlsPerson schreibt den neuen Namen in die kopierten Zeilen
                    foreach (int i in Leistungen(r.Key)) _le.Einfuegen(pos, AlsPerson(_le.Zeilen[i], s));
                    Merke(nn, s.Vorname, s.Geb);

                    string gruppen = "";
                    int kurs = 0;
                    if (!plaeneVorher.SetEquals(plaeneNachher))
                    {
                        kurs = 1;
                        var plus = plaeneNachher.Where(k => !plaeneVorher.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
                        var minus = plaeneVorher.Where(k => !plaeneNachher.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
                        gruppen = "; Kurs-/Gruppenwechsel: " + string.Join(", ", plus.Select(k => "+ " + k).Concat(minus.Select(k => "– " + k)));
                    }
                    Verlaesst(s);
                    _benutzt.Add(s.Key); _benutzt.Add(r.Key);
                    _geaendert.Add(s.Key);
                    _klassenBeteiligt.Add(klAlt); _klassenBeteiligt.Add(b);
                    Erkannt(s, false, 1, kurs);
                    E.Aenderungen.Add($"Nachname geändert + Klassenwechsel: {vorher} ({klAlt}) → {s.Vorname} {nn} " +
                                      $"({SchildText.KlasseNorm(b)}) (Leistungsdaten wie {r.Anzeige}){gruppen} – " +
                                      $"{Erkennung("Vorname + Geburtsdatum, andere Klasse", false)}");
                    E.Uebersicht.Add("1 Nachname geändert + Klassenwechsel");
                    return;
                }
                E.NichtMoeglich.Add("Nachname + Klassenwechsel: kein passender Schüler gefunden.");
            }

            /// <summary>Neuaufnahme mit demselben Namen wie ein Mitschüler derselben Klasse (anderes Geburtsdatum).</summary>
            private void Namensgleiche()
            {
                var aufnahme = DateOnly.FromDateTime(DateTime.Today.AddDays(14));
                var r = Zufall(_personen.Where(p => !p.Oberstufe && p.Klasse.Length > 0 && p.Status == 2 && p.Id > 0
                                                    && p.Zusatz.Length == 0 && !_benutzt.Contains(p.Key)
                                                    && Leistungen(p.Key).Count > 0).ToList());
                if (r == null || !Neuaufnahme(r, aufnahme, gleicherName: true))
                {
                    E.NichtMoeglich.Add("Namensgleiche: kein passender Schüler gefunden.");
                    return;
                }
                E.Uebersicht.Add($"1 Neuaufnahme mit gleichem Namen wie ein Mitschüler (Status 0, Aufnahme {D(aufnahme)})");
            }

            private void FehltOhneAbgang()
            {
                var s = Zufall(_personen.Where(p => Frei(p) && !p.Oberstufe && p.Klasse.Length > 0 && Entbehrlich(p)).ToList());
                if (s == null) { E.NichtMoeglich.Add("Fehlt im Export: kein passender Schüler."); return; }
                Entferne(s);
                Verlaesst(s);
                E.Aenderungen.Add($"Fehlt im Export (ohne Abgang): {s.Anzeige} ({s.Stufe}) – in der Vorschau nicht vorausgewählt");
                E.Uebersicht.Add("1 Schüler fehlt ohne Abgang");
            }

            private void KlasseFehlt()
            {
                var beteiligt = new HashSet<string>(_klassenBeteiligt, StringComparer.Ordinal);
                foreach (var b in _personen.Where(x => _benutzt.Contains(x.Key))) beteiligt.Add(b.Klasse);
                string? kl = _personen.Where(x => !x.Oberstufe && x.Klasse.Length > 0).Select(x => x.Klasse).Distinct()
                                      .Where(k => !beteiligt.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).LastOrDefault();
                if (kl == null) { E.NichtMoeglich.Add("Eine Klasse fehlt: keine Klasse ohne andere Änderungen gefunden."); return; }
                int n = 0;
                foreach (var weg in _personen.Where(x => x.Klasse == kl && !_entfernt.Contains(x.Key)).ToList())
                {
                    Entferne(weg);
                    n++;
                }
                E.FehlendeKlasse = kl;
                E.Aenderungen.Add($"Die ganze Klasse {kl} fehlt im Export ({n} Schüler) – prüft die Sperre „Teil-Export“.");
                E.Uebersicht.Add($"Klasse {kl} fehlt ganz ({n} Schüler)");
            }

            // ---------------------------------------------------------------
            //  P4a (207): Folgestand „Abschnitt 2“ (Halbjahreswechsel)
            // ---------------------------------------------------------------

            private void RechneAbschnitt2()
            {
                if (_abschnitt != 1)
                    throw new InvalidOperationException("Einen Folgestand „Abschnitt 2“ gibt es nur aus Abschnitt 1 (gewählt ist " +
                                                        $"Abschnitt {_abschnitt}).");
                E.Abschnitt2 = true;
                E.Uebersicht.Add("Abschnitt 2 (Halbjahreswechsel)");

                // Reihenfolge fest (Startwert)
                Klassenwechsel();
                var oZeilen = _zu.Zeilen.Where(u => u.Art == SchildZuordnungArt.Oberstufe && Fest(u) && u.Plan != null
                                                    && SchildZuordnung.KursartNorm(u.Kursart) == "GK").ToList();
                int ef = 0;
                for (int i = 0; i < 3; i++)
                    if (EinKurswechsel("EF", oZeilen)) ef++;
                if (ef < 3) E.NichtMoeglich.Add($"Kurswechsel EF: nur {ef} von 3 möglich.");
                if (ef > 0) E.Uebersicht.Add($"{ef} Kurswechsel EF");
                Abwahlen(oZeilen);

                int rel = 0, wp = 0;
                for (int i = 0; i < 3; i++)
                    if (EinGruppenwechsel(u => Religion.Contains(u.Fach), "Gruppenwechsel Religion/PP")) rel++;
                for (int i = 0; i < 2; i++)
                    if (EinGruppenwechsel(u => !Religion.Contains(u.Fach), "Gruppenwechsel WP")) wp++;
                if (rel < 3) E.NichtMoeglich.Add($"Wechsel Religion/PP: nur {rel} von 3 möglich (parallele Gruppen mit fester Zuordnung).");
                if (rel > 0) E.Uebersicht.Add($"{rel} Wechsel Religion/PP");
                if (wp < 2) E.NichtMoeglich.Add($"Wechsel WP bzw. andere parallele Sek-I-Gruppen: nur {wp} von 2 möglich.");
                if (wp > 0) E.Uebersicht.Add($"{wp} Wechsel WP");

                var abgang = Zufall(_personen.Where(p => Frei(p) && Entbehrlich(p)).ToList());
                if (abgang != null)
                {
                    Abgang(abgang, "");
                    E.Uebersicht.Add("1 Abgang (Status 9)");
                }
                else E.NichtMoeglich.Add("Abgang: kein passender Schüler.");

                var aufnahme = new DateOnly(_jahr + 1, 2, 1);
                var vorlage = Zufall(_personen.Where(p => !p.Oberstufe && p.Klasse.Length > 0 && p.Status == 2 && p.Id > 0
                                                          && !_benutzt.Contains(p.Key) && Leistungen(p.Key).Count > 0).ToList());
                if (vorlage != null && Neuaufnahme(vorlage, aufnahme, status: "2"))
                    E.Uebersicht.Add($"1 Neuaufnahme zum 2. Halbjahr (Status 2, Aufnahme {D(aufnahme)})");
                else E.NichtMoeglich.Add("Neuaufnahme zum 2. Halbjahr: kein passender Schüler.");

                // Zuletzt: Abschnitt 2 in allen Dateien mit Jahr/Abschnitt (auch in den neuen Zeilen)
                int nBa = AbschnittUmstellen(_ba), nLe = AbschnittUmstellen(_le);
                var std = SchildProfil.Standard();
                int nWeitere = 0;
                foreach (var art in new[] { SchildDateiArt.Kurse, SchildDateiArt.Klassen, SchildDateiArt.KurseLehrkraefte })
                {
                    string name = std.Fuer(art).Datei;
                    if (!_marker.Dateien.ContainsKey(name)) continue;
                    var r = Lies(_ex.Ordner, name);
                    int n = AbschnittUmstellen(r);
                    if (n == 0) continue;
                    _weitere.Add(r);
                    nWeitere += n;
                }
                E.Aenderungen.Insert(0, $"Abschnitt 1 → 2 in {nBa} Zeilen der Basisdaten, {nLe} der Leistungsdaten und {nWeitere} " +
                                        "Zeilen von Kurse.dat, Klassen.dat bzw. KurseLehrkraefte.dat");
            }

            /// <summary>Q1/Q2: zwei Schüler wählen je einen Grundkurs ab (die Zeile der Leistungsdaten fällt weg).</summary>
            private void Abwahlen(List<SchildZuordnungZeile> oZeilen)
            {
                int n = 0;
                foreach (var st in new[] { "Q1", "Q2", "Q1", "Q2" })
                {
                    if (n >= 2) break;
                    if (EineAbwahl(st, oZeilen)) n++;
                }
                if (n < 2) E.NichtMoeglich.Add($"Abwahlen Q1/Q2: nur {n} von 2 möglich.");
                if (n > 0) E.Uebersicht.Add($"{n} Abwahl(en) Grundkurs Q1/Q2");
            }

            private bool EineAbwahl(string stufe, List<SchildZuordnungZeile> oZeilen)
            {
                foreach (var s in Gemischt(_personen.Where(p => Frei(p) && p.Oberstufe && p.Stufe == stufe)).Take(80))
                {
                    // Die Kurswahl bleibt voll genug (mindestens 8 Zeilen).
                    if (Leistungen(s.Key).Count(j => !_le.Loesch.Contains(j)) <= 8) continue;
                    foreach (var u in Gemischt(ZeilenVon(s.Key).Where(x => oZeilen.Contains(x))))
                    {
                        if (!Entbehrlich(s, u)) continue;
                        int? i = ZeileFuer(s, u);
                        if (i == null) continue;
                        _le.Entferne(i.Value);
                        Verlaesst(s, u);
                        _benutzt.Add(s.Key);
                        _geaendert.Add(s.Key);
                        E.Kurswechsel++;
                        E.Aenderungen.Add($"Abwahl {stufe}: {s.Anzeige} wählt den Grundkurs „{u.KursBez}“ ab (Plan {u.Plan})");
                        return true;
                    }
                }
                return false;
            }

            /// <summary>Abschnitt der Zeilen des Ausgangsabschnitts auf 2 setzen (auch eingefügte Zeilen). Rückgabe: Anzahl.</summary>
            private int AbschnittUmstellen(Roh r)
            {
                int iJ = r.Sp("Jahr"), iA = r.Sp("Abschnitt");
                if (iA < 0) return 0;
                const string neu = "2";
                int n = 0;
                for (int i = 0; i < r.Zeilen.Count; i++)
                {
                    if (r.Loesch.Contains(i)) continue;
                    var z = r.Zeilen[i];
                    if (Roh.W(z, iA).Length == 0 || !ImAbschnitt(z, iJ, iA)) continue;
                    r.Setze(i, iA, neu);
                    n++;
                }
                foreach (var liste in r.Danach.Values)
                    for (int j = 0; j < liste.Count; j++)
                    {
                        if (Roh.W(liste[j], iA).Length == 0 || !ImAbschnitt(liste[j], iJ, iA)) continue;
                        liste[j] = Mit(liste[j], r.Kopf.Length, (iA, neu));
                        n++;
                    }
                return n;
            }

            // ---------------------------------------------------------------
            //  5.2c-2: Klassenleitung und Namen der Lehrkräfte
            // ---------------------------------------------------------------

            /// <summary>Eine Sek-I-Klasse bekommt in Klassen.dat eine andere Klassenleitung (Plan-Kürzel „L2“, sonst frei).</summary>
            private void KlassenleitungWechselt()
            {
                string name = SchildProfil.Standard().Fuer(SchildDateiArt.Klassen).Datei;
                if (!_marker.Dateien.ContainsKey(name))
                {
                    E.NichtMoeglich.Add($"Klassenleitung wechselt: {name} fehlt im Pseudo-Export.");
                    return;
                }
                var kl = Lies(_ex.Ordner, name);
                int iBez = kl.SpPflicht("InternBez"), iLehrer = kl.SpPflicht("Klassenlehrer"), iJg = kl.Sp("Jahrgang"),
                    iJ = kl.Sp("Jahr"), iA = kl.Sp("Abschnitt");
                var fest = _zu.KuerzelFest;   // SchILD-Kürzel → Plan-Kürzel (fest)
                var klassen = new List<(int Zeile, string Klasse, string Alt)>();
                for (int i = 0; i < kl.Zeilen.Count; i++)
                {
                    var z = kl.Zeilen[i];
                    if (!ImAbschnitt(z, iJ, iA)) continue;
                    string k = SchildText.KlasseNorm(Roh.W(z, iBez));
                    if (k.Length == 0 || Oberstufe.Contains(k) || Oberstufe.Contains(Roh.W(z, iJg)) || !_plan.Klassen.Contains(k))
                        continue;
                    string alt = Roh.W(z, iLehrer);
                    if (alt.Length > 0 && fest.ContainsKey(alt)) klassen.Add((i, k, alt));
                }
                var leiter = new HashSet<string>(klassen.Select(x => x.Alt), StringComparer.OrdinalIgnoreCase);
                string? neu = fest.Where(kv => string.Equals(kv.Value, "L2", StringComparison.OrdinalIgnoreCase))
                                  .Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
                if (neu == null)
                {
                    var frei = fest.Keys.Where(x => !leiter.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    neu = frei.Count == 0 ? null : frei[_rnd.Next(frei.Count)];
                }
                if (neu == null)
                {
                    E.NichtMoeglich.Add("Klassenleitung wechselt: keine passende Lehrkraft (feste Kürzel) gefunden.");
                    return;
                }
                string neuPlan = fest[neu];
                var wahl = klassen.Where(x => !string.Equals(fest[x.Alt], neuPlan, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(x => x.Klasse, StringComparer.Ordinal).ToList();
                if (wahl.Count == 0)
                {
                    E.NichtMoeglich.Add("Klassenleitung wechselt: keine passende Klasse gefunden.");
                    return;
                }
                var ziel = wahl[_rnd.Next(wahl.Count)];
                kl.Setze(ziel.Zeile, iLehrer, neu);
                _kl = kl;
                E.KlassenleitungText = $"{ziel.Klasse}: {fest[ziel.Alt]} → {neuPlan}";
                E.Aenderungen.Add($"Klassenleitung {E.KlassenleitungText} ({name}) – laut SchILD; die Konten ändern sich nicht");
                E.Uebersicht.Add("1 Klassenleitung gewechselt");
            }

            /// <summary>Eine Lehrkraft des Plans bekommt in Lehrkraefte.dat einen anderen Nachnamen (Plan-Kürzel „L1“, sonst zufällig).</summary>
            private void LehrkraftHeisstAnders()
            {
                string name = SchildProfil.Standard().Fuer(SchildDateiArt.Lehrkraefte).Datei;
                if (!_marker.Dateien.ContainsKey(name))
                {
                    E.NichtMoeglich.Add($"Lehrkraft heißt anders: {name} fehlt im Pseudo-Export.");
                    return;
                }
                var lk = Lies(_ex.Ordner, name);
                int iK = lk.SpPflicht("InternKrz"), iN = lk.SpPflicht("Nachname");
                var fest = _zu.KuerzelFest;
                var kandidaten = new List<(int Zeile, string Plan)>();
                for (int i = 0; i < lk.Zeilen.Count; i++)
                {
                    var z = lk.Zeilen[i];
                    if (Roh.W(z, iN).Length > 0 && fest.TryGetValue(Roh.W(z, iK), out var planK)) kandidaten.Add((i, planK));
                }
                if (kandidaten.Count == 0)
                {
                    E.NichtMoeglich.Add("Lehrkraft heißt anders: keine Lehrkraft mit festem Plan-Kürzel gefunden.");
                    return;
                }
                var l1 = kandidaten.Where(x => string.Equals(x.Plan, "L1", StringComparison.OrdinalIgnoreCase)).ToList();
                var ziel = l1.Count > 0 ? l1[0] : kandidaten[_rnd.Next(kandidaten.Count)];
                string alt = Roh.W(lk.Zeilen[ziel.Zeile], iN);
                var namen = new[] { "Lindner", "Brandt", "Vogel", "Seidel", "Franke" }
                    .Where(x => !string.Equals(x, alt, StringComparison.OrdinalIgnoreCase)).ToList();
                string neu = namen[_rnd.Next(namen.Count)];
                lk.Setze(ziel.Zeile, iN, neu);
                _lk = lk;
                E.LehrkraftText = ziel.Plan;
                E.Aenderungen.Add($"Lehrkraft {ziel.Plan} heißt anders ({name}): Nachname {alt} → {neu} (Pseudo-Name)");
                E.Uebersicht.Add("1 Lehrkraft mit anderem Nachnamen");
            }

            // ---------------------------------------------------------------
            //  Schreiben
            // ---------------------------------------------------------------

            public void Schreibe(string ziel, int nummer)
            {
                Directory.CreateDirectory(ziel);
                var geschrieben = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in new[] { _ba, _le, _lp, _kl, _lk }.Concat(_weitere))
                {
                    if (r == null) continue;
                    SchildFolgestand.Schreibe(ziel, r);
                    geschrieben.Add(r.Name);
                }
                var dateien = _marker.Dateien.Keys.ToList();
                foreach (var name in dateien.Where(d => !geschrieben.Contains(d)))
                    File.Copy(Path.Combine(_ex.Ordner, name), Path.Combine(ziel, name), false);

                E.Ordner = ziel;
                E.Nummer = nummer;
                SchildPseudoMarker.Schreiben(ziel, new SchildPseudoMarker.Marker
                {
                    ErzeugtAm = DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                    Plan = _marker.Plan,
                    Schuljahr = _marker.Schuljahr,
                    Abschnitt = _opt.Abschnitt2 ? 2 : _marker.Abschnitt,   // P4a (207)
                    Startwert = _marker.Startwert,
                    Zeichensatz = _marker.Zeichensatz,
                    Sek1Variante = _marker.Sek1Variante,
                    Stolpersteine = _marker.Stolpersteine.ToList(),
                    Folgestand = nummer,
                    Ausgangsordner = E.Ausgangsordner,
                    FolgeStartwert = _opt.Startwert,
                    Folgeaenderungen = E.Uebersicht.ToList()
                }, dateien);
                File.WriteAllText(Path.Combine(ziel, TextDatei), E.Text().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"),
                                  new UTF8Encoding(true));
            }
        }
    }
}
