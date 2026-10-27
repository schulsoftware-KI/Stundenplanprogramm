#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// Vertretungs-Wocheneditor (Konzept §15). Zeigt – wie der Plan-Editor – für
    /// Lehrkraft und Klasse die GANZE WOCHE (Tage × Stunden) nebeneinander,
    /// farbcodiert aus <see cref="TagesplanBerechnung"/>. Optional 2 Wochen.
    ///
    /// Bearbeiten: Klick auf eine Stunde öffnet ein Menü (Entfall / Vertretung /
    /// Betreuung / EVA / Verlegung hierher / Zurücksetzen). Eine Klassenstunde
    /// lässt sich per Drag&amp;Drop auf eine freie Stunde ziehen (Verlegung – über
    /// die ganze sichtbare Spanne, also auch über zwei Wochen). Die Plandatei
    /// wird nie verändert; gebucht wird in die Vertretungsdatei.
    /// </summary>
    public partial class TagesEditorDialog : Window, IVertretungsFenster
    {
        /// <summary>Partnerfenster („Vertretung erfassen") für Auswahl-/Daten-Kopplung.</summary>
        public IVertretungsFenster? Partner { get; set; }

        private readonly string _planPfad;
        private readonly Action<string>? _log;
        private readonly string _ordner;

        private string _vertretungsPfad = "";
        private VertretungsDaten _daten = new();
        private PlanDaten? _plan;
        private bool _bereit = false;
        private bool _laedt = false;
        private bool _syncLaeuft = false;
        private bool _dragGemacht = false;

        // Live-Feedback beim Ziehen (wie im Planeditor): die aktuell markierte
        // Zielzelle mit erzwungenem ToolTip; rot = harter Konflikt, orange =
        // weiche Warnung (Regel-Verstoß, aber erlaubt).
        private Border? _dragMarkZelle;
        private string? _letzterDragKey;              // drosselt die Neuberechnung je Zielfeld
        private DragDropEffects _letzterDragEffects = DragDropEffects.None;
        private static readonly SolidColorBrush WeicheWarnungBrush =
            new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x00));

        private enum VerlegStufe { Keiner, Weich, Hart, Unmoeglich }

        // Angezeigte Tage (1 oder 2 Wochen) + Tagesplan-Cache pro Datum.
        private List<DateTime> _tage = new();
        private readonly Dictionary<DateTime, Tagesplan> _tpCache = new();
        private int _maxStunde = 1;

        // Rasterbreite/-höhe einer Zelle (Wochen-Grid etwas kompakter).
        private const double SpaltenBreite = 128;
        private const double ZeilenHoehe = 52;
        private const int MaxVorschlaegeImMenu = 15;

        /// <summary>Nutzlast einer Verlegungs-Drag-Operation.</summary>
        private class VerlegDrag
        {
            public TagesZelle Quelle { get; set; } = new();
            public DateTime QuellDatum { get; set; }
            public string Klasse { get; set; } = "";
        }

        /// <summary>Kandidat für den tagesübergreifenden Verlegungs-Picker.</summary>
        private class PickKandidat
        {
            public string WTag { get; set; } = "";
            public int Stunde { get; set; }
            public string Fach { get; set; } = "";
            public string Lehrer { get; set; } = "";
            public int UNr { get; set; }
            public List<string> Klassen { get; set; } = new();
            public string Anzeige => $"{WTag}  Std {Stunde}  {Fach}  ({Lehrer})";
        }

        public TagesEditorDialog(string planPfad, Action<string>? log = null)
        {
            InitializeComponent();
            _planPfad = planPfad ?? "";
            _log = log;
            _ordner = string.IsNullOrWhiteSpace(_planPfad)
                ? Directory.GetCurrentDirectory()
                : (Path.GetDirectoryName(_planPfad) ?? Directory.GetCurrentDirectory());

            CbGrund.ItemsSource = Enum.GetValues(typeof(VertretungsGrund));
            CbGrund.SelectedItem = VertretungsGrund.Krankheit;

            LadeDaten();
        }

        // =====================================================
        //  Laden
        // =====================================================

        private void LadeDaten()
        {
            try
            {
                _vertretungsPfad = Directory.GetFiles(_ordner, "Vertretung_*.xlsx")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
            }
            catch { _vertretungsPfad = ""; }

            if (_vertretungsPfad.Length == 0)
            {
                MessageBox.Show("Keine Vertretungsdatei gefunden. Bitte zuerst über Button 22 " +
                    "einen Schuljahreskalender anlegen und speichern.",
                    "Wocheneditor", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtInfo.Text = "Keine Vertretungsdatei.";
                return;
            }

            try { _daten = VertretungsStore.Laden(_vertretungsPfad); }
            catch (Exception ex)
            {
                MessageBox.Show("Vertretungsdatei konnte nicht gelesen werden:\n\n" + ex.Message,
                    "Wocheneditor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_daten.Kalender.Start == default || _daten.Kalender.Ende == default)
            {
                MessageBox.Show("Im Schuljahreskalender fehlen erster/letzter Schultag (Button 22).",
                    "Wocheneditor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try { _plan = PlanLeser.Lies(_planPfad); }
            catch (Exception ex)
            {
                MessageBox.Show("Plan konnte nicht gelesen werden:\n\n" + ex.Message,
                    "Wocheneditor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _maxStunde = _plan.Belegung.Count > 0 ? _plan.Belegung.Keys.Max(k => k.Stunde) : 1;

            _laedt = true;
            CbPlan.ItemsSource = _plan.PlanSpalten;
            CbPlan.SelectedItem = _plan.GewaehltePlanSpalte;

            CbLehrer.ItemsSource = _plan.AlleLehrer
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            CbKlasse.ItemsSource = _plan.Unterrichte.Values.SelectMany(t => t).SelectMany(t => t.Klassen)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (CbLehrer.Items.Count > 0) CbLehrer.SelectedIndex = 0;
            if (CbKlasse.Items.Count > 0) CbKlasse.SelectedIndex = 0;

            DateTime heute = DateTime.Today;
            DpTag.SelectedDate = (heute >= _daten.Kalender.Start.Date && heute <= _daten.Kalender.Ende.Date)
                ? heute : _daten.Kalender.Start.Date;

            _bereit = true;
            _laedt = false;
            Title = $"Vertretungs-Wocheneditor – {_daten.Schuljahr}";
            Zeichne();
        }

        // =====================================================
        //  Navigation / Auswahl
        // =====================================================

        private void BtnWocheZurueck_Click(object sender, RoutedEventArgs e) => VerschiebeTag(-7);
        private void BtnWocheVor_Click(object sender, RoutedEventArgs e) => VerschiebeTag(+7);

        private void VerschiebeTag(int tage)
        {
            if (DpTag.SelectedDate == null) return;
            DpTag.SelectedDate = DpTag.SelectedDate.Value.AddDays(tage);
        }

        private void BtnLehrerZurueck_Click(object sender, RoutedEventArgs e) => BlaettereCombo(CbLehrer, -1);
        private void BtnLehrerVor_Click(object sender, RoutedEventArgs e) => BlaettereCombo(CbLehrer, +1);
        private void BtnKlasseZurueck_Click(object sender, RoutedEventArgs e) => BlaettereCombo(CbKlasse, -1);
        private void BtnKlasseVor_Click(object sender, RoutedEventArgs e) => BlaettereCombo(CbKlasse, +1);

        private static void BlaettereCombo(ComboBox cb, int richtung)
        {
            int n = cb.Items.Count;
            if (n == 0) return;
            int idx = cb.SelectedIndex;
            if (idx < 0) idx = richtung > 0 ? -1 : 0;
            cb.SelectedIndex = ((idx + richtung) % n + n) % n;
        }

        private void DpTag_Changed(object sender, SelectionChangedEventArgs e)
        { if (_laedt || _syncLaeuft) return; Zeichne(); MeldeAuswahl(); }
        private void Auswahl_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_laedt || _syncLaeuft) return;
            Zeichne();
            if (ReferenceEquals(sender, CbLehrer)) MeldeAuswahl(nurLehrer: true);
            else if (ReferenceEquals(sender, CbKlasse)) MeldeAuswahl(nurKlasse: true);
            else MeldeAuswahl();
        }
        private void ZweiWochen_Changed(object sender, RoutedEventArgs e) { if (!_laedt) Zeichne(); }

        private void BtnAktualisieren_Click(object sender, RoutedEventArgs e)
        {
            if (_plan != null && CbPlan.SelectedItem is string sp &&
                !string.Equals(sp, _plan.GewaehltePlanSpalte, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _plan = PlanLeser.Lies(_planPfad, sp);
                    _maxStunde = _plan.Belegung.Count > 0 ? _plan.Belegung.Keys.Max(k => k.Stunde) : 1;
                }
                catch { /* alten Plan behalten */ }
            }
            Zeichne();
        }

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e) => Close();

        // =====================================================
        //  Online veröffentlichen (M4 – Push an die VertretungApi)
        // =====================================================

        private async void BtnOnlinePush_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit || _plan == null || DpTag.SelectedDate == null)
            { SetzeInfo("Kein Tag gewählt."); return; }

            var einst = OnlineSync.LadeEinstellungen(_ordner);
            if (string.IsNullOrWhiteSpace(einst.BaseUrl))
            {
                var neu = OnlineSync.FrageEinstellungen(this, einst);
                if (neu == null) return;
                OnlineSync.SpeichereEinstellungen(_ordner, neu);
                einst = neu;
            }

            DateTime tag = DpTag.SelectedDate.Value.Date;
            var tp = Tagesplan(tag);
            if (!tp.IstSchultag) { SetzeInfo($"{tag:dd.MM.} ist kein Schultag."); return; }

            int anz = tp.Zellen.Count(z => z.IstGeaendert);
            var sync = new OnlineSync { BaseUrl = einst.BaseUrl, ApiKey = einst.ApiKey };

            try
            {
                BtnOnlinePush.IsEnabled = false;
                SetzeInfo($"Veröffentliche {tag:dd.MM.yyyy} ({anz} Änderung(en)) …");
                await sync.PushTagAsync(_daten.Schuljahr, tag, tp);
                SetzeInfo($"Online veröffentlicht: {tag:dd.MM.yyyy}, {anz} Änderung(en).");
                _log?.Invoke($"Online: {tag:dd.MM.yyyy} veröffentlicht ({anz} Änderungen).");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Veröffentlichen fehlgeschlagen:\n\n" + ex.Message,
                    "Online", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetzeInfo("Online-Veröffentlichung fehlgeschlagen.");
            }
            finally { BtnOnlinePush.IsEnabled = true; }
        }

        private void BtnOnlineEinst_Click(object sender, RoutedEventArgs e)
        {
            var einst = OnlineSync.LadeEinstellungen(_ordner);
            var neu = OnlineSync.FrageEinstellungen(this, einst);
            if (neu != null)
            {
                OnlineSync.SpeichereEinstellungen(_ordner, neu);
                SetzeInfo("Online-Einstellungen gespeichert.");
            }
        }

        // =====================================================
        //  Lehrer am Bezugstag abwesend
        // =====================================================

        private void BtnLehrerAbwesend_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit || _plan == null || DpTag.SelectedDate == null) return;
            if (CbLehrer.SelectedItem is not string lehrer) return;

            DateTime tag = DpTag.SelectedDate.Value.Date;
            var tp = Tagesplan(tag);
            if (!tp.IstSchultag) { SetzeInfo($"{tag:dd.MM.} ist kein Schultag."); return; }

            var zuEntfall = tp.Zellen
                .Where(z => string.Equals(z.OriginalLehrer, lehrer, StringComparison.OrdinalIgnoreCase))
                .Where(z => z.Status == ZellStatus.Normal)
                .ToList();

            if (zuEntfall.Count == 0) { SetzeInfo($"{lehrer}: keine offenen Stunden am {tag:dd.MM.}."); return; }

            if (MessageBox.Show(
                    $"{lehrer} am {tag:dd.MM.yyyy}: {zuEntfall.Count} Stunde(n) auf Entfall setzen?",
                    "Lehrer abwesend", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            foreach (var z in zuEntfall)
                BucheIntern(z, tag, Vertretungsart.Entfall, null, speichern: false, zeichnen: false);

            Speichere();
            Zeichne();
            SetzeInfo($"{zuEntfall.Count} Stunde(n) von {lehrer} am {tag:dd.MM.} auf Entfall gesetzt.");
        }

        // =====================================================
        //  Wochentage bestimmen + Tagesplan-Cache
        // =====================================================

        private DateTime MontagVon(DateTime d)
        {
            int diffMo = ((int)d.DayOfWeek + 6) % 7;   // Montag = 0
            return d.Date.AddDays(-diffMo);
        }

        /// <summary>Die Raster-Tage (Mo–Fr …) einer Woche ab <paramref name="montag"/>.</summary>
        private List<DateTime> WochenTageFuer(DateTime montag)
        {
            var result = new List<DateTime>();
            if (_plan == null) return result;
            foreach (var tag in _plan.RasterTage)
            {
                int idx = SchuljahrKalender.WochentagIndex(tag);
                if (idx < 0) continue;
                int offset = (idx + 6) % 7;            // Montag=0 … Sonntag=6
                result.Add(montag.AddDays(offset));
            }
            return result;
        }

        private Tagesplan Tagesplan(DateTime datum)
        {
            if (_tpCache.TryGetValue(datum.Date, out var vorhanden)) return vorhanden;
            var tp = TagesplanBerechnung.Berechne(datum, _plan!, _daten.Kalender, _daten.Vertretungen);
            _tpCache[datum.Date] = tp;
            return tp;
        }

        // =====================================================
        //  Zeichnen
        // =====================================================

        private DateTime _montagW1;
        private DateTime _montagW2;
        private bool _zweiWochen;

        private void Zeichne()
        {
            if (!_bereit || _plan == null || DpTag.SelectedDate == null) return;

            _zweiWochen = ChkZweiWochen.IsChecked == true;
            _montagW1 = MontagVon(DpTag.SelectedDate.Value.Date);
            _montagW2 = _montagW1.AddDays(7);

            // zweite Zeile (Woche 2) ein-/ausblenden
            ContW2.Visibility = _zweiWochen ? Visibility.Visible : Visibility.Collapsed;
            RowW2.Height = _zweiWochen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            // Cache aller angezeigten Tage füllen
            _tpCache.Clear();
            _tage = WochenTageFuer(_montagW1).ToList();
            if (_zweiWochen) _tage.AddRange(WochenTageFuer(_montagW2));
            foreach (var d in _tage) Tagesplan(d);

            if (_tage.Count > 0)
                TxtInfo.Text = $"{_tage.First():dd.MM.} – {_tage.Last():dd.MM.yyyy}";

            ZeichneLehrerSeite();
            ZeichneKlasseSeite();
        }

        private void ZeichneLehrerSeite()
        {
            string? lehrer = CbLehrer.SelectedItem as string;
            ZeichneWoche(HostLehrerW1, $"Lehrerplan: {lehrer}  (KW {Kw(_montagW1)})",
                         lehrer, lehrerSicht: true, WochenTageFuer(_montagW1));
            if (_zweiWochen)
                ZeichneWoche(HostLehrerW2, $"Lehrerplan: {lehrer}  (KW {Kw(_montagW2)})",
                             lehrer, lehrerSicht: true, WochenTageFuer(_montagW2));
            else
                HostLehrerW2.Children.Clear();
        }

        private void ZeichneKlasseSeite()
        {
            string? klasse = CbKlasse.SelectedItem as string;
            ZeichneWoche(HostKlasseW1, $"Klassenplan: {klasse}  (KW {Kw(_montagW1)})",
                         klasse, lehrerSicht: false, WochenTageFuer(_montagW1));
            if (_zweiWochen)
                ZeichneWoche(HostKlasseW2, $"Klassenplan: {klasse}  (KW {Kw(_montagW2)})",
                             klasse, lehrerSicht: false, WochenTageFuer(_montagW2));
            else
                HostKlasseW2.Children.Clear();
        }

        private static int Kw(DateTime montag)
            => System.Globalization.ISOWeek.GetWeekOfYear(montag);

        private void ZeichneWoche(StackPanel host, string titel, string? bezug, bool lehrerSicht, List<DateTime> tage)
        {
            host.Children.Clear();
            host.Children.Add(new TextBlock { Text = titel, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });

            if (tage.Count == 0 || bezug == null) return;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });         // Std
            foreach (var _ in tage)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SpaltenBreite) });

            // Kopfzeile
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var eck = KopfZelle("Std", 30);
            Grid.SetRow(eck, 0); Grid.SetColumn(eck, 0); grid.Children.Add(eck);
            for (int c = 0; c < tage.Count; c++)
            {
                var tp = Tagesplan(tage[c]);
                string kopf = $"{tp.WTag} {tage[c]:dd.MM.}";
                var k = KopfZelle(kopf);
                Grid.SetRow(k, 0); Grid.SetColumn(k, c + 1); grid.Children.Add(k);
            }

            // Stundenzeilen
            int row = 1;
            for (int st = 1; st <= Math.Max(1, _maxStunde); st++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var stLabel = new Border
                {
                    BorderBrush = Brushes.Silver,
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)),
                    MinWidth = 30,
                    Child = new TextBlock
                    {
                        Text = st.ToString(), FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Grid.SetRow(stLabel, row); Grid.SetColumn(stLabel, 0); grid.Children.Add(stLabel);

                for (int c = 0; c < tage.Count; c++)
                {
                    DateTime datum = tage[c];
                    var tp = Tagesplan(datum);

                    var inhalt = new StackPanel();
                    if (!tp.IstSchultag)
                    {
                        inhalt.Children.Add(new TextBlock { Text = "", Margin = new Thickness(2) });
                    }
                    else
                    {
                        var zellen = (lehrerSicht ? tp.FuerLehrer(bezug) : tp.FuerKlasse(bezug))
                            .Where(z => z.Stunde == st).ToList();
                        foreach (var z in zellen)
                            inhalt.Children.Add(MachZelle(z, datum, lehrerSicht, bezug));
                    }

                    var zelle = new Border
                    {
                        BorderBrush = Brushes.Silver,
                        BorderThickness = new Thickness(1),
                        Background = tp.IstSchultag ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                        MinHeight = ZeilenHoehe,
                        Child = inhalt
                    };

                    // Drop-Ziel für Verlegung (nur Klassenplan, nur Schultage).
                    if (!lehrerSicht && tp.IstSchultag)
                    {
                        int stZiel = st;
                        DateTime zielDatum = datum;
                        string klasseZiel = bezug;
                        zelle.AllowDrop = true;
                        zelle.DragOver += (s, e) => DragUeberZelle((Border)s, e, zielDatum, stZiel, klasseZiel);
                        zelle.DragLeave += (s, e) =>
                        {
                            _letzterDragKey = null;   // Zielfeld verlassen -> neu bewerten
                            if (ReferenceEquals(_dragMarkZelle, s)) EntferneDragMarkierung();
                        };
                        zelle.Drop += (s, e) =>
                        {
                            _letzterDragKey = null;
                            EntferneDragMarkierung();
                            if (e.Data.GetData(typeof(VerlegDrag)) is VerlegDrag d)
                                VerlegeDrop(d, zielDatum, stZiel, klasseZiel);
                        };
                    }

                    Grid.SetRow(zelle, row); Grid.SetColumn(zelle, c + 1); grid.Children.Add(zelle);
                }
                row++;
            }

            host.Children.Add(grid);
        }

        private static Border KopfZelle(string text, double minWidth = 0) => new Border
        {
            BorderBrush = Brushes.Silver,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            MinWidth = minWidth,
            Child = new TextBlock
            {
                Text = text, FontWeight = FontWeights.Bold, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 3, 4, 3)
            }
        };

        private Border MachZelle(TagesZelle z, DateTime datum, bool lehrerSicht, string? bezug)
        {
            var (kopf, zeile2, zeile3) = ZelltextTeile(z, lehrerSicht, bezug);

            var sp = new StackPanel { Margin = new Thickness(3, 2, 3, 2) };
            var tbKopf = new TextBlock { Text = kopf, FontWeight = FontWeights.Bold, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            if (z.Status == ZellStatus.Entfall) tbKopf.TextDecorations = TextDecorations.Strikethrough;
            sp.Children.Add(tbKopf);
            if (zeile2.Length > 0) sp.Children.Add(new TextBlock { Text = zeile2, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            if (zeile3.Length > 0) sp.Children.Add(new TextBlock { Text = zeile3, FontSize = 10, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });

            var border = new Border
            {
                Background = Hintergrund(z.Status),
                BorderBrush = Brushes.Silver,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = sp,
                Tag = z
            };

            // Verlegung per Drag&Drop: im KLASSENPLAN eine verschiebbare Stunde
            // ziehbar machen (über die ganze sichtbare Spanne, auch 2 Wochen).
            if (!lehrerSicht && bezug != null && KannVerlegtWerden(z))
            {
                Point start = default;
                bool armed = false;
                border.PreviewMouseLeftButtonDown += (s, e) => { start = e.GetPosition(null); armed = true; };
                border.MouseMove += (s, e) =>
                {
                    if (!armed || e.LeftButton != MouseButtonState.Pressed) return;
                    var p = e.GetPosition(null);
                    if (Math.Abs(p.X - start.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(p.Y - start.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        armed = false;
                        _dragGemacht = true;
                        _letzterDragKey = null;   // frische Drag-Operation
                        DragDrop.DoDragDrop(border,
                            new VerlegDrag { Quelle = z, QuellDatum = datum, Klasse = bezug },
                            DragDropEffects.Move);
                        EntferneDragMarkierung();   // Sicherheit: Markierung nach Abbruch weg
                        _letzterDragKey = null;
                    }
                };
            }

            // Klick: Gegenseite synchronisieren, dann Aktionsmenü öffnen.
            border.ContextMenu = BaueZellMenu(z, datum, lehrerSicht, bezug);
            border.MouseLeftButtonUp += (s, e) =>
            {
                if (_dragGemacht) { _dragGemacht = false; return; }

                if (lehrerSicht) SpringeZurKlasse(z);
                else SpringeZumLehrer(z);

                if (border.ContextMenu != null)
                {
                    border.ContextMenu.PlacementTarget = border;
                    border.ContextMenu.IsOpen = true;
                }
            };

            return border;
        }

        private bool KannVerlegtWerden(TagesZelle z)
            => z.Status == ZellStatus.Normal
               && !string.IsNullOrWhiteSpace(z.OriginalLehrer)
               && _plan != null
               && _plan.Unterrichte.TryGetValue(z.UNr, out var teile)
               && teile.Count == 1;

        // =====================================================
        //  Klick-Synchronisation (nur Gegenseite neu zeichnen)
        // =====================================================

        private void SpringeZurKlasse(TagesZelle z)
        {
            string? kl = z.Klassen.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            if (kl == null) return;
            if (string.Equals(CbKlasse.SelectedItem as string, kl, StringComparison.OrdinalIgnoreCase)) return;

            var treffer = CbKlasse.Items.OfType<string>()
                .FirstOrDefault(x => string.Equals(x, kl, StringComparison.OrdinalIgnoreCase));
            if (treffer == null) return;

            _syncLaeuft = true; CbKlasse.SelectedItem = treffer; _syncLaeuft = false;
            ZeichneKlasseSeite();
        }

        private void SpringeZumLehrer(TagesZelle z)
        {
            string le = !string.IsNullOrWhiteSpace(z.Lehrer) ? z.Lehrer : z.OriginalLehrer;
            if (string.IsNullOrWhiteSpace(le)) return;
            if (string.Equals(CbLehrer.SelectedItem as string, le, StringComparison.OrdinalIgnoreCase)) return;

            var treffer = CbLehrer.Items.OfType<string>()
                .FirstOrDefault(x => string.Equals(x, le, StringComparison.OrdinalIgnoreCase));
            if (treffer == null) return;

            _syncLaeuft = true; CbLehrer.SelectedItem = treffer; _syncLaeuft = false;
            ZeichneLehrerSeite();
        }

        // =====================================================
        //  Aktionsmenü einer Zelle
        // =====================================================

        private ContextMenu? BaueZellMenu(TagesZelle z, DateTime datum, bool lehrerSicht, string? bezug)
        {
            if (string.IsNullOrWhiteSpace(z.OriginalLehrer)) return null;

            var menu = new ContextMenu();

            if (z.Eintrag?.Art == Vertretungsart.Verlegung)
            {
                var miReset = new MenuItem { Header = "Verlegung zurücknehmen" };
                miReset.Click += (_, __) => Entferne(z, datum);
                menu.Items.Add(miReset);
                return menu;
            }

            // Verlegung HIERHER (tagesübergreifend per Picker) auf einem Loch.
            if (!lehrerSicht && bezug != null && z.Status == ZellStatus.Entfall)
            {
                var miVerleg = new MenuItem { Header = "Verlegung hierher … (Picker)" };
                int stZiel = z.Stunde; string klasse = bezug; DateTime zd = datum;
                miVerleg.Click += (_, __) => VerlegeHierher(zd, stZiel, klasse);
                menu.Items.Add(miVerleg);
                menu.Items.Add(new Separator());
            }

            var miEntfall = new MenuItem { Header = "Entfall" };
            miEntfall.Click += (_, __) => Buche(z, datum, Vertretungsart.Entfall, null);
            menu.Items.Add(miEntfall);

            var miVertr = new MenuItem { Header = "Vertretung durch …" };
            FuelleVorschlagMenu(miVertr, z, datum, Vertretungsart.Vertretung);
            menu.Items.Add(miVertr);

            var miBetr = new MenuItem { Header = "Betreuung/Aufsicht durch …" };
            FuelleVorschlagMenu(miBetr, z, datum, Vertretungsart.BetreuungAufsicht);
            menu.Items.Add(miBetr);

            var miEva = new MenuItem { Header = "Eigenverantw. Arbeiten (EVA)" };
            miEva.Click += (_, __) => Buche(z, datum, Vertretungsart.EVA, null);
            menu.Items.Add(miEva);

            if (z.IstGeaendert && z.Eintrag != null)
            {
                menu.Items.Add(new Separator());
                var miReset = new MenuItem { Header = "Zurücksetzen (Buchung entfernen)" };
                miReset.Click += (_, __) => Entferne(z, datum);
                menu.Items.Add(miReset);
            }

            return menu;
        }

        private void FuelleVorschlagMenu(MenuItem parent, TagesZelle z, DateTime datum, Vertretungsart art)
        {
            if (_plan == null) return;

            var inst = new StundenInstanz
            {
                Datum = datum, Stunde = z.Stunde, UNr = z.UNr,
                Lehrer = z.OriginalLehrer, Fach = z.Fach, Klassen = new List<string>(z.Klassen)
            };
            var zaehler = VertretungsZaehler.GehaltenProLehrer(_daten.Vertretungen);
            var vorschlaege = VertretungsService.Vorschlaege(inst, _plan, _daten.Kalender, zaehler);

            if (vorschlaege.Count == 0)
            {
                parent.Items.Add(new MenuItem { Header = "(keine freie Lehrkraft)", IsEnabled = false });
                return;
            }
            foreach (var v in vorschlaege.Take(MaxVorschlaegeImMenu))
            {
                var mi = new MenuItem { Header = $"{v.Lehrer}  ({v.Begruendung})" };
                string gewaehlt = v.Lehrer;
                mi.Click += (_, __) => Buche(z, datum, art, gewaehlt);
                parent.Items.Add(mi);
            }
            if (vorschlaege.Count > MaxVorschlaegeImMenu)
                parent.Items.Add(new MenuItem
                {
                    Header = $"… {vorschlaege.Count - MaxVorschlaegeImMenu} weitere",
                    IsEnabled = false
                });
        }

        // =====================================================
        //  Buchen / Entfernen
        // =====================================================

        private void Buche(TagesZelle z, DateTime datum, Vertretungsart art, string? vertreter)
            => BucheIntern(z, datum, art, vertreter, speichern: true, zeichnen: true);

        // True, wenn die einzusetzende Lehrkraft an diesem Tag/Stunde eine
        // platzierte UV-Stunde hat – dann zählt die Vertretung nicht.
        private bool IstUvEinsatz(string vertreter, DateTime datum, int stunde)
        {
            if (_plan == null) return false;
            string? wtag = _daten.Kalender?.RasterTagFuer(datum);
            return wtag != null && _plan.HatUvImSlot(vertreter, wtag, stunde);
        }

        private void BucheIntern(TagesZelle z, DateTime datum, Vertretungsart art, string? vertreter,
            bool speichern, bool zeichnen)
        {
            if (_plan == null) return;

            bool brauchtVertreter = art == Vertretungsart.Vertretung
                                  || art == Vertretungsart.BetreuungAufsicht
                                  || art == Vertretungsart.Zusammenlegung;
            var grund = CbGrund.SelectedItem is VertretungsGrund g ? g : VertretungsGrund.Sonstiges;

            // Zählwert: normal aus der Art. Vertritt eine Lehrkraft mit einer
            // UV-Stunde in diesem Slot, wird der Zähler NICHT erhöht.
            int zaehlwert = brauchtVertreter ? _daten.Zaehlwert(art) : 0;
            if (brauchtVertreter && vertreter != null && IstUvEinsatz(vertreter, datum, z.Stunde))
                zaehlwert = 0;

            var eintrag = new VertretungsEintrag
            {
                Datum = datum.Date, Stunde = z.Stunde, UNr = z.UNr,
                Art = art, Grund = grund,
                OriginalLehrer = z.OriginalLehrer,
                VertretungsLehrer = brauchtVertreter ? vertreter : null,
                Klassen = new List<string>(z.Klassen), Fach = z.Fach,
                Fachgerecht = (art == Vertretungsart.Vertretung && vertreter != null)
                    ? _plan.Fachnah(vertreter, z.Fach) : (bool?)null,
                ZaehlwertSnapshot = zaehlwert
            };

            EntferneEintraege(datum, z.Stunde, z.UNr, z.OriginalLehrer);
            _daten.Vertretungen.Add(eintrag);

            if (speichern) Speichere();
            if (zeichnen) Zeichne();
        }

        private void Entferne(TagesZelle z, DateTime datum)
        {
            EntferneEintraege(datum, z.Stunde, z.UNr, z.OriginalLehrer);
            Speichere();
            Zeichne();
            SetzeInfo("Buchung entfernt.");
        }

        // =====================================================
        //  Verlegung per Drag&Drop (über die sichtbare Spanne)
        // =====================================================

        // =====================================================
        //  Drag-Feedback (wie Planeditor: ToolTip + farbige Zielzelle + Status)
        // =====================================================

        private void DragUeberZelle(Border zelle, DragEventArgs e, DateTime zielDatum, int zielStunde, string klasse)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(typeof(VerlegDrag)) ||
                e.Data.GetData(typeof(VerlegDrag)) is not VerlegDrag d)
            { e.Effects = DragDropEffects.None; return; }

            // Nur neu bewerten, wenn sich das überfahrene Zielfeld geändert hat
            // (DragOver feuert sonst bei jeder Mausbewegung).
            string key = $"{zielDatum:yyyyMMdd}#{zielStunde}#{klasse}";
            if (key == _letzterDragKey)
            { e.Effects = _letzterDragEffects; return; }
            _letzterDragKey = key;

            var (stufe, text) = BewerteVerlegung(d, zielDatum, zielStunde, klasse);
            switch (stufe)
            {
                case VerlegStufe.Unmoeglich:
                    e.Effects = DragDropEffects.None;
                    MarkiereDragZelle(zelle, text, hart: true);
                    SetzeInfo(text.Replace("\n", "   ·   "));
                    break;
                case VerlegStufe.Hart:
                    e.Effects = DragDropEffects.Move;
                    MarkiereDragZelle(zelle, text, hart: true);
                    SetzeInfo("Konflikt: " + text.Replace("\n", "   ·   ") + "   —   Loslassen: trotzdem verlegen?");
                    break;
                case VerlegStufe.Weich:
                    e.Effects = DragDropEffects.Move;
                    MarkiereDragZelle(zelle, text, hart: false);
                    SetzeInfo("Achtung: " + text.Replace("\n", "   ·   ") + "   —   Loslassen: trotzdem verlegen?");
                    break;
                default:
                    e.Effects = DragDropEffects.Move;
                    EntferneDragMarkierung();
                    SetzeInfo(text);
                    break;
            }
            _letzterDragEffects = e.Effects;
        }

        private void MarkiereDragZelle(Border zelle, string grund, bool hart)
        {
            var farbe = hart ? Brushes.Red : WeicheWarnungBrush;

            if (ReferenceEquals(_dragMarkZelle, zelle))
            {
                zelle.BorderBrush = farbe;
                if (zelle.ToolTip is ToolTip tt0) tt0.Content = grund;
                return;
            }
            EntferneDragMarkierung();

            _dragMarkZelle = zelle;
            zelle.BorderBrush = farbe;
            zelle.BorderThickness = new Thickness(2.5);

            // Während eines aktiven Drag liefert WPF keine Hover-Events, ein per
            // Hover ausgelöster ToolTip erschiene also nie -> erzwungen öffnen.
            zelle.ToolTip = new ToolTip
            {
                Content = grund,
                PlacementTarget = zelle,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                IsOpen = true
            };
        }

        private void EntferneDragMarkierung()
        {
            if (_dragMarkZelle == null) return;
            _dragMarkZelle.BorderBrush = Brushes.Silver;
            _dragMarkZelle.BorderThickness = new Thickness(1);
            if (_dragMarkZelle.ToolTip is ToolTip tt) tt.IsOpen = false;
            _dragMarkZelle.ToolTip = null;
            _dragMarkZelle = null;
        }

        /// <summary>Bewertung für das Drag-Feedback (inkl. Quelle==Ziel).</summary>
        private (VerlegStufe stufe, string text) BewerteVerlegung(
            VerlegDrag d, DateTime zielDatum, int zielStunde, string klasse)
        {
            var src = d.Quelle;
            if (d.QuellDatum.Date == zielDatum.Date && src.Stunde == zielStunde)
                return (VerlegStufe.Unmoeglich, "Quelle und Ziel sind identisch.");
            return BewerteZiel(src.UNr, src.OriginalLehrer, klasse,
                               d.QuellDatum, src.Stunde, zielDatum, zielStunde);
        }

        /// <summary>
        /// Prüft das Ziel einer Verlegung und stuft es ein – dieselben Regeln wie
        /// im Planeditor, soweit für eine Verlegung sinnvoll:
        ///  Unmöglich  = gekoppelt / kein Schultag,
        ///  Hart       = Klasse oder Lehrkraft im Ziel-Slot schon belegt,
        ///  Weich      = Std.Folge überschritten (zu viele Stunden hintereinander)
        ///               oder ein freier Tag der Lehrkraft ginge verloren,
        ///  Keiner     = unkritisch.
        /// </summary>
        private (VerlegStufe stufe, string text) BewerteZiel(
            int unr, string lehrer, string klasse,
            DateTime quellDatum, int quellStunde, DateTime zielDatum, int zielStunde)
        {
            if (_plan == null) return (VerlegStufe.Unmoeglich, "Kein Plan geladen.");

            if (!(_plan.Unterrichte.TryGetValue(unr, out var teile) && teile.Count == 1))
                return (VerlegStufe.Unmoeglich, "Gekoppelter Kurs – keine Verlegung möglich.");

            var tpZiel = TagesplanBerechnung.Berechne(zielDatum, _plan, _daten.Kalender, _daten.Vertretungen);
            if (!tpZiel.IstSchultag)
                return (VerlegStufe.Unmoeglich, $"{zielDatum:dd.MM.} ist kein Schultag.");

            bool selberTag = quellDatum.Date == zielDatum.Date;

            // --- harte Konflikte ---
            var hart = new List<string>();
            if (tpZiel.Zellen.Any(c => c.Stunde == zielStunde && !c.IstFrei &&
                    c.Klassen.Contains(klasse, StringComparer.OrdinalIgnoreCase)))
                hart.Add($"Klasse {klasse} hat am {zielDatum:dd.MM.} in Std {zielStunde} bereits Unterricht.");
            if (tpZiel.Zellen.Any(c => c.Stunde == zielStunde && !c.IstFrei &&
                    string.Equals(c.Lehrer, lehrer, StringComparison.OrdinalIgnoreCase)))
                hart.Add($"{lehrer} unterrichtet am {zielDatum:dd.MM.} in Std {zielStunde} bereits.");
            if (hart.Count > 0) return (VerlegStufe.Hart, string.Join("\n", hart));

            // --- weiche Warnungen (Planeditor-Regeln) ---
            var weich = new List<string>();

            // Belegte Stunden der Lehrkraft am Zieltag. Liegt die Quelle am selben
            // Tag, wird die Quellstunde entfernt – sie wird ja wegverschoben und
            // darf nicht mehr mitgezählt werden (sonst Phantom-Stunde in Folge).
            var belegt = tpZiel.Zellen
                .Where(c => !c.IstFrei && string.Equals(c.Lehrer, lehrer, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Stunde).ToHashSet();
            if (selberTag) belegt.Remove(quellStunde);

            // Freier Tag geht nur bei einem Tageswechsel verloren; bei einer
            // Verschiebung innerhalb desselben Tages unterrichtet die Lehrkraft ohnehin.
            if (!selberTag && belegt.Count == 0)
                weich.Add($"{lehrer} hätte am {zielDatum:dd.MM.} sonst keinen Unterricht – ein freier Tag ginge verloren.");

            belegt.Add(zielStunde);
            if (_plan.StdFolge.TryGetValue(lehrer, out var sf) && sf.HasValue)
            {
                int run = 1;
                for (int j = zielStunde - 1; belegt.Contains(j); j--) run++;
                for (int j = zielStunde + 1; belegt.Contains(j); j++) run++;
                if (run > sf.Value)
                    weich.Add($"{lehrer} hätte {run} Stunden hintereinander (Std.Folge erlaubt {sf.Value}).");
            }
            if (weich.Count > 0) return (VerlegStufe.Weich, string.Join("\n", weich));

            return (VerlegStufe.Keiner, $"Verlegen nach {zielDatum:dd.MM.} Std {zielStunde}.");
        }

        private void VerlegeDrop(VerlegDrag d, DateTime zielDatum, int zielStunde, string klasse)
        {
            var src = d.Quelle;
            BucheVerlegung(d.QuellDatum, src.Stunde, src.UNr, src.OriginalLehrer, src.Fach, src.Klassen,
                           zielDatum, zielStunde, klasse);
        }

        /// <summary>
        /// Kernroutine (Drag&Drop UND Picker): legt eine Verlegung von
        /// (quellDatum, quellStunde) nach (zielDatum, zielStunde) an – mit harter
        /// Konfliktprüfung. Beide Hälften teilen eine VerlegungsId.
        /// </summary>
        private void BucheVerlegung(
            DateTime quellDatum, int quellStunde, int unr, string lehrer, string fach, List<string> klassen,
            DateTime zielDatum, int zielStunde, string klasse)
        {
            if (_plan == null) return;

            if (quellDatum.Date == zielDatum.Date && quellStunde == zielStunde)
            { SetzeInfo("Quelle und Ziel sind identisch."); return; }

            // Quelle muss an dem Datum normal stattfinden (strukturell – nicht umgehbar).
            var tpQuell = TagesplanBerechnung.Berechne(quellDatum, _plan, _daten.Kalender, _daten.Vertretungen);
            var quellZelle = tpQuell.Zellen.FirstOrDefault(c =>
                c.Stunde == quellStunde && c.UNr == unr &&
                string.Equals(c.OriginalLehrer, lehrer, StringComparison.OrdinalIgnoreCase));
            if (quellZelle == null || quellZelle.Status != ZellStatus.Normal)
            { SetzeInfo($"Die Quellstunde am {quellDatum:dd.MM.} ist nicht (mehr) frei verschiebbar."); return; }

            // Ziel bewerten (gekoppelt/Schultag = unmöglich; Klasse/Lehrkraft belegt
            // = hart; Std.Folge/freier Tag = weich). Wie im Planeditor sind harte UND
            // weiche Verstöße nach Rückfrage trotzdem möglich; Unmögliches nicht.
            var (stufe, text) = BewerteZiel(unr, lehrer, klasse, quellDatum, quellStunde, zielDatum, zielStunde);
            if (stufe == VerlegStufe.Unmoeglich)
            { SetzeInfo(text.Replace("\n", "   ·   ")); return; }

            if (stufe == VerlegStufe.Hart || stufe == VerlegStufe.Weich)
            {
                string kopf = stufe == VerlegStufe.Hart ? "Konflikt:" : "Hinweis:";
                var frage = $"{kopf}\n\n{text}\n\nVerlegung trotzdem durchführen?";
                var bild = stufe == VerlegStufe.Hart ? MessageBoxImage.Warning : MessageBoxImage.Question;
                if (MessageBox.Show(frage, "Verlegung", MessageBoxButton.OKCancel, bild) != MessageBoxResult.OK)
                { SetzeInfo("Verlegung abgebrochen."); return; }
            }

            var grund = CbGrund.SelectedItem is VertretungsGrund g ? g : VertretungsGrund.Sonstiges;
            string vid = Guid.NewGuid().ToString("N");

            var quell = new VertretungsEintrag
            {
                Datum = quellDatum.Date, Stunde = quellStunde, UNr = unr,
                Art = Vertretungsart.Verlegung, Grund = grund,
                OriginalLehrer = lehrer, Fach = fach, Klassen = new List<string>(klassen),
                VerlegungsId = vid, VerlegtNachDatum = zielDatum.Date, VerlegtNachStunde = zielStunde,
                ZaehlwertSnapshot = 0
            };
            var ziel = new VertretungsEintrag
            {
                Datum = zielDatum.Date, Stunde = zielStunde, UNr = unr,
                Art = Vertretungsart.Verlegung, Grund = grund,
                OriginalLehrer = lehrer, VertretungsLehrer = lehrer,
                Fach = fach, Klassen = new List<string>(klassen),
                VerlegungsId = vid, VerlegtVonDatum = quellDatum.Date, VerlegtVonStunde = quellStunde,
                ZaehlwertSnapshot = 0
            };

            EntferneEintraege(quellDatum, quellStunde, unr, lehrer);
            _daten.Vertretungen.Add(quell);
            _daten.Vertretungen.Add(ziel);

            Speichere();
            Zeichne();
            SetzeInfo($"Verlegt: {fach} ({klasse}) von {quellDatum:dd.MM.} Std {quellStunde} " +
                      $"nach {zielDatum:dd.MM.} Std {zielStunde}.");
        }

        // =====================================================
        //  Verlegung hierher – tagesübergreifender Picker
        // =====================================================

        private void VerlegeHierher(DateTime zielDatum, int zielStunde, string klasse)
        {
            if (_plan == null) return;

            string? zielWTag = _daten.Kalender.RasterTagFuer(zielDatum);
            var kandidaten = new List<PickKandidat>();
            foreach (var kv in _plan.Belegung)
            {
                foreach (int unr in kv.Value)
                {
                    if (!_plan.Unterrichte.TryGetValue(unr, out var teile) || teile.Count != 1) continue;
                    var t = teile[0];
                    if (!t.Klassen.Contains(klasse, StringComparer.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kv.Key.WTag, zielWTag, StringComparison.OrdinalIgnoreCase) &&
                        kv.Key.Stunde == zielStunde) continue;
                    kandidaten.Add(new PickKandidat
                    {
                        WTag = kv.Key.WTag, Stunde = kv.Key.Stunde,
                        Fach = t.Fach, Lehrer = t.Lehrer, UNr = unr, Klassen = new List<string>(t.Klassen)
                    });
                }
            }

            var rang = _plan.RasterTage.Select((w, i) => (w, i))
                .ToDictionary(x => x.w, x => x.i, StringComparer.OrdinalIgnoreCase);
            kandidaten = kandidaten
                .OrderBy(k => rang.TryGetValue(k.WTag, out int r) ? r : 99)
                .ThenBy(k => k.Stunde).ThenBy(k => k.Fach, StringComparer.OrdinalIgnoreCase).ToList();

            if (kandidaten.Count == 0)
            {
                MessageBox.Show($"Für Klasse {klasse} wurden keine verschiebbaren Stunden gefunden.",
                    "Verlegung", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (gewaehlt, quellDatum) = ZeigeVerlegungPicker(zielDatum, zielStunde, klasse, kandidaten);
            if (gewaehlt == null || quellDatum == null) return;

            string? qWTag = _daten.Kalender.RasterTagFuer(quellDatum.Value);
            if (!string.Equals(qWTag, gewaehlt.WTag, StringComparison.OrdinalIgnoreCase) ||
                !_daten.Kalender.IstSchultag(quellDatum.Value))
            { SetzeInfo($"Quell-Datum passt nicht zum Wochentag {gewaehlt.WTag} oder ist schulfrei."); return; }

            BucheVerlegung(quellDatum.Value, gewaehlt.Stunde, gewaehlt.UNr, gewaehlt.Lehrer,
                           gewaehlt.Fach, gewaehlt.Klassen, zielDatum, zielStunde, klasse);
        }

        private (PickKandidat?, DateTime?) ZeigeVerlegungPicker(
            DateTime zielDatum, int zielStunde, string klasse, List<PickKandidat> kandidaten)
        {
            var win = new Window
            {
                Title = "Verlegung hierher",
                Width = 460, Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.CanResizeWithGrip
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var kopf = new TextBlock
            {
                Text = $"Ziel: Klasse {klasse}, {zielDatum:ddd dd.MM.yyyy} Std {zielStunde}\n" +
                       "Welche Stunde der Klasse soll hierher verlegt werden?",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(kopf, 0); root.Children.Add(kopf);

            var liste = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var k in kandidaten) liste.Items.Add(new ListBoxItem { Content = k.Anzeige, Tag = k });
            Grid.SetRow(liste, 1); root.Children.Add(liste);

            var datumZeile = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            datumZeile.Children.Add(new TextBlock { Text = "Quell-Datum:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var dp = new DatePicker { Width = 150 };
            datumZeile.Children.Add(dp);
            var hinweis = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = Brushes.DimGray };
            datumZeile.Children.Add(hinweis);
            Grid.SetRow(datumZeile, 2); root.Children.Add(datumZeile);

            liste.SelectionChanged += (_, __) =>
            {
                if (liste.SelectedItem is ListBoxItem it && it.Tag is PickKandidat k)
                {
                    dp.SelectedDate = NaechsterPassenderSchultag(k.WTag, zielDatum);
                    hinweis.Text = $"(Wochentag {k.WTag})";
                }
            };

            var btnZeile = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnOk = new Button { Content = "Verlegen", Width = 110, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var btnAbbr = new Button { Content = "Abbrechen", Width = 110, IsCancel = true };
            btnZeile.Children.Add(btnOk); btnZeile.Children.Add(btnAbbr);
            Grid.SetRow(btnZeile, 3); root.Children.Add(btnZeile);

            PickKandidat? ergebnisK = null; DateTime? ergebnisD = null;
            btnOk.Click += (_, __) =>
            {
                if (liste.SelectedItem is not ListBoxItem it || it.Tag is not PickKandidat k)
                { MessageBox.Show(win, "Bitte eine Stunde auswählen.", "Verlegung", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (dp.SelectedDate == null)
                { MessageBox.Show(win, "Bitte ein Quell-Datum wählen.", "Verlegung", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                ergebnisK = k; ergebnisD = dp.SelectedDate.Value.Date;
                win.DialogResult = true;
            };

            win.Content = root;
            if (liste.Items.Count > 0) liste.SelectedIndex = 0;
            return win.ShowDialog() == true ? (ergebnisK, ergebnisD) : (null, null);
        }

        private DateTime? NaechsterPassenderSchultag(string wtag, DateTime ab)
        {
            int idx = SchuljahrKalender.WochentagIndex(wtag);
            if (idx < 0) return null;
            for (var d = ab.Date; d <= _daten.Kalender.Ende.Date.AddDays(7); d = d.AddDays(1))
                if ((int)d.DayOfWeek == idx && _daten.Kalender.IstSchultag(d))
                    return d;
            return null;
        }

        // =====================================================
        //  Hilfsfunktionen
        // =====================================================

        private void EntferneEintraege(DateTime datum, int stunde, int unr, string orig)
        {
            var ids = _daten.Vertretungen
                .Where(v => v.Datum.Date == datum.Date && v.Stunde == stunde && v.UNr == unr &&
                            string.Equals(v.OriginalLehrer, orig, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.VerlegungsId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _daten.Vertretungen.RemoveAll(v =>
                (v.Datum.Date == datum.Date && v.Stunde == stunde && v.UNr == unr &&
                 string.Equals(v.OriginalLehrer, orig, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(v.VerlegungsId) && ids.Contains(v.VerlegungsId)));
        }

        private void Speichere()
        {
            try { VertretungsStore.Speichern(_daten, _vertretungsPfad); }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Speichern:\n\n" + ex.Message +
                    "\n\n(Häufigste Ursache: Datei ist parallel in Excel geöffnet.)",
                    "Wocheneditor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _log?.Invoke($"Wocheneditor: gespeichert ({Path.GetFileName(_vertretungsPfad)}).");

            // Partnerfenster über die geänderten Daten informieren.
            Partner?.NeuLaden();
        }

        private void SetzeInfo(string text) => TxtInfo.Text = text;

        // =====================================================
        //  Kopplung mit dem Partnerfenster (IVertretungsFenster)
        // =====================================================

        /// <summary>
        /// Meldet die aktuelle Auswahl an das Partnerfenster. Es wird nur die
        /// GEÄNDERTE Dimension mitgeschickt (Lehrer ODER Klasse ODER nur Datum),
        /// damit das Erfassen-Fenster weiß, was es anzeigen soll.
        /// </summary>
        private void MeldeAuswahl(bool nurLehrer = false, bool nurKlasse = false)
        {
            if (_laedt || _syncLaeuft || Partner == null) return;
            var a = new VertretungsAuswahl { Datum = DpTag.SelectedDate?.Date };
            if (nurLehrer) a.Lehrer = CbLehrer.SelectedItem as string;
            else if (nurKlasse) a.Klasse = CbKlasse.SelectedItem as string;
            Partner.UebernehmeAuswahl(a);
        }

        /// <summary>Übernimmt eine Auswahl vom Partnerfenster (ohne Rückmeldung).</summary>
        public void UebernehmeAuswahl(VertretungsAuswahl a)
        {
            if (!_bereit || a == null) return;
            bool aenderung = false;
            _syncLaeuft = true;
            try
            {
                if (a.Datum != null && DpTag.SelectedDate?.Date != a.Datum.Value.Date)
                { DpTag.SelectedDate = a.Datum.Value.Date; aenderung = true; }

                if (!string.IsNullOrWhiteSpace(a.Lehrer))
                {
                    var t = CbLehrer.Items.OfType<string>()
                        .FirstOrDefault(x => string.Equals(x, a.Lehrer, StringComparison.OrdinalIgnoreCase));
                    if (t != null && !string.Equals(CbLehrer.SelectedItem as string, t, StringComparison.OrdinalIgnoreCase))
                    { CbLehrer.SelectedItem = t; aenderung = true; }
                }

                if (!string.IsNullOrWhiteSpace(a.Klasse))
                {
                    var t = CbKlasse.Items.OfType<string>()
                        .FirstOrDefault(x => string.Equals(x, a.Klasse, StringComparison.OrdinalIgnoreCase));
                    if (t != null && !string.Equals(CbKlasse.SelectedItem as string, t, StringComparison.OrdinalIgnoreCase))
                    { CbKlasse.SelectedItem = t; aenderung = true; }
                }
            }
            finally { _syncLaeuft = false; }

            if (aenderung) Zeichne();
        }

        /// <summary>Lädt die Vertretungsdaten neu aus der Datei und zeichnet neu.</summary>
        public void NeuLaden()
        {
            if (string.IsNullOrEmpty(_vertretungsPfad)) return;
            try { _daten = VertretungsStore.Laden(_vertretungsPfad); }
            catch { return; }
            _tpCache.Clear();
            if (_bereit) Zeichne();
        }

        private static (string kopf, string zeile2, string zeile3) ZelltextTeile(
            TagesZelle z, bool lehrerSicht, string? bezug)
        {
            string klassen = string.Join(", ", z.Klassen);

            if (lehrerSicht)
            {
                bool istOriginal = string.Equals(z.OriginalLehrer, bezug, StringComparison.OrdinalIgnoreCase);
                bool istEffektiv = string.Equals(z.Lehrer, bezug, StringComparison.OrdinalIgnoreCase);

                string anmerkung;
                if (istOriginal && !istEffektiv && z.Status != ZellStatus.Normal)
                    anmerkung = z.Status == ZellStatus.Entfall ? "entfällt"
                              : z.Status == ZellStatus.VerlegtWeg ? z.Info
                              : $"→ {z.Lehrer}  ({z.Info})";
                else if (istEffektiv && !istOriginal)
                    anmerkung = $"für {z.OriginalLehrer}  ({z.Info})";
                else
                    anmerkung = z.Info;

                return (z.Fach, klassen, anmerkung);
            }
            else
            {
                if (z.Status == ZellStatus.Entfall) return (z.Fach, z.OriginalLehrer, "entfällt");
                if (z.Status == ZellStatus.VerlegtWeg) return ("— frei —", "", z.Info);
                return (z.Fach, z.Lehrer, z.Info);
            }
        }

        private static Brush Hintergrund(ZellStatus s) => s switch
        {
            ZellStatus.Vertreten => new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xC9)),
            ZellStatus.Entfall => new SolidColorBrush(Color.FromRgb(0xFF, 0xCD, 0xD2)),
            ZellStatus.VerlegtHin => new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xB2)),
            ZellStatus.VerlegtWeg => new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0)),
            ZellStatus.Betreuung => new SolidColorBrush(Color.FromRgb(0xBB, 0xDE, 0xFB)),
            ZellStatus.EVA => new SolidColorBrush(Color.FromRgb(0xBB, 0xDE, 0xFB)),
            ZellStatus.Zusammenlegung => new SolidColorBrush(Color.FromRgb(0xBB, 0xDE, 0xFB)),
            ZellStatus.Raumvertretung => new SolidColorBrush(Color.FromRgb(0xFF, 0xF9, 0xC4)),
            _ => Brushes.White
        };
    }
}
