#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  VertretungsService – die Fachlogik von M2.
    //
    //  1) BetroffeneInstanzen: Aus einer erfassten Abwesenheit (Lehrer/Klasse/
    //     Raum, von–bis) und dem Grundplan × Kalender die konkreten
    //     Stunden-Instanzen bilden, die zu vertreten/entfallen sind. Bei
    //     gekoppelten Kursen ist nur der Teil der betroffenen Lehrkraft
    //     betroffen – die Parallelkurse laufen weiter.
    //
    //  2) Vorschlaege: Für eine Instanz die geeigneten Vertretungslehrkräfte
    //     ranken. Es gibt DREI Gruppen, in dieser Reihenfolge:
    //
    //       GRUPPE 0 – "UV": Lehrkräfte, die in genau diesem Slot eine
    //         platzierte UV-Stunde (Fach "UV"/"UV1"/…) haben. Sie stehen GANZ
    //         OBEN. Übernehmen sie die Vertretung, wird ihre UV-Stunde ersetzt
    //         – der Vertretungszähler wird NICHT erhöht.
    //
    //       GRUPPE 1 – "Vber": Lehrkräfte, die in genau diesem Slot eine
    //         platzierte Vber-Stunde (Bereitschaft) haben. Übernehmen sie die
    //         Vertretung, wird ihre Vber-Stunde durch die zu vertretende Stunde
    //         ersetzt – der Vertretungszähler wird trotzdem erhöht.
    //
    //       GRUPPE 2 – "NuHo": Lehrkräfte mit einer NUTZBAREN Hohlstunde in
    //         diesem Slot (Kriterien in PlanDaten.HatNutzbareHohlstunde:
    //         Stunden 2–5, echte Lücke innerhalb der Tagesspanne, Std.Folge
    //         eingehalten, nur vertretungsbereite Lehrkräfte).
    //
    //     Lehrkräfte, die im Slot ECHT unterrichten, und die abwesende
    //     Lehrkraft selbst sind nie Kandidaten. Innerhalb jeder Gruppe wird
    //     sortiert nach: fachnah, fairer Ausgleich (kleiner Zähler zuerst),
    //     Name.
    // =====================================================================

    /// <summary>Herkunft eines Vorschlags.</summary>
    public enum VorschlagQuelle { Uv, Vber, NuHo }

    /// <summary>
    /// Aktuelle Auswahl, die zwischen „Vertretung erfassen" und dem
    /// Tagesvertretungseditor synchronisiert wird. Nur gesetzte (nicht-null)
    /// Felder werden vom Empfänger übernommen.
    /// </summary>
    public class VertretungsAuswahl
    {
        public DateTime? Datum { get; set; }
        public string? Lehrer { get; set; }
        public string? Klasse { get; set; }
    }

    /// <summary>
    /// Gemeinsame Schnittstelle der beiden Vertretungsfenster für die Kopplung:
    /// Auswahl übernehmen und Daten neu laden. So kann jedes Fenster sein
    /// Partnerfenster direkt ansprechen (gleicher UI-Thread).
    /// </summary>
    public interface IVertretungsFenster
    {
        /// <summary>Übernimmt die Auswahl (Datum/Lehrer/Klasse) vom Partnerfenster.</summary>
        void UebernehmeAuswahl(VertretungsAuswahl auswahl);

        /// <summary>Lädt die Vertretungsdaten neu aus der Datei und zeichnet neu.</summary>
        void NeuLaden();
    }

    /// <summary>Ein gerankter Vertretungsvorschlag für eine Stunde.</summary>
    public class VertreterVorschlag
    {
        public string Lehrer { get; set; } = "";
        public bool Fachnah { get; set; }
        public bool Vertretungsbereit { get; set; }
        public int AktuellerZaehler { get; set; }   // bisher gehaltene (gewichtete) Vertretungen

        /// <summary>Herkunft: platzierte Vber-Stunde oder nutzbare Hohlstunde.</summary>
        public VorschlagQuelle Quelle { get; set; } = VorschlagQuelle.NuHo;

        /// <summary>Kurzbegründung für die Anzeige.</summary>
        public string Begruendung
        {
            get
            {
                var teile = new List<string>();
                teile.Add(Quelle switch
                {
                    VorschlagQuelle.Uv   => "UV → wird ersetzt, ohne Zähler",
                    VorschlagQuelle.Vber => "Vber → wird durch Vertretung ersetzt",
                    _                    => "nutzbare Hohlstunde"
                });
                teile.Add(Fachnah ? "fachgerecht" : "fachfremd");
                teile.Add($"Zähler {AktuellerZaehler}");
                return string.Join(", ", teile);
            }
        }
    }

    public static class VertretungsService
    {
        /// <summary>
        /// Bildet die betroffenen Stunden-Instanzen einer Abwesenheit über ihren
        /// Zeitraum. Am ersten Tag erst ab VonStunde, am letzten Tag bis
        /// BisStunde, an Tagen dazwischen ganztägig. Werte &lt;= 0 bei Von/Bis
        /// Stunde gelten als unbegrenzt.
        /// </summary>
        public static List<StundenInstanz> BetroffeneInstanzen(
            Abwesenheit abw, PlanDaten plan, SchuljahrKalender kal)
        {
            var result = new List<StundenInstanz>();
            if (abw == null) return result;

            // Für jeden Schultag im Zeitraum (Klassenbezug beachtet Klassen-
            // freie Zeiten; bei Lehrer/Raum zählt die ganze Schule).
            string? klasseFilter = abw.Subjekt == AbwesenheitsSubjekt.Klasse ? abw.Name : null;

            foreach (var datum in kal.Schultage(abw.VonDatum, abw.BisDatum, klasseFilter))
            {
                string? wtag = kal.RasterTagFuer(datum);
                if (wtag == null) continue;

                int minSt = (datum.Date == abw.VonDatum.Date && abw.VonStunde > 0)
                    ? abw.VonStunde : int.MinValue;
                int maxSt = (datum.Date == abw.BisDatum.Date && abw.BisStunde > 0)
                    ? abw.BisStunde : int.MaxValue;

                // Alle Slots dieses Wochentags.
                foreach (var kv in plan.Belegung)
                {
                    if (!string.Equals(kv.Key.WTag, wtag, StringComparison.OrdinalIgnoreCase)) continue;
                    int stunde = kv.Key.Stunde;
                    if (stunde < minSt || stunde > maxSt) continue;

                    foreach (int unr in kv.Value)
                    {
                        if (!plan.Unterrichte.TryGetValue(unr, out var teile)) continue;
                        foreach (var teil in teile)
                        {
                            // Pseudofächer (PSF…) müssen nicht vertreten werden.
                            if (teil.IstPseudo) continue;
                            if (!Betrifft(abw, teil)) continue;
                            result.Add(new StundenInstanz
                            {
                                Datum = datum,
                                Stunde = stunde,
                                UNr = unr,
                                Lehrer = teil.Lehrer,
                                Fach = teil.Fach,
                                Klassen = new List<string>(teil.Klassen),
                                Raum = teil.Stammraum ?? teil.Fachraum
                            });
                        }
                    }
                }
            }

            return result
                .OrderBy(i => i.Datum)
                .ThenBy(i => i.Stunde)
                .ThenBy(i => string.Join(",", i.Klassen))
                .ToList();
        }

        private static bool Betrifft(Abwesenheit abw, TeilInfo teil)
        {
            switch (abw.Subjekt)
            {
                case AbwesenheitsSubjekt.Lehrer:
                    return string.Equals(teil.Lehrer, abw.Name, StringComparison.OrdinalIgnoreCase);
                case AbwesenheitsSubjekt.Klasse:
                    return teil.Klassen.Contains(abw.Name, StringComparer.OrdinalIgnoreCase);
                case AbwesenheitsSubjekt.Raum:
                    return string.Equals(teil.Fachraum, abw.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(teil.Stammraum, abw.Name, StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Gerankte Vertretungsvorschläge für eine Instanz. Es werden drei
        /// Gruppen gebildet und in dieser Reihenfolge ausgegeben:
        ///   0) Lehrkräfte mit platzierter UV-Stunde im Slot (Quelle = Uv) –
        ///      ganz oben, Einsatz erhöht den Zähler NICHT,
        ///   1) Lehrkräfte mit platzierter Vber-Stunde im Slot (Quelle = Vber),
        ///   2) Lehrkräfte mit nutzbarer Hohlstunde im Slot (Quelle = NuHo).
        /// Innerhalb jeder Gruppe: fachnah, kleiner Zähler, Name.
        /// Echt unterrichtende Lehrkräfte und die abwesende Lehrkraft selbst
        /// sind ausgeschlossen. <paramref name="zaehlerProLehrer"/> liefert je
        /// Lehrkraft den bisherigen (gewichteten) Vertretungszähler für den
        /// fairen Ausgleich (fehlt eine Lehrkraft, gilt 0).
        /// </summary>
        public static List<VertreterVorschlag> Vorschlaege(
            StundenInstanz inst, PlanDaten plan, SchuljahrKalender kal,
            IDictionary<string, int>? zaehlerProLehrer = null)
        {
            var result = new List<VertreterVorschlag>();
            string? wtag = kal.RasterTagFuer(inst.Datum);
            if (wtag == null) return result;

            int Zaehler(string l)
                => (zaehlerProLehrer != null && zaehlerProLehrer.TryGetValue(l, out int zz)) ? zz : 0;

            bool IstOrig(string l) => string.Equals(l, inst.Lehrer, StringComparison.OrdinalIgnoreCase);
            var echtImSlot = plan.LehrerImSlot(wtag, inst.Stunde);
            var belegt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // --- Gruppe 0: platzierte UV-Stunde (ganz oben, ohne Zählererhöhung) ---
            foreach (var lehrer in plan.AlleLehrer)
            {
                if (IstOrig(lehrer)) continue;
                if (echtImSlot.Contains(lehrer)) continue;          // unterrichtet echt -> nie frei
                if (!plan.HatUvImSlot(lehrer, wtag, inst.Stunde)) continue;
                belegt.Add(lehrer);
                result.Add(new VertreterVorschlag
                {
                    Lehrer = lehrer,
                    Quelle = VorschlagQuelle.Uv,
                    Fachnah = plan.Fachnah(lehrer, inst.Fach),
                    Vertretungsbereit = plan.Vertretungsbereit.Contains(lehrer),
                    AktuellerZaehler = Zaehler(lehrer)
                });
            }

            // --- Gruppe 1: platzierte Vber-Stunde in genau diesem Slot ---
            foreach (var lehrer in plan.AlleLehrer)
            {
                if (IstOrig(lehrer)) continue;
                if (echtImSlot.Contains(lehrer)) continue;          // unterrichtet echt -> nie frei
                if (belegt.Contains(lehrer)) continue;              // schon in Gruppe 0
                if (!plan.HatVberImSlot(lehrer, wtag, inst.Stunde)) continue;
                belegt.Add(lehrer);
                result.Add(new VertreterVorschlag
                {
                    Lehrer = lehrer,
                    Quelle = VorschlagQuelle.Vber,
                    Fachnah = plan.Fachnah(lehrer, inst.Fach),
                    Vertretungsbereit = true,   // Vber-Stunde => vertretungsbereit
                    AktuellerZaehler = Zaehler(lehrer)
                });
            }

            // --- Gruppe 2: nutzbare Hohlstunde in diesem Slot ---
            foreach (var lehrer in plan.AlleLehrer)
            {
                if (IstOrig(lehrer)) continue;
                if (echtImSlot.Contains(lehrer)) continue;
                if (belegt.Contains(lehrer)) continue;              // schon in Gruppe 0/1
                if (!plan.HatNutzbareHohlstunde(lehrer, wtag, inst.Stunde)) continue;
                result.Add(new VertreterVorschlag
                {
                    Lehrer = lehrer,
                    Quelle = VorschlagQuelle.NuHo,
                    Fachnah = plan.Fachnah(lehrer, inst.Fach),
                    Vertretungsbereit = plan.Vertretungsbereit.Contains(lehrer),
                    AktuellerZaehler = Zaehler(lehrer)
                });
            }

            // Gruppen-Reihenfolge UV < Vber < NuHo, dann fachnah/Zähler/Name.
            static int Rang(VorschlagQuelle q) => q switch
            {
                VorschlagQuelle.Uv   => 0,
                VorschlagQuelle.Vber => 1,
                _                    => 2
            };
            return result
                .OrderBy(v => Rang(v.Quelle))
                .ThenByDescending(v => v.Fachnah)
                .ThenBy(v => v.AktuellerZaehler)
                .ThenBy(v => v.Lehrer, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
