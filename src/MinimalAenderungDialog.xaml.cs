using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Stundenplan_V2
{
    public partial class MinimalAenderungDialog : Window
    {
        public string GewählterAusgangsLabel  { get; private set; }
        public int    StabilitaetsGewicht     { get; private set; }
        public int    ZeitlimitSekunden       { get; private set; }
        public int    AnzahlLoesungen         { get; private set; }
        public bool   ExportiereAbweichungen  { get; private set; }

        // Teilplan im Stabilitätsmodus: nicht verplanbare Unterrichte werden
        // fallengelassen (wie Teilplan bei Knopf 10) statt den Lauf scheitern
        // zu lassen. TeilplanGapWst = absoluter Phase-A-Gap in Wochenstunden.
        public bool   TeilplanAktiv           { get; private set; }
        public int    TeilplanGapWst          { get; private set; }

        // Je Lösungs-Label: Anzahl Unterrichte (UNr) mit unverplanten Stunden
        // und Summe dieser Stunden – bezogen auf die aktuelle UV. Steuert die
        // Vorauswahl des Teilplan-Häkchens. null = keine Information.
        private readonly IReadOnlyDictionary<string, (int unr, int stunden)> _unverplantJeLabel;

        // true, sobald der Nutzer das Häkchen selbst angeklickt hat – danach
        // wird es beim Wechsel des Ausgangsplans nicht mehr automatisch gesetzt.
        private bool _teilplanManuell;

        public MinimalAenderungDialog(
            List<string> lösungsLabels,
            IReadOnlyDictionary<string, (int unr, int stunden)> unverplantJeLabel = null,
            int teilplanGapVorgabe = 0)
        {
            InitializeComponent();

            _unverplantJeLabel = unverplantJeLabel;
            TxtTeilplanGapK.Text = Math.Max(0, teilplanGapVorgabe).ToString();

            foreach (var label in lösungsLabels)
                CmbLösung.Items.Add(label);

            if (CmbLösung.Items.Count > 0)
                CmbLösung.SelectedIndex = 0;
            else
                AktualisiereTeilplanAnzeige();
        }

        private void CmbLösung_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AktualisiereTeilplanAnzeige();
        }

        private void ChkTeilplan_Click(object sender, RoutedEventArgs e)
        {
            _teilplanManuell = true;
            AktualisiereTeilplanAnzeige();
        }

        // Hinweistext + Vorauswahl je nach gewähltem Ausgangsplan.
        private void AktualisiereTeilplanAnzeige()
        {
            if (ChkTeilplan == null || LblTeilplanHinweis == null || PanelTeilplanGap == null) return;

            string label = CmbLösung.SelectedItem?.ToString();
            (int unr, int stunden) unverplant = (0, 0);
            bool bekannt = label != null && _unverplantJeLabel != null &&
                           _unverplantJeLabel.TryGetValue(label, out unverplant);

            if (!_teilplanManuell && bekannt)
                ChkTeilplan.IsChecked = unverplant.unr > 0;

            bool teil = ChkTeilplan.IsChecked == true;
            PanelTeilplanGap.IsEnabled = teil;

            string text;
            if (!bekannt)
                text = "Mit Teilplan verplant der Solver möglichst viele Unterrichte; " +
                       "was nicht fehlerfrei passt, bleibt offen (im Plan-Editor sichtbar).";
            else if (unverplant.unr > 0)
                text = $"Der Ausgangsplan enthält {unverplant.unr} Unterricht(e) mit zusammen " +
                       $"{unverplant.stunden} unverplanten Stunde(n) (z. B. aus einem Teilplan oder neu in der UV). " +
                       (teil
                           ? "Mit Teilplan werden davon so viele wie möglich verplant, der Rest bleibt offen."
                           : "Ohne Teilplan müssen alle verplant werden – sonst findet der Solver keine Lösung.");
            else
                text = "Der Ausgangsplan ist vollständig verplant. " +
                       (teil
                           ? "Mit Teilplan darf der Solver Unterrichte fallen lassen, falls der Plan sonst unlösbar ist."
                           : "Teilplan ist nur nötig, wenn der Plan mit den aktuellen Vorgaben unlösbar ist.");
            if (teil)
                text += " Die Lösungen heißen NKT_1, NKT_2 …";

            LblTeilplanHinweis.Text = text;
        }

        private void BtnStarten_Click(object sender, RoutedEventArgs e)
        {
            if (CmbLösung.SelectedItem == null)
            {
                MessageBox.Show("Bitte eine Ausgangslösung wählen.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtGewicht.Text, out int gewicht) || gewicht < 1 || gewicht > 200)
            {
                MessageBox.Show("Stabilitätsgewicht muss eine Zahl zwischen 1 und 200 sein.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtZeitlimit.Text, out int zeitlimit) || zeitlimit < 1)
            {
                MessageBox.Show("Ungültiges Zeitlimit.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtAnzahl.Text, out int anzahl) || anzahl < 1 || anzahl > 10)
            {
                MessageBox.Show("Anzahl Lösungen muss zwischen 1 und 10 liegen.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool teilplan = ChkTeilplan.IsChecked == true;
            int gapK = 0;
            if (teilplan &&
                (!int.TryParse(TxtTeilplanGapK.Text.Trim(), out gapK) || gapK < 0))
            {
                MessageBox.Show("Phase-A-Gap K muss eine ganze Zahl ≥ 0 sein.", "Fehler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GewählterAusgangsLabel  = CmbLösung.SelectedItem.ToString();
            StabilitaetsGewicht     = gewicht;
            ZeitlimitSekunden       = zeitlimit;
            AnzahlLoesungen         = anzahl;
            ExportiereAbweichungen  = ChkAbweichung.IsChecked == true;
            TeilplanAktiv           = teilplan;
            TeilplanGapWst          = teilplan ? gapK : 0;

            DialogResult = true;
            Close();
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
