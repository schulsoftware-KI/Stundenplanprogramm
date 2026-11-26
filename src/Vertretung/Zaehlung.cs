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
    //
    //  V4b (276, 287–296):
    //  - 287: Hält eine ANDERE Lehrkraft die Stunde (Vertretung, Betreuung, Zusammenlegung, EVA mit Aufsicht),
    //    zählt zusätzlich die ausfallende Lehrkraft laut Plan nach ihrem Anlass – „fehlt“ 0 (Vorgabe), „nicht
    //    verhindert“ −1 – bzw. nach ihrem eigenen Sondergrund (−1/0/+1 mit Kommentar). „Genau eine Lehrkraft“
    //    gilt weiter für Entfall, Raumvertretung, EVA ohne Aufsicht und Verlegung.
    //  - 276: Freie Zeit einzelner Klassen laut Kalender → Entfall mit Anlass FreieZeit und dem Wert des
    //    Kalendereintrags (KalenderEntfaelle).
    //  - 288: Gründe bei „Klasse/Kurs fehlt“: Klassenfahrt, Exkursion, Prüfung, Praktikum, Sonstiges.
    //  - 289: Kurztexte mit Anlass, bei vertretenen Stunden beide Teile.
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

        /// <summary>275: Vorbelegung eines Entfalls nach Anlass (unbekannt = 0, wie in älteren Dateien; freie Zeit: siehe unten).</summary>
        public static int EntfallWert(EntfallAnlass? anlass) => anlass == EntfallAnlass.Klasse ? -1 : 0;

        /// <summary>276: wie <see cref="EntfallWert(EntfallAnlass?)"/>, bei „freie Zeit“ der Wert des Kalendereintrags.</summary>
        public static int EntfallWert(EntfallAnlass? anlass, VertretungsDaten daten, DateTime datum, IEnumerable<string>? klassen)
            => anlass == EntfallAnlass.FreieZeit
                ? KalenderEntfaelle.FreiFuer(daten.Kalender, datum, klassen)?.Wert ?? 0
                : EntfallWert(anlass);

        // =================================================================
        //  288: Gründe je Anlass
        // =================================================================

        /// <summary>Gründe bei „Klasse/Kurs fehlt“ (Abwesenheit „Klasse“, Wocheneditor „Entfall ▸ Klasse/Kurs fehlt“).</summary>
        public static readonly VertretungsGrund[] KlassenGruende =
        {
            VertretungsGrund.Klassenfahrt, VertretungsGrund.Exkursion, VertretungsGrund.Pruefung,
            VertretungsGrund.Praktikum, VertretungsGrund.Sonstiges
        };

        /// <summary>Vorgabe bei „Klasse/Kurs fehlt“ (statt „Krankheit“).</summary>
        public const VertretungsGrund KlassenGrundVorgabe = VertretungsGrund.Sonstiges;

        /// <summary>Gründe bei fehlender Lehrkraft bzw. Raum – wie bisher (ohne „Praktikum“; „Klassenfahrt“ = Begleitung).</summary>
        public static readonly VertretungsGrund[] LehrerGruende =
            ((VertretungsGrund[])Enum.GetValues(typeof(VertretungsGrund))).Where(g => g != VertretungsGrund.Praktikum).ToArray();

        /// <summary>Liste der Gründe, ergänzt um <paramref name="vorhanden"/>, falls er fehlt (z. B. eine gebuchte „Praktikum“).</summary>
        public static List<VertretungsGrund> GruendeMit(IEnumerable<VertretungsGrund> liste, VertretungsGrund? vorhanden)
        {
            var l = liste.ToList();
            if (vorhanden is VertretungsGrund v && !l.Contains(v)) l.Add(v);
            return l;
        }

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

        /// <summary>289: kurzer Anlass für die Spalten „Zählung“/„gebucht“, z. B. „Klasse fehlt“.</summary>
        public static string AnlassKurz(EntfallAnlass? a) => a switch
        {
            EntfallAnlass.Lehrkraft => "Lehrkraft fehlt",
            EntfallAnlass.Klasse => "Klasse fehlt",
            EntfallAnlass.Raum => "Raum gesperrt",
            EntfallAnlass.FreieZeit => "freie Zeit",
            _ => "unbekannt"
        };

        // =================================================================
        //  287: ausfallende Lehrkraft einer vertretenen Stunde
        // =================================================================

        /// <summary>
        /// Hält eine ANDERE Lehrkraft die Stunde (Vertretung, Betreuung, Zusammenlegung, EVA mit Aufsicht)? Dann zählt
        /// zusätzlich die ausfallende Lehrkraft laut Plan (287). Nicht bei Entfall, Raumvertretung, EVA ohne Aufsicht,
        /// Verlegung.
        /// </summary>
        public static bool HatAusfall(Vertretungsart art, string? vertreter, string? orig)
        {
            if (art != Vertretungsart.Vertretung && art != Vertretungsart.BetreuungAufsicht
                && art != Vertretungsart.Zusammenlegung && art != Vertretungsart.EVA) return false;
            string v = (vertreter ?? "").Trim(), o = (orig ?? "").Trim();
            return v.Length > 0 && o.Length > 0 && !string.Equals(v, o, StringComparison.OrdinalIgnoreCase);
        }

        public static bool HatAusfall(VertretungsEintrag e) => HatAusfall(e.Art, e.VertretungsLehrer, e.OriginalLehrer);

        /// <summary>Wert der ausfallenden Lehrkraft nach Anlass (fehlt bzw. unbekannt 0, nicht verhindert −1).</summary>
        public static int AusfallWert(AusfallAnlass? a) => a == AusfallAnlass.NichtVerhindert ? -1 : 0;

        public static string AusfallText(AusfallAnlass? a) => a switch
        {
            AusfallAnlass.Lehrkraft => "fehlt (krank/dienstlich)",
            AusfallAnlass.NichtVerhindert => "nicht verhindert",
            _ => "unbekannt"
        };

        public static string AusfallKurz(AusfallAnlass? a) => a switch
        {
            AusfallAnlass.Lehrkraft => "fehlt",
            AusfallAnlass.NichtVerhindert => "nicht verhindert",
            _ => "unbekannt"
        };

        /// <summary>Anlass der ausfallenden Lehrkraft setzen; ohne Sondergrund folgt der Wert dem Anlass.</summary>
        public static void SetzeAusfall(VertretungsEintrag e, AusfallAnlass a)
        {
            e.AnlassAusfall = a;
            if (!e.SondergrundAusfall) e.ZaehlwertAusfall = AusfallWert(a);
        }

        /// <summary>Sondergrund der ausfallenden Lehrkraft (Kommentar vorher mit <see cref="KommentarFehler"/> prüfen).</summary>
        public static void SetzeAusfallSondergrund(VertretungsEintrag e, int wert, string kommentar)
        {
            e.SondergrundAusfall = true;
            e.ZaehlwertAusfall = Math.Max(-1, Math.Min(1, wert));
            e.KommentarAusfall = Bereinige(kommentar);
        }

        /// <summary>„Zurück auf Standard“ für die ausfallende Lehrkraft: Sondergrund weg, Wert laut Anlass.</summary>
        public static void SetzeAusfallStandard(VertretungsEintrag e)
        {
            e.SondergrundAusfall = false;
            e.KommentarAusfall = "";
            e.ZaehlwertAusfall = AusfallWert(e.AnlassAusfall);
        }

        /// <summary>Alle Angaben zur ausfallenden Lehrkraft löschen (die Stunde hält keine andere Lehrkraft mehr).</summary>
        public static void LoescheAusfall(VertretungsEintrag e)
        {
            e.AnlassAusfall = null;
            e.ZaehlwertAusfall = 0;
            e.SondergrundAusfall = false;
            e.KommentarAusfall = "";
        }

        /// <summary>Hat der Eintrag Angaben zur ausfallenden Lehrkraft (Anlass oder Sondergrund)?</summary>
        public static bool HatAusfallAngaben(VertretungsEintrag? e)
            => e != null && HatAusfall(e) && (e.AnlassAusfall != null || e.SondergrundAusfall);

        /// <summary>
        /// 296: Angaben zur ausfallenden Lehrkraft für eine neue Buchung derselben Stunde. Hält danach weiter eine andere
        /// Lehrkraft (z. B. Vertretung → Betreuung, anderer Vertreter), bleiben Anlass und Sondergrund aus
        /// <paramref name="alt"/>; sonst gilt <paramref name="vorgabe"/>. Hält keine andere Lehrkraft mehr, fallen sie weg.
        /// Liefert true, wenn dabei Angaben von <paramref name="alt"/> wegfallen (Hinweis in der Statuszeile).
        /// </summary>
        public static bool UebernimmAusfall(VertretungsEintrag? alt, VertretungsEintrag neu, AusfallAnlass vorgabe)
        {
            if (!HatAusfall(neu))
            {
                LoescheAusfall(neu);
                return HatAusfallAngaben(alt);
            }
            if (alt != null && !ReferenceEquals(alt, neu) && HatAusfall(alt))
            {
                neu.AnlassAusfall = alt.AnlassAusfall;
                neu.SondergrundAusfall = alt.SondergrundAusfall;
                neu.KommentarAusfall = alt.SondergrundAusfall ? alt.KommentarAusfall ?? "" : "";
                neu.ZaehlwertAusfall = alt.SondergrundAusfall ? alt.ZaehlwertAusfall : AusfallWert(alt.AnlassAusfall);
                if (neu.AnlassAusfall == null && !neu.SondergrundAusfall) SetzeAusfall(neu, vorgabe);
                return false;
            }
            neu.SondergrundAusfall = false;
            neu.KommentarAusfall = "";
            SetzeAusfall(neu, vorgabe);
            return false;
        }

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
            DateTime datum, int stunde, VertretungsDaten daten, PlanDaten? plan, IEnumerable<string>? klassen = null)
        {
            if (art == Vertretungsart.Entfall) return EntfallWert(anlass, daten, datum, klassen);
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
            => StandardWert(e.Art, e.Anlass, e.VertretungsLehrer, e.Datum, e.Stunde, daten, plan, e.Klassen);

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

        /// <summary>
        /// 289: Kurztext der Zählung mit Anlass, z. B. „−1 (Klasse fehlt)“, „0 (Lehrkraft fehlt)“, „+1 (Sondergrund)“; bei
        /// vertretenen Stunden beide Teile, z. B. „+1 L5 · 0 L1 (fehlt)“.
        /// </summary>
        public static string KurzText(VertretungsEintrag e)
        {
            if (e.Art == Vertretungsart.Entfall)
                return WertText(e.ZaehlwertSnapshot) + (e.Sondergrund ? " (Sondergrund)" : $" ({AnlassKurz(e.Anlass)})");
            if (HatAusfall(e))
                return $"{WertText(e.ZaehlwertSnapshot)} {Zaehlperson(e)}" + (e.Sondergrund ? " (Sondergrund)" : "") +
                       $" · {WertText(e.ZaehlwertAusfall)} {(e.OriginalLehrer ?? "").Trim()} " +
                       (e.SondergrundAusfall ? "(Sondergrund)" : $"({AusfallKurz(e.AnlassAusfall)})");
            return WertText(e.ZaehlwertSnapshot) + (e.Sondergrund ? " (Sondergrund)" : "");
        }

        /// <summary>Tooltip-Text: Zählwert(e), für wen, Anlass, ggf. Sondergrund mit Kommentar (289: beide Teile).</summary>
        public static string TippText(VertretungsEintrag e)
        {
            string wer = Zaehlperson(e);
            string t = $"Zählung: {WertText(e.ZaehlwertSnapshot)}" + (wer.Length > 0 ? $" für {wer}" : "");
            if (e.Art == Vertretungsart.Entfall) t += $" (Entfall, {AnlassText(e.Anlass)})";
            if (e.Sondergrund) t += "\nSondergrund: " + (string.IsNullOrWhiteSpace(e.Zaehlkommentar) ? "–" : e.Zaehlkommentar);
            if (HatAusfall(e))
            {
                t += $"\nAusfall: {WertText(e.ZaehlwertAusfall)} für {(e.OriginalLehrer ?? "").Trim()} ({AusfallText(e.AnlassAusfall)})";
                if (e.SondergrundAusfall)
                    t += "\nSondergrund Ausfall: " + (string.IsNullOrWhiteSpace(e.KommentarAusfall) ? "–" : e.KommentarAusfall);
            }
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
            /// <summary>„gebucht“, „Kalender“ (freie Zeit einer Klasse, 276) bzw. „vertreten“ (ausfallende Lehrkraft, 294).</summary>
            public string Quelle { get; set; } = "gebucht";
        }

        /// <summary>
        /// Alle Entfälle (Verlegungen sind keine – die Stunde findet statt), nach Datum und Stunde: gebuchte, freie Zeiten
        /// einzelner Klassen laut Kalender (276; abgeleitete aus <see cref="KalenderEntfaelle.MitAbgeleiteten"/>, Quelle
        /// „Kalender“) und – 294 – vertretene Stunden, bei denen die ausfallende Lehrkraft nicht 0 zählt („nicht
        /// verhindert“ oder Sondergrund; Quelle „vertreten“).
        /// </summary>
        public static List<EntfallZeile> EntfallZeilen(IEnumerable<VertretungsEintrag> eintraege)
            => eintraege.Where(e => e.Art == Vertretungsart.Entfall
                                    || (HatAusfall(e) && (e.SondergrundAusfall || e.ZaehlwertAusfall != 0)))
                .Select(e => e.Art == Vertretungsart.Entfall
                    ? new EntfallZeile
                    {
                        Datum = e.Datum.Date,
                        Stunde = e.Stunde,
                        Klassen = string.Join(", ", e.Klassen),
                        Fach = e.Fach,
                        Lehrkraft = (e.OriginalLehrer ?? "").Trim(),
                        Anlass = AnlassText(e.Anlass),
                        Grund = e.Anlass == EntfallAnlass.FreieZeit ? (e.Bemerkung ?? "").Trim() : GrundText(e.Grund),
                        Zaehlwert = e.ZaehlwertSnapshot,
                        Sondergrund = e.Sondergrund,
                        Kommentar = e.Sondergrund ? e.Zaehlkommentar ?? "" : "",
                        Quelle = e.Anlass == EntfallAnlass.FreieZeit ? "Kalender" : "gebucht"
                    }
                    : new EntfallZeile
                    {
                        Datum = e.Datum.Date,
                        Stunde = e.Stunde,
                        Klassen = string.Join(", ", e.Klassen),
                        Fach = e.Fach,
                        Lehrkraft = (e.OriginalLehrer ?? "").Trim(),
                        Anlass = $"{AusfallText(e.AnlassAusfall)} ({Dauervertretung.ArtText(e.Art)} {Zaehlperson(e)})",
                        Grund = GrundText(e.Grund),
                        Zaehlwert = e.ZaehlwertAusfall,
                        Sondergrund = e.SondergrundAusfall,
                        Kommentar = e.SondergrundAusfall ? e.KommentarAusfall ?? "" : "",
                        Quelle = "vertreten"
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
