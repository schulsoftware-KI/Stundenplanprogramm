#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Raumplanung
{
    // =====================================================================
    //  Räume aus Untis übernehmen (Knopf 3 „Plan importieren", GPU001.TXT).
    //
    //  Nach dem Import ins Blatt „Plan" baut diese Klasse – auf Nachfrage – aus den
    //  Räumen der GPU001.TXT den Raumplan des Plans „Plan" (Blatt „Raumplan"; die
    //  bisherigen Zeilen dieses Plans werden ersetzt). Untis hat je Klasse eine eigene
    //  Zeile; zusammengefasst wird je Stunde × UNr × Lehrkraft. Die Zeilen entstehen
    //  aus denselben Raumbedarfen laut UV wie bei Knopf 25 (RaumVerteilung.ZeilenVorlage):
    //    1. Untis-Raum = Wunschraum laut UV → genau dieser Bedarf;
    //    2. übrige Untis-Räume → die noch offenen Bedarfe derselben Lehrkraft;
    //    3. dann Teile derselben Lehrkraft ohne Raumangabe in der UV;
    //    4. Rest → zusätzlicher Raum (eigene Zeile ohne Wunschraum).
    //  Team-Teaching: hat nur eine der Lehrkräfte in Untis einen Raum, gilt er für den
    //  gemeinsamen Bedarf auch für die andere. Räume von Lehrkräften, die die UV für die
    //  UNr nicht kennt (z. B. Tausch in Untis), gehen an noch offene Bedarfe derselben
    //  Stunde, sonst nur ins Protokoll. Bedarfe ohne Untis-Raum: „ohne Raum – in Untis
    //  kein Raum" (verteilt wird nur auf Nachfrage). Herkunft „aus Untis", nicht fest.
    //  Räume, die die UV nicht kennt, werden im Blatt „Räume" ergänzt.
    // =====================================================================
    public static class RaumUntisImport
    {
        /// <summary>Eine Zeile der GPU001.TXT, soweit sie für die Räume gebraucht wird.</summary>
        public sealed class UntisRaum
        {
            public int UNr { get; set; }
            public int Tag { get; set; }        // 1 = Mo … 5 = Fr
            public int Stunde { get; set; }
            public string Lehrer { get; set; } = "";
            public string Raum { get; set; } = "";
        }

        public sealed class Ergebnis
        {
            public string Plan { get; set; } = "";
            public int Belegungen { get; set; }
            public int WieUv { get; set; }
            public int Anders { get; set; }
            public int OhneAngabeMitRaum { get; set; }
            public int Zusaetzlich { get; set; }
            public int OhneRaum { get; set; }
            public int NeueRaeume { get; set; }
            public List<string> Hinweise { get; } = new();

            public string Kurztext()
                => $"Räume aus Untis für Plan „{Plan}“: {Belegungen} Raumbelegungen – {WieUv} wie in der UV, " +
                   $"{Anders} mit anderem Raum, {OhneAngabeMitRaum} ohne Raumangabe in der UV, {Zusaetzlich} zusätzlich. " +
                   (OhneRaum > 0
                       ? $"{OhneRaum} Raumbedarf(e) ohne Raum in Untis (rot; verteilen bei Bedarf im Planeditor mit „Räume verteilen“ " +
                         "oder mit Knopf 25 „Behalten und ergänzen“)."
                       : "Jeder Raumbedarf hat einen Raum.") +
                   (NeueRaeume > 0 ? $" {NeueRaeume} Raum/Räume aus Untis im Blatt „Räume“ ergänzt (Art bitte prüfen)." : "");
        }

        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
        private static bool Gleich(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Baut aus den Untis-Räumen den Raumplan für <paramref name="plan"/> (Standard „Plan“,
        /// das Blatt aus Knopf 3) und speichert ihn. Die Belegung kommt aus dem Plan selbst.
        /// </summary>
        public static Ergebnis Importiere(string planPfad, IEnumerable<UntisRaum> untis, string plan = "Plan")
        {
            var e = RaumplanStore.Lies(planPfad, plan);
            if (!Gleich(e.PlanSpalte, plan))
                throw new InvalidOperationException($"Plan „{plan}“ nicht gefunden.");
            plan = e.PlanSpalte;
            var v = RaumVerteilung.BaueVerzeichnis(e);
            var vorlage = new RaumVerteilung.ZeilenVorlage(e, v);
            var erg = new Ergebnis { Plan = plan };

            // Untis-Räume je (Tag, Stunde, UNr) → Lehrkraft → Räume (Reihenfolge der Datei, ohne Doppelte).
            var jeStunde = new Dictionary<(int Tag, int Stunde, int UNr), Dictionary<string, List<string>>>();
            foreach (var u in untis)
            {
                string raum = (u.Raum ?? "").Trim();
                if (raum.Length == 0) continue;
                var key = (u.Tag, u.Stunde, u.UNr);
                if (!jeStunde.TryGetValue(key, out var jeLehrer))
                    jeStunde[key] = jeLehrer = new Dictionary<string, List<string>>(Cmp);
                string lehrer = (u.Lehrer ?? "").Trim();
                if (!jeLehrer.TryGetValue(lehrer, out var raeume)) jeLehrer[lehrer] = raeume = new List<string>();
                if (!raeume.Contains(raum, Cmp)) raeume.Add(raum);
            }

            var zeilen = new List<RaumZeile>();
            var unbekannt = new SortedSet<int>();
            var genutzt = new HashSet<(int, int, int)>();
            foreach (var (wtag, stunde, unrn) in e.Slots)
            {
                int tag = TagNummer(wtag);
                foreach (int unr in unrn)
                {
                    if (!e.Unterrichte.ContainsKey(unr)) { unbekannt.Add(unr); continue; }
                    var stundeZeilen = vorlage.Offen(plan, wtag, stunde, unr);
                    jeStunde.TryGetValue((tag, stunde, unr), out var jeLehrer);
                    if (jeLehrer != null) genutzt.Add((tag, stunde, unr));
                    OrdneZu(stundeZeilen, jeLehrer, erg, $"{wtag} {stunde}. Std, UNr {unr}");
                    zeilen.AddRange(stundeZeilen);
                }
            }

            if (unbekannt.Count > 0)
                erg.Hinweise.Add($"{unbekannt.Count} UNr(n) im Plan stehen nicht in der UV – ohne Räume: " +
                                 string.Join(", ", unbekannt.Take(20)) + (unbekannt.Count > 20 ? ", …" : ""));
            int ohneStunde = jeStunde.Keys.Count(k => !genutzt.Contains(k) && !unbekannt.Contains(k.UNr));
            if (ohneStunde > 0)
                erg.Hinweise.Add($"{ohneStunde} Untis-Stunde(n) mit Raum liegen nicht im Zeitraster des Plans (Tag/Stunde fehlt) – nicht übernommen.");

            // Räume, die die UV nicht kennt → Raumverzeichnis bzw. Blatt „Räume“ ergänzen.
            foreach (var raum in zeilen.Select(z => z.Raum).Where(r => r.Length > 0).Distinct(Cmp).ToList())
            {
                if (v.Enthaelt(raum)) continue;
                v.Hinzu(new RaumInfo
                {
                    Name = raum,
                    Art = RaumArt.Fachraum,
                    AlsAusweich = false,
                    Bemerkung = "aus Untis (GPU001) ergänzt – Art bitte prüfen",
                    Ergaenzt = true,
                    QuelleText = "aus Untis ergänzt"
                });
                erg.NeueRaeume++;
            }

            erg.Belegungen = zeilen.Count(z => z.Raum.Length > 0);
            RaumplanStore.Speichern(planPfad, plan, zeilen, v);
            return erg;
        }

        // Ordnet die Untis-Räume einer Stunde (eine UNr) den Zeilen laut UV zu.
        private static void OrdneZu(List<RaumZeile> zeilen, Dictionary<string, List<string>>? jeLehrer,
                                    Ergebnis erg, string wo)
        {
            var lehrerUv = zeilen.Select(z => z.Lehrer).Distinct(Cmp).ToList();
            var fremd = new List<string>();
            if (jeLehrer != null)
                foreach (var kv in jeLehrer)
                    if (!lehrerUv.Contains(kv.Key, Cmp))
                        foreach (var r in kv.Value)
                            if (!fremd.Contains(r, Cmp)) fremd.Add(r);

            foreach (string lehrer in lehrerUv)
            {
                var rest = jeLehrer != null && jeLehrer.TryGetValue(lehrer, out var rs)
                    ? new List<string>(rs) : new List<string>();
                var bedarfe = zeilen.Where(z => Gleich(z.Lehrer, lehrer) && z.Herkunft == RaumHerkunft.Offen).ToList();

                // 1. Untis-Raum = Wunschraum laut UV
                foreach (var z in bedarfe)
                {
                    string? r = rest.FirstOrDefault(x => Gleich(x, z.Wunschraum));
                    if (r == null) continue;
                    Setze(z, r, "");
                    rest.Remove(r);
                    erg.WieUv++;
                }
                // 2. übrige Untis-Räume → noch offene Bedarfe derselben Lehrkraft
                foreach (var z in bedarfe.Where(z => z.Herkunft == RaumHerkunft.Offen))
                {
                    if (rest.Count == 0) break;
                    Setze(z, rest[0], "laut UV " + z.Wunschraum);
                    rest.RemoveAt(0);
                    erg.Anders++;
                }
                // 3. Teile derselben Lehrkraft ohne Raumangabe in der UV
                foreach (var z in zeilen.Where(z => Gleich(z.Lehrer, lehrer) && z.Wunschraum.Length == 0 && z.Raum.Length == 0))
                {
                    if (rest.Count == 0) break;
                    Setze(z, rest[0], "keine Raumangabe in der UV");
                    rest.RemoveAt(0);
                    erg.OhneAngabeMitRaum++;
                }
                // 4. Rest → zusätzlicher Raum
                if (rest.Count > 0)
                {
                    var muster = zeilen.First(z => Gleich(z.Lehrer, lehrer));
                    foreach (var r in rest)
                    {
                        zeilen.Add(new RaumZeile
                        {
                            Plan = muster.Plan, WTag = muster.WTag, Stunde = muster.Stunde, UNr = muster.UNr,
                            Lehrer = muster.Lehrer, Fach = muster.Fach, Klassen = muster.Klassen,
                            Raum = r, Wunschraum = "", Herkunft = RaumHerkunft.Untis,
                            Hinweis = "zusätzlicher Raum laut Untis"
                        });
                        erg.Zusaetzlich++;
                    }
                }
            }

            // Team-Teaching: gemeinsamer Bedarf (gleicher Wunschraum), nur eine Lehrkraft hat einen Raum.
            foreach (var z in zeilen.Where(z => z.Herkunft == RaumHerkunft.Offen).ToList())
            {
                var partner = zeilen.FirstOrDefault(x => x != z && x.Herkunft == RaumHerkunft.Untis
                                                         && x.Wunschraum.Length > 0 && Gleich(x.Wunschraum, z.Wunschraum));
                if (partner == null) continue;
                Setze(z, partner.Raum, "wie Team-Partner " + partner.Lehrer);
                if (Gleich(partner.Raum, z.Wunschraum)) erg.WieUv++; else erg.Anders++;
            }

            // Räume von Lehrkräften, die die UV nicht kennt → noch offene Bedarfe dieser Stunde.
            foreach (var z in zeilen.Where(z => z.Herkunft == RaumHerkunft.Offen))
            {
                if (fremd.Count == 0) break;
                string r = fremd.FirstOrDefault(x => Gleich(x, z.Wunschraum)) ?? fremd[0];
                Setze(z, r, "Lehrkraft in Untis abweichend");
                fremd.Remove(r);
                if (Gleich(r, z.Wunschraum)) erg.WieUv++; else erg.Anders++;
            }
            if (fremd.Count > 0)
                erg.Hinweise.Add($"{wo}: Raum {string.Join(", ", fremd)} einer Lehrkraft, die die UV für diese UNr nicht kennt – nicht zugeordnet.");

            // Bedarfe ohne Untis-Raum
            foreach (var z in zeilen.Where(z => z.Herkunft == RaumHerkunft.Offen))
            {
                z.Herkunft = RaumHerkunft.OhneRaum;
                z.Hinweis = "in Untis kein Raum";
                erg.OhneRaum++;
            }
        }

        private static void Setze(RaumZeile z, string raum, string hinweis)
        {
            z.Raum = raum;
            z.Herkunft = RaumHerkunft.Untis;
            z.Fix = false;
            z.Hinweis = hinweis;
        }

        // „Mo“ … „Fr“ (auch ausgeschrieben) → 1 … 5; sonst 0.
        private static int TagNummer(string wtag)
        {
            string t = (wtag ?? "").Trim().ToLowerInvariant();
            if (t.StartsWith("mo")) return 1;
            if (t.StartsWith("di")) return 2;
            if (t.StartsWith("mi")) return 3;
            if (t.StartsWith("do")) return 4;
            if (t.StartsWith("fr")) return 5;
            return 0;
        }
    }
}
