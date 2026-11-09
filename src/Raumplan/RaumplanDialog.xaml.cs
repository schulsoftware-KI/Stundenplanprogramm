#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Stundenplan_V2.Raumplanung
{
    /// <summary>Zeile im Reiter „Übersicht".</summary>
    public sealed class RaumZahl
    {
        public RaumZahl(string bereich, string angabe, string wert) { Bereich = bereich; Angabe = angabe; Wert = wert; }
        public string Bereich { get; }
        public string Angabe { get; }
        public string Wert { get; }
    }

    /// <summary>
    /// Knopf 25 „Räume zuweisen": liest die Plandatei, berechnet sofort eine Vorschau der
    /// Raumverteilung für die gewählte Plan-Spalte und speichert sie auf Wunsch ins Blatt
    /// „Raumplan" (Zeilen anderer Pläne bleiben). Fehlt das Blatt „Räume", wird es dabei aus
    /// der UV angelegt; fehlen dort Räume aus der UV, werden sie unten angehängt.
    /// </summary>
    public partial class RaumplanDialog : Window
    {
        private readonly string _planPfad;
        private readonly Action<string>? _log;

        private RaumEingabe? _eingabe;
        private RaumVerzeichnis? _verzeichnis;
        private RaumErgebnis? _ergebnis;
        private bool _laedt;

        private static readonly Dictionary<string, string> HerkunftText = new()
        {
            [RaumHerkunft.Fachraum] = "Fachraum laut UV",
            [RaumHerkunft.Stammraum] = "Stammraum laut UV",
            [RaumHerkunft.Abgeleitet] = "Stammraum der Klasse (keine Raumangabe in der UV)",
            [RaumHerkunft.Fachgruppe] = "anderer Raum derselben Fachgruppe",
            [RaumHerkunft.Zusatzraum] = "Zusatzraum (z. B. Halle zum Ausweichen beim Schwimmen)",
            [RaumHerkunft.Ausweich] = "freier Ausweichraum",
            [RaumHerkunft.Fest] = "fest (Fix X im Blatt „Raumplan“)",
            [RaumHerkunft.OhneRaum] = "ohne Raum (kein freier Raum gefunden)"
        };

        public RaumplanDialog(string planPfad, Action<string>? log = null)
        {
            InitializeComponent();
            _planPfad = planPfad ?? "";
            _log = log;
            Loaded += (_, __) => Laden(null);
        }

        // =====================================================
        //  Lesen und berechnen
        // =====================================================

        private void Laden(string? planSpalte)
        {
            RaumEingabe eingabe;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                eingabe = RaumplanStore.Lies(_planPfad, planSpalte);
                _verzeichnis = RaumVerteilung.BaueVerzeichnis(eingabe);
                _eingabe = eingabe;
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                _eingabe = null; _verzeichnis = null; _ergebnis = null;
                TxtStand.Text = "Die Plandatei konnte nicht gelesen werden.";
                BrdStand.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xE0));
                BtnSpeichern.IsEnabled = false; BtnRaeume.IsEnabled = false; BtnBerechnen.IsEnabled = false;
                MessageBox.Show(this, "Die Plandatei konnte nicht gelesen werden:\n\n" + ex.Message +
                                      "\n\n(Ist sie gerade in Excel geöffnet?)",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            _laedt = true;
            CbPlan.ItemsSource = eingabe.PlanSpalten;
            CbPlan.SelectedItem = eingabe.PlanSpalten.FirstOrDefault(
                p => string.Equals(p, eingabe.PlanSpalte, StringComparison.OrdinalIgnoreCase));
            bool hatRaumplan = ZeilenDesPlans().Any();
            RbBehalten.IsChecked = hatRaumplan;
            RbNeu.IsChecked = !hatRaumplan;
            _laedt = false;

            BtnBerechnen.IsEnabled = true;
            ZeigeRaeume();
            Berechne();
        }

        private IEnumerable<RaumZeile> ZeilenDesPlans()
        {
            var e = _eingabe;
            return e == null
                ? Enumerable.Empty<RaumZeile>()
                : e.Raumplan.Where(z => string.Equals(z.Plan, e.PlanSpalte, StringComparison.OrdinalIgnoreCase));
        }

        private void Berechne()
        {
            if (_eingabe == null || _verzeichnis == null) return;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                _ergebnis = RaumVerteilung.Verteile(_eingabe, _verzeichnis, RbBehalten.IsChecked == true);
            }
            catch (Exception ex)
            {
                _ergebnis = null;
                BtnSpeichern.IsEnabled = false;
                MessageBox.Show(this, "Die Räume konnten nicht verteilt werden:\n\n" + ex.Message,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            ZeigeStand();
            ZeigeZahlen();
            ZeigeZeilen();
            ZeigeHinweise();
            BtnSpeichern.IsEnabled = true;
            int ohne = _ergebnis.Anzahl(RaumHerkunft.OhneRaum);
            TxtStatus.Text = $"Vorschau für Plan „{_ergebnis.PlanSpalte}“: {_ergebnis.Belegungen} Raumbelegungen" +
                             (ohne > 0 ? $", {ohne} ohne Raum" : ", alle mit Raum") + " – noch nicht gespeichert.";
        }

        // =====================================================
        //  Anzeigen
        // =====================================================

        private void ZeigeStand()
        {
            if (_eingabe == null || _verzeichnis == null) return;
            var zeilen = ZeilenDesPlans().ToList();
            int andere = _eingabe.Raumplan.Select(z => z.Plan).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(p => !string.Equals(p, _eingabe.PlanSpalte, StringComparison.OrdinalIgnoreCase));

            string raumplan = zeilen.Count == 0
                ? $"Für Plan „{_eingabe.PlanSpalte}“ gibt es noch keinen Raumplan."
                : $"Blatt „Raumplan“: {zeilen.Count} Zeilen für Plan „{_eingabe.PlanSpalte}“" +
                  (zeilen.Any(z => z.Fix) ? $", davon {zeilen.Count(z => z.Fix)} fest (Fix X)" : "") + ".";
            if (andere > 0) raumplan += $" Weitere Pläne im Blatt: {andere} (bleiben unverändert).";

            int ergaenzt = _verzeichnis.Ergaenzte.Count();
            string raeume = _verzeichnis.AusBlatt
                ? $"Blatt „Räume“: {_verzeichnis.Raeume.Count - ergaenzt} Räume" +
                  (ergaenzt > 0 ? $", {ergaenzt} weitere aus der UV erkannt (werden beim Speichern angehängt)." : ".")
                : $"Blatt „Räume“ fehlt – {_verzeichnis.Raeume.Count} Räume aus der UV erkannt (wird beim Speichern angelegt; bitte danach prüfen).";

            int hinweise = AlleHinweise().Count;
            TxtStand.Text = raumplan + "\n" + raeume + (hinweise > 0 ? $"\n{hinweise} Hinweise – siehe Reiter „Hinweise“." : "");
            bool gelb = hinweise > 0 || !_verzeichnis.AusBlatt
                        || (_ergebnis != null && _ergebnis.Anzahl(RaumHerkunft.OhneRaum) > 0);
            BrdStand.Background = gelb
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCC))
                : new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));

            BtnRaeume.IsEnabled = !_verzeichnis.AusBlatt || ergaenzt > 0;
            BtnRaeume.Content = _verzeichnis.AusBlatt ? "Fehlende Räume im Blatt „Räume“ ergänzen" : "Blatt „Räume“ anlegen";
        }

        private void ZeigeZahlen()
        {
            if (_eingabe == null || _verzeichnis == null || _ergebnis == null) return;
            var r = _ergebnis;
            var l = new List<RaumZahl>
            {
                new("Plan", "Plan-Spalte aus „Lös“", r.PlanSpalte),
                new("Plan", "Stunden im Raster", _eingabe.Slots.Count.ToString()),
                new("Plan", "Modus", r.Behalten ? "behalten und ergänzen" : "neu verteilen"),
                new("Raumbelegungen", "gesamt (Unterricht × Raum × Stunde)", r.Belegungen.ToString())
            };
            foreach (var h in RaumHerkunft.Alle)
            {
                int n = r.Anzahl(h);
                if (n > 0 || h == RaumHerkunft.OhneRaum)
                    l.Add(new RaumZahl("Raumbelegungen", HerkunftText.TryGetValue(h, out var t) ? t : h, n.ToString()));
            }
            if (r.Behalten)
            {
                l.Add(new RaumZahl("Behalten", "aus dem bisherigen Raumplan übernommen", r.Uebernommen.ToString()));
                l.Add(new RaumZahl("Behalten", "neu verteilt (neue oder verschobene Stunden)", r.Neu.ToString()));
            }
            l.Add(new RaumZahl("UV", "Unterrichtsstunden ohne Raumangabe (AGs u. Ä., bleiben ohne Raum)",
                                r.TeileOhneAngabe.ToString()));
            if (r.UnbekannteUNr > 0)
                l.Add(new RaumZahl("UV", "UNr im Plan, die in der UV fehlen", r.UnbekannteUNr.ToString()));
            if (r.FesteVerworfen > 0)
                l.Add(new RaumZahl("Fest", "feste Zeilen, die nicht mehr zum Plan passen (entfallen)", r.FesteVerworfen.ToString()));
            if (r.FesteKonflikte > 0)
                l.Add(new RaumZahl("Fest", "Konflikte zwischen festen Zeilen (Raum doppelt)", r.FesteKonflikte.ToString()));
            l.Add(new RaumZahl("Räume", "Räume im Raumverzeichnis", _verzeichnis.Raeume.Count.ToString()));
            foreach (var g in _verzeichnis.Raeume.GroupBy(x => x.ArtText).OrderBy(g => g.Key))
                l.Add(new RaumZahl("Räume", g.Key, g.Count().ToString()));
            DgZahlen.ItemsSource = l;
        }

        private void ZeigeZeilen()
        {
            if (_ergebnis == null) { DgZeilen.ItemsSource = null; return; }
            string f = TxtFilter.Text.Trim();
            bool nurPruef = ChkNurPruef.IsChecked == true;
            var liste = _ergebnis.Zeilen
                .Where(z => !nurPruef || z.IstPruefFall)
                .Where(z => f.Length == 0 || Passt(z, f))
                .ToList();
            DgZeilen.ItemsSource = liste;
            int pruef = _ergebnis.Zeilen.Count(z => z.IstPruefFall);
            TxtZeilenKopf.Text = $"Zuweisungen ({pruef} zum Prüfen)";
            TxtTreffer.Text = $"{liste.Count} von {_ergebnis.Zeilen.Count} Zeilen";
        }

        private static bool Passt(RaumZeile z, string f)
        {
            bool In(string s) => s.Contains(f, StringComparison.OrdinalIgnoreCase);
            return In(z.Raum) || In(z.Wunschraum) || In(z.Klassen) || In(z.Lehrer) || In(z.Fach)
                   || In(z.WTag) || In(z.Herkunft) || z.UNr.ToString() == f;
        }

        private void ZeigeRaeume()
        {
            if (_verzeichnis == null) return;
            DgRaeume.ItemsSource = _verzeichnis.Raeume.ToList();
            TxtRaeumeKopf.Text = $"Räume ({_verzeichnis.Raeume.Count})";
        }

        private List<string> AlleHinweise()
        {
            var l = new List<string>();
            if (_eingabe != null) l.AddRange(_eingabe.Hinweise);
            if (_verzeichnis != null) l.AddRange(_verzeichnis.Hinweise);
            if (_ergebnis != null) l.AddRange(_ergebnis.Meldungen);
            return l;
        }

        private void ZeigeHinweise()
        {
            var l = AlleHinweise();
            TxtHinweiseKopf.Text = l.Count > 0 ? $"Hinweise ({l.Count})" : "Hinweise";
            TxtHinweise.Text = l.Count > 0
                ? string.Join("\n", l.Select(x => "• " + x))
                : "Keine Hinweise.";
        }

        // =====================================================
        //  Ereignisse
        // =====================================================

        private void CbPlan_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_laedt || _eingabe == null) return;
            if (CbPlan.SelectedItem is string sp &&
                !string.Equals(sp, _eingabe.PlanSpalte, StringComparison.OrdinalIgnoreCase))
                Laden(sp);
        }

        private void Modus_Checked(object sender, RoutedEventArgs e)
        {
            if (_laedt) return;
            Berechne();
        }

        private void BtnBerechnen_Click(object sender, RoutedEventArgs e)
            => Laden(_eingabe?.PlanSpalte);   // Datei neu lesen (z. B. nach Änderungen in Excel)

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded) ZeigeZeilen();   // feuert sonst schon beim Aufbau (IsChecked="True" in der XAML)
        }

        private void TxtFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (IsLoaded) ZeigeZeilen();
        }

        private void BtnRaeume_Click(object sender, RoutedEventArgs e)
        {
            if (_verzeichnis == null || _eingabe == null) return;
            string frage = _verzeichnis.AusBlatt
                ? $"{_verzeichnis.Ergaenzte.Count()} Räume aus der UV unten im Blatt „Räume“ anhängen?"
                : $"Das Blatt „Räume“ mit {_verzeichnis.Raeume.Count} aus der UV erkannten Räumen anlegen?\n\n" +
                  "Danach kannst du es in Excel prüfen und ändern (Art, Fachgruppe, als Ausweich, Bereich, Reihenfolge). " +
                  "Der Raumplan wird dabei nicht gespeichert.";
            if (MessageBox.Show(this, frage, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                RaumplanStore.RaeumeSpeichern(_planPfad, _verzeichnis);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(this, SpeicherFehler(ex), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            _log?.Invoke($"Raumverzeichnis (Blatt „Räume“) gespeichert: {_verzeichnis.Raeume.Count} Räume.");
            Laden(_eingabe.PlanSpalte);
            TxtStatus.Text = "Blatt „Räume“ gespeichert. Die Vorschau wurde neu berechnet.";
        }

        private void BtnSpeichern_Click(object sender, RoutedEventArgs e)
        {
            if (_ergebnis == null || _verzeichnis == null || _eingabe == null) return;
            var r = _ergebnis;
            int alt = ZeilenDesPlans().Count();
            string frage =
                $"Raumplan für Plan „{r.PlanSpalte}“ speichern?\n\n" +
                $"{r.Belegungen} Raumbelegungen, davon {r.Anzahl(RaumHerkunft.OhneRaum)} ohne Raum.\n" +
                (alt > 0 ? $"Die bisherigen {alt} Zeilen dieses Plans im Blatt „Raumplan“ werden ersetzt " +
                           "(Zeilen mit Fix (X) stehen wieder drin).\n" : "") +
                (_verzeichnis.AusBlatt
                    ? (_verzeichnis.Ergaenzte.Any() ? $"Im Blatt „Räume“ werden {_verzeichnis.Ergaenzte.Count()} Räume angehängt.\n" : "")
                    : "Das Blatt „Räume“ wird aus der UV angelegt – bitte danach in Excel prüfen.\n") +
                "\nDie Plandatei darf dabei nicht in Excel geöffnet sein.";
            if (MessageBox.Show(this, frage, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                RaumplanStore.Speichern(_planPfad, r.PlanSpalte, r.Zeilen, _verzeichnis);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(this, SpeicherFehler(ex), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            _log?.Invoke($"Raumplan „{r.PlanSpalte}“ gespeichert (Blatt „Raumplan“): {r.Belegungen} Raumbelegungen – " +
                         string.Join(", ", RaumHerkunft.Alle.Where(h => r.Anzahl(h) > 0).Select(h => $"{h} {r.Anzahl(h)}")) +
                         $"; {r.TeileOhneAngabe} Unterrichtsstunden ohne Raumangabe in der UV.");
            Laden(r.PlanSpalte);
            TxtStatus.Text = $"Gespeichert: Blatt „Raumplan“, Plan „{r.PlanSpalte}“.";
            MessageBox.Show(this, $"Der Raumplan für „{r.PlanSpalte}“ steht jetzt im Blatt „Raumplan“ der Plandatei.\n\n" +
                                  "Räume von Hand ändern: in Excel die Spalte „Raum“ ändern und in „Fix (X)“ ein X setzen – " +
                                  "solche Zeilen bleiben bei jedem neuen Lauf erhalten.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string SpeicherFehler(Exception ex)
            => ex is IOException
                ? "Speichern fehlgeschlagen – ist die Plandatei gerade in Excel geöffnet?\n\n" + ex.Message
                : "Speichern fehlgeschlagen:\n\n" + ex.Message;

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e) => Close();
    }
}
