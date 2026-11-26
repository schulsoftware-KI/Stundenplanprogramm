#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  V4b (276) – freie Zeiten einzelner Klassen als abgeleitete Entfälle
    //
    //  Ein Kalendereintrag mit Klassen (Button 22, z. B. „Stufenfahrt Q1“) macht jede Stunde des Grundplans an
    //  diesem Tag, deren Klassen ALLE frei sind, zu einem Entfall der Lehrkraft laut Plan mit Anlass „freie Zeit“
    //  und dem Wert des Kalendereintrags („Entfall zählt“: 0 oder −1). Diese Entfälle werden aus Kalender und Plan
    //  ABGELEITET, nicht gespeichert – so stimmen sie nach jeder Kalenderänderung. Gespeichert wird nur, was der
    //  Planer an einer solchen Stunde selbst tut: eine Buchung (geht immer vor) oder ein Sondergrund (dann ein
    //  Eintrag Art Entfall, Anlass FreieZeit, Sondergrund – er geht wie die abgeleiteten nie in die App).
    //  Einträge für die ganze Schule (Ferien, Feiertage …) erzeugen keine Zeilen (an diesen Tagen gibt es keine
    //  Stunden).
    // =====================================================================

    /// <summary>Freie Zeit einer Stunde laut Kalender: Bezeichnung und Zählwert.</summary>
    public sealed class KalenderFrei
    {
        public string Bezeichnung { get; set; } = "";
        /// <summary>0 oder −1 (bei mehreren Einträgen der kleinste).</summary>
        public int Wert { get; set; }
    }

    public static class KalenderEntfaelle
    {
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

        /// <summary>Zählt eine Stunde mit diesem Fach und diesen Klassen als Unterricht einer Lerngruppe (276)?</summary>
        public static bool IstUnterricht(string? fach, IReadOnlyCollection<string>? klassen)
            => klassen != null && klassen.Any(k => !string.IsNullOrWhiteSpace(k))
               && !TeilInfo.IstPseudoFach(fach) && !TeilInfo.IstVberFach(fach) && !TeilInfo.IstUvFach(fach);

        /// <summary>
        /// Sind am <paramref name="datum"/> ALLE <paramref name="klassen"/> laut einem Kalendereintrag mit Klassen frei?
        /// Dann Bezeichnung(en) und Zählwert (bei mehreren Einträgen „A / B“, der kleinste Wert), sonst null. Tage, an
        /// denen die ganze Schule frei hat, liefern null (dort gibt es keine Stunden).
        /// </summary>
        public static KalenderFrei? FreiFuer(SchuljahrKalender kal, DateTime datum, IEnumerable<string>? klassen)
        {
            if (kal == null || klassen == null) return null;
            var kl = klassen.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(Cmp).ToList();
            if (kl.Count == 0) return null;
            var d = datum.Date;
            if (kal.FreieZeiten.Any(f => f.GiltFuerGanzeSchule && f.Enthaelt(d))) return null;

            var treffer = new List<FreieZeit>();
            foreach (var k in kl)
            {
                var fz = kal.FreieZeiten
                    .Where(f => !f.GiltFuerGanzeSchule && f.Enthaelt(d) && f.BetroffeneKlassen.Contains(k, Cmp))
                    .OrderBy(f => f.Von)
                    .FirstOrDefault();
                if (fz == null) return null;   // eine Klasse hat Unterricht → die Stunde findet statt
                if (!treffer.Contains(fz)) treffer.Add(fz);
            }
            return new KalenderFrei
            {
                Bezeichnung = string.Join(" / ", treffer.Select(Text).Distinct(Cmp)),
                Wert = treffer.Min(f => Norm(f.EntfallZaehlwert))
            };
        }

        /// <summary>Nur 0 oder −1 (andere Werte aus der Datei werden so gelesen).</summary>
        public static int Norm(int wert) => wert < 0 ? -1 : 0;

        private static string Text(FreieZeit f)
            => !string.IsNullOrWhiteSpace(f.Bezeichnung) ? f.Bezeichnung.Trim() : f.Typ switch
            {
                FreieZeitTyp.SchulfreiSonstiges => "schulfrei",
                FreieZeitTyp.BeweglicherFerientag => "beweglicher Ferientag",
                _ => f.Typ.ToString()
            };

        /// <summary>
        /// Alle abgeleiteten Entfälle des Schuljahres (276): je Tag mit einer freien Zeit einzelner Klassen jede Stunde
        /// des Grundplans <paramref name="plan"/>, deren Klassen alle frei sind und für die nichts gespeichert ist
        /// (Schlüssel wie beim Buchen: Datum, Stunde, UNr, Lehrkraft laut Plan). Werden nicht gespeichert.
        /// </summary>
        public static List<VertretungsEintrag> Abgeleitete(VertretungsDaten daten, PlanDaten? plan)
        {
            var result = new List<VertretungsEintrag>();
            if (daten == null || plan == null) return result;
            var kal = daten.Kalender;
            var mitKlassen = kal.FreieZeiten.Where(f => !f.GiltFuerGanzeSchule).ToList();
            if (mitKlassen.Count == 0) return result;

            // Tage mit einer freien Zeit einzelner Klassen (im Schuljahr, nur Schultage der ganzen Schule).
            var tage = new SortedSet<DateTime>();
            foreach (var fz in mitKlassen)
            {
                var von = fz.Von.Date;
                var bis = fz.Bis.Date;
                if (kal.Start != default && von < kal.Start.Date) von = kal.Start.Date;
                if (kal.Ende != default && bis > kal.Ende.Date) bis = kal.Ende.Date;
                int n = 0;
                for (var d = von; d <= bis && n < 400; d = d.AddDays(1), n++)
                    if (kal.IstSchultag(d)) tage.Add(d);
            }
            if (tage.Count == 0) return result;

            var gebucht = new HashSet<string>(daten.Vertretungen.Select(v => Schluessel(v.Datum, v.Stunde, v.UNr, v.OriginalLehrer)),
                StringComparer.Ordinal);

            foreach (var d in tage)
            {
                string? wtag = kal.RasterTagFuer(d);
                if (wtag == null) continue;
                foreach (var kv in plan.Belegung.Where(x => string.Equals(x.Key.WTag, wtag, StringComparison.OrdinalIgnoreCase))
                                                .OrderBy(x => x.Key.Stunde))
                {
                    int stunde = kv.Key.Stunde;
                    foreach (int unr in kv.Value)
                    {
                        if (!plan.Unterrichte.TryGetValue(unr, out var teile)) continue;
                        foreach (var teil in teile)
                        {
                            if (!IstUnterricht(teil.Fach, teil.Klassen)) continue;
                            if (gebucht.Contains(Schluessel(d, stunde, unr, teil.Lehrer))) continue;
                            var frei = FreiFuer(kal, d, teil.Klassen);
                            if (frei == null) continue;
                            result.Add(Eintrag(d, stunde, unr, teil.Lehrer, teil.Fach, teil.Klassen, frei));
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>Gebuchte Einträge und die abgeleiteten Entfälle zusammen – Grundlage für Zähler, Saldo und Liste „Entfälle“.</summary>
        public static List<VertretungsEintrag> MitAbgeleiteten(VertretungsDaten daten, PlanDaten? plan)
        {
            var l = new List<VertretungsEintrag>(daten.Vertretungen);
            l.AddRange(Abgeleitete(daten, plan));
            return l;
        }

        /// <summary>
        /// Eintrag einer freien Stunde (abgeleitet bzw. als Vorlage für einen Sondergrund): Art Entfall, Anlass FreieZeit,
        /// Wert des Kalendereintrags, Bezeichnung der freien Zeit in „Bemerkung“ (für die Liste „Entfälle“).
        /// </summary>
        public static VertretungsEintrag Eintrag(DateTime datum, int stunde, int unr, string lehrer, string fach,
            IEnumerable<string> klassen, KalenderFrei frei)
            => new()
            {
                Datum = datum.Date, Stunde = stunde, UNr = unr,
                Art = Vertretungsart.Entfall, Grund = VertretungsGrund.Sonstiges,
                OriginalLehrer = lehrer ?? "", Klassen = new List<string>(klassen), Fach = fach ?? "",
                ZaehlwertSnapshot = frei.Wert,
                Anlass = EntfallAnlass.FreieZeit,
                Bemerkung = frei.Bezeichnung
            };

        private static string Schluessel(DateTime datum, int stunde, int unr, string? lehrer)
            => $"{datum:yyyyMMdd}|{stunde}|{unr}|{(lehrer ?? "").Trim().ToUpperInvariant()}";
    }
}
