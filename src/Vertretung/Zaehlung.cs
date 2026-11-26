#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  V4 – Zählung neu (Entscheidungen 274, 275, 277; claude/V4_Vorschlag.md)
    //
    //  - 274: Jede Buchung zählt für GENAU EINE Lehrkraft: für die Lehrkraft, die die Stunde hält
    //    (Vertretung, Betreuung, Zusammenlegung, „hierher verlegt“ – VertretungsLehrer), sonst für die
    //    Lehrkraft laut Plan (Entfall, Raumvertretung, EVA ohne Aufsicht). Die ausfallende Lehrkraft einer
    //    vertretenen Stunde zählt 0. Saldo = Summe aller Zählwerte (VertretungsZaehler).
    //  - 275: Ein Entfall bekommt den Wert seines Anlasses: Lehrkraft fehlt 0, Klasse/Kurs fehlt −1,
    //    Raum gesperrt 0 (freie Zeit laut Kalender: V4b). Gehaltene Stunden behalten den Wert aus dem
    //    Blatt „Arten“ (0, wenn eine UV-Stunde ersetzt wird) – wie bisher.
    //  - 277: Sondergrund – der Planer setzt −1/0/+1 selbst, Kommentar Pflicht (eine Zeile, ≤ 200 Zeichen).
    //  Der Kommentar bleibt im Desktop (nie in der App, 282).
    // =====================================================================

    public static class Zaehlung
    {
        public const int KommentarMax = 200;

        /// <summary>Hinweis unter jedem Kommentarfeld (277).</summary>
        public const string KommentarHinweis = "Nur für die Zählung – keine Gesundheitsangaben.";

        /// <summary>Wählbare eigene Werte eines Sondergrunds.</summary>
        public static readonly int[] EigeneWerte = { -1, 0, +1 };

        /// <summary>„+1“, „0“, „−1“ (mit echtem Minuszeichen).</summary>
        public static string WertText(int w) => w > 0 ? "+" + w : w < 0 ? "−" + (-w) : "0";

        /// <summary>275: Vorbelegung eines Entfalls nach Anlass (unbekannt = 0, wie in älteren Dateien).</summary>
        public static int EntfallWert(EntfallAnlass? anlass) => anlass == EntfallAnlass.Klasse ? -1 : 0;

        /// <summary>Anlass aus dem, was laut Abwesenheit fehlt.</summary>
        public static EntfallAnlass AnlassAus(AbwesenheitsSubjekt s) => s switch
        {
            AbwesenheitsSubjekt.Klasse => EntfallAnlass.Klasse,
            AbwesenheitsSubjekt.Raum => EntfallAnlass.Raum,
            _ => EntfallAnlass.Lehrkraft
        };

        public static string AnlassText(EntfallAnlass? a) => a switch
        {
            EntfallAnlass.Lehrkraft => "Lehrkraft fehlt",
            EntfallAnlass.Klasse => "Klasse/Kurs fehlt",
            EntfallAnlass.Raum => "Raum gesperrt",
            EntfallAnlass.FreieZeit => "freie Zeit",
            _ => "unbekannt"
        };

        /// <summary>274: Lehrkraft, für die die Buchung zählt (die haltende, sonst die laut Plan).</summary>
        public static string Zaehlperson(VertretungsEintrag e)
        {
            string v = (e.VertretungsLehrer ?? "").Trim();
            if (e.Art != Vertretungsart.Entfall && v.Length > 0) return v;
            return (e.OriginalLehrer ?? "").Trim();
        }

        /// <summary>
        /// Standard-Zählwert einer Buchung (274/275) – ohne Sondergrund. Gehaltene Stunden: Wert der Art aus dem
        /// Blatt „Arten“, 0, wenn die Lehrkraft dafür eine UV-Stunde aufgibt; ohne haltende Lehrkraft 0 (Raumvertretung,
        /// EVA ohne Aufsicht, Verlegung); Entfall nach Anlass.
        /// </summary>
        public static int StandardWert(Vertretungsart art, EntfallAnlass? anlass, string? vertreter,
            DateTime datum, int stunde, VertretungsDaten daten, PlanDaten? plan)
        {
            if (art == Vertretungsart.Entfall) return EntfallWert(anlass);
            if (art == Vertretungsart.Verlegung || art == Vertretungsart.Raumvertretung) return 0;
            string v = (vertreter ?? "").Trim();
            if (v.Length == 0) return 0;
            if (plan != null)
            {
                string? wtag = daten.Kalender?.RasterTagFuer(datum);
                if (wtag != null && plan.HatUvImSlot(v, wtag, stunde)) return 0;
            }
            return daten.Zaehlwert(art);
        }

        /// <summary>Standard-Zählwert eines vorhandenen Eintrags.</summary>
        public static int StandardWert(VertretungsEintrag e, VertretungsDaten daten, PlanDaten? plan)
            => StandardWert(e.Art, e.Anlass, e.VertretungsLehrer, e.Datum, e.Stunde, daten, plan);

        /// <summary>„Zurück auf Standard“: Sondergrund und Kommentar weg, Wert laut 274/275.</summary>
        public static void SetzeStandard(VertretungsEintrag e, VertretungsDaten daten, PlanDaten? plan)
        {
            e.Sondergrund = false;
            e.Zaehlkommentar = "";
            e.ZaehlwertSnapshot = StandardWert(e, daten, plan);
        }

        /// <summary>Sondergrund setzen (Kommentar vorher mit <see cref="KommentarFehler"/> prüfen).</summary>
        public static void SetzeSondergrund(VertretungsEintrag e, int wert, string kommentar)
        {
            e.Sondergrund = true;
            e.ZaehlwertSnapshot = Math.Max(-1, Math.Min(1, wert));
            e.Zaehlkommentar = Bereinige(kommentar);
        }

        /// <summary>Kommentar auf eine Zeile bringen (Zeilenumbrüche/Tabs → Leerzeichen, getrimmt).</summary>
        public static string Bereinige(string? kommentar)
            => string.Join(" ", (kommentar ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(t => t.Trim()).Where(t => t.Length > 0));

        /// <summary>Fehlertext, wenn der Kommentar eines Sondergrunds fehlt bzw. zu lang ist, sonst null.</summary>
        public static string? KommentarFehler(string? kommentar)
        {
            string k = Bereinige(kommentar);
            if (k.Length == 0) return "Für einen eigenen Zählwert ist ein Kommentar nötig.";
            if (k.Length > KommentarMax) return $"Der Kommentar ist zu lang ({k.Length} von höchstens {KommentarMax} Zeichen).";
            return null;
        }

        /// <summary>Kurztext der Zählung, z. B. „−1“, „0 (Sondergrund)“.</summary>
        public static string KurzText(VertretungsEintrag e)
            => WertText(e.ZaehlwertSnapshot) + (e.Sondergrund ? " (Sondergrund)" : "");

        /// <summary>Tooltip-Text: Zählwert, für wen, Anlass, ggf. Sondergrund mit Kommentar.</summary>
        public static string TippText(VertretungsEintrag e)
        {
            string wer = Zaehlperson(e);
            string t = $"Zählung: {WertText(e.ZaehlwertSnapshot)}" + (wer.Length > 0 ? $" für {wer}" : "");
            if (e.Art == Vertretungsart.Entfall) t += $" (Entfall, {AnlassText(e.Anlass)})";
            if (e.Sondergrund) t += "\nSondergrund: " + (string.IsNullOrWhiteSpace(e.Zaehlkommentar) ? "–" : e.Zaehlkommentar);
            return t;
        }

        // =================================================================
        //  Liste aller Entfälle (279, 281) – Blatt „Entfälle“ der Vertretungsdatei und des Zähler-Exports
        // =================================================================

        /// <summary>Eine Zeile der Liste „Entfälle“.</summary>
        public sealed class EntfallZeile
        {
            public DateTime Datum { get; set; }
            public int Stunde { get; set; }
            public string Klassen { get; set; } = "";
            public string Fach { get; set; } = "";
            public string Lehrkraft { get; set; } = "";
            public string Anlass { get; set; } = "";
            /// <summary>Abwesenheitsgrund bzw. (V4b) Bezeichnung der freien Zeit.</summary>
            public string Grund { get; set; } = "";
            public int Zaehlwert { get; set; }
            public bool Sondergrund { get; set; }
            public string Kommentar { get; set; } = "";
            /// <summary>„gebucht“ bzw. (V4b) „Kalender“.</summary>
            public string Quelle { get; set; } = "gebucht";
        }

        /// <summary>Alle gebuchten Entfälle (Verlegungen sind keine – die Stunde findet statt), nach Datum und Stunde.</summary>
        public static List<EntfallZeile> EntfallZeilen(IEnumerable<VertretungsEintrag> eintraege)
            => eintraege.Where(e => e.Art == Vertretungsart.Entfall)
                .Select(e => new EntfallZeile
                {
                    Datum = e.Datum.Date,
                    Stunde = e.Stunde,
                    Klassen = string.Join(", ", e.Klassen),
                    Fach = e.Fach,
                    Lehrkraft = (e.OriginalLehrer ?? "").Trim(),
                    Anlass = AnlassText(e.Anlass),
                    Grund = GrundText(e.Grund),
                    Zaehlwert = e.ZaehlwertSnapshot,
                    Sondergrund = e.Sondergrund,
                    Kommentar = e.Sondergrund ? e.Zaehlkommentar ?? "" : "",
                    Quelle = "gebucht"
                })
                .OrderBy(z => z.Datum).ThenBy(z => z.Stunde)
                .ThenBy(z => z.Klassen, StringComparer.OrdinalIgnoreCase)
                .ThenBy(z => z.Lehrkraft, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static string GrundText(VertretungsGrund g) => g switch
        {
            VertretungsGrund.Pruefung => "Prüfung",
            _ => g.ToString()
        };
    }
}
