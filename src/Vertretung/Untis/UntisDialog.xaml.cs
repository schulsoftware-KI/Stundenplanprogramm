#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// U-1b: Fenster „Vertretungen aus Untis“ (Knopf „Untis-Vertretungen…“ im Wocheneditor). Liest die Untis-Datei
    /// GPU014.TXT, prüft und übersetzt sie (UntisLeser, UntisUebersetzung), zeigt Übersicht, Probleme und eine Vorschau
    /// je Tag und veröffentlicht die Schultage des Zeitraums Tag für Tag (jeder Tag wird in der App komplett ersetzt).
    /// Im Desktop bleibt nur das Protokoll untis_import.json (ohne Inhalte); die gelesenen Daten liegen nur im
    /// Arbeitsspeicher und werden beim Schließen freigegeben.
    /// U-2: Das Fenster ist nicht mehr modal (der Wocheneditor bleibt bedienbar) und kann den Ordner der Datei
    /// beobachten – dann werden nur die geänderten Tage gesendet (UntisDialog_Beobachten.cs).
    /// </summary>
    public partial class UntisDialog : Window
    {
        private readonly string _planOrdner;
        private readonly string _schuljahr;
        private readonly SchuljahrKalender _kal;
        private readonly Func<(PlanDaten? Plan, string? Fehler)> _planHolen;
        private readonly Func<ISet<DateTime>> _buchungsTageHolen;
        private readonly DateTime _basis;
        private readonly Action<string>? _log;

        private UntisDatei? _datei;
        private UntisErgebnis? _ergebnis;
        private bool _laeuft;
        private bool _zeitraumSetzen;
        private CancellationTokenSource? _abbruch;

        private const string Hilfe =
            "So kommen die Untis-Vertretungen in die App\n\n" +
            "1. In Untis die Vertretungen als DIF-Datei exportieren („Export Vertretungen“, Datei GPU014.TXT). Trennzeichen " +
            "Semikolon oder Komma, Zeichensatz UTF-8 oder ANSI – beides erkennt das Programm.\n" +
            "2. Die Datei nur auf diesem Rechner ablegen (z. B. C:\\Vertretung\\), nicht in OneDrive, Teams oder einem anderen " +
            "Cloud-Ordner: Eine echte GPU014.TXT enthält Absenzgründe von Lehrkräften. Nach dem Veröffentlichen löschen.\n" +
            "3. Hier „Datei wählen…“ – das Fenster prüft sofort: Reiter „Übersicht“ (Zahlen), „Probleme“ (Zeile, Spalte, Grund) " +
            "und „Vorschau“ (je Tag, wie die Einträge in der App ankommen).\n" +
            "4. Zeitraum prüfen (Vorgabe: erster bis letzter Tag der Datei) und „Veröffentlichen“. Jeder Schultag im Zeitraum " +
            "wird in der App komplett ersetzt – auch Tage ohne Vertretung (dann leer), damit in Untis gelöschte Vertretungen " +
            "verschwinden. Ferien und Feiertage laut Kalender (Button 22) werden übersprungen.\n" +
            "5. Nach jeder Änderungsrunde in Untis neu exportieren und veröffentlichen – oder „Ordner beobachten…“ " +
            "(unten) einschalten, dann geschieht das von selbst (siehe „Ordner beobachten“ am Ende).\n\n" +
            "Voraussetzung: Unter „Plan“ im Wocheneditor ist der aus Untis importierte Plan gewählt (Knopf 3, GPU001.TXT), und " +
            "genau dieser Grundplan ist veröffentlicht („Grundplan veröffentlichen…“). Sonst passen U-Nr und Lehrkräfte nicht " +
            "und viele Zeilen sind „ohne Gegenstück“.\n\n" +
            "Was übernommen wird: Vertretungen (auch Lehrertausch, Teil-Vertretung, Sondereinsatz), offene Vertretungen (in der App " +
            "„???“), Entfall und Freisetzung („entfällt“), Plus als Vertreter (EVA), Betreuung, Verlegungen (alte und neue " +
            "Stelle), Raumvertretungen, Klausuren (mit Aufsicht und Raum, Teilnehmer des Kurses), Raum-, Fach- und " +
            "Klassenwechsel. Nicht übernommen: Pausenaufsichten, Klausuraufsichten ohne Klasse („Klau“) und Zeilen mit „nicht " +
            "drucken“. Absenzgrund (Feld 16) und Text zur Vertretung (Feld 17) werden nie gelesen, angezeigt, gespeichert oder " +
            "gesendet.\n\n" +
            "Quelle: In „Online-Einstellungen…“ „Untis“ wählen. Dann fragt „Online veröffentlichen“ im Wocheneditor nach, bevor " +
            "ein Tag aus dem Vertretungsmodul die Untis-Vertretungen in der App ersetzt.\n\n" +
            "Protokoll: untis_import.json neben der Plandatei – wann welche Datei für welche Tage veröffentlicht wurde, ohne " +
            "Inhalte.\n\n" +
            "Testen: „Demo-Datei erzeugen…“ schreibt aus dem gewählten Plan eine GPU014.TXT für eine Woche mit erfundenen " +
            "Abwesenheiten und allen Fällen (Liste nach dem Erzeugen und in UNTIS_DEMO.json). In Feld 16/17 stehen dort nur die " +
            "Testwerte „GEHEIM-ABSENZGRUND“ und „GEHEIM-TEXT“ – sie dürfen nirgends erscheinen.";

        public UntisDialog(string planOrdner, string schuljahr, SchuljahrKalender kalender,
                           Func<(PlanDaten? Plan, string? Fehler)> planHolen, Func<ISet<DateTime>> buchungsTage,
                           DateTime basis, Action<string>? log = null)
        {
            InitializeComponent();
            _planOrdner = planOrdner ?? "";
            _schuljahr = schuljahr ?? "";
            _kal = kalender;
            _planHolen = planHolen;
            _buchungsTageHolen = buchungsTage;
            _basis = basis.Date;
            _log = log;
            TxtHilfe.Text = Hilfe + HilfeBeobachten;
            LbBeobachten.ItemsSource = _beobProtokoll;
            TxtPlan.Text = "Plan: wird beim Prüfen gelesen (Auswahl „Plan“ im Wocheneditor)";
            ZeigeQuelle();
        }

        // =====================================================
        //  Datei wählen und prüfen
        // =====================================================

        private async void BtnDatei_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft || Beobachtet) return;
            var prot = UntisImportProtokoll.Laden(_planOrdner);
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Untis-Vertretungen (GPU014.TXT) wählen",
                Filter = "Untis-Vertretungen (GPU014*.TXT)|GPU014*.TXT|Textdateien (*.txt)|*.txt|Alle Dateien (*.*)|*.*"
            };
            string? start = File.Exists(TxtDatei.Text) ? Path.GetDirectoryName(TxtDatei.Text) : prot.Ordner;
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
            else if (Directory.Exists(_planOrdner)) dlg.InitialDirectory = _planOrdner;
            if (dlg.ShowDialog(this) != true) return;

            TxtDatei.Text = dlg.FileName;
            try
            {
                // Gemerkt wird nur der Ordner, nicht die Datei (§3 Punkt 1).
                prot.Ordner = Path.GetDirectoryName(dlg.FileName);
                prot.Speichern(_planOrdner);
            }
            catch { /* nur Komfort */ }
            await PruefenAsync();
        }

        private async void BtnPruefen_Click(object sender, RoutedEventArgs e) => await PruefenAsync();

        private async Task PruefenAsync()
        {
            if (_laeuft) return;
            string pfad = TxtDatei.Text.Trim();
            if (pfad.Length == 0 || !File.Exists(pfad))
            {
                MessageBox.Show(this, "Bitte zuerst die Untis-Datei (GPU014.TXT) wählen.", "Untis-Vertretungen",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Plan der gewählten Plan-Spalte (UI-Thread: nutzt die Auswahl im Wocheneditor).
            PlanDaten? plan;
            string? fehler;
            try { (plan, fehler) = _planHolen(); }
            catch (Exception ex) { plan = null; fehler = ex.Message; }
            if (plan == null)
            {
                MessageBox.Show(this, "Der Plan konnte nicht gelesen werden:\n\n" + (fehler ?? "unbekannter Fehler"),
                    "Untis-Vertretungen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TxtPlan.Text = $"Plan: „{plan.GewaehltePlanSpalte}“";

            _laeuft = true;
            SetzeKnoepfe(false);
            TxtStatus.Text = "Lese und prüfe die Datei …";
            UntisDatei? datei = null;
            UntisErgebnis? erg = null;
            try
            {
                var kal = _kal;
                var tage = _buchungsTageHolen();
                PlanDaten p = plan;
                (datei, erg) = await Task.Run(() =>
                {
                    var d = UntisLeser.Lies(pfad);
                    return (d, UntisUebersetzung.Uebersetze(d, p, kal, tage, DateTime.Today));
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Lesen fehlgeschlagen:\n\n" + ex.Message, "Untis-Vertretungen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _laeuft = false;
            }

            if (datei == null || erg == null)
            {
                _datei = null;
                _ergebnis = null;
                SetzeKnoepfe(true);
                TxtStatus.Text = "Prüfen fehlgeschlagen.";
                return;
            }
            ZeigeErgebnis(datei, erg, pfad);
            int f = erg.Anzahl(UntisProblemArt.Fehler);
            int w = erg.Anzahl(UntisProblemArt.Warnung);
            TxtStatus.Text = f > 0 ? $"Geprüft: {f} Zeile(n) mit Fehlern werden nicht gesendet – siehe „Probleme“."
                           : w > 0 ? $"Geprüft ohne Fehler, {w} Warnung(en)."
                           : "Geprüft ohne Fehler und Warnungen.";
            _log?.Invoke($"Untis: {Path.GetFileName(pfad)} geprüft ({(datei.IstDemo ? "Demo-Datei" : "echte Datei")}, " +
                         $"{datei.Zeilen.Count} Zeilen, {erg.Tage.Values.Sum(l => l.Count)} Einträge, {f} Fehler, {w} Warnungen).");
        }

        /// <summary>Zeigt ein Prüfergebnis an (Übersicht, Probleme, Zeitraum, Vorschau) – auch für U-2.</summary>
        private void ZeigeErgebnis(UntisDatei datei, UntisErgebnis erg, string pfad, bool waehleTag = true)
        {
            _datei = datei;
            _ergebnis = erg;
            SetzeKnoepfe(!_laeuft);

            // Echte Datei mit Absenzgründen in einem Cloud-Ordner? (Entscheidung 123)
            bool cloud = !_datei.IstDemo && _datei.MitAbsenzgrund > 0 && IstCloudOrdner(pfad);
            if (cloud)
                _ergebnis.Probleme.Insert(0, new UntisProblem
                {
                    Art = UntisProblemArt.Warnung,
                    Grund = $"Die Datei enthält Absenzgründe (Feld 16, in {_datei.MitAbsenzgrund} Zeilen) und liegt in einem " +
                            "Cloud-Ordner (OneDrive, Teams …). Echte GPU014-Dateien nur auf diesem Rechner ablegen, z. B. " +
                            "C:\\Vertretung\\, und nach dem Veröffentlichen löschen."
                });

            TxtArt.Text = _datei.DemoText + (cloud ? "  ⚠ Liegt in einem Cloud-Ordner." : "");
            BrdArt.Background = new SolidColorBrush(_datei.IstDemo
                ? Color.FromRgb(0xDC, 0xEF, 0xD9) : Color.FromRgb(0xFF, 0xF1, 0xCC));

            DgZahlen.ItemsSource = _ergebnis.Zahlen;
            FiltereProbleme();
            int f = _ergebnis.Anzahl(UntisProblemArt.Fehler);
            int w = _ergebnis.Anzahl(UntisProblemArt.Warnung);
            int h = _ergebnis.Anzahl(UntisProblemArt.Hinweis);
            TxtProblemeKopf.Text = $"Probleme ({f} Fehler · {w} Warnungen · {h} Hinweise)";

            _zeitraumSetzen = true;
            DpVon.SelectedDate = _ergebnis.ErsterTag;
            DpBis.SelectedDate = _ergebnis.LetzterTag;
            _zeitraumSetzen = false;
            ZeitraumAnzeigen(waehleTag);

            BtnBericht.IsEnabled = !_laeuft;
            ZeigeQuelle();
        }

        private static bool IstCloudOrdner(string pfad)
        {
            try
            {
                string p = Path.GetFullPath(pfad);
                foreach (var name in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
                {
                    string? v = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrWhiteSpace(v) &&
                        p.StartsWith(v.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return Regex.IsMatch(p, @"[\\/](OneDrive|Dropbox|Google Drive|GoogleDrive|iCloudDrive)( - [^\\/]*)?[\\/]",
                                     RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => FiltereProbleme();

        private void FiltereProbleme()
        {
            if (_ergebnis == null || DgProbleme == null) return;
            bool f = ChkFehler?.IsChecked == true, w = ChkWarnungen?.IsChecked == true, h = ChkHinweise?.IsChecked == true;
            DgProbleme.ItemsSource = _ergebnis.Probleme.Where(p =>
                (p.Art == UntisProblemArt.Fehler && f) ||
                (p.Art == UntisProblemArt.Warnung && w) ||
                (p.Art == UntisProblemArt.Hinweis && h)).ToList();
        }

        // =====================================================
        //  Zeitraum und Vorschau
        // =====================================================

        private void Zeitraum_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_zeitraumSetzen) return;
            ZeitraumAnzeigen(waehleTag: false);
        }

        private (DateTime Von, DateTime Bis)? Zeitraum()
        {
            if (DpVon.SelectedDate is not DateTime von || DpBis.SelectedDate is not DateTime bis) return null;
            return von.Date <= bis.Date ? (von.Date, bis.Date) : null;
        }

        private void ZeitraumAnzeigen(bool waehleTag)
        {
            if (_ergebnis == null) return;
            var z = Zeitraum();
            DateTime? bisher = (CbTag.SelectedItem as UntisTagAuswahl)?.Datum;
            if (z == null)
            {
                TxtTage.Text = "Zeitraum ungültig.";
                CbTag.ItemsSource = null;
                BtnVeroeffentlichen.IsEnabled = false;
                ZeigeVorschau();
                return;
            }
            var tage = UntisUebersetzung.Schultage(_kal, z.Value.Von, z.Value.Bis);
            int mit = tage.Count(t => _ergebnis.EintraegeAm(t).Count > 0);
            int eintraege = tage.Sum(t => _ergebnis.EintraegeAm(t).Count);
            TxtTage.Text = $"{tage.Count} Schultag(e), davon {mit} mit Einträgen ({eintraege}) und {tage.Count - mit} leer";
            BtnVeroeffentlichen.IsEnabled = !_laeuft && tage.Count > 0;

            var auswahl = tage.Select(t => new UntisTagAuswahl { Datum = t, Anzahl = _ergebnis.EintraegeAm(t).Count, Schultag = true }).ToList();
            CbTag.ItemsSource = auswahl;
            UntisTagAuswahl? wahl = null;
            if (!waehleTag && bisher != null) wahl = auswahl.FirstOrDefault(a => a.Datum == bisher);
            wahl ??= auswahl.FirstOrDefault(a => a.Anzahl > 0 && a.Datum >= DateTime.Today)
                     ?? auswahl.FirstOrDefault(a => a.Anzahl > 0)
                     ?? auswahl.FirstOrDefault();
            CbTag.SelectedItem = wahl;
            ZeigeVorschau();
        }

        private void CbTag_SelectionChanged(object sender, SelectionChangedEventArgs e) => ZeigeVorschau();

        private void BtnTagZurueck_Click(object sender, RoutedEventArgs e) => BlaettereTag(-1);
        private void BtnTagVor_Click(object sender, RoutedEventArgs e) => BlaettereTag(+1);

        private void BlaettereTag(int richtung)
        {
            int n = CbTag.Items.Count;
            if (n == 0) return;
            int i = CbTag.SelectedIndex < 0 ? 0 : CbTag.SelectedIndex + richtung;
            CbTag.SelectedIndex = Math.Max(0, Math.Min(n - 1, i));
        }

        private void ZeigeVorschau()
        {
            if (_ergebnis == null || CbTag.SelectedItem is not UntisTagAuswahl tag)
            {
                DgVorschau.ItemsSource = null;
                TxtVorschauInfo.Text = "";
                TxtVorschauKopf.Text = "Vorschau";
                return;
            }
            var liste = _ergebnis.EintraegeAm(tag.Datum)
                .OrderBy(x => x.Stunde).ThenBy(x => x.KlassenText, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Zeile).ToList();
            DgVorschau.ItemsSource = liste;
            TxtVorschauKopf.Text = $"Vorschau ({_ergebnis.Tage.Count} Tage mit Einträgen)";
            int ohne = liste.Count(x => x.Gegenstueck == "nein");
            TxtVorschauInfo.Text = liste.Count == 0
                ? "Keine Einträge – der Tag wird beim Veröffentlichen in der App leer (frühere Vertretungen verschwinden)."
                : $"{liste.Count} Einträge" + (ohne > 0 ? $", davon {ohne} ohne Gegenstück im Plan (grau)" : "") +
                  ". Farben wie im Wocheneditor, Klausur pink.";
        }

        // =====================================================
        //  Veröffentlichen
        // =====================================================

        private async void BtnVeroeffentlichen_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft || _ergebnis == null || _datei == null) return;
            using var pause = PauseBeobachten();   // U-2: kein Beobachten-Durchgang während Rückfrage und Senden
            var z = Zeitraum();
            if (z == null)
            {
                MessageBox.Show(this, "Bitte einen gültigen Zeitraum wählen (von ≤ bis).", "Veröffentlichen",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(_schuljahr))
            {
                MessageBox.Show(this, "Im Schuljahreskalender ist kein Schuljahr eingetragen (Button 22).", "Veröffentlichen",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var tage = UntisUebersetzung.Schultage(_kal, z.Value.Von, z.Value.Bis);
            if (tage.Count == 0)
            {
                MessageBox.Show(this, "Im Zeitraum liegt kein Schultag (laut Kalender).", "Veröffentlichen",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var einst = HoleEinstellungenMitQuelle("veröffentlichen");
            if (einst == null) return;

            int mit = tage.Count(t => _ergebnis.EintraegeAm(t).Count > 0);
            int eintraege = tage.Sum(t => _ergebnis.EintraegeAm(t).Count);
            var buchungsTage = _buchungsTageHolen();
            var buchungen = tage.Where(t => buchungsTage.Contains(t)).ToList();
            int fehler = _ergebnis.Anzahl(UntisProblemArt.Fehler);
            string frage =
                $"Vertretungen aus Untis veröffentlichen?\n\n" +
                $"Zeitraum: {z.Value.Von:dd.MM.yyyy} bis {z.Value.Bis:dd.MM.yyyy} – {tage.Count} Schultag(e), davon {mit} mit " +
                $"Einträgen ({eintraege}) und {tage.Count - mit} leer.\n" +
                "Jeder dieser Tage wird in der App komplett ersetzt – Tage ohne Einträge werden leer.\n" +
                (buchungen.Count > 0
                    ? $"\n⚠ Für {buchungen.Count} dieser Tage gibt es Buchungen im Vertretungsmodul (z. B. " +
                      $"{string.Join(", ", buchungen.Take(3).Select(t => t.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)))}) – " +
                      "sie verschwinden dort aus der App.\n"
                    : "") +
                (fehler > 0 ? $"\n⚠ {fehler} Zeile(n) mit Fehlern werden nicht gesendet (Reiter „Probleme“).\n" : "") +
                (_ergebnis.OhneGegenstueck > 0
                    ? $"\n{_ergebnis.OhneGegenstueck} Zeile(n) ohne Gegenstück im Plan (erscheinen als zusätzliche Stunden bzw. ohne Wirkung).\n"
                    : "") +
                $"\nPlan: „{_ergebnis.PlanName}“ – der veröffentlichte Grundplan muss zu diesem Plan passen.\n" +
                $"Server: {einst.BaseUrl}";
            if (MessageBox.Show(this, frage, "Veröffentlichen", MessageBoxButton.OKCancel,
                    buchungen.Count > 0 || fehler > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            await VeroeffentlicheAsync(einst, tage, z.Value.Von, z.Value.Bis);
        }

        /// <summary>
        /// Serveradresse und Quelle prüfen (Entscheidung 120): fehlt die Adresse, wird nachgefragt; steht die Quelle auf
        /// „Vertretungsmodul“, bietet das Fenster an, sie auf „Untis“ umzustellen. null = abgebrochen.
        /// </summary>
        private OnlineEinstellungen? HoleEinstellungenMitQuelle(string wozu)
        {
            var einst = OnlineSync.LadeEinstellungen(_planOrdner);
            if (string.IsNullOrWhiteSpace(einst.BaseUrl))
            {
                var neu = OnlineSync.FrageEinstellungen(this, einst, _planOrdner);
                if (neu == null) return null;
                OnlineSync.SpeichereEinstellungen(_planOrdner, neu);
                einst = neu;
            }

            // Eine Quelle je Tag (Entscheidung 120).
            if (!einst.QuelleIstUntis)
            {
                var r = MessageBox.Show(this,
                    "In den Online-Einstellungen ist „Vertretungsmodul“ als Quelle der Vertretungen eingestellt.\n\n" +
                    $"Ja = Quelle auf „Untis“ umstellen und {wozu} (empfohlen: dann fragt „Online veröffentlichen“ im " +
                    "Wocheneditor nach, bevor ein Tag aus dem Vertretungsmodul die Untis-Vertretungen ersetzt).\n" +
                    $"Nein = nur {wozu}, Quelle bleibt „Vertretungsmodul“.",
                    "Quelle der Vertretungen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return null;
                if (r == MessageBoxResult.Yes)
                {
                    einst.Quelle = OnlineEinstellungen.QuelleUntis;
                    try { OnlineSync.SpeichereEinstellungen(_planOrdner, einst); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Die Online-Einstellungen konnten nicht gespeichert werden:\n\n" + ex.Message,
                            "Quelle der Vertretungen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    ZeigeQuelle();
                }
            }
            return einst;
        }

        private async Task VeroeffentlicheAsync(OnlineEinstellungen einst, List<DateTime> tage, DateTime von, DateTime bis)
        {
            if (_ergebnis == null || _datei == null) return;
            var r = await SendeTageAsync(einst, _datei, _ergebnis, tage, von, bis, automatisch: false, unveraendert: 0);

            if (r.Fehler != null)
                MessageBox.Show(this, $"Veröffentlichen bei {r.FehlerTag:dd.MM.yyyy} fehlgeschlagen:\n\n{r.Fehler}\n\n{r.Zahlen}",
                    "Veröffentlichen", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (r.Abgebrochen)
                MessageBox.Show(this, "Abgebrochen.\n\n" + r.Zahlen + "\nDie übrigen Tage sind in der App unverändert.",
                    "Veröffentlichen", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(this, r.Zahlen, "Veröffentlichen", MessageBoxButton.OK, MessageBoxImage.Information);
            if (r.ProtokollFehler != null)
                TxtStatus.Text = "Protokoll untis_import.json konnte nicht gespeichert werden: " + r.ProtokollFehler;
        }

        /// <summary>Ergebnis eines Sendelaufs (manuell oder beim Beobachten).</summary>
        private sealed class SendeErgebnis
        {
            public int Geplant { get; init; }
            public int Gesendet { get; set; }
            public int Eintraege { get; set; }
            public bool Abgebrochen { get; set; }
            public string? Fehler { get; set; }
            public DateTime? FehlerTag { get; set; }
            public string? ProtokollFehler { get; set; }
            /// <summary>Gesendete Tage (für die Liste beim Beobachten).</summary>
            public List<DateTime> Tage { get; } = new();
            public string Zahlen => $"{Gesendet} von {Geplant} Tag(en) veröffentlicht ({Eintraege} Einträge).";
        }

        /// <summary>
        /// Sendet die Tage nacheinander über POST /api/plan/push (jeder Tag wird in der App komplett ersetzt), mit
        /// Fortschritt und „Abbrechen“; bricht beim ersten Fehler ab. Schreibt je gesendetem Tag die Prüfsumme und am Ende
        /// den Lauf ins Protokoll untis_import.json. Gemeinsam für „Veröffentlichen“ und „Ordner beobachten“ (U-2).
        /// </summary>
        private async Task<SendeErgebnis> SendeTageAsync(OnlineEinstellungen einst, UntisDatei datei, UntisErgebnis erg,
            List<DateTime> tage, DateTime von, DateTime bis, bool automatisch, int unveraendert)
        {
            var sync = new OnlineSync { BaseUrl = einst.BaseUrl, ApiKey = einst.ApiKey };
            var prot = UntisImportProtokoll.Laden(_planOrdner);
            _abbruch = new CancellationTokenSource();
            var token = _abbruch.Token;
            var r = new SendeErgebnis { Geplant = tage.Count };

            _laeuft = true;
            SetzeKnoepfe(false);
            PbFortschritt.Minimum = 0;
            PbFortschritt.Maximum = Math.Max(1, tage.Count);
            PbFortschritt.Value = 0;
            PbFortschritt.Visibility = Visibility.Visible;
            BtnAbbrechen.Visibility = Visibility.Visible;
            BtnAbbrechen.IsEnabled = true;

            try
            {
                foreach (var tag in tage)
                {
                    if (token.IsCancellationRequested) { r.Abgebrochen = true; break; }
                    TxtFortschritt.Text = (automatisch ? "Beobachten: " : "") +
                                          $"Tag {r.Gesendet + 1} von {tage.Count}: {UntisText.Tag(tag)} {tag:dd.MM.yyyy} …";
                    var dto = erg.PushFuer(_schuljahr, tag);
                    try
                    {
                        await sync.PushAsync(dto, token);
                        prot.TagGesendet(dto);
                        r.Gesendet++;
                        r.Eintraege += dto.Eintraege.Count;
                        r.Tage.Add(tag);
                        PbFortschritt.Value = r.Gesendet;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        r.Abgebrochen = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        r.Fehler = ex is TaskCanceledException ? "Zeitüberschreitung – der Server hat nicht geantwortet." : OnlineSync.FehlerText(ex, _planOrdner);
                        r.FehlerTag = tag;
                        break;
                    }
                }
            }
            finally
            {
                prot.Laeufe.Add(new UntisImportLauf
                {
                    Zeit = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Datei = datei.Pfad,
                    Demo = datei.IstDemo,
                    Plan = erg.PlanName,
                    Von = UntisImportProtokoll.TagSchluessel(von),
                    Bis = UntisImportProtokoll.TagSchluessel(bis),
                    TageGeplant = tage.Count,
                    TageGesendet = r.Gesendet,
                    Eintraege = r.Eintraege,
                    Abgebrochen = r.Abgebrochen,
                    Fehler = r.Fehler == null ? null : $"bei {r.FehlerTag:dd.MM.yyyy}",
                    Automatisch = automatisch,
                    TageUnveraendert = unveraendert
                });
                try { prot.Speichern(_planOrdner); }
                catch (Exception ex) { r.ProtokollFehler = ex.Message; }

                _laeuft = false;
                _abbruch?.Dispose();
                _abbruch = null;
                PbFortschritt.Visibility = Visibility.Collapsed;
                BtnAbbrechen.Visibility = Visibility.Collapsed;
                SetzeKnoepfe(true);
            }

            TxtFortschritt.Text = (automatisch ? "Beobachten: " : "") +
                                  (r.Abgebrochen ? "Abgebrochen – " + r.Zahlen : r.Fehler != null ? "Fehler – " + r.Zahlen : r.Zahlen);
            _log?.Invoke($"Online: Untis-Vertretungen {(automatisch ? "(Ordner beobachten) " : "")}{von:dd.MM.yyyy}–{bis:dd.MM.yyyy}: " +
                         r.Zahlen + (automatisch ? $" {unveraendert} Tag(e) unverändert." : "") +
                         (r.Abgebrochen ? " (abgebrochen)" : r.Fehler != null ? $" (Fehler bei {r.FehlerTag:dd.MM.yyyy})" : ""));
            return r;
        }

        private void BtnAbbrechen_Click(object sender, RoutedEventArgs e)
        {
            _abbruch?.Cancel();
            BtnAbbrechen.IsEnabled = false;
            TxtFortschritt.Text = "Breche ab …";
        }

        // =====================================================
        //  Demo-Datei
        // =====================================================

        private async void BtnDemo_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft) return;
            using var pause = PauseBeobachten();   // U-2: die neue Datei erst nach den Rückfragen beobachten
            var opt = FrageDemoOptionen();
            if (opt == null) return;

            PlanDaten? plan;
            string? fehler;
            try { (plan, fehler) = _planHolen(); }
            catch (Exception ex) { plan = null; fehler = ex.Message; }
            if (plan == null)
            {
                MessageBox.Show(this, "Der Plan konnte nicht gelesen werden:\n\n" + (fehler ?? "unbekannter Fehler"),
                    "Demo-Datei", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ziel = Path.Combine(opt.Zielordner, UntisGenerator.DateiName);
            if (File.Exists(ziel))
            {
                bool demo;
                try { demo = UntisDemo.Pruefe(ziel, File.ReadAllBytes(ziel), out _); }
                catch { demo = false; }
                if (!demo)
                {
                    MessageBox.Show(this, "Im Ordner liegt eine GPU014.TXT, die keine Demo-Datei ist:\n" + opt.Zielordner +
                        "\n\nBitte einen anderen Ordner wählen – echte Dateien werden nie überschrieben.", "Demo-Datei",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (MessageBox.Show(this, "Im Ordner liegt schon eine Demo-Datei. Ersetzen?\n" + opt.Zielordner, "Demo-Datei",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }

            _laeuft = true;
            SetzeKnoepfe(false);
            TxtStatus.Text = "Erzeuge Demo-Datei …";
            UntisDemoErgebnis? erg = null;
            try
            {
                var kal = _kal;
                PlanDaten p = plan;
                erg = await Task.Run(() => UntisGenerator.Erzeuge(p, kal, opt));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Demo-Datei konnte nicht erzeugt werden:\n\n" + ex.Message, "Demo-Datei",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _laeuft = false;
                SetzeKnoepfe(true);
            }
            if (erg == null) { TxtStatus.Text = "Demo-Datei nicht erzeugt."; return; }

            _log?.Invoke($"Untis: Demo-Datei erzeugt ({erg.Zeilen} Zeilen, Plan „{plan.GewaehltePlanSpalte}“, Woche ab " +
                         $"{opt.Montag:dd.MM.yyyy}, Startwert {opt.Startwert}) in {opt.Zielordner}");
            string text =
                $"Demo-Datei erzeugt:\n{erg.Pfad}\n\n{erg.Zeilen} Zeilen, Woche ab {opt.Montag:dd.MM.yyyy}, Plan „{plan.GewaehltePlanSpalte}“.\n\n" +
                "Enthaltene Fälle (auch in UNTIS_DEMO.json):\n• " + string.Join("\n• ", erg.Faelle.Take(45)) +
                (erg.Faelle.Count > 45 ? "\n• …" : "");
            if (Beobachtet)
            {
                // U-2: Liegt die Demo-Datei im beobachteten Ordner, prüft und sendet das Beobachten sie gleich von selbst.
                bool gleich = string.Equals(Path.GetFullPath(erg.Pfad), Path.GetFullPath(_beobPfad ?? ""), StringComparison.OrdinalIgnoreCase);
                MessageBox.Show(this, text + "\n\n" + (gleich
                        ? "Die Datei liegt im beobachteten Ordner – sie wird gleich von selbst geprüft, geänderte Tage werden gesendet."
                        : "Beobachtet wird ein anderer Ordner. Zum Prüfen dieser Datei erst „Beobachten beenden“."),
                    "Demo-Datei", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = "Demo-Datei erzeugt.";
                return;
            }
            if (MessageBox.Show(this, text + "\n\nJetzt prüfen?", "Demo-Datei", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                TxtDatei.Text = erg.Pfad;
                await PruefenAsync();
            }
            else TxtStatus.Text = "Demo-Datei erzeugt.";
        }

        /// <summary>Kleiner Dialog für die Demo-Optionen (null = Abbruch).</summary>
        private UntisDemoOptionen? FrageDemoOptionen()
        {
            // Vorgabe: die Woche des Datums im Wocheneditor; an einem Wochenende die folgende Woche.
            DateTime basis = _basis;
            if (basis.DayOfWeek == DayOfWeek.Saturday) basis = basis.AddDays(2);
            else if (basis.DayOfWeek == DayOfWeek.Sunday) basis = basis.AddDays(1);
            DateTime montag = basis.AddDays(-(((int)basis.DayOfWeek + 6) % 7));
            string zielVorgabe = Path.Combine(_planOrdner, $"Untis-Demo_{DateTime.Today:yyyy-MM-dd}");

            var win = new Window
            {
                Title = "Demo-Datei erzeugen",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Erzeugt aus der im Wocheneditor gewählten Plan-Spalte eine GPU014.TXT für eine Woche (Montag bis Freitag) " +
                       "im Format von Untis – mit erfundenen Abwesenheiten und allen Fällen, die das Fenster kennen muss. In " +
                       "Feld 16/17 stehen nur die Testwerte „GEHEIM-ABSENZGRUND“ und „GEHEIM-TEXT“, die nirgends erscheinen dürfen.",
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
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Zielordner für die Demo-Datei" };
                string? start = Directory.Exists(txtZiel.Text) ? txtZiel.Text : Path.GetDirectoryName(txtZiel.Text);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
                if (dlg.ShowDialog(win) == true) txtZiel.Text = dlg.FolderName;
            };

            var wocheZeile = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            wocheZeile.Children.Add(new TextBlock { Text = "Woche ab Montag:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var dp = new DatePicker { SelectedDate = montag, Width = 140 };
            wocheZeile.Children.Add(dp);
            wocheZeile.Children.Add(new TextBlock
            {
                Text = "  (ein anderer Tag zählt als seine Woche)",
                Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(wocheZeile);

            var startZeile = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            startZeile.Children.Add(new TextBlock { Text = "Startwert für den Zufall:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var txtStart = new TextBox { Text = "42", Width = 70 };
            startZeile.Children.Add(txtStart);
            startZeile.Children.Add(new TextBlock
            {
                Text = "  (gleicher Startwert + gleicher Plan = gleiche Datei)",
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

            UntisDemoOptionen? ergebnis = null;
            ok.Click += (_, __) =>
            {
                string ziel = txtZiel.Text.Trim();
                if (ziel.Length == 0 || !Path.IsPathRooted(ziel))
                {
                    MessageBox.Show(win, "Bitte einen vollständigen Zielordner angeben.", "Demo-Datei",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (dp.SelectedDate == null)
                {
                    MessageBox.Show(win, "Bitte die Woche wählen.", "Demo-Datei", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!int.TryParse(txtStart.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start))
                {
                    MessageBox.Show(win, "Der Startwert muss eine ganze Zahl sein.", "Demo-Datei",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                DateTime t = dp.SelectedDate.Value.Date;
                ergebnis = new UntisDemoOptionen
                {
                    Zielordner = ziel,
                    Montag = t.AddDays(-(((int)t.DayOfWeek + 6) % 7)),
                    Startwert = start
                };
                win.DialogResult = true;
            };

            return win.ShowDialog() == true ? ergebnis : null;
        }

        // =====================================================
        //  Allgemein
        // =====================================================

        private void BtnBericht_Click(object sender, RoutedEventArgs e)
        {
            if (_ergebnis == null || _datei == null) return;
            try
            {
                Clipboard.SetText(_ergebnis.Bericht(_datei));
                TxtStatus.Text = "Bericht in die Zwischenablage kopiert (ohne Absenzgründe und Texte).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Zwischenablage nicht verfügbar:\n\n" + ex.Message, "Bericht",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ZeigeQuelle()
        {
            var einst = OnlineSync.LadeEinstellungen(_planOrdner);
            TxtQuelle.Text = einst.QuelleIstUntis
                ? "Quelle (Online-Einstellungen): Untis"
                : "Quelle (Online-Einstellungen): Vertretungsmodul – beim Veröffentlichen fragt das Fenster nach";
            TxtQuelle.Foreground = einst.QuelleIstUntis ? Brushes.DimGray : Brushes.DarkOrange;
        }

        private void SetzeKnoepfe(bool an)
        {
            BtnDatei.IsEnabled = an && !Beobachtet;
            BtnBeobachten.IsEnabled = an;
            BtnJetztSenden.IsEnabled = an;
            BtnPruefen.IsEnabled = an;
            BtnDemo.IsEnabled = an;
            DpVon.IsEnabled = an;
            DpBis.IsEnabled = an;
            BtnBericht.IsEnabled = an && _ergebnis != null;
            var z = an && _ergebnis != null ? Zeitraum() : null;
            BtnVeroeffentlichen.IsEnabled = z != null && UntisUebersetzung.Schultage(_kal, z.Value.Von, z.Value.Bis).Count > 0;
        }

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_laeuft)
            {
                e.Cancel = true;
                TxtStatus.Text = _abbruch != null
                    ? "Bitte warten oder „Abbrechen“ wählen – es wird gerade veröffentlicht."
                    : "Bitte warten, bis der Vorgang fertig ist …";
            }
            else if (Beobachtet && !_ohneRueckfrageSchliessen)
            {
                using var pause = PauseBeobachten();
                if (MessageBox.Show(this,
                        "Der Ordner wird beobachtet. Mit dem Schließen des Fensters endet das Beobachten – neue Untis-Dateien " +
                        "werden dann nicht mehr von selbst veröffentlicht.\n\nFenster schließen und Beobachten beenden?",
                        "Ordner beobachten", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    e.Cancel = true;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // U-2: Beobachten endet mit dem Fenster.
            if (Beobachtet) BeendeBeobachten("Fenster geschlossen");
            _gehalten = null;
            // Gelesene Daten freigeben (nur Arbeitsspeicher).
            DgZahlen.ItemsSource = null;
            DgProbleme.ItemsSource = null;
            DgVorschau.ItemsSource = null;
            CbTag.ItemsSource = null;
            _ergebnis = null;
            _datei = null;
            base.OnClosed(e);
        }
    }
}
