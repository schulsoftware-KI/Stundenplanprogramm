#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// U-1b: Knopf „Untis-Vertretungen…“ im Wocheneditor. Öffnet das Fenster „Vertretungen aus Untis“ (UntisDialog).
    /// Nutzt – wie „Grundplan veröffentlichen…“ und „SchILD-Daten…“ – die oben unter „Plan“ gewählte Plan-Spalte.
    /// Die Tage mit Buchungen im Vertretungsmodul gehen für die Warnung „eine Quelle je Tag“ mit (Entscheidung 120).
    /// </summary>
    public partial class TagesEditorDialog
    {
        private void BtnUntis_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit || _plan == null)
            { SetzeInfo("Plan ist nicht geladen."); return; }

            var buchungsTage = new HashSet<DateTime>(_daten.Vertretungen.Select(v => v.Datum.Date));
            DateTime basis = DpTag.SelectedDate ?? DateTime.Today;
            var dlg = new UntisDialog(_ordner, _daten.Schuljahr ?? "", _daten.Kalender, HolePlanFuerUntis, buchungsTage, basis, _log)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }

        /// <summary>Plan der gewählten Plan-Spalte (auch ohne „Aktualisieren“).</summary>
        private (PlanDaten? Plan, string? Fehler) HolePlanFuerUntis()
        {
            var geladen = _plan;
            if (geladen == null) return (null, "Plan ist nicht geladen.");
            if (CbPlan.SelectedItem is string spalte &&
                !string.Equals(spalte, geladen.GewaehltePlanSpalte, StringComparison.OrdinalIgnoreCase))
            {
                try { return (PlanLeser.Lies(_planPfad, spalte), null); }
                catch (Exception ex) { return (null, $"Plan „{spalte}“ konnte nicht gelesen werden: {ex.Message}"); }
            }
            return (geladen, null);
        }
    }
}
