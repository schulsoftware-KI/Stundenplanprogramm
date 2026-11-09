// ============================================================================
// Kopfleiste des Plan-Editors – erweitert die partielle Klasse PlanEditorDialog.
//
//  Eine feste Zeile (bricht beim Ändern der Fensterbreite nicht mehr um und zoomt
//  nicht mit): links Lösung, Modus und drei Klappmenüs, rechts Diag-Werte,
//  UV anzeigen und „?“ (Hinweise). Die Häkchen und Auswahlknöpfe liegen mit
//  unveränderten Namen und Ereignissen in den Klappmenüs (Popups) – ihre Logik und
//  das Merken in EdCfg bleiben, wie sie waren. Hier nur:
//   - die Knöpfe der Klappmenüs zeigen den Zustand („Ansicht (2)“, „Farben: Fach“,
//     „Vergleich: an“) und sind hellblau hinterlegt, sobald darin etwas an ist;
//   - Öffnen/Schließen der Klappmenüs (Klick auf den Knopf bei offenem Menü schließt
//     es, statt es sofort wieder zu öffnen; Esc schließt);
//   - „?“ blendet die Hinweise unter der Leiste ein und aus.
// ============================================================================

#nullable disable

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Stundenplan_V2
{
    public partial class PlanEditorDialog
    {
        private static readonly Brush KlappAktivHintergrund = new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xFF));
        private static readonly Brush KlappAktivRahmen = new SolidColorBrush(Color.FromRgb(0x7A, 0x9C, 0xE0));

        /// <summary>Direkt nach InitializeComponent: Zustandsanzeige an alle Schalter der Klappmenüs hängen.</summary>
        private void RichteKopfleisteEin()
        {
            var schalter = new ToggleButton[]
            {
                ChkKlassenVergleich, ChkFachgruppenPlan, ChkFaecherPlan, ChkRaumPlan, ChkRaeume,
                RbFarbeAus, RbFarbeKlasse, RbFarbeFach, RbFarbeBeide, ChkSpaetePaed, ChkSpaetePaedFix,
                ChkVergleichsModus
            };
            foreach (var s in schalter)
            {
                // Läuft nach den Ereignissen aus der XAML (die zeichnen die Ansichten).
                s.Checked += (o, e) => AktualisiereKopfleiste();
                s.Unchecked += (o, e) => AktualisiereKopfleiste();
            }
            AktualisiereKopfleiste();
        }

        private void AktualisiereKopfleiste()
        {
            if (LblAnsicht == null || LblFarben == null || LblVergleich == null) return;

            int ansicht = new CheckBox[] { ChkKlassenVergleich, ChkFachgruppenPlan, ChkFaecherPlan, ChkRaumPlan, ChkRaeume }
                .Count(c => c.IsChecked == true);
            LblAnsicht.Text = ansicht > 0 ? "Ansicht (" + ansicht + ")" : "Ansicht";
            SetzeKlappAktiv(TglAnsicht, ansicht > 0);

            string farbe = RbFarbeKlasse.IsChecked == true ? "Klasse"
                         : RbFarbeFach.IsChecked == true ? "Fach"
                         : RbFarbeBeide.IsChecked == true ? "Klasse+Fach"
                         : "aus";
            bool rot = ChkSpaetePaed.IsChecked == true;
            LblFarben.Text = "Farben: " + farbe + (rot ? " · rot" : "");
            SetzeKlappAktiv(TglFarben, farbe != "aus" || rot);

            bool vergleich = ChkVergleichsModus.IsChecked == true;
            LblVergleich.Text = vergleich ? "Vergleich: an" : "Vergleich";
            SetzeKlappAktiv(TglVergleich, vergleich);
        }

        private static void SetzeKlappAktiv(Control knopf, bool aktiv)
        {
            if (knopf == null) return;
            if (aktiv)
            {
                knopf.Background = KlappAktivHintergrund;
                knopf.BorderBrush = KlappAktivRahmen;
            }
            else
            {
                knopf.ClearValue(Control.BackgroundProperty);
                knopf.ClearValue(Control.BorderBrushProperty);
            }
        }

        // Solange ein Klappmenü offen ist, nimmt sein Knopf keine Klicks an: Ein Klick darauf
        // schließt dann nur das Menü (StaysOpen="False") und öffnet es nicht gleich wieder.
        private void Klappmenue_Opened(object sender, EventArgs e)
        {
            if (sender is Popup p && p.PlacementTarget is UIElement knopf) knopf.IsHitTestVisible = false;
        }

        private void Klappmenue_Closed(object sender, EventArgs e)
        {
            if (sender is Popup p && p.PlacementTarget is UIElement knopf) knopf.IsHitTestVisible = true;
        }

        private void Klappmenue_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            TglAnsicht.IsChecked = false;
            TglFarben.IsChecked = false;
            TglVergleich.IsChecked = false;
            e.Handled = true;
        }

        // „?“: Hinweise zur Bedienung unter der Kopfleiste ein-/ausblenden (gemerkt in EdCfg).
        private void TglHinweise_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtHinweise == null) return;
            TxtHinweise.Visibility = TglHinweise.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
