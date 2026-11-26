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
    //    - je Lehrkraft: gehaltene Stunden je Art (Anzahl, wie bisher) und –
    //      seit V4 (274, 280) – Punkte, Entfälle mit 0 bzw. −1, Sondergründe
    //      und Saldo. Jede Buchung zählt für genau eine Lehrkraft
    //      (Zaehlung.Zaehlperson): die haltende, sonst die laut Plan. Saldo =
    //      Summe aller Zählwerte; die ausfallende Lehrkraft einer vertretenen
    //      Stunde zählt 0 (bis V4: gehalten − von anderen übernommen).
    //    - je Klasse: Entfall- und Vertretungsstunden (Basis für Quoten).
    //
    //  Für die faire Verteilung der Vorschläge liefert SaldoProLehrer den
    //  Saldo je Lehrkraft (283: Minusstunden zuerst).
    // =====================================================================

    public static class VertretungsZaehler
    {
        private static bool HatVertreter(VertretungsEintrag e)
            => !string.IsNullOrWhiteSpace(e.VertretungsLehrer);

        /// <summary>Zählerstände je Lehrkraft (V4, 274/280).</summary>
        public static Dictionary<string, ZaehlerStand> ProLehrer(IEnumerable<VertretungsEintrag> eintraege)
        {
            var result = new Dictionary<string, ZaehlerStand>(StringComparer.OrdinalIgnoreCase);

            ZaehlerStand Stand(string lehrer)
            {
                if (!result.TryGetValue(lehrer, out var st))
                    result[lehrer] = st = new ZaehlerStand { Bezug = lehrer };
                return st;
            }

            foreach (var e in eintraege)
            {
                // Gehaltene Stunden je Art (Anzahl) – wie bisher bei der haltenden Lehrkraft.
                if (HatVertreter(e))
                {
                    var vs = Stand(e.VertretungsLehrer!.Trim());
                    vs.ProArt[e.Art] = (vs.ProArt.TryGetValue(e.Art, out int a) ? a : 0) + 1;
                }

                string orig = (e.OriginalLehrer ?? "").Trim();
                if (orig.Length > 0)
                {
                    var os = Stand(orig);
                    if (e.Art == Vertretungsart.Entfall) os.EigeneAusfaelle += 1;   // „Entfall gesamt“
                }

                // 274: Punkte zählen für genau eine Lehrkraft.
                string wer = Zaehlung.Zaehlperson(e);
                if (wer.Length == 0) continue;
                var s = Stand(wer);
                int w = e.ZaehlwertSnapshot;
                s.Saldo += w;
                if (e.Sondergrund)
                {
                    s.Sondergruende += 1;
                    s.SondergrundPunkte += w;
                }
                else if (e.Art == Vertretungsart.Entfall)
                {
                    if (w < 0) s.EntfallMinus1 += 1;
                    else s.Entfall0 += 1;
                }
                else s.GehalteneVertretungen += w;
            }
            return result;
        }

        /// <summary>
        /// 283: Saldo je Lehrkraft (Summe aller Zählwerte, 274) – für den fairen Ausgleich in den Vorschlägen
        /// (VertretungsService.Vorschlaege: kleiner Saldo zuerst).
        /// </summary>
        public static Dictionary<string, int> SaldoProLehrer(IEnumerable<VertretungsEintrag> eintraege)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in eintraege)
            {
                string wer = Zaehlung.Zaehlperson(e);
                if (wer.Length == 0) continue;
                map[wer] = (map.TryGetValue(wer, out int x) ? x : 0) + e.ZaehlwertSnapshot;
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
