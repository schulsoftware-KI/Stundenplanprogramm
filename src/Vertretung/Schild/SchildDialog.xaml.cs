#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// Phase 5.2a: Fenster „SchILD-Daten prüfen“ (Knopf „SchILD-Daten…“ im Wocheneditor).
    /// Prüflauf eines Ordners mit SchILD-NRW-3-Dateien – liest nur, sendet nichts. Gespeichert wird nur auf
    /// Wunsch: die Formatprofil-Vorlage, ein Pseudo-Export und (5.2a-2) die Festlegungen der Zuordnung
    /// (schild_zuordnung.json, ohne Personendaten; Reiter „Zuordnung“ in SchildDialog_Zuordnung.cs).
    /// Die gelesenen Daten liegen nur im Arbeitsspeicher und werden beim Schließen freigegeben.
    /// </summary>
    public partial class SchildDialog : Window
    {
        private readonly string _planOrdner;
        private readonly string _schuljahr;
        private readonly Func<(GrundplanPushDto? Plan, string? Hinweis)> _planHolen;
        private readonly Action<string>? _log;

        private SchildProfil _profil = SchildProfil.Standard();
        private string _profilQuelle = "";
        private SchildExport? _export;
        private SchildPruefErgebnis? _ergebnis;
        private SchildPlanInfo? _planInfo;
        private bool _laeuft;
        private bool _abschnittSetzen;

        private const string DateienHilfe =
            "Welche Dateien in SchILD-NRW 3 exportieren?\n\n" +
            "Pflicht: Klassen.dat · Kurse.dat · SchuelerBasisdaten.dat · SchuelerLeistungsdaten.dat\n" +
            "Optional: SchuelerLernplattformen.dat (enthält die SchILD-ID) · Lehrkraefte.dat · Faecher.dat · " +
            "Jahrgaenge.dat · KurseLehrkraefte.dat\n\n" +
            "Bitte NICHT auswählen (wird nicht gebraucht und nicht gelesen): SchuelerZusatzdaten, SchuelerAdressen, " +
            "SchuelerErzieher, SchuelerTelefonnummern, SchuelerVermerke, SchuelerLernabschnittsdaten, " +
            "SchuelerSprachenfolgen, SchuelerAbitur und alle übrigen.\n\n" +
            "Format: Textdateien mit Kopfzeile, Trennzeichen „|“, Zeichensatz ANSI oder UTF-8 mit BOM. " +
            "Die Dateinamen müssen genau so heißen wie oben.\n\n" +
            "Was gelesen wird: Klassen (Bezeichnung, Jahrgang, Folgeklasse, Klassenleitung), Kurse (Bezeichnung, Klasse, " +
            "Jahrgang, Fach, Kursart, Lehrkräfte), Schüler (Name, Vorname, Geburtsdatum, Status, Klasse, Jahrgang; das " +
            "Aufnahmedatum nur bei Neuaufnahmen), Leistungsdaten (Fach, Fachlehrer, Kursart, Kurs), SchILD-ID, Namen der " +
            "Lehrkräfte, Fachbezeichnungen. Alles andere – Noten, Adressen, Konfession, Förderschwerpunkte, " +
            "Kennwörter der Lernplattform, Adressen und Geburtsdaten der Lehrkräfte – wird beim Lesen verworfen.\n\n" +
            "Ablage: Echte Exporte nur auf dem Verwaltungsrechner speichern – nicht in OneDrive, Teams oder einem " +
            "anderen Cloud-Ordner – und löschen, wenn sie nicht mehr gebraucht werden.\n\n" +
            "Zuordnung (Reiter „Zuordnung“): Das Programm ordnet SchILD-Kurse, Sek-I-Gruppen (auch ohne Kurs über Klasse + " +
            "Fach + Fachlehrer), Lehrkräftekürzel und Fächer dem Stundenplan zu – meist automatisch. Kürzel und Fächer, die es " +
            "nur auf einer Seite gibt, werden vorgeschlagen („Vorschläge übernehmen“). Was offen bleibt, dort festlegen und mit " +
            "„Zuordnungen speichern“ sichern: schild_zuordnung.json neben der Plandatei (für Pseudodaten " +
            "schild_zuordnung_pseudo.json). Die Datei enthält nur Kürzel, Fächer, Kurs- und Klassenbezeichnungen – keine " +
            "Personendaten.\n\n" +
            "Der Bericht („Bericht kopieren“) enthält keine Schülerdaten: nur Zahlen, Zeilennummern, Klassen-, Kurs- " +
            "und Fachbezeichnungen sowie Lehrkräftekürzel.";

        public SchildDialog(string planOrdner, string schuljahr,
                            Func<(GrundplanPushDto? Plan, string? Hinweis)> planHolen, Action<string>? log = null)
        {
            InitializeComponent();
            _planOrdner = planOrdner ?? "";
            _schuljahr = schuljahr ?? "";
            _planHolen = planHolen;
            _log = log;
            TxtDateienHilfe.Text = DateienHilfe;
            LadeProfil();
        }

        // =====================================================
        //  Profil
        // =====================================================

        private void LadeProfil()
        {
            _profil = SchildProfil.Laden(_planOrdner, out _profilQuelle, out string? fehler);
            TxtProfil.Text = "Formatprofil: " + _profilQuelle;
            if (fehler != null)
                MessageBox.Show(this, fehler, "Formatprofil", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void BtnProfil_Click(object sender, RoutedEventArgs e)
        {
            string pfad = Path.Combine(_planOrdner, SchildProfil.DateiName);
            if (File.Exists(pfad))
            {
                MessageBox.Show(this, "Es gibt schon ein eigenes Formatprofil:\n" + pfad +
                    "\n\nÄnderungen daran gelten beim nächsten Prüflauf. Zum Zurücksetzen auf den Standard die Datei löschen.",
                    "Formatprofil", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this,
                    "Das Standard-Formatprofil (amtliche Beschreibung) als Datei neben der Plandatei anlegen?\n\n" +
                    "Das ist nur nötig, wenn ein echter Export davon abweicht – z. B. andere Dateinamen, " +
                    "Spaltenüberschriften oder ein fester Zeichensatz. Dann nur diese Angaben in der Datei ändern.",
                    "Formatprofil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try
            {
                string neu = SchildProfil.VorlageSpeichern(_planOrdner);
                MessageBox.Show(this, "Angelegt:\n" + neu, "Formatprofil", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Konnte nicht angelegt werden:\n\n" + ex.Message, "Formatprofil",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            LadeProfil();
        }

        // =====================================================
        //  Prüflauf
        // =====================================================

        private async void BtnOrdner_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft || !ZuordnungUngespeichertKlaeren("Wechsel des Ordners")) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner mit den SchILD-Dateien (.dat) wählen" };
            if (Directory.Exists(TxtOrdner.Text)) dlg.InitialDirectory = TxtOrdner.Text;
            else if (Directory.Exists(_planOrdner)) dlg.InitialDirectory = _planOrdner;
            if (dlg.ShowDialog(this) != true) return;
            TxtOrdner.Text = dlg.FolderName;
            await PruefenAsync();
        }

        private async void BtnPruefen_Click(object sender, RoutedEventArgs e) => await PruefenAsync();

        private async Task PruefenAsync()
        {
            if (_laeuft) return;
            string ordner = TxtOrdner.Text.Trim();
            if (ordner.Length == 0 || !Directory.Exists(ordner))
            {
                MessageBox.Show(this, "Bitte zuerst einen Ordner mit den SchILD-Dateien wählen.", "SchILD-Daten",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!ZuordnungUngespeichertKlaeren("neuen Prüflauf")) return;

            LadeProfil();
            // Plan für Abgleich und Zuordnung (auf dem UI-Thread, nutzt die Auswahl im Wocheneditor). Der Hinweis ist
            // gesetzt, wenn die Schülergruppen der UV nicht lesbar waren – dann ist keine Zuordnung möglich.
            _planInfo = null;
            try
            {
                var (plan, hinweis) = _planHolen();
                if (plan != null) _planInfo = SchildPlanInfo.Aus(plan, hinweis);
            }
            catch { _planInfo = null; }

            _laeuft = true;
            SetzeKnoepfe(false);
            TxtStatus.Text = "Lese Dateien …";
            SchildExport? export = null;
            try
            {
                var profil = _profil;
                export = await Task.Run(() => SchildLeser.Lies(ordner, profil));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Lesen fehlgeschlagen:\n\n" + ex.Message, "SchILD-Daten",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _laeuft = false;
                SetzeKnoepfe(true);
            }

            _export = export;
            if (_export == null) { TxtStatus.Text = "Prüflauf fehlgeschlagen."; return; }
            LadeZuordnung(_export.IstPseudo);
            _zuErsteAnzeige = true;

            TxtArt.Text = _export.PseudoText;
            BrdArt.Background = new SolidColorBrush(_export.IstPseudo
                ? Color.FromRgb(0xDC, 0xEF, 0xD9) : Color.FromRgb(0xFF, 0xF1, 0xCC));

            var abschnitte = SchildPruefung.Abschnitte(_export);
            _abschnittSetzen = true;
            CbAbschnitt.ItemsSource = abschnitte;
            CbAbschnitt.SelectedIndex = abschnitte.Count > 0 ? 0 : -1;
            _abschnittSetzen = false;
            ZeigeErgebnis();

            if (_ergebnis != null)
                _log?.Invoke($"SchILD-Prüflauf ({(_export.IstPseudo ? "Pseudodaten" : "echte Daten")}, " +
                             $"{_ergebnis.Jahr}/{_ergebnis.Abschnitt}): {_ergebnis.Anzahl(SchildProblemArt.Fehler)} Fehler, " +
                             $"{_ergebnis.Anzahl(SchildProblemArt.Warnung)} Warnungen.");
        }

        private void CbAbschnitt_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_abschnittSetzen || _export == null || _laeuft) return;
            ZeigeErgebnis();
        }

        private void ZeigeErgebnis()
        {
            if (_export == null) return;
            int jahr, abschnitt;
            if (CbAbschnitt.SelectedItem is SchildAbschnitt a) { jahr = a.Jahr; abschnitt = a.Abschnitt; }
            else { jahr = SchildGenerator.Jahr(_schuljahr); abschnitt = 1; }

            _ergebnis = SchildPruefung.Pruefe(_export, jahr, abschnitt, _planInfo, _zuordnung);
            DgZahlen.ItemsSource = _ergebnis.Zahlen;
            FiltereProbleme();

            int f = _ergebnis.Anzahl(SchildProblemArt.Fehler);
            int w = _ergebnis.Anzahl(SchildProblemArt.Warnung);
            int h = _ergebnis.Anzahl(SchildProblemArt.Hinweis);
            TxtProblemeKopf.Text = $"Probleme ({f} Fehler · {w} Warnungen · {h} Hinweise)";
            TxtStatus.Text = f > 0 ? $"Prüflauf: {f} Fehler – siehe „Probleme“."
                           : w > 0 ? $"Prüflauf ohne Fehler, {w} Warnung(en)."
                           : "Prüflauf ohne Fehler und Warnungen.";
            BtnBericht.IsEnabled = true;
            ZeigeZuordnung();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => FiltereProbleme();

        private void FiltereProbleme()
        {
            if (_ergebnis == null || DgProbleme == null) return;
            bool f = ChkFehler?.IsChecked == true, w = ChkWarnungen?.IsChecked == true, h = ChkHinweise?.IsChecked == true;
            DgProbleme.ItemsSource = _ergebnis.Probleme.Where(p =>
                (p.Art == SchildProblemArt.Fehler && f) ||
                (p.Art == SchildProblemArt.Warnung && w) ||
                (p.Art == SchildProblemArt.Hinweis && h)).ToList();
        }

        private void BtnBericht_Click(object sender, RoutedEventArgs e)
        {
            if (_ergebnis == null || _export == null) return;
            try
            {
                Clipboard.SetText(_ergebnis.Bericht(_export, _profilQuelle, _planInfo?.Bezeichnung));
                TxtStatus.Text = "Bericht in die Zwischenablage kopiert (ohne Schülerdaten).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Zwischenablage nicht verfügbar:\n\n" + ex.Message, "Bericht",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // =====================================================
        //  Pseudo-Export
        // =====================================================

        private async void BtnPseudo_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft) return;
            var opt = FrageGeneratorOptionen();
            if (opt == null) return;

            GrundplanPushDto? plan;
            string? hinweis;
            try { (plan, hinweis) = _planHolen(); }
            catch (Exception ex) { plan = null; hinweis = ex.Message; }
            if (plan == null)
            {
                MessageBox.Show(this, "Der Grundplan konnte nicht aufgebaut werden:\n\n" + (hinweis ?? "unbekannter Fehler"),
                    "Pseudo-Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (hinweis != null && MessageBox.Show(this, "⚠ " + hinweis +
                    "\n\nOhne Schülergruppen fehlen die Sek-I-Gruppen, und die Kursschlüssel weichen ab. Trotzdem erzeugen?",
                    "Pseudo-Export", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var stand = SchildGenerator.PruefeZielordner(opt.Zielordner);
            if (stand == SchildZielordnerStand.Fremd)
            {
                MessageBox.Show(this, "Der Ordner enthält .dat-Dateien, die nicht aus einem Pseudo-Export stammen:\n" +
                    opt.Zielordner + "\n\nBitte einen leeren Ordner wählen – echte Exporte werden nie überschrieben.",
                    "Pseudo-Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (stand == SchildZielordnerStand.AlterPseudoExport &&
                MessageBox.Show(this, "Im Ordner liegt schon ein Pseudo-Export. Ersetzen?\n" + opt.Zielordner,
                    "Pseudo-Export", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _laeuft = true;
            SetzeKnoepfe(false);
            TxtStatus.Text = "Erzeuge Pseudo-Export …";
            SchildGeneratorErgebnis? erg = null;
            try
            {
                GrundplanPushDto p = plan;
                string sj = _schuljahr;
                erg = await Task.Run(() => SchildGenerator.Erzeuge(p, sj, opt));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Pseudo-Export fehlgeschlagen:\n\n" + ex.Message, "Pseudo-Export",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _laeuft = false;
                SetzeKnoepfe(true);
            }
            if (erg == null) { TxtStatus.Text = "Pseudo-Export fehlgeschlagen."; return; }

            _log?.Invoke($"SchILD: Pseudo-Export erzeugt ({erg.Schueler} Pseudo-Schüler, Plan „{plan.Bezeichnung}“) in {opt.Zielordner}");
            string text =
                $"Pseudo-Export erzeugt:\n{opt.Zielordner}\n\n" +
                $"{erg.Schueler} Schüler · {erg.Klassen} Klassen/Stufen · {erg.KurseSek2} Oberstufenkurse\n" +
                $"{erg.KurseSek1} Sek-I-Kurse · {erg.GruppenOhneKurs} Sek-I-Gruppen ohne Kurs\n" +
                $"{erg.Leistungszeilen} Leistungsdaten-Zeilen · {erg.Lehrkraefte} Lehrkräfte · {erg.Faecher} Fächer\n\n" +
                "Absichtliche Stolpersteine:\n• " + string.Join("\n• ", erg.Stolpersteine) +
                "\n\nJetzt den Prüflauf mit diesem Ordner starten?";
            if (MessageBox.Show(this, text, "Pseudo-Export", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                TxtOrdner.Text = opt.Zielordner;
                await PruefenAsync();
            }
            else TxtStatus.Text = "Pseudo-Export erzeugt.";
        }

        /// <summary>Kleiner Dialog für die Generator-Optionen (null = Abbruch).</summary>
        private SchildGeneratorOptionen? FrageGeneratorOptionen()
        {
            int jahr = SchildGenerator.Jahr(_schuljahr);
            int abschnittVorgabe = DateTime.Today >= new DateTime(jahr + 1, 2, 1) ? 2 : 1;
            string zielVorgabe = Path.Combine(_planOrdner, $"SchILD-Pseudo_{DateTime.Today:yyyy-MM-dd}");

            var win = new Window
            {
                Title = "Pseudo-Export erzeugen",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Erzeugt aus der im Wocheneditor gewählten Plan-Spalte Dateien im Format von SchILD-NRW 3 – " +
                       "mit fiktiven Schülern (keine echten Personen). Ein Pseudo-Export ersetzt künftig den " +
                       "Pseudodaten-Seed der API und testet denselben Weg wie die echten Dateien.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            panel.Children.Add(new TextBlock { Text = "Zielordner:", Margin = new Thickness(0, 0, 0, 2) });
            var zielZeile = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var btnZiel = new Button { Content = "…", Width = 32, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(btnZiel, Dock.Right);
            var txtZiel = new TextBox { Text = zielVorgabe, VerticalContentAlignment = VerticalAlignment.Center };
            zielZeile.Children.Add(btnZiel);
            zielZeile.Children.Add(txtZiel);
            panel.Children.Add(zielZeile);
            btnZiel.Click += (_, __) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Zielordner für den Pseudo-Export" };
                string? start = Directory.Exists(txtZiel.Text) ? txtZiel.Text : Path.GetDirectoryName(txtZiel.Text);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
                if (dlg.ShowDialog(win) == true) txtZiel.Text = dlg.FolderName;
            };

            panel.Children.Add(new TextBlock { Text = "Zeichensatz:", Margin = new Thickness(0, 0, 0, 2) });
            var rbAnsi = new RadioButton { Content = "ANSI (Windows-1252)", IsChecked = true, GroupName = "zs", Margin = new Thickness(0, 0, 0, 2) };
            var rbUtf8 = new RadioButton { Content = "UTF-8 mit BOM", GroupName = "zs", Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(rbAnsi);
            panel.Children.Add(rbUtf8);

            panel.Children.Add(new TextBlock { Text = $"Abschnitt im Schuljahr {jahr}/{(jahr + 1) % 100:00}:", Margin = new Thickness(0, 0, 0, 2) });
            var cbAbschnitt = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            cbAbschnitt.Items.Add("Abschnitt 1 (1. Halbjahr)");
            cbAbschnitt.Items.Add("Abschnitt 2 (2. Halbjahr)");
            cbAbschnitt.SelectedIndex = abschnittVorgabe - 1;
            panel.Children.Add(cbAbschnitt);

            panel.Children.Add(new TextBlock { Text = "Sek-I-Gruppen (Religion, WP …):", Margin = new Thickness(0, 0, 0, 2) });
            var cbSek1 = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
            cbSek1.Items.Add("gemischt – Jg. 05–07 ohne Kurs (nur Fach + Fachlehrer), 08–10 als Kurse");
            cbSek1.Items.Add("alle als Kurse");
            cbSek1.Items.Add("alle ohne Kurs (nur Fach + Fachlehrer)");
            cbSek1.SelectedIndex = 0;
            panel.Children.Add(cbSek1);

            var chkIds = new CheckBox
            {
                Content = "SchuelerLernplattformen.dat mit SchILD-ID erzeugen",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(chkIds);

            var startZeile = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            startZeile.Children.Add(new TextBlock { Text = "Startwert für den Zufall:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var txtStart = new TextBox { Text = "42", Width = 70 };
            startZeile.Children.Add(txtStart);
            startZeile.Children.Add(new TextBlock
            {
                Text = "  (gleicher Startwert + gleicher Plan = gleiche Pseudodaten)",
                Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(startZeile);

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
            win.Content = panel;

            SchildGeneratorOptionen? ergebnis = null;
            ok.Click += (_, __) =>
            {
                string ziel = txtZiel.Text.Trim();
                if (ziel.Length == 0 || !Path.IsPathRooted(ziel))
                {
                    MessageBox.Show(win, "Bitte einen vollständigen Zielordner angeben.", "Pseudo-Export",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!int.TryParse(txtStart.Text.Trim(), out int start))
                {
                    MessageBox.Show(win, "Der Startwert muss eine ganze Zahl sein.", "Pseudo-Export",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                ergebnis = new SchildGeneratorOptionen
                {
                    Zielordner = ziel,
                    Utf8MitBom = rbUtf8.IsChecked == true,
                    Abschnitt = cbAbschnitt.SelectedIndex + 1,
                    Sek1 = cbSek1.SelectedIndex switch
                    {
                        1 => SchildSek1Variante.MitKurs,
                        2 => SchildSek1Variante.OhneKurs,
                        _ => SchildSek1Variante.Gemischt
                    },
                    MitIds = chkIds.IsChecked == true,
                    Startwert = start
                };
                win.DialogResult = true;
            };

            return win.ShowDialog() == true ? ergebnis : null;
        }

        // =====================================================
        //  Allgemein
        // =====================================================

        private void SetzeKnoepfe(bool an)
        {
            BtnOrdner.IsEnabled = an;
            BtnPruefen.IsEnabled = an;
            BtnPseudo.IsEnabled = an;
            BtnProfil.IsEnabled = an;
            CbAbschnitt.IsEnabled = an;
            BtnBericht.IsEnabled = an && _ergebnis != null;
            BtnZuSpeichern.IsEnabled = an && _zuordnungGeaendert;
            BtnZuVorschlaege.IsEnabled = an && (_ergebnis?.Zuordnung?.VorschlagTexte.Count ?? 0) > 0;
            if (!an) { BtnZuFestlegen.IsEnabled = false; BtnZuEntfernen.IsEnabled = false; }
        }

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_laeuft)
            {
                e.Cancel = true;
                TxtStatus.Text = "Bitte warten, bis der Vorgang fertig ist …";
            }
            else if (!ZuordnungUngespeichertKlaeren("Schließen")) e.Cancel = true;
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Gelesene Daten freigeben (nur Arbeitsspeicher, nichts wurde gespeichert).
            DgZahlen.ItemsSource = null;
            DgProbleme.ItemsSource = null;
            DgZuordnung.ItemsSource = null;
            CbAbschnitt.ItemsSource = null;
            _ergebnis = null;
            _export = null;
            base.OnClosed(e);
        }
    }
}
