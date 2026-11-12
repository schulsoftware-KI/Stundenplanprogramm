#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// Phase 5.2a-2: Reiter „Zuordnung“ im Fenster „SchILD-Daten prüfen“. Zeigt die Zuordnung SchILD ↔ Plan
    /// (Oberstufenkurse, Sek-I-Kurse, Sek I ohne Kurs, Kürzel, Fächer, Plan-Kurse ohne SchILD) und erlaubt
    /// Festlegungen von Hand. Festlegungen gelten sofort im Prüflauf; in die Datei (schild_zuordnung.json bzw.
    /// für Pseudodaten schild_zuordnung_pseudo.json neben der Plandatei) kommen sie erst mit „Zuordnungen speichern“.
    /// Die Datei enthält keine Personendaten.
    /// </summary>
    public partial class SchildDialog
    {
        private SchildZuordnungDatei _zuordnung = new();
        private bool _zuordnungPseudo;
        private bool _zuordnungGeaendert;
        private bool _zuAnsichtSetzen;
        private bool _zuErsteAnzeige = true;

        private enum ZuAnsicht { Oberstufe, Sek1Kurs, OhneKurs, Kuerzel, Faecher, PlanOhne }

        /// <summary>Eintrag der Auswahlliste „Festlegen auf“.</summary>
        private sealed class ZuWahl
        {
            public string Text { get; init; } = "";
            /// <summary>Plan-Schlüssel bzw. Plan-Kürzel (nur bei Art 2).</summary>
            public string? Wert { get; init; }
            /// <summary>0 = automatisch (Festlegung entfernen), 1 = keine Zuordnung, 2 = Wert.</summary>
            public int Art { get; init; }
            public override string ToString() => Text;
        }

        private ZuAnsicht Ansicht => CbZuAnsicht.SelectedIndex switch
        {
            1 => ZuAnsicht.Sek1Kurs, 2 => ZuAnsicht.OhneKurs, 3 => ZuAnsicht.Kuerzel, 4 => ZuAnsicht.Faecher,
            5 => ZuAnsicht.PlanOhne, _ => ZuAnsicht.Oberstufe
        };

        // =====================================================
        //  Datei
        // =====================================================

        /// <summary>Lädt die Festlegungen passend zur Art der Daten (echt/pseudo).</summary>
        private void LadeZuordnung(bool pseudo)
        {
            _zuordnung = SchildZuordnungDatei.Laden(_planOrdner, pseudo, out string? fehler);
            _zuordnungPseudo = pseudo;
            _zuordnungGeaendert = false;
            if (fehler != null)
                MessageBox.Show(this, fehler, "Zuordnung", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private bool SpeichereZuordnung()
        {
            if (string.IsNullOrEmpty(_planOrdner) || !Directory.Exists(_planOrdner))
            {
                MessageBox.Show(this, "Der Ordner der Plandatei ist nicht bekannt – die Zuordnung kann nicht gespeichert werden.",
                    "Zuordnung", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            try
            {
                string pfad = _zuordnung.Speichern(_planOrdner, _zuordnungPseudo);
                _zuordnungGeaendert = false;
                _log?.Invoke($"SchILD: Zuordnung gespeichert ({Path.GetFileName(pfad)}, {_zuordnung.Anzahl} Festlegungen).");
                ZeigeZuStatus($"Gespeichert: {Path.GetFileName(pfad)}");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Speichern fehlgeschlagen:\n\n" + ex.Message, "Zuordnung",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        /// <returns>false = Vorgang abbrechen.</returns>
        private bool ZuordnungUngespeichertKlaeren(string vorDem)
        {
            if (!_zuordnungGeaendert) return true;
            var r = MessageBox.Show(this,
                $"Die Zuordnung hat Festlegungen, die noch nicht gespeichert sind.\n\nVor dem {vorDem} speichern?",
                "Zuordnung", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return SpeichereZuordnung();
            _zuordnungGeaendert = false;   // verwerfen
            return true;
        }

        // =====================================================
        //  Anzeige
        // =====================================================

        /// <summary>Nach jedem Prüflauf bzw. jeder Festlegung: Kopf, Hinweisband, Auswahl, Tabelle.</summary>
        private void ZeigeZuordnung()
        {
            var z = _ergebnis?.Zuordnung;
            int offen = z == null ? 0
                : z.Zeilen.Count(x => x.IstOffen) + z.Kuerzel.Count(x => x.IstOffen) + z.Faecher.Count(x => x.IstOffen)
                  + z.PlanOhne.Count(x => x.IstOffen);
            TxtZuordnungKopf.Text = z == null ? "Zuordnung" : offen > 0 ? $"Zuordnung ({offen} offen)" : "Zuordnung ✓";

            string? band = null;
            if (_export == null) band = null;
            else if (_planInfo == null) band = "Kein Plan verfügbar – die Zuordnung braucht den Grundplan der im Wocheneditor gewählten Plan-Spalte.";
            else if (_planInfo.Hinweis != null) band = "Zuordnung nicht möglich: " + _planInfo.Hinweis;
            else if (z == null) band = "Zuordnung entfällt, weil Kurse.dat nicht gelesen werden konnte (siehe „Probleme“).";
            BrdZuHinweis.Visibility = band == null ? Visibility.Collapsed : Visibility.Visible;
            TxtZuHinweis.Text = band ?? "";

            _zuAnsichtSetzen = true;
            int sel = CbZuAnsicht.SelectedIndex;
            CbZuAnsicht.Items.Clear();
            string T(string name, int n, int o) => o > 0 ? $"{name} ({n} · {o} offen)" : $"{name} ({n})";
            if (z == null)
                foreach (var n in new[] { "Oberstufenkurse", "Sek-I-Kurse", "Sek I ohne Kurs (Klasse + Fach + Fachlehrer)",
                                          "Lehrkräftekürzel", "Fächer", "Plan-Kurse ohne SchILD" })
                    CbZuAnsicht.Items.Add(n);
            else
            {
                CbZuAnsicht.Items.Add(T("Oberstufenkurse", z.Zeilen.Count(x => x.Art == SchildZuordnungArt.Oberstufe),
                                        z.AnzahlOffen(SchildZuordnungArt.Oberstufe)));
                CbZuAnsicht.Items.Add(T("Sek-I-Kurse", z.Zeilen.Count(x => x.Art == SchildZuordnungArt.Sek1Kurs),
                                        z.AnzahlOffen(SchildZuordnungArt.Sek1Kurs)));
                CbZuAnsicht.Items.Add(T("Sek I ohne Kurs (Klasse + Fach + Fachlehrer)", z.Zeilen.Count(x => x.Art == SchildZuordnungArt.OhneKurs),
                                        z.AnzahlOffen(SchildZuordnungArt.OhneKurs)));
                CbZuAnsicht.Items.Add(T("Lehrkräftekürzel", z.Kuerzel.Count, z.Kuerzel.Count(x => x.IstOffen)));
                CbZuAnsicht.Items.Add(T("Fächer", z.Faecher.Count, z.Faecher.Count(x => x.IstOffen)));
                CbZuAnsicht.Items.Add(T("Plan-Kurse ohne SchILD", z.PlanOhne.Count, z.PlanOhne.Count(x => x.IstOffen)));
                if (_zuErsteAnzeige)
                {
                    // Beim ersten Prüflauf die erste Ansicht mit offenen Einträgen zeigen.
                    int[] offenJe =
                    {
                        z.AnzahlOffen(SchildZuordnungArt.Oberstufe), z.AnzahlOffen(SchildZuordnungArt.Sek1Kurs),
                        z.AnzahlOffen(SchildZuordnungArt.OhneKurs), z.Kuerzel.Count(x => x.IstOffen),
                        z.Faecher.Count(x => x.IstOffen), z.PlanOhne.Count(x => x.IstOffen)
                    };
                    int i = Array.FindIndex(offenJe, n => n > 0);
                    sel = i >= 0 ? i : 0;
                    _zuErsteAnzeige = false;
                }
            }
            CbZuAnsicht.SelectedIndex = sel >= 0 && sel < CbZuAnsicht.Items.Count ? sel : 0;
            _zuAnsichtSetzen = false;

            FuelleZuTabelle(null);
            ZeigeZuStatus(null);
        }

        private void CbZuAnsicht_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_zuAnsichtSetzen) return;
            FuelleZuTabelle(null);
        }

        private void ZuFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (DgZuordnung == null) return;
            FuelleZuTabelle(null);
        }

        private void Spalte(string kopf, string pfad, double breite, bool umbrechen = false)
        {
            var b = new DataGridLength(breite <= 0 ? 1 : breite, breite <= 0 ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
            if (!umbrechen)
            {
                DgZuordnung.Columns.Add(new DataGridTextColumn { Header = kopf, Binding = new Binding(pfad), Width = b });
                return;
            }
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding(pfad));
            tb.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            DgZuordnung.Columns.Add(new DataGridTemplateColumn
            {
                Header = kopf, Width = b, CellTemplate = new DataTemplate { VisualTree = tb }, SortMemberPath = pfad
            });
        }

        /// <param name="auswahl">Zeile, die danach wieder ausgewählt werden soll (Schlüssel), sonst null.</param>
        private void FuelleZuTabelle(string? auswahl)
        {
            var z = _ergebnis?.Zuordnung;
            DgZuordnung.ItemsSource = null;
            DgZuordnung.Columns.Clear();
            if (z == null) { TxtZuAnzahl.Text = ""; ZeigeZuAuswahl(); return; }
            bool nurOffen = ChkZuNurOffene.IsChecked == true;
            IList liste;
            var ansicht = Ansicht;
            switch (ansicht)
            {
                case ZuAnsicht.Kuerzel:
                    Spalte("SchILD-Kürzel", nameof(SchildKuerzelZeile.Schild), 120);
                    Spalte("Plan-Kürzel", nameof(SchildKuerzelZeile.Plan), 120);
                    Spalte("Status", nameof(SchildKuerzelZeile.StatusText), 190);
                    Spalte("Begründung", nameof(SchildKuerzelZeile.Begruendung), 0, true);
                    liste = z.Kuerzel.Where(x => !nurOffen || x.IstOffen).ToList();
                    break;
                case ZuAnsicht.Faecher:
                    Spalte("SchILD-Fach", nameof(SchildFachZeile.Schild), 100);
                    Spalte("Bezeichnung", nameof(SchildFachZeile.Bezeichnung), 180);
                    Spalte("Plan-Fächer", nameof(SchildFachZeile.Plan), 140);
                    Spalte("Status", nameof(SchildFachZeile.StatusText), 110);
                    Spalte("Begründung", nameof(SchildFachZeile.Begruendung), 0, true);
                    liste = z.Faecher.Where(x => !nurOffen || x.IstOffen).ToList();
                    break;
                case ZuAnsicht.PlanOhne:
                    Spalte("Plan-Kurs", nameof(SchildPlanOhneZeile.Schluessel), 230);
                    Spalte("Art", nameof(SchildPlanOhneZeile.ArtText), 120);
                    Spalte("Stufe / Klassen", nameof(SchildPlanOhneZeile.Stufe), 200);
                    Spalte("Fach", nameof(SchildPlanOhneZeile.Fach), 120);
                    Spalte("Lehrkräfte", nameof(SchildPlanOhneZeile.Lehrer), 0, true);
                    liste = z.PlanOhne.Where(x => !nurOffen || x.IstOffen).ToList();
                    break;
                default:
                    var art = ansicht == ZuAnsicht.Sek1Kurs ? SchildZuordnungArt.Sek1Kurs
                            : ansicht == ZuAnsicht.OhneKurs ? SchildZuordnungArt.OhneKurs : SchildZuordnungArt.Oberstufe;
                    Spalte(art == SchildZuordnungArt.OhneKurs ? "Klasse · Fach · Fachlehrer" : "SchILD-Kurs",
                           nameof(SchildZuordnungZeile.Schild), art == SchildZuordnungArt.OhneKurs ? 170 : 110);
                    if (art != SchildZuordnungArt.OhneKurs)
                    {
                        Spalte("Jahrgang", nameof(SchildZuordnungZeile.JahrgangAnzeige), 95);
                        Spalte("Fach", nameof(SchildZuordnungZeile.FachText), 55);
                        Spalte("Kursart", nameof(SchildZuordnungZeile.Kursart), 55);
                        Spalte("Lehrkräfte", nameof(SchildZuordnungZeile.LehrkraefteText), 120);
                    }
                    Spalte("Teiln.", nameof(SchildZuordnungZeile.Teilnehmer), 48);
                    Spalte("Plan-Kurs", nameof(SchildZuordnungZeile.PlanText), 190);
                    Spalte("Status", nameof(SchildZuordnungZeile.StatusText), 150);
                    Spalte("Begründung", nameof(SchildZuordnungZeile.Begruendung), 0, true);
                    liste = z.Zeilen.Where(x => x.Art == art && (!nurOffen || x.IstOffen)).ToList();
                    break;
            }
            DgZuordnung.ItemsSource = liste;
            TxtZuAnzahl.Text = liste.Count == 0 ? (nurOffen ? "nichts offen ✓" : "keine Einträge") : $"{liste.Count} Zeile(n)";

            if (auswahl != null)
            {
                object? treffer = liste.Cast<object>().FirstOrDefault(o => ZuSchluessel(o) == auswahl);
                if (treffer != null) { DgZuordnung.SelectedItem = treffer; DgZuordnung.ScrollIntoView(treffer); }
            }
            ZeigeZuAuswahl();
        }

        private static string? ZuSchluessel(object? o) => o switch
        {
            SchildZuordnungZeile z => $"Z|{z.Art}|{z.JahrgangText}|{z.Schild}",
            SchildKuerzelZeile k => $"K|{k.Schild}|{k.Plan}",
            SchildFachZeile f => $"F|{f.Schild}|{(f.Schild.Length == 0 ? f.Plan : "")}",
            SchildPlanOhneZeile p => $"P|{p.Schluessel}",
            _ => null
        };

        private void DgZuordnung_SelectionChanged(object sender, SelectionChangedEventArgs e) => ZeigeZuAuswahl();

        /// <summary>Unterer Bereich: was ist gewählt, worauf kann festgelegt werden.</summary>
        private void ZeigeZuAuswahl()
        {
            var z = _ergebnis?.Zuordnung;
            CbZuPlan.Items.Clear();
            LbZuFaecher.Items.Clear();
            CbZuPlan.Visibility = Visibility.Visible;
            LbZuFaecher.Visibility = Visibility.Collapsed;
            BtnZuFestlegen.IsEnabled = false;
            BtnZuEntfernen.IsEnabled = false;
            if (z == null || _laeuft) { TxtZuAuswahl.Text = "Zeile wählen, um die Zuordnung von Hand festzulegen."; return; }

            switch (DgZuordnung.SelectedItem)
            {
                case SchildZuordnungZeile zz:
                {
                    TxtZuAuswahl.Text = $"SchILD: {zz.Schild}" + (zz.Art != SchildZuordnungArt.OhneKurs ? $" ({zz.JahrgangAnzeige})" : "") +
                                        $" · {zz.Teilnehmer} Teilnehmer → Plan: {zz.PlanText} ({zz.StatusText}). " +
                                        "✓ = passt nach Fach, Kursart und Lehrkraft.";
                    CbZuPlan.Items.Add(new ZuWahl { Text = "(automatisch – Festlegung entfernen)", Art = 0 });
                    CbZuPlan.Items.Add(new ZuWahl { Text = "(keine Zuordnung – gehört zu keinem Plan-Kurs)", Art = 1 });
                    foreach (var p in zz.Kandidaten) CbZuPlan.Items.Add(new ZuWahl { Text = "✓ " + p.Anzeige, Wert = p.Schluessel, Art = 2 });
                    foreach (var p in zz.Weitere) CbZuPlan.Items.Add(new ZuWahl { Text = "    " + p.Anzeige, Wert = p.Schluessel, Art = 2 });
                    int idx = 0;
                    if (zz.Status == SchildZuordnungStatus.KeineZuordnung) idx = 1;
                    else if (zz.Plan != null)
                        for (int i = 2; i < CbZuPlan.Items.Count; i++)
                            if (CbZuPlan.Items[i] is ZuWahl w && SchildText.Gleich(w.Wert, zz.Plan)) { idx = i; break; }
                    CbZuPlan.SelectedIndex = idx;
                    BtnZuFestlegen.IsEnabled = true;
                    BtnZuEntfernen.IsEnabled = zz.HatFestlegung;
                    break;
                }
                case SchildKuerzelZeile kz when kz.Schild.Length > 0:
                {
                    TxtZuAuswahl.Text = $"SchILD-Kürzel {kz.Schild} → Plan: {kz.Plan} ({kz.StatusText}).";
                    CbZuPlan.Items.Add(new ZuWahl { Text = "(automatisch – gleiches Kürzel im Plan)", Art = 0 });
                    int idx = 0;
                    foreach (var k in z.PlanKuerzel)
                    {
                        CbZuPlan.Items.Add(new ZuWahl { Text = k, Wert = k, Art = 2 });
                        if (kz.Plan == k && (kz.VonHand || kz.StatusText == "Vorschlag")) idx = CbZuPlan.Items.Count - 1;
                    }
                    CbZuPlan.SelectedIndex = idx;
                    BtnZuFestlegen.IsEnabled = true;
                    BtnZuEntfernen.IsEnabled = kz.VonHand;
                    break;
                }
                case SchildFachZeile fz when fz.Schild.Length > 0:
                {
                    TxtZuAuswahl.Text = $"SchILD-Fach {fz.Schild}{(fz.Bezeichnung.Length > 0 ? " (" + fz.Bezeichnung + ")" : "")} → Plan-Fächer: " +
                                        $"{fz.Plan} ({fz.StatusText}). Ein SchILD-Fach kann mehreren Plan-Fächern entsprechen – " +
                                        "alle passenden anklicken.";
                    CbZuPlan.Visibility = Visibility.Collapsed;
                    LbZuFaecher.Visibility = Visibility.Visible;
                    foreach (var f in z.PlanFaecher) LbZuFaecher.Items.Add(f);
                    foreach (var f in fz.PlanListe) if (LbZuFaecher.Items.Contains(f)) LbZuFaecher.SelectedItems.Add(f);
                    BtnZuFestlegen.IsEnabled = true;
                    BtnZuEntfernen.IsEnabled = fz.VonHand;
                    break;
                }
                case SchildKuerzelZeile:
                case SchildFachZeile:
                    TxtZuAuswahl.Text = "Nur im Plan vorhanden – festlegen über die Zeile des passenden SchILD-Kürzels bzw. SchILD-Fachs.";
                    break;
                case SchildPlanOhneZeile po:
                    TxtZuAuswahl.Text = $"{po.ArtText} „{po.Schluessel}“ hat keinen SchILD-Kurs. Zuordnen über die SchILD-Seite " +
                                        "(Ansicht Oberstufenkurse, Sek-I-Kurse bzw. Sek I ohne Kurs).";
                    break;
                default:
                    TxtZuAuswahl.Text = "Zeile wählen, um die Zuordnung von Hand festzulegen.";
                    break;
            }
        }

        private void ZeigeZuStatus(string? meldung)
        {
            var z = _ergebnis?.Zuordnung;
            BtnZuSpeichern.IsEnabled = _zuordnungGeaendert && !_laeuft;
            BtnZuVorschlaege.IsEnabled = z != null && z.VorschlagTexte.Count > 0 && !_laeuft;
            string datei = SchildZuordnungDatei.Name(_zuordnungPseudo);
            TxtZuStatus.Text = (meldung != null ? meldung + " · " : "") +
                               $"Festlegungen: {datei} ({_zuordnung.Anzahl})" +
                               (_zuordnungGeaendert ? " · NICHT GESPEICHERT" : "");
        }

        // =====================================================
        //  Festlegen
        // =====================================================

        private void BtnZuFestlegen_Click(object sender, RoutedEventArgs e)
        {
            var z = _ergebnis?.Zuordnung;
            if (z == null) return;
            var gewaehlt = DgZuordnung.SelectedItem;
            switch (gewaehlt)
            {
                case SchildZuordnungZeile zz when CbZuPlan.SelectedItem is ZuWahl w:
                    if (zz.Art == SchildZuordnungArt.OhneKurs)
                        _zuordnung.SetzeOhne(z.Schuljahr, zz.Klasse, zz.FachText, zz.Fachlehrer, w.Art == 2 ? w.Wert : null, w.Art == 0);
                    else
                        _zuordnung.SetzeKurs(z.Schuljahr, zz.JahrgangText, zz.KursBez, w.Art == 2 ? w.Wert : null, w.Art == 0);
                    break;
                case SchildKuerzelZeile kz when kz.Schild.Length > 0 && CbZuPlan.SelectedItem is ZuWahl w:
                    _zuordnung.SetzeKuerzel(kz.Schild, w.Art == 2 ? w.Wert : null);
                    break;
                case SchildFachZeile fz when fz.Schild.Length > 0:
                    var faecher = LbZuFaecher.SelectedItems.Cast<string>().ToList();
                    if (faecher.Count == 0)
                    {
                        MessageBox.Show(this, "Bitte mindestens ein Plan-Fach anklicken – oder „Festlegung entfernen“ (dann gilt wieder " +
                                              "das gleiche Fach im Plan).", "Zuordnung", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    _zuordnung.SetzeFaecher(fz.Schild, faecher);
                    break;
                default:
                    return;
            }
            ZuordnungGeaendert(ZuSchluessel(gewaehlt));
        }

        private void BtnZuEntfernen_Click(object sender, RoutedEventArgs e)
        {
            var z = _ergebnis?.Zuordnung;
            if (z == null) return;
            var gewaehlt = DgZuordnung.SelectedItem;
            switch (gewaehlt)
            {
                case SchildZuordnungZeile zz:
                    if (zz.Art == SchildZuordnungArt.OhneKurs) _zuordnung.SetzeOhne(z.Schuljahr, zz.Klasse, zz.FachText, zz.Fachlehrer, null, true);
                    else _zuordnung.SetzeKurs(z.Schuljahr, zz.JahrgangText, zz.KursBez, null, true);
                    break;
                case SchildKuerzelZeile kz when kz.Schild.Length > 0:
                    _zuordnung.SetzeKuerzel(kz.Schild, null);
                    break;
                case SchildFachZeile fz when fz.Schild.Length > 0:
                    _zuordnung.SetzeFaecher(fz.Schild, null);
                    break;
                default:
                    return;
            }
            ZuordnungGeaendert(ZuSchluessel(gewaehlt));
        }

        private void BtnZuVorschlaege_Click(object sender, RoutedEventArgs e)
        {
            var z = _ergebnis?.Zuordnung;
            if (z == null || z.VorschlagTexte.Count == 0) return;
            if (MessageBox.Show(this,
                    "Diese Vorschläge als Festlegungen übernehmen?\n\n• " + string.Join("\n• ", z.VorschlagTexte) +
                    "\n\nBitte vorher kurz prüfen, ob sie stimmen. Gespeichert wird erst mit „Zuordnungen speichern“.",
                    "Vorschläge übernehmen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            foreach (var kv in z.VorschlagKuerzel) _zuordnung.SetzeKuerzel(kv.Key, kv.Value);
            foreach (var kv in z.VorschlagFaecher) _zuordnung.SetzeFaecher(kv.Key, kv.Value);
            ZuordnungGeaendert(ZuSchluessel(DgZuordnung.SelectedItem));
        }

        private void BtnZuSpeichern_Click(object sender, RoutedEventArgs e)
        {
            SpeichereZuordnung();
            ZeigeZuStatus(_zuordnungGeaendert ? null : "Gespeichert");
        }

        /// <summary>Nach einer Festlegung: Prüflauf neu auswerten (ohne neu zu lesen), Ansicht und Auswahl behalten.</summary>
        private void ZuordnungGeaendert(string? auswahl)
        {
            _zuordnungGeaendert = true;
            int ansicht = CbZuAnsicht.SelectedIndex;
            ZeigeErgebnis();
            if (ansicht >= 0 && ansicht < CbZuAnsicht.Items.Count && CbZuAnsicht.SelectedIndex != ansicht)
            {
                _zuAnsichtSetzen = true;
                CbZuAnsicht.SelectedIndex = ansicht;
                _zuAnsichtSetzen = false;
            }
            FuelleZuTabelle(auswahl);
            ZeigeZuStatus("Festlegung gilt im Prüflauf");
        }
    }
}
