#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        private void LadeDaten()
        {
            string? vorhandene = FindeVorhandeneDatei();

            if (vorhandene != null)
            {
                try { _daten = VertretungsStore.Laden(vorhandene); }
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
            SetzeStatus(vorhandene != null
                ? $"Geladen aus {Path.GetFileName(vorhandene)} – {_zeilen.Count} Einträge."
                : "Neuer Kalender. Schuljahr und Schultage eintragen, dann importieren.");
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
            _log?.Invoke($"Schuljahreskalender gespeichert: {Path.GetFileName(ziel)} " +
                         $"({freie.Count} freie Zeiträume).");
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
