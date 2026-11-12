#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildZuordnung – Phase 5.2a-2: SchILD ↔ Stundenplan (Entscheidungen
    //  107–116). Nur Desktop, liest nur, sendet nichts.
    //
    //  Einheiten (SchILD-Seite):
    //   - Oberstufenkurs (Kurse.dat, Jahrgang EF/Q1/Q2)  ↔ Plan-Kurs „MG3_Q2_L11“
    //   - Sek-I-Kurs (Kurse.dat mit KursBez)             ↔ Sek-I-Gruppe „PP3_06A06B06D_L85“
    //   - Sek I ohne Kurs: Klasse + Fach + Fachlehrer    ↔ Sek-I-Gruppe (86) – sonst Klassenunterricht
    //  Automatik je Kurs (108): Stufe/Jahrgang → Fach (ein SchILD-Fach ↔ mehrere
    //  Plan-Fächer) → Kursart → Lehrkraft; bei mehreren: Stundenplan der
    //  Teilnehmer (keine zwei Kurse zur selben Zeit) → Nummer in der
    //  Kursbezeichnung → Klassen der Teilnehmer (Sek I) → übrig gebliebenes Paar.
    //  Vorschläge (110): Kürzel/Fächer, die es nur auf einer Seite gibt, werden
    //  über passende Kurse bzw. Klassenunterricht vorgeschlagen und gelten erst
    //  nach Bestätigung (Festlegung von Hand in der Zuordnungsdatei).
    //  Meldungen (111, 114): Zuordnungsprobleme sind Warnungen, nie Fehler;
    //  genannt werden nur Kurs-, Klassen- und Fachbezeichnungen, Kürzel und
    //  Anzahlen – nie Schülerdaten (87, 91).
    // =====================================================================

    public enum SchildZuordnungArt { Oberstufe, Sek1Kurs, OhneKurs }

    public enum SchildZuordnungStatus
    {
        Automatisch, OhneLehrkraft, VonHand, KeineZuordnung, Vorschlag, Offen, Konflikt, Veraltet,
        Klassenunterricht, OhneGegenstueck
    }

    /// <summary>Eine SchILD-Einheit mit ihrer Zuordnung (eine Zeile im Reiter „Zuordnung“).</summary>
    public sealed class SchildZuordnungZeile
    {
        public SchildZuordnungArt Art { get; init; }
        public string KursBez { get; init; } = "";
        /// <summary>Normierte Klasse (Sek-I-Kurs für eine ganze Klasse bzw. Unterricht ohne Kurs), sonst leer.</summary>
        public string Klasse { get; init; } = "";
        /// <summary>Jahrgang wie in Kurse.dat („Q1“, „05,06,07“, „(alle)“) – Teil der Festlegung.</summary>
        public string JahrgangText { get; init; } = "";
        public string FachText { get; init; } = "";
        public string Fach { get; init; } = "";
        public string Kursart { get; init; } = "";
        public string Fachlehrer { get; init; } = "";
        public List<string> Lehrkraefte { get; init; } = new();
        public int Zeile { get; init; }
        public int Nr { get; init; }

        public string? Plan { get; set; }
        public SchildZuordnungStatus Status { get; set; }
        public string Begruendung { get; set; } = "";
        public string LehrkraefteText { get; set; } = "";
        public List<SchildPlanKurs> Kandidaten { get; set; } = new();
        public List<SchildPlanKurs> Weitere { get; set; } = new();

        internal HashSet<string> Jg = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, SchildSchueler> Teiln = new(StringComparer.Ordinal);
        internal HashSet<string> TKlassen = new(StringComparer.Ordinal);

        public int Teilnehmer => Teiln.Count;
        public string Schild => Art == SchildZuordnungArt.OhneKurs
            ? $"{Klasse} · {FachText} · {(Fachlehrer.Length > 0 ? Fachlehrer : "(ohne)")}" : KursBez;
        public string JahrgangAnzeige => Art == SchildZuordnungArt.OhneKurs ? Klasse
            : Klasse.Length > 0 ? $"{JahrgangText} · Klasse {Klasse}" : JahrgangText;
        public string PlanText => Plan ?? "—";
        /// <summary>
        /// Zeitkonflikt mit anderen zugeordneten Kursen/Gruppen („mit „KU-G1 (Q2)“ (Di 5.)“), sonst null.
        /// Eine Zeile mit Zeitkonflikt gilt als offen – egal ob automatisch oder von Hand zugeordnet (Nachtrag 5.2a-2).
        /// </summary>
        public string? Zeitkonflikt { get; set; }
        public string StatusText => SchildZuordnung.Text(Status) + (Zeitkonflikt != null ? " · ⚠ Zeitkonflikt" : "");
        public bool IstOffen => Zeitkonflikt != null || Status is SchildZuordnungStatus.Vorschlag or SchildZuordnungStatus.Offen
            or SchildZuordnungStatus.Konflikt or SchildZuordnungStatus.Veraltet or SchildZuordnungStatus.OhneGegenstueck;
        public bool HatFestlegung => Status is SchildZuordnungStatus.VonHand or SchildZuordnungStatus.KeineZuordnung
            or SchildZuordnungStatus.Veraltet;
        internal string Sortierung => $"{JahrgangText.ToUpperInvariant()}|{KursBez.ToUpperInvariant()}";
    }

    public sealed class SchildKuerzelZeile
    {
        public string Schild { get; set; } = "";
        public string Plan { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string Begruendung { get; set; } = "";
        public bool IstOffen { get; set; }
        public bool VonHand { get; set; }
    }

    public sealed class SchildFachZeile
    {
        public string Schild { get; set; } = "";
        public string Bezeichnung { get; set; } = "";
        public string Plan { get; set; } = "";
        public List<string> PlanListe { get; set; } = new();
        public string StatusText { get; set; } = "";
        public string Begruendung { get; set; } = "";
        public bool IstOffen { get; set; }
        public bool VonHand { get; set; }
    }

    public sealed class SchildPlanOhneZeile
    {
        public string Schluessel { get; set; } = "";
        public string ArtText { get; set; } = "";
        public string Stufe { get; set; } = "";
        public string Fach { get; set; } = "";
        public string Lehrer { get; set; } = "";
        public bool IstOffen { get; set; }
    }

    public sealed class SchildZuordnungErgebnis
    {
        public bool Moeglich { get; set; } = true;
        public string? NichtMoeglich { get; set; }
        public int Schuljahr { get; set; }
        public List<SchildZuordnungZeile> Zeilen { get; } = new();
        public List<SchildKuerzelZeile> Kuerzel { get; } = new();
        public List<SchildFachZeile> Faecher { get; } = new();
        public List<SchildPlanOhneZeile> PlanOhne { get; } = new();
        /// <summary>SchILD-Kürzel → vorgeschlagenes Plan-Kürzel.</summary>
        public Dictionary<string, string> VorschlagKuerzel { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>SchILD-Fach → vollständige vorgeschlagene Liste der Plan-Fächer.</summary>
        public Dictionary<string, List<string>> VorschlagFaecher { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Texte der Vorschläge (für die Rückfrage vor „Vorschläge übernehmen“).</summary>
        public List<string> VorschlagTexte { get; } = new();
        public List<string> PlanKuerzel { get; } = new();
        public List<string> PlanFaecher { get; } = new();
        public int Klassenunterricht { get; set; }
        public int Zeitkonflikte { get; set; }

        public int AnzahlOffen(SchildZuordnungArt art) => Zeilen.Count(z => z.Art == art && z.IstOffen);
    }

    public static class SchildZuordnung
    {
        private static readonly HashSet<string> Oberstufe = new(StringComparer.Ordinal) { "EF", "Q1", "Q2" };
        private static readonly Dictionary<string, string> Kursarten = new(StringComparer.Ordinal)
        {
            ["LK1"] = "LK", ["LK2"] = "LK", ["GKS"] = "GK", ["GKM"] = "GK", ["AB3"] = "GK", ["AB4"] = "GK"
        };
        private static readonly Regex Ziffern = new(@"\d+", RegexOptions.Compiled);
        private const int MaxKonfliktMeldungen = 20;

        public static string Text(SchildZuordnungStatus s) => s switch
        {
            SchildZuordnungStatus.Automatisch => "automatisch",
            SchildZuordnungStatus.OhneLehrkraft => "automatisch (ohne Lehrkraft)",
            SchildZuordnungStatus.VonHand => "von Hand",
            SchildZuordnungStatus.KeineZuordnung => "keine Zuordnung (von Hand)",
            SchildZuordnungStatus.Vorschlag => "Vorschlag",
            SchildZuordnungStatus.Offen => "offen",
            SchildZuordnungStatus.Konflikt => "Konflikt",
            SchildZuordnungStatus.Veraltet => "veraltet",
            SchildZuordnungStatus.Klassenunterricht => "Klassenunterricht",
            _ => "ohne Gegenstück"
        };

        private static bool Zugeordnet(SchildZuordnungStatus s) => s is SchildZuordnungStatus.Automatisch
            or SchildZuordnungStatus.OhneLehrkraft or SchildZuordnungStatus.VonHand or SchildZuordnungStatus.Vorschlag
            or SchildZuordnungStatus.Veraltet;

        public static string KursartNorm(string? a)
        {
            string s = (a ?? "").Trim().ToUpperInvariant();
            return Kursarten.TryGetValue(s, out var n) ? n : s;
        }

        /// <summary>Nummer in der Kursbezeichnung: erste Ziffernfolge nach dem Fach („D-G2“ → 2, „M GK1“ → 1).</summary>
        public static int Nummer(string kursBez, string fach, string klasse)
        {
            string b = (kursBez ?? "").Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(klasse)) b = b.Replace(klasse.ToUpperInvariant(), " ");
            string f = (fach ?? "").Trim().ToUpperInvariant();
            string rest = f.Length > 0 && b.StartsWith(f, StringComparison.Ordinal) ? b[f.Length..] : b;
            var m = Ziffern.Match(rest);
            return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        }

        private static string Kurz(List<string> l)
            => string.Join(", ", l.Take(12)) + (l.Count > 12 ? $" … (+{l.Count - 12})" : "");

        public static SchildZuordnungErgebnis Berechne(SchildExport ex, int jahr, int abschnitt, SchildPlanInfo plan,
            SchildZuordnungDatei datei, List<SchildProblem> probleme, List<SchildZahl> zahlen)
            => new Rechner(ex, jahr, abschnitt, plan, datei, probleme, zahlen).Rechne();

        // =================================================================

        private sealed class Treffer
        {
            public string? Plan;   // Großbuchstaben
            public SchildZuordnungStatus Status;
            public string Grund = "";
            public List<SchildPlanKurs> Kand = new();
        }

        private sealed class Rechner
        {
            private readonly SchildExport _ex;
            private readonly int _jahr, _abschnitt;
            private readonly SchildPlanInfo _plan;
            private readonly SchildZuordnungDatei _datei;
            private readonly List<SchildProblem> _probleme;
            private readonly List<SchildZahl> _zahlen;
            private readonly SchildZuordnungErgebnis _e = new();

            private readonly List<SchildZuordnungZeile> _kurse = new();
            private readonly List<SchildZuordnungZeile> _ohne = new();
            private readonly HashSet<string> _kVerw = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _kSchreibweise = new(StringComparer.Ordinal);
            private readonly HashSet<string> _kAlle = new(StringComparer.Ordinal);
            private readonly HashSet<string> _fVerw = new(StringComparer.Ordinal);
            private readonly HashSet<string> _fAlle = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _handK = new(StringComparer.Ordinal);
            private readonly Dictionary<string, HashSet<string>> _handF = new(StringComparer.Ordinal);
            private bool _mitTeilnehmern;
            private HashSet<string> _orphF = new(StringComparer.Ordinal), _orphK = new(StringComparer.Ordinal),
                                    _unkK = new(StringComparer.Ordinal);

            // Vorschläge: SchILD-Fach → (Plan-Fach → Anzahl), SchILD-Kürzel → (Plan-Kürzel, Anzahl)
            private readonly Dictionary<string, Dictionary<string, int>> _sugF = new(StringComparer.Ordinal);
            private readonly Dictionary<string, (string P, int N)> _sugK = new(StringComparer.Ordinal);

            public Rechner(SchildExport ex, int jahr, int abschnitt, SchildPlanInfo plan, SchildZuordnungDatei datei,
                           List<SchildProblem> probleme, List<SchildZahl> zahlen)
            {
                _ex = ex; _jahr = jahr; _abschnitt = abschnitt; _plan = plan; _datei = datei;
                _probleme = probleme; _zahlen = zahlen;
                _e.Schuljahr = jahr;
            }

            private void P(SchildProblemArt art, string datei, string grund, int? zeile = null, string? spalte = null)
                => _probleme.Add(new SchildProblem { Art = art, Datei = datei, Zeile = zeile, Spalte = spalte, Grund = grund });

            private void Z(string angabe, string wert, string bereich = "Zuordnung")
                => _zahlen.Add(new SchildZahl { Bereich = bereich, Angabe = angabe, Wert = wert });

            private string Name(SchildDateiArt art) => _ex.Info(art)?.Dateiname ?? art.ToString();

            /// <summary>SchILD-Kürzel in der Schreibweise der Datei (Schlüssel = Großbuchstaben).</summary>
            private string SK(string gross) => _kSchreibweise.TryGetValue(gross, out var s) ? s : gross;

            // ---------------------------------------------------------------
            public SchildZuordnungErgebnis Rechne()
            {
                if (_plan.Hinweis != null)
                {
                    _e.Moeglich = false;
                    _e.NichtMoeglich = _plan.Hinweis;
                    P(SchildProblemArt.Warnung, "", "Zuordnung nicht möglich: " + _plan.Hinweis);
                    return _e;
                }
                Einlesen();

                var basis = Lauf(KMap(false), FMap(false));
                var res = basis;
                for (int runde = 0; runde < 3; runde++)
                {
                    if (!Ableiten(res, KMap(true), FMap(true))) break;
                    res = Lauf(KMap(true), FMap(true));
                }
                var noetigF = new HashSet<(string, string)>(_sugF.SelectMany(kv => kv.Value.Keys.Select(b => (kv.Key, b))));
                var noetigK = new HashSet<string>(_sugK.Keys, StringComparer.Ordinal);
                Nebenbei(res);
                var km = KMap(true);
                var fm = FMap(true);

                foreach (var u in _kurse.Concat(_ohne))
                {
                    var r = res[u]; var b = basis[u];
                    if ((r.Status is SchildZuordnungStatus.Automatisch or SchildZuordnungStatus.OhneLehrkraft)
                        && (r.Plan != b.Plan || r.Status != b.Status))
                    {
                        r.Status = SchildZuordnungStatus.Vorschlag;
                        r.Grund += " – setzt einen Vorschlag (Kürzel/Fach) voraus";
                    }
                }

                Zeilen(res, km, fm);
                Meldungen(res, km, noetigF, noetigK);
                return _e;
            }

            // ---------------------------------------------------------------
            //  SchILD-Seite einlesen (gewählter Abschnitt)
            // ---------------------------------------------------------------
            private void Einlesen()
            {
                bool hatB = _ex.Hat(SchildDateiArt.Basisdaten), hatK = _ex.Hat(SchildDateiArt.Kurse),
                     hatL = _ex.Hat(SchildDateiArt.Leistungsdaten);
                _mitTeilnehmern = hatB && hatL;
                var sch = new Dictionary<string, SchildSchueler>(StringComparer.Ordinal);
                if (hatB)
                    foreach (var s in _ex.Schueler)
                        if ((s.Jahr == null || s.Jahr == _jahr) && (s.Abschnitt == null || s.Abschnitt == _abschnitt) &&
                            s.Status is 0 or 2 or 3 or 6)
                            sch.TryAdd(s.PersonSchluessel, s);

                var kurseAb = hatK ? _ex.Kurse.Where(k => k.Jahr == _jahr && k.Abschnitt == _abschnitt).ToList() : new List<SchildKurs>();
                var echte = kurseAb.Where(k => !k.IstKlassenverband).ToList();
                var nachBez = new Dictionary<string, List<SchildKurs>>(StringComparer.Ordinal);
                foreach (var k in echte)
                {
                    string b = k.KursBez.ToUpperInvariant();
                    if (!nachBez.TryGetValue(b, out var l)) nachBez[b] = l = new List<SchildKurs>();
                    l.Add(k);
                }
                var ld = hatL ? _ex.Leistungen.Where(l => l.Jahr == _jahr && l.Abschnitt == _abschnitt).ToList() : new List<SchildLeistung>();

                var teil = echte.ToDictionary(k => k, _ => new Dictionary<string, SchildSchueler>(StringComparer.Ordinal));
                var ohne = new Dictionary<string, SchildZuordnungZeile>(StringComparer.Ordinal);
                foreach (var l in ld)
                {
                    if (!sch.TryGetValue(l.PersonSchluessel, out var s)) continue;
                    if (l.Kurs.Length > 0)
                    {
                        var kand = nachBez.TryGetValue(l.Kurs.ToUpperInvariant(), out var kl)
                            ? kl.Where(k => k.PasstZuJahrgang(s.Jahrgang)).ToList() : new List<SchildKurs>();
                        if (kand.Count == 1) teil[kand[0]].TryAdd(s.PersonSchluessel, s);
                        continue;
                    }
                    if (s.Status is not (0 or 2 or 3) || s.Klasse.Length == 0) continue;
                    string kn = SchildText.KlasseNorm(s.Klasse);
                    if (Oberstufe.Contains(kn) || Oberstufe.Contains(s.Jahrgang.Trim().ToUpperInvariant())) continue;
                    string key = $"{kn}|{l.Fach.ToUpperInvariant()}|{l.Fachlehrer.ToUpperInvariant()}";
                    if (!ohne.TryGetValue(key, out var u))
                    {
                        u = new SchildZuordnungZeile
                        {
                            Art = SchildZuordnungArt.OhneKurs, Klasse = kn, JahrgangText = kn.Length >= 2 ? kn[..2] : kn,
                            Fach = l.Fach.ToUpperInvariant(), FachText = l.Fach, Fachlehrer = l.Fachlehrer,
                            Lehrkraefte = l.Fachlehrer.Length > 0 ? new List<string> { l.Fachlehrer } : new List<string>()
                        };
                        u.Jg.Add(u.JahrgangText);
                        ohne[key] = u;
                    }
                    u.Teiln.TryAdd(s.PersonSchluessel, s);
                }
                foreach (var k in echte.Where(k2 => k2.Klasse.Length > 0))
                    foreach (var s in sch.Values)
                        if (s.Status is 0 or 2 or 3 && SchildText.Gleich(s.Klasse, k.Klasse)) teil[k].TryAdd(s.PersonSchluessel, s);

                foreach (var k in echte)
                {
                    var t = teil[k];
                    var jg = new HashSet<string>(k.Jahrgaenge.Select(j => j.Trim().ToUpperInvariant()), StringComparer.Ordinal);
                    if (jg.Count == 0)
                        foreach (var s in t.Values) if (s.Jahrgang.Length > 0) jg.Add(s.Jahrgang.Trim().ToUpperInvariant());
                    bool ober = jg.Overlaps(Oberstufe) || Oberstufe.Contains(k.Klasse.Trim().ToUpperInvariant());
                    var lehrer = new List<string>();
                    foreach (var x in k.AlleLehrkraefte())
                        if (x.Length > 0 && !lehrer.Any(y => SchildText.Gleich(x, y))) lehrer.Add(x);
                    string kl = ober || k.Klasse.Length == 0 ? "" : SchildText.KlasseNorm(k.Klasse);
                    var u = new SchildZuordnungZeile
                    {
                        Art = ober ? SchildZuordnungArt.Oberstufe : SchildZuordnungArt.Sek1Kurs, KursBez = k.KursBez, Klasse = kl,
                        JahrgangText = k.JahrgangText, Fach = k.Fach.ToUpperInvariant(), FachText = k.Fach,
                        Kursart = KursartNorm(k.Kursart), Nr = Nummer(k.KursBez, k.Fach, kl), Lehrkraefte = lehrer, Zeile = k.Zeile
                    };
                    u.Jg = jg;
                    foreach (var kv in t) u.Teiln.TryAdd(kv.Key, kv.Value);
                    _kurse.Add(u);
                }
                _ohne.AddRange(ohne.Values.OrderBy(u => u.Klasse, StringComparer.Ordinal).ThenBy(u => u.Fach, StringComparer.Ordinal)
                                          .ThenBy(u => u.Fachlehrer.ToUpperInvariant(), StringComparer.Ordinal));
                foreach (var u in _kurse.Concat(_ohne))
                    u.TKlassen = new HashSet<string>(u.Teiln.Values.Where(s => s.Klasse.Length > 0).Select(s => SchildText.KlasseNorm(s.Klasse)),
                                                     StringComparer.Ordinal);
                _kurse.Sort((a, b) =>
                {
                    int c = (a.Art == SchildZuordnungArt.Oberstufe ? 0 : 1).CompareTo(b.Art == SchildZuordnungArt.Oberstufe ? 0 : 1);
                    return c != 0 ? c : string.CompareOrdinal(a.Sortierung, b.Sortierung);
                });

                // ---- Kürzel und Fächer, die SchILD verwendet ----
                void K(string? x)
                {
                    if (string.IsNullOrWhiteSpace(x)) return;
                    string g = x.Trim().ToUpperInvariant();
                    _kVerw.Add(g);
                    _kSchreibweise.TryAdd(g, x.Trim());
                }
                foreach (var u in _kurse.Concat(_ohne)) foreach (var x in u.Lehrkraefte) K(x);
                foreach (var k in _ex.Klassen)
                    if ((k.Jahr == null || k.Jahr == _jahr) && (k.Abschnitt == null || k.Abschnitt == _abschnitt)) K(k.Klassenlehrer);
                foreach (var k in kurseAb) { K(k.Kursleiter); foreach (var z in k.Zusatzkraefte) K(z); }
                foreach (var l in ld) K(l.Fachlehrer);
                _kAlle.UnionWith(_kVerw);
                if (_ex.Hat(SchildDateiArt.Lehrkraefte))
                    foreach (var l in _ex.Lehrkraefte) _kAlle.Add(l.Kuerzel.Trim().ToUpperInvariant());
                foreach (var k in kurseAb) _fVerw.Add(k.Fach.Trim().ToUpperInvariant());
                foreach (var l in ld) _fVerw.Add(l.Fach.Trim().ToUpperInvariant());
                _fAlle.UnionWith(_fVerw);
                if (_ex.Hat(SchildDateiArt.Faecher))
                    foreach (var f in _ex.Faecher) _fAlle.Add(f.Kuerzel.Trim().ToUpperInvariant());

                foreach (var kv in _datei.Kuerzel) _handK[kv.Key.Trim().ToUpperInvariant()] = kv.Value.Trim().ToUpperInvariant();
                foreach (var kv in _datei.Faecher)
                    _handF[kv.Key.Trim().ToUpperInvariant()] = new HashSet<string>(kv.Value.Select(x => x.Trim().ToUpperInvariant()), StringComparer.Ordinal);
                var zieleK = new HashSet<string>(_handK.Values, StringComparer.Ordinal);
                var zieleF = new HashSet<string>(_handF.Values.SelectMany(v => v), StringComparer.Ordinal);
                _orphF = new HashSet<string>(_plan.Faecher.Where(f => !_fAlle.Contains(f) && !zieleF.Contains(f)), StringComparer.Ordinal);
                _orphK = new HashSet<string>(_plan.Kuerzel.Where(k => !_kAlle.Contains(k) && !zieleK.Contains(k)), StringComparer.Ordinal);
                _unkK = new HashSet<string>(_kVerw.Where(k => !_plan.Kuerzel.Contains(k) && !_handK.ContainsKey(k)), StringComparer.Ordinal);
            }

            // ---------------------------------------------------------------
            //  Kürzel- und Fächertabelle (von Hand → gleich → Vorschlag)
            // ---------------------------------------------------------------
            private Func<string?, string?> KMap(bool mitVorschlag) => s =>
            {
                string S = (s ?? "").Trim().ToUpperInvariant();
                if (S.Length == 0) return null;
                if (_handK.TryGetValue(S, out var h)) return h;
                if (_plan.Kuerzel.Contains(S)) return S;
                return mitVorschlag && _sugK.TryGetValue(S, out var v) ? v.P : null;
            };

            private Func<string, HashSet<string>> FMap(bool mitVorschlag) => x =>
            {
                string X = (x ?? "").Trim().ToUpperInvariant();
                if (_handF.TryGetValue(X, out var h)) return h;
                var r = new HashSet<string>(StringComparer.Ordinal);
                if (_plan.Faecher.Contains(X)) r.Add(X);
                if (mitVorschlag && _sugF.TryGetValue(X, out var d)) r.UnionWith(d.Keys);
                return r;
            };

            private static bool FachPasst(string x, SchildPlanKurs p, Func<string, HashSet<string>> fm) => fm(x).Overlaps(p.Faecher);
            private static bool ArtPasst(string a, string b) => a.Length == 0 || b.Length == 0 || a == b;

            private static bool InPool(SchildZuordnungZeile u, SchildPlanKurs p)
            {
                if (u.Art == SchildZuordnungArt.Oberstufe)
                    return p.Oberstufe && (u.Jg.Count == 0 || p.Stufen.Overlaps(u.Jg));
                if (p.Oberstufe || p.Klassen.Count == 0) return false;
                if (u.Jg.Count > 0 && !p.Jahrgaenge().Overlaps(u.Jg)) return false;
                if (u.Klasse.Length > 0 && !p.Klassen.Contains(u.Klasse)) return false;
                return true;
            }

            private static string StufeText(SchildZuordnungZeile u) => u.Art != SchildZuordnungArt.OhneKurs ? u.JahrgangText : u.Klasse;

            private (List<SchildPlanKurs> Kand, string Grund, bool OhneLk) Kandidaten(SchildZuordnungZeile u,
                Func<string?, string?> km, Func<string, HashSet<string>> fm)
            {
                var pool = _plan.Kurse.Values.Where(p => InPool(u, p)).ToList();
                string art = u.Art == SchildZuordnungArt.Oberstufe ? "Stufe" : "Jahrgang";
                if (pool.Count == 0) return (new List<SchildPlanKurs>(), $"kein Plan-Kurs für {art} {StufeText(u)}", false);
                var f = pool.Where(p => FachPasst(u.Fach, p, fm)).ToList();
                if (f.Count == 0) return (new List<SchildPlanKurs>(), $"kein Plan-Kurs mit Fach „{u.FachText}“ ({art} {StufeText(u)})", false);
                var a = f.Where(p => ArtPasst(u.Kursart, p.Art)).ToList();
                if (a.Count == 0)
                    return (new List<SchildPlanKurs>(), $"kein Plan-Kurs mit Kursart {u.Kursart} (Fach „{u.FachText}“, {art} {StufeText(u)})", false);
                if (u.Lehrkraefte.Count == 0) return (a, "", true);
                var lp = new HashSet<string>(u.Lehrkraefte.Select(km).Where(x => x != null).Select(x => x!), StringComparer.Ordinal);
                var l = a.Where(p => p.Lehrer.Overlaps(lp)).ToList();
                if (l.Count == 0) return (new List<SchildPlanKurs>(), $"Lehrkraft passt nicht ({string.Join(", ", u.Lehrkraefte)})", false);
                return (l, "", false);
            }

            private static bool GleicherPool(SchildZuordnungZeile u, SchildZuordnungZeile v)
            {
                if ((u.Art == SchildZuordnungArt.Oberstufe) != (v.Art == SchildZuordnungArt.Oberstufe)) return false;
                if (u.Jg.Count > 0 && v.Jg.Count > 0 && !u.Jg.Overlaps(v.Jg)) return false;
                if (u.Klasse.Length > 0 && v.Klasse.Length > 0 && u.Klasse != v.Klasse) return false;
                return true;
            }

            private static IOrderedEnumerable<SchildZuordnungZeile> Sortiert(IEnumerable<SchildZuordnungZeile> l)
                => l.OrderBy(u => u.Sortierung, StringComparer.Ordinal);

            // ---------------------------------------------------------------
            //  Ein Durchlauf der Zuordnung mit gegebenen Tabellen
            // ---------------------------------------------------------------
            private Dictionary<SchildZuordnungZeile, Treffer> Lauf(Func<string?, string?> km, Func<string, HashSet<string>> fm)
            {
                var res = new Dictionary<SchildZuordnungZeile, Treffer>();
                var veraltet = new Dictionary<SchildZuordnungZeile, string>();
                var vergeben = new List<(SchildZuordnungZeile U, string Key)>();
                var offen = new List<SchildZuordnungZeile>();

                foreach (var u in _kurse)
                {
                    var f = _datei.Kurs(_jahr, u.JahrgangText, u.KursBez);
                    if (f != null)
                    {
                        if (f.Plan == null)
                        { res[u] = new Treffer { Status = SchildZuordnungStatus.KeineZuordnung, Grund = "von Hand: keine Zuordnung" }; continue; }
                        var p = _plan.Kurs(f.Plan);
                        if (p != null)
                        {
                            res[u] = new Treffer { Plan = p.Gross, Status = SchildZuordnungStatus.VonHand, Grund = "von Hand festgelegt" };
                            vergeben.Add((u, p.Gross));
                            continue;
                        }
                        veraltet[u] = f.Plan;
                    }
                    offen.Add(u);
                }

                var C = offen.ToDictionary(u => u, u => Kandidaten(u, km, fm));
                var zu = new Dictionary<SchildZuordnungZeile, (SchildPlanKurs P, string Wie)>();
                var belegt = new Dictionary<string, List<SchildPlanKurs>>(StringComparer.Ordinal);
                void Belege(SchildZuordnungZeile u, SchildPlanKurs p)
                {
                    foreach (var pk in u.Teiln.Keys)
                    {
                        if (!belegt.TryGetValue(pk, out var l)) belegt[pk] = l = new List<SchildPlanKurs>();
                        l.Add(p);
                    }
                }
                foreach (var (v, key) in vergeben) Belege(v, _plan.Kurse[key]);

                List<SchildPlanKurs> Frei(SchildZuordnungZeile u)
                    => C[u].Kand.Where(p => !vergeben.Any(t => t.U != u && t.Key == p.Gross && GleicherPool(t.U, u))).ToList();
                void Setze(SchildZuordnungZeile u, SchildPlanKurs p, string wie)
                {
                    zu[u] = (p, wie);
                    vergeben.Add((u, p.Gross));
                    Belege(u, p);
                }
                int Zeitkonflikte(SchildZuordnungZeile u, SchildPlanKurs p)
                    => u.Teiln.Keys.Count(pk => belegt.TryGetValue(pk, out var l) && l.Any(q => q != p && q.Zeiten.Overlaps(p.Zeiten)));
                // Plan-Kurse, mit denen sich p für die Teilnehmer von u überschneiden würde (für die Begründung)
                List<string> Ueberschneidungen(SchildZuordnungZeile u, SchildPlanKurs p)
                    => u.Teiln.Keys.Where(pk => belegt.ContainsKey(pk))
                                   .SelectMany(pk => belegt[pk].Where(q => q != p && q.Zeiten.Overlaps(p.Zeiten)))
                                   .Select(q => q.Schluessel).Distinct(StringComparer.Ordinal)
                                   .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                List<SchildZuordnungZeile> Rest() => offen.Where(u => !zu.ContainsKey(u)).ToList();

                while (true)
                {
                    bool fort = false;
                    // A: genau ein passender Plan-Kurs
                    foreach (var u in Rest().OrderBy(x => C[x].Kand.Count == 1 && x.Nr > 0 && C[x].Kand[0].Nr == x.Nr ? 0 : 1)
                                            .ThenBy(x => x.Sortierung, StringComparer.Ordinal))
                    {
                        if (C[u].Kand.Count != 1) continue;
                        var r = Frei(u);
                        if (r.Count == 1) { Setze(u, r[0], "eindeutig"); fort = true; }
                    }
                    // B: Stundenplan der Teilnehmer (keine zwei Kurse zur selben Zeit)
                    foreach (var u in Sortiert(Rest()))
                    {
                        var r = Frei(u);
                        if (r.Count <= 1 || u.Teiln.Count == 0) continue;
                        var kz = r.Select(p => (P: p, N: Zeitkonflikte(u, p))).ToList();
                        var ohneKonflikt = kz.Where(x => x.N == 0).Select(x => x.P).ToList();
                        if (ohneKonflikt.Count == 1 && kz.Any(x => x.N > 0))
                        { Setze(u, ohneKonflikt[0], "Stundenplan der Teilnehmer"); fort = true; }
                    }
                    // C: Nummer in der Kursbezeichnung
                    foreach (var u in Sortiert(Rest()))
                    {
                        var r = Frei(u);
                        if (r.Count <= 1 || u.Nr <= 0) continue;
                        var n = r.Where(p => p.Nr == u.Nr).ToList();
                        if (n.Count == 1) { Setze(u, n[0], "Nummer"); fort = true; }
                    }
                    // D: Sek I – Klassen der Teilnehmer bzw. Gruppe nur dieser Klasse
                    foreach (var u in Sortiert(Rest().Where(u2 => u2.Art != SchildZuordnungArt.Oberstufe)))
                    {
                        var r = Frei(u);
                        if (r.Count <= 1) continue;
                        var cov = r.Where(p => u.TKlassen.Count > 0 && u.TKlassen.IsSubsetOf(p.Klassen)).ToList();
                        if (cov.Count == 1) { Setze(u, cov[0], "Klassen der Teilnehmer"); fort = true; continue; }
                        if (u.Klasse.Length > 0)
                        {
                            var ein = r.Where(p => p.Klassen.Count == 1).ToList();
                            if (ein.Count == 1) { Setze(u, ein[0], "Gruppe nur dieser Klasse"); fort = true; }
                        }
                    }
                    // E: als einziger übrig – nur, wenn der Stundenplan der Teilnehmer dagegen nichts einwendet
                    //    (sonst bliebe z. B. nach einer falschen Festlegung von Hand der Nachbarkurs still vertauscht)
                    foreach (var u in Sortiert(Rest()))
                    {
                        var r = Frei(u);
                        if (r.Count == 1 && Zeitkonflikte(u, r[0]) == 0) { Setze(u, r[0], "als einziger übrig"); fort = true; }
                    }
                    if (!fort) break;
                }

                foreach (var u in offen)
                {
                    var (kand, grund, ohneLk) = C[u];
                    veraltet.TryGetValue(u, out var alt);
                    string pre = alt != null ? $"Festlegung „{alt}“ gibt es im Plan nicht mehr; " : "";
                    if (zu.TryGetValue(u, out var z))
                    {
                        string g = u.Art == SchildZuordnungArt.Oberstufe ? "Stufe, Fach, Kursart" : "Jahrgang, Fach";
                        if (!ohneLk) g += ", Lehrkraft";
                        if (z.Wie != "eindeutig") g += "; " + z.Wie;
                        res[u] = new Treffer
                        {
                            Plan = z.P.Gross, Kand = kand,
                            Status = alt != null ? SchildZuordnungStatus.Veraltet
                                   : ohneLk ? SchildZuordnungStatus.OhneLehrkraft : SchildZuordnungStatus.Automatisch,
                            Grund = alt != null ? pre + "automatisch: " + g : g
                        };
                    }
                    else if (kand.Count == 0)
                        res[u] = new Treffer
                        {
                            Status = alt != null ? SchildZuordnungStatus.Veraltet : SchildZuordnungStatus.Offen, Grund = pre + grund
                        };
                    else if (Frei(u).Count == 0)
                    {
                        var anderer = vergeben.FirstOrDefault(t => t.Key == kand[0].Gross && t.U != u).U;
                        res[u] = new Treffer
                        {
                            Status = SchildZuordnungStatus.Konflikt, Kand = kand,
                            Grund = $"passender Plan-Kurs „{kand[0].Schluessel}“ ist schon „{anderer?.KursBez ?? "?"}“ zugeordnet"
                        };
                    }
                    else
                    {
                        var frei = Frei(u);
                        int nz = frei.Count == 1 ? Zeitkonflikte(u, frei[0]) : 0;
                        var mit = nz > 0 ? Ueberschneidungen(u, frei[0]) : new List<string>();
                        res[u] = new Treffer
                        {
                            Status = alt != null ? SchildZuordnungStatus.Veraltet : SchildZuordnungStatus.Offen, Kand = kand,
                            Grund = pre + (nz > 0
                                ? $"einziger freier Plan-Kurs „{frei[0].Schluessel}“ passt nicht zum Stundenplan der Teilnehmer " +
                                  $"({nz} Teilnehmer hätten dann zur selben Zeit auch {string.Join(", ", mit.Take(3))}" +
                                  (mit.Count > 3 ? " …" : "") + ") – vermutlich stimmt eine dieser Zuordnungen nicht"
                                : "mehrdeutig: " + string.Join(", ", frei.Select(p => p.Schluessel)))
                        };
                    }
                }

                // ---- Sek I ohne Kurs ----
                var erreicht = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var u in _kurse)
                {
                    var r = res[u];
                    if (r.Plan == null) continue;
                    foreach (var pk in u.Teiln.Keys)
                    {
                        if (!erreicht.TryGetValue(pk, out var h)) erreicht[pk] = h = new HashSet<string>(StringComparer.Ordinal);
                        h.Add(r.Plan);
                    }
                }
                foreach (var w in _ohne)
                {
                    string? alt = null;
                    var f = _datei.Ohne(_jahr, w.Klasse, w.Fach, w.Fachlehrer);
                    if (f != null)
                    {
                        if (f.Plan == null)
                        { res[w] = new Treffer { Status = SchildZuordnungStatus.KeineZuordnung, Grund = "von Hand: keine Zuordnung" }; continue; }
                        var p = _plan.Kurs(f.Plan);
                        if (p != null)
                        { res[w] = new Treffer { Plan = p.Gross, Status = SchildZuordnungStatus.VonHand, Grund = "von Hand festgelegt" }; continue; }
                        alt = f.Plan;
                    }
                    string pre = alt != null ? $"Festlegung „{alt}“ gibt es im Plan nicht mehr; " : "";
                    var stOffen = alt != null ? SchildZuordnungStatus.Veraltet : SchildZuordnungStatus.Offen;
                    string? lp = km(w.Fachlehrer);
                    var aus = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var pk in w.Teiln.Keys) if (erreicht.TryGetValue(pk, out var h)) aus.UnionWith(h);
                    var G = _plan.Kurse.Values.Where(p => !p.Oberstufe && p.Klassen.Contains(w.Klasse) && FachPasst(w.Fach, p, fm)
                                                          && !aus.Contains(p.Gross)).ToList();
                    var GT = G.Where(p => lp != null && p.Lehrer.Contains(lp)).ToList();
                    var fx = fm(w.Fach);
                    var kuf = _plan.KlassenUnterricht.TryGetValue(w.Klasse, out var ku)
                        ? ku.Where(x => fx.Contains(x.Fach)).ToList() : new List<(string Fach, string Lehrer)>();
                    var kut = kuf.Where(x => lp != null && x.Lehrer == lp).ToList();
                    if (GT.Count > 0 && kut.Count == 0)
                        res[w] = GT.Count == 1
                            ? new Treffer { Plan = GT[0].Gross, Status = alt != null ? SchildZuordnungStatus.Veraltet : SchildZuordnungStatus.Automatisch,
                                            Grund = pre + "Klasse, Fach, Fachlehrer", Kand = GT }
                            : new Treffer { Status = stOffen, Kand = GT, Grund = pre + "mehrdeutig: " + string.Join(", ", GT.Select(p => p.Schluessel)) };
                    else if (GT.Count > 0)
                        res[w] = new Treffer { Status = stOffen, Kand = GT,
                                               Grund = pre + "Klassenunterricht oder Gruppe? Im Plan gibt es beides mit dieser Lehrkraft" };
                    else if (kuf.Count > 0)
                        res[w] = new Treffer { Status = SchildZuordnungStatus.Klassenunterricht, Grund = "Klassenunterricht" };
                    else if (G.Count > 0)
                        res[w] = new Treffer { Status = stOffen, Kand = G,
                                               Grund = pre + $"Lehrkraft passt nicht ({(w.Fachlehrer.Length > 0 ? w.Fachlehrer : "ohne")})" };
                    else
                        res[w] = new Treffer { Status = SchildZuordnungStatus.OhneGegenstueck, Grund = "weder Gruppe noch Klassenunterricht im Plan" };
                }
                return res;
            }

            // ---------------------------------------------------------------
            //  Vorschläge für Kürzel/Fächer, die es nur auf einer Seite gibt
            // ---------------------------------------------------------------
            private static List<SchildPlanKurs> NrFilter(List<SchildPlanKurs> r, SchildZuordnungZeile u)
            {
                if (r.Count > 1 && u.Nr > 0)
                {
                    var n = r.Where(p => p.Nr == u.Nr).ToList();
                    if (n.Count > 0) return n;
                }
                return r;
            }

            private static void Zaehle(Dictionary<string, Dictionary<string, int>> d, string a, string b)
            {
                if (!d.TryGetValue(a, out var c)) d[a] = c = new Dictionary<string, int>(StringComparer.Ordinal);
                c.TryGetValue(b, out int n);
                c[b] = n + 1;
            }

            /// <returns>true, wenn neue Vorschläge dazugekommen sind.</returns>
            private bool Ableiten(Dictionary<SchildZuordnungZeile, Treffer> res, Func<string?, string?> km, Func<string, HashSet<string>> fm)
            {
                var orphF = new HashSet<string>(_orphF.Where(b => !_sugF.Values.Any(d => d.ContainsKey(b))), StringComparer.Ordinal);
                var orphK = new HashSet<string>(_orphK.Where(p => !_sugK.Values.Any(v => v.P == p)), StringComparer.Ordinal);
                var unkK = new HashSet<string>(_unkK.Where(s => !_sugK.ContainsKey(s)), StringComparer.Ordinal);
                var evF = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);   // Plan-Fach → SchILD-Fach → n
                var evK = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);   // Plan-Kürzel → SchILD-Kürzel → n

                foreach (var u in _kurse)
                {
                    var r = res[u];
                    if (r.Status is not (SchildZuordnungStatus.Offen or SchildZuordnungStatus.Veraltet) || r.Kand.Count > 0) continue;
                    var lp = new HashSet<string>(u.Lehrkraefte.Select(km).Where(x => x != null).Select(x => x!), StringComparer.Ordinal);
                    var pool = _plan.Kurse.Values.Where(p => InPool(u, p) && ArtPasst(u.Kursart, p.Art)).ToList();
                    if (lp.Count > 0)
                    {
                        var c = NrFilter(pool.Where(p => p.Lehrer.Overlaps(lp) && !FachPasst(u.Fach, p, fm) && p.Faecher.Overlaps(orphF)).ToList(), u);
                        if (c.Count == 1)
                            foreach (var b in c[0].Faecher.Where(orphF.Contains)) Zaehle(evF, b, u.Fach);
                    }
                    var uk = u.Lehrkraefte.Select(x => x.Trim().ToUpperInvariant()).Where(unkK.Contains).Distinct().ToList();
                    if (uk.Count == 1)
                    {
                        var c = NrFilter(pool.Where(p => FachPasst(u.Fach, p, fm) && p.Lehrer.Overlaps(orphK)).ToList(), u);
                        if (c.Count == 1)
                        {
                            var pk = c[0].Lehrer.Where(orphK.Contains).ToList();
                            if (pk.Count == 1) Zaehle(evK, pk[0], uk[0]);
                        }
                    }
                }
                foreach (var w in _ohne)
                {
                    var r = res[w];
                    if (r.Status is not (SchildZuordnungStatus.OhneGegenstueck or SchildZuordnungStatus.Offen or SchildZuordnungStatus.Veraltet))
                        continue;
                    string S = w.Fachlehrer.Trim().ToUpperInvariant();
                    string? lp = km(w.Fachlehrer);
                    var gruppen = _plan.Kurse.Values.Where(p => !p.Oberstufe && p.Klassen.Contains(w.Klasse)).ToList();
                    _plan.KlassenUnterricht.TryGetValue(w.Klasse, out var ku);
                    if (lp != null && r.Status == SchildZuordnungStatus.OhneGegenstueck)
                    {
                        var c = new HashSet<string>(StringComparer.Ordinal);
                        if (ku != null) foreach (var x in ku) if (x.Lehrer == lp && orphF.Contains(x.Fach)) c.Add(x.Fach);
                        foreach (var p in gruppen) if (p.Lehrer.Contains(lp)) foreach (var b in p.Faecher) if (orphF.Contains(b)) c.Add(b);
                        if (c.Count == 1) Zaehle(evF, c.First(), w.Fach);
                    }
                    if (unkK.Contains(S))
                    {
                        var fx = fm(w.Fach);
                        var c = new HashSet<string>(StringComparer.Ordinal);
                        if (ku != null) foreach (var x in ku) if (fx.Contains(x.Fach) && orphK.Contains(x.Lehrer)) c.Add(x.Lehrer);
                        foreach (var p in gruppen) if (FachPasst(w.Fach, p, fm)) foreach (var l in p.Lehrer) if (orphK.Contains(l)) c.Add(l);
                        if (c.Count == 1) Zaehle(evK, c.First(), S);
                    }
                }

                bool neu = false;
                foreach (var (b, c) in evF)
                {
                    if (c.Count != 1) continue;
                    var (x, n) = c.First();
                    if (!_sugF.TryGetValue(x, out var d)) _sugF[x] = d = new Dictionary<string, int>(StringComparer.Ordinal);
                    d[b] = n;
                    neu = true;
                }
                foreach (var kv in JeSchild(evK))
                { _sugK[kv.Key] = kv.Value; neu = true; }
                return neu;
            }

            /// <summary>Eindeutige Paare SchILD-Kürzel ↔ Plan-Kürzel aus den Belegen.</summary>
            private static Dictionary<string, (string P, int N)> JeSchild(Dictionary<string, Dictionary<string, int>> evK)
            {
                var jeS = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                foreach (var (p, c) in evK)
                {
                    if (c.Count != 1) continue;
                    var (s, n) = c.First();
                    if (!jeS.TryGetValue(s, out var d)) jeS[s] = d = new Dictionary<string, int>(StringComparer.Ordinal);
                    d[p] = n;
                }
                return jeS.Where(kv => kv.Value.Count == 1)
                          .ToDictionary(kv => kv.Key, kv => (P: kv.Value.First().Key, N: kv.Value.First().Value), StringComparer.Ordinal);
            }

            /// <summary>
            /// Nebenbei-Vorschläge: verwaiste Plan-Fächer/-Kürzel, die nur in schon zugeordneten Kursen vorkommen
            /// (z. B. Plan „P G1“ + „PH G1“ in einem Kurs, SchILD kennt nur „PH“). Für die Zuordnung nicht nötig,
            /// aber für Rückweg (5.3) und Konten (5.2c).
            /// </summary>
            private void Nebenbei(Dictionary<SchildZuordnungZeile, Treffer> res)
            {
                var alle = _kurse.Concat(_ohne).ToList();
                foreach (var b in _orphF.Where(b2 => !_sugF.Values.Any(d => d.ContainsKey(b2))).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var xs = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var u in alle)
                    {
                        var r = res[u];
                        if (r.Plan == null || !_plan.Kurse[r.Plan].Faecher.Contains(b)) continue;
                        xs.TryGetValue(u.Fach, out int n);
                        xs[u.Fach] = n + 1;
                    }
                    if (xs.Count != 1) continue;
                    var (x, anz) = xs.First();
                    if (!_sugF.TryGetValue(x, out var d)) _sugF[x] = d = new Dictionary<string, int>(StringComparer.Ordinal);
                    d[b] = anz;
                }
                var restK = new HashSet<string>(_orphK.Where(p => !_sugK.Values.Any(v => v.P == p)), StringComparer.Ordinal);
                var unkRest = new HashSet<string>(_unkK.Where(s => !_sugK.ContainsKey(s)), StringComparer.Ordinal);
                var evK = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                foreach (var u in alle)
                {
                    var r = res[u];
                    if (r.Plan == null) continue;
                    var uk = u.Lehrkraefte.Select(x => x.Trim().ToUpperInvariant()).Where(unkRest.Contains).Distinct().ToList();
                    var pk = _plan.Kurse[r.Plan].Lehrer.Where(restK.Contains).ToList();
                    if (uk.Count == 1 && pk.Count == 1) Zaehle(evK, pk[0], uk[0]);
                }
                foreach (var kv in JeSchild(evK)) _sugK[kv.Key] = kv.Value;
            }

            // ---------------------------------------------------------------
            //  Zeilen für den Reiter „Zuordnung“
            // ---------------------------------------------------------------
            private void Zeilen(Dictionary<SchildZuordnungZeile, Treffer> res, Func<string?, string?> km, Func<string, HashSet<string>> fm)
            {
                foreach (var u in _kurse.Concat(_ohne))
                {
                    var r = res[u];
                    u.Plan = r.Plan != null ? _plan.Kurse[r.Plan].Schluessel : null;
                    u.Status = r.Status;
                    u.Begruendung = r.Grund;
                    u.LehrkraefteText = string.Join(", ", u.Lehrkraefte.Select(x =>
                    {
                        string? m = km(x);
                        return m == null ? x + " (?)" : SchildText.Gleich(m, x) ? x : $"{x} → {_plan.KuerzelText(m)}";
                    }));
                    List<SchildPlanKurs> pool;
                    if (u.Art == SchildZuordnungArt.OhneKurs)
                    {
                        pool = _plan.Kurse.Values.Where(p => !p.Oberstufe && p.Klassen.Contains(u.Klasse)).ToList();
                        u.Kandidaten = r.Kand.Count > 0 ? r.Kand.ToList()
                            : pool.Where(p => FachPasst(u.Fach, p, fm)).ToList();
                    }
                    else
                    {
                        pool = _plan.Kurse.Values.Where(p => InPool(u, p)).ToList();
                        var k = r.Kand.Count > 0 ? r.Kand : Kandidaten(u, km, fm).Kand;
                        u.Kandidaten = k.OrderBy(p => u.Nr > 0 && p.Nr == u.Nr ? 0 : 1)
                                        .ThenBy(p => p.Schluessel, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    var kandSet = new HashSet<SchildPlanKurs>(u.Kandidaten);
                    u.Weitere = pool.Where(p => !kandSet.Contains(p)).OrderBy(p => p.Schluessel, StringComparer.OrdinalIgnoreCase).ToList();
                    if (u.Status != SchildZuordnungStatus.Klassenunterricht) _e.Zeilen.Add(u);
                    else _e.Klassenunterricht++;
                }

                // Kürzel
                foreach (var S in _kVerw.OrderBy(x => x, StringComparer.Ordinal))
                {
                    var z = new SchildKuerzelZeile { Schild = SK(S) };
                    if (_handK.TryGetValue(S, out var h))
                    { z.Plan = _plan.KuerzelText(h); z.StatusText = "von Hand"; z.VonHand = true;
                      if (!_plan.Kuerzel.Contains(h)) { z.StatusText = "von Hand – im Plan unbekannt"; z.IstOffen = true; } }
                    else if (_plan.Kuerzel.Contains(S)) { z.Plan = _plan.KuerzelText(S); z.StatusText = "gleich"; }
                    else if (_sugK.TryGetValue(S, out var v))
                    { z.Plan = _plan.KuerzelText(v.P); z.StatusText = "Vorschlag"; z.IstOffen = true; z.Begruendung = $"passt bei {v.N} Kurs(en)/Unterricht(en)"; }
                    else { z.Plan = "—"; z.StatusText = "unbekannt"; z.IstOffen = true; z.Begruendung = "Kürzel gibt es im Plan nicht"; }
                    _e.Kuerzel.Add(z);
                }
                foreach (var p in _orphK.Where(p2 => !_sugK.Values.Any(v => v.P == p2)).OrderBy(x => x, StringComparer.Ordinal))
                    _e.Kuerzel.Add(new SchildKuerzelZeile { Schild = "", Plan = _plan.KuerzelText(p), StatusText = "nur im Plan",
                                                            Begruendung = "in SchILD nicht gefunden" });
                _e.PlanKuerzel.AddRange(_plan.Kuerzel.Select(_plan.KuerzelText).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

                // Fächer
                var bez = new Dictionary<string, string>(StringComparer.Ordinal);
                if (_ex.Hat(SchildDateiArt.Faecher))
                    foreach (var f in _ex.Faecher) bez.TryAdd(f.Kuerzel.Trim().ToUpperInvariant(), f.Bezeichnung);
                foreach (var X in _fVerw.OrderBy(x => x, StringComparer.Ordinal))
                {
                    var liste = fm(X).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var z = new SchildFachZeile { Schild = X, Bezeichnung = bez.TryGetValue(X, out var b) ? b : "", PlanListe = liste,
                                                  Plan = liste.Count > 0 ? string.Join(", ", liste) : "—" };
                    if (_handF.ContainsKey(X)) { z.StatusText = "von Hand"; z.VonHand = true; }
                    else if (_sugF.TryGetValue(X, out var d))
                    { z.StatusText = "Vorschlag"; z.IstOffen = true;
                      z.Begruendung = "dazu " + string.Join(", ", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} (passt bei {kv.Value})")); }
                    else if (liste.Count > 0) z.StatusText = "gleich";
                    else { z.StatusText = "unbekannt"; z.IstOffen = true; z.Begruendung = "Fach gibt es im Plan nicht"; }
                    _e.Faecher.Add(z);
                }
                foreach (var b in _orphF.Where(b2 => !_sugF.Values.Any(d => d.ContainsKey(b2))).OrderBy(x => x, StringComparer.Ordinal))
                    _e.Faecher.Add(new SchildFachZeile { Schild = "", Plan = b, PlanListe = new List<string> { b }, StatusText = "nur im Plan",
                                                         Begruendung = "kein SchILD-Fach – für den Rückweg (5.3) zuordnen" });
                _e.PlanFaecher.AddRange(_plan.Faecher.OrderBy(x => x, StringComparer.Ordinal));

                // Vorschläge (für „Vorschläge übernehmen“)
                foreach (var (s, v) in _sugK.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    string schild = SK(s);
                    _e.VorschlagKuerzel[schild] = _plan.KuerzelText(v.P);
                    _e.VorschlagTexte.Add($"Lehrkraft „{schild}“ = „{_plan.KuerzelText(v.P)}“ (passt bei {v.N})");
                }
                foreach (var (x, d) in _sugF.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    _e.VorschlagFaecher[x] = fm(x).OrderBy(y => y, StringComparer.Ordinal).ToList();
                    _e.VorschlagTexte.Add($"Fach „{x}“ = {string.Join(", ", _e.VorschlagFaecher[x])}");
                }

                // Plan-Kurse ohne SchILD
                var ziele = new HashSet<string>(_kurse.Concat(_ohne).Select(u => res[u].Plan).Where(p => p != null).Select(p => p!), StringComparer.Ordinal);
                foreach (var p in _plan.Kurse.Values.Where(p2 => !ziele.Contains(p2.Gross) && (p2.Oberstufe || p2.Klassen.Count > 0))
                                       .OrderBy(p2 => p2.Oberstufe ? 0 : 1).ThenBy(p2 => p2.Gross, StringComparer.Ordinal))
                    _e.PlanOhne.Add(new SchildPlanOhneZeile
                    {
                        Schluessel = p.Schluessel, ArtText = p.Oberstufe ? "Oberstufenkurs" : "Sek-I-Gruppe", Stufe = p.StufeText,
                        Fach = p.FachText, Lehrer = p.LehrerText, IstOffen = p.Oberstufe
                    });
            }

            // ---------------------------------------------------------------
            //  Meldungen und Zahlen
            // ---------------------------------------------------------------
            private void Meldungen(Dictionary<SchildZuordnungZeile, Treffer> res, Func<string?, string?> km,
                                   HashSet<(string, string)> noetigF, HashSet<string> noetigK)
            {
                string fKu = Name(SchildDateiArt.Kurse), fLe = Name(SchildDateiArt.Leistungsdaten);

                foreach (var (s, v) in _sugK.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    P(noetigK.Contains(s) ? SchildProblemArt.Warnung : SchildProblemArt.Hinweis, "Zuordnung",
                      $"Vorschlag: Lehrkraft „{SK(s)}“ (SchILD) = „{_plan.KuerzelText(v.P)}“ (Plan) – " +
                      $"passt bei {v.N} Kurs(en)/Unterricht(en). Im Reiter „Zuordnung“ mit „Vorschläge übernehmen“ bestätigen.");
                foreach (var (x, d) in _sugF.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    foreach (var (b, n) in d.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                        P(noetigF.Contains((x, b)) ? SchildProblemArt.Warnung : SchildProblemArt.Hinweis, "Zuordnung",
                          $"Vorschlag: Fach „{x}“ (SchILD) umfasst auch Plan-Fach „{b}“ – passt bei {n} Kurs(en)/Unterricht(en). " +
                          "Im Reiter „Zuordnung“ mit „Vorschläge übernehmen“ bestätigen.");

                foreach (var u in _kurse)
                {
                    var r = res[u];
                    string name = $"Kurs „{u.KursBez}“ ({u.JahrgangText})";
                    if (r.Status is SchildZuordnungStatus.Offen or SchildZuordnungStatus.Konflikt or SchildZuordnungStatus.Veraltet)
                    {
                        string was = r.Status == SchildZuordnungStatus.Offen ? "nicht zugeordnet"
                                   : r.Status == SchildZuordnungStatus.Konflikt ? "Konflikt" : "Festlegung veraltet";
                        P(SchildProblemArt.Warnung, fKu, $"{name}: {was} – {r.Grund}.", u.Zeile, "KursBez");
                    }
                    else if (r.Status == SchildZuordnungStatus.OhneLehrkraft)
                        P(SchildProblemArt.Hinweis, fKu,
                          $"{name} ohne Kursleiter – zugeordnet zu „{_plan.Kurse[r.Plan!].Schluessel}“ ohne Vergleich der Lehrkraft.", u.Zeile);
                }
                foreach (var w in _ohne)
                {
                    var r = res[w];
                    if (r.Status is SchildZuordnungStatus.Offen or SchildZuordnungStatus.Veraltet)
                        P(SchildProblemArt.Warnung, fLe, $"Unterricht ohne Kurs {w.Schild}: nicht zugeordnet – {r.Grund}.");
                }
                var og = _ohne.Where(w => res[w].Status == SchildZuordnungStatus.OhneGegenstueck).ToList();
                if (og.Count > 0)
                    P(SchildProblemArt.Hinweis, fLe,
                      $"{og.Count} Unterricht(e) ohne Kurs ohne Gegenstück im Plan (weder Gruppe noch Klassenunterricht): " +
                      Kurz(og.Select(w => $"{w.Klasse} {w.FachText} ({w.Fachlehrer})").ToList()));

                var pob = _e.PlanOhne.Where(p => p.ArtText == "Oberstufenkurs").Select(p => p.Schluessel).ToList();
                var pos = _e.PlanOhne.Where(p => p.ArtText != "Oberstufenkurs").Select(p => p.Schluessel).ToList();
                if (pob.Count > 0)
                    P(SchildProblemArt.Warnung, "Plan", $"{pob.Count} Oberstufenkurs(e) im Plan „{_plan.Bezeichnung}“ ohne SchILD-Kurs: {Kurz(pob)}");
                if (pos.Count > 0 && _mitTeilnehmern)   // ohne Schülerdaten wäre das jede Gruppe
                    P(SchildProblemArt.Hinweis, "Plan",
                      $"{pos.Count} Sek-I-Gruppe(n) im Plan ohne SchILD-Zuordnung – dafür bleiben die Gruppenlisten: {Kurz(pos)}");

                // Teilnehmer passen zur Plan-Gruppe bzw. Stufe (114)
                foreach (var u in _kurse.Concat(_ohne))
                {
                    var r = res[u];
                    if (r.Plan == null || !Zugeordnet(r.Status)) continue;
                    var p = _plan.Kurse[r.Plan];
                    if (u.Art == SchildZuordnungArt.Oberstufe)
                    {
                        var fremd = u.Teiln.Values.Where(s => !p.Stufen.Contains(s.Jahrgang.Trim().ToUpperInvariant())).ToList();
                        if (fremd.Count > 0)
                            P(SchildProblemArt.Warnung, fKu,
                              $"Kurs „{u.KursBez}“: {fremd.Count} Teilnehmer aus einer anderen Stufe als „{p.Schluessel}“ " +
                              $"({string.Join(", ", fremd.Select(s => s.Jahrgang).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))}).", u.Zeile);
                    }
                    else
                    {
                        var fremd = u.Teiln.Values.Where(s => !p.Klassen.Contains(SchildText.KlasseNorm(s.Klasse))).ToList();
                        if (fremd.Count > 0)
                        {
                            string name = u.Art == SchildZuordnungArt.OhneKurs ? $"Unterricht {u.Schild}" : $"Kurs „{u.KursBez}“";
                            P(SchildProblemArt.Warnung, u.Art == SchildZuordnungArt.OhneKurs ? fLe : fKu,
                              $"{name}: {fremd.Count} Teilnehmer aus Klassen, die im Plan nicht zur Gruppe „{p.Schluessel}“ gehören " +
                              $"({string.Join(", ", fremd.Select(s => SchildText.KlasseNorm(s.Klasse)).Distinct().OrderBy(x => x, StringComparer.Ordinal))}).",
                              u.Zeile > 0 ? u.Zeile : null);
                        }
                    }
                }

                // Zeitkonflikte (114): ein Schüler zur selben Stunde in zwei zugeordneten Kursen/Gruppen.
                // Nachtrag 5.2a-2: beide beteiligten Zeilen werden im Reiter „Zuordnung“ markiert und gelten als offen.
                string Lab(SchildZuordnungZeile x)
                    => x.Art == SchildZuordnungArt.OhneKurs ? $"{x.Klasse} {x.FachText} {x.Fachlehrer}" : $"{x.KursBez} ({x.JahrgangText})";
                string ZeitText((int Tag, int Stunde) zt)
                    => (_plan.TagNamen.TryGetValue(zt.Tag, out var tn) ? tn : zt.Tag.ToString(CultureInfo.InvariantCulture)) + $" {zt.Stunde}.";
                var mitglied = new Dictionary<string, Dictionary<string, SchildZuordnungZeile>>(StringComparer.Ordinal);
                foreach (var u in _kurse.Concat(_ohne))
                {
                    var r = res[u];
                    if (r.Plan == null || !Zugeordnet(r.Status)) continue;
                    foreach (var pk in u.Teiln.Keys)
                    {
                        if (!mitglied.TryGetValue(pk, out var d))
                            mitglied[pk] = d = new Dictionary<string, SchildZuordnungZeile>(StringComparer.Ordinal);
                        d.TryAdd(r.Plan, u);
                    }
                }
                var paare = new Dictionary<(string A, string B), (int N, SchildZuordnungZeile Ua, SchildZuordnungZeile Ub, (int Tag, int Stunde) Z)>();
                var beteiligt = new Dictionary<SchildZuordnungZeile, List<(SchildZuordnungZeile Mit, (int Tag, int Stunde) Z)>>();
                void Merke(SchildZuordnungZeile wer, SchildZuordnungZeile mit, (int Tag, int Stunde) zt)
                {
                    if (!beteiligt.TryGetValue(wer, out var bl)) beteiligt[wer] = bl = new();
                    if (!bl.Any(e => e.Mit == mit)) bl.Add((mit, zt));
                }
                foreach (var d in mitglied.Values)
                {
                    var ks = d.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    for (int i = 0; i < ks.Count; i++)
                        for (int j = i + 1; j < ks.Count; j++)
                        {
                            var z = _plan.Kurse[ks[i]].Zeiten.Intersect(_plan.Kurse[ks[j]].Zeiten).ToList();
                            if (z.Count == 0) continue;
                            var zm = z.Min();
                            var key = (ks[i], ks[j]);
                            paare[key] = paare.TryGetValue(key, out var alt)
                                ? (alt.N + 1, alt.Ua, alt.Ub, alt.Z)
                                : (1, d[ks[i]], d[ks[j]], zm);
                            Merke(d[ks[i]], d[ks[j]], zm);
                            Merke(d[ks[j]], d[ks[i]], zm);
                        }
                }
                _e.Zeitkonflikte = paare.Count;
                foreach (var (wz, wl) in beteiligt)
                {
                    var texte = wl.OrderBy(e => Lab(e.Mit), StringComparer.Ordinal).Select(e => $"„{Lab(e.Mit)}“ ({ZeitText(e.Z)})").ToList();
                    wz.Zeitkonflikt = "Zeitkonflikt mit " + string.Join(", ", texte.Take(3)) + (texte.Count > 3 ? $" (+{texte.Count - 3})" : "");
                    wz.Begruendung = (wz.Begruendung.Length > 0 ? wz.Begruendung + "; " : "") + "⚠ " + wz.Zeitkonflikt;
                }
                int nr = 0;
                foreach (var ((a, b), (n, ua, ub, z)) in paare.OrderBy(kv => kv.Key.A, StringComparer.Ordinal).ThenBy(kv => kv.Key.B, StringComparer.Ordinal))
                {
                    if (++nr > MaxKonfliktMeldungen)
                    {
                        P(SchildProblemArt.Warnung, "Zuordnung", $"… und {paare.Count - MaxKonfliktMeldungen} weitere Kurspaare mit Zeitkonflikt.");
                        break;
                    }
                    P(SchildProblemArt.Warnung, "Zuordnung",
                      $"Zeitkonflikt: „{Lab(ua)}“ → {_plan.Kurse[a].Schluessel} und „{Lab(ub)}“ → {_plan.Kurse[b].Schluessel} liegen beide " +
                      $"{ZeitText(z)} – {n} Schüler wären in beiden. Eine der beiden Zuordnungen stimmt vermutlich nicht.");
                }

                // Kürzel/Fächer ohne Gegenstück, Klassenleitung (Hinweise)
                var unkRest = _unkK.Where(x => !_sugK.ContainsKey(x)).OrderBy(x => x, StringComparer.Ordinal).Select(SK).ToList();
                if (unkRest.Count > 0) P(SchildProblemArt.Hinweis, "Zuordnung", $"Kürzel aus SchILD ohne Gegenstück im Plan: {Kurz(unkRest)}");
                var orphKRest = _orphK.Where(p => !_sugK.Values.Any(v => v.P == p)).OrderBy(x => x, StringComparer.Ordinal)
                                      .Select(_plan.KuerzelText).ToList();
                if (orphKRest.Count > 0) P(SchildProblemArt.Hinweis, "Plan", $"Kürzel im Plan, die in SchILD fehlen: {Kurz(orphKRest)}");
                var unkF = _fVerw.Where(x => !_plan.Faecher.Contains(x) && !_handF.ContainsKey(x) && !_sugF.ContainsKey(x))
                                 .OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (unkF.Count > 0) P(SchildProblemArt.Hinweis, "Zuordnung", $"Fächer aus SchILD ohne Gegenstück im Plan: {Kurz(unkF)}");
                var orphFRest = _orphF.Where(b => !_sugF.Values.Any(d => d.ContainsKey(b))).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (orphFRest.Count > 0)
                    P(SchildProblemArt.Hinweis, "Plan", $"Plan-Fächer ohne SchILD-Fach: {Kurz(orphFRest)} – für den Rückweg der Fehlstunden " +
                                                        "(5.3) braucht jedes Plan-Fach ein SchILD-Fach.");
                var klOhne = _ex.Klassen.Where(k => (k.Jahr == null || k.Jahr == _jahr) && (k.Abschnitt == null || k.Abschnitt == _abschnitt)
                                                    && k.Klassenlehrer.Length > 0 && km(k.Klassenlehrer) == null)
                                        .Select(k => $"{k.InternBez} ({k.Klassenlehrer})").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                if (klOhne.Count > 0)
                    P(SchildProblemArt.Hinweis, Name(SchildDateiArt.Klassen), $"Klassenleitungen ohne Kürzel im Plan: {Kurz(klOhne)}");

                // ---- Zahlen ----
                string Zaehl(IEnumerable<SchildZuordnungZeile> l)
                {
                    var liste = l.ToList();
                    var teile = Enum.GetValues<SchildZuordnungStatus>()
                                    .Select(st => (st, n: liste.Count(u => res[u].Status == st)))
                                    .Where(x => x.n > 0).Select(x => $"{x.n} {Text(x.st)}").ToList();
                    int mitZk = liste.Count(u => u.Zeitkonflikt != null);
                    if (mitZk > 0) teile.Add($"davon {mitZk} mit Zeitkonflikt");
                    return $"{liste.Count}" + (teile.Count > 0 ? ": " + string.Join(" · ", teile) : "");
                }
                Z("Oberstufenkurse", Zaehl(_kurse.Where(u => u.Art == SchildZuordnungArt.Oberstufe)));
                Z("Sek-I-Kurse", Zaehl(_kurse.Where(u => u.Art == SchildZuordnungArt.Sek1Kurs)));
                Z("Sek I ohne Kurs (Klasse + Fach + Fachlehrer)", Zaehl(_ohne));
                Z("Plan-Kurse ohne SchILD", $"Oberstufe {pob.Count} · Sek I {pos.Count}");
                Z("Zeitkonflikte (Kurspaare)", paare.Count.ToString(CultureInfo.InvariantCulture));
                Z("Festlegungen von Hand (Zuordnungsdatei)", _datei.Anzahl.ToString(CultureInfo.InvariantCulture));
                foreach (var z in _e.Kuerzel.Where(z2 => z2.Schild.Length > 0 && z2.StatusText != "gleich"))
                    Z($"Lehrkraft {z.Schild}", $"= {z.Plan} ({z.StatusText})", "Zuordnung – abweichende Kürzel und Fächer");
                foreach (var z in _e.Faecher.Where(z2 => z2.Schild.Length > 0 && (z2.StatusText != "gleich" || z2.PlanListe.Count > 1
                                                                                   || !z2.PlanListe.Contains(z2.Schild))))
                    Z($"Fach {z.Schild}", $"= {z.Plan} ({z.StatusText})", "Zuordnung – abweichende Kürzel und Fächer");
            }
        }
    }
}
