#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// U-1b: Knopf „Untis-Vertretungen…“ im Wocheneditor. Öffnet das Fenster „Vertretungen aus Untis“ (UntisDialog).
    /// Nutzt – wie „Grundplan veröffentlichen…“ und „SchILD-Daten…“ – die oben unter „Plan“ gewählte Plan-Spalte.
    /// Die Tage mit Buchungen im Vertretungsmodul gehen für die Warnung „eine Quelle je Tag“ mit (Entscheidung 120).
    /// U-2: Das Fenster ist nicht modal (es kann den Ordner beobachten, während der Wocheneditor bedienbar bleibt); es
    /// gibt höchstens eines. Schließt der Wocheneditor, endet das Beobachten – vorher fragt er nach.
    /// </summary>
    public partial class TagesEditorDialog
    {
        private UntisDialog? _untisFenster;

        private void BtnUntis_Click(object sender, RoutedEventArgs e)
        {
            if (_untisFenster != null)
            {
                if (_untisFenster.WindowState == WindowState.Minimized) _untisFenster.WindowState = WindowState.Normal;
                _untisFenster.Activate();
                return;
            }
            if (!_bereit || _plan == null)
            { SetzeInfo("Plan ist nicht geladen."); return; }

            DateTime basis = DpTag.SelectedDate ?? DateTime.Today;
            var dlg = new UntisDialog(_ordner, _daten.Schuljahr ?? "", _daten.Kalender, HolePlanFuerUntis, HoleBuchungsTage, basis, _log)
            {
                Owner = this
            };
            dlg.Closed += (_, __) => { if (ReferenceEquals(_untisFenster, dlg)) _untisFenster = null; };
            _untisFenster = dlg;
            dlg.Show();
        }

        /// <summary>Tage mit Buchungen im Vertretungsmodul – jedes Mal neu (das Untis-Fenster bleibt offen).</summary>
        private ISet<DateTime> HoleBuchungsTage()
            => new HashSet<DateTime>(_daten.Vertretungen.Select(v => v.Datum.Date));

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

        /// <summary>U-2: Wird im Untis-Fenster gerade gesendet bzw. der Ordner beobachtet, erst nachfragen.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            var u = _untisFenster;
            if (!e.Cancel && u != null)
            {
                if (u.Beschaeftigt)
                {
                    MessageBox.Show(this, "Im Fenster „Vertretungen aus Untis“ wird gerade gelesen bzw. veröffentlicht. Bitte warten " +
                        "oder dort „Abbrechen“ wählen.", "Wocheneditor schließen", MessageBoxButton.OK, MessageBoxImage.Information);
                    e.Cancel = true;
                }
                else if (u.Beobachtet)
                {
                    if (MessageBox.Show(this, "Im Fenster „Vertretungen aus Untis“ wird der Ordner beobachtet. Mit dem Wocheneditor " +
                            "schließt auch dieses Fenster, und neue Untis-Dateien werden nicht mehr von selbst veröffentlicht.\n\n" +
                            "Trotzdem schließen?", "Wocheneditor schließen", MessageBoxButton.YesNo, MessageBoxImage.Question,
                            MessageBoxResult.No) != MessageBoxResult.Yes)
                        e.Cancel = true;
                    else
                        u.SchliessenOhneRueckfrage();
                }
            }
            base.OnClosing(e);
        }
    }
}
