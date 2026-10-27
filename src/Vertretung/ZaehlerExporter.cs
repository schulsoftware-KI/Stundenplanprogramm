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
    //  Zwei Blätter:
    //   - "Zähler Lehrer": je Lehrkraft die Vertretungen je Art, eigene
    //     Ausfälle, gehaltene (gewichtete) Summe und Saldo.
    //   - "Zähler Klassen": je Klasse die Stunden je Art und Entfallstunden.
    // =====================================================================

    public static class ZaehlerExporter
    {
        public static void Erzeuge(IEnumerable<VertretungsEintrag> eintraege, string zielPfad, string schuljahr = "")
        {
            var liste = eintraege.ToList();
            using var wb = new XLWorkbook();

            SchreibeLehrer(wb, liste, schuljahr);
            SchreibeKlassen(wb, liste, schuljahr);

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
            r += 3;

            // Kopf
            string[] kopf = { "Lehrkraft", "Vertretung", "Betreuung/Aufsicht", "EVA",
                              "Raumvertretung", "Verlegung", "Zusammenlegung",
                              "Eigene Ausfälle", "Gehalten (gew.)", "Saldo" };
            for (int c = 0; c < kopf.Length; c++) ws.Cell(r, c + 1).Value = kopf[c];
            ws.Row(r).Style.Font.Bold = true;
            ws.Range(r, 1, r, kopf.Length).Style.Fill.BackgroundColor = XLColor.LightGray;
            r++;

            var stand = VertretungsZaehler.ProLehrer(liste);
            foreach (var s in stand.Values.OrderByDescending(s => s.GehalteneVertretungen)
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
                ws.Cell(r, 8).Value = s.EigeneAusfaelle;
                ws.Cell(r, 9).Value = s.GehalteneVertretungen;
                ws.Cell(r, 10).Value = s.Saldo;
                r++;
            }

            ws.Columns().AdjustToContents();
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
                              "Raumvertretung", "Verlegung", "Zusammenlegung", "Entfall" };
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
                r++;
            }

            ws.Columns().AdjustToContents();
        }
    }
}
