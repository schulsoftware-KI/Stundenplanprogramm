#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  Import von Feiertagen und Ferien – Default NRW
    //
    //  FEIERTAGE sind deterministisch berechenbar (feste Daten + von Ostern
    //  abhängige bewegliche Feste) und werden daher exakt erzeugt – ohne
    //  externe Datenquelle, auch offline. Grundlage ist die Oster-Berechnung
    //  nach dem anonymen gregorianischen Algorithmus (Meeus/Jones/Butcher).
    //
    //  FERIEN werden vom Land festgelegt und ändern sich jährlich; sie lassen
    //  sich NICHT berechnen. Sie werden deshalb aus einer JSON-Datei importiert
    //  (FerienQuelle), die pro Schuljahr die offiziellen Zeiträume enthält.
    //  So bleibt der Import "per NRW-Default", ohne dass im Code geratene Daten
    //  stehen, die falsch sein könnten. Die JSON-Datei wird mit den amtlichen
    //  NRW-Terminen befüllt (Schulministerium NRW).
    // =====================================================================

    public enum Bundesland
    {
        NRW,
        // weitere Länder später ergänzbar
    }

    public static class FeiertageImport
    {
        // -----------------------------------------------------------------
        //  Feiertage (berechnet)
        // -----------------------------------------------------------------

        /// <summary>
        /// Gesetzliche Feiertage des Bundeslands für ein Kalenderjahr als
        /// FreieZeit-Einträge (Typ = Feiertag, je ein Tag).
        /// </summary>
        public static List<FreieZeit> Feiertage(int jahr, Bundesland land = Bundesland.NRW)
        {
            var ostern = OsterSonntag(jahr);
            var liste = new List<FreieZeit>();

            void Add(DateTime tag, string name)
                => liste.Add(new FreieZeit
                {
                    Typ = FreieZeitTyp.Feiertag,
                    Von = tag.Date,
                    Bis = tag.Date,
                    Bezeichnung = name
                });

            // Bundesweit + in NRW gesetzlich:
            Add(new DateTime(jahr, 1, 1), "Neujahr");
            Add(ostern.AddDays(-2), "Karfreitag");
            Add(ostern.AddDays(1), "Ostermontag");
            Add(new DateTime(jahr, 5, 1), "Tag der Arbeit");
            Add(ostern.AddDays(39), "Christi Himmelfahrt");
            Add(ostern.AddDays(50), "Pfingstmontag");
            Add(new DateTime(jahr, 10, 3), "Tag der Deutschen Einheit");
            Add(new DateTime(jahr, 12, 25), "1. Weihnachtstag");
            Add(new DateTime(jahr, 12, 26), "2. Weihnachtstag");

            if (land == Bundesland.NRW)
            {
                Add(ostern.AddDays(60), "Fronleichnam");      // in NRW gesetzlich
                Add(new DateTime(jahr, 11, 1), "Allerheiligen"); // in NRW gesetzlich
            }

            return liste.OrderBy(f => f.Von).ToList();
        }

        /// <summary>
        /// Feiertage für ein SCHULJAHR, das über den Jahreswechsel läuft:
        /// liefert die in [von, bis] fallenden Feiertage beider Kalenderjahre.
        /// </summary>
        public static List<FreieZeit> FeiertageImZeitraum(
            DateTime von, DateTime bis, Bundesland land = Bundesland.NRW)
        {
            var result = new List<FreieZeit>();
            for (int jahr = von.Year; jahr <= bis.Year; jahr++)
                result.AddRange(Feiertage(jahr, land));
            return result
                .Where(f => f.Von.Date >= von.Date && f.Von.Date <= bis.Date)
                .OrderBy(f => f.Von)
                .ToList();
        }

        /// <summary>
        /// Oster-Sonntag (gregorianisch) nach dem anonymen Algorithmus
        /// (Meeus/Jones/Butcher). Gültig für das gregorianische Kalendersystem.
        /// </summary>
        public static DateTime OsterSonntag(int jahr)
        {
            int a = jahr % 19;
            int b = jahr / 100;
            int c = jahr % 100;
            int d = b / 4;
            int e = b % 4;
            int f = (b + 8) / 25;
            int g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30;
            int i = c / 4;
            int k = c % 4;
            int l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451;
            int monat = (h + l - 7 * m + 114) / 31;      // 3 = März, 4 = April
            int tag = ((h + l - 7 * m + 114) % 31) + 1;
            return new DateTime(jahr, monat, tag);
        }

        // -----------------------------------------------------------------
        //  Ferien (aus JSON importiert)
        // -----------------------------------------------------------------

        /// <summary>Eine Ferienperiode, wie sie in der JSON-Quelle steht.</summary>
        public class FerienPeriode
        {
            public string Name { get; set; } = "";
            public string Von { get; set; } = "";   // "yyyy-MM-dd"
            public string Bis { get; set; } = "";   // "yyyy-MM-dd"
        }

        /// <summary>Oberste Struktur der Ferien-JSON.</summary>
        public class FerienQuelle
        {
            public string Bundesland { get; set; } = "NRW";
            // Schlüssel = Schuljahr ("2026/27"); Wert = Liste der Ferienperioden.
            public Dictionary<string, List<FerienPeriode>> Schuljahre { get; set; } = new();
        }

        /// <summary>
        /// Lädt die Ferien-Quelle aus einer JSON-Datei. Fehlt die Datei oder ist
        /// sie unlesbar, wird eine leere Quelle zurückgegeben (Feiertage und
        /// manuelle Eingabe funktionieren dann weiterhin).
        /// </summary>
        public static FerienQuelle LadeFerienQuelle(string jsonPfad, Action<string>? log = null)
        {
            try
            {
                if (!File.Exists(jsonPfad))
                {
                    log?.Invoke($"Ferien-Quelle nicht gefunden: {jsonPfad}. " +
                                "Feiertage werden berechnet, Ferien bitte importieren oder manuell eintragen.");
                    return new FerienQuelle();
                }

                string json = File.ReadAllText(jsonPfad);
                var quelle = JsonSerializer.Deserialize<FerienQuelle>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return quelle ?? new FerienQuelle();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Ferien-Quelle konnte nicht gelesen werden ({ex.Message}).");
                return new FerienQuelle();
            }
        }

        /// <summary>
        /// Liefert die Ferien eines Schuljahres aus der Quelle als FreieZeit-Liste.
        /// Unbekanntes Schuljahr -> leere Liste.
        /// </summary>
        public static List<FreieZeit> FerienFuerSchuljahr(FerienQuelle quelle, string schuljahr)
        {
            var result = new List<FreieZeit>();
            if (quelle?.Schuljahre == null) return result;
            if (!quelle.Schuljahre.TryGetValue(schuljahr, out var perioden)) return result;

            foreach (var p in perioden)
            {
                if (DateTime.TryParse(p.Von, out var von) && DateTime.TryParse(p.Bis, out var bis))
                {
                    result.Add(new FreieZeit
                    {
                        Typ = FreieZeitTyp.Ferien,
                        Von = von.Date,
                        Bis = bis.Date,
                        Bezeichnung = string.IsNullOrWhiteSpace(p.Name) ? "Ferien" : p.Name
                    });
                }
            }
            return result.OrderBy(f => f.Von).ToList();
        }

        /// <summary>
        /// Komfort: Feiertage (berechnet) + Ferien (aus Quelle) für ein Schuljahr
        /// zusammen. Dubletten (gleicher Tag) werden nicht entfernt – ein
        /// Feiertag innerhalb der Ferien stört die Frei-Logik nicht.
        /// </summary>
        public static List<FreieZeit> ImportSchuljahr(
            string schuljahr, DateTime start, DateTime ende,
            FerienQuelle? ferienQuelle = null, Bundesland land = Bundesland.NRW)
        {
            var result = new List<FreieZeit>();
            result.AddRange(FeiertageImZeitraum(start, ende, land));
            if (ferienQuelle != null)
                result.AddRange(FerienFuerSchuljahr(ferienQuelle, schuljahr));
            return result.OrderBy(f => f.Von).ToList();
        }
    }
}
