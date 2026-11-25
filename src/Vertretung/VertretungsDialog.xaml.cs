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
            _plan.Raeume = PlanRaeume.Lade(_plan);   // R3-D: Raumplan + Raumverzeichnis

            CbPlan.ItemsSource = _plan.PlanSpalten;
            CbPlan.SelectedItem = _plan.GewaehltePlanSpalte;

            _bereit = true;
            FuelleNamen((AbwesenheitsSubjekt)CbSubjekt.SelectedItem);
            AktualisiereZaehler();
            AktualisiereUebersicht();
            SetzeStatus($"Bereit. Schuljahr {_daten.Schuljahr}, Plan-Spalte '{_plan.GewaehltePlanSpalte}'. {_plan.Raeume.Info}");
            Title = $"Vertretung erfassen – {_daten.Schuljahr}";
        }

        private string? FindeVertretungsdatei()
        {
            return VertretungsStore.ArbeitsDatei(_ordner);   // P5b-2 (F1): Arbeitsjahr laut Kalender, sonst die jüngste
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
            // R3-D-Nachtrag: Namen einer anderen Art (z. B. Lehrkräfte, wenn jetzt „Raum“ gewählt ist) passen nicht mehr.
            if (e.RemovedItems.Count > 0) TxtNamen.Text = "";
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
                    // R3-D: Räume aus Raumverzeichnis, Raumplan und UV („A~B“ einzeln).
                    namen = (_plan.Raeume?.AlleRaeume ?? Enumerable.Empty<string>())
                        .Concat(_plan.Unterrichte.Values.SelectMany(t => t)
                            .SelectMany(t => PlanRaeume.Zerlege(t.Fachraum).Concat(PlanRaeume.Zerlege(t.Stammraum))));
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
                _plan.Raeume = PlanRaeume.Lade(_plan);
            }
            // R3-D: Räume neu lesen, wenn die Plandatei inzwischen gespeichert wurde (z. B. Knopf 25).
            else if (_plan.Raeume == null || _plan.Raeume.IstVeraltet(_planPfad))
                _plan.Raeume = PlanRaeume.Lade(_plan);

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

            // R3-D: effektive Tage (für freie Räume) und Raumsperren – gespeicherte und die gerade eingetragenen.
            var tage = new Dictionary<DateTime, Tagesplan>();
            var planJetzt = _plan;
            Tagesplan TagFuer(DateTime d)
            {
                if (!tage.TryGetValue(d.Date, out var t))
                    tage[d.Date] = t = TagesplanBerechnung.Berechne(d.Date, planJetzt, _daten.Kalender, _daten.Vertretungen);
                return t;
            }
            var sperren = _daten.Abwesenheiten.Concat(_letzteAbwesenheiten).ToList();
            bool raumSperre = subj == AbwesenheitsSubjekt.Raum;
            // Vorbelegung bei Raumsperre: je Tag/Stunde jeden freien Raum nur einmal (Team-Teaching teilt ihn).
            var vergeben = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // Tag|Std|Raum → Gruppe
            var gruppenRaum = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Tag|Std|Gruppe → Raum
            int ohneFreienRaum = 0;

            // Duplikate entfernen (z. B. gekoppelte Kurse über mehrere gewählte Klassen).
            var gesehen = new HashSet<string>();
            int schonGebucht = 0;
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

                var zeile = new InstanzZeile
                {
                    Inst = inst,
                    DatumText = inst.Datum.ToString("ddd dd.MM.yyyy"),
                    Stunde = inst.Stunde,
                    KlassenText = string.Join(", ", inst.Klassen),
                    Fach = inst.Fach,
                    OrigLehrer = inst.Lehrer,
                    RaumText = inst.Raum ?? "",
                    RaumOptionen = RaumOptionenFuer(inst, TagFuer(inst.Datum), sperren),
                    Art = Vertretungsart.Vertretung,
                    Vorschlaege = vorschlaege,
                    GewaehlterVertreter = vorschlaege.FirstOrDefault()?.Lehrer,
                    Bemerkung = ""
                };

                // R3-D: Raumsperre → Raumvertretung mit dem ersten freien, noch nicht vergebenen Raum.
                if (raumSperre && FindeBuchung(inst) == null)
                {
                    string tagStd = $"{inst.Datum:yyyyMMdd}|{inst.Stunde}";
                    string gruppe = $"{inst.UNr}|{inst.Raum}";
                    if (!gruppenRaum.TryGetValue(tagStd + "|" + gruppe, out var raum))
                    {
                        raum = zeile.RaumOptionen.Where(o => o.IstFreierRaum).Select(o => o.Name)
                                   .FirstOrDefault(r => !vergeben.ContainsKey(tagStd + "|" + r)) ?? "";
                        if (raum.Length > 0)
                        {
                            vergeben[tagStd + "|" + raum] = gruppe;
                            gruppenRaum[tagStd + "|" + gruppe] = raum;
                        }
                    }
                    zeile.Art = Vertretungsart.Raumvertretung;
                    zeile.NeuerRaum = raum;
                    if (raum.Length == 0) ohneFreienRaum++;
                }

                // V1: Ist die Stunde schon gebucht, die gespeicherte Art/Vertretung zeigen statt
                // „Vertretung“ + erstem Vorschlag (sonst überschrieb „Buchen“ die Buchung still).
                var bestehend = FindeBuchung(inst);
                if (bestehend != null)
                {
                    zeile.UebernimmBuchung(bestehend);
                    schonGebucht++;
                }
                _zeilen.Add(zeile);
            }

            SetzeStatus(_zeilen.Count == 0
                ? "Keine betroffenen Stunden (an Schultagen im Zeitraum) gefunden."
                : $"{_zeilen.Count} betroffene Stunde(n) für {namen.Count} Name(n)" +
                  (schonGebucht > 0 ? $", davon {schonGebucht} schon gebucht (gelb, wie gespeichert vorbelegt)" : "") +
                  (raumSperre ? ", Raumvertretung mit freiem Raum vorbelegt (Spalte „neuer Raum“)" +
                                (ohneFreienRaum > 0 ? $" – {ohneFreienRaum} ohne freien Raum" : "") : "") +
                  ". Art/Vertretung prüfen, dann 'Buchen'.");
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

            // R3-D: derselbe neue Raum für verschiedene Unterrichte in derselben Stunde?
            var doppelt = _zeilen
                .Where(z => z.RaumWaehlbar && !string.IsNullOrWhiteSpace(z.NeuerRaum))
                .GroupBy(z => $"{z.Inst.Datum:yyyyMMdd}|{z.Inst.Stunde}|{z.NeuerRaum!.Trim().ToUpperInvariant()}")
                .Where(g => g.Select(z => $"{z.Inst.UNr}|{z.Inst.Raum}").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .ToList();
            // R3-D-Nachtrag: bewusst gewählte belegte bzw. gesperrte Räume (nur Zeilen, die wirklich gebucht würden).
            var belegtGewaehlt = _zeilen
                .Where(z => !(z.Bestehend != null && z.Unveraendert()) && z.GewaehlteRaumOption?.Belegt != null)
                .ToList();
            if (doppelt.Count > 0 || belegtGewaehlt.Count > 0)
            {
                var teileFrage = new List<string>();
                if (doppelt.Count > 0)
                    teileFrage.Add("Derselbe neue Raum ist mehrfach vergeben:\n" + string.Join("\n", doppelt.Take(8).Select(g =>
                        $"• {g.First().DatumText}, {g.First().Stunde}. Std: {g.First().NeuerRaum} für " +
                        string.Join(" und ", g.Select(z => $"{z.KlassenText} {z.Fach}")))));
                if (belegtGewaehlt.Count > 0)
                    teileFrage.Add("Gewählte Räume sind in dieser Stunde nicht frei:\n" + string.Join("\n", belegtGewaehlt.Take(8).Select(z =>
                        $"• {z.DatumText}, {z.Stunde}. Std, {z.KlassenText} {z.Fach}: {z.NeuerRaum} ({z.GewaehlteRaumOption!.Belegt})"))
                        + (belegtGewaehlt.Count > 8 ? $"\n… und {belegtGewaehlt.Count - 8} weitere" : ""));
                string titel = doppelt.Count > 0 ? "Raum doppelt" : "Raum belegt";
                if (MessageBox.Show(string.Join("\n\n", teileFrage) +
                        "\n\nTrotzdem buchen? (Ja = die Stunden kommen in diese Räume, auch doppelt belegt.)",
                        titel, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    SetzeStatus(doppelt.Count > 0 ? "Nichts gebucht (Raum doppelt vergeben)." : "Nichts gebucht (Raum belegt).");
                    return;
                }
            }

            // V1: Was geschieht mit schon gebuchten Stunden? Unverändert = bleibt, wie es ist;
            // geändert = erst nach Rückfrage; Verlegungen (zwei verknüpfte Einträge) nur im Wocheneditor.
            var geaenderte = _zeilen.Where(z => z.Bestehend != null && !z.IstVerlegung && !z.Unveraendert()).ToList();
            bool aenderungenBuchen = true;
            if (geaenderte.Count > 0)
            {
                var liste = string.Join("\n", geaenderte.Take(12).Select(z =>
                    $"• {z.DatumText}, {z.Stunde}. Std, {z.KlassenText} {z.Fach}: {z.GebuchtText} → {z.NeuText()}"));
                if (geaenderte.Count > 12) liste += $"\n… und {geaenderte.Count - 12} weitere";
                var antwort = MessageBox.Show(
                    $"{geaenderte.Count} schon gebuchte Stunde(n) würden geändert:\n\n{liste}\n\n" +
                    "Ja = ändern · Nein = nur die neuen Stunden buchen · Abbrechen = nichts buchen",
                    "Bestehende Buchungen ändern?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (antwort == MessageBoxResult.Cancel) { SetzeStatus("Nichts gebucht."); return; }
                aenderungenBuchen = antwort == MessageBoxResult.Yes;
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

            int gebucht = 0, uebersprungen = 0, geaendert = 0, unveraendert = 0, verlegungen = 0, behalten = 0, ohneRaum = 0;
            var neuGebucht = new List<(InstanzZeile Zeile, VertretungsEintrag Eintrag)>();
            foreach (var z in _zeilen)
            {
                bool brauchtVertreter = InstanzZeile.ArtBrauchtVertreter(z.Art);
                string? vertreter = string.IsNullOrWhiteSpace(z.GewaehlterVertreter)
                    ? null : z.GewaehlterVertreter!.Trim();

                if (z.Bestehend != null)
                {
                    if (z.IstVerlegung) { if (!z.Unveraendert()) verlegungen++; else unveraendert++; continue; }
                    if (z.Unveraendert()) { unveraendert++; continue; }
                    if (!aenderungenBuchen) { behalten++; continue; }
                }

                if (brauchtVertreter && vertreter == null)
                {
                    uebersprungen++;
                    continue;
                }

                // R3-D: neuer Raum (bei jeder Art außer Entfall/Verlegung); Raumvertretung braucht einen.
                string? neuerRaum = z.RaumWaehlbar && !string.IsNullOrWhiteSpace(z.NeuerRaum) ? z.NeuerRaum!.Trim() : null;
                if (z.Art == Vertretungsart.Raumvertretung && neuerRaum == null)
                {
                    ohneRaum++;
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
                    OriginalRaum = z.Inst.Raum,
                    VertretungsRaum = neuerRaum,
                    ZaehlwertSnapshot = zaehlwert
                };

                _daten.Vertretungen.RemoveAll(v =>
                    v.Datum == eintrag.Datum && v.Stunde == eintrag.Stunde &&
                    v.UNr == eintrag.UNr &&
                    string.Equals(v.OriginalLehrer, eintrag.OriginalLehrer, StringComparison.OrdinalIgnoreCase));

                _daten.Vertretungen.Add(eintrag);
                if (z.Bestehend != null) geaendert++;
                gebucht++;
                neuGebucht.Add((z, eintrag));
            }

            try { VertretungsStore.Speichern(_daten, _vertretungsPfad); }
            catch (Exception ex)
            {
                MessageBox.Show("Fehler beim Speichern:\n\n" + ex.Message +
                    "\n\n(Häufigste Ursache: Datei ist parallel in Excel geöffnet.)",
                    "Vertretung", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // V1: Gespeicherte Zeilen gelten ab jetzt als gebucht (kein zweites Nachfragen).
            foreach (var (zeile, eintrag) in neuGebucht) zeile.UebernimmBuchung(eintrag);

            AktualisiereZaehler();
            AktualisiereUebersicht();
            var teile = new List<string> { $"{gebucht} Stunde(n) gebucht und gespeichert" + (geaendert > 0 ? $" (davon {geaendert} geändert)" : "") };
            if (unveraendert > 0) teile.Add($"{unveraendert} unverändert");
            if (behalten > 0) teile.Add($"{behalten} Änderung(en) nicht übernommen");
            if (uebersprungen > 0) teile.Add($"{uebersprungen} übersprungen (keine Vertretungskraft gewählt)");
            if (ohneRaum > 0) teile.Add($"{ohneRaum} Raumvertretung(en) übersprungen (kein neuer Raum gewählt)");
            if (verlegungen > 0) teile.Add($"{verlegungen} Verlegung(en) nicht geändert – Verlegungen im Wocheneditor ändern");
            string msg = string.Join(", ", teile) + ".";
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

            var tage = new Dictionary<DateTime, Tagesplan>();
            foreach (var v in quelle.OrderBy(v => v.Datum).ThenBy(v => v.Stunde)
                                     .ThenBy(v => string.Join(",", v.Klassen)))
            {
                _uebersicht.Add(new UebersichtZeile
                {
                    DatumText = v.Datum.ToString("ddd dd.MM.yyyy"),
                    Stunde = v.Stunde,
                    Klasse = string.Join(", ", v.Klassen),
                    Fach = v.Fach,
                    Raum = RaumTextFuer(v, tage),
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
                var tage = new Dictionary<DateTime, Tagesplan>();
                VertretungsplanPdfExporter.Erzeuge(_daten.Vertretungen, tag, mitGrund, dlg.FileName, _daten.Schuljahr,
                    v => RaumTextFuer(v, tage));
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

        /// <summary>V1: gespeicherte Buchung derselben Stunde (Schlüssel wie beim Buchen: Datum, Stunde, UNr, Lehrkraft).</summary>
        private VertretungsEintrag? FindeBuchung(StundenInstanz inst)
            => _daten.Vertretungen.FirstOrDefault(v =>
                v.Datum.Date == inst.Datum.Date && v.Stunde == inst.Stunde && v.UNr == inst.UNr &&
                string.Equals(v.OriginalLehrer, inst.Lehrer, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// R3-D: Auswahl „neuer Raum“ einer Zeile – „(wie bisher)“ und die in dieser Stunde freien Räume (ohne
        /// gesperrte; der Raum laut Plan fehlt, er ist „wie bisher“). R3-D-Nachtrag: danach „– weitere Räume –“
        /// (Fachräume anderer Fächer, Unbekannte) und „– belegte Räume –“ (bewusst wählbar, Rückfrage beim Buchen).
        /// </summary>
        private List<RaumOption> RaumOptionenFuer(StundenInstanz inst, Tagesplan tp, List<Abwesenheit> sperren)
        {
            var l = new List<RaumOption>
            {
                new() { Name = "", Anzeige = "(wie bisher" + (string.IsNullOrWhiteSpace(inst.Raum) ? "" : ": " + inst.Raum) + ")" }
            };
            if (_plan?.Raeume == null || !tp.IstSchultag) return l;
            var zelle = tp.Zellen.FirstOrDefault(c =>
                            c.Stunde == inst.Stunde && c.UNr == inst.UNr &&
                            string.Equals(c.OriginalLehrer, inst.Lehrer, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.Fach, inst.Fach, StringComparison.OrdinalIgnoreCase))
                        ?? tp.Zellen.FirstOrDefault(c =>
                            c.Stunde == inst.Stunde && c.UNr == inst.UNr &&
                            string.Equals(c.OriginalLehrer, inst.Lehrer, StringComparison.OrdinalIgnoreCase));
            if (zelle == null) return l;
            var gesperrt = PlanRaeume.Gesperrt(sperren, inst.Datum, inst.Stunde);
            var alle = _plan.Raeume.Vorschlaege(_plan, tp, zelle, gesperrt, PlanRaeume.Zerlege(inst.Raum));
            foreach (var v in alle.Where(x => x.IstFrei && !x.Weitere))
                l.Add(new RaumOption { Name = v.Name, Anzeige = v.Anzeige });
            var weitere = alle.Where(x => x.IstFrei && x.Weitere).ToList();
            if (weitere.Count > 0) l.Add(RaumOption.Trenner("– weitere Räume –"));
            foreach (var v in weitere)
                l.Add(new RaumOption { Name = v.Name, Anzeige = v.Anzeige });
            var belegte = alle.Where(x => !x.IstFrei).ToList();
            if (belegte.Count > 0) l.Add(RaumOption.Trenner("– belegte Räume –"));
            foreach (var v in belegte)
                l.Add(new RaumOption { Name = v.Name, Anzeige = v.Anzeige, Belegt = v.Belegt });
            return l;
        }

        /// <summary>
        /// R3-D: Raumtext eines Eintrags für Tagesübersicht und PDF – aus dem effektiven Tag („alt → neu“ bei
        /// einer Raumänderung, leer bei Entfall/verlegt weg); ohne passende Stunde aus dem Eintrag selbst.
        /// </summary>
        private string RaumTextFuer(VertretungsEintrag v, Dictionary<DateTime, Tagesplan> tage)
        {
            if (_plan == null) return VertretungsplanPdfExporter.RaumText(v);
            if (!tage.TryGetValue(v.Datum.Date, out var tp))
                tage[v.Datum.Date] = tp = TagesplanBerechnung.Berechne(v.Datum.Date, _plan, _daten.Kalender, _daten.Vertretungen);
            var c = tp.Zellen.FirstOrDefault(x => ReferenceEquals(x.Eintrag, v));
            if (c == null) return VertretungsplanPdfExporter.RaumText(v);
            if (c.IstFrei) return "";
            if (c.RaumGeaendert) return (string.IsNullOrWhiteSpace(c.GrundRaum) ? "–" : c.GrundRaum) + " → " + c.Raum;
            return c.Raum ?? "";
        }

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

        /// <summary>R3-D: Eintrag der Auswahl „neuer Raum“ (Name leer = wie bisher).</summary>
        public class RaumOption
        {
            /// <summary>Kennung der Trennzeilen (kein Raumname beginnt so).</summary>
            public const string TrennerPraefix = "\u0001";

            public string Name { get; set; } = "";
            public string Anzeige { get; set; } = "";
            /// <summary>R3-D-Nachtrag: null = frei; sonst „belegt: 07B E L21“ bzw. „gesperrt“.</summary>
            public string? Belegt { get; set; }
            /// <summary>R3-D-Nachtrag: Trennzeile „– weitere Räume –“ bzw. „– belegte Räume –“ (nicht wählbar).</summary>
            public bool IstTrenner { get; set; }
            /// <summary>Ein freier Raum (für Vorbelegungen) – nicht „wie bisher“, kein Trenner, nicht belegt.</summary>
            public bool IstFreierRaum => !IstTrenner && Belegt == null && Name.Length > 0;

            public static RaumOption Trenner(string text)
                => new() { Name = TrennerPraefix + text, Anzeige = text, IstTrenner = true };
        }

        public class InstanzZeile : INotifyPropertyChanged
        {
            public StundenInstanz Inst { get; set; } = new();
            public string DatumText { get; set; } = "";
            public int Stunde { get; set; }
            public string KlassenText { get; set; } = "";
            public string Fach { get; set; } = "";
            public string OrigLehrer { get; set; } = "";
            /// <summary>R3-D: Raum laut Plan (Raumplan, sonst Fachraum vor Stammraum).</summary>
            public string RaumText { get; set; } = "";
            /// <summary>R3-D: „(wie bisher)“ + freie Räume dieser Stunde.</summary>
            public List<RaumOption> RaumOptionen { get; set; } = new();
            public List<VorschlagAnzeige> Vorschlaege { get; set; } = new();
            public string? GewaehlterVertreter { get; set; }
            public string Bemerkung { get; set; } = "";

            // ---- V1: schon gebuchte Stunde ----
            private VertretungsEintrag? _bestehend;
            /// <summary>Gespeicherte Buchung dieser Stunde (null = noch nicht gebucht).</summary>
            public VertretungsEintrag? Bestehend => _bestehend;
            public bool IstGebucht => _bestehend != null;
            /// <summary>Teil einer Verlegung (zwei verknüpfte Einträge) – nur im Wocheneditor änderbar.</summary>
            public bool IstVerlegung => _bestehend != null && !string.IsNullOrEmpty(_bestehend.VerlegungsId);
            /// <summary>Spalte „gebucht“: gespeicherte Art und Vertretung.</summary>
            public string GebuchtText => _bestehend == null ? ""
                : IstVerlegung ? "Verlegung (im Wocheneditor ändern)"
                : BuchungText(_bestehend.Art, _bestehend.VertretungsLehrer, _bestehend.VertretungsRaum);

            /// <summary>Zeile wie gespeichert vorbelegen (Art, Vertretung, Bemerkung).</summary>
            public void UebernimmBuchung(VertretungsEintrag b)
            {
                _bestehend = b;
                Art = b.Art;
                if (!string.IsNullOrWhiteSpace(b.VertretungsLehrer))
                {
                    string v = b.VertretungsLehrer!.Trim();
                    if (!Vorschlaege.Any(x => string.Equals(x.Lehrer, v, StringComparison.OrdinalIgnoreCase)))
                        Vorschlaege = new List<VorschlagAnzeige> { new() { Lehrer = v, Anzeige = $"{v}  (gebucht)" } }
                            .Concat(Vorschlaege).ToList();
                    GewaehlterVertreter = Vorschlaege.First(x => string.Equals(x.Lehrer, v, StringComparison.OrdinalIgnoreCase)).Lehrer;
                }
                Bemerkung = b.Bemerkung ?? "";
                // R3-D: gebuchten neuen Raum vorbelegen (auch wenn er inzwischen nicht mehr frei ist).
                string raum = (b.VertretungsRaum ?? "").Trim();
                if (raum.Length > 0 && !RaumOptionen.Any(x => string.Equals(x.Name, raum, StringComparison.OrdinalIgnoreCase)))
                {
                    var l = new List<RaumOption>(RaumOptionen);
                    l.Insert(Math.Min(1, l.Count), new RaumOption { Name = raum, Anzeige = raum + "  (gebucht)" });
                    RaumOptionen = l;
                }
                _neuerRaum = raum.Length > 0
                    ? RaumOptionen.First(x => string.Equals(x.Name, raum, StringComparison.OrdinalIgnoreCase)).Name
                    : "";
                OnChanged(nameof(RaumOptionen));
                OnChanged(nameof(NeuerRaum));
                OnChanged(nameof(Vorschlaege));
                OnChanged(nameof(GewaehlterVertreter));
                OnChanged(nameof(Bemerkung));
                OnChanged(nameof(IstGebucht));
                OnChanged(nameof(GebuchtText));
            }

            /// <summary>Art, Vertretung (falls nötig) und Bemerkung wie gespeichert?</summary>
            public bool Unveraendert()
            {
                if (_bestehend == null) return false;
                if (Art != _bestehend.Art) return false;
                if (!string.Equals((Bemerkung ?? "").Trim(), (_bestehend.Bemerkung ?? "").Trim(), StringComparison.Ordinal))
                    return false;
                if (RaumWaehlbar && !string.Equals((NeuerRaum ?? "").Trim(), (_bestehend.VertretungsRaum ?? "").Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!BrauchtVertreter) return true;
                return string.Equals((GewaehlterVertreter ?? "").Trim(), (_bestehend.VertretungsLehrer ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>Neue Art/Vertretung für die Rückfrage.</summary>
            public string NeuText() => BuchungText(Art, BrauchtVertreter ? GewaehlterVertreter : null,
                                                   RaumWaehlbar ? NeuerRaum : null);

            private static string BuchungText(Vertretungsart art, string? vertreter, string? raum = null)
            {
                string t = string.IsNullOrWhiteSpace(vertreter) ? art.ToString() : $"{art} – {vertreter!.Trim()}";
                if (!string.IsNullOrWhiteSpace(raum))
                    t += (art == Vertretungsart.Raumvertretung ? " → " : ", Raum → ") + raum!.Trim();
                return t;
            }

            // ---- R3-D: neuer Raum ----
            private string? _neuerRaum = "";
            /// <summary>Gewählter neuer Raum („“ = wie bisher).</summary>
            public string? NeuerRaum
            {
                get => _neuerRaum;
                set
                {
                    // R3-D-Nachtrag: Trennzeilen sind nicht wählbar (Absicherung, falls die Tastatur sie trifft).
                    if (value != null && value.StartsWith(RaumOption.TrennerPraefix, StringComparison.Ordinal))
                    { OnChanged(nameof(NeuerRaum)); return; }
                    if (string.Equals(_neuerRaum, value, StringComparison.Ordinal)) return;
                    _neuerRaum = value;
                    OnChanged(nameof(NeuerRaum));
                }
            }

            /// <summary>R3-D-Nachtrag: Option des gewählten neuen Raums (null bei „wie bisher“ bzw. nicht wählbar).</summary>
            public RaumOption? GewaehlteRaumOption
                => !RaumWaehlbar || string.IsNullOrWhiteSpace(_neuerRaum) ? null
                   : RaumOptionen.FirstOrDefault(x => !x.IstTrenner &&
                         string.Equals(x.Name, _neuerRaum!.Trim(), StringComparison.OrdinalIgnoreCase));

            /// <summary>Raum wählbar bei jeder Art außer Entfall und Verlegung.</summary>
            public bool RaumWaehlbar => _art != Vertretungsart.Entfall && _art != Vertretungsart.Verlegung;

            // Einmal erzeugt (F1): jede Zeile und jedes Lesen bekommt dieselbe Liste.
            private static readonly Array ArtenListe = Enum.GetValues(typeof(Vertretungsart));
            public Array AlleArten => ArtenListe;

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
                    OnChanged(nameof(RaumWaehlbar));
                    // R3-D: Raumvertretung ohne Raum → ersten freien Raum vorschlagen.
                    if (_art == Vertretungsart.Raumvertretung && string.IsNullOrWhiteSpace(_neuerRaum)
                        && RaumOptionen.FirstOrDefault(o => o.IstFreierRaum) is RaumOption frei)
                        NeuerRaum = frei.Name;
                }
            }

            public bool BrauchtVertreter => ArtBrauchtVertreter(_art);

            public static bool ArtBrauchtVertreter(Vertretungsart a) => a switch
            {
                Vertretungsart.Entfall => false,
                Vertretungsart.Verlegung => false,
                Vertretungsart.Raumvertretung => false,   // R3-D: nur der Raum ändert sich, die Lehrkraft bleibt
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
            public string Raum { get; set; } = "";
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
