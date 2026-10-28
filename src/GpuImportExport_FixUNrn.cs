// ============================================================================
// EIGENSTÄNDIGE DATEI — einfach neben GpuImportExport.cs ins Projekt legen.
// Erweitert die (bereits partielle) Klasse GpuImportExport. An
// GpuImportExport.cs selbst ist nichts zu ändern.
// ============================================================================

using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public static partial class GpuImportExport
    {
        // ================================================================
        // "FIX UNRN" AUS UV + IMPORTIERTEM PLAN NEU AUFBAUEN
        // (optional beim Plan-Import, Button 3)
        //
        // Leert die Tabelle "Fix UNrn" und trägt danach jede UNr, die laut UV
        // fixiert ist, an GENAU den Slots ein, an denen sie im Sheet "Plan"
        // steht. Aufruf direkt nach ImportiereGpu001NachPlan: dann passen die
        // Positionsfixierungen wieder exakt zum gerade importierten Untis-Plan,
        // und veraltete Fixierungen aus früheren Plänen verschwinden.
        //
        // "Laut UV fixiert": mindestens eine UV-Zeile der UNr trägt "X" in
        // "Fix (X)" (Groß/Klein egal). Wie bei SetzeUvFixKennzeichen gilt das X
        // für die ganze UNr. UNrn, deren UV-Zeilen ALLE ignoriert sind (i/x in
        // "Ignore (i)"), werden übersprungen — der ExcelLoader würde sie aus
        // "Fix UNrn" ohnehin wieder herausfiltern.
        //
        // Sicherheitsnetz: Fehlt in UV die Spalte "Fix (X)" oder "U-Nr", oder
        // fehlt das Sheet "Plan", wird "Fix UNrn" NICHT angefasst — sonst wären
        // alle Fixierungen weg, ohne dass etwas Neues eingetragen werden kann.
        //
        // Die Kopfzeile (WTag | Stunde) wird immer geschrieben, auch wenn keine
        // einzige Fixierung folgt: der ExcelLoader liest "Fix UNrn" über
        // RangeUsed(), und das liefert bei einem komplett leeren Blatt null.
        //
        // Alles passiert in EINER Workbook-Sitzung mit Save() als letztem
        // Schritt — scheitert unterwegs etwas, bleibt die Datei unverändert.
        // ================================================================

        public class FixUNrnNeuErgebnis
        {
            // false = "Fix UNrn" wurde nicht verändert, Grund steht in Hinweis.
            public bool Ausgefuehrt;
            public string Hinweis = "";

            // UNr-Einträge (Zellen ab Spalte C), die vorher in "Fix UNrn" standen.
            public int AlteEintraege;

            // UNrn mit "X" in UV (ohne komplett ignorierte).
            public int FixierteUNrnLautUv;

            // Neu geschriebene UNr-Einträge (eine UNr je Slot = ein Eintrag).
            public int EingetrageneEintraege;

            // Anzahl Datenzeilen (Slots) in "Fix UNrn" nach dem Neuaufbau.
            public int BelegteSlots;

            // Laut UV fixiert, kommt im importierten Plan aber nirgends vor.
            public List<int> NichtImPlan = new();

            // Haben ein "X", sind in UV aber komplett ignoriert -> übersprungen.
            public List<int> IgnoriertUebersprungen = new();
        }

        // Für die Rückfrage VOR dem Import: Ist der Neuaufbau überhaupt möglich
        // (UV-Spalten vorhanden), wie viele UNrn sind laut UV fixiert und wie
        // viele Einträge stehen aktuell in "Fix UNrn"?
        public static (bool uvSpaltenDa, int fixierteUNrn, int eintraegeFixUNrn) ZaehleFixStand(string excelPfad)
        {
            using var wb = new XLWorkbook(excelPfad);
            var uv = LiesUvFixierteUNrnFuerFixNeu(wb);
            int alt = ZaehleFixUNrnEintraege(wb);
            return uv == null
                ? (false, 0, alt)
                : (true, uv.Value.fixiert.Count, alt);
        }

        public static FixUNrnNeuErgebnis BaueFixUNrnAusUvNeu(string excelPfad)
        {
            var erg = new FixUNrnNeuErgebnis();

            using var wb = new XLWorkbook(excelPfad);

            var uv = LiesUvFixierteUNrnFuerFixNeu(wb);
            if (uv == null)
            {
                erg.Hinweis = "In UV fehlt die Spalte 'Fix (X)' oder 'U-Nr' – 'Fix UNrn' wurde NICHT verändert.";
                return erg;
            }

            if (!wb.Worksheets.TryGetWorksheet("Plan", out var plan))
            {
                erg.Hinweis = "Sheet 'Plan' nicht gefunden – 'Fix UNrn' wurde NICHT verändert.";
                return erg;
            }

            var fixiert = uv.Value.fixiert;
            erg.FixierteUNrnLautUv = fixiert.Count;
            erg.IgnoriertUebersprungen = uv.Value.nurIgnoriert;

            // ---- Plan lesen: je Slot-Zeile die dort liegenden fixierten UNrn ----
            // Format wie von ImportiereGpu001NachPlan geschrieben:
            // WTag | Stunde | UNr | UNr | ...
            var zeilen = new List<(string wtag, int stunde, List<int> unrn)>();
            var imPlan = new HashSet<int>();

            int planLetzteZeile = plan.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= planLetzteZeile; r++)
            {
                string wtag = plan.Cell(r, 1).GetString().Trim();
                if (wtag.Length == 0) continue;
                if (!int.TryParse(plan.Cell(r, 2).GetString().Trim(), out int stunde)) continue;

                int letzteSpalte = plan.Row(r).LastCellUsed()?.Address.ColumnNumber ?? 2;
                var treffer = new List<int>();
                for (int c = 3; c <= letzteSpalte; c++)
                {
                    if (!int.TryParse(plan.Cell(r, c).GetString().Trim(), out int unr)) continue;
                    imPlan.Add(unr);
                    if (fixiert.Contains(unr) && !treffer.Contains(unr))
                        treffer.Add(unr);
                }

                if (treffer.Count > 0)
                    zeilen.Add((wtag, stunde, treffer.OrderBy(u => u).ToList()));
            }

            erg.NichtImPlan = fixiert.Where(u => !imPlan.Contains(u)).OrderBy(u => u).ToList();
            erg.AlteEintraege = ZaehleFixUNrnEintraege(wb);

            // ---- "Fix UNrn" leeren (bzw. anlegen) und neu befüllen ----
            if (!wb.Worksheets.TryGetWorksheet("Fix UNrn", out var fixSheet))
                fixSheet = wb.Worksheets.Add("Fix UNrn");
            else
                fixSheet.Clear(XLClearOptions.All);

            fixSheet.Cell(1, 1).Value = "WTag";
            fixSheet.Cell(1, 2).Value = "Stunde";
            fixSheet.Cell(1, 1).Style.Font.Bold = true;
            fixSheet.Cell(1, 2).Style.Font.Bold = true;

            int zeile = 2;
            foreach (var (wtag, stunde, unrn) in zeilen)
            {
                fixSheet.Cell(zeile, 1).Value = wtag;
                fixSheet.Cell(zeile, 2).Value = stunde;

                int spalte = 3;
                foreach (int unr in unrn)
                {
                    fixSheet.Cell(zeile, spalte++).Value = unr;
                    erg.EingetrageneEintraege++;
                }
                zeile++;
            }
            erg.BelegteSlots = zeilen.Count;

            // Speichert wie ImportiereGpu001NachPlan an der zentralen
            // Vor-dem-Speichern-Stelle vorbei -> Spaltenbreiten hier optimieren.
            SpaltenBreiten.Optimieren(wb);

            wb.Save();
            erg.Ausgefuehrt = true;
            return erg;
        }

        // Liest aus UV die laut "Fix (X)" fixierten UNrn. Rückgabe null, wenn
        // das Sheet UV oder die Spalte "Fix (X)"/"U-Nr" fehlt. Zeilen werden
        // absolut adressiert (Row(r)), damit die Spaltennummern aus der
        // Kopfzeile auch dann stimmen, wenn Spalte A leer ist.
        private static (HashSet<int> fixiert, List<int> nurIgnoriert)? LiesUvFixierteUNrnFuerFixNeu(XLWorkbook wb)
        {
            if (!wb.Worksheets.TryGetWorksheet("UV", out var uv)) return null;

            var header = LiesHeader(uv);
            int colUNr    = UvSpalteFuerFixNeu(header, "U-Nr", "UNr");
            int colFix    = UvSpalteFuerFixNeu(header, "Fix (X)", "Fix");
            int colIgnore = UvSpalteFuerFixNeu(header, "Ignore (i)", "Ignore");
            if (colUNr < 0 || colFix < 0) return null;

            var mitX = new HashSet<int>();
            var aktiv = new HashSet<int>();

            int letzteZeile = uv.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= letzteZeile; r++)
            {
                var row = uv.Row(r);
                if (!int.TryParse(row.Cell(colUNr).GetString().Trim(), out int unr) || unr <= 0)
                    continue;

                // Ignore-Logik wie im ExcelLoader: "i" oder "x" = Zeile ignoriert.
                string ig = colIgnore > 0 ? row.Cell(colIgnore).GetString().Trim().ToLowerInvariant() : "";
                if (ig != "i" && ig != "x")
                    aktiv.Add(unr);

                if (string.Equals(row.Cell(colFix).GetString().Trim(), "X", StringComparison.OrdinalIgnoreCase))
                    mitX.Add(unr);
            }

            var nurIgnoriert = mitX.Where(u => !aktiv.Contains(u)).OrderBy(u => u).ToList();
            mitX.IntersectWith(aktiv);
            return (mitX, nurIgnoriert);
        }

        // Zählt die UNr-Einträge (Zellen ab Spalte C) in "Fix UNrn".
        private static int ZaehleFixUNrnEintraege(XLWorkbook wb)
        {
            if (!wb.Worksheets.TryGetWorksheet("Fix UNrn", out var sheet)) return 0;

            int anzahl = 0;
            int letzteZeile = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= letzteZeile; r++)
            {
                int letzteSpalte = sheet.Row(r).LastCellUsed()?.Address.ColumnNumber ?? 2;
                for (int c = 3; c <= letzteSpalte; c++)
                    if (int.TryParse(sheet.Cell(r, c).GetString().Trim(), out _))
                        anzahl++;
            }
            return anzahl;
        }

        private static int UvSpalteFuerFixNeu(Dictionary<string, int> header, params string[] namen)
        {
            foreach (var n in namen)
                if (header.TryGetValue(n, out int c))
                    return c;
            return -1;
        }
    }
}
