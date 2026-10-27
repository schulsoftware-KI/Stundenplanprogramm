#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  Schuljahreskalender
    //
    //  Führt die DATUMSEBENE ein, die das Grundprogramm (reiner Wochenraster)
    //  nicht kennt. Hält Start/Ende des Schuljahres und die unterrichtsfreien
    //  Zeiträume (Ferien, Feiertage, bewegliche Ferientage, sonstige freie
    //  Tage) und beantwortet: An welchen Daten findet überhaupt Unterricht –
    //  und damit Vertretung/Zählung – statt?
    //
    //  Das Mapping Datum -> WTag des Rasters: Der Wochenplan benutzt
    //  Wochentagsbezeichnungen (z.B. "Montag" ... "Freitag"). Diese Klasse
    //  bildet ein Datum auf den passenden Raster-WTag ab. Da im Grundprogramm
    //  die exakten Bezeichnungen frei wählbar sind, wird die Zuordnung über
    //  den DayOfWeek-Index und die REIHENFOLGE der im Raster vorkommenden Tage
    //  aufgelöst (RasterTage), statt Strings fest zu verdrahten.
    // =====================================================================

    public class SchuljahrKalender
    {
        /// <summary>Anzeigename, z.B. "2026/27".</summary>
        public string Schuljahr { get; set; } = "";

        public DateTime Start { get; set; }   // erster möglicher Schultag (inkl.)
        public DateTime Ende { get; set; }    // letzter möglicher Schultag (inkl.)

        public List<FreieZeit> FreieZeiten { get; set; } = new();

        /// <summary>
        /// Die Wochentage, die im Stundenraster vorkommen, in Planreihenfolge –
        /// z.B. ["Montag","Dienstag","Mittwoch","Donnerstag","Freitag"]. Wird aus
        /// den ZeitSlots des Grundplans übernommen (ZeitSlot.WTag, distinct).
        /// Default deckt die übliche 5-Tage-Woche ab.
        /// </summary>
        public List<string> RasterTage { get; set; } = new()
        {
            "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag"
        };

        // -----------------------------------------------------------------

        /// <summary>
        /// Ist an diesem Datum (optional: für diese Klasse) schulfrei?
        /// Frei ist ein Tag, wenn er außerhalb [Start,Ende] liegt, auf einen
        /// Wochentag fällt, der nicht im Raster vorkommt (z.B. Wochenende), oder
        /// von einer passenden FreieZeit abgedeckt wird.
        /// </summary>
        public bool IstFrei(DateTime datum, string? klasse = null)
        {
            var d = datum.Date;

            if (d < Start.Date || d > Ende.Date) return true;
            if (!IstRasterTag(d)) return true;

            foreach (var fz in FreieZeiten)
            {
                if (!fz.Enthaelt(d)) continue;
                if (fz.GiltFuerGanzeSchule) return true;
                if (klasse != null && fz.BetroffeneKlassen
                        .Contains(klasse, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Gegenstück zu <see cref="IstFrei"/>.</summary>
        public bool IstSchultag(DateTime datum, string? klasse = null)
            => !IstFrei(datum, klasse);

        /// <summary>
        /// Fällt dieses Datum auf einen Wochentag, der im Raster vorkommt?
        /// Vergleich sprachunabhängig über den DayOfWeek-Index der RasterTage.
        /// </summary>
        public bool IstRasterTag(DateTime datum)
            => RasterTageAlsIndex().Contains((int)datum.DayOfWeek);

        /// <summary>
        /// Liefert den Raster-WTag-String für ein Datum (z.B. "Mittwoch"), oder
        /// null, wenn der Wochentag nicht im Raster vorkommt.
        /// </summary>
        public string? RasterTagFuer(DateTime datum)
        {
            int idx = (int)datum.DayOfWeek;
            foreach (var tag in RasterTage)
                if (WochentagIndex(tag) == idx)
                    return tag;
            return null;
        }

        /// <summary>
        /// Alle Schultage (für optionale Klasse) im angegebenen Bereich, der
        /// standardmäßig das ganze Schuljahr umfasst.
        /// </summary>
        public IEnumerable<DateTime> Schultage(
            DateTime? von = null, DateTime? bis = null, string? klasse = null)
        {
            var start = (von ?? Start).Date;
            var ende = (bis ?? Ende).Date;
            for (var d = start; d <= ende; d = d.AddDays(1))
                if (IstSchultag(d, klasse))
                    yield return d;
        }

        /// <summary>Anzahl Schultage im Bereich (für Soll-/Quotenberechnung).</summary>
        public int AnzahlSchultage(
            DateTime? von = null, DateTime? bis = null, string? klasse = null)
            => Schultage(von, bis, klasse).Count();

        // -----------------------------------------------------------------
        //  Wochentags-Mapping (sprachunabhängig)
        // -----------------------------------------------------------------

        private HashSet<int> RasterTageAlsIndex()
            => RasterTage.Select(WochentagIndex).Where(i => i >= 0).ToHashSet();

        /// <summary>
        /// Ordnet eine Wochentagsbezeichnung dem DayOfWeek-Index (0=Sonntag ..
        /// 6=Samstag) zu. Erkennt deutsche Namen und gängige Abkürzungen;
        /// Groß/Klein egal. Unbekannt -> -1.
        /// </summary>
        public static int WochentagIndex(string wtag)
        {
            if (string.IsNullOrWhiteSpace(wtag)) return -1;
            string t = wtag.Trim().ToLowerInvariant();

            if (t.StartsWith("so")) return (int)DayOfWeek.Sunday;
            if (t.StartsWith("mo")) return (int)DayOfWeek.Monday;
            if (t.StartsWith("di")) return (int)DayOfWeek.Tuesday;
            if (t.StartsWith("mi")) return (int)DayOfWeek.Wednesday;
            if (t.StartsWith("do")) return (int)DayOfWeek.Thursday;
            if (t.StartsWith("fr")) return (int)DayOfWeek.Friday;
            if (t.StartsWith("sa")) return (int)DayOfWeek.Saturday;
            return -1;
        }
    }
}
