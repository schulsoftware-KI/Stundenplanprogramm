#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  UntisGenerator – U-1b, „Demo-Datei erzeugen…“ (claude/Untis_Vorschlag.md §8):
    //  schreibt aus der gewählten Plan-Spalte eine GPU014.TXT für eine Woche im
    //  echten Format (Semikolon, Text in "…", UTF-8 ohne BOM, CRLF, 22 Felder)
    //  mit allen Fällen aus §4/§12: Vertretung (auch mit Raum-/Fachwechsel),
    //  offene Vertretung, EVA (Plus), Entfall, Betreuung, Lehrertausch,
    //  Teil-Vertretung, Freisetzung, Raumvertretung, Verlegung als Zeilenpaar,
    //  geteilte Stunde, Sondereinsatz mit/ohne U-Nr, Klausuraufsicht „Klau“,
    //  Pausenaufsicht, Klausur über mehrere Stunden mit wechselnder Aufsicht,
    //  Zeile ohne Gegenstück, unbekanntes Kürzel, doppelte Zeile, Tag ohne
    //  Zeilen, Samstag, Fehlerzeilen – und in Feld 16/17 die Testwerte
    //  „GEHEIM-ABSENZGRUND“/„GEHEIM-TEXT“, die nirgends auftauchen dürfen.
    //
    //  Der Zufall ist ein eigener kleiner Generator (gleicher Startwert + gleicher
    //  Plan = gleiche Datei, unabhängig von der .NET-Version). Daneben legt der
    //  Generator UNTIS_DEMO.json mit der SHA-256-Prüfsumme (UntisDemo).
    // =====================================================================

    public sealed class UntisDemoOptionen
    {
        public string Zielordner { get; set; } = "";
        public DateTime Montag { get; set; }
        public int Startwert { get; set; } = 42;
    }

    public sealed class UntisDemoErgebnis
    {
        public string Pfad { get; set; } = "";
        public int Zeilen { get; set; }
        public List<string> Faelle { get; } = new();
    }

    public static class UntisGenerator
    {
        public const string DateiName = "GPU014.TXT";
        internal const string TestGrund = "GEHEIM-ABSENZGRUND";
        internal const string TestText = "GEHEIM-TEXT";

        public static UntisDemoErgebnis Erzeuge(PlanDaten plan, SchuljahrKalender kal, UntisDemoOptionen opt)
        {
            Directory.CreateDirectory(opt.Zielordner);
            var raum = RaumQuelle(plan);
            var lauf = new Lauf(plan, kal, opt.Montag.Date, opt.Startwert, raum);
            lauf.Erzeuge();

            string text = Text(lauf.ZeilenListe);
            string pfad = Path.Combine(opt.Zielordner, DateiName);
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            File.WriteAllBytes(pfad, bytes);
            UntisDemo.Schreiben(opt.Zielordner, bytes, plan.GewaehltePlanSpalte, opt.Montag, opt.Startwert, lauf.Faelle);

            var erg = new UntisDemoErgebnis { Pfad = pfad, Zeilen = lauf.ZeilenListe.Count };
            erg.Faelle.AddRange(lauf.Faelle);
            return erg;
        }

        // Felder in Anführungszeichen (0-basiert): 2, 6–17, 19, 20 – Zahlenfelder ohne.
        private static readonly HashSet<int> TextFelder = new() { 1, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 19 };

        internal static string Text(List<string[]> zeilen)
        {
            var sb = new StringBuilder();
            foreach (var f in zeilen)
            {
                for (int i = 0; i < f.Length; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(TextFelder.Contains(i) ? "\"" + f[i].Replace("\"", "\"\"") + "\"" : f[i]);
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Raum je Grundplan-Stunde: Blatt „Raumplan“ der Plan-Spalte, sonst Fachraum vor Stammraum (wie die App).</summary>
        private static Func<string, int, int, TeilInfo, string> RaumQuelle(PlanDaten plan)
        {
            var mitFach = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ohneFach = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            static string N(string s) => UntisLeser.Norm(s).ToUpperInvariant();
            try
            {
                var zeilen = Stundenplan_V2.Raumplanung.RaumplanStore.LiesRaumplanFuer(plan.PlanPfad, plan.GewaehltePlanSpalte);
                foreach (var z in zeilen ?? new List<Stundenplan_V2.Raumplanung.RaumZeile>())
                {
                    string r = (z.Raum ?? "").Trim();
                    if (r.Length == 0) continue;
                    void Add(Dictionary<string, List<string>> d, string k)
                    {
                        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<string>();
                        if (!l.Contains(r, StringComparer.OrdinalIgnoreCase)) l.Add(r);
                    }
                    Add(mitFach, $"{N(z.WTag)}|{z.Stunde}|{z.UNr}|{N(z.Lehrer)}|{N(z.Fach)}");
                    Add(ohneFach, $"{N(z.WTag)}|{z.Stunde}|{z.UNr}|{N(z.Lehrer)}");
                }
            }
            catch { /* ohne Raumplan: Fachraum bzw. Stammraum */ }

            return (wtag, stunde, unr, t) =>
            {
                string k = $"{N(wtag)}|{stunde}|{unr}|{N(t.Lehrer)}";
                if (mitFach.TryGetValue(k + "|" + N(t.Fach), out var l) || ohneFach.TryGetValue(k, out l))
                    return string.Join("~", l);
                return t.Fachraum ?? t.Stammraum ?? "";
            };
        }

        // =================================================================

        /// <summary>Kleiner Zufallsgenerator (64-Bit-LCG) – gleiche Folge in jeder .NET-Version.</summary>
        private sealed class Zufall
        {
            private ulong _s;
            public Zufall(int seed) { _s = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x2545F4914F6CDD1DUL); }
            public int Next(int n)
            {
                _s = unchecked(_s * 6364136223846793005UL + 1442695040888963407UL);
                return (int)((_s >> 33) % (ulong)n);
            }
        }

        private sealed class Std
        {
            public int Tag;
            public string WTag = "";
            public int Stunde;
            public int UNr;
            public TeilInfo Teil = new();
            public string Lehrer => Teil.Lehrer;
        }

        private sealed class Lauf
        {
            private static readonly Regex KM = new(@"^\d{2}[A-Za-z]{1,2}$", RegexOptions.Compiled);
            private static readonly string[] Kurz = { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" };

            private readonly PlanDaten _p;
            private readonly SchuljahrKalender _kal;
            private readonly DateTime _montag;
            private readonly Zufall _z;
            private readonly Func<string, int, int, TeilInfo, string> _raumFuer;

            private readonly Dictionary<int, string> _wtagFuer = new();
            private readonly List<string> _alleRaeume;
            private readonly Dictionary<string, HashSet<string>> _faecherVon = new(StringComparer.Ordinal);
            private readonly List<DateTime> _tage = new();
            private readonly Dictionary<int, List<Std>> _stunden = new();
            private readonly Dictionary<int, int> _maxSt = new();

            public readonly List<string[]> ZeilenListe = new();
            public readonly List<string> Faelle = new();
            private int _vnr = 1000;
            private readonly Dictionary<string, int> _absnr = new(StringComparer.Ordinal);
            private readonly HashSet<(int, int, int, string)> _benutzt = new();
            private readonly Dictionary<(int, int), HashSet<string>> _belegt = new();

            public Lauf(PlanDaten p, SchuljahrKalender kal, DateTime montag, int seed, Func<string, int, int, TeilInfo, string> raumFuer)
            {
                _p = p; _kal = kal; _montag = montag; _z = new Zufall(seed); _raumFuer = raumFuer;

                foreach (var w in p.RasterTage)
                {
                    int i = SchuljahrKalender.WochentagIndex(w);
                    if (i >= 0) _wtagFuer.TryAdd(i, w);
                }
                var raeume = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ts in p.Unterrichte.Values)
                    foreach (var t in ts)
                        foreach (var x in new[] { t.Fachraum, t.Stammraum })
                            if (!string.IsNullOrEmpty(x))
                                foreach (var r in Regex.Split(x, "[~,]"))
                                    if (r.Trim().Length > 0) raeume.Add(r.Trim());
                _alleRaeume = raeume.OrderBy(Up, StringComparer.Ordinal).ThenBy(r => r, StringComparer.Ordinal).ToList();

                foreach (var u in p.Unterrichte.Keys.OrderBy(x => x))
                    foreach (var t in p.Unterrichte[u])
                        if (IstUnterricht(t))
                        {
                            if (!_faecherVon.TryGetValue(Up(t.Lehrer), out var set)) _faecherVon[Up(t.Lehrer)] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(Fn(t.Fach));
                        }

                for (int i = 0; i < 5; i++) _tage.Add(montag.AddDays(i));
                for (int i = 0; i < 5; i++)
                {
                    var lst = new List<Std>();
                    if (_wtagFuer.TryGetValue((int)_tage[i].DayOfWeek, out var w))
                    {
                        foreach (var kv in p.Belegung)
                        {
                            if (kv.Key.WTag != w) continue;
                            _maxSt[i] = Math.Max(_maxSt.TryGetValue(i, out int m) ? m : 0, kv.Key.Stunde);
                            foreach (int unr in kv.Value)
                                if (p.Unterrichte.TryGetValue(unr, out var teile))
                                    foreach (var t in teile)
                                        if (IstUnterricht(t)) lst.Add(new Std { Tag = i, WTag = w, Stunde = kv.Key.Stunde, UNr = unr, Teil = t });
                        }
                    }
                    _stunden[i] = lst.OrderBy(s => s.Stunde).ThenBy(s => s.UNr)
                                     .ThenBy(s => Up(s.Lehrer), StringComparer.Ordinal)
                                     .ThenBy(s => Up(Fn(s.Teil.Fach)), StringComparer.Ordinal).ToList();
                }
            }

            // ---------- Hilfen ----------

            private static string Up(string? s) => (s ?? "").ToUpperInvariant();
            private static string Fn(string? f) => UntisLeser.Norm(f);

            private static bool IstUnterricht(TeilInfo t)
            {
                if (t.IstVber || t.IstUv || t.IstPseudo) return false;
                string f = Fn(t.Fach).ToLowerInvariant();
                if (f == "ufrei" || f.Length == 0) return false;
                return t.Klassen.Any(k => KM.IsMatch(k) || Up(k) is "EF" or "Q1" or "Q2");
            }

            private string Raum(Std s) => _raumFuer(s.WTag, s.Stunde, s.UNr, s.Teil);

            private string AndererRaum(string alt)
            {
                if (_alleRaeume.Count == 0) return "R999";
                int i = _z.Next(_alleRaeume.Count);
                var altSet = new HashSet<string>(Regex.Split(alt ?? "", "[~,]").Select(Up), StringComparer.Ordinal);
                for (int k = 0; k < _alleRaeume.Count; k++)
                {
                    string r = _alleRaeume[(i + k) % _alleRaeume.Count];
                    if (Up(r) != Up(alt) && !altSet.Contains(Up(r))) return r;
                }
                return "R999";
            }

            private string Frei(int tag, int st, IEnumerable<string?> aus)
            {
                string w = _wtagFuer[(int)_tage[tag].DayOfWeek];
                var echt = new HashSet<string>((_p.LehrerProSlot.TryGetValue((w, st), out var e) ? e : new HashSet<string>()).Select(Up), StringComparer.Ordinal);
                var weg = new HashSet<string>(aus.Select(Up), StringComparer.Ordinal);
                if (_belegt.TryGetValue((tag, st), out var b)) foreach (var x in b) weg.Add(Up(x));
                bool Ok(string l) => !echt.Contains(Up(l)) && !weg.Contains(Up(l));
                List<string> Sortiert(IEnumerable<string> q)
                    => q.Distinct(StringComparer.Ordinal).OrderBy(Up, StringComparer.Ordinal).ThenBy(x => x, StringComparer.Ordinal).ToList();

                var kand = Sortiert((_p.VberProSlot.TryGetValue((w, st), out var v) ? v : new HashSet<string>()).Where(Ok));
                var heute = new HashSet<string>(_stunden[tag].Select(s => Up(s.Lehrer)), StringComparer.Ordinal);
                if (kand.Count == 0) kand = Sortiert(_p.Vertretungsbereit.Where(l => heute.Contains(Up(l)) && Ok(l)));
                if (kand.Count == 0) kand = Sortiert(_p.AlleLehrer.Where(l => heute.Contains(Up(l)) && Ok(l)));
                if (kand.Count == 0) return "L1";
                string wahl = kand[_z.Next(kand.Count)];
                if (!_belegt.TryGetValue((tag, st), out var bl)) _belegt[(tag, st)] = bl = new HashSet<string>(StringComparer.Ordinal);
                bl.Add(wahl);
                return wahl;
            }

            private string Zeit(DateTime d)
            {
                int m; DateTime dd;
                if (_z.Next(3) == 0) { m = 7 * 60 + _z.Next(60); dd = d; }
                else { m = 13 * 60 + _z.Next(300); dd = d.AddDays(-1); }
                return dd.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + (m / 60).ToString("00", CultureInfo.InvariantCulture) +
                       (m % 60).ToString("00", CultureInfo.InvariantCulture);
            }

            private string[] Zeile(DateTime d, int st, int unr, string abs, string vert, string fach, string vfach, string raum,
                                   string vraum, List<string> klassen, long bits, List<string> vklassen, string b,
                                   int? vnrFest = null, bool grund = true)
            {
                int v;
                if (vnrFest == null) { _vnr++; v = _vnr; } else v = vnrFest.Value;
                int an = 0;
                if (abs.Length > 0 && !_absnr.TryGetValue(Up(abs), out an)) { an = 500 + _absnr.Count + 1; _absnr[Up(abs)] = an; }
                if (b != "E" && _z.Next(5) == 0) bits |= 1L << 21;
                string zeit = Zeit(d);
                var f = new[]
                {
                    v.ToString(CultureInfo.InvariantCulture), d.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    st.ToString(CultureInfo.InvariantCulture), (abs.Length > 0 ? an : 0).ToString(CultureInfo.InvariantCulture),
                    unr.ToString(CultureInfo.InvariantCulture), abs, vert, fach, "", vfach, "", raum, vraum, "",
                    string.Join("~", klassen), abs.Length > 0 && grund ? TestGrund : "", v % 4 == 0 ? TestText : "",
                    bits.ToString(CultureInfo.InvariantCulture), string.Join("~", vklassen), b, zeit, ""
                };
                ZeilenListe.Add(f);
                return f;
            }

            private static List<string> Kl(Std s) => new(s.Teil.Klassen);
            private static string Wt(DateTime d) => Kurz[((int)d.DayOfWeek + 6) % 7];
            private static string Dm(DateTime d) => d.ToString("dd.MM.", CultureInfo.InvariantCulture);
            private string Beschr(Std s) => $"{Wt(_tage[s.Tag])} {Dm(_tage[s.Tag])}, {s.Stunde}. Std., {string.Join(", ", Kl(s))} {Fn(s.Teil.Fach)}";
            private bool Benutzt(Std s) => _benutzt.Contains((s.Tag, s.Stunde, s.UNr, Up(s.Lehrer)));
            private void Merke(Std s) => _benutzt.Add((s.Tag, s.Stunde, s.UNr, Up(s.Lehrer)));

            private (string? Lehrer, List<Std> Stunden) LehrerTag(int tag, int minimum, IEnumerable<string?> aus)
            {
                var zaehl = new Dictionary<string, List<Std>>(StringComparer.Ordinal);
                var reihe = new List<string>();
                foreach (var s in _stunden[tag])
                {
                    if (!zaehl.TryGetValue(Up(s.Lehrer), out var l)) { zaehl[Up(s.Lehrer)] = l = new List<Std>(); reihe.Add(Up(s.Lehrer)); }
                    l.Add(s);
                }
                var ausU = new HashSet<string>(aus.Select(Up), StringComparer.Ordinal);
                var kand = reihe.Where(l => zaehl[l].Count >= minimum && !ausU.Contains(l)
                                            && zaehl[l].All(x => x.Teil.Klassen.All(k => KM.IsMatch(k))))
                                .OrderBy(l => l, StringComparer.Ordinal).ToList();
                if (kand.Count == 0)
                    kand = reihe.Where(l => zaehl[l].Count >= minimum && !ausU.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).ToList();
                if (kand.Count == 0) return (null, new List<Std>());
                string wahl = kand[_z.Next(kand.Count)];
                return (zaehl[wahl][0].Lehrer, zaehl[wahl].Where(s => !Benutzt(s)).ToList());
            }

            private string FachVon(string l, string nicht)
            {
                if (_faecherVon.TryGetValue(Up(l), out var set))
                    foreach (var f in set.OrderBy(Up, StringComparer.Ordinal).ThenBy(x => x, StringComparer.Ordinal))
                        if (Up(f) != Up(Fn(nicht))) return f;
                return "M";
            }

            private int? ErsteFreieStunde(int tag, HashSet<int> besetzt)
            {
                int max = _maxSt.TryGetValue(tag, out int m) ? m : 6;
                for (int h = 1; h <= max; h++) if (!besetzt.Contains(h)) return h;
                return null;
            }

            // ---------- Woche ----------

            public void Erzeuge()
            {
                Montag();
                Dienstag();
                Mittwoch();
                Donnerstag();
                Faelle.Add($"{Wt(_tage[4])} {Dm(_tage[4])}: keine Zeilen – der Tag wird beim Veröffentlichen leer (alte Vertretungen verschwinden)");
                DateTime sa = _montag.AddDays(5);
                if (_stunden[4].Count > 0)
                {
                    var s = _stunden[4][0];
                    Zeile(sa, s.Stunde, s.UNr, s.Lehrer, "L1", s.Teil.Fach, s.Teil.Fach, Raum(s), "", Kl(s), 0, Kl(s), "");
                    Faelle.Add($"Sa {Dm(sa)}: Zeile an einem Samstag – kein Schultag (Hinweis, nicht gesendet)");
                }
            }

            private void Montag()
            {
                const int T = 0;
                DateTime d = _tage[T];
                string[]? erste = null;
                if (!_kal.IstSchultag(d) || _stunden[T].Count == 0) return;

                var (a1, ls) = LehrerTag(T, 5, Array.Empty<string?>());
                for (int k = 0; k < ls.Count; k++)
                {
                    var s = ls[k]; Merke(s); string f = s.Teil.Fach; string r = Raum(s); string a = a1!;
                    switch (k)
                    {
                        case 0:
                        {
                            string v = Frei(T, s.Stunde, new[] { a1 });
                            erste = Zeile(d, s.Stunde, s.UNr, a, v, f, f, r, "", Kl(s), 0, Kl(s), "");
                            Faelle.Add($"{Beschr(s)}: Vertretung {a} → {v}");
                            break;
                        }
                        case 1:
                        {
                            string v = Frei(T, s.Stunde, new[] { a1 });
                            string nr = AndererRaum(r);
                            Zeile(d, s.Stunde, s.UNr, a, v, f, f, r, nr, Kl(s), 0, Kl(s), "");
                            Faelle.Add($"{Beschr(s)}: Vertretung {a} → {v} mit Raumwechsel {(r.Length > 0 ? r : "–")} → {nr}");
                            break;
                        }
                        case 2:
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, r, Kl(s), 0, Kl(s), "");
                            Faelle.Add($"{Beschr(s)}: offene Vertretung {a} → ???");
                            break;
                        case 3:
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, "", Kl(s), 1L << 5, Kl(s), "");
                            Faelle.Add($"{Beschr(s)}: EVA (Plus), {a} gestrichen");
                            break;
                        case 4:
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, "", Kl(s), 1, new List<string>(), "C");
                            Faelle.Add($"{Beschr(s)}: Entfall (C)");
                            break;
                        case 5:
                        {
                            string v = Frei(T, s.Stunde, new[] { a1 });
                            Zeile(d, s.Stunde, s.UNr, a, v, f, f, r, "", Kl(s), 1L << 1, Kl(s), "S");
                            Faelle.Add($"{Beschr(s)}: Betreuung (S) {a} → {v}");
                            break;
                        }
                        default:
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, "", Kl(s), 1, new List<string>(), "");
                            Faelle.Add($"{Beschr(s)}: Entfall (nur Bit)");
                            break;
                    }
                }

                var (a2, ls2) = LehrerTag(T, 4, new[] { a1 });
                for (int k = 0; k < ls2.Count; k++)
                {
                    var s = ls2[k]; Merke(s); string f = s.Teil.Fach; string r = Raum(s); string a = a2!;
                    switch (k)
                    {
                        case 0:
                        {
                            string v = Frei(T, s.Stunde, new[] { a1, a2 });
                            string vf = FachVon(v, f);
                            Zeile(d, s.Stunde, s.UNr, a, v, f, vf, r, "", Kl(s), 0, Kl(s), "");
                            Faelle.Add($"{Beschr(s)}: Vertretung {a} → {v} mit Fachwechsel {Fn(f)} → {vf}");
                            break;
                        }
                        case 1:
                        {
                            string v = Frei(T, s.Stunde, new[] { a1, a2 });
                            Zeile(d, s.Stunde, s.UNr, a, v, f, f, r, r, Kl(s), 0, Kl(s), "~");
                            Faelle.Add($"{Beschr(s)}: Lehrertausch (~) {a} → {v}");
                            break;
                        }
                        case 2:
                        {
                            string v = Frei(T, s.Stunde, new[] { a1, a2 });
                            Zeile(d, s.Stunde, s.UNr, a, v, f, f, r, "", Kl(s), 1L << 6, Kl(s), "P");
                            Faelle.Add($"{Beschr(s)}: Teil-Vertretung (P) {a} → {v}");
                            break;
                        }
                        case 3:
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, "", Kl(s), 1L << 4, new List<string>(), "L");
                            Faelle.Add($"{Beschr(s)}: Freisetzung (L) – entfällt");
                            break;
                        default:
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, "", Kl(s), 1, new List<string>(), "C");
                            Faelle.Add($"{Beschr(s)}: Entfall (C)");
                            break;
                    }
                }

                var rest = _stunden[T].Where(s => Up(s.Lehrer) != Up(a1) && Up(s.Lehrer) != Up(a2) && !Benutzt(s)).ToList();
                if (rest.Count > 0)
                {
                    var s = rest[_z.Next(rest.Count)]; Merke(s); string r = Raum(s);
                    string nr = AndererRaum(r);
                    Zeile(d, s.Stunde, s.UNr, s.Lehrer, s.Lehrer, s.Teil.Fach, "", r, nr, Kl(s), 1L << 16, Kl(s), "R");
                    Faelle.Add($"{Beschr(s)}: Raumvertretung (R) {(r.Length > 0 ? r : "–")} → {nr}");
                }
                if (erste != null)
                {
                    ZeilenListe.Add((string[])erste.Clone());
                    Faelle.Add("Doppelte Zeile (Kopie der ersten Vertretung) – Hinweis, nur einmal gesendet");
                    string v = Frei(T, 3, new[] { a1 });
                    Zeile(d, 3, 0, a1!, v, "", "", "", "", new List<string>(), 1L << 17, new List<string>(), "B");
                    Faelle.Add($"{Wt(d)} {Dm(d)}, 3. Std.: Pausenaufsicht (B) – nicht übernommen");
                    var f15 = (string[])erste.Clone();
                    f15[2] = "15"; _vnr++; f15[0] = _vnr.ToString(CultureInfo.InvariantCulture);
                    ZeilenListe.Add(f15);
                    Faelle.Add("Stunde 15 – Fehler (gibt es im Zeitraster nicht)");
                    var kurz = erste.Take(21).ToArray();
                    _vnr++; kurz[0] = _vnr.ToString(CultureInfo.InvariantCulture);
                    ZeilenListe.Add(kurz);
                    Faelle.Add("Zeile mit 21 statt 22 Feldern – Fehler");
                }
            }

            private void Dienstag()
            {
                const int T = 1;
                DateTime d = _tage[T];
                if (!_kal.IstSchultag(d) || _stunden[T].Count == 0) return;
                string w1 = _stunden[T][0].WTag;
                bool FreiL(int st, string l)
                    => !(_p.LehrerProSlot.TryGetValue((w1, st), out var e) && e.Any(x => Up(x) == Up(l)));

                // Verlegung als Zeilenpaar
                var nur = _stunden[T].Where(s => s.Teil.Klassen.Count == 1 && KM.IsMatch(s.Teil.Klassen[0])
                                                && _p.Unterrichte.TryGetValue(s.UNr, out var tl) && tl.Count == 1).ToList();
                var klassen = nur.Select(s => Up(s.Teil.Klassen[0])).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
                (Std X, Std Y)? paar = null;
                foreach (var kName in klassen)
                {
                    var ks = nur.Where(s => Up(s.Teil.Klassen[0]) == kName).ToList();
                    foreach (var x in ks)
                    {
                        foreach (var y in ks)
                            if (y.Stunde > x.Stunde && Up(y.Lehrer) != Up(x.Lehrer) && FreiL(y.Stunde, x.Lehrer)) { paar = (x, y); break; }
                        if (paar != null) break;
                    }
                    if (paar != null) break;
                }
                if (paar != null)
                {
                    var (x, y) = paar.Value; Merke(x); Merke(y);
                    string rx = Raum(x), ry = Raum(y);
                    Zeile(d, x.Stunde, x.UNr, x.Lehrer, "", x.Teil.Fach, "", rx, "", Kl(x), 1 | (1L << 3), new List<string>(), "C");
                    Zeile(d, y.Stunde, y.UNr, y.Lehrer, x.Lehrer, y.Teil.Fach, x.Teil.Fach, ry, Up(rx) != Up(ry) ? rx : "",
                          Kl(y), 1L << 7, Kl(x), "F");
                    Faelle.Add($"{Beschr(x)}: verlegt (alte Stelle, C + Bit 3)");
                    Faelle.Add($"{Beschr(y)}: hierher verlegt (F): {Fn(x.Teil.Fach)} {x.Lehrer} statt {Fn(y.Teil.Fach)} {y.Lehrer}");
                    if (!_belegt.TryGetValue((T, y.Stunde), out var bl)) _belegt[(T, y.Stunde)] = bl = new HashSet<string>(StringComparer.Ordinal);
                    bl.Add(x.Lehrer);
                }

                // Geteilte Stunde (Entscheidung 133)
                var g = _stunden[T].Where(s => s.Teil.Klassen.Count >= 2 && s.Teil.Klassen.Count <= 4
                                              && s.Teil.Klassen.All(k => KM.IsMatch(k)) && !Benutzt(s)).ToList();
                if (g.Count > 0)
                {
                    var s = g[_z.Next(g.Count)]; Merke(s); string r = Raum(s); var kk = Kl(s);
                    string v1 = Frei(T, s.Stunde, new[] { s.Lehrer });
                    string v2 = Frei(T, s.Stunde, new[] { s.Lehrer, v1 });
                    Zeile(d, s.Stunde, s.UNr, s.Lehrer, v1, s.Teil.Fach, s.Teil.Fach, r, "", kk, 0, new List<string> { kk[^1] }, "");
                    Zeile(d, s.Stunde, s.UNr, s.Lehrer, v2, s.Teil.Fach, s.Teil.Fach, r, "", kk, 0, kk.Take(kk.Count - 1).ToList(), "");
                    Faelle.Add($"{Beschr(s)}: geteilte Stunde – {kk[^1]} an {v1}, {string.Join(", ", kk.Take(kk.Count - 1))} an {v2}");
                }

                // Sondereinsatz mit U-Nr (andere Lehrkraft, anderes Fach)
                var rest = _stunden[T].Where(s => !Benutzt(s) && KM.IsMatch(s.Teil.Klassen[0])).ToList();
                if (rest.Count > 0)
                {
                    var s = rest[_z.Next(rest.Count)]; Merke(s); string r = Raum(s);
                    string v = Frei(T, s.Stunde, new[] { s.Lehrer });
                    string vf = FachVon(v, s.Teil.Fach);
                    Zeile(d, s.Stunde, s.UNr, s.Lehrer, v, s.Teil.Fach, vf, r, "", Kl(s), 1L << 2, Kl(s), "A");
                    Faelle.Add($"{Beschr(s)}: Sondereinsatz (A) mit U-Nr – {s.Lehrer} → {v}, Fach {Fn(s.Teil.Fach)} → {vf}");
                }

                // Sondereinsatz ohne U-Nr: zusätzliche Stunde einer Klasse
                var alleKl = _stunden[T].SelectMany(s => s.Teil.Klassen).Where(k => KM.IsMatch(k)).Select(Up)
                                        .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
                if (alleKl.Count > 0)
                {
                    string kName = alleKl[_z.Next(alleKl.Count)];
                    var besetztK = _stunden[T].Where(s => s.Teil.Klassen.Any(k => Up(k) == kName)).Select(s => s.Stunde).ToHashSet();
                    int? st = ErsteFreieStunde(T, besetztK);
                    if (st != null)
                    {
                        string v = Frei(T, st.Value, Array.Empty<string?>());
                        string vf = FachVon(v, "");
                        string nr = AndererRaum("");
                        Zeile(d, st.Value, 0, "", v, "", vf, "", nr, new List<string> { kName }, 1L << 2, new List<string> { kName }, "A");
                        Faelle.Add($"{Wt(d)} {Dm(d)}, {st}. Std., {kName}: Sondereinsatz (A) ohne U-Nr – zusätzliche Stunde {vf} bei {v}");
                    }
                }

                // Klausuraufsicht ohne Klasse („Klau“) – nicht übernommen
                {
                    string v = Frei(T, 2, Array.Empty<string?>());
                    Zeile(d, 2, 0, "", v, "", "", "", "", new List<string> { "Klau" }, 1L << 2, new List<string>(), "A");
                    Faelle.Add($"{Wt(d)} {Dm(d)}, 2. Std.: Sondereinsatz „Klau“ (Klausuraufsicht) – nicht übernommen");
                }

                // unbekanntes Kürzel
                rest = _stunden[T].Where(s => !Benutzt(s)).ToList();
                if (rest.Count > 0)
                {
                    var s = rest[_z.Next(rest.Count)]; Merke(s);
                    Zeile(d, s.Stunde, s.UNr, s.Lehrer, "XY9", s.Teil.Fach, s.Teil.Fach, Raum(s), "", Kl(s), 0, Kl(s), "");
                    Faelle.Add($"{Beschr(s)}: Vertretung {s.Lehrer} → XY9 (Kürzel nicht im Plan – Warnung)");
                }

                // ohne Gegenstück: U-Nr in einer Stunde, in der sie nicht liegt
                rest = _stunden[T].Where(s => !Benutzt(s) && KM.IsMatch(s.Teil.Klassen[0])).ToList();
                if (rest.Count > 0)
                {
                    var s = rest[_z.Next(rest.Count)];
                    var besetzt = _stunden[T].Where(x => Up(x.Lehrer) == Up(s.Lehrer)).Select(x => x.Stunde).ToHashSet();
                    int? st = ErsteFreieStunde(T, besetzt);
                    if (st != null)
                    {
                        string v = Frei(T, st.Value, new[] { s.Lehrer });
                        Zeile(d, st.Value, s.UNr, s.Lehrer, v, s.Teil.Fach, s.Teil.Fach, Raum(s), "", Kl(s), 0, Kl(s), "");
                        Faelle.Add($"{Wt(d)} {Dm(d)}, {st}. Std., {string.Join(", ", Kl(s))} {Fn(s.Teil.Fach)}: Vertretung {s.Lehrer} → {v} – " +
                                   $"U-Nr {s.UNr} liegt nicht in dieser Stunde (ohne Gegenstück, Warnung)");
                    }
                }
            }

            private void Mittwoch()
            {
                const int T = 2;
                DateTime d = _tage[T];
                if (!_kal.IstSchultag(d) || _stunden[T].Count == 0) return;
                List<Std> Kurse(string stufe) => _stunden[T].Where(s => s.Teil.Klassen.Count == 1 && Up(s.Teil.Klassen[0]) == stufe
                                                                       && Fn(s.Teil.Fach).Split(' ').Length == 2).ToList();
                Std? k1 = null;
                foreach (var stufe in new[] { "Q1", "Q2", "EF" })
                {
                    var ks = Kurse(stufe);
                    if (ks.Count > 0) { k1 = ks[_z.Next(ks.Count)]; break; }
                }
                if (k1 == null) return;

                int mx = _maxSt.TryGetValue(T, out int m) ? m : 6;
                int h0 = k1.Stunde + 2 <= mx ? k1.Stunde : Math.Max(1, k1.Stunde - 2);
                int[] hs = { h0, h0 + 1, h0 + 2 };
                string kr = AndererRaum("");
                var auf = new string?[3];
                auf[0] = Frei(T, hs[0], new[] { k1.Lehrer });
                auf[1] = Frei(T, hs[1], new[] { k1.Lehrer, auf[0] });
                string stufe1 = Up(k1.Teil.Klassen[0]);
                for (int i = 0; i < hs.Length; i++)
                    Zeile(d, hs[i], 0, "", auf[i] ?? "", "", k1.Teil.Fach, "", kr, new List<string>(), 1L << 22,
                          new List<string> { stufe1 }, "E", vnrFest: 0, grund: false);
                Faelle.Add($"{Wt(d)} {Dm(d)}, {hs[0]}.–{hs[2]}. Std., {stufe1}: Klausur {Fn(k1.Teil.Fach)} (Kurs {k1.Lehrer}, " +
                           $"{k1.Stunde}. Std. im Plan) in {kr}, Aufsicht {auf[0]}, {auf[1]}, in der {hs[2]}. Std. keine");

                Std? k2 = null;
                foreach (var stufe in new[] { "EF", "Q2", "Q1" })
                {
                    if (stufe == stufe1) continue;
                    var ks = Kurse(stufe);
                    if (ks.Count > 0) { k2 = ks[_z.Next(ks.Count)]; break; }
                }
                if (k2 != null)
                {
                    string stufe2 = Up(k2.Teil.Klassen[0]);
                    foreach (int h in hs.Skip(1))
                    {
                        string a = Frei(T, h, new[] { k2.Lehrer });
                        Zeile(d, h, 0, "", a, "", k2.Teil.Fach, "", kr, new List<string>(), 1L << 22,
                              new List<string> { stufe2 }, "E", vnrFest: 0, grund: false);
                    }
                    Faelle.Add($"{Wt(d)} {Dm(d)}, {hs[1]}.–{hs[2]}. Std., {stufe2}: Klausur {Fn(k2.Teil.Fach)} im selben Raum {kr}");
                }
                Zeile(d, hs[0], 0, "", "", "", "XX  G9", "", kr, new List<string>(), 1L << 22,
                      new List<string> { stufe1 }, "E", vnrFest: 0, grund: false);
                Faelle.Add($"{Wt(d)} {Dm(d)}, {hs[0]}. Std., {stufe1}: Klausur „XX G9“ – Kurs nicht im Plan (Hinweis, zusätzliche Stunde)");
            }

            private void Donnerstag()
            {
                const int T = 3;
                DateTime d = _tage[T];
                if (!_kal.IstSchultag(d) || _stunden[T].Count == 0) return;
                var aus = new List<string?>();
                for (int n = 0; n < 3; n++)
                {
                    var (a, ls) = LehrerTag(T, 3, aus);
                    if (a == null) break;
                    aus.Add(a);
                    var typen = new List<(string Name, int Anzahl)>();
                    void Zaehle(string t)
                    {
                        int i = typen.FindIndex(x => x.Name == t);
                        if (i < 0) typen.Add((t, 1)); else typen[i] = (t, typen[i].Anzahl + 1);
                    }
                    foreach (var s in ls)
                    {
                        Merke(s); string r = Raum(s); string f = s.Teil.Fach;
                        int w = _z.Next(20);
                        string nr = _z.Next(4) == 0 ? AndererRaum(r) : "";
                        if (w < 8)
                        {
                            string v = Frei(T, s.Stunde, aus);
                            Zeile(d, s.Stunde, s.UNr, a, v, f, f, r, nr, Kl(s), 0, Kl(s), ""); Zaehle("Vertretung");
                        }
                        else if (w < 11)
                        {
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, nr, Kl(s), 0, Kl(s), ""); Zaehle("offen");
                        }
                        else if (w < 15)
                        {
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, "", Kl(s), 1, new List<string>(), "C"); Zaehle("Entfall");
                        }
                        else if (w < 18)
                        {
                            Zeile(d, s.Stunde, s.UNr, a, "", f, "", r, nr, Kl(s), 1L << 5, Kl(s), ""); Zaehle("EVA");
                        }
                        else
                        {
                            string v = Frei(T, s.Stunde, aus);
                            Zeile(d, s.Stunde, s.UNr, a, v, f, f, r, nr, Kl(s), 1L << 1, Kl(s), "S"); Zaehle("Betreuung");
                        }
                    }
                    Faelle.Add($"{Wt(d)} {Dm(d)}: {a} abwesend – " + string.Join(", ", typen.Select(t => $"{t.Name} {t.Anzahl}")));
                }
            }
        }
    }

    // =====================================================================
    //  UntisDemo – Kennzeichnung der Demo-Datei (wie PSEUDODATEN.json bei SchILD):
    //  UNTIS_DEMO.json im selben Ordner mit der SHA-256-Prüfsumme. Nur wenn sie
    //  zur Datei passt, gilt die Datei als Demo; sonst als echte Datei.
    // =====================================================================

    public static class UntisDemo
    {
        public const string MarkerName = "UNTIS_DEMO.json";

        public sealed class Marker
        {
            public string Art { get; set; } = "Demo-Datei";
            public string Hinweis { get; set; } =
                "Erzeugt vom Stundenplanprogramm („Demo-Datei erzeugen…“). Keine echten Vertretungen; Feld 16/17 enthalten nur " +
                "Testwerte. Nur gültig, solange die Prüfsumme stimmt.";
            public string ErzeugtAm { get; set; } = "";
            public string Plan { get; set; } = "";
            public string Woche { get; set; } = "";
            public int Startwert { get; set; }
            public string Datei { get; set; } = UntisGenerator.DateiName;
            public string Pruefsumme { get; set; } = "";
            public List<string> Faelle { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        internal static void Schreiben(string ordner, byte[] bytes, string plan, DateTime montag, int startwert, List<string> faelle)
        {
            var m = new Marker
            {
                ErzeugtAm = DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                Plan = plan,
                Woche = $"{montag:dd.MM.yyyy} bis {montag.AddDays(4):dd.MM.yyyy}",
                Startwert = startwert,
                Pruefsumme = Convert.ToHexString(SHA256.HashData(bytes)),
                Faelle = new List<string>(faelle)
            };
            File.WriteAllText(Path.Combine(ordner, MarkerName), JsonSerializer.Serialize(m, JsonOpt), new UTF8Encoding(false));
        }

        /// <summary>true = gültige Demo-Datei; text beschreibt das Ergebnis für die Anzeige.</summary>
        public static bool Pruefe(string pfad, byte[] bytes, out string text)
        {
            text = "Echte Datei – wird nur auf diesem Rechner gelesen. Absenzgründe und Texte werden nicht gelesen.";
            try
            {
                string? ordner = Path.GetDirectoryName(pfad);
                if (string.IsNullOrEmpty(ordner)) return false;
                string mpfad = Path.Combine(ordner, MarkerName);
                if (!File.Exists(mpfad)) return false;
                var m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(mpfad), JsonOpt);
                if (m == null || !string.Equals(m.Datei, Path.GetFileName(pfad), StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(m.Pruefsumme, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                {
                    text = "Echte Datei – die Demo-Markierung passt nicht mehr (Datei verändert). Absenzgründe und Texte werden nicht gelesen.";
                    return false;
                }
                text = $"Demo-Datei (erzeugt am {m.ErzeugtAm}{(m.Plan.Length > 0 ? $" aus Plan „{m.Plan}“" : "")}, Woche {m.Woche}) – " +
                       "keine echten Vertretungen.";
                return true;
            }
            catch { return false; }
        }
    }
}
