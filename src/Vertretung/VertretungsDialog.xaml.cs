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
    ///
    /// V4b: Gründe je Abwesenheit (288), Spalte „Ausfall“ für die ausfallende Lehrkraft vertretener Stunden (287, 292),
    /// Abwesend „Klasse“: Kurse mit anwesenden Klassen ohne Buchung (290), Abwesend „Lehrer“ an einem freien Tag einer
    /// Klasse: Entfall – Lehrkraft fehlt (295), Zähler mit „Vertreten 0/−1“ und abgeleiteten Entfällen (276, 293).
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
            SetzeGruende(AbwesenheitsSubjekt.Lehrer);   // V4b (288): je Abwesenheit, siehe CbSubjekt_SelectionChanged
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
            // V4b (288): bei „Klasse“ nur Gründe für Klassen (Vorgabe „Sonstiges“), sonst wie bisher (Vorgabe „Krankheit“).
            if (CbSubjekt.SelectedItem is AbwesenheitsSubjekt sg) SetzeGruende(sg);
            if (!_bereit || _plan == null) return;
            if (CbSubjekt.SelectedItem is AbwesenheitsSubjekt subj)
                FuelleNamen(subj);
            MeldeAuswahl();
        }

        /// <summary>V4b (288): Liste und Vorgabe des Feldes „Grund“ je Abwesenheit.</summary>
        private void SetzeGruende(AbwesenheitsSubjekt subj)
        {
            bool klasse = subj == AbwesenheitsSubjekt.Klasse;
            CbGrund.ItemsSource = klasse ? Zaehlung.KlassenGruende : Zaehlung.LehrerGruende;
            CbGrund.SelectedItem = klasse ? Zaehlung.KlassenGrundVorgabe : VertretungsGrund.Krankheit;
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
            _letzteGrund = CbGrund.SelectedItem is VertretungsGrund lg ? lg : VertretungsGrund.Sonstiges;
            _letzteAbwesenheiten.Clear();
            // V4 (275): Anlass eines Entfalls = was fehlt; bei „Klasse“ sind die Lehrkräfte freigesetzt → Entfall (−1).
            var anlassZeile = Zaehlung.AnlassAus(subj);
            bool klasseFehlt = subj == AbwesenheitsSubjekt.Klasse;

            // 283/276: Saldo mit den abgeleiteten Entfällen (freie Zeiten einzelner Klassen)
            var zaehler = VertretungsZaehler.SaldoProLehrer(KalenderEntfaelle.MitAbgeleiteten(_daten, _plan));
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
            int entfallVorbelegt = 0, teilweiseAnwesend = 0;   // V4: Abwesenheit „Klasse“
            int freiVorbelegt = 0;                             // V4b (295): Abwesenheit „Lehrer“ an einem freien Tag der Klasse

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

                // V4 (275): Entfall (−1) nur, wenn ALLE Klassen der Stunde fehlen; ein Kurs mit weiteren, anwesenden
                // Klassen bleibt wie bisher mit Vertretung vorbelegt.
                bool alleKlassenFehlen = klasseFehlt && inst.Klassen.Count > 0
                    && inst.Klassen.All(k => namen.Contains(k, StringComparer.OrdinalIgnoreCase));
                // V4b (290): Kurse mit weiteren, anwesenden Klassen finden mit der eigenen Lehrkraft statt → ohne Buchung.
                bool kursFindetStatt = klasseFehlt && !alleKlassenFehlen;
                if (klasseFehlt && FindeBuchung(inst) == null)
                {
                    if (alleKlassenFehlen) entfallVorbelegt++;
                    else teilweiseAnwesend++;
                }

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
                    Art = alleKlassenFehlen ? Vertretungsart.Entfall : Vertretungsart.Vertretung,
                    Vorschlaege = vorschlaege,
                    GewaehlterVertreter = kursFindetStatt ? null : vorschlaege.FirstOrDefault()?.Lehrer,
                    KursFindetStatt = kursFindetStatt,
                    // V4b (290): Ausfall „fehlt (0)“; bei Abwesenheit „Klasse“ ist die Lehrkraft nicht verhindert (−1).
                    AusfallWahl = klasseFehlt ? InstanzZeile.AusfallNichtVerhindert : InstanzZeile.AusfallFehlt,
                    Bemerkung = "",
                    Anlass = anlassZeile,
                    StandardRechner = StandardWertFuer,
                    Hinweis = SetzeStatus
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
                // V4b (295): Fehlt die Lehrkraft, während die Klassen der Stunde laut Kalender frei sind, ist es ein Entfall
                // „Lehrkraft fehlt (0)“ – sie war nicht freigesetzt. Ein Sondergrund an der freien Stunde wird dabei ersetzt.
                if (subj == AbwesenheitsSubjekt.Lehrer
                    && (bestehend == null || bestehend.Anlass == EntfallAnlass.FreieZeit)
                    && ZelleVon(inst, TagFuer(inst.Datum))?.FreiLautKalender != null)
                {
                    zeile.Art = Vertretungsart.Entfall;
                    bestehend = null;
                    freiVorbelegt++;
                }
                if (bestehend != null)
                {
                    zeile.UebernimmBuchung(bestehend);
                    schonGebucht++;
                }
                zeile.AktualisiereStandard();   // V4: „Standard (…)“ in der Spalte „Zählung“
                _zeilen.Add(zeile);
            }

            SetzeStatus(_zeilen.Count == 0
                ? "Keine betroffenen Stunden (an Schultagen im Zeitraum) gefunden."
                : $"{_zeilen.Count} betroffene Stunde(n) für {namen.Count} Name(n)" +
                  (schonGebucht > 0 ? $", davon {schonGebucht} schon gebucht (gelb, wie gespeichert vorbelegt)" : "") +
                  (raumSperre ? ", Raumvertretung mit freiem Raum vorbelegt (Spalte „neuer Raum“)" +
                                (ohneFreienRaum > 0 ? $" – {ohneFreienRaum} ohne freien Raum" : "") : "") +
                  (entfallVorbelegt > 0 ? $", {entfallVorbelegt} mit Entfall (−1, Klasse/Kurs fehlt) vorbelegt" : "") +
                  (teilweiseAnwesend > 0 ? $", {teilweiseAnwesend} ohne Buchung (weitere Klassen anwesend – der Kurs findet statt; " +
                                           "für eine Vertretung ist „Ausfall: nicht verhindert (−1)“ vorbelegt)" : "") +
                  (freiVorbelegt > 0 ? $", {freiVorbelegt} mit freier Klasse laut Kalender: Entfall (Lehrkraft fehlt, 0)" : "") +
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
            foreach (var z in markiert)
            {
                z.Art = art;
                z.ZaehlWahl = InstanzZeile.StandardSchluessel;   // V4 (278): Zählung dann „Standard“
                z.AktualisiereStandard();
            }
            SetzeStatus($"{markiert.Count} Zeile(n) auf '{art}' gesetzt (Zählung: Standard).");
        }

        // =====================================================
        //  Buchen
        // =====================================================

        /// <summary>
        /// V4 (274/275): Standard-Zählwert einer Zeile – Art aus dem Blatt „Arten“ (0, wenn die gewählte Lehrkraft dafür
        /// eine UV-Stunde aufgibt), Entfall nach Anlass, ohne haltende Lehrkraft 0.
        /// </summary>
        private int StandardWertFuer(InstanzZeile z)
            => Zaehlung.StandardWert(z.Art, z.Art == Vertretungsart.Entfall ? z.Anlass : null,
                InstanzZeile.ArtBrauchtVertreter(z.Art) ? z.GewaehlterVertreter : null,
                z.Inst.Datum, z.Inst.Stunde, _daten, _plan);

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

            // V4 (277/278): eigener Zählwert nur mit Kommentar (eine Zeile, bis 200 Zeichen) – sonst nichts buchen.
            var ohneKommentar = _zeilen.Where(z => z.EigenerWert != null && Zaehlung.KommentarFehler(z.Zaehlkommentar) != null).ToList();
            if (ohneKommentar.Count > 0)
            {
                var erste = ohneKommentar[0];
                SetzeStatus($"Nichts gebucht: {ohneKommentar.Count} Zeile(n) mit eigenem Zählwert ohne gültigen Kommentar " +
                            $"(z. B. {erste.DatumText}, {erste.Stunde}. Std: {Zaehlung.KommentarFehler(erste.Zaehlkommentar)})");
                DgInstanzen.SelectedItem = erste;
                DgInstanzen.ScrollIntoView(erste);
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

            int gebucht = 0, uebersprungen = 0, geaendert = 0, unveraendert = 0, verlegungen = 0, behalten = 0, ohneRaum = 0, sondergruende = 0;
            int ohneBuchung = 0, nichtVerhindert = 0, ausfallWeg = 0;   // V4b (290, 292, 296)
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
                    if (z.KursFindetStatt) ohneBuchung++;   // V4b (290): Kurs findet mit der eigenen Lehrkraft statt
                    else uebersprungen++;
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
                // V4 (274/275): Entfall nach Anlass (Lehrkraft fehlt 0, Klasse/Kurs fehlt −1, Raum gesperrt 0).
                EntfallAnlass? anlass = z.Art == Vertretungsart.Entfall ? z.Anlass : null;
                int zaehlwert = Zaehlung.StandardWert(z.Art, anlass, brauchtVertreter ? vertreter : null,
                    z.Inst.Datum, z.Inst.Stunde, _daten, _plan);

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
                    ZaehlwertSnapshot = zaehlwert,
                    Anlass = anlass
                };
                // V4 (277): eigener Zählwert = Sondergrund mit Kommentar.
                if (z.EigenerWert is int eigen)
                {
                    Zaehlung.SetzeSondergrund(eintrag, eigen, z.Zaehlkommentar);
                    sondergruende++;
                }
                // V4b (287, 292, 296): ausfallende Lehrkraft – Anlass aus der Spalte „Ausfall“; ein vorhandener Sondergrund
                // dafür (Wocheneditor/Dauervertretung) bleibt, solange weiter eine andere Lehrkraft hält.
                if (Zaehlung.UebernimmAusfall(z.Bestehend, eintrag, z.AusfallAnlassGewaehlt)) ausfallWeg++;
                if (Zaehlung.HatAusfall(eintrag))
                {
                    Zaehlung.SetzeAusfall(eintrag, z.AusfallAnlassGewaehlt);
                    if (!eintrag.SondergrundAusfall && eintrag.ZaehlwertAusfall < 0) nichtVerhindert++;
                }

                _daten.Vertretungen.RemoveAll(v =>
                    v.Datum == eintrag.Datum && v.Stunde == eintrag.Stunde &&
                    v.UNr == eintrag.UNr &&
                    string.Equals(v.OriginalLehrer, eintrag.OriginalLehrer, StringComparison.OrdinalIgnoreCase));

                _daten.Vertretungen.Add(eintrag);
                if (z.Bestehend != null) geaendert++;
                gebucht++;
                neuGebucht.Add((z, eintrag));
            }

            try { VertretungsStore.Speichern(_daten, _vertretungsPfad, _plan); }   // V4b: Plan für das Blatt „Entfälle“
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
            if (sondergruende > 0) teile.Add($"{sondergruende} mit eigenem Zählwert (Sondergrund)");
            if (nichtVerhindert > 0) teile.Add($"{nichtVerhindert} mit Ausfall „nicht verhindert“ (−1)");
            if (ausfallWeg > 0) teile.Add($"bei {ausfallWeg} entfallen Anlass/Sondergrund der ausfallenden Lehrkraft (hält niemand anderes mehr)");
            if (ohneBuchung > 0) teile.Add($"{ohneBuchung} ohne Buchung (Kurs mit anwesenden Klassen findet statt)");
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
                    // V4b (276): Sondergrund an einer freien Stunde laut Kalender – Bezeichnung der freien Zeit
                    GrundText = v.Anlass == EntfallAnlass.FreieZeit ? $"{v.Bemerkung} (Kalender)".Trim() : Zaehlung.GrundText(v.Grund),
                    ZaehlText = v.Art == Vertretungsart.Verlegung ? "" : Zaehlung.KurzText(v),
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
                // V4b (276, 281): mit den abgeleiteten Entfällen (freie Zeiten einzelner Klassen)
                ZaehlerExporter.Erzeuge(KalenderEntfaelle.MitAbgeleiteten(_daten, _plan), dlg.FileName, _daten.Schuljahr);
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
            var stand = VertretungsZaehler.ProLehrer(KalenderEntfaelle.MitAbgeleiteten(_daten, _plan));   // V4b (276)
            foreach (var s in stand.Values.OrderByDescending(s => s.Saldo)
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
                    GehaltenPunkte = s.GehalteneVertretungen,
                    Entfall0 = s.Entfall0,
                    EntfallMinus1 = s.EntfallMinus1,
                    Vertreten0 = s.Vertreten0,
                    VertretenMinus1 = s.VertretenMinus1,
                    Sondergruende = s.Sondergruende,
                    SondergrundPunkte = s.SondergrundPunkte,
                    EntfallGesamt = s.EigeneAusfaelle,
                    Saldo = s.Saldo
                });
            }
        }

        // =====================================================
        //  Hilfsfunktionen
        // =====================================================

        /// <summary>V4b (295): Zelle des effektiven Tages zu einer Stunde (Schlüssel wie beim Buchen).</summary>
        private static TagesZelle? ZelleVon(StundenInstanz inst, Tagesplan tp)
            => tp.Zellen.FirstOrDefault(c => c.Stunde == inst.Stunde && c.UNr == inst.UNr &&
                                             string.Equals(c.OriginalLehrer, inst.Lehrer, StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(c.Fach, inst.Fach, StringComparison.OrdinalIgnoreCase))
               ?? tp.Zellen.FirstOrDefault(c => c.Stunde == inst.Stunde && c.UNr == inst.UNr &&
                                                string.Equals(c.OriginalLehrer, inst.Lehrer, StringComparison.OrdinalIgnoreCase));

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
            private string? _gewaehlterVertreter;
            public string? GewaehlterVertreter
            {
                get => _gewaehlterVertreter;
                set
                {
                    if (string.Equals(_gewaehlterVertreter, value, StringComparison.Ordinal)) return;
                    _gewaehlterVertreter = value;
                    OnChanged(nameof(GewaehlterVertreter));
                    OnChanged(nameof(AusfallMoeglich));
                    OnChanged(nameof(AusfallTipp));
                    AktualisiereStandard();   // V4: UV-Einsatz zählt 0 – der Standard hängt von der Lehrkraft ab
                }
            }
            public string Bemerkung { get; set; } = "";

            /// <summary>V4b (290): Abwesend „Klasse“, aber weitere Klassen der Stunde sind da – ohne Buchung vorbelegt.</summary>
            public bool KursFindetStatt { get; set; }

            // ---- V4b (287, 292): ausfallende Lehrkraft einer vertretenen Stunde ----
            public const string AusfallFehlt = "L";
            public const string AusfallNichtVerhindert = "N";

            /// <summary>Eintrag der Auswahl „Ausfall“.</summary>
            public sealed class AusfallOption
            {
                public string Schluessel { get; init; } = "";
                public string Anzeige { get; init; } = "";
            }

            // Einmal erzeugt (wie ArtenListe): jede Zeile bekommt dieselbe Liste.
            private static readonly List<AusfallOption> AusfallListe = new()
            {
                new AusfallOption { Schluessel = AusfallFehlt, Anzeige = "fehlt (0)" },
                new AusfallOption { Schluessel = AusfallNichtVerhindert, Anzeige = "nicht verhindert (−1)" }
            };
            public List<AusfallOption> AusfallOptionen => AusfallListe;

            private string _ausfallWahl = AusfallFehlt;
            /// <summary>„L“ = Lehrkraft fehlt (0), „N“ = nicht verhindert (−1).</summary>
            public string AusfallWahl
            {
                get => _ausfallWahl;
                set
                {
                    if (value == null) { OnChanged(nameof(AusfallWahl)); return; }   // Neuaufbau der Combo
                    if (string.Equals(_ausfallWahl, value, StringComparison.Ordinal)) return;
                    _ausfallWahl = value;
                    OnChanged(nameof(AusfallWahl));
                    OnChanged(nameof(AusfallTipp));
                }
            }

            public AusfallAnlass AusfallAnlassGewaehlt
                => _ausfallWahl == AusfallNichtVerhindert ? AusfallAnlass.NichtVerhindert : AusfallAnlass.Lehrkraft;

            /// <summary>Hält eine andere Lehrkraft die Stunde (dann zählt auch die ausfallende, 287)?</summary>
            public bool AusfallMoeglich
                => Zaehlung.HatAusfall(_art, ArtBrauchtVertreter(_art) ? _gewaehlterVertreter : null, OrigLehrer);

            /// <summary>Tooltip der Spalte „Ausfall“.</summary>
            public string AusfallTipp
                => $"Warum fehlt {OrigLehrer}? „fehlt (0)“ = krank bzw. dienstlich verhindert; „nicht verhindert (−1)“ = sie hätte " +
                   "eigentlich einen Entfall gehabt." +
                   (_bestehend != null && Zaehlung.HatAusfall(_bestehend) && _bestehend.SondergrundAusfall
                       ? $"\nGebucht: Sondergrund {Zaehlung.WertText(_bestehend.ZaehlwertAusfall)} – bleibt beim Buchen (ändern im Wocheneditor)."
                       : "\nEinen eigenen Wert dafür im Wocheneditor („Zählwert festlegen …“) bzw. in der Dauervertretung.");

            // ---- V4: Zählung je Zeile (275, 277, 278) ----
            /// <summary>Schlüssel der Auswahl „Standard (…)“.</summary>
            public const string StandardSchluessel = "S";

            /// <summary>Eintrag der Auswahl „Zählung“; nur der Text von „Standard (…)“ ändert sich.</summary>
            public sealed class ZaehlOption : INotifyPropertyChanged
            {
                public string Schluessel { get; init; } = "";
                private string _anzeige = "";
                public string Anzeige
                {
                    get => _anzeige;
                    set { if (_anzeige == value) return; _anzeige = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Anzeige))); }
                }
                public event PropertyChangedEventHandler? PropertyChanged;
            }

            private readonly List<ZaehlOption> _zaehlOptionen = new()
            {
                new ZaehlOption { Schluessel = StandardSchluessel, Anzeige = "Standard" },
                new ZaehlOption { Schluessel = "-1", Anzeige = "−1 (eigener Wert)" },
                new ZaehlOption { Schluessel = "0", Anzeige = "0 (eigener Wert)" },
                new ZaehlOption { Schluessel = "+1", Anzeige = "+1 (eigener Wert)" }
            };
            public List<ZaehlOption> ZaehlOptionen => _zaehlOptionen;

            /// <summary>Anlass, falls die Zeile als Entfall gebucht wird (aus der Abwesenheit bzw. der Buchung; null = unbekannt).</summary>
            public EntfallAnlass? Anlass { get; set; }
            /// <summary>Rechnet den Standard-Zählwert der Zeile (vom Fenster gesetzt).</summary>
            public Func<InstanzZeile, int>? StandardRechner { get; set; }
            /// <summary>Hinweis in der Statuszeile (vom Fenster gesetzt).</summary>
            public Action<string>? Hinweis { get; set; }

            private string _zaehlWahl = StandardSchluessel;
            /// <summary>„S“ = Standard, sonst „-1“ / „0“ / „+1“ (eigener Wert = Sondergrund).</summary>
            public string ZaehlWahl
            {
                get => _zaehlWahl;
                set
                {
                    // Ein Neuaufbau der Combo kann null schicken – dann die Auswahl behalten.
                    if (value == null) { OnChanged(nameof(ZaehlWahl)); return; }
                    if (string.Equals(_zaehlWahl, value, StringComparison.Ordinal)) return;
                    _zaehlWahl = value;
                    OnChanged(nameof(ZaehlWahl));
                    OnChanged(nameof(ZaehlTipp));
                }
            }

            private string _zaehlkommentar = "";
            /// <summary>Kommentar zum eigenen Zählwert (Pflicht bei Sondergrund).</summary>
            public string Zaehlkommentar
            {
                get => _zaehlkommentar;
                set { _zaehlkommentar = value ?? ""; OnChanged(nameof(Zaehlkommentar)); OnChanged(nameof(ZaehlTipp)); }
            }

            /// <summary>Eigener Wert (−1/0/+1) oder null = Standard.</summary>
            public int? EigenerWert => _zaehlWahl switch
            {
                "-1" => -1,
                "0" => 0,
                "+1" => 1,
                _ => (int?)null
            };

            private static string WahlFuer(int wert) => wert < 0 ? "-1" : wert > 0 ? "+1" : "0";

            /// <summary>Standard-Zählwert der Zeile in ihrem jetzigen Zustand.</summary>
            public int StandardWertJetzt => StandardRechner?.Invoke(this) ?? 0;

            /// <summary>Text „Standard (…)“ neu setzen (nach Änderung von Art bzw. Vertretung).</summary>
            public void AktualisiereStandard()
            {
                _zaehlOptionen[0].Anzeige = $"Standard ({Zaehlung.WertText(StandardWertJetzt)})";
                OnChanged(nameof(ZaehlTipp));
            }

            /// <summary>Tooltip der Spalte „Zählung“.</summary>
            public string ZaehlTipp
            {
                get
                {
                    string wer = BrauchtVertreter && !string.IsNullOrWhiteSpace(_gewaehlterVertreter)
                        ? _gewaehlterVertreter!.Trim() : OrigLehrer;
                    string t = Art == Vertretungsart.Entfall
                        ? $"Entfall – {Zaehlung.AnlassText(Anlass)}: Standard {Zaehlung.WertText(StandardWertJetzt)}"
                        : $"Standard {Zaehlung.WertText(StandardWertJetzt)}";
                    t += $" für {wer}.";
                    if (AusfallMoeglich)
                        t += $"\nAusfall {OrigLehrer}: {Zaehlung.AusfallKurz(AusfallAnlassGewaehlt)} " +
                             $"({Zaehlung.WertText(Zaehlung.AusfallWert(AusfallAnlassGewaehlt))}).";
                    if (EigenerWert is int ew)
                        t += $"\nEigener Wert {Zaehlung.WertText(ew)} (Sondergrund) – Kommentar Pflicht.";
                    return t + "\n" + Zaehlung.KommentarHinweis;
                }
            }

            // ---- V1: schon gebuchte Stunde ----
            private VertretungsEintrag? _bestehend;
            /// <summary>Gespeicherte Buchung dieser Stunde (null = noch nicht gebucht).</summary>
            public VertretungsEintrag? Bestehend => _bestehend;
            public bool IstGebucht => _bestehend != null;
            /// <summary>Teil einer Verlegung (zwei verknüpfte Einträge) – nur im Wocheneditor änderbar.</summary>
            public bool IstVerlegung => _bestehend != null && !string.IsNullOrEmpty(_bestehend.VerlegungsId);
            /// <summary>Spalte „gebucht“: gespeicherte Art und Vertretung.</summary>
            /// <summary>V4b (289): mit Zählung und Anlass, bei vertretenen Stunden beide Teile.</summary>
            public string GebuchtText => _bestehend == null ? ""
                : IstVerlegung ? "Verlegung (im Wocheneditor ändern)"
                : BuchungText(_bestehend.Art, _bestehend.VertretungsLehrer, _bestehend.VertretungsRaum)
                  + ": " + Zaehlung.KurzText(_bestehend);

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
                // V4: Zählung wie gespeichert (Anlass eines Entfalls, Sondergrund mit Kommentar).
                if (b.Art == Vertretungsart.Entfall) Anlass = b.Anlass;
                _zaehlWahl = b.Sondergrund ? WahlFuer(b.ZaehlwertSnapshot) : StandardSchluessel;
                _zaehlkommentar = b.Sondergrund ? b.Zaehlkommentar ?? "" : "";
                // V4b (292): Anlass der ausfallenden Lehrkraft wie gebucht (unbekannt → „fehlt (0)“).
                if (Zaehlung.HatAusfall(b))
                    _ausfallWahl = b.AnlassAusfall == AusfallAnlass.NichtVerhindert ? AusfallNichtVerhindert : AusfallFehlt;
                KursFindetStatt = false;
                OnChanged(nameof(AusfallWahl));
                OnChanged(nameof(AusfallMoeglich));
                OnChanged(nameof(AusfallTipp));
                OnChanged(nameof(RaumOptionen));
                OnChanged(nameof(NeuerRaum));
                OnChanged(nameof(Vorschlaege));
                OnChanged(nameof(GewaehlterVertreter));
                OnChanged(nameof(Bemerkung));
                OnChanged(nameof(IstGebucht));
                OnChanged(nameof(GebuchtText));
                OnChanged(nameof(ZaehlWahl));
                OnChanged(nameof(Zaehlkommentar));
                AktualisiereStandard();
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
                // V4 (278): Eine nur in der Zählung geänderte Stunde gilt als Änderung.
                if (EigenerWert is int ew)
                {
                    if (!_bestehend.Sondergrund || _bestehend.ZaehlwertSnapshot != ew
                        || !string.Equals(Zaehlung.Bereinige(Zaehlkommentar), (_bestehend.Zaehlkommentar ?? "").Trim(), StringComparison.Ordinal))
                        return false;
                }
                else if (_bestehend.Sondergrund) return false;
                // V4b (292): Eine nur im Ausfall-Anlass geänderte Stunde gilt als Änderung (unbekannt = „fehlt“).
                if (AusfallMoeglich && Zaehlung.HatAusfall(_bestehend)
                    && AusfallAnlassGewaehlt != (_bestehend.AnlassAusfall ?? AusfallAnlass.Lehrkraft))
                    return false;
                if (!BrauchtVertreter) return true;
                return string.Equals((GewaehlterVertreter ?? "").Trim(), (_bestehend.VertretungsLehrer ?? "").Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>Neue Art/Vertretung für die Rückfrage.</summary>
            public string NeuText() => BuchungText(Art, BrauchtVertreter ? GewaehlterVertreter : null,
                                                   RaumWaehlbar ? NeuerRaum : null)
                + (EigenerWert is int ew ? $" (Zählung {Zaehlung.WertText(ew)}, Sondergrund)"
                   : _bestehend?.Sondergrund == true ? " (Zählung wieder Standard)" : "")
                + (AusfallMoeglich && (_bestehend == null || !Zaehlung.HatAusfall(_bestehend)
                                       || AusfallAnlassGewaehlt != (_bestehend.AnlassAusfall ?? AusfallAnlass.Lehrkraft))
                    ? $" (Ausfall {OrigLehrer}: {Zaehlung.AusfallKurz(AusfallAnlassGewaehlt)})" : "");

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
                    OnChanged(nameof(AusfallMoeglich));   // V4b (292)
                    OnChanged(nameof(AusfallTipp));
                    // V4 (277): Ein eigener Zählwert galt der alten Art – zurück auf Standard, mit Hinweis.
                    if (_zaehlWahl != StandardSchluessel)
                    {
                        _zaehlWahl = StandardSchluessel;
                        OnChanged(nameof(ZaehlWahl));
                        Hinweis?.Invoke($"{DatumText}, {Stunde}. Std: Art geändert – der eigene Zählwert gilt nicht mehr (Zählung wieder Standard).");
                    }
                    AktualisiereStandard();
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
            public string ZaehlText { get; set; } = "";
            public string Bemerkung { get; set; } = "";
        }

        public class ZaehlerZeile
        {
            public string Lehrer { get; set; } = "";
            public int Vertretung { get; set; }
            public int Betreuung { get; set; }
            public int EVA { get; set; }
            public int Zusammenlegung { get; set; }
            // V4 (280)
            public int GehaltenPunkte { get; set; }
            public int Entfall0 { get; set; }
            public int EntfallMinus1 { get; set; }
            // V4b (293)
            public int Vertreten0 { get; set; }
            public int VertretenMinus1 { get; set; }
            public int Sondergruende { get; set; }
            public int SondergrundPunkte { get; set; }
            public int EntfallGesamt { get; set; }
            public int Saldo { get; set; }
        }
    }
}
