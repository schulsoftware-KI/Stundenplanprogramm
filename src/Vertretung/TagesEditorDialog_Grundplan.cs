#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// Phase 2, Schritt 1: Button „Grundplan veröffentlichen…" im Wocheneditor.
    /// Teil der partiellen Klasse TagesEditorDialog (nutzt _plan, _planPfad, _daten,
    /// _ordner, _log, CbPlan, SetzeInfo).
    ///
    /// Veröffentlicht wird der GRUNDPLAN (Wochenraster ohne Vertretungen) der Plan-
    /// Spalte, die oben unter „Plan" ausgewählt ist – auch wenn noch nicht auf
    /// „Aktualisieren" geklickt wurde. Dazu der Schuljahreskalender.
    /// </summary>
    public partial class TagesEditorDialog
    {
        private async void BtnGrundplanPush_Click(object sender, RoutedEventArgs e)
        {
            var geladen = _plan;
            if (!_bereit || geladen == null)
            { SetzeInfo("Plan ist nicht geladen."); return; }

            if (string.IsNullOrWhiteSpace(_daten.Schuljahr))
            {
                MessageBox.Show("Im Schuljahreskalender ist kein Schuljahr eingetragen (Button 22).",
                    "Grundplan", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var einst = OnlineSync.LadeEinstellungen(_ordner);
            if (string.IsNullOrWhiteSpace(einst.BaseUrl))
            {
                var neu = OnlineSync.FrageEinstellungen(this, einst);
                if (neu == null) return;
                OnlineSync.SpeichereEinstellungen(_ordner, neu);
                einst = neu;
            }

            DateTime? gueltigAb = FrageGueltigAb();
            if (gueltigAb == null) return;

            // Plan-Spalte so nehmen, wie sie oben ausgewählt ist.
            PlanDaten plan = geladen;
            if (CbPlan.SelectedItem is string spalte &&
                !string.Equals(spalte, geladen.GewaehltePlanSpalte, StringComparison.OrdinalIgnoreCase))
            {
                try { plan = PlanLeser.Lies(_planPfad, spalte); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Plan „{spalte}\" konnte nicht gelesen werden:\n\n" + ex.Message,
                        "Grundplan", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            GrundplanPushDto dto;
            string? hinweis;
            try { dto = GrundplanSync.Baue(_daten.Schuljahr, _daten.Kalender, plan, gueltigAb.Value, out hinweis); }
            catch (Exception ex)
            {
                MessageBox.Show("Grundplan konnte nicht aufgebaut werden:\n\n" + ex.Message,
                    "Grundplan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (dto.Slots.Count == 0)
            {
                MessageBox.Show($"Der Plan „{dto.Bezeichnung}\" enthält keine belegten Stunden – nichts zu veröffentlichen.",
                    "Grundplan", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int nKlassen = dto.Slots.Where(s => s.LerngruppeTyp == "Klasse").Select(s => s.LerngruppeKey).Distinct().Count();
            int nKurse = dto.Slots.Where(s => s.LerngruppeTyp == "Kurs").Select(s => s.LerngruppeKey).Distinct().Count();
            int nKurseOhneSg = dto.Slots.Where(s => s.LerngruppeTyp == "Kurs" && s.Schuelergruppe == null)
                                        .Select(s => s.LerngruppeKey).Distinct().Count();
            int nSonst = dto.Slots.Where(s => s.LerngruppeTyp == "Sonstige").Select(s => s.LerngruppeKey).Distinct().Count();
            int nLehrer = dto.Slots.Select(s => s.Lehrer).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int nFrei = dto.Kalender?.FreieZeiten.Count ?? 0;

            var frage =
                $"Grundplan „{dto.Bezeichnung}\" ab {gueltigAb:dd.MM.yyyy} veröffentlichen?\n\n" +
                $"{dto.Slots.Count} Einträge · {nLehrer} Lehrkräfte\n" +
                $"{nKlassen} Klassen · {nKurse} Kurse · {nSonst} sonstige Gruppen\n" +
                (nKurseOhneSg > 0 ? $"(davon {nKurseOhneSg} Kurs(e) ohne Schülergruppe in der UV)\n" : "") +
                $"Kalender {_daten.Schuljahr}: {nFrei} freie Zeiträume\n\n" +
                (hinweis != null ? "⚠ " + hinweis + "\n\n" : "") +
                "Eine bereits vorhandene Version mit demselben Gültig-ab-Datum wird ersetzt.";
            if (MessageBox.Show(frage, "Grundplan veröffentlichen",
                    MessageBoxButton.OKCancel,
                    hinweis != null ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            try
            {
                BtnGrundplanPush.IsEnabled = false;
                SetzeInfo($"Veröffentliche Grundplan ({dto.Slots.Count} Einträge) …");
                string antwort = await GrundplanSync.PushAsync(einst, dto);
                string art = GrundplanSync.WarVersionErsetzt(antwort) ? "Version ersetzt" : "neue Version";
                SetzeInfo($"Grundplan veröffentlicht: „{dto.Bezeichnung}\" ab {gueltigAb:dd.MM.yyyy}, {dto.Slots.Count} Einträge ({art}).");
                _log?.Invoke($"Online: Grundplan '{dto.Bezeichnung}' ab {gueltigAb:dd.MM.yyyy} veröffentlicht " +
                             $"({dto.Slots.Count} Einträge, {art}).");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Veröffentlichen fehlgeschlagen:\n\n" + ex.Message,
                    "Grundplan", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetzeInfo("Grundplan-Veröffentlichung fehlgeschlagen.");
            }
            finally { BtnGrundplanPush.IsEnabled = true; }
        }

        /// <summary>Kleiner Dialog: ab welchem Datum gilt dieser Grundplan? (null = Abbruch)</summary>
        private DateTime? FrageGueltigAb()
        {
            // Vorgabe: heute; liegt heute außerhalb des Schuljahres, der erste Schultag.
            DateTime vorgabe = DateTime.Today;
            if (_daten.Kalender.Start != default &&
                (vorgabe < _daten.Kalender.Start.Date ||
                 (_daten.Kalender.Ende != default && vorgabe > _daten.Kalender.Ende.Date)))
                vorgabe = _daten.Kalender.Start.Date;

            var win = new Window
            {
                Title = "Grundplan – gültig ab",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Ab welchem Datum soll dieser Grundplan gelten?\n" +
                       "(Für einen Planwechsel im Schuljahr das Datum des Wechsels wählen; " +
                       "ältere Versionen bleiben erhalten.)",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            var dp = new DatePicker { SelectedDate = vorgabe, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(dp);

            var knoepfe = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var ok = new Button { Content = "Weiter", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var abbr = new Button { Content = "Abbrechen", Width = 90, IsCancel = true };
            knoepfe.Children.Add(ok); knoepfe.Children.Add(abbr);
            panel.Children.Add(knoepfe);

            win.Content = panel;

            DateTime? ergebnis = null;
            ok.Click += (_, __) =>
            {
                if (dp.SelectedDate == null)
                { MessageBox.Show(win, "Bitte ein Datum wählen.", "Grundplan", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                ergebnis = dp.SelectedDate.Value.Date;
                win.DialogResult = true;
            };

            return win.ShowDialog() == true ? ergebnis : null;
        }
    }
}
