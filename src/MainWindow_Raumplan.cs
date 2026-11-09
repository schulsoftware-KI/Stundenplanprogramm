// ============================================================================
// EIGENSTÄNDIGE DATEI — erweitert die partielle Klasse MainWindow um Knopf 25.
// (Keine Änderung an MainWindow.xaml.cs nötig; der Knopf steht in MainWindow.xaml.)
// ============================================================================

using System.Windows;

namespace Stundenplan_V2
{
    public partial class MainWindow
    {
        // =====================================================
        // BUTTON 25 – RÄUME ZUWEISEN (RAUMPLAN)
        // Verteilt für eine Plan-Spalte aus „Lös" die Räume auf die verplanten
        // Unterrichte (Fachraum laut UV → Raum derselben Fachgruppe → Stammraum →
        // freier Ausweichraum) und schreibt das Ergebnis ins Blatt „Raumplan".
        // Konzept: claude/Raumplan_Vorschlag.md (Schritt R1).
        // =====================================================
        private void BtnRaeumeZuweisen_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(excelPfad))
            {
                MessageBox.Show("Bitte zuerst Excel-Datei laden (Button 1).");
                return;
            }

            var dialog = new Stundenplan_V2.Raumplanung.RaumplanDialog(excelPfad, s => Log(s))
            { Owner = this };
            dialog.ShowDialog();
        }
    }
}
