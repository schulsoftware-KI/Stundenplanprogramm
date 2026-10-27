#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  VertretungsZaehler – der differenzierte Zähler (Konzept §5).
    //
    //  Aggregiert die gebuchten VertretungsEinträge zu Zählerständen:
    //    - je Lehrkraft: gehaltene Vertretungen (gewichtet nach Zählwert),
    //      je Art einzeln, eigene Ausfälle und Saldo (gehalten − von anderen
    //      übernommen).
    //    - je Klasse: Entfall- und Vertretungsstunden (Basis für Quoten).
    //
    //  Für die faire Verteilung der Vorschläge liefert GehaltenProLehrer die
    //  reine (gewichtete) Vertretungszahl je Lehrkraft.
    // =====================================================================

    public static class VertretungsZaehler
    {
        private static bool HatVertreter(VertretungsEintrag e)
            => !string.IsNullOrWhiteSpace(e.VertretungsLehrer);

        /// <summary>Zählerstände je Lehrkraft.</summary>
        public static Dictionary<string, ZaehlerStand> ProLehrer(IEnumerable<VertretungsEintrag> eintraege)
        {
            var gehalten = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var erlassen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ausfall = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var proArt = new Dictionary<string, Dictionary<Vertretungsart, int>>(StringComparer.OrdinalIgnoreCase);
            var alle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(Dictionary<string, int> map, string k, int v)
                => map[k] = (map.TryGetValue(k, out int x) ? x : 0) + v;

            foreach (var e in eintraege)
            {
                if (HatVertreter(e))
                {
                    string v = e.VertretungsLehrer!.Trim();
                    alle.Add(v);
                    Add(gehalten, v, e.ZaehlwertSnapshot);

                    if (!proArt.TryGetValue(v, out var arten))
                        proArt[v] = arten = new Dictionary<Vertretungsart, int>();
                    arten[e.Art] = (arten.TryGetValue(e.Art, out int a) ? a : 0) + 1;
                }

                if (!string.IsNullOrWhiteSpace(e.OriginalLehrer))
                {
                    string o = e.OriginalLehrer.Trim();
                    alle.Add(o);
                    if (HatVertreter(e)) Add(erlassen, o, 1);
                    if (e.Art == Vertretungsart.Entfall) Add(ausfall, o, 1);
                }
            }

            var result = new Dictionary<string, ZaehlerStand>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in alle)
            {
                int g = gehalten.TryGetValue(l, out int gg) ? gg : 0;
                int er = erlassen.TryGetValue(l, out int ee) ? ee : 0;
                result[l] = new ZaehlerStand
                {
                    Bezug = l,
                    GehalteneVertretungen = g,
                    EigeneAusfaelle = ausfall.TryGetValue(l, out int af) ? af : 0,
                    Saldo = g - er,
                    ProArt = proArt.TryGetValue(l, out var pa) ? pa : new Dictionary<Vertretungsart, int>()
                };
            }
            return result;
        }

        /// <summary>
        /// Reine (gewichtete) Vertretungszahl je Lehrkraft – für den fairen
        /// Ausgleich in den Vorschlägen (VertretungsService.Vorschlaege).
        /// </summary>
        public static Dictionary<string, int> GehaltenProLehrer(IEnumerable<VertretungsEintrag> eintraege)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in eintraege)
                if (HatVertreter(e))
                {
                    string v = e.VertretungsLehrer!.Trim();
                    map[v] = (map.TryGetValue(v, out int x) ? x : 0) + e.ZaehlwertSnapshot;
                }
            return map;
        }

        /// <summary>
        /// Zählerstände je Klasse (Entfall- und Vertretungsstunden). SollStunden
        /// bleibt 0, bis die geplanten Stunden je Klasse gezählt werden (später);
        /// EntfallQuote ist dann entsprechend 0.
        /// </summary>
        public static Dictionary<string, ZaehlerStand> ProKlasse(IEnumerable<VertretungsEintrag> eintraege)
        {
            var result = new Dictionary<string, ZaehlerStand>(StringComparer.OrdinalIgnoreCase);

            ZaehlerStand Get(string klasse)
            {
                if (!result.TryGetValue(klasse, out var z))
                    result[klasse] = z = new ZaehlerStand { Bezug = klasse };
                return z;
            }

            foreach (var e in eintraege)
            {
                foreach (var klasse in e.Klassen)
                {
                    if (string.IsNullOrWhiteSpace(klasse)) continue;
                    var z = Get(klasse.Trim());
                    z.ProArt[e.Art] = (z.ProArt.TryGetValue(e.Art, out int a) ? a : 0) + 1;
                    if (e.Art == Vertretungsart.Entfall) z.EntfallStunden += 1;
                }
            }
            return result;
        }
    }
}
