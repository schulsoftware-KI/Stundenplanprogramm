#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// R3-D-Nachtrag, Entscheidung 161 (aus V3 Dauervertretung vorgezogen): Knopf „Zeitraum veröffentlichen…“ im
    /// Wocheneditor. Statt nur des einen Tags im Feld „Woche um“ werden alle Schultage von–bis (laut Kalender)
    /// veröffentlicht – wie im Untis-Fenster mit Fortschritt, Abbruch und der Meldung „n von m Tag(en)
    /// veröffentlicht“. Jeder Tag wird in der App komplett ersetzt (wie bei „Online veröffentlichen“); Tage ohne
    /// Buchungen werden dort leer. Steht die Quelle in den Online-Einstellungen auf „Untis“, wird vorher
    /// nachgefragt (wie beim einen Tag). Es gilt der geladene Plan (wie bei „Online veröffentlichen“).
    /// Teil der partiellen Klasse TagesEditorDialog; V3 nutzt das später mit.
    /// </summary>
    public partial class TagesEditorDialog
    {
        private void BtnZeitraumPush_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit || _plan == null || DpTag.SelectedDate == null)
            { SetzeInfo("Kein Tag gewählt."); return; }

            DateTime von = DpTag.SelectedDate.Value.Date;
            DateTime bis = _tage.Count > 0 && _tage.Last().Date >= von ? _tage.Last().Date : von;
            ZeigeZeitraumFenster(von, bis);
        }

        /// <summary>Ein Schultag des Zeitraums mit seinem effektiven Tagesplan.</summary>
        private sealed class ZeitraumTag
        {
            public DateTime Datum { get; init; }
            public Tagesplan Plan { get; init; } = new();
            public int Aenderungen { get; init; }
        }

        /// <summary>Schultage im Zeitraum (laut Kalender und Tagesplan) mit ihren effektiven Tagesplänen.</summary>
        private List<ZeitraumTag> TageImZeitraum(DateTime von, DateTime bis)
        {
            var l = new List<ZeitraumTag>();
            if (_plan == null || bis < von) return l;
            foreach (var d in _daten.Kalender.Schultage(von.Date, bis.Date))
            {
                var tp = TagesplanBerechnung.Berechne(d.Date, _plan, _daten.Kalender, _daten.Vertretungen);
                if (!tp.IstSchultag) continue;
                l.Add(new ZeitraumTag { Datum = d.Date, Plan = tp, Aenderungen = tp.Zellen.Count(z => z.IstGeaendert) });
            }
            return l;
        }

        private void ZeigeZeitraumFenster(DateTime vonVorgabe, DateTime bisVorgabe)
        {
            var win = new Window
            {
                Title = "Zeitraum veröffentlichen",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Veröffentlicht die Buchungen des Vertretungsmoduls für alle Schultage von–bis (laut " +
                       "Schuljahreskalender; Ferien und freie Tage werden übersprungen). Jeder Tag wird in der App " +
                       "komplett ersetzt – Tage ohne Buchungen werden dort leer.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var zeile = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            zeile.Children.Add(new TextBlock { Text = "Von:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var dpVon = new DatePicker { SelectedDate = vonVorgabe, Width = 140, Margin = new Thickness(0, 0, 14, 0) };
            zeile.Children.Add(dpVon);
            zeile.Children.Add(new TextBlock { Text = "Bis:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var dpBis = new DatePicker { SelectedDate = bisVorgabe, Width = 140 };
            zeile.Children.Add(dpBis);
            panel.Children.Add(zeile);

            var txtTage = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(txtTage);

            // Es gilt der geladene Plan (wie bei „Online veröffentlichen“) – Hinweis, wenn oben ein anderer gewählt ist.
            string geladen = _plan?.GewaehltePlanSpalte ?? "";
            bool planAnders = CbPlan.SelectedItem is string sp &&
                              !string.Equals(sp, geladen, StringComparison.OrdinalIgnoreCase);
            panel.Children.Add(new TextBlock
            {
                Text = planAnders
                    ? $"⚠ Es gilt der geladene Plan „{geladen}“ – im Feld „Plan“ ist „{CbPlan.SelectedItem}“ gewählt " +
                      "(erst „Aktualisieren“, dann hier veröffentlichen)."
                    : $"Plan: „{geladen}“ – der veröffentlichte Grundplan muss zu diesem Plan passen.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = planAnders ? Brushes.DarkRed : Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var pb = new ProgressBar { Height = 16, Minimum = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(pb);
            var txtFortschritt = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
            panel.Children.Add(txtFortschritt);

            var knoepfe = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var btnLos = new Button { Content = "Veröffentlichen", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnAbbruch = new Button { Content = "Abbrechen", Width = 100, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
            var btnZu = new Button { Content = "Schließen", Width = 100, IsCancel = true };
            knoepfe.Children.Add(btnLos); knoepfe.Children.Add(btnAbbruch); knoepfe.Children.Add(btnZu);
            panel.Children.Add(knoepfe);
            win.Content = panel;

            List<ZeitraumTag> tage = new();
            bool laeuft = false;
            bool pruefe = false;   // P3: Abfrage des Grundplans läuft
            CancellationTokenSource? abbruch = null;

            void Anzeigen()
            {
                if (laeuft) return;
                if (dpVon.SelectedDate is not DateTime v || dpBis.SelectedDate is not DateTime b || b.Date < v.Date)
                {
                    tage = new();
                    txtTage.Text = "Zeitraum ungültig (von ≤ bis).";
                    btnLos.IsEnabled = false;
                    return;
                }
                tage = TageImZeitraum(v, b);
                int mit = tage.Count(t => t.Aenderungen > 0);
                txtTage.Text = tage.Count == 0
                    ? "Im Zeitraum liegt kein Schultag (laut Kalender)."
                    : $"{tage.Count} Schultag(e), davon {mit} mit Änderungen ({tage.Sum(t => t.Aenderungen)}) und {tage.Count - mit} ohne.";
                btnLos.IsEnabled = tage.Count > 0;
            }
            dpVon.SelectedDateChanged += (_, __) =>
            {
                // Bis nicht vor Von stehen lassen.
                if (dpVon.SelectedDate is DateTime v && dpBis.SelectedDate is DateTime b && b.Date < v.Date)
                    dpBis.SelectedDate = v.Date;
                Anzeigen();
            };
            dpBis.SelectedDateChanged += (_, __) => Anzeigen();
            Anzeigen();

            btnAbbruch.Click += (_, __) =>
            {
                abbruch?.Cancel();
                btnAbbruch.IsEnabled = false;
                txtFortschritt.Text = "Breche ab …";
            };
            win.Closing += (_, ce) =>
            {
                if (pruefe) { ce.Cancel = true; return; }
                if (!laeuft) return;
                ce.Cancel = true;
                txtFortschritt.Text = "Bitte warten oder „Abbrechen“ wählen – es wird gerade veröffentlicht.";
            };

            btnLos.Click += async (_, __) =>
            {
                if (laeuft) return;
                Anzeigen();
                if (tage.Count == 0 || dpVon.SelectedDate is not DateTime von || dpBis.SelectedDate is not DateTime bis) return;
                if (string.IsNullOrWhiteSpace(_daten.Schuljahr))
                {
                    MessageBox.Show(win, "Im Schuljahreskalender ist kein Schuljahr eingetragen (Button 22).",
                        "Zeitraum veröffentlichen", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var einst = OnlineSync.LadeEinstellungen(_ordner);
                if (string.IsNullOrWhiteSpace(einst.BaseUrl))
                {
                    var neu = OnlineSync.FrageEinstellungen(win, einst, _ordner);
                    if (neu == null) return;
                    OnlineSync.SpeichereEinstellungen(_ordner, neu);
                    einst = neu;
                }

                // P3 (Kleinigkeit c, Entscheidung 199): Passt der geladene Plan zum Grundplan der App im Zeitraum?
                bool planOk;
                pruefe = true;
                btnLos.IsEnabled = false;
                txtFortschritt.Text = "Prüfe den Grundplan in der App …";
                try
                {
                    planOk = await GrundplanAbgleich.FragenAsync(win, einst, _daten.Schuljahr ?? "", _plan?.GewaehltePlanSpalte ?? "",
                        CbPlan.SelectedItem as string, von.Date, bis.Date, "Zeitraum veröffentlichen");
                }
                finally
                {
                    pruefe = false;
                    btnLos.IsEnabled = tage.Count > 0;
                }
                if (!planOk) { txtFortschritt.Text = "Nicht veröffentlicht (der Plan passt nicht zum Grundplan in der App)."; return; }
                txtFortschritt.Text = "";

                if (!FrageZeitraum(win, einst, tage, von.Date, bis.Date)) { txtFortschritt.Text = "Nicht veröffentlicht."; return; }

                laeuft = true;
                abbruch = new CancellationTokenSource();
                btnLos.IsEnabled = false;
                btnZu.IsEnabled = false;
                dpVon.IsEnabled = false;
                dpBis.IsEnabled = false;
                btnAbbruch.IsEnabled = true;
                btnAbbruch.Visibility = Visibility.Visible;
                pb.Maximum = tage.Count;
                pb.Value = 0;
                pb.Visibility = Visibility.Visible;
                try
                {
                    var (ergebnis, fehler) = await VeroeffentlicheZeitraumAsync(einst, tage, von.Date, bis.Date, abbruch.Token,
                        (n, tag) =>
                        {
                            txtFortschritt.Text = $"Tag {n + 1} von {tage.Count}: {UntisText.Tag(tag)} {tag:dd.MM.yyyy} …";
                            pb.Value = n;
                        });
                    pb.Value = ergebnis.Gesendet;
                    txtFortschritt.Text = ergebnis.Text;
                    if (fehler != null)
                        MessageBox.Show(win, $"Veröffentlichen bei {ergebnis.FehlerTag:dd.MM.yyyy} fehlgeschlagen:\n\n{fehler}\n\n{ergebnis.Zahlen}",
                            "Zeitraum veröffentlichen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    else if (ergebnis.Abgebrochen)
                        MessageBox.Show(win, "Abgebrochen.\n\n" + ergebnis.Zahlen + "\nDie übrigen Tage sind in der App unverändert.",
                            "Zeitraum veröffentlichen", MessageBoxButton.OK, MessageBoxImage.Information);
                    else
                        MessageBox.Show(win, ergebnis.Zahlen, "Zeitraum veröffentlichen", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    laeuft = false;
                    abbruch?.Dispose();
                    abbruch = null;
                    btnAbbruch.Visibility = Visibility.Collapsed;
                    btnZu.IsEnabled = true;
                    dpVon.IsEnabled = true;
                    dpBis.IsEnabled = true;
                    btnLos.IsEnabled = tage.Count > 0;
                }
            };

            win.ShowDialog();
        }

        /// <summary>Rückfrage vor dem Senden; bei Quelle „Untis“ mit Warnung (Entscheidung 120, wie beim einen Tag).</summary>
        private bool FrageZeitraum(Window owner, OnlineEinstellungen einst, List<ZeitraumTag> tage, DateTime von, DateTime bis)
        {
            int mit = tage.Count(t => t.Aenderungen > 0);
            int aend = tage.Sum(t => t.Aenderungen);
            string kopf = $"Zeitraum: {von:dd.MM.yyyy} bis {bis:dd.MM.yyyy} – {tage.Count} Schultag(e), davon {mit} mit " +
                          $"Änderungen ({aend}) und {tage.Count - mit} ohne.\n" +
                          "Jeder dieser Tage wird in der App komplett ersetzt – Tage ohne Buchungen werden dort leer.\n\n" +
                          $"Server: {einst.BaseUrl}";

            if (einst.QuelleIstUntis)
            {
                var prot = UntisImportProtokoll.Laden(_ordner);
                var ausUntis = tage.Where(t => prot.Tage.ContainsKey(UntisImportProtokoll.TagSchluessel(t.Datum)))
                                   .Select(t => t.Datum).ToList();
                string frage =
                    "In den Online-Einstellungen ist „Untis“ als Quelle der Vertretungen eingestellt.\n\n" +
                    (ausUntis.Count > 0
                        ? $"{ausUntis.Count} dieser Tage wurden zuletzt aus Untis veröffentlicht (z. B. " +
                          string.Join(", ", ausUntis.Take(3).Select(d => d.ToString("dd.MM.yyyy"))) + ").\n\n"
                        : "") +
                    "„Zeitraum veröffentlichen“ ersetzt diese Tage in der App durch die Buchungen des Vertretungsmoduls – " +
                    "die Untis-Vertretungen dieser Tage verschwinden dort.\n\n" + kopf + "\n\nTrotzdem veröffentlichen?";
                return MessageBox.Show(owner, frage, "Zeitraum veröffentlichen", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                           MessageBoxResult.No) == MessageBoxResult.Yes;
            }

            return MessageBox.Show(owner, "Buchungen des Vertretungsmoduls veröffentlichen?\n\n" + kopf,
                       "Zeitraum veröffentlichen", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        /// <summary>Ergebnis eines Laufs „Zeitraum veröffentlichen“.</summary>
        private sealed class ZeitraumErgebnis
        {
            public int Geplant { get; init; }
            public int Gesendet { get; set; }
            public int Aenderungen { get; set; }
            public bool Abgebrochen { get; set; }
            public DateTime? FehlerTag { get; set; }
            public string Zahlen => $"{Gesendet} von {Geplant} Tag(en) veröffentlicht ({Aenderungen} Änderung(en)).";
            public string Text => Abgebrochen ? "Abgebrochen – " + Zahlen : FehlerTag != null ? "Fehler – " + Zahlen : Zahlen;
        }

        /// <summary>
        /// Sendet die Tage nacheinander (je Tag wie „Online veröffentlichen“). Bricht beim ersten Fehler bzw. auf
        /// Abbruch ab; bereits gesendete Tage bleiben in der App.
        /// </summary>
        private async Task<(ZeitraumErgebnis Ergebnis, string? Fehler)> VeroeffentlicheZeitraumAsync(
            OnlineEinstellungen einst, List<ZeitraumTag> tage, DateTime von, DateTime bis,
            CancellationToken token, Action<int, DateTime> fortschritt)
        {
            var sync = new OnlineSync { BaseUrl = einst.BaseUrl, ApiKey = einst.ApiKey };
            var erg = new ZeitraumErgebnis { Geplant = tage.Count };
            string? fehler = null;
            BtnOnlinePush.IsEnabled = false;
            BtnZeitraumPush.IsEnabled = false;
            try
            {
                foreach (var t in tage)
                {
                    if (token.IsCancellationRequested) { erg.Abgebrochen = true; break; }
                    fortschritt(erg.Gesendet, t.Datum);
                    try
                    {
                        await sync.PushAsync(OnlineSync.BaueDto(_daten.Schuljahr, t.Datum, t.Plan), token);
                        erg.Gesendet++;
                        erg.Aenderungen += t.Aenderungen;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        erg.Abgebrochen = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        fehler = ex is TaskCanceledException
                            ? "Zeitüberschreitung – der Server hat nicht geantwortet."
                            : OnlineSync.FehlerText(ex, _ordner);
                        erg.FehlerTag = t.Datum;
                        break;
                    }
                }
            }
            finally
            {
                BtnOnlinePush.IsEnabled = true;
                BtnZeitraumPush.IsEnabled = true;
            }

            SetzeInfo($"Zeitraum {von:dd.MM.}–{bis:dd.MM.yyyy}: {erg.Text}");
            _log?.Invoke($"Online: Zeitraum {von:dd.MM.yyyy}–{bis:dd.MM.yyyy}: {erg.Zahlen}" +
                         (erg.Abgebrochen ? " (abgebrochen)" : erg.FehlerTag != null ? $" (Fehler bei {erg.FehlerTag:dd.MM.yyyy})" : ""));
            return (erg, fehler);
        }
    }
}
