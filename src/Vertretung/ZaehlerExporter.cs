#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  ZaehlerExporter – schreibt die Zählerstände als Excel-Datei (ClosedXML).
    //
    //  Drei Blätter (V4, 281):
    //   - "Zähler Lehrer": je Lehrkraft die gehaltenen Stunden je Art, Punkte,
    //     Entfälle mit 0 bzw. −1, Sondergründe, Entfall gesamt und Saldo (274).
    //   - "Zähler Klassen": je Klasse die Stunden je Art und Entfallstunden,
    //     V4b (280): dazu „frei laut Kalender“ (freie Zeiten der Klasse).
    //   - "Entfälle": alle Entfälle (dieselbe Liste wie im Blatt „Entfälle“ der
    //     Vertretungsdatei, 279) – für die Weitergabe ohne die ganze Datei.
    //  V4b (293): „Vertreten 0“ / „Vertreten −1“ für die ausfallende Lehrkraft
    //  vertretener Stunden. Der Aufrufer gibt die abgeleiteten Entfälle mit
    //  (KalenderEntfaelle.MitAbgeleiteten).
    // =====================================================================

    public static class ZaehlerExporter
    {
        public const string EntfallBlatt = "Entfälle";

        /// <summary>Überschrift-Hinweis zur Zählung (281).</summary>
        public const string SaldoHinweis =
            "Saldo nach der Zählung ab V4: Gehalten (Punkte) − Entfall −1 − Vertreten −1 + Sondergrund (Punkte). " +
            "Bei vertretenen Stunden zählt auch die ausfallende Lehrkraft (fehlt 0, nicht verhindert −1); " +
            "freie Zeiten einzelner Klassen laut Kalender sind Entfälle der Lehrkraft laut Plan.";

        public static void Erzeuge(IEnumerable<VertretungsEintrag> eintraege, string zielPfad, string schuljahr = "")
        {
            var liste = eintraege.ToList();
            using var wb = new XLWorkbook();

            SchreibeLehrer(wb, liste, schuljahr);
            SchreibeKlassen(wb, liste, schuljahr);
            SchreibeEntfaelle(wb, Zaehlung.EntfallZeilen(liste),
                "Alle Entfälle" + (string.IsNullOrWhiteSpace(schuljahr) ? "" : $" – Schuljahr {schuljahr}"));

            wb.SaveAs(zielPfad);
        }

        private static void SchreibeLehrer(XLWorkbook wb, List<VertretungsEintrag> liste, string schuljahr)
        {
            var ws = wb.Worksheets.Add("Zähler Lehrer");
            int r = 1;

            ws.Cell(r, 1).Value = $"Vertretungszähler je Lehrkraft" +
                                  (string.IsNullOrWhiteSpace(schuljahr) ? "" : $" – Schuljahr {schuljahr}");
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 1).Style.Font.FontSize = 13;
            ws.Cell(r + 1, 1).Value = $"Stand: {DateTime.Now:dd.MM.yyyy HH:mm}";
            ws.Cell(r + 1, 1).Style.Font.FontColor = XLColor.Gray;
            ws.Cell(r + 2, 1).Value = SaldoHinweis;
            ws.Cell(r + 2, 1).Style.Font.FontColor = XLColor.Gray;
            r += 4;

            // Kopf
            string[] kopf = { "Lehrkraft", "Vertretung", "Betreuung/Aufsicht", "EVA",
                              "Raumvertretung", "Verlegung", "Zusammenlegung",
                              "Gehalten (Punkte)", "Entfall 0", "Entfall −1", "Vertreten 0", "Vertreten −1",
                              "Sondergrund", "Sondergrund (Punkte)", "Entfall gesamt", "Saldo" };
            for (int c = 0; c < kopf.Length; c++) ws.Cell(r, c + 1).Value = kopf[c];
            ws.Row(r).Style.Font.Bold = true;
            ws.Range(r, 1, r, kopf.Length).Style.Fill.BackgroundColor = XLColor.LightGray;
            r++;

            var stand = VertretungsZaehler.ProLehrer(liste);
            foreach (var s in stand.Values.OrderByDescending(s => s.Saldo)
                                           .ThenBy(s => s.Bezug, StringComparer.OrdinalIgnoreCase))
            {
                int P(Vertretungsart a) => s.ProArt.TryGetValue(a, out int n) ? n : 0;
                ws.Cell(r, 1).Value = s.Bezug;
                ws.Cell(r, 2).Value = P(Vertretungsart.Vertretung);
                ws.Cell(r, 3).Value = P(Vertretungsart.BetreuungAufsicht);
                ws.Cell(r, 4).Value = P(Vertretungsart.EVA);
                ws.Cell(r, 5).Value = P(Vertretungsart.Raumvertretung);
                ws.Cell(r, 6).Value = P(Vertretungsart.Verlegung);
                ws.Cell(r, 7).Value = P(Vertretungsart.Zusammenlegung);
                ws.Cell(r, 8).Value = s.GehalteneVertretungen;
                ws.Cell(r, 9).Value = s.Entfall0;
                ws.Cell(r, 10).Value = s.EntfallMinus1;
                ws.Cell(r, 11).Value = s.Vertreten0;
                ws.Cell(r, 12).Value = s.VertretenMinus1;
                ws.Cell(r, 13).Value = s.Sondergruende;
                ws.Cell(r, 14).Value = s.SondergrundPunkte;
                ws.Cell(r, 15).Value = s.EigeneAusfaelle;
                ws.Cell(r, 16).Value = s.Saldo;
                r++;
            }

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 14;   // die lange Hinweiszeile soll die erste Spalte nicht aufblähen
        }

        private static void SchreibeKlassen(XLWorkbook wb, List<VertretungsEintrag> liste, string schuljahr)
        {
            var ws = wb.Worksheets.Add("Zähler Klassen");
            int r = 1;

            ws.Cell(r, 1).Value = "Entfall-/Vertretungsstunden je Klasse";
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 1).Style.Font.FontSize = 13;
            r += 2;

            string[] kopf = { "Klasse", "Vertretung", "Betreuung/Aufsicht", "EVA",
                              "Raumvertretung", "Verlegung", "Zusammenlegung", "Entfall", "frei laut Kalender" };
            for (int c = 0; c < kopf.Length; c++) ws.Cell(r, c + 1).Value = kopf[c];
            ws.Row(r).Style.Font.Bold = true;
            ws.Range(r, 1, r, kopf.Length).Style.Fill.BackgroundColor = XLColor.LightGray;
            r++;

            var stand = VertretungsZaehler.ProKlasse(liste);
            foreach (var s in stand.Values.OrderBy(s => s.Bezug, StringComparer.OrdinalIgnoreCase))
            {
                int P(Vertretungsart a) => s.ProArt.TryGetValue(a, out int n) ? n : 0;
                ws.Cell(r, 1).Value = s.Bezug;
                ws.Cell(r, 2).Value = P(Vertretungsart.Vertretung);
                ws.Cell(r, 3).Value = P(Vertretungsart.BetreuungAufsicht);
                ws.Cell(r, 4).Value = P(Vertretungsart.EVA);
                ws.Cell(r, 5).Value = P(Vertretungsart.Raumvertretung);
                ws.Cell(r, 6).Value = P(Vertretungsart.Verlegung);
                ws.Cell(r, 7).Value = P(Vertretungsart.Zusammenlegung);
                ws.Cell(r, 8).Value = P(Vertretungsart.Entfall);
                ws.Cell(r, 9).Value = s.FreiLautKalender;
                r++;
            }

            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// V4 (279, 281): Blatt „Entfälle“ – eine Zeile je Entfall (nur zum Lesen). Wird auch von
        /// <see cref="VertretungsStore"/> in die Vertretungsdatei geschrieben (bei jedem Speichern neu).
        /// </summary>
        public static void SchreibeEntfaelle(XLWorkbook wb, List<Zaehlung.EntfallZeile> zeilen, string titel)
        {
            var ws = wb.Worksheets.Add(EntfallBlatt);
            int r = 1;
            ws.Cell(r, 1).Value = titel;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 1).Style.Font.FontSize = 13;
            ws.Cell(r + 1, 1).Value = $"{zeilen.Count} Zeile(n) · nur zum Lesen – wird bei jedem Speichern neu geschrieben · " +
                                      "Zählung für die Lehrkraft laut Plan · Quelle: gebucht, Kalender (freie Zeit einer " +
                                      "Klasse) bzw. vertreten (ausfallende Lehrkraft zählt nicht 0)";
            ws.Cell(r + 1, 1).Style.Font.FontColor = XLColor.Gray;
            r += 3;

            string[] kopf = { "Datum", "Stunde", "Klassen", "Fach", "Lehrkraft", "Anlass", "Grund",
                              "Zählung", "Sondergrund", "Kommentar", "Quelle" };
            for (int c = 0; c < kopf.Length; c++) ws.Cell(r, c + 1).Value = kopf[c];
            ws.Row(r).Style.Font.Bold = true;
            ws.Range(r, 1, r, kopf.Length).Style.Fill.BackgroundColor = XLColor.LightGray;
            int kopfZeile = r;
            r++;

            foreach (var z in zeilen)
            {
                ws.Cell(r, 1).Value = z.Datum.Date;
                ws.Cell(r, 1).Style.DateFormat.Format = "ddd dd.mm.yyyy";
                ws.Cell(r, 2).Value = z.Stunde;
                ws.Cell(r, 3).Value = z.Klassen;
                ws.Cell(r, 4).Value = z.Fach;
                ws.Cell(r, 5).Value = z.Lehrkraft;
                ws.Cell(r, 6).Value = z.Anlass;
                ws.Cell(r, 7).Value = z.Grund;
                ws.Cell(r, 8).Value = z.Zaehlwert;
                ws.Cell(r, 9).Value = z.Sondergrund ? "ja" : "";
                ws.Cell(r, 10).Value = z.Kommentar;
                ws.Cell(r, 11).Value = z.Quelle;
                r++;
            }

            ws.Columns().AdjustToContents();
            ws.Column(1).Width = 16;
            if (zeilen.Count > 0) ws.Range(kopfZeile, 1, r - 1, kopf.Length).SetAutoFilter();
            ws.SheetView.FreezeRows(kopfZeile);
        }
    }
}
