// ============================================================================
// EIGENSTÄNDIGE DATEI — neben MainWindow.xaml.cs ins Projekt legen.
// Erweitert die partielle Klasse MainWindow.
//
// ▶ EINZIGE nötige Änderung an MainWindow.xaml.cs:
//   die bisherige Methode  private void BtnPlanImportieren_Click(...)
//   samt ihres Kommentarblocks "BUTTON 3 – GPU001.TXT IN SHEET "PLAN"
//   IMPORTIEREN" LÖSCHEN. Sie steht jetzt (erweitert) hier — bleibt die alte
//   stehen, meldet der Compiler eine doppelte Definition.
// ============================================================================

using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2
{
    public partial class MainWindow
    {
        // =====================================================
        // BUTTON 3 – GPU001.TXT IN SHEET "PLAN" IMPORTIEREN (UNr-Plan importieren)
        // Importiert die Datei ins Sheet "Plan" und trägt den importierten
        // Plan anschließend automatisch als Lösung "Plan" in die Lösungen-,
        // Rank- und Diagnose-Tabellen ein (wie Button 4 – UNr-Plan bewerten).
        // Liest eine aus Untis exportierte GPU001.TXT (Stundenplan-Zeitraster)
        // und überträgt die enthaltenen UNr/Tag/Stunde-Zuordnungen direkt ins
        // Sheet "Plan". Das Sheet "Plan" wird dabei komplett überschrieben —
        // nach Bestätigung durch den Nutzer.
        //
        // OPTIONAL (Rückfrage vor dem Import): Tabelle "Fix UNrn" leeren und
        // die laut UV fixierten UNrn (Spalte "Fix (X)") an genau den Positionen
        // eintragen, an denen sie im importierten Plan liegen
        // (GpuImportExport.BaueFixUNrnAusUvNeu). Die UV selbst bleibt dabei
        // unverändert. Standard der Rückfrage ist "Nein".
        // =====================================================
        private void BtnPlanImportieren_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            var dlgOpen = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Untis GPU001 (*.txt)|*.txt",
                Title = "GPU001.TXT zum Import in Sheet 'Plan' wählen"
            };
            if (dlgOpen.ShowDialog() != true) return;

            // ---- Optional: "Fix UNrn" aus UV + importiertem Plan neu aufbauen ----
            bool fixNeuAufbauen = false;
            (bool uvSpaltenDa, int fixierteUNrn, int eintraegeFixUNrn) fixStand;
            try
            {
                fixStand = GpuImportExport.ZaehleFixStand(excelPfad);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Lesen von UV / 'Fix UNrn': " + ex.Message +
                    "\n\n(Ist die Excel-Datei evtl. in Excel geöffnet?)");
                return;
            }

            if (fixStand.uvSpaltenDa)
            {
                var antwortFix = MessageBox.Show(
                    "Soll die Tabelle 'Fix UNrn' beim Import neu aufgebaut werden?\n\n" +
                    $"Laut UV (Spalte 'Fix (X)') sind {fixStand.fixierteUNrn} UNr(n) fixiert. " +
                    $"In 'Fix UNrn' stehen derzeit {fixStand.eintraegeFixUNrn} Eintrag/Einträge.\n\n" +
                    "[Ja] = 'Fix UNrn' leeren und die laut UV fixierten UNrn an ihren Positionen " +
                    "im importierten Plan neu eintragen\n" +
                    "[Nein] = 'Fix UNrn' unverändert lassen\n" +
                    "[Abbrechen] = Import abbrechen",
                    "Fix UNrn neu aufbauen?",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);

                if (antwortFix == MessageBoxResult.Cancel) return;
                fixNeuAufbauen = antwortFix == MessageBoxResult.Yes;
            }
            else
            {
                Log("Plan-Import: In UV fehlt die Spalte 'Fix (X)' oder 'U-Nr' — " +
                    "Neuaufbau von 'Fix UNrn' nicht möglich, Rückfrage übersprungen.");
            }

            var bestaetigung = MessageBox.Show(
                $"Die Datei\n{dlgOpen.FileName}\n\nwird ins Sheet 'Plan' importiert. " +
                "Der bisherige Inhalt von 'Plan' wird dabei VOLLSTÄNDIG ÜBERSCHRIEBEN." +
                (fixNeuAufbauen
                    ? "\n\nAußerdem wird die Tabelle 'Fix UNrn' GELEERT und aus UV und " +
                      "importiertem Plan neu befüllt."
                    : "") +
                "\n\nFortfahren?",
                "Import bestätigen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (bestaetigung != MessageBoxResult.Yes) return;

            try
            {
                int anzahl = GpuImportExport.ImportiereGpu001NachPlan(excelPfad, dlgOpen.FileName);
                Log($"[Diag] Schritt 1/4: Sheet 'Plan' mit {anzahl} Zeitraster-Zeile(n) aus '{dlgOpen.FileName}' neu geschrieben (vollständiges Raster inkl. freier Slots, Sheet vorher geleert).");

                // "Fix UNrn" neu aufbauen — VOR dem Neueinlesen, damit input.Slots
                // die neuen Fixierungen direkt enthält. Ein Fehler hier bricht den
                // bereits erfolgten Plan-Import nicht ab; "Fix UNrn" bleibt dann
                // unverändert (BaueFixUNrnAusUvNeu speichert erst ganz am Ende).
                string fixText = "";
                if (fixNeuAufbauen)
                {
                    try
                    {
                        var fixErg = GpuImportExport.BaueFixUNrnAusUvNeu(excelPfad);
                        fixText = FixNeuaufbauProtokoll(fixErg);
                    }
                    catch (Exception fixEx)
                    {
                        fixText = $"⚠ 'Fix UNrn' konnte nicht neu aufgebaut werden: {fixEx.Message}\n" +
                                  "(Der Plan-Import selbst ist erfolgt; 'Fix UNrn' ist unverändert.)";
                        Log("Plan-Import: " + fixText.Replace("\n", " "));
                    }
                }

                LadeExcelDatenNeu(zeigeWarnungen: false);
                Log($"[Diag] Schritt 2/4: Excel neu eingelesen. Blocks={input?.Blocks?.Count ?? -1}, " +
                    $"Slots={input?.Slots?.Count ?? -1}, letzteSolutions vor Bewertung: " +
                    $"[{string.Join(", ", letzteSolutions.Select(s => s.label))}]");

                // Importierten Plan sofort als Lösung "Plan" bewerten und in
                // Lösungen/Rank/Diagnose eintragen (dieselbe Logik wie Button 4).
                BtnUnrPlan_Click(null, null);

                Log($"[Diag] Schritt 3/4: Nach Bewertung — letzteSolutions: " +
                    $"[{string.Join(", ", letzteSolutions.Select(s => s.label))}]");

                // Direkte, unabhängige Kontrolle: Was steht WIRKLICH auf der Platte
                // in "Lös" (frisches Öffnen der Datei, ohne jegliches Caching)?
                try
                {
                    using var kontrollWb = new XLWorkbook(excelPfad);
                    if (kontrollWb.Worksheets.Any(ws => ws.Name == "Lös"))
                    {
                        var kontrollSheet = kontrollWb.Worksheet("Lös");
                        var headerZellen = new List<string>();
                        int maxCol = kontrollSheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 2;
                        for (int c = 3; c <= maxCol; c++)
                        {
                            string h = kontrollSheet.Cell(1, c).GetString().Trim();
                            if (!string.IsNullOrEmpty(h)) headerZellen.Add(h);
                        }
                        Log($"[Diag] Schritt 4/4: Kontroll-Lesung 'Lös' DIREKT VON DER PLATTE — " +
                            $"Spaltenköpfe ab Spalte C: [{string.Join(", ", headerZellen)}]");
                    }
                    else
                    {
                        Log("[Diag] Schritt 4/4: Sheet 'Lös' existiert laut Kontroll-Lesung NICHT in der Datei!");
                    }
                }
                catch (Exception diagEx)
                {
                    Log($"[Diag] Schritt 4/4 fehlgeschlagen: {diagEx.Message}");
                }

                TxtStatus.Text = $"{anzahl} Slot(s) aus GPU001.TXT importiert und als Lösung 'Plan' eingetragen." +
                                 (fixNeuAufbauen ? " 'Fix UNrn' neu aufgebaut." : "");
                MessageBox.Show($"{anzahl} Slot(s) wurden ins Sheet 'Plan' importiert " +
                    "und automatisch als Lösung 'Plan' in die Lösungen-Tabelle eingetragen.\n\n" +
                    (fixText.Length > 0 ? fixText + "\n\n" : "") +
                    "Die Excel-Datei wurde automatisch neu eingelesen.\n\n" +
                    "Details zum Ablauf stehen jetzt im Log-Fenster (Zeilen mit '[Diag]').",
                    "Import fertig", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"[Diag] AUSNAHME in BtnPlanImportieren_Click: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show("Fehler beim Import: " + ex.Message);
            }
        }

        // Schreibt das Ergebnis des Fix-UNrn-Neuaufbaus ins Log (vollständig,
        // inkl. aller UNr-Listen) und liefert eine kurze Fassung für die
        // abschließende MessageBox.
        private string FixNeuaufbauProtokoll(GpuImportExport.FixUNrnNeuErgebnis erg)
        {
            if (!erg.Ausgefuehrt)
            {
                Log("Plan-Import: " + erg.Hinweis);
                return "⚠ " + erg.Hinweis;
            }

            int imPlan = erg.FixierteUNrnLautUv - erg.NichtImPlan.Count;

            Log($"Plan-Import: 'Fix UNrn' neu aufgebaut — {erg.AlteEintraege} alte(r) Eintrag/Einträge gelöscht, " +
                $"{erg.FixierteUNrnLautUv} UNr(n) laut UV fixiert, davon {imPlan} im Plan gefunden → " +
                $"{erg.EingetrageneEintraege} Eintrag/Einträge in {erg.BelegteSlots} Slot(s) eingetragen.");

            string text = $"'Fix UNrn' neu aufgebaut: {erg.EingetrageneEintraege} Eintrag/Einträge in " +
                          $"{erg.BelegteSlots} Slot(s) für {imPlan} von {erg.FixierteUNrnLautUv} laut UV " +
                          $"fixierten UNr(n) ({erg.AlteEintraege} alte Einträge gelöscht).";

            if (erg.NichtImPlan.Count > 0)
            {
                Log($"   ⚠ {erg.NichtImPlan.Count} laut UV fixierte UNr(n) kommen im importierten Plan " +
                    $"nicht vor und konnten nicht fixiert werden: {string.Join(", ", erg.NichtImPlan)}");
                text += $"\n⚠ Nicht im Plan (nicht fixiert): {FixNeuaufbauKurzliste(erg.NichtImPlan)}";
            }

            if (erg.IgnoriertUebersprungen.Count > 0)
            {
                Log($"   Hinweis: {erg.IgnoriertUebersprungen.Count} UNr(n) mit 'X' sind in UV komplett " +
                    $"ignoriert und wurden übersprungen: {string.Join(", ", erg.IgnoriertUebersprungen)}");
                text += $"\nHinweis: {erg.IgnoriertUebersprungen.Count} UNr(n) mit 'X' sind komplett " +
                        "ignoriert und wurden übersprungen.";
            }

            return text;
        }

        private static string FixNeuaufbauKurzliste(List<int> liste, int max = 15)
            => liste.Count <= max
                ? string.Join(", ", liste)
                : string.Join(", ", liste.Take(max)) + $", … (insgesamt {liste.Count}, siehe Log)";
    }
}
