#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildKursMuster – Phase 5.2a-3 (Entscheidung 137): Muster der
    //  Kursbezeichnung im SchILD der Schule, z. B. „{Fach}-{Art}{Nr}“ für
    //  „D-G2“ oder „{Fach} {Art}{Nr}“ für „D GK1“. Liefert die Nummer des
    //  Kurses (Kriterium „Nummer“ der Zuordnung) zuverlässiger als die
    //  Automatik (erste Ziffernfolge nach dem Fach), die z. B. bei „Q1-D-G2“
    //  die 1 aus „Q1“ liest.
    //
    //  Platzhalter: {Fach} (Fach des Kurses laut Kurse.dat, sonst Buchstaben),
    //  {Art} (Buchstaben), {Nr} (Ziffern, Pflicht), {Jg} (Stufe/Jahrgang, z. B.
    //  Q1, EF, 05 oder 5), {*} (beliebiger Text). Leerzeichen im Muster = ein
    //  oder mehrere Leerzeichen; Groß/klein egal; die ganze Bezeichnung muss
    //  passen. Mehrere Muster mit „;“ trennen – das erste passende gilt.
    //  Gespeichert in der Zuordnungsdatei (KursMuster); leer = Automatik.
    //  Kurse, die nicht passen, bekommen die automatische Nummer (Hinweis).
    // =====================================================================

    public sealed class SchildKursTeile
    {
        public string Fach { get; init; } = "";
        public string Art { get; init; } = "";
        public int Nr { get; init; }
        public string Jg { get; init; } = "";
    }

    public sealed class SchildKursMuster
    {
        private enum Teil { Text, Fach, Art, Nr, Jg, Beliebig }

        private static readonly Regex Platzhalter = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
        private static readonly Regex Leer = new(@"(\s+)", RegexOptions.Compiled);
        private static readonly Regex Buchstaben = new(@"[^\W\d_]+", RegexOptions.Compiled);
        private static readonly Regex Ziffern = new(@"[0-9]+", RegexOptions.Compiled);
        private static readonly HashSet<string> Oberstufe = new(StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };
        private static readonly TimeSpan Zeitlimit = TimeSpan.FromMilliseconds(200);

        public const string Erlaubt = "{Fach}, {Art}, {Nr}, {Jg} und {*}";

        public string Text { get; }
        private readonly List<List<(Teil Art, string Wert)>> _muster;
        private readonly Dictionary<string, Regex> _cache = new(StringComparer.Ordinal);

        private SchildKursMuster(string text, List<List<(Teil Art, string Wert)>> muster)
        {
            Text = text;
            _muster = muster;
        }

        /// <summary>Liest das Muster. null = leer (Automatik) oder ungültig – dann ist fehler gesetzt.</summary>
        public static SchildKursMuster? Lies(string? text, out string? fehler)
        {
            fehler = null;
            string t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            var alle = new List<List<(Teil Art, string Wert)>>();
            foreach (var roh in t.Split(';'))
            {
                string r = roh.Trim();
                if (r.Length == 0) continue;
                var stuecke = new List<(Teil Art, string Wert)>();
                var zahl = new Dictionary<Teil, int>();
                int pos = 0;
                foreach (Match m in Platzhalter.Matches(r))
                {
                    if (m.Index > pos) stuecke.Add((Teil.Text, r[pos..m.Index]));
                    Teil? art = m.Groups[1].Value.Trim().ToUpperInvariant() switch
                    {
                        "FACH" => Teil.Fach,
                        "ART" => Teil.Art,
                        "NR" => Teil.Nr,
                        "JG" => Teil.Jg,
                        "*" => Teil.Beliebig,
                        _ => (Teil?)null
                    };
                    if (art == null)
                    {
                        fehler = $"unbekannter Platzhalter „{m.Value}“ – erlaubt sind {Erlaubt}";
                        return null;
                    }
                    zahl.TryGetValue(art.Value, out int n);
                    zahl[art.Value] = n + 1;
                    if (art.Value != Teil.Beliebig && n + 1 > 1)
                    {
                        fehler = $"Platzhalter „{m.Value}“ kommt in „{r}“ zweimal vor";
                        return null;
                    }
                    stuecke.Add((art.Value, ""));
                    pos = m.Index + m.Length;
                }
                if (pos < r.Length) stuecke.Add((Teil.Text, r[pos..]));
                if (stuecke.Any(s => s.Art == Teil.Text && (s.Wert.Contains('{') || s.Wert.Contains('}'))))
                {
                    fehler = $"„{r}“: geschweifte Klammer ohne Gegenstück";
                    return null;
                }
                if (!zahl.ContainsKey(Teil.Nr))
                {
                    fehler = $"„{r}“ enthält kein {{Nr}} – das Muster soll die Nummer des Kurses finden";
                    return null;
                }
                alle.Add(stuecke);
            }
            return alle.Count == 0 ? null : new SchildKursMuster(t, alle);
        }

        /// <summary>Zerlegt eine Kursbezeichnung; null = passt zu keinem der Muster.</summary>
        public SchildKursTeile? Zerlege(string? kursBez, string? fach, IEnumerable<string>? jahrgaenge)
        {
            string b = (kursBez ?? "").Trim();
            if (b.Length == 0) return null;
            string f = (fach ?? "").Trim();
            var jg = (jahrgaenge ?? Enumerable.Empty<string>()).Select(x => x.Trim().ToUpperInvariant())
                                                               .Where(x => x.Length > 0).ToList();
            for (int i = 0; i < _muster.Count; i++)
            {
                string key = $"{i}|{f.ToUpperInvariant()}|{string.Join(",", jg)}";
                if (!_cache.TryGetValue(key, out var rx)) _cache[key] = rx = Baue(_muster[i], f, jg);
                Match m;
                try { m = rx.Match(b); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups["nr"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int nr)) nr = 0;
                return new SchildKursTeile
                {
                    Fach = m.Groups["fach"].Success ? m.Groups["fach"].Value : "",
                    Art = m.Groups["art"].Success ? m.Groups["art"].Value : "",
                    Nr = nr,
                    Jg = m.Groups["jg"].Success ? m.Groups["jg"].Value : ""
                };
            }
            return null;
        }

        private static Regex Baue(List<(Teil Art, string Wert)> stuecke, string fach, List<string> jg)
        {
            var sb = new StringBuilder("^");
            foreach (var (art, wert) in stuecke)
            {
                switch (art)
                {
                    case Teil.Text:
                        foreach (var t in Leer.Split(wert))
                            if (t.Length > 0) sb.Append(string.IsNullOrWhiteSpace(t) ? @"\s+" : Regex.Escape(t));
                        break;
                    case Teil.Fach:
                        // Fach des Kurses zuerst (auch Fächer mit Ziffer wie „F6“), sonst beliebige Buchstaben
                        sb.Append("(?<fach>").Append(fach.Length > 0 ? Regex.Escape(fach) + "|" : "").Append(@"[^\W\d_]+)");
                        break;
                    case Teil.Art:
                        sb.Append(@"(?<art>[^\W\d_]+)");
                        break;
                    case Teil.Nr:
                        sb.Append("(?<nr>[0-9]+)");
                        break;
                    case Teil.Jg:
                        sb.Append("(?<jg>");
                        foreach (var j in jg)
                        {
                            sb.Append(Regex.Escape(j)).Append('|');
                            if (j.Length == 2 && j[0] == '0' && char.IsDigit(j[1])) sb.Append(Regex.Escape(j[1..])).Append('|');
                        }
                        sb.Append("EF|Q1|Q2|[0-9]{1,2})");
                        break;
                    default:
                        sb.Append(".*?");
                        break;
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Zeitlimit);
        }

        // =================================================================
        //  Vorschau für das Fenster (an Kursen aus dem Export)
        // =================================================================

        private static bool IstOberstufe(SchildKurs k)
            => k.Jahrgaenge.Any(j => Oberstufe.Contains(j.Trim())) || Oberstufe.Contains(k.Klasse.Trim());

        /// <summary>Nummer, wie die Automatik sie liest (gleiche Regel wie in der Zuordnung).</summary>
        public static int AutomatischeNummer(SchildKurs k)
            => SchildZuordnung.Nummer(k.KursBez, k.Fach, IstOberstufe(k) || k.Klasse.Length == 0 ? "" : SchildText.KlasseNorm(k.Klasse));

        private static string Form(string s) => Ziffern.Replace(Buchstaben.Replace(s ?? "", "A"), "9");

        /// <summary>
        /// Eine Zeile für das Fenster: drei Beispiele (verschiedene Formen, ein nicht passendes zuerst) und wie viele
        /// Kurse passen. gueltig = false bei einem fehlerhaften Muster (dann steht der Fehler im Text).
        /// </summary>
        public static string Vorschau(string? text, IReadOnlyList<SchildKurs> kurse, out bool gueltig)
        {
            var m = Lies(text, out string? fehler);
            gueltig = fehler == null;
            if (fehler != null) return "⚠ " + fehler;
            var echte = kurse.Where(k => !k.IstKlassenverband).OrderBy(k => IstOberstufe(k) ? 0 : 1).ThenBy(k => k.Zeile).ToList();
            if (echte.Count == 0) return m == null ? "leer = automatisch (erste Ziffernfolge nach dem Fach)" : "keine Kurse in diesem Abschnitt";

            var teile = echte.ToDictionary(k => k, k => m?.Zerlege(k.KursBez, k.Fach, k.Jahrgaenge));
            var beispiele = new List<SchildKurs>();
            if (m != null)
            {
                var nicht = echte.FirstOrDefault(k => teile[k] == null);
                if (nicht != null) beispiele.Add(nicht);
            }
            foreach (var k in echte)
            {
                if (beispiele.Count >= 3) break;
                if (!beispiele.Any(b => Form(b.KursBez) == Form(k.KursBez))) beispiele.Add(k);
            }
            foreach (var k in echte)
            {
                if (beispiele.Count >= 3) break;
                if (!beispiele.Contains(k)) beispiele.Add(k);
            }

            var sb = new StringBuilder(m == null ? "leer = automatisch: " : "Vorschau: ");
            sb.Append(string.Join(" · ", beispiele.Select(k =>
            {
                int auto = AutomatischeNummer(k);
                var t = teile[k];
                if (m == null) return $"„{k.KursBez}“ → Nr {auto}";
                if (t == null) return $"„{k.KursBez}“ → passt nicht (Automatik: Nr {auto})";
                string s = $"„{k.KursBez}“ → " + (t.Jg.Length > 0 ? $"Jg {t.Jg}, " : "") + (t.Fach.Length > 0 ? $"Fach {t.Fach}, " : "") +
                           (t.Art.Length > 0 ? $"Art {t.Art}, " : "") + $"Nr {t.Nr}";
                return auto != t.Nr ? s + $" (Automatik: {auto})" : s;
            })));
            if (m != null)
            {
                var ober = echte.Where(IstOberstufe).ToList();
                var sek1 = echte.Where(k => !IstOberstufe(k)).ToList();
                sb.Append($" — passt bei {ober.Count(k => teile[k] != null)} von {ober.Count} Oberstufenkursen");
                if (sek1.Count > 0) sb.Append($" und {sek1.Count(k => teile[k] != null)} von {sek1.Count} Sek-I-Kursen");
            }
            return sb.ToString();
        }
    }
}
