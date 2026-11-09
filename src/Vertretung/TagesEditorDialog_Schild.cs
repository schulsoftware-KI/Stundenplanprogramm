#nullable enable
using System;
using System.Windows;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// Phase 5.2a: Knopf „SchILD-Daten…“ im Wocheneditor. Öffnet den Prüflauf bzw. den
    /// Pseudo-Export (SchildDialog). Nutzt – wie „Grundplan veröffentlichen…“ – die oben unter
    /// „Plan“ gewählte Plan-Spalte; in 5.2a wird nichts gesendet.
    /// </summary>
    public partial class TagesEditorDialog
    {
        private void BtnSchild_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit || _plan == null)
            { SetzeInfo("Plan ist nicht geladen."); return; }

            var dlg = new SchildDialog(_ordner, _daten.Schuljahr ?? "", HoleGrundplanFuerSchild, _log) { Owner = this };
            dlg.ShowDialog();
        }

        /// <summary>
        /// Baut den Grundplan der gewählten Plan-Spalte (ohne Senden). Hinweis ist gesetzt, wenn die
        /// Schülergruppen aus der UV nicht lesbar waren (z. B. Plandatei in Excel geöffnet).
        /// </summary>
        private (GrundplanPushDto? Plan, string? Hinweis) HoleGrundplanFuerSchild()
        {
            var geladen = _plan;
            if (geladen == null) return (null, "Plan ist nicht geladen.");

            PlanDaten plan = geladen;
            if (CbPlan.SelectedItem is string spalte &&
                !string.Equals(spalte, geladen.GewaehltePlanSpalte, StringComparison.OrdinalIgnoreCase))
            {
                try { plan = PlanLeser.Lies(_planPfad, spalte); }
                catch (Exception ex) { return (null, $"Plan „{spalte}“ konnte nicht gelesen werden: {ex.Message}"); }
            }

            try
            {
                var dto = GrundplanSync.Baue(_daten.Schuljahr ?? "", _daten.Kalender, plan, DateTime.Today, out string? hinweis);
                return (dto, hinweis);
            }
            catch (Exception ex)
            {
                return (null, "Grundplan konnte nicht aufgebaut werden: " + ex.Message);
            }
        }
    }
}
