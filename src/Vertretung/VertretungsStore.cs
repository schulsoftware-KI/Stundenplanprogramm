#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  Persistenz des Vertretungsmoduls: Vertretung_<Jahr>.xlsx
    //
    //  Eigene Datei pro Schuljahr (Konzept §2/§7), damit die Grund-Plandatei
    //  unangetastet bleibt. Umsetzung mit ClosedXML, wie im übrigen Projekt
    //  (LiveExporter, LehrerplanGenerator).
    //
    //  Sheets:
    //    Meta           Schuljahr, Pfad der Plandatei, Version, Start/Ende,
    //                   Rastertage, Ferien-Quelle
    //    Kalender       freie Zeiten (Ferien/Feiertage/freie Tage)
    //    Arten          Zählwert-Konfiguration je Vertretungsart
    //    Abwesenheiten  erfasste Abwesenheiten
    //    Vertretungen   ein Eintrag je Stunden-Instanz (inkl. Verlegungsfelder)
    //    Kompetenzen    Lehrer -> Fächer (UV-abgeleitet + manuell)
    // =====================================================================

    /// <summary>Gesamter Inhalt einer Vertretungsdatei.</summary>
    public class VertretungsDaten
    {
        public string Schuljahr { get; set; } = "";
        public string PlanDateiPfad { get; set; } = "";
        public string Version { get; set; } = "1";

        // Pfad der zuletzt gewählten NRW-Ferien-JSON (ferien_nrw.json). Wird im
        // Meta-Sheet gespeichert, damit der Kalender-Dialog sie beim nächsten
        // Öffnen automatisch wiederfindet, ohne dass sie im Ausgabeverzeichnis
        // liegen muss.
        public string FerienQuellePfad { get; set; } = "";

        public SchuljahrKalender Kalender { get; set; } = new();
        public List<VertretungsartConfig> Arten { get; set; } = VertretungsartConfig.Standard();
        public List<Abwesenheit> Abwesenheiten { get; set; } = new();
        public List<VertretungsEintrag> Vertretungen { get; set; } = new();
        public List<LehrerKompetenz> Kompetenzen { get; set; } = new();

        /// <summary>Zählwert einer Art aus der aktuellen Konfiguration (Default 0).</summary>
        public int Zaehlwert(Vertretungsart art)
            => Arten.FirstOrDefault(a => a.Art == art)?.Zaehlwert ?? 0;
    }

    public static class VertretungsStore
    {
        private const string TRENNER = ";";   // Trennzeichen in Klassenlisten

        /// <summary>Standard-Dateiname, z.B. "Vertretung_2026_27.xlsx".</summary>
        public static string StandardDateiname(string schuljahr)
        {
            string s = (schuljahr ?? "").Replace("/", "_").Replace("\\", "_").Trim();
            if (string.IsNullOrWhiteSpace(s)) s = "unbekannt";
            return $"Vertretung_{s}.xlsx";
        }

        // =================================================================
        //  Speichern
        // =================================================================

        public static void Speichern(VertretungsDaten daten, string pfad)
        {
            using var wb = new XLWorkbook();

            SchreibeMeta(wb, daten);
            SchreibeKalender(wb, daten.Kalender);
            SchreibeArten(wb, daten.Arten);
            SchreibeAbwesenheiten(wb, daten.Abwesenheiten);
            SchreibeVertretungen(wb, daten.Vertretungen);
            SchreibeKompetenzen(wb, daten.Kompetenzen);

            wb.SaveAs(pfad);
        }

        private static void SchreibeMeta(XLWorkbook wb, VertretungsDaten d)
        {
            var ws = wb.Worksheets.Add("Meta");
            ws.Cell(1, 1).Value = "Schlüssel";
            ws.Cell(1, 2).Value = "Wert";
            ws.Row(1).Style.Font.Bold = true;

            var rows = new (string k, string v)[]
            {
                ("Schuljahr",      d.Schuljahr),
                ("PlanDatei",      d.PlanDateiPfad),
                ("Version",        d.Version),
                ("KalenderStart",  IsoDatum(d.Kalender.Start)),
                ("KalenderEnde",   IsoDatum(d.Kalender.Ende)),
                ("RasterTage",     string.Join(TRENNER, d.Kalender.RasterTage)),
                ("FerienQuelle",   d.FerienQuellePfad),
            };
            for (int i = 0; i < rows.Length; i++)
            {
                ws.Cell(i + 2, 1).Value = rows[i].k;
                ws.Cell(i + 2, 2).Value = rows[i].v;
            }
            ws.Columns().AdjustToContents();
        }

        private static void SchreibeKalender(XLWorkbook wb, SchuljahrKalender kal)
        {
            var ws = wb.Worksheets.Add("Kalender");
            string[] header = { "Typ", "Von", "Bis", "Bezeichnung", "Klassen" };
            for (int c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];
            ws.Row(1).Style.Font.Bold = true;

            int r = 2;
            foreach (var fz in kal.FreieZeiten.OrderBy(f => f.Von))
            {
                ws.Cell(r, 1).Value = fz.Typ.ToString();
                ws.Cell(r, 2).Value = fz.Von.Date;
                ws.Cell(r, 3).Value = fz.Bis.Date;
                ws.Cell(r, 4).Value = fz.Bezeichnung;
                ws.Cell(r, 5).Value = string.Join(TRENNER, fz.BetroffeneKlassen);
                ws.Cell(r, 2).Style.DateFormat.Format = "yyyy-mm-dd";
                ws.Cell(r, 3).Style.DateFormat.Format = "yyyy-mm-dd";
                r++;
            }
            ws.Columns().AdjustToContents();
        }

        private static void SchreibeArten(XLWorkbook wb, List<VertretungsartConfig> arten)
        {
            var ws = wb.Worksheets.Add("Arten");
            string[] header = { "Art", "Zaehlwert", "FachgerechtUnterscheiden" };
            for (int c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];
            ws.Row(1).Style.Font.Bold = true;

            int r = 2;
            foreach (var a in arten)
            {
                ws.Cell(r, 1).Value = a.Art.ToString();
                ws.Cell(r, 2).Value = a.Zaehlwert;
                ws.Cell(r, 3).Value = BoolStr(a.FachgerechtUnterscheiden);
                r++;
            }
            ws.Columns().AdjustToContents();
        }

        private static void SchreibeAbwesenheiten(XLWorkbook wb, List<Abwesenheit> liste)
        {
            var ws = wb.Worksheets.Add("Abwesenheiten");
            string[] header =
                { "Subjekt", "Name", "VonDatum", "VonStunde", "BisDatum", "BisStunde", "Grund", "Bemerkung" };
            for (int c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];
            ws.Row(1).Style.Font.Bold = true;

            int r = 2;
            foreach (var a in liste)
            {
                ws.Cell(r, 1).Value = a.Subjekt.ToString();
                ws.Cell(r, 2).Value = a.Name;
                ws.Cell(r, 3).Value = a.VonDatum.Date;
                ws.Cell(r, 4).Value = a.VonStunde;
                ws.Cell(r, 5).Value = a.BisDatum.Date;
                ws.Cell(r, 6).Value = a.BisStunde;
                ws.Cell(r, 7).Value = a.Grund.ToString();
                ws.Cell(r, 8).Value = a.Bemerkung;
                ws.Cell(r, 3).Style.DateFormat.Format = "yyyy-mm-dd";
                ws.Cell(r, 5).Style.DateFormat.Format = "yyyy-mm-dd";
                r++;
            }
            ws.Columns().AdjustToContents();
        }

        private static void SchreibeVertretungen(XLWorkbook wb, List<VertretungsEintrag> liste)
        {
            var ws = wb.Worksheets.Add("Vertretungen");
            string[] header =
            {
                "Datum", "Stunde", "UNr", "Art", "Grund", "OriginalLehrer", "VertretungsLehrer",
                "Klassen", "Fach", "OriginalRaum", "VertretungsRaum", "Fachgerecht",
                "Zaehlwert", "Bemerkung",
                // Verlegung (M3a)
                "VerlegungsId", "VerlegtVonDatum", "VerlegtVonStunde",
                "VerlegtNachDatum", "VerlegtNachStunde"
            };
            for (int c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];
            ws.Row(1).Style.Font.Bold = true;

            int r = 2;
            foreach (var v in liste)
            {
                ws.Cell(r, 1).Value = v.Datum.Date;
                ws.Cell(r, 2).Value = v.Stunde;
                ws.Cell(r, 3).Value = v.UNr;
                ws.Cell(r, 4).Value = v.Art.ToString();
                ws.Cell(r, 5).Value = v.Grund.ToString();
                ws.Cell(r, 6).Value = v.OriginalLehrer;
                ws.Cell(r, 7).Value = v.VertretungsLehrer ?? "";
                ws.Cell(r, 8).Value = string.Join(TRENNER, v.Klassen);
                ws.Cell(r, 9).Value = v.Fach;
                ws.Cell(r, 10).Value = v.OriginalRaum ?? "";
                ws.Cell(r, 11).Value = v.VertretungsRaum ?? "";
                ws.Cell(r, 12).Value = NullBoolStr(v.Fachgerecht);
                ws.Cell(r, 13).Value = v.ZaehlwertSnapshot;
                ws.Cell(r, 14).Value = v.Bemerkung;
                ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";

                // ---- Verlegung ----
                ws.Cell(r, 15).Value = v.VerlegungsId ?? "";
                if (v.VerlegtVonDatum.HasValue)
                {
                    ws.Cell(r, 16).Value = v.VerlegtVonDatum.Value.Date;
                    ws.Cell(r, 16).Style.DateFormat.Format = "yyyy-mm-dd";
                }
                if (v.VerlegtVonStunde.HasValue) ws.Cell(r, 17).Value = v.VerlegtVonStunde.Value;
                if (v.VerlegtNachDatum.HasValue)
                {
                    ws.Cell(r, 18).Value = v.VerlegtNachDatum.Value.Date;
                    ws.Cell(r, 18).Style.DateFormat.Format = "yyyy-mm-dd";
                }
                if (v.VerlegtNachStunde.HasValue) ws.Cell(r, 19).Value = v.VerlegtNachStunde.Value;

                r++;
            }
            ws.Columns().AdjustToContents();
        }

        private static void SchreibeKompetenzen(XLWorkbook wb, List<LehrerKompetenz> liste)
        {
            var ws = wb.Worksheets.Add("Kompetenzen");
            string[] header = { "Lehrer", "Faecher", "ManuellErgaenzt" };
            for (int c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];
            ws.Row(1).Style.Font.Bold = true;

            int r = 2;
            foreach (var k in liste)
            {
                ws.Cell(r, 1).Value = k.Lehrer;
                ws.Cell(r, 2).Value = string.Join(TRENNER, k.Faecher);
                ws.Cell(r, 3).Value = string.Join(TRENNER, k.ManuellErgaenzt);
                r++;
            }
            ws.Columns().AdjustToContents();
        }

        // =================================================================
        //  Laden
        // =================================================================

        public static VertretungsDaten Laden(string pfad)
        {
            var d = new VertretungsDaten();
            if (!File.Exists(pfad)) return d;

            using var wb = new XLWorkbook(pfad);

            LiesMeta(wb, d);
            LiesKalender(wb, d);
            LiesArten(wb, d);
            LiesAbwesenheiten(wb, d);
            LiesVertretungen(wb, d);
            LiesKompetenzen(wb, d);

            return d;
        }

        private static void LiesMeta(XLWorkbook wb, VertretungsDaten d)
        {
            if (!wb.Worksheets.TryGetWorksheet("Meta", out var ws)) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ws.RowsUsed().Skip(1))
                map[row.Cell(1).GetString().Trim()] = row.Cell(2).GetString().Trim();

            d.Schuljahr = Get(map, "Schuljahr");
            d.PlanDateiPfad = Get(map, "PlanDatei");
            d.Version = Get(map, "Version", "1");
            d.FerienQuellePfad = Get(map, "FerienQuelle");
            d.Kalender.Schuljahr = d.Schuljahr;
            d.Kalender.Start = ParseDatum(Get(map, "KalenderStart"));
            d.Kalender.Ende = ParseDatum(Get(map, "KalenderEnde"));

            var tage = Get(map, "RasterTage");
            if (!string.IsNullOrWhiteSpace(tage))
                d.Kalender.RasterTage = SplitListe(tage);
        }

        private static void LiesKalender(XLWorkbook wb, VertretungsDaten d)
        {
            if (!wb.Worksheets.TryGetWorksheet("Kalender", out var ws)) return;
            d.Kalender.FreieZeiten.Clear();
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                var typStr = row.Cell(1).GetString().Trim();
                if (string.IsNullOrWhiteSpace(typStr)) continue;
                Enum.TryParse<FreieZeitTyp>(typStr, true, out var typ);
                d.Kalender.FreieZeiten.Add(new FreieZeit
                {
                    Typ = typ,
                    Von = ZellDatum(row.Cell(2)),
                    Bis = ZellDatum(row.Cell(3)),
                    Bezeichnung = row.Cell(4).GetString(),
                    BetroffeneKlassen = SplitListe(row.Cell(5).GetString())
                });
            }
        }

        private static void LiesArten(XLWorkbook wb, VertretungsDaten d)
        {
            if (!wb.Worksheets.TryGetWorksheet("Arten", out var ws)) return;
            var geladen = new List<VertretungsartConfig>();
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (!Enum.TryParse<Vertretungsart>(row.Cell(1).GetString().Trim(), true, out var art))
                    continue;
                geladen.Add(new VertretungsartConfig
                {
                    Art = art,
                    Zaehlwert = (int)ZellZahl(row.Cell(2)),
                    FachgerechtUnterscheiden = ParseBool(row.Cell(3).GetString())
                });
            }
            if (geladen.Count > 0)
            {
                // fehlende Arten aus Standard ergänzen, damit immer alle da sind
                foreach (var std in VertretungsartConfig.Standard())
                    if (!geladen.Any(g => g.Art == std.Art))
                        geladen.Add(std);
                d.Arten = geladen;
            }
        }

        private static void LiesAbwesenheiten(XLWorkbook wb, VertretungsDaten d)
        {
            if (!wb.Worksheets.TryGetWorksheet("Abwesenheiten", out var ws)) return;
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (string.IsNullOrWhiteSpace(row.Cell(2).GetString())) continue;
                Enum.TryParse<AbwesenheitsSubjekt>(row.Cell(1).GetString().Trim(), true, out var subj);
                Enum.TryParse<VertretungsGrund>(row.Cell(7).GetString().Trim(), true, out var grund);
                d.Abwesenheiten.Add(new Abwesenheit
                {
                    Subjekt = subj,
                    Name = row.Cell(2).GetString(),
                    VonDatum = ZellDatum(row.Cell(3)),
                    VonStunde = (int)ZellZahl(row.Cell(4)),
                    BisDatum = ZellDatum(row.Cell(5)),
                    BisStunde = (int)ZellZahl(row.Cell(6)),
                    Grund = grund,
                    Bemerkung = row.Cell(8).GetString()
                });
            }
        }

        private static void LiesVertretungen(XLWorkbook wb, VertretungsDaten d)
        {
            if (!wb.Worksheets.TryGetWorksheet("Vertretungen", out var ws)) return;
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (row.Cell(1).IsEmpty()) continue;
                Enum.TryParse<Vertretungsart>(row.Cell(4).GetString().Trim(), true, out var art);
                Enum.TryParse<VertretungsGrund>(row.Cell(5).GetString().Trim(), true, out var grund);

                string vertr = row.Cell(7).GetString();
                string oRaum = row.Cell(10).GetString();
                string vRaum = row.Cell(11).GetString();

                d.Vertretungen.Add(new VertretungsEintrag
                {
                    Datum = ZellDatum(row.Cell(1)),
                    Stunde = (int)ZellZahl(row.Cell(2)),
                    UNr = (int)ZellZahl(row.Cell(3)),
                    Art = art,
                    Grund = grund,
                    OriginalLehrer = row.Cell(6).GetString(),
                    VertretungsLehrer = string.IsNullOrWhiteSpace(vertr) ? null : vertr,
                    Klassen = SplitListe(row.Cell(8).GetString()),
                    Fach = row.Cell(9).GetString(),
                    OriginalRaum = string.IsNullOrWhiteSpace(oRaum) ? null : oRaum,
                    VertretungsRaum = string.IsNullOrWhiteSpace(vRaum) ? null : vRaum,
                    Fachgerecht = ParseNullBool(row.Cell(12).GetString()),
                    ZaehlwertSnapshot = (int)ZellZahl(row.Cell(13)),
                    Bemerkung = row.Cell(14).GetString(),

                    // ---- Verlegung (M3a) – fehlen in Altdateien => leer/null ----
                    VerlegungsId = row.Cell(15).GetString().Trim(),
                    VerlegtVonDatum = ZellDatumNull(row.Cell(16)),
                    VerlegtVonStunde = ZellIntNull(row.Cell(17)),
                    VerlegtNachDatum = ZellDatumNull(row.Cell(18)),
                    VerlegtNachStunde = ZellIntNull(row.Cell(19))
                });
            }
        }

        private static void LiesKompetenzen(XLWorkbook wb, VertretungsDaten d)
        {
            if (!wb.Worksheets.TryGetWorksheet("Kompetenzen", out var ws)) return;
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                string lehrer = row.Cell(1).GetString().Trim();
                if (string.IsNullOrWhiteSpace(lehrer)) continue;
                var k = new LehrerKompetenz { Lehrer = lehrer };
                foreach (var f in SplitListe(row.Cell(2).GetString())) k.Faecher.Add(f);
                foreach (var f in SplitListe(row.Cell(3).GetString())) k.ManuellErgaenzt.Add(f);
                d.Kompetenzen.Add(k);
            }
        }

        // =================================================================
        //  Hilfsfunktionen
        // =================================================================

        private static string Get(Dictionary<string, string> map, string key, string fallback = "")
            => map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        private static List<string> SplitListe(string s)
            => string.IsNullOrWhiteSpace(s)
                ? new List<string>()
                : s.Split(new[] { TRENNER, "," }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private static string IsoDatum(DateTime d)
            => d == default ? "" : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static DateTime ParseDatum(string s)
            => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
               || DateTime.TryParse(s, out d) ? d.Date : default;

        private static DateTime ZellDatum(IXLCell cell)
        {
            if (cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var dt))
                return dt.Date;
            return ParseDatum(cell.GetString());
        }

        /// <summary>Datum oder null (leere Zelle / unlesbar).</summary>
        private static DateTime? ZellDatumNull(IXLCell cell)
        {
            if (cell.IsEmpty()) return null;
            if (cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var dt))
                return dt.Date;
            var s = cell.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            var d = ParseDatum(s);
            return d == default ? (DateTime?)null : d;
        }

        /// <summary>Ganzzahl oder null (leere Zelle / unlesbar).</summary>
        private static int? ZellIntNull(IXLCell cell)
        {
            if (cell.IsEmpty()) return null;
            if (cell.TryGetValue<double>(out var d)) return (int)d;
            var s = cell.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return int.TryParse(s.Trim(), out int v) ? v : (int?)null;
        }

        private static double ZellZahl(IXLCell cell)
        {
            if (cell.TryGetValue<double>(out var d)) return d;
            return double.TryParse(cell.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var p) ? p : 0;
        }

        private static string BoolStr(bool b) => b ? "ja" : "nein";

        private static string NullBoolStr(bool? b)
            => b == null ? "" : (b.Value ? "ja" : "nein");

        private static bool ParseBool(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            return s is "ja" or "x" or "1" or "true" or "wahr";
        }

        private static bool? ParseNullBool(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return null;
            if (s is "ja" or "x" or "1" or "true" or "wahr") return true;
            if (s is "nein" or "0" or "false" or "falsch") return false;
            return null;
        }
    }
}
