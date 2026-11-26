#nullable enable
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// V4 (277, 278): kleines Fenster „Zählwert festlegen“ für eine gebuchte Stunde im Wocheneditor. Der Planer
    /// setzt den Zählwert selbst (−1, 0 oder +1) – nur mit Kommentar (eine Zeile, bis 200 Zeichen) – oder nimmt
    /// einen Sondergrund mit „Zurück auf Standard“ zurück. Ohne XAML (wie die Fenster der Dauervertretung).
    /// </summary>
    public static class ZaehlwertDialog
    {
        /// <summary>Ergebnis: <see cref="Standard"/> = zurück auf Standard, sonst eigener Wert mit Kommentar.</summary>
        public sealed class Ergebnis
        {
            public bool Standard { get; init; }
            public int Wert { get; init; }
            public string Kommentar { get; init; } = "";
        }

        /// <summary>
        /// Zeigt das Fenster. <paramref name="kopf"/> beschreibt die Stunde, <paramref name="standardWert"/> und
        /// <paramref name="standardText"/> den Wert laut 274/275. null = abgebrochen.
        /// </summary>
        public static Ergebnis? Zeige(Window owner, string kopf, int standardWert, string standardText, VertretungsEintrag e)
        {
            var win = new Window
            {
                Title = "Zählwert festlegen",
                Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner
            };
            var sp = new StackPanel { Margin = new Thickness(14) };

            sp.Children.Add(new TextBlock
            {
                Text = kopf, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            });
            string wer = Zaehlung.Zaehlperson(e);
            sp.Children.Add(new TextBlock
            {
                Text = $"Standard: {Zaehlung.WertText(standardWert)} ({standardText})" +
                       (wer.Length > 0 ? $" – zählt für {wer}" : "") + "\n" +
                       (e.Sondergrund
                           ? $"Jetzt: Sondergrund {Zaehlung.WertText(e.ZaehlwertSnapshot)}"
                           : $"Jetzt: {Zaehlung.WertText(e.ZaehlwertSnapshot)} (Standard)"),
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10)
            });

            var zeileWert = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            zeileWert.Children.Add(new TextBlock { Text = "Eigener Wert:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var knoepfe = Zaehlung.EigeneWerte.Select(w => new RadioButton
            {
                Content = Zaehlung.WertText(w), Tag = w, GroupName = "zaehlwert", MinWidth = 50,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                IsChecked = w == e.ZaehlwertSnapshot
            }).ToList();
            if (!knoepfe.Any(k => k.IsChecked == true)) knoepfe[1].IsChecked = true;   // 0
            foreach (var rb in knoepfe) zeileWert.Children.Add(rb);
            sp.Children.Add(zeileWert);

            sp.Children.Add(new TextBlock { Text = "Kommentar (Pflicht, eine Zeile):", Margin = new Thickness(0, 0, 0, 2) });
            var txtKomm = new TextBox
            {
                Text = e.Sondergrund ? e.Zaehlkommentar ?? "" : "", MaxLength = Zaehlung.KommentarMax, AcceptsReturn = false
            };
            sp.Children.Add(txtKomm);
            sp.Children.Add(new TextBlock
            {
                Text = Zaehlung.KommentarHinweis, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 2, 0, 6)
            });
            var txtFehler = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(txtFehler);

            var unten = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnOk = new Button { Content = "Festlegen", Width = 110, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var btnStd = new Button
            {
                Content = "Zurück auf Standard", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = e.Sondergrund,
                ToolTip = "Sondergrund und Kommentar löschen, Wert laut Standard"
            };
            var btnAbbr = new Button { Content = "Abbrechen", Width = 100, IsCancel = true };
            unten.Children.Add(btnOk); unten.Children.Add(btnStd); unten.Children.Add(btnAbbr);
            sp.Children.Add(unten);
            win.Content = sp;

            Ergebnis? ergebnis = null;
            btnOk.Click += (_, __) =>
            {
                if (Zaehlung.KommentarFehler(txtKomm.Text) is string fehler)
                {
                    txtFehler.Text = fehler;
                    txtKomm.Focus();
                    return;
                }
                int wert = knoepfe.FirstOrDefault(k => k.IsChecked == true)?.Tag is int gw ? gw : 0;
                ergebnis = new Ergebnis { Standard = false, Wert = wert, Kommentar = Zaehlung.Bereinige(txtKomm.Text) };
                win.DialogResult = true;
            };
            btnStd.Click += (_, __) =>
            {
                ergebnis = new Ergebnis { Standard = true };
                win.DialogResult = true;
            };
            win.Loaded += (_, __) => txtKomm.Focus();

            return win.ShowDialog() == true ? ergebnis : null;
        }
    }
}
