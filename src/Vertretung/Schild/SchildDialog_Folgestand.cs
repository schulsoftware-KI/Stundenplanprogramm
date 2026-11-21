#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// Phase 5.2b-2b-2 (Entscheidung 171): Knopf „Folgestand erzeugen…“ im Fenster „SchILD-Daten prüfen und importieren“.
    /// Nur bei einem gültigen Pseudo-Ordner nach einem Prüflauf ohne Fehler mit Zuordnung – die Änderungen rechnet
    /// <see cref="SchildFolgestand"/>. Das Log nennt nur Zahlen; Pseudo-Namen stehen nur im Fenster und in FOLGESTAND.txt.
    /// </summary>
    public partial class SchildDialog
    {
        /// <summary>Grund, aus dem kein Folgestand möglich ist (null = möglich).</summary>
        private string? FolgeVoraussetzung()
        {
            if (_export == null || _ergebnis == null)
                return "Zuerst einen Pseudo-Export wählen und den Prüflauf starten.";
            if (!_export.IstPseudo)
                return "Einen Folgestand gibt es nur für Pseudodaten (Ordner mit gültiger Pseudo-Markierung) – echte Exporte " +
                       "werden nie verändert.";
            int f = _ergebnis.Anzahl(SchildProblemArt.Fehler);
            if (f > 0) return $"Der Prüflauf hat {f} Fehler – erst beheben.";
            if (_planInfo == null || _ergebnis.Zuordnung == null || !_ergebnis.Zuordnung.Moeglich)
                return "Ohne Zuordnung SchILD ↔ Plan ist kein Folgestand möglich (Reiter „Zuordnung“).";
            return null;
        }

        private void FolgeAnzeigen()
        {
            if (BtnFolge == null) return;
            string? grund = FolgeVoraussetzung();
            BtnFolge.IsEnabled = !_laeuft && grund == null;
            BtnFolge.ToolTip = grund ??
                "Erzeugt aus diesem Pseudo-Export einen neuen Ordner „…_Folge1“ mit typischen Änderungen (Abgänge, Klassen- und " +
                "Kurswechsel, Namensänderung, Neuaufnahmen …) – zum Testen von Vorschau und Import.";
        }

        private async void BtnFolge_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft) return;
            if (!ZuordnungUngespeichertKlaeren("Folgestand")) return;
            string? grund = FolgeVoraussetzung();
            if (grund != null)
            {
                MessageBox.Show(this, grund, "Folgestand", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var ex = _export!;
            var erg = _ergebnis!;
            var plan = _planInfo!;
            string ziel = SchildFolgestand.Zielordner(ex.Ordner, out int nummer);
            var opt = FrageFolgeOptionen(ziel, nummer);
            if (opt == null) return;

            _laeuft = true;
            SetzeKnoepfe(false);
            TxtStatus.Text = "Erzeuge Folgestand …";
            SchildFolgestandErgebnis? fs = null;
            try
            {
                fs = await Task.Run(() => SchildFolgestand.Erzeuge(ex, erg, plan, ziel, nummer, opt));
            }
            catch (Exception fehler)
            {
                MessageBox.Show(this, "Folgestand fehlgeschlagen:\n\n" + fehler.Message, "Folgestand",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _laeuft = false;
                SetzeKnoepfe(true);
            }
            if (fs == null) { TxtStatus.Text = "Folgestand fehlgeschlagen."; return; }

            // Log ohne Namen: nur Zahlen und Ordner
            _log?.Invoke($"SchILD: Folgestand {fs.Nummer} erzeugt ({string.Join(", ", fs.Uebersicht)}) in {fs.Ordner}");
            string text = fs.Text() + $"\n\nDie Liste steht auch in {SchildFolgestand.TextDatei} im neuen Ordner." +
                          "\n\nJetzt den Prüflauf mit diesem Ordner starten?";
            if (MessageBox.Show(this, text, "Folgestand", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                TxtOrdner.Text = fs.Ordner;
                await PruefenAsync();
            }
            else TxtStatus.Text = $"Folgestand {fs.Nummer} erzeugt.";
        }

        /// <summary>Kleiner Dialog: welche Änderungen, Startwert (null = Abbruch).</summary>
        private SchildFolgestandOptionen? FrageFolgeOptionen(string ziel, int nummer)
        {
            var win = new Window
            {
                Title = "Folgestand erzeugen",
                Width = 620,
                SizeToContent = SizeToContent.Height,
                MaxHeight = SystemParameters.WorkArea.Height - 40,   // 5.2c-1: mehr Häkchen – notfalls scrollen
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Erzeugt aus dem geprüften Pseudo-Export einen neuen Ordner mit typischen Änderungen zwischen zwei " +
                       "SchILD-Exporten (nur Pseudodaten). Der Ausgangsordner bleibt unverändert. Die Vorschau sollte danach " +
                       "genau diese Änderungen zeigen, wenn der Ausgangsordner importiert ist.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(new TextBlock { Text = $"Neuer Ordner (Folgestand {nummer}):", Margin = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(new TextBox
            {
                Text = ziel, IsReadOnly = true, Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)),
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(new TextBlock { Text = "Änderungen:", Margin = new Thickness(0, 0, 0, 4) });

            CheckBox Wahl(string text, bool an)
            {
                // Text mit Umbruch – sonst werden lange Texte rechts abgeschnitten (Nachbesserung 20.11.2026)
                var cb = new CheckBox
                {
                    Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = an,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                panel.Children.Add(cb);
                return cb;
            }
            var chkAbgang = Wahl("3 Abgänge (Status 9) – der erste ist der erste Schüler der ersten Klasse (in der Entwicklung meist mit dem " +
                                 "Testkonto „s05A“ verknüpft)", true);
            var chkKlasse = Wahl("2 Klassenwechsel Sek I", true);
            var chkKurs = Wahl("3 Kurswechsel Oberstufe (ohne Überschneidung im Stundenplan)", true);
            var chkGruppe = Wahl("1 Gruppenwechsel Sek I (parallele Gruppe, z. B. Religion)", true);
            var chkName = Wahl("1 Namensänderung (Sek I)", true);
            var chkGeb = Wahl("1 korrigiertes Geburtsdatum (Oberstufe)", true);
            var chkNeu = Wahl("2 Neuaufnahmen (Status 0, Aufnahme in zwei Wochen)", true);
            var chkFehlt = Wahl("1 Schüler fehlt im Export (ohne Abgang)", true);
            var chkKlasseFehlt = Wahl("eine ganze Klasse fehlt (prüft die Sperre „Teil-Export“ – dieser Stand lässt sich nicht " +
                                      "importieren)", false);
            chkKlasseFehlt.Margin = new Thickness(0, 6, 0, 10);

            // 5.2c-1 (Entscheidungen 186–190): „dieselbe Person?“ und Neu-Zuordnung – Standard aus
            panel.Children.Add(new TextBlock
            {
                Text = "Für „dieselbe Person?“ und die Neu-Zuordnung (5.2c):", FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var chkOhneId = Wahl("Änderungen ohne SchILD-ID (die Schüler mit geändertem Namen bzw. Geburtsdatum fehlen in " +
                                 "SchuelerLernplattformen.dat) – die Vorschau erkennt sie dann nur als Paar „dieselbe Person?“", false);
            var chkVorname = Wahl("1 Vorname korrigiert (Sek I)", false);
            var chkNameKlasse = Wahl("1 Nachname geändert + Klassenwechsel (Sek I)", false);
            var chkGleich = Wahl("1 Neuaufnahme mit demselben Namen wie ein Mitschüler derselben Klasse (Namensgleiche, anderes " +
                                 "Geburtsdatum)", false);
            chkGleich.Margin = new Thickness(0, 0, 0, 10);

            var startZeile = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            startZeile.Children.Add(new TextBlock { Text = "Startwert für den Zufall:", VerticalAlignment = VerticalAlignment.Center,
                                                    Margin = new Thickness(0, 0, 6, 0) });
            var txtStart = new TextBox { Text = "171", Width = 70 };
            startZeile.Children.Add(txtStart);
            panel.Children.Add(startZeile);
            panel.Children.Add(new TextBlock
            {
                Text = "Gleicher Ausgangsordner + gleicher Startwert = gleicher Folgestand.",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4)
            });

            var knoepfe = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var ok = new Button { Content = "Erzeugen", Width = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var abbr = new Button { Content = "Abbrechen", Width = 100, IsCancel = true };
            knoepfe.Children.Add(ok);
            knoepfe.Children.Add(abbr);
            panel.Children.Add(knoepfe);
            win.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            SchildFolgestandOptionen? ergebnis = null;
            ok.Click += (_, __) =>
            {
                if (!int.TryParse(txtStart.Text.Trim(), out int start))
                {
                    MessageBox.Show(win, "Der Startwert muss eine ganze Zahl sein.", "Folgestand",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var o = new SchildFolgestandOptionen
                {
                    Abgaenge = chkAbgang.IsChecked == true,
                    Klassenwechsel = chkKlasse.IsChecked == true,
                    Kurswechsel = chkKurs.IsChecked == true,
                    Gruppenwechsel = chkGruppe.IsChecked == true,
                    Namensaenderung = chkName.IsChecked == true,
                    Geburtsdatum = chkGeb.IsChecked == true,
                    Neuaufnahmen = chkNeu.IsChecked == true,
                    FehltOhneAbgang = chkFehlt.IsChecked == true,
                    KlasseFehlt = chkKlasseFehlt.IsChecked == true,
                    Startwert = start,
                    OhneSchildId = chkOhneId.IsChecked == true,
                    Vorname = chkVorname.IsChecked == true,
                    NameUndKlasse = chkNameKlasse.IsChecked == true,
                    Namensgleiche = chkGleich.IsChecked == true
                };
                if (!(o.Abgaenge || o.Klassenwechsel || o.Kurswechsel || o.Gruppenwechsel || o.Namensaenderung || o.Geburtsdatum
                      || o.Neuaufnahmen || o.FehltOhneAbgang || o.KlasseFehlt || o.Vorname || o.NameUndKlasse || o.Namensgleiche
                      || o.OhneSchildId))
                {
                    MessageBox.Show(win, "Bitte mindestens eine Änderung wählen.", "Folgestand",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                ergebnis = o;
                win.DialogResult = true;
            };

            return win.ShowDialog() == true ? ergebnis : null;
        }
    }
}
