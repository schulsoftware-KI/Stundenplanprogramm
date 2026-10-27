#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// Erfasst Abwesenheiten (auch mehrere Lehrkräfte/Klassen gleichzeitig),
    /// ermittelt die betroffenen Stunden aus Plan × Kalender, schlägt
    /// Vertretungen vor, bucht sie in die Vertretungsdatei und zeigt eine
    /// Tagesübersicht sowie die Zähler.
    ///
    /// Arbeitet auf "Vertretung_&lt;Schuljahr&gt;.xlsx" neben der Plandatei und
    /// setzt einen angelegten Schuljahreskalender voraus (Button 22).
    /// </summary>
    public partial class VertretungsDialog : Window, IVertretungsFenster
    {
        /// <summary>Partnerfenster (Tagesvertretungseditor) für Auswahl-/Daten-Kopplung.</summary>
        public IVertretungsFenster? Partner { get; set; }

        private readonly string _planPfad;
        private readonly Action<string>? _log;
        private readonly string _ordner;

        private string _vertretungsPfad = "";
        private VertretungsDaten _daten = new();
        private PlanDaten? _plan;
        private bool _bereit = false;
        private bool _initFertig = false;   // Von-Changed erst nach dem Konstruktor wirken lassen
        private bool _syncLaeuft = false;   // unterdrückt Rückmeldung während Auswahl-Übernahme

        private readonly List<Abwesenheit> _letzteAbwesenheiten = new();
        private VertretungsGrund _letzteGrund = VertretungsGrund.Sonstiges;

        private readonly ObservableCollection<InstanzZeile> _zeilen = new();
        private readonly ObservableCollection<ZaehlerZeile> _zaehler = new();
        private readonly ObservableCollection<UebersichtZeile> _uebersicht = new();

        public VertretungsDialog(string planPfad, Action<string>? log = null)
        {
            InitializeComponent();
            _planPfad = planPfad ?? "";
            _log = log;
            _ordner = string.IsNullOrWhiteSpace(_planPfad)
                ? Directory.GetCurrentDirectory()
                : (Path.GetDirectoryName(_planPfad) ?? Directory.GetCurrentDirectory());

            CbSubjekt.ItemsSource = Enum.GetValues(typeof(AbwesenheitsSubjekt));
            CbGrund.ItemsSource = Enum.GetValues(typeof(VertretungsGrund));
            CbBulkArt.ItemsSource = Enum.GetValues(typeof(Vertretungsart));
            DgInstanzen.ItemsSource = _zeilen;
            DgZaehler.ItemsSource = _zaehler;
            DgUebersicht.ItemsSource = _uebersicht;

            CbSubjekt.SelectedIndex = 0;
            CbGrund.SelectedIndex = 0;
            CbBulkArt.SelectedItem = Vertretungsart.Entfall;
            DpVon.SelectedDate = DateTime.Today;
            DpBis.SelectedDate = DateTime.Today;
            DpUebersicht.SelectedDate = DateTime.Today;

            // Default-Stunden: von 1. bis 11. Stunde.
            TxtVonStunde.Text = "1";
            TxtBisStunde.Text = "11";

            // Auswahl einer Lehrkraft/Klasse im Namensfeld an das Partnerfenster melden.
            CbName.SelectionChanged += (_, __) => MeldeAuswahl();

            _initFertig = true;
            LadeDaten();
        }

        // =====================================================
        //  Laden
        // =====================================================

        private void LadeDaten()
        {
            _vertretungsPfad = FindeVertretungsdatei() ?? "";
            if (_vertretungsPfad.Length == 0)
            {
                MessageBox.Show(
                    "Es wurde keine Vertretungsdatei gefunden. Bitte zuerst über Button 22 " +
                    "einen Schuljahreskalender anlegen und speichern.",
                    "Vertretung", MessageBoxButton.OK, MessageBoxImage.Information);
                SetzeStatus("Keine Vertretungsdatei – bitte erst Schuljahreskalender anlegen.");
                SperreEingabe();
                return;
            }

            try { _daten = VertretungsStore.Laden(_vertretungsPfad); }
            catch (Exception ex)
            {
                MessageBox.Show("Vertretungsdatei konnte nicht gelesen werden:\n\n" + ex.Message,
                    "Vertretung", MessageBoxButton.OK, MessageBoxImage.Warning);
                SperreEingabe();
                return;
            }

            if (_daten.Kalender.Start == default || _daten.Kalender.Ende == default)
            {
                MessageBox.Show(
                    "Im Schuljahreskalender sind erster/letzter Schultag noch nicht gesetzt. " +
                    "Bitte über Button 22 ergänzen und speichern.",
                    "Vertretung", MessageBoxButton.OK, MessageBoxImage.Information);
                SetzeStatus("Schuljahreskalender unvollständig (kein Start/Ende).");
                SperreEingabe();
                return;
            }

            try { _plan = PlanLeser.Lies(_planPfad); }
            catch (Exception ex)
            {
                MessageBox.Show("Der Plan konnte nicht gelesen werden:\n\n" + ex.Message,
                    "Vertretung", MessageBoxButton.OK, MessageBoxImage.Warning);
                SperreEingabe();
                return;
            }

            CbPlan.ItemsSource = _plan.PlanSpalten;
            CbPlan.SelectedItem = _plan.GewaehltePlanSpalte;

            _bereit = true;
            FuelleNamen((AbwesenheitsSubjekt)CbSubjekt.SelectedItem);
            AktualisiereZaehler();
            AktualisiereUebersicht();
            SetzeStatus($"Bereit. Schuljahr {_daten.Schuljahr}, Plan-Spalte '{_plan.GewaehltePlanSpalte}'.");
            Title = $"Vertretung erfassen – {_daten.Schuljahr}";
        }

        private string? FindeVertretungsdatei()
        {
            try
            {
                return Directory.GetFiles(_ordner, "Vertretung_*.xlsx")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private void SperreEingabe()
        {
            BtnErmitteln.IsEnabled = false;
            BtnBuchen.IsEnabled = false;
            BtnNameHinzu.IsEnabled = false;
            BtnBulkArt.IsEnabled = false;
        }

        // =====================================================
        //  Namen (mehrere, kommagetrennt)
        // =====================================================

        private void CbSubjekt_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_bereit || _plan == null) return;
            if (CbSubjekt.SelectedItem is AbwesenheitsSubjekt subj)
                FuelleNamen(subj);
            MeldeAuswahl();
        }

        private void FuelleNamen(AbwesenheitsSubjekt subj)
        {
            if (_plan == null) return;
            IEnumerable<string> namen;
            switch (subj)
            {
                case AbwesenheitsSubjekt.Lehrer:
                    namen = _plan.AlleLehrer;
                    break;
                case AbwesenheitsSubjekt.Klasse:
                    namen = _plan.Unterrichte.Values.SelectMany(t => t).SelectMany(t => t.Klassen);
                    break;
                case AbwesenheitsSubjekt.Raum:
                    namen = _plan.Unterrichte.Values.SelectMany(t => t)
                        .SelectMany(t => new[] { t.Fachraum, t.Stammraum })
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!);
                    break;
                default:
                    namen = Enumerable.Empty<string>();
                    break;
            }
            CbName.ItemsSource = namen.Distinct(StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            CbName.Text = "";
        }

        private void BtnNameHinzu_Click(object sender, RoutedEventArgs e)
        {
            string n = CbName.Text.Trim();
            if (n.Length == 0) return;
            var liste = SplitNamen(TxtNamen.Text);
            if (!liste.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)))
                liste.Add(n);
            TxtNamen.Text = string.Join(", ", liste);
            CbName.Text = "";
        }

        private static List<string> SplitNamen(string s)
            => string.IsNullOrWhiteSpace(s)
                ? new List<string>()
                : s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        // =====================================================
        //  Betroffene Stunden ermitteln
        // =====================================================

        private void BtnErmitteln_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit || _plan == null) return;

            // Plan-Spalte ggf. wechseln.
            if (CbPlan.SelectedItem is string spalte &&
                !string.Equals(spalte, _plan.GewaehltePlanSpalte, StringComparison.OrdinalIgnoreCase))
            {
                try { _plan = PlanLeser.Lies(_planPfad, spalte); }
                catch (Exception ex)
                {
                    MessageBox.Show("Plan-Spalte konnte nicht geladen werden:\n\n" + ex.Message,
                        "Vertretung", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var namen = SplitNamen(TxtNamen.Text);
            if (namen.Count == 0)
            {
                SetzeStatus("Bitte mindestens einen Namen angeben (Feld 'Namen', mehrere mit Komma).");
                return;
            }
            if (DpVon.SelectedDate == null || DpBis.SelectedDate == null)
            {
                SetzeStatus("Bitte Von- und Bis-Datum wählen.");
                return;
            }
            if (DpBis.SelectedDate.Value.Date < DpVon.SelectedDate.Value.Date)
            {
                SetzeStatus("Das Bis-Datum liegt vor dem Von-Datum.");
                return;
            }

            var subj = (AbwesenheitsSubjekt)CbSubjekt.SelectedItem;
            _letzteGrund = (VertretungsGrund)CbGrund.SelectedItem;
            _letzteAbwesenheiten.Clear();

            var zaehler = VertretungsZaehler.GehaltenProLehrer(_daten.Vertretungen);
            var alleInstanzen = new List<StundenInstanz>();

            foreach (var name in namen)
            {
                var abw = new Abwesenheit
                {
                    Subjekt = subj,
                    Name = name,
                    VonDatum = DpVon.SelectedDate.Value.Date,
                    VonStunde = ParseStunde(TxtVonStunde.Text),
                    BisDatum = DpBis.SelectedDate.Value.Date,
                    BisStunde = ParseStunde(TxtBisStunde.Text),
                    Grund = _letzteGrund
                };
                _letzteAbwesenheiten.Add(abw);
                alleInstanzen.AddRange(VertretungsService.BetroffeneInstanzen(abw, _plan, _daten.Kalender));
            }

            // Duplikate entfernen (z. B. gekoppelte Kurse über mehrere gewählte Klassen).
            var gesehen = new HashSet<string>();
            _zeilen.Clear();
            foreach (var inst in alleInstanzen
                         .OrderBy(i => i.Datum).ThenBy(i => i.Stunde)
                         .ThenBy(i => string.Join(",", i.Klassen)))
            {
                string key = $"{inst.Datum:yyyyMMdd}|{inst.Stunde}|{inst.UNr}|{inst.Lehrer}";
                if (!gesehen.Add(key)) continue;

                var vorschlaege = VertretungsService.Vorschlaege(inst, _plan, _daten.Kalender, zaehler)
                    .Select(v => new VorschlagAnzeige
                    {
                        Lehrer = v.Lehrer,
                        Anzeige = $"{v.Lehrer}  ({v.Begruendung})"
                    })
                    .ToList();

                _zeilen.Add(new InstanzZeile
                {
                    Inst = inst,
                    DatumText = inst.Datum.ToString("ddd dd.MM.yyyy"),
                    Stunde = inst.Stunde,
                    KlassenText = string.Join(", ", inst.Klassen),
                    Fach = inst.Fach,
                    OrigLehrer = inst.Lehrer,
                    Art = Vertretungsart.Vertretung,
                    Vorschlaege = vorschlaege,
                    GewaehlterVertreter = vorschlaege.FirstOrDefault()?.Lehrer,
                    Bemerkung = ""
                });
            }

            SetzeStatus(_zeilen.Count == 0
                ? "Keine betroffenen Stunden (an Schultagen im Zeitraum) gefunden."
                : $"{_zeilen.Count} betroffene Stunde(n) für {namen.Count} Name(n). Art/Vertretung prüfen, dann 'Buchen'.");
        }

        // =====================================================
        //  Sammel-Aktion: markierte Zeilen auf eine Art setzen
        // =====================================================

        private void BtnBulkArt_Click(object sender, RoutedEventArgs e)
        {
            if (CbBulkArt.SelectedItem is not Vertretungsart art) return;
            var markiert = DgInstanzen.SelectedItems.OfType<InstanzZeile>().ToList();
            if (markiert.Count == 0)
            {
                SetzeStatus("Zuerst eine oder mehrere Zeilen markieren (Strg/Umschalt).");
                return;
            }
            foreach (var z in markiert) z.Art = art;
            SetzeStatus($"{markiert.Count} Zeile(n) auf '{art}' gesetzt.");
        }

        // =====================================================
        //  Buchen
        // =====================================================

        // True, wenn die (einzusetzende) Lehrkraft an diesem Tag/Stunde eine
        // platzierte UV-Stunde hat – dann zählt die Vertretung nicht.
        private bool IstUvEinsatz(string vertreter, DateTime datum, int stunde)
        {
            if (_plan == null) return false;
            string? wtag = _daten.Kalender?.RasterTagFuer(datum);
            return wtag != null && _plan.HatUvImSlot(vertreter, wtag, stunde);
        }

        private void BtnBuchen_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit) return;
            DgInstanzen.CommitEdit(DataGridEditingUnit.Cell, true);
            DgInstanzen.CommitEdit(DataGridEditingUnit.Row, true);

            if (_zeilen.Count == 0 || _letzteAbwesenheiten.Count == 0)
            {
                SetzeStatus("Nichts zu buchen – zuerst betroffene Stunden ermitteln.");
                return;
            }

            // Abwesenheiten festhalten (nicht doppelt).
            foreach (var abw in _letzteAbwesenheiten)
            {
                bool vorhanden = _daten.Abwesenheiten.Any(a =>
                    a.Subjekt == abw.Subjekt &&
                    string.Equals(a.Name, abw.Name, StringComparison.OrdinalIgnoreCase) &&
                    a.VonDatum == abw.VonDatum && a.VonStunde == abw.VonStunde &&
                    a.BisDatum == abw.BisDatum && a.BisStunde == abw.BisStunde);
                if (!vorhanden) _daten.Abwesenheiten.Add(abw);
            }

            int gebucht = 0, uebersprungen = 0;
            foreach (var z in _zeilen)
            {
                bool brauchtVertreter = InstanzZeile.ArtBrauchtVertreter(z.Art);
                string? vertreter = string.IsNullOrWhiteSpace(z.GewaehlterVertreter)
                    ? null : z.GewaehlterVertreter!.Trim();

                if (brauchtVertreter && vertreter == null)
                {
                    uebersprungen++;
                    continue;
                }

                // Zählwert: normal aus der Art. Vertritt aber eine Lehrkraft mit
                // einer UV-Stunde in diesem Slot, wird der Zähler NICHT erhöht.
                int zaehlwert = brauchtVertreter ? _daten.Zaehlwert(z.Art) : 0;
                if (brauchtVertreter && vertreter != null && _plan != null && IstUvEinsatz(vertreter, z.Inst.Datum, z.Inst.Stunde))
                    zaehlwert = 0;

                var eintrag = new VertretungsEintrag
                {
                    Datum = z.Inst.Datum,
                    Stunde = z.Inst.Stunde,
                    UNr = z.Inst.UNr,
                    Art = z.Art,
                    Grund = _letzteGrund,
                    OriginalLehrer = z.Inst.Lehrer,
                    VertretungsLehrer = brauchtVertreter ? vertreter : null,
                    Klassen = new List<string>(z.Inst.Klassen),
                    Fach = z.Inst.Fach,
                    Bemerkung = z.Bemerkung ?? "",
                    Fachgerecht = (z.Art == Vertretungsart.Vertretung && vertreter != null && _plan != null)
                        ? _plan.Fachnah(vertreter, z.Inst.Fach)
                        : (bool?)null,
                    ZaehlwertSnapshot = zaehlwert
                };

                _daten.Vertretungen.RemoveAll(v =>
                    v.Datum == eintrag.Datum && v.Stunde == eintrag.Stunde &&
                    v.UNr == eintrag.UNr &&
                    string.Equals(v.OriginalLehrer, eintrag.OriginalLehrer, StringComparison.OrdinalIgnoreCase));

                _daten.Vertretungen.Add(eintrag);
                gebucht++;
            }

            try { VertretungsStore.Speichern(_daten, _vertretungsPfad); }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Speichern:\n\n" + ex.Message +
                    "\n\n(Häufigste Ursache: Datei ist parallel in Excel geöffnet.)",
                    "Vertretung", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AktualisiereZaehler();
            AktualisiereUebersicht();
            string msg = uebersprungen > 0
                ? $"{gebucht} gebucht, {uebersprungen} übersprungen (keine Vertretungskraft gewählt)."
                : $"{gebucht} Stunde(n) gebucht und gespeichert.";
            SetzeStatus(msg);
            _log?.Invoke("Vertretung: " + msg + $" ({Path.GetFileName(_vertretungsPfad)})");

            // Partnerfenster über die geänderten Daten informieren.
            Partner?.NeuLaden();
        }

        // =====================================================
        //  Tagesübersicht
        // =====================================================

        // Beim Ändern des Von-Datums das Bis-Datum automatisch nachziehen
        // (eintägige Abwesenheit ist der Normalfall). Danach vom Nutzer frei
        // auf einen späteren Tag änderbar.
        private void DpVon_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initFertig || _syncLaeuft) return;
            if (DpVon.SelectedDate != null)
            {
                DpBis.SelectedDate = DpVon.SelectedDate;
                DpUebersicht.SelectedDate = DpVon.SelectedDate;
            }
            MeldeAuswahl();
        }

        private void BtnUebersicht_Click(object sender, RoutedEventArgs e) => AktualisiereUebersicht();
        private void DpUebersicht_Changed(object sender, SelectionChangedEventArgs e) => AktualisiereUebersicht();
        private void ChkAlle_Changed(object sender, RoutedEventArgs e) => AktualisiereUebersicht();

        private void AktualisiereUebersicht()
        {
            if (DgUebersicht == null) return;   // kann vor dem Laden feuern
            _uebersicht.Clear();

            bool alle = ChkAlle.IsChecked == true;
            DateTime? tag = DpUebersicht.SelectedDate?.Date;

            IEnumerable<VertretungsEintrag> quelle = _daten.Vertretungen;
            if (!alle && tag != null)
                quelle = quelle.Where(v => v.Datum.Date == tag.Value);

            foreach (var v in quelle.OrderBy(v => v.Datum).ThenBy(v => v.Stunde)
                                     .ThenBy(v => string.Join(",", v.Klassen)))
            {
                _uebersicht.Add(new UebersichtZeile
                {
                    DatumText = v.Datum.ToString("ddd dd.MM.yyyy"),
                    Stunde = v.Stunde,
                    Klasse = string.Join(", ", v.Klassen),
                    Fach = v.Fach,
                    Orig = v.OriginalLehrer,
                    ArtText = v.Art.ToString(),
                    Vertreter = v.VertretungsLehrer ?? "",
                    GrundText = v.Grund.ToString(),
                    Bemerkung = v.Bemerkung
                });
            }

            TxtUebersichtInfo.Text = alle
                ? $"{_uebersicht.Count} Einträge insgesamt."
                : (tag != null
                    ? $"{_uebersicht.Count} Eintrag/Einträge am {tag.Value:dd.MM.yyyy}."
                    : "Kein Tag gewählt.");
        }

        // =====================================================
        //  Export (PDF-Tagesplan, Excel-Zähler)
        // =====================================================

        private void BtnPdfLehrer_Click(object sender, RoutedEventArgs e) => ExportierePdf(mitGrund: true);
        private void BtnPdfAushang_Click(object sender, RoutedEventArgs e) => ExportierePdf(mitGrund: false);

        private void ExportierePdf(bool mitGrund)
        {
            if (!_bereit) return;
            if (ChkAlle.IsChecked == true || DpUebersicht.SelectedDate == null)
            {
                MessageBox.Show("Für den PDF-Export bitte einen einzelnen Tag wählen ('alle Tage' abwählen).",
                    "PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DateTime tag = DpUebersicht.SelectedDate.Value.Date;
            string sicht = mitGrund ? "Lehrer" : "Aushang";
            var dlg = new SaveFileDialog
            {
                Title = "Vertretungsplan speichern",
                Filter = "PDF-Datei (*.pdf)|*.pdf",
                FileName = $"Vertretungsplan_{tag:yyyy-MM-dd}_{sicht}.pdf",
                InitialDirectory = _ordner
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                VertretungsplanPdfExporter.Erzeuge(_daten.Vertretungen, tag, mitGrund, dlg.FileName, _daten.Schuljahr);
            }
            catch (Exception ex)
            {
                MessageBox.Show("PDF konnte nicht erstellt werden:\n\n" + ex.Message,
                    "PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetzeStatus($"PDF erstellt: {Path.GetFileName(dlg.FileName)}");
            _log?.Invoke($"Vertretungsplan-PDF erstellt: {Path.GetFileName(dlg.FileName)}");
            OeffneDatei(dlg.FileName);
        }

        private void BtnZaehlerExcel_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit) return;
            var dlg = new SaveFileDialog
            {
                Title = "Zähler als Excel speichern",
                Filter = "Excel-Datei (*.xlsx)|*.xlsx",
                FileName = $"Vertretungszaehler_{_daten.Schuljahr.Replace('/', '_')}.xlsx",
                InitialDirectory = _ordner
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                ZaehlerExporter.Erzeuge(_daten.Vertretungen, dlg.FileName, _daten.Schuljahr);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Excel konnte nicht erstellt werden:\n\n" + ex.Message,
                    "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetzeStatus($"Zähler exportiert: {Path.GetFileName(dlg.FileName)}");
            _log?.Invoke($"Zähler-Excel erstellt: {Path.GetFileName(dlg.FileName)}");
            OeffneDatei(dlg.FileName);
        }

        private static void OeffneDatei(string pfad)
        {
            try { Process.Start(new ProcessStartInfo(pfad) { UseShellExecute = true }); }
            catch { /* Öffnen ist nur Komfort */ }
        }

        // =====================================================
        //  Zähler
        // =====================================================

        private void BtnZaehlerAktualisieren_Click(object sender, RoutedEventArgs e) => AktualisiereZaehler();

        private void AktualisiereZaehler()
        {
            _zaehler.Clear();
            var stand = VertretungsZaehler.ProLehrer(_daten.Vertretungen);
            foreach (var s in stand.Values.OrderByDescending(s => s.GehalteneVertretungen)
                                           .ThenBy(s => s.Bezug, StringComparer.OrdinalIgnoreCase))
            {
                int ProArt(Vertretungsart a) => s.ProArt.TryGetValue(a, out int n) ? n : 0;
                _zaehler.Add(new ZaehlerZeile
                {
                    Lehrer = s.Bezug,
                    Vertretung = ProArt(Vertretungsart.Vertretung),
                    Betreuung = ProArt(Vertretungsart.BetreuungAufsicht),
                    EVA = ProArt(Vertretungsart.EVA),
                    Zusammenlegung = ProArt(Vertretungsart.Zusammenlegung),
                    EigeneAusfaelle = s.EigeneAusfaelle,
                    Gehalten = s.GehalteneVertretungen,
                    Saldo = s.Saldo
                });
            }
        }

        // =====================================================
        //  Hilfsfunktionen
        // =====================================================

        private static int ParseStunde(string s)
            => int.TryParse((s ?? "").Trim(), out int v) && v > 0 ? v : 0;

        private void SetzeStatus(string text) => TxtStatus.Text = text;

        // =====================================================
        //  Kopplung mit dem Partnerfenster (IVertretungsFenster)
        // =====================================================

        /// <summary>Meldet die aktuelle Auswahl (Datum + Lehrer/Klasse) an das Partnerfenster.</summary>
        private void MeldeAuswahl()
        {
            if (!_bereit || _syncLaeuft || Partner == null) return;

            var subj = CbSubjekt.SelectedItem as AbwesenheitsSubjekt?;
            // Beim Auswählen aus der Liste ist SelectedItem zuverlässiger als Text.
            string? nm = CbName.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(nm))
                nm = string.IsNullOrWhiteSpace(CbName.Text) ? null : CbName.Text.Trim();

            Partner.UebernehmeAuswahl(new VertretungsAuswahl
            {
                Datum = DpVon.SelectedDate?.Date,
                Lehrer = subj == AbwesenheitsSubjekt.Lehrer ? nm : null,
                Klasse = subj == AbwesenheitsSubjekt.Klasse ? nm : null
            });
        }

        /// <summary>Übernimmt eine Auswahl vom Partnerfenster (ohne Rückmeldung).</summary>
        public void UebernehmeAuswahl(VertretungsAuswahl a)
        {
            if (!_bereit || a == null) return;
            _syncLaeuft = true;
            try
            {
                if (a.Datum != null)
                {
                    DpVon.SelectedDate = a.Datum.Value.Date;
                    DpBis.SelectedDate = a.Datum.Value.Date;
                    DpUebersicht.SelectedDate = a.Datum.Value.Date;
                }

                if (!string.IsNullOrWhiteSpace(a.Lehrer))
                    ZeigeNamen(AbwesenheitsSubjekt.Lehrer, a.Lehrer!);
                else if (!string.IsNullOrWhiteSpace(a.Klasse))
                    ZeigeNamen(AbwesenheitsSubjekt.Klasse, a.Klasse!);
            }
            finally { _syncLaeuft = false; }

            AktualisiereUebersicht();
        }

        /// <summary>Setzt Subjekt + Namensfeld (Liste und Anzeige) auf einen Namen.</summary>
        private void ZeigeNamen(AbwesenheitsSubjekt subj, string name)
        {
            if (!Equals(CbSubjekt.SelectedItem, subj)) CbSubjekt.SelectedItem = subj;
            FuelleNamen(subj);
            var treffer = CbName.Items.OfType<string>()
                .FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            CbName.SelectedItem = treffer;   // null, wenn nicht in der Liste
            CbName.Text = treffer ?? name;
        }

        /// <summary>Lädt die Vertretungsdaten neu aus der Datei und aktualisiert die Anzeige.</summary>
        public void NeuLaden()
        {
            if (string.IsNullOrEmpty(_vertretungsPfad)) return;
            try { _daten = VertretungsStore.Laden(_vertretungsPfad); }
            catch { return; }
            if (_bereit)
            {
                AktualisiereZaehler();
                AktualisiereUebersicht();
            }
        }

        private void BtnSchliessen_Click(object sender, RoutedEventArgs e) => Close();

        // =====================================================
        //  Zeilen-Objekte für die Grids
        // =====================================================

        public class VorschlagAnzeige
        {
            public string Lehrer { get; set; } = "";
            public string Anzeige { get; set; } = "";
        }

        public class InstanzZeile : INotifyPropertyChanged
        {
            public StundenInstanz Inst { get; set; } = new();
            public string DatumText { get; set; } = "";
            public int Stunde { get; set; }
            public string KlassenText { get; set; } = "";
            public string Fach { get; set; } = "";
            public string OrigLehrer { get; set; } = "";
            public List<VorschlagAnzeige> Vorschlaege { get; set; } = new();
            public string? GewaehlterVertreter { get; set; }
            public string Bemerkung { get; set; } = "";

            public Array AlleArten => Enum.GetValues(typeof(Vertretungsart));

            private Vertretungsart _art = Vertretungsart.Vertretung;
            public Vertretungsart Art
            {
                get => _art;
                set
                {
                    if (_art == value) return;
                    _art = value;
                    OnChanged(nameof(Art));
                    OnChanged(nameof(BrauchtVertreter));
                }
            }

            public bool BrauchtVertreter => ArtBrauchtVertreter(_art);

            public static bool ArtBrauchtVertreter(Vertretungsart a) => a switch
            {
                Vertretungsart.Entfall => false,
                Vertretungsart.Verlegung => false,
                _ => true
            };

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        public class UebersichtZeile
        {
            public string DatumText { get; set; } = "";
            public int Stunde { get; set; }
            public string Klasse { get; set; } = "";
            public string Fach { get; set; } = "";
            public string Orig { get; set; } = "";
            public string ArtText { get; set; } = "";
            public string Vertreter { get; set; } = "";
            public string GrundText { get; set; } = "";
            public string Bemerkung { get; set; } = "";
        }

        public class ZaehlerZeile
        {
            public string Lehrer { get; set; } = "";
            public int Vertretung { get; set; }
            public int Betreuung { get; set; }
            public int EVA { get; set; }
            public int Zusammenlegung { get; set; }
            public int EigeneAusfaelle { get; set; }
            public int Gehalten { get; set; }
            public int Saldo { get; set; }
        }
    }
}
