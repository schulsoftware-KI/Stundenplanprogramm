#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClosedXML.Excel;
using Microsoft.Win32;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// Pflegt den Schuljahreskalender des Vertretungsmoduls: Ferien, Feiertage,
    /// bewegliche Ferientage und sonstige freie Tage.
    ///
    /// Arbeitet NICHT auf der Plandatei, sondern auf einer eigenen Datei
    /// "Vertretung_&lt;Schuljahr&gt;.xlsx" neben der Plandatei (VertretungsStore).
    /// Gesetzliche Feiertage werden berechnet, die NRW-Ferien aus der Datei
    /// "ferien_nrw.json" neben dem Programm importiert (FeiertageImport).
    ///
    /// Beim Speichern bleiben alle übrigen Inhalte der Vertretungsdatei
    /// (Vertretungsarten, Abwesenheiten, Vertretungen, Kompetenzen) erhalten;
    /// dieser Dialog ersetzt nur den Kalender-Teil.
    ///
    /// P5b-1 (Entscheidung 244): Ein anderes Schuljahr ohne eigene Datei legt ein
    /// NEUES Schuljahr an – eigene Datei mit Vertretungsarten und Kompetenzen, ohne
    /// Abwesenheiten und Vertretungen, leerer Kalender. Die Datei eines anderen
    /// Jahres wird nie überschrieben. „Schuljahr öffnen“ lädt eine vorhandene Datei
    /// und macht sie zur jüngsten – damit arbeiten Wocheneditor und „Vertretung
    /// erfassen“ (wie bisher: die zuletzt geschriebene Vertretung_*.xlsx).
    /// </summary>
    public partial class KalenderDialog : Window
    {
        private readonly string _planPfad;
        private readonly Action<string>? _log;
        private readonly string _ordner;

        private VertretungsDaten _daten = new();
        private FeiertageImport.FerienQuelle _ferienQuelle = new();
        private string _ferienQuellePfad = "";
        private readonly ObservableCollection<FreieZeitZeile> _zeilen = new();
        private bool _dirty = false;
        // P5b-1 (244): Datei, aus der _daten stammt (null = noch nicht gespeichert), neues Schuljahr, Titel aus dem XAML
        private string? _geladenPfad;
        private bool _neuesJahr;
        private bool _jahrPruefen;
        private readonly string _titel;
        private static readonly Regex DateiJahr = new(@"^Vertretung_(\d{4})_(\d{2})\.xlsx$", RegexOptions.IgnoreCase);

        /// <summary>Zeilen-Objekt für das DataGrid (editierbar, wird beim Speichern zurückgelesen).</summary>
        public class FreieZeitZeile
        {
            public FreieZeitTyp Typ { get; set; } = FreieZeitTyp.SchulfreiSonstiges;
            public string Von { get; set; } = "";
            public string Bis { get; set; } = "";
            public string Bezeichnung { get; set; } = "";
            public string Klassen { get; set; } = "";
        }

        public KalenderDialog(string planPfad, Action<string>? log = null)
        {
            InitializeComponent();
            _titel = Title;
            _planPfad = planPfad ?? "";
            _log = log;
            _ordner = string.IsNullOrWhiteSpace(_planPfad)
                ? Directory.GetCurrentDirectory()
                : (Path.GetDirectoryName(_planPfad) ?? Directory.GetCurrentDirectory());

            ColTyp.ItemsSource = Enum.GetValues(typeof(FreieZeitTyp));
            DgKalender.ItemsSource = _zeilen;

            // Änderungen im Grid markieren den Dialog als "ungespeichert".
            _zeilen.CollectionChanged += (_, __) => _dirty = true;
            DgKalender.CellEditEnding += (_, __) => _dirty = true;

            // Erst die (ggf. vorhandene) Vertretungsdatei laden – sie kann den
            // gemerkten Pfad der Ferien-Quelle enthalten –, dann die Quelle.
            LadeDaten();
            LadeFerienQuelle();
        }

        // =====================================================
        //  Laden
        // =====================================================

        private void LadeFerienQuelle()
        {
            string pfad = "";

            // Suchreihenfolge:
            // 1) in der Vertretungsdatei gemerkter Pfad,
            // 2) ferien_nrw.json neben der Vertretungs-/Plandatei (stabiler Ort),
            // 3) ferien_nrw.json neben dem Programm (bin-Ordner – wird bei
            //    "Bereinigen" gelöscht, daher nur als letzter Rückfall).
            if (!string.IsNullOrWhiteSpace(_daten.FerienQuellePfad) && File.Exists(_daten.FerienQuellePfad))
            {
                pfad = _daten.FerienQuellePfad;
            }
            else
            {
                string nebenDatei = Path.Combine(_ordner, "ferien_nrw.json");
                string nebenProgramm = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ferien_nrw.json");
                if (File.Exists(nebenDatei)) pfad = nebenDatei;
                else if (File.Exists(nebenProgramm)) pfad = nebenProgramm;
            }

            if (pfad.Length > 0)
            {
                _ferienQuelle = FeiertageImport.LadeFerienQuelle(pfad, m => _log?.Invoke(m));
                _ferienQuellePfad = pfad;
            }
            else
            {
                // Keine Quelle gefunden ist KEIN Fehler: Feiertage werden berechnet,
                // und ein bereits geladener Kalender behält seine Ferien. Daher hier
                // bewusst KEINE Meldung ins Ausgabefenster – der Hinweis steht nur im
                // Dialog (TxtFerienQuelle), und beim Import wird bei Bedarf gewarnt.
                _ferienQuelle = new FeiertageImport.FerienQuelle();
                _ferienQuellePfad = "";
            }
            AktualisiereFerienQuelleAnzeige();
        }

        private void AktualisiereFerienQuelleAnzeige()
        {
            TxtFerienQuelle.Text = _ferienQuellePfad.Length == 0
                ? "Keine Ferien-Quelle – nur Feiertage werden berechnet."
                : $"Ferien-Quelle: {Path.GetFileName(_ferienQuellePfad)} " +
                  $"({_ferienQuelle.Schuljahre.Count} Schuljahre)";
        }

        private void BtnFerienDatei_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Ferien-Quelle wählen (ferien_nrw.json)",
                Filter = "Ferien-JSON (*.json)|*.json|Alle Dateien (*.*)|*.*",
                FileName = "ferien_nrw.json"
            };

            // Startordner: zuletzt gewählter Pfad, sonst Programmordner.
            try
            {
                string startOrdner = _ferienQuellePfad.Length > 0
                    ? Path.GetDirectoryName(_ferienQuellePfad) ?? ""
                    : AppDomain.CurrentDomain.BaseDirectory;
                if (Directory.Exists(startOrdner)) dlg.InitialDirectory = startOrdner;
            }
            catch { /* Startordner ist nur Komfort */ }

            if (dlg.ShowDialog() != true) return;

            _ferienQuelle = FeiertageImport.LadeFerienQuelle(dlg.FileName, m => _log?.Invoke(m));
            _ferienQuellePfad = dlg.FileName;
            _dirty = true;   // Pfad soll mit "Speichern" dauerhaft gemerkt werden
            AktualisiereFerienQuelleAnzeige();
            SetzeStatus($"Ferien-Quelle geladen: {Path.GetFileName(dlg.FileName)} " +
                        $"({_ferienQuelle.Schuljahre.Count} Schuljahre). Jetzt importieren.");
        }

        /// <param name="pfad">P5b-1: bestimmte Datei („Schuljahr öffnen“), sonst die jüngste.</param>
        private void LadeDaten(string? pfad = null)
        {
            string? vorhandene = pfad ?? FindeVorhandeneDatei();
            _geladenPfad = null;
            _neuesJahr = false;

            if (vorhandene != null)
            {
                try
                {
                    _daten = VertretungsStore.Laden(vorhandene);
                    _geladenPfad = vorhandene;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Vorhandene Vertretungsdatei konnte nicht gelesen werden:\n\n" +
                        ex.Message + "\n\nEs wird ein leerer Kalender geöffnet.",
                        "Kalender", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _daten = new VertretungsDaten();
                }
            }
            else
            {
                _daten = new VertretungsDaten { Schuljahr = RateSchuljahr(DateTime.Today) };
            }

            // Rastertage möglichst aus dem Plan übernehmen (Wochenenden korrekt
            // erkennen); schlägt das fehl, bleibt der Default Mo–Fr.
            var raster = LiesRasterTageAusPlan();
            if (raster != null && raster.Count > 0)
                _daten.Kalender.RasterTage = raster;

            // UI füllen
            TxtSchuljahr.Text = string.IsNullOrWhiteSpace(_daten.Schuljahr)
                ? RateSchuljahr(DateTime.Today) : _daten.Schuljahr;
            DpStart.SelectedDate = _daten.Kalender.Start == default ? (DateTime?)null : _daten.Kalender.Start;
            DpEnde.SelectedDate = _daten.Kalender.Ende == default ? (DateTime?)null : _daten.Kalender.Ende;

            _zeilen.Clear();
            foreach (var fz in _daten.Kalender.FreieZeiten.OrderBy(f => f.Von))
                _zeilen.Add(ZuZeile(fz));

            _dirty = false;
            TitelSetzen();
            SetzeStatus(vorhandene != null
                ? $"Geladen aus {Path.GetFileName(vorhandene)} – {_zeilen.Count} Einträge."
                : "Neuer Kalender. Schuljahr und Schultage eintragen, dann importieren.");
        }

        private void TitelSetzen()
            => Title = string.IsNullOrWhiteSpace(_daten.Schuljahr) ? _titel : $"{_titel} – {_daten.Schuljahr}";

        // =====================================================
        //  P5b-1 (244): Schuljahr wechseln, neues Schuljahr, Schuljahr öffnen
        // =====================================================

        private static bool Gleich(string? a, string? b)
            => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool GleicherPfad(string? a, string? b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Datei eines Schuljahres neben der Plandatei (Standardname), falls vorhanden.</summary>
        private string? DateiVon(string schuljahr)
        {
            string p = Path.Combine(_ordner, VertretungsStore.StandardDateiname(schuljahr));
            return File.Exists(p) ? p : null;
        }

        /// <summary>„Vertretung_2026_27.xlsx“ → „2026/27“ (sonst der Dateiname ohne Endung).</summary>
        private static string JahrAusDatei(string pfad)
        {
            string name = Path.GetFileName(pfad);
            var m = DateiJahr.Match(name);
            return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : Path.GetFileNameWithoutExtension(name);
        }

        private void TxtSchuljahr_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Beim Schließen bzw. Speichern nicht vorher fragen (Speichern prüft selbst, Verwerfen verwirft).
            if (ReferenceEquals(e.NewFocus, BtnSchliessen) || ReferenceEquals(e.NewFocus, BtnSpeichern)) return;
            SchuljahrPruefen();
        }

        private void TxtSchuljahr_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SchuljahrPruefen();
        }

        /// <summary>
        /// Das Feld „Schuljahr“ nennt ein anderes Jahr als die geladene Datei: vorhandene Datei öffnen bzw. neues Schuljahr
        /// anlegen (Rückfrage). false = abgebrochen bzw. eine andere Datei geöffnet (Speichern dann nicht fortsetzen).
        /// </summary>
        private bool SchuljahrPruefen()
        {
            if (_jahrPruefen) return false;
            string neu = TxtSchuljahr.Text.Trim();
            string alt = _daten.Schuljahr?.Trim() ?? "";
            if (neu.Length == 0 || Gleich(neu, alt)) return true;
            _jahrPruefen = true;
            try
            {
                string? vorhanden = DateiVon(neu);
                if (vorhanden != null && !GleicherPfad(vorhanden, _geladenPfad))
                {
                    if (MessageBox.Show(this,
                            $"Für das Schuljahr {neu} gibt es schon die Datei {Path.GetFileName(vorhanden)} – sie wird nicht " +
                            $"überschrieben.\n\nDiese Datei jetzt öffnen?" +
                            (_dirty ? "\n\nUngespeicherte Änderungen am Kalender von " + (alt.Length > 0 ? alt : "bisher") +
                                      " gehen dabei verloren." : ""),
                            "Schuljahr", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        Oeffnen(vorhanden, nachfragen: false);
                    else
                        TxtSchuljahr.Text = alt;
                    return false;
                }
                if (_geladenPfad == null || alt.Length == 0)
                {
                    // Noch keine Datei (neuer Kalender bzw. neues Schuljahr in Arbeit) bzw. Datei ohne Schuljahr: nur das Jahr ändern.
                    _daten.Schuljahr = neu;
                    _dirty = true;
                    TitelSetzen();
                    return true;
                }
                if (MessageBox.Show(this,
                        $"Neues Schuljahr {neu} anlegen?\n\n" +
                        $"Beim Speichern entsteht die Datei {VertretungsStore.StandardDateiname(neu)} – mit den Vertretungsarten und " +
                        $"Kompetenzen aus {alt}, ohne Abwesenheiten und Vertretungen. Der Kalender beginnt leer: erster und letzter " +
                        "Schultag eintragen, dann „Feiertage + NRW-Ferien importieren“.\n\n" +
                        $"Die Datei von {alt} bleibt unverändert (zurück mit „Schuljahr öffnen“). Nach dem Speichern arbeiten " +
                        $"Wocheneditor und „Vertretung erfassen“ mit {neu}." +
                        (_dirty ? $"\n\nUngespeicherte Änderungen am Kalender von {alt} gehen verloren." : ""),
                        "Neues Schuljahr", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    TxtSchuljahr.Text = alt;
                    return false;
                }
                NeuesSchuljahr(neu);
                return true;
            }
            finally { _jahrPruefen = false; }
        }

        /// <summary>Neues Schuljahr (244): Arten und Kompetenzen übernehmen, Kalender leer, keine Abwesenheiten/Vertretungen.</summary>
        private void NeuesSchuljahr(string neu)
        {
            string alt = _daten.Schuljahr;
            var raster = _daten.Kalender.RasterTage.ToList();
            _daten = new VertretungsDaten
            {
                Schuljahr = neu,
                PlanDateiPfad = _planPfad,
                Version = _daten.Version,
                FerienQuellePfad = _daten.FerienQuellePfad,
                Kalender = new SchuljahrKalender { Schuljahr = neu, RasterTage = raster },
                Arten = _daten.Arten,
                Kompetenzen = _daten.Kompetenzen
            };
            _geladenPfad = null;
            _neuesJahr = true;
            DpStart.SelectedDate = null;
            DpEnde.SelectedDate = null;
            _zeilen.Clear();
            _dirty = true;
            TitelSetzen();
            SetzeStatus($"Neues Schuljahr {neu} (aus {alt}: {_daten.Arten.Count} Vertretungsarten, {_daten.Kompetenzen.Count} " +
                        "Kompetenzen; ohne Abwesenheiten und Vertretungen). Ersten und letzten Schultag eintragen, „Feiertage + " +
                        "NRW-Ferien importieren“, dann „Speichern und schließen“.");
        }

        private void BtnSchuljahrOeffnen_Click(object sender, RoutedEventArgs e)
        {
            string[] dateien;
            try
            {
                dateien = Directory.GetFiles(_ordner, "Vertretung_*.xlsx")
                    .OrderByDescending(x => JahrAusDatei(x), StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch { dateien = Array.Empty<string>(); }
            var menue = new ContextMenu { PlacementTarget = BtnSchuljahrOeffnen };
            if (dateien.Length == 0)
                menue.Items.Add(new MenuItem { Header = "(keine Datei Vertretung_*.xlsx neben der Plandatei)", IsEnabled = false });
            string? juengste = FindeVorhandeneDatei();
            foreach (var f in dateien)
            {
                string datei = f;
                var mi = new MenuItem
                {
                    Header = $"{JahrAusDatei(datei)}  ({Path.GetFileName(datei)}" +
                             (GleicherPfad(datei, juengste) ? ", damit arbeiten Wocheneditor und „Vertretung erfassen“" : "") + ")",
                    IsCheckable = false,
                    IsChecked = GleicherPfad(datei, _geladenPfad)
                };
                mi.Click += (_, __) => Oeffnen(datei, nachfragen: true);
                menue.Items.Add(mi);
            }
            BtnSchuljahrOeffnen.ContextMenu = menue;
            menue.IsOpen = true;
        }

        /// <summary>
        /// Öffnet die Datei eines Schuljahres und macht sie zur jüngsten (Änderungszeit = jetzt) – damit arbeiten
        /// Wocheneditor und „Vertretung erfassen“ beim nächsten Öffnen (244). Der Inhalt bleibt unverändert.
        /// </summary>
        private void Oeffnen(string pfad, bool nachfragen)
        {
            if (nachfragen && _dirty &&
                MessageBox.Show(this, "Die Änderungen am Kalender wurden noch nicht gespeichert und gehen verloren.\n\nTrotzdem " +
                                      $"{JahrAusDatei(pfad)} öffnen?",
                    "Schuljahr öffnen", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            LadeDaten(pfad);
            LadeFerienQuelle();
            if (_geladenPfad == null) return;   // nicht lesbar (Meldung kam schon)
            string sj = string.IsNullOrWhiteSpace(_daten.Schuljahr) ? JahrAusDatei(pfad) : _daten.Schuljahr;
            try
            {
                File.SetLastWriteTimeUtc(pfad, DateTime.UtcNow);
                _log?.Invoke($"Schuljahr {sj} geöffnet ({Path.GetFileName(pfad)}) – Wocheneditor und „Vertretung erfassen“ " +
                             "arbeiten jetzt damit.");
                SetzeStatus($"Schuljahr {sj} geöffnet ({Path.GetFileName(pfad)}, {_zeilen.Count} Einträge) – Wocheneditor und " +
                            "„Vertretung erfassen“ arbeiten ab dem nächsten Öffnen damit.");
            }
            catch (Exception ex)
            {
                SetzeStatus($"Schuljahr {sj} geladen, aber nicht als aktuelles gemerkt ({ex.Message} – ist die Datei in Excel " +
                            "geöffnet?). „Speichern und schließen“ merkt es.");
                _dirty = true;
            }
        }

        private string? FindeVorhandeneDatei()
        {
            try
            {
                var treffer = Directory.GetFiles(_ordner, "Vertretung_*.xlsx")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                return treffer;
            }
            catch { return null; }
        }

        // Liest die im Stundenraster vorkommenden Wochentage aus dem Sheet "Lös"
        // der Plandatei (Spalte 1 = WTag, dort wo Spalte 2 eine Stundenzahl ist).
        // Best effort – fehlt die Datei/das Sheet, wird null zurückgegeben.
        private System.Collections.Generic.List<string>? LiesRasterTageAusPlan()
        {
            if (string.IsNullOrWhiteSpace(_planPfad) || !File.Exists(_planPfad)) return null;
            try
            {
                using var wb = new XLWorkbook(_planPfad);
                if (!wb.Worksheets.TryGetWorksheet("Lös", out var ws)) return null;

                var tage = new System.Collections.Generic.List<string>();
                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    string wtag = row.Cell(1).GetString().Trim();
                    if (wtag.Length == 0) continue;
                    if (!int.TryParse(row.Cell(2).GetString().Trim(), out _)) continue; // nur echte Stundenzeilen
                    if (!tage.Contains(wtag)) tage.Add(wtag);
                }
                return tage.Count > 0 ? tage : null;
            }
            catch { return null; }
        }

        // =====================================================
        //  Import Feiertage + NRW-Ferien
        // =====================================================

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            string schuljahr = TxtSchuljahr.Text.Trim();
            if (schuljahr.Length == 0)
            {
                MessageBox.Show("Bitte zuerst das Schuljahr eintragen (z. B. 2026/27).",
                    "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (DpStart.SelectedDate == null || DpEnde.SelectedDate == null)
            {
                MessageBox.Show("Bitte ersten und letzten Schultag wählen – sie begrenzen den Import.",
                    "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DateTime start = DpStart.SelectedDate.Value.Date;
            DateTime ende = DpEnde.SelectedDate.Value.Date;
            if (ende < start)
            {
                MessageBox.Show("Der letzte Schultag liegt vor dem ersten.",
                    "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var importiert = FeiertageImport.ImportSchuljahr(
                schuljahr, start, ende, _ferienQuelle, Bundesland.NRW);

            // Feiertage, die außerhalb von [start, ende] liegen, nicht aufnehmen;
            // Ferien dürfen über die Ränder hinausragen (z. B. Sommerferien).
            int neu = 0;
            foreach (var fz in importiert)
            {
                if (fz.Typ == FreieZeitTyp.Feiertag &&
                    (fz.Von.Date < start || fz.Von.Date > ende))
                    continue;

                if (!SchonVorhanden(fz))
                {
                    _zeilen.Add(ZuZeile(fz));
                    neu++;
                }
            }

            // Nach Datum sortiert anzeigen.
            SortiereZeilen();

            string hinweis = (!_ferienQuelle.Schuljahre.ContainsKey(schuljahr))
                ? "  Hinweis: Für dieses Schuljahr stehen in 'ferien_nrw.json' keine Ferien – nur Feiertage wurden ergänzt."
                : "";
            SetzeStatus($"{neu} neue Einträge importiert (Feiertage berechnet, Ferien aus NRW-Quelle)." + hinweis);
            _dirty = true;
        }

        private bool SchonVorhanden(FreieZeit fz)
        {
            string v = fz.Von.ToString("yyyy-MM-dd");
            string b = fz.Bis.ToString("yyyy-MM-dd");
            return _zeilen.Any(z =>
                z.Typ == fz.Typ &&
                z.Von.Trim() == v &&
                z.Bis.Trim() == b &&
                string.Equals(z.Bezeichnung.Trim(), fz.Bezeichnung.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // =====================================================
        //  Zeilen löschen
        // =====================================================

        private void MnuZeileLoeschen_Click(object sender, RoutedEventArgs e)
        {
            var markiert = DgKalender.SelectedItems.OfType<FreieZeitZeile>().ToList();
            if (markiert.Count == 0)
            {
                SetzeStatus("Zum Löschen zunächst eine oder mehrere Zeilen markieren.");
                return;
            }
            foreach (var z in markiert) _zeilen.Remove(z);
            SetzeStatus($"{markiert.Count} Zeile(n) gelöscht (wird mit 'Speichern und schließen' geschrieben).");
        }

        // =====================================================
        //  Speichern
        // =====================================================

        private void BtnSpeichern_Click(object sender, RoutedEventArgs e)
        {
            DgKalender.CommitEdit(DataGridEditingUnit.Cell, true);
            DgKalender.CommitEdit(DataGridEditingUnit.Row, true);

            string schuljahr = TxtSchuljahr.Text.Trim();
            if (schuljahr.Length == 0)
            {
                MessageBox.Show("Bitte das Schuljahr eintragen (z. B. 2026/27).",
                    "Speichern", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // P5b-1 (244): anderes Schuljahr → neues Schuljahr anlegen bzw. vorhandene Datei öffnen (Rückfrage)
            if (!Gleich(schuljahr, _daten.Schuljahr) && !SchuljahrPruefen()) return;
            schuljahr = TxtSchuljahr.Text.Trim();

            // Zeilen einlesen und validieren.
            var freie = new System.Collections.Generic.List<FreieZeit>();
            int zeilenNr = 0;
            foreach (var z in _zeilen)
            {
                zeilenNr++;
                bool vonLeer = string.IsNullOrWhiteSpace(z.Von);
                bool bisLeer = string.IsNullOrWhiteSpace(z.Bis);
                if (vonLeer && bisLeer) continue; // leere (Platzhalter-)Zeile überspringen

                if (!TryParseDatum(z.Von, out var von) || !TryParseDatum(z.Bis, out var bis))
                {
                    MessageBox.Show($"Zeile {zeilenNr}: Datum nicht lesbar. Bitte Format JJJJ-MM-TT verwenden " +
                                    $"(Von='{z.Von}', Bis='{z.Bis}').",
                        "Speichern", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (bis < von)
                {
                    MessageBox.Show($"Zeile {zeilenNr}: 'Bis' liegt vor 'Von'.",
                        "Speichern", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                freie.Add(new FreieZeit
                {
                    Typ = z.Typ,
                    Von = von,
                    Bis = bis,
                    Bezeichnung = z.Bezeichnung?.Trim() ?? "",
                    BetroffeneKlassen = SplitKlassen(z.Klassen)
                });
            }

            // _daten (mit allen übrigen Sheets) beibehalten, nur Kalender/Meta ersetzen.
            _daten.Schuljahr = schuljahr;
            _daten.PlanDateiPfad = _planPfad;
            _daten.FerienQuellePfad = _ferienQuellePfad;
            _daten.Kalender.Schuljahr = schuljahr;
            _daten.Kalender.Start = DpStart.SelectedDate?.Date ?? default;
            _daten.Kalender.Ende = DpEnde.SelectedDate?.Date ?? default;
            _daten.Kalender.FreieZeiten = freie;

            string ziel = Path.Combine(_ordner, VertretungsStore.StandardDateiname(schuljahr));
            // P5b-1 (244): nie die Datei eines anderen Schuljahres überschreiben
            if (File.Exists(ziel) && !GleicherPfad(ziel, _geladenPfad))
            {
                MessageBox.Show($"Die Datei {Path.GetFileName(ziel)} gibt es schon – sie gehört zu einem anderen Stand und wird " +
                                "nicht überschrieben. Zum Bearbeiten „Schuljahr öffnen“ wählen.",
                    "Speichern", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                VertretungsStore.Speichern(_daten, ziel);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Schreiben:\n\n" + ex.Message +
                    "\n\n(Häufigste Ursache: Datei ist parallel in Excel geöffnet.)",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _dirty = false;
            _geladenPfad = ziel;
            _log?.Invoke(_neuesJahr
                ? $"Neues Schuljahr {schuljahr} angelegt: {Path.GetFileName(ziel)} ({freie.Count} freie Zeiträume; " +
                  $"{_daten.Arten.Count} Vertretungsarten und {_daten.Kompetenzen.Count} Kompetenzen übernommen, ohne " +
                  "Abwesenheiten und Vertretungen). Wocheneditor und „Vertretung erfassen“ arbeiten jetzt damit."
                : $"Schuljahreskalender gespeichert: {Path.GetFileName(ziel)} " +
                  $"({freie.Count} freie Zeiträume).");
            _neuesJahr = false;
            Close();
        }

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_dirty &&
                MessageBox.Show(
                    "Die Änderungen am Kalender wurden noch nicht gespeichert und gehen verloren.\n\nTrotzdem schließen?",
                    "Verwerfen", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK)
            {
                e.Cancel = true;
            }
        }

        // =====================================================
        //  Hilfsfunktionen
        // =====================================================

        private void SortiereZeilen()
        {
            var sortiert = _zeilen.OrderBy(z =>
                TryParseDatum(z.Von, out var d) ? d : DateTime.MaxValue).ToList();
            _zeilen.Clear();
            foreach (var z in sortiert) _zeilen.Add(z);
        }

        private static FreieZeitZeile ZuZeile(FreieZeit fz) => new()
        {
            Typ = fz.Typ,
            Von = fz.Von.ToString("yyyy-MM-dd"),
            Bis = fz.Bis.ToString("yyyy-MM-dd"),
            Bezeichnung = fz.Bezeichnung,
            Klassen = string.Join(";", fz.BetroffeneKlassen)
        };

        private static System.Collections.Generic.List<string> SplitKlassen(string s)
            => string.IsNullOrWhiteSpace(s)
                ? new System.Collections.Generic.List<string>()
                : s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private static bool TryParseDatum(string s, out DateTime d)
        {
            s = (s ?? "").Trim();
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out d)) { d = d.Date; return true; }
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ||
                DateTime.TryParse(s, CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.None, out d))
            { d = d.Date; return true; }
            d = default;
            return false;
        }

        // Schuljahr aus einem Datum raten: ab August gehört man ins Schuljahr
        // JJJJ/(JJJJ+1), davor ins vorige.
        private static string RateSchuljahr(DateTime d)
        {
            int startjahr = d.Month >= 8 ? d.Year : d.Year - 1;
            return $"{startjahr}/{(startjahr + 1) % 100:D2}";
        }

        private void SetzeStatus(string text) => TxtStatus.Text = text;
    }
}
