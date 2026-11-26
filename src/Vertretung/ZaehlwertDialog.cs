#nullable enable
using System.Collections.Generic;
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
    ///
    /// V4b (291): Bei vertretenen Stunden (eine andere Lehrkraft hält sie, Zaehlung.HatAusfall) ein zweiter Teil
    /// „Ausfallende Lehrkraft (L…)“: Anlass (fehlt 0 / nicht verhindert −1) und eigener Wert mit Kommentar. Jeder
    /// Teil hat „Standard“ und „Zurück auf Standard“; „Festlegen“ übernimmt beide Teile.
    /// </summary>
    public static class ZaehlwertDialog
    {
        /// <summary>Ergebnis: <see cref="Standard"/> = zurück auf Standard, sonst eigener Wert mit Kommentar.</summary>
        public sealed class Ergebnis
        {
            public bool Standard { get; init; }
            public int Wert { get; init; }
            public string Kommentar { get; init; } = "";
            /// <summary>V4b (291): Teil der ausfallenden Lehrkraft (null = kein solcher Teil).</summary>
            public AusfallErgebnis? Ausfall { get; init; }
        }

        /// <summary>V4b (291): Teil „Ausfallende Lehrkraft“.</summary>
        public sealed class AusfallErgebnis
        {
            public AusfallAnlass Anlass { get; init; }
            public bool Standard { get; init; }
            public int Wert { get; init; }
            public string Kommentar { get; init; } = "";
        }

        /// <summary>Bausteine eines Teils (Standard, −1, 0, +1, Kommentar).</summary>
        private sealed class Teil
        {
            public RadioButton Standard = new();
            public List<RadioButton> Werte = new();
            public TextBox Kommentar = new();
            public TextBlock Fehler = new();

            public int? EigenerWert => Werte.FirstOrDefault(k => k.IsChecked == true)?.Tag is int w ? w : (int?)null;

            public void Freigeben() => Kommentar.IsEnabled = EigenerWert != null;
        }

        /// <summary>
        /// Zeigt das Fenster. <paramref name="kopf"/> beschreibt die Stunde, <paramref name="standardWert"/> und
        /// <paramref name="standardText"/> den Wert laut 274/275. Bei <paramref name="mitAusfall"/> zusätzlich der Teil der
        /// ausfallenden Lehrkraft (Vorbelegung aus dem Eintrag). null = abgebrochen.
        /// </summary>
        public static Ergebnis? Zeige(Window owner, string kopf, int standardWert, string standardText, VertretungsEintrag e,
            bool mitAusfall = false)
        {
            var win = new Window
            {
                Title = "Zählwert festlegen",
                Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner
            };
            var sp = new StackPanel { Margin = new Thickness(14) };

            sp.Children.Add(new TextBlock
            {
                Text = kopf, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });

            // ---- Teil 1: Zählperson (die haltende Lehrkraft bzw. die laut Plan) ----
            string wer = Zaehlung.Zaehlperson(e);
            var t1 = BaueTeil(sp, "zaehlwert",
                mitAusfall ? $"Lehrkraft, die die Stunde hält ({wer})" : (wer.Length > 0 ? $"Zählung für {wer}" : "Zählung"),
                $"Standard: {Zaehlung.WertText(standardWert)} ({standardText})" + "\n" +
                (e.Sondergrund
                    ? $"Jetzt: Sondergrund {Zaehlung.WertText(e.ZaehlwertSnapshot)}"
                    : $"Jetzt: {Zaehlung.WertText(e.ZaehlwertSnapshot)} (Standard)"),
                $"Standard ({Zaehlung.WertText(standardWert)})",
                e.Sondergrund ? e.ZaehlwertSnapshot : (int?)null, e.Sondergrund ? e.Zaehlkommentar ?? "" : "");

            // ---- Teil 2 (V4b, 291): ausfallende Lehrkraft ----
            Teil? t2 = null;
            RadioButton? rbFehlt = null, rbNichtV = null;
            if (mitAusfall)
            {
                sp.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 8) });
                string orig = (e.OriginalLehrer ?? "").Trim();
                var anlassJetzt = e.AnlassAusfall;
                var zeileAnlass = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                zeileAnlass.Children.Add(new TextBlock { Text = "Anlass:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                rbFehlt = new RadioButton
                {
                    Content = "fehlt (krank/dienstlich) → 0", GroupName = "ausfallAnlass", Margin = new Thickness(0, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Center, IsChecked = anlassJetzt != AusfallAnlass.NichtVerhindert
                };
                rbNichtV = new RadioButton
                {
                    Content = "nicht verhindert → −1", GroupName = "ausfallAnlass", VerticalAlignment = VerticalAlignment.Center,
                    IsChecked = anlassJetzt == AusfallAnlass.NichtVerhindert,
                    ToolTip = "Die Lehrkraft war gar nicht verhindert und hätte eigentlich einen Entfall gehabt."
                };
                zeileAnlass.Children.Add(rbFehlt);
                zeileAnlass.Children.Add(rbNichtV);

                t2 = BaueTeil(sp, "zaehlwertAusfall",
                    $"Ausfallende Lehrkraft ({orig})",
                    $"Jetzt: " + (e.SondergrundAusfall
                        ? $"Sondergrund {Zaehlung.WertText(e.ZaehlwertAusfall)} (Anlass {Zaehlung.AusfallText(anlassJetzt)})"
                        : $"{Zaehlung.WertText(e.ZaehlwertAusfall)} – {Zaehlung.AusfallText(anlassJetzt)}"),
                    "Standard (laut Anlass)",
                    e.SondergrundAusfall ? e.ZaehlwertAusfall : (int?)null, e.SondergrundAusfall ? e.KommentarAusfall ?? "" : "",
                    zeileAnlass);
            }

            var unten = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var btnOk = new Button { Content = "Festlegen", Width = 110, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var btnAbbr = new Button { Content = "Abbrechen", Width = 100, IsCancel = true };
            unten.Children.Add(btnOk); unten.Children.Add(btnAbbr);
            sp.Children.Add(unten);
            win.Content = sp;

            Ergebnis? ergebnis = null;
            btnOk.Click += (_, __) =>
            {
                bool ok = true;
                foreach (var t in new[] { t1, t2 })
                {
                    if (t == null) continue;
                    t.Fehler.Text = "";
                    if (t.EigenerWert != null && Zaehlung.KommentarFehler(t.Kommentar.Text) is string fehler)
                    {
                        t.Fehler.Text = fehler;
                        if (ok) t.Kommentar.Focus();
                        ok = false;
                    }
                }
                if (!ok) return;
                AusfallErgebnis? ausfall = null;
                if (t2 != null)
                    ausfall = new AusfallErgebnis
                    {
                        Anlass = rbNichtV?.IsChecked == true ? AusfallAnlass.NichtVerhindert : AusfallAnlass.Lehrkraft,
                        Standard = t2.EigenerWert == null,
                        Wert = t2.EigenerWert ?? 0,
                        Kommentar = t2.EigenerWert != null ? Zaehlung.Bereinige(t2.Kommentar.Text) : ""
                    };
                ergebnis = new Ergebnis
                {
                    Standard = t1.EigenerWert == null,
                    Wert = t1.EigenerWert ?? 0,
                    Kommentar = t1.EigenerWert != null ? Zaehlung.Bereinige(t1.Kommentar.Text) : "",
                    Ausfall = ausfall
                };
                win.DialogResult = true;
            };
            win.Loaded += (_, __) => { if (t1.EigenerWert != null) t1.Kommentar.Focus(); else t1.Standard.Focus(); };

            return win.ShowDialog() == true ? ergebnis : null;
        }

        /// <summary>Ein Teil: Überschrift, Hinweis, optional eine Zeile davor, Auswahl Standard/−1/0/+1, Kommentar, „Zurück auf Standard“.</summary>
        private static Teil BaueTeil(StackPanel sp, string gruppe, string ueberschrift, string hinweis, string standardText,
            int? eigenJetzt, string kommentarJetzt, UIElement? vorAuswahl = null)
        {
            var t = new Teil();
            sp.Children.Add(new TextBlock { Text = ueberschrift, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            sp.Children.Add(new TextBlock
            {
                Text = hinweis, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6)
            });
            if (vorAuswahl != null) sp.Children.Add(vorAuswahl);

            var zeileWert = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            zeileWert.Children.Add(new TextBlock { Text = "Zählwert:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            t.Standard = new RadioButton
            {
                Content = standardText, GroupName = gruppe, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0), IsChecked = eigenJetzt == null
            };
            zeileWert.Children.Add(t.Standard);
            zeileWert.Children.Add(new TextBlock { Text = "eigener Wert:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = Brushes.DimGray });
            t.Werte = Zaehlung.EigeneWerte.Select(w => new RadioButton
            {
                Content = Zaehlung.WertText(w), Tag = w, GroupName = gruppe, MinWidth = 42,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                IsChecked = eigenJetzt == w
            }).ToList();
            foreach (var rb in t.Werte) zeileWert.Children.Add(rb);
            sp.Children.Add(zeileWert);

            sp.Children.Add(new TextBlock { Text = "Kommentar (Pflicht bei eigenem Wert, eine Zeile):", Margin = new Thickness(0, 0, 0, 2) });
            var zeileKomm = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var btnStd = new Button
            {
                Content = "Zurück auf Standard", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Sondergrund und Kommentar dieses Teils löschen, Wert laut Standard (übernommen mit „Festlegen“)"
            };
            DockPanel.SetDock(btnStd, Dock.Right);
            t.Kommentar = new TextBox { Text = kommentarJetzt, MaxLength = Zaehlung.KommentarMax, AcceptsReturn = false };
            zeileKomm.Children.Add(btnStd);
            zeileKomm.Children.Add(t.Kommentar);
            sp.Children.Add(zeileKomm);
            sp.Children.Add(new TextBlock
            {
                Text = Zaehlung.KommentarHinweis, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 2, 0, 4)
            });
            t.Fehler = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(t.Fehler);

            t.Standard.Checked += (_, __) => t.Freigeben();
            foreach (var rb in t.Werte) rb.Checked += (_, __) => t.Freigeben();
            btnStd.Click += (_, __) =>
            {
                t.Standard.IsChecked = true;
                t.Kommentar.Text = "";
                t.Fehler.Text = "";
                t.Freigeben();
            };
            t.Freigeben();
            return t;
        }
    }
}
