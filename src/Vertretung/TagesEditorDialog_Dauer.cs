#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// V3 – Dauervertretung im Wocheneditor (Teil der partiellen Klasse TagesEditorDialog; Entscheidungen 158–162).
    ///  - Klick auf eine Stunde → „Dauervertretung (jede Woche) …“: dieselbe Wochenstunde jede Woche bis zu einem
    ///    Datum buchen (Vertretung, Betreuung/Aufsicht, EVA oder Entfall), mit Vorschau je Tag. Ferien und freie
    ///    Tage werden übersprungen; fehlt die Vertretungskraft oder ist sie schon belegt, bleibt die Stunde offen
    ///    (Lücke mit Hinweis). Ein neuer Raum der Stunde wird mitgenommen, wenn er frei ist.
    ///  - Knopf „Künftige zurücksetzen…“ bzw. Klick auf eine Stunde einer Dauervertretung → „ab hier
    ///    zurücksetzen …“: Buchungen ab einem Datum entfernen – nur einer Dauervertretung bzw. einer Lehrkraft,
    ///    mit Vorschau und Rückfrage; wahlweise endet die Abwesenheit der Lehrkraft am Tag davor.
    ///  - Danach wird „Zeitraum veröffentlichen“ für die betroffenen Tage vorgeschlagen (Entscheidung 161).
    ///  - V4 (278): „Zählung: Standard / eigener Wert“ mit Kommentar gilt für alle Stunden der Serie.
    ///  - V4b (296): ebenso „Ausfall“ (fehlt 0 / nicht verhindert −1) und ein eigener Wert der ausfallenden Lehrkraft.
    /// Die Plandatei wird nicht verändert; gebucht wird in die Vertretungsdatei (Spalte „SerienId“).
    /// </summary>
    public partial class TagesEditorDialog
    {
        /// <summary>Letzter Tag je Dauervertretung (für „⟳ bis …“ in den Zellen); wird in Zeichne() neu bestimmt.</summary>
        private Dictionary<string, DateTime> _serienEnde = new(StringComparer.OrdinalIgnoreCase);

        private static readonly SolidColorBrush DauerLueckeBrush = new(Color.FromRgb(0xB7, 0x1C, 0x1C));
        private static readonly SolidColorBrush DauerBehaltenBrush = new(Color.FromRgb(0xE6, 0x51, 0x00));

        /// <summary>„⟳ bis 18.12.“ für eine Stunde einer Dauervertretung, sonst leer.</summary>
        private string DauerMarke(TagesZelle z)
        {
            var e = z.Eintrag;
            if (e == null || !e.IstDauervertretung) return "";
            return _serienEnde.TryGetValue(e.SerienId.Trim(), out var bis) ? $"⟳ bis {bis:dd.MM.}" : "⟳";
        }

        /// <summary>Menüpunkte „Dauervertretung …“ einer Stunde (leer bei Pseudofächern wie VBer, UV, PSF).</summary>
        private List<MenuItem> BaueDauerMenu(TagesZelle z, DateTime datum)
        {
            var l = new List<MenuItem>();
            if (_plan == null || string.IsNullOrWhiteSpace(z.OriginalLehrer)) return l;
            if (z.Status == ZellStatus.VerlegtWeg || z.Status == ZellStatus.VerlegtHin) return l;
            if (Stundenplan_V2.Raumplanung.RaumplanStore.IstOhneRaumbedarf(z.Fach)) return l;

            var mi = new MenuItem
            {
                Header = "Dauervertretung (jede Woche bis …) …",
                ToolTip = $"Diese Stunde ({UntisText.Tag(datum)}, {z.Stunde}. Std) jede Woche bis zu einem Datum buchen – mit Vorschau"
            };
            mi.Click += (_, __) => ZeigeDauerFenster(z, datum);
            l.Add(mi);

            if (z.Eintrag?.IstDauervertretung == true)
            {
                string id = z.Eintrag.SerienId.Trim();
                string bis = _serienEnde.TryGetValue(id, out var b) ? $" (bis {b:dd.MM.})" : "";
                var miReset = new MenuItem { Header = $"Dauervertretung{bis} ab hier zurücksetzen …" };
                string orig = z.OriginalLehrer;
                miReset.Click += (_, __) => ZeigeZuruecksetzenFenster(datum, id, orig);
                l.Add(miReset);
            }
            return l;
        }

        /// <summary>Eintrag der Auswahlliste „Vertretung durch“.</summary>
        private sealed class VertreterWahl
        {
            public string Lehrer { get; init; } = "";
            public string Anzeige { get; init; } = "";
            public override string ToString() => Anzeige;
        }

        // =====================================================
        //  Fenster „Dauervertretung“
        // =====================================================

        private void ZeigeDauerFenster(TagesZelle z, DateTime datum)
        {
            if (!_bereit || _plan == null) return;
            var plan = _plan;
            string? wtag = _daten.Kalender.RasterTagFuer(datum);
            if (wtag == null) { SetzeInfo($"{datum:dd.MM.} ist kein Unterrichtstag."); return; }

            var einst = OnlineSync.LadeEinstellungen(_ordner);
            UntisImportProtokoll? prot = null;
            try { prot = UntisImportProtokoll.Laden(_ordner); } catch { prot = null; }
            bool AusUntis(DateTime d) => prot != null && prot.Tage.ContainsKey(UntisImportProtokoll.TagSchluessel(d));

            string klassen = string.Join(", ", z.Klassen);
            var win = new Window
            {
                Title = "Dauervertretung",
                Width = 700, Height = 780, MinWidth = 560, MinHeight = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.CanResizeWithGrip
            };
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var oben = new StackPanel();
            oben.Children.Add(new TextBlock
            {
                Text = $"Wochenstunde: {UntisText.Tag(datum)}, {z.Stunde}. Std  ·  {z.Fach} {klassen}  ·  {z.OriginalLehrer}" +
                       (string.IsNullOrWhiteSpace(z.GrundRaum) ? "" : $"  ·  Raum {z.GrundRaum}"),
                FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4)
            });
            oben.Children.Add(new TextBlock
            {
                Text = "Bucht diese Stunde an jedem Schultag dieses Wochentags von–bis. Ferien und freie Tage laut " +
                       "Kalender werden übersprungen. Fehlt die Vertretungskraft an einem Tag oder ist sie in der Stunde " +
                       "schon belegt, wird dort nicht gebucht (Lücke – die Stunde bleibt offen). Jede Stunde zählt wie " +
                       "eine einzelne Vertretung (Entfall: Lehrkraft fehlt, 0).",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10)
            });

            // Art + Vertretung durch
            var zeileArt = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            zeileArt.Children.Add(Beschriftung("Art:"));
            var cbArt = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 14, 0) };
            foreach (var a in Dauervertretung.Arten)
                cbArt.Items.Add(new ComboBoxItem { Content = Dauervertretung.ArtText(a), Tag = a });
            var artVorgabe = z.Eintrag != null && Dauervertretung.Arten.Contains(z.Eintrag.Art)
                ? z.Eintrag.Art : Vertretungsart.Vertretung;
            cbArt.SelectedItem = cbArt.Items.OfType<ComboBoxItem>().First(i => (Vertretungsart)i.Tag == artVorgabe);
            zeileArt.Children.Add(cbArt);

            zeileArt.Children.Add(Beschriftung("durch:"));
            var cbVertreter = new ComboBox { Width = 330 };
            var inst = new StundenInstanz
            {
                Datum = datum, Stunde = z.Stunde, UNr = z.UNr,
                Lehrer = z.OriginalLehrer, Fach = z.Fach, Klassen = new List<string>(z.Klassen)
            };
            foreach (var v in VertretungsService.Vorschlaege(inst, plan, _daten.Kalender, Saldo()))
                cbVertreter.Items.Add(new VertreterWahl { Lehrer = v.Lehrer, Anzeige = $"{v.Lehrer}  ({v.Begruendung})" });
            string? bisher = z.Eintrag != null && !string.IsNullOrWhiteSpace(z.Eintrag.VertretungsLehrer)
                ? z.Eintrag.VertretungsLehrer!.Trim() : null;
            if (bisher != null && !cbVertreter.Items.OfType<VertreterWahl>().Any(w =>
                    string.Equals(w.Lehrer, bisher, StringComparison.OrdinalIgnoreCase)))
                cbVertreter.Items.Insert(0, new VertreterWahl { Lehrer = bisher, Anzeige = $"{bisher}  (bisher an diesem Tag gebucht)" });
            cbVertreter.SelectedItem = cbVertreter.Items.OfType<VertreterWahl>().FirstOrDefault(w =>
                                           string.Equals(w.Lehrer, bisher, StringComparison.OrdinalIgnoreCase))
                                       ?? cbVertreter.Items.OfType<VertreterWahl>().FirstOrDefault();
            zeileArt.Children.Add(cbVertreter);
            oben.Children.Add(zeileArt);
            var txtKeinVertreter = new TextBlock
            {
                Text = "(Für diese Stunde gibt es laut Plan keine freie Lehrkraft – nur EVA oder Entfall möglich.)",
                Foreground = DauerLueckeBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
                Visibility = cbVertreter.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed
            };
            oben.Children.Add(txtKeinVertreter);

            // Von–bis
            var zeileZeit = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            zeileZeit.Children.Add(Beschriftung("Von:"));
            var dpVon = new DatePicker { SelectedDate = datum.Date, Width = 130, Margin = new Thickness(0, 0, 12, 0) };
            zeileZeit.Children.Add(dpVon);
            zeileZeit.Children.Add(Beschriftung("Bis:"));
            var dpBis = new DatePicker
            {
                SelectedDate = Dauervertretung.BisFerienbeginn(_daten.Kalender, datum), Width = 130, Margin = new Thickness(0, 0, 12, 0)
            };
            zeileZeit.Children.Add(dpBis);
            var btnFerien = new Button { Content = "bis Ferienbeginn", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
            var btnEnde = new Button { Content = "bis Schuljahresende", Padding = new Thickness(8, 2, 8, 2) };
            zeileZeit.Children.Add(btnFerien);
            zeileZeit.Children.Add(btnEnde);
            oben.Children.Add(zeileZeit);

            // Grund
            var zeileGrund = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            zeileGrund.Children.Add(Beschriftung("Grund:"));
            // V4b (288): Gründe wie bisher (Lehrkraft fehlt), dazu der Grund der angeklickten Buchung, falls er fehlt.
            var cbGrund = new ComboBox { Width = 170, ItemsSource = Zaehlung.GruendeMit(Zaehlung.LehrerGruende, z.Eintrag?.Grund) };
            cbGrund.SelectedItem = z.Eintrag != null ? z.Eintrag.Grund
                                 : CbGrund.SelectedItem is VertretungsGrund g ? g : VertretungsGrund.Krankheit;
            zeileGrund.Children.Add(cbGrund);
            oben.Children.Add(zeileGrund);

            // V4 (278): Zählung für alle Stunden der Serie – Standard oder eigener Wert mit Kommentar (Sondergrund).
            var zeileZaehl = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            zeileZaehl.Children.Add(Beschriftung("Zählung:"));
            var cbZaehl = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 14, 0) };
            var itemStandard = new ComboBoxItem { Content = "Standard", Tag = null };
            cbZaehl.Items.Add(itemStandard);
            foreach (int zw in Zaehlung.EigeneWerte)
                cbZaehl.Items.Add(new ComboBoxItem { Content = $"eigener Wert {Zaehlung.WertText(zw)}", Tag = zw });
            cbZaehl.SelectedItem = itemStandard;
            // Hat die angeklickte Stunde einen Sondergrund, ihn als Vorgabe zeigen.
            var sgVorgabe = z.Eintrag != null && z.Eintrag.Sondergrund ? z.Eintrag : null;
            if (sgVorgabe != null)
                cbZaehl.SelectedItem = cbZaehl.Items.OfType<ComboBoxItem>()
                                           .FirstOrDefault(i => i.Tag is int iw && iw == sgVorgabe.ZaehlwertSnapshot) ?? itemStandard;
            zeileZaehl.Children.Add(cbZaehl);
            zeileZaehl.Children.Add(Beschriftung("Kommentar:"));
            var txtZaehlKomm = new TextBox
            {
                Width = 300, MaxLength = Zaehlung.KommentarMax, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
                Text = sgVorgabe?.Zaehlkommentar ?? "",
                ToolTip = "Pflicht bei einem eigenen Zählwert (eine Zeile, bis 200 Zeichen)"
            };
            zeileZaehl.Children.Add(txtZaehlKomm);
            oben.Children.Add(zeileZaehl);
            oben.Children.Add(new TextBlock
            {
                Text = Zaehlung.KommentarHinweis + " Ein eigener Wert gilt als Sondergrund jeder Stunde der Serie.",
                Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            });

            // V4b (287, 296): ausfallende Lehrkraft – Anlass und eigener Wert für alle Stunden der Serie (nur, wenn eine
            // andere Lehrkraft die Stunde hält: Vertretung, Betreuung/Aufsicht).
            var ausfallVorgabe = z.Eintrag != null && Zaehlung.HatAusfall(z.Eintrag) ? z.Eintrag : null;
            var panelAusfall = new StackPanel();
            var zeileAusfall = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            zeileAusfall.Children.Add(Beschriftung($"Ausfall {z.OriginalLehrer}:"));
            var cbAusfall = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 14, 0) };
            var itemFehlt = new ComboBoxItem { Content = "fehlt (0)", Tag = AusfallAnlass.Lehrkraft };
            var itemNichtV = new ComboBoxItem
            {
                Content = "nicht verhindert (−1)", Tag = AusfallAnlass.NichtVerhindert,
                ToolTip = "Die Lehrkraft war gar nicht verhindert und hätte eigentlich einen Entfall gehabt."
            };
            cbAusfall.Items.Add(itemFehlt);
            cbAusfall.Items.Add(itemNichtV);
            cbAusfall.SelectedItem = ausfallVorgabe?.AnlassAusfall == AusfallAnlass.NichtVerhindert ? itemNichtV : itemFehlt;
            zeileAusfall.Children.Add(cbAusfall);
            var cbAusfallWert = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 14, 0) };
            var itemAusfallStd = new ComboBoxItem { Content = "Zählung laut Anlass", Tag = null };
            cbAusfallWert.Items.Add(itemAusfallStd);
            foreach (int zw in Zaehlung.EigeneWerte)
                cbAusfallWert.Items.Add(new ComboBoxItem { Content = $"eigener Wert {Zaehlung.WertText(zw)}", Tag = zw });
            cbAusfallWert.SelectedItem = ausfallVorgabe != null && ausfallVorgabe.SondergrundAusfall
                ? cbAusfallWert.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int iwa && iwa == ausfallVorgabe.ZaehlwertAusfall) ?? itemAusfallStd
                : itemAusfallStd;
            zeileAusfall.Children.Add(cbAusfallWert);
            panelAusfall.Children.Add(zeileAusfall);
            var zeileAusfallKomm = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            zeileAusfallKomm.Children.Add(Beschriftung("Kommentar (Ausfall):"));
            var txtAusfallKomm = new TextBox
            {
                Width = 360, MaxLength = Zaehlung.KommentarMax, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
                Text = ausfallVorgabe != null && ausfallVorgabe.SondergrundAusfall ? ausfallVorgabe.KommentarAusfall ?? "" : "",
                ToolTip = "Pflicht bei einem eigenen Wert der ausfallenden Lehrkraft (eine Zeile, bis 200 Zeichen) – keine Gesundheitsangaben"
            };
            zeileAusfallKomm.Children.Add(txtAusfallKomm);
            panelAusfall.Children.Add(zeileAusfallKomm);
            oben.Children.Add(panelAusfall);

            // Raum (R3-D: neuer Raum wird mitgenommen)
            string? neuerRaum = z.RaumGeaendert && !string.IsNullOrWhiteSpace(z.Raum) ? z.Raum!.Trim() : null;
            var chkRaum = new CheckBox
            {
                Content = $"Neuen Raum {neuerRaum} an allen Tagen übernehmen (wenn er dort frei ist)",
                IsChecked = true, Margin = new Thickness(0, 0, 0, 4),
                Visibility = neuerRaum != null ? Visibility.Visible : Visibility.Collapsed
            };
            oben.Children.Add(chkRaum);
            var chkErsetzen = new CheckBox
            {
                Content = "Bestehende Buchungen dieser Stunde ersetzen (z. B. Entfall, andere Vertretung)",
                IsChecked = true, Margin = new Thickness(0, 0, 0, 4)
            };
            oben.Children.Add(chkErsetzen);

            if (einst.QuelleIstUntis)
                oben.Children.Add(new TextBlock
                {
                    Text = "⚠ Unter „Server verbinden…“ ist „Untis“ als Quelle der Vertretungen eingestellt – beim " +
                           "Veröffentlichen wird nachgefragt; veröffentlichte Tage ersetzen dort die Untis-Vertretungen.",
                    Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
                });

            var txtSumme = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };
            oben.Children.Add(txtSumme);
            Grid.SetRow(oben, 0); root.Children.Add(oben);

            var liste = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
            ScrollViewer.SetHorizontalScrollBarVisibility(liste, ScrollBarVisibility.Disabled);
            Grid.SetRow(liste, 1); root.Children.Add(liste);

            var knoepfe = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnBuchen = new Button { Content = "Buchen", Width = 120, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnAbbr = new Button { Content = "Abbrechen", Width = 110, IsCancel = true };
            knoepfe.Children.Add(btnBuchen); knoepfe.Children.Add(btnAbbr);
            Grid.SetRow(knoepfe, 2); root.Children.Add(knoepfe);
            win.Content = root;

            DauerVorlage? vorlage = null;
            List<DauerTag> tage = new();
            string serienId = Dauervertretung.NeueSerienId();

            Vertretungsart ArtGewaehlt()
                => cbArt.SelectedItem is ComboBoxItem it && it.Tag is Vertretungsart a ? a : Vertretungsart.Vertretung;

            int? EigenerWert()
                => cbZaehl.SelectedItem is ComboBoxItem zi && zi.Tag is int ew ? ew : (int?)null;

            int? AusfallEigenerWert()
                => cbAusfallWert.SelectedItem is ComboBoxItem ai && ai.Tag is int aw ? aw : (int?)null;

            AusfallAnlass AusfallGewaehlt()
                => cbAusfall.SelectedItem is ComboBoxItem ci && ci.Tag is AusfallAnlass aa ? aa : AusfallAnlass.Lehrkraft;

            void Aktualisiere()
            {
                var art = ArtGewaehlt();
                bool braucht = Dauervertretung.BrauchtVertreter(art);
                cbVertreter.IsEnabled = braucht;
                chkRaum.IsEnabled = art != Vertretungsart.Entfall;
                // V4b (296): „Ausfall“ nur, wenn eine andere Lehrkraft die Stunde hält
                panelAusfall.Visibility = braucht ? Visibility.Visible : Visibility.Collapsed;
                int? ausfallEigen = braucht ? AusfallEigenerWert() : null;
                txtAusfallKomm.IsEnabled = ausfallEigen != null;
                liste.Items.Clear();
                tage = new();
                vorlage = null;
                btnBuchen.IsEnabled = false;

                if (dpVon.SelectedDate is not DateTime von || dpBis.SelectedDate is not DateTime bis || bis.Date < von.Date)
                { txtSumme.Text = "Zeitraum ungültig (von ≤ bis)."; return; }
                string? vertreter = (cbVertreter.SelectedItem as VertreterWahl)?.Lehrer;
                // V4: Standardwert der gewählten Art (am angeklickten Tag) in der Auswahl „Zählung“ zeigen.
                int std = Zaehlung.StandardWert(art, art == Vertretungsart.Entfall ? (EntfallAnlass?)EntfallAnlass.Lehrkraft : null,
                    braucht ? vertreter : null, datum, z.Stunde, _daten, plan);
                itemStandard.Content = $"Standard ({Zaehlung.WertText(std)})";
                int? eigenWahl = EigenerWert();
                txtZaehlKomm.IsEnabled = eigenWahl != null;
                if (braucht && vertreter == null)
                { txtSumme.Text = $"Bitte wählen, wer die {Dauervertretung.ArtText(art)} übernimmt."; return; }

                vorlage = new DauerVorlage
                {
                    WTag = wtag, Stunde = z.Stunde, UNr = z.UNr, OriginalLehrer = z.OriginalLehrer,
                    Fach = z.Fach, Klassen = new List<string>(z.Klassen),
                    Art = art, Vertreter = braucht ? vertreter : null,
                    Grund = cbGrund.SelectedItem is VertretungsGrund gr ? gr : VertretungsGrund.Sonstiges,
                    Raum = neuerRaum != null && chkRaum.IsChecked == true && art != Vertretungsart.Entfall ? neuerRaum : null,
                    Ersetzen = chkErsetzen.IsChecked == true,
                    Von = von.Date, Bis = bis.Date, SerienId = serienId,
                    EigenerZaehlwert = eigenWahl,
                    Zaehlkommentar = eigenWahl != null ? Zaehlung.Bereinige(txtZaehlKomm.Text) : "",
                    Ausfall = AusfallGewaehlt(),
                    AusfallEigenerWert = ausfallEigen,
                    AusfallKommentar = ausfallEigen != null ? Zaehlung.Bereinige(txtAusfallKomm.Text) : ""
                };
                tage = Dauervertretung.Vorschau(vorlage, plan, _daten, AusUntis);

                foreach (var t in tage)
                {
                    var tb = new TextBlock
                    {
                        Text = $"{UntisText.Tag(t.Datum)} {t.Datum:dd.MM.yyyy}   {t.Text}" + (t.AusUntis ? "  · zuletzt aus Untis veröffentlicht" : ""),
                        TextWrapping = TextWrapping.Wrap
                    };
                    if (t.IstLuecke) { tb.Foreground = DauerLueckeBrush; tb.FontWeight = FontWeights.SemiBold; }
                    else if (t.Status == DauerTagStatus.Behalten || t.Status == DauerTagStatus.Verlegt) tb.Foreground = DauerBehaltenBrush;
                    else if (!t.WirdGebucht) tb.Foreground = Brushes.Gray;
                    liste.Items.Add(new ListBoxItem { Content = tb });
                }

                int n = tage.Count(t => t.WirdGebucht);
                int luecken = tage.Count(t => t.IstLuecke);
                int frei = tage.Count(t => t.Status == DauerTagStatus.Schulfrei);
                int bleibt = tage.Count(t => t.Status == DauerTagStatus.Behalten || t.Status == DauerTagStatus.Verlegt
                                             || t.Status == DauerTagStatus.KeineStunde);
                txtSumme.Text = tage.Count == 0
                    ? $"Im Zeitraum liegt kein {UntisText.Tag(datum)}."
                    : $"{n} Stunde(n) werden gebucht" +
                      (eigenWahl != null ? $" (Zählung {Zaehlung.WertText(eigenWahl.Value)}, Sondergrund)" : "") +
                      (braucht ? $", Ausfall {z.OriginalLehrer}: " + (ausfallEigen != null
                          ? $"{Zaehlung.WertText(ausfallEigen.Value)} (Sondergrund)"
                          : $"{Zaehlung.AusfallKurz(AusfallGewaehlt())} ({Zaehlung.WertText(Zaehlung.AusfallWert(AusfallGewaehlt()))})") : "") +
                      (luecken > 0 ? $", {luecken} Lücke(n)" : "") +
                      (bleibt > 0 ? $", {bleibt} bleiben unverändert" : "") +
                      (frei > 0 ? $", {frei} schulfrei" : "") + ".";
                btnBuchen.IsEnabled = n > 0;
            }

            cbArt.SelectionChanged += (_, __) => Aktualisiere();
            cbZaehl.SelectionChanged += (_, __) => Aktualisiere();
            cbAusfall.SelectionChanged += (_, __) => Aktualisiere();
            cbAusfallWert.SelectionChanged += (_, __) => Aktualisiere();
            cbVertreter.SelectionChanged += (_, __) => Aktualisiere();
            cbGrund.SelectionChanged += (_, __) => Aktualisiere();
            chkRaum.Checked += (_, __) => Aktualisiere(); chkRaum.Unchecked += (_, __) => Aktualisiere();
            chkErsetzen.Checked += (_, __) => Aktualisiere(); chkErsetzen.Unchecked += (_, __) => Aktualisiere();
            dpVon.SelectedDateChanged += (_, __) =>
            {
                if (dpVon.SelectedDate is DateTime v && dpBis.SelectedDate is DateTime b && b.Date < v.Date) dpBis.SelectedDate = v.Date;
                Aktualisiere();
            };
            dpBis.SelectedDateChanged += (_, __) => Aktualisiere();
            btnFerien.Click += (_, __) =>
                dpBis.SelectedDate = Dauervertretung.BisFerienbeginn(_daten.Kalender, dpVon.SelectedDate ?? datum);
            btnEnde.Click += (_, __) =>
                dpBis.SelectedDate = Dauervertretung.BisSchuljahresende(_daten.Kalender, dpVon.SelectedDate ?? datum);
            Aktualisiere();

            btnBuchen.Click += (_, __) =>
            {
                Aktualisiere();   // mit dem aktuellen Stand der Buchungen
                if (vorlage == null || !tage.Any(t => t.WirdGebucht)) return;
                // V4 (277): eigener Zählwert nur mit Kommentar.
                if (vorlage.EigenerZaehlwert != null && Zaehlung.KommentarFehler(txtZaehlKomm.Text) is string kf)
                {
                    txtSumme.Text = kf + " Nichts gebucht.";
                    txtZaehlKomm.Focus();
                    return;
                }
                // V4b (287): ebenso beim eigenen Wert der ausfallenden Lehrkraft.
                if (vorlage.BrauchtVertreter && vorlage.AusfallEigenerWert != null
                    && Zaehlung.KommentarFehler(txtAusfallKomm.Text) is string kfa)
                {
                    txtSumme.Text = "Ausfall: " + kfa + " Nichts gebucht.";
                    txtAusfallKomm.Focus();
                    return;
                }
                win.DialogResult = true;
            };

            if (win.ShowDialog() != true || vorlage == null) return;
            BucheDauer(vorlage, tage);
        }

        private void BucheDauer(DauerVorlage v, List<DauerTag> tage)
        {
            if (_plan == null) return;
            var gebucht = new List<DateTime>();
            foreach (var t in tage.Where(x => x.WirdGebucht && x.Zelle != null))
            {
                var e = Dauervertretung.Eintrag(v, t, _plan, _daten);
                EntferneEintraege(t.Datum, t.Zelle!.Stunde, t.Zelle.UNr, t.Zelle.OriginalLehrer);
                _daten.Vertretungen.Add(e);
                gebucht.Add(t.Datum.Date);
            }
            if (gebucht.Count == 0) { SetzeInfo("Nichts gebucht."); return; }

            bool ok = Speichere();
            Zeichne();

            DateTime erster = gebucht.Min(), letzter = gebucht.Max();
            string wer = (v.BrauchtVertreter ? $"{Dauervertretung.ArtText(v.Art)} {v.Vertreter}" : Dauervertretung.ArtText(v.Art))
                         + (v.EigenerZaehlwert is int ez ? $", Zählung {Zaehlung.WertText(ez)} (Sondergrund)" : "")
                         + (v.BrauchtVertreter
                             ? ", Ausfall " + (v.AusfallEigenerWert is int eaw ? $"{Zaehlung.WertText(eaw)} (Sondergrund)"
                                                 : $"{Zaehlung.AusfallKurz(v.Ausfall)} ({Zaehlung.WertText(Zaehlung.AusfallWert(v.Ausfall))})")
                             : "");
            string kurz = $"Dauervertretung {UntisText.Tag(erster)} {v.Stunde}. Std {v.Fach} {string.Join(", ", v.Klassen)} " +
                          $"({v.OriginalLehrer} → {wer}): {gebucht.Count} Stunde(n), {erster:dd.MM.}–{letzter:dd.MM.yyyy}";
            var luecken = tage.Where(t => t.IstLuecke).ToList();
            SetzeInfo(kurz + (luecken.Count > 0 ? $", {luecken.Count} Lücke(n)." : "."));
            _log?.Invoke($"Wocheneditor: {kurz}, {luecken.Count} Lücke(n).");
            if (!ok) return;

            string meldung = kurz + ".";
            if (luecken.Count > 0)
                meldung += $"\n\n{luecken.Count} Lücke(n) – dort bleibt die Stunde offen (bitte einzeln vertreten):\n" +
                           string.Join("\n", luecken.Take(10).Select(t => $"• {UntisText.Tag(t.Datum)} {t.Datum:dd.MM.}: {t.Text}")) +
                           (luecken.Count > 10 ? $"\n• … {luecken.Count - 10} weitere" : "");
            BieteVeroeffentlichenAn(meldung, erster, letzter, "Dauervertretung");
        }

        /// <summary>Nach dem Buchen bzw. Zurücksetzen: den betroffenen Zeitraum veröffentlichen? (Entscheidung 161)</summary>
        private void BieteVeroeffentlichenAn(string meldung, DateTime von, DateTime bis, string titel)
        {
            string frage = meldung + $"\n\nJetzt den Zeitraum {von:dd.MM.yyyy} bis {bis:dd.MM.yyyy} in der App veröffentlichen?\n" +
                           "(Das Fenster „Zeitraum veröffentlichen“ zeigt vorher die Schultage und fragt nach.)";
            if (MessageBox.Show(this, frage, titel, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
                == MessageBoxResult.Yes)
                ZeigeZeitraumFenster(von, bis);
        }

        private static TextBlock Beschriftung(string text)
            => new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

        // =====================================================
        //  Fenster „Künftige Vertretungen zurücksetzen“ (Entscheidung 162)
        // =====================================================

        private void BtnZuruecksetzen_Click(object sender, RoutedEventArgs e)
        {
            if (!_bereit || _plan == null) return;
            DateTime ab = DpTag.SelectedDate?.Date ?? DateTime.Today;
            ZeigeZuruecksetzenFenster(ab, null, CbLehrer.SelectedItem as string);
        }

        private void ZeigeZuruecksetzenFenster(DateTime abVorgabe, string? serienVorgabe, string? lehrerVorgabe)
        {
            if (!_bereit || _plan == null) return;

            var win = new Window
            {
                Title = "Künftige Vertretungen zurücksetzen",
                Width = 700, Height = 640, MinWidth = 560, MinHeight = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.CanResizeWithGrip
            };
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var oben = new StackPanel();
            oben.Children.Add(new TextBlock
            {
                Text = "Entfernt Buchungen ab einem Datum – z. B. wenn eine Lehrkraft früher zurück ist. Es werden nur " +
                       "die Buchungen der gewählten Dauervertretung bzw. der gewählten Lehrkraft entfernt; die Vorschau " +
                       "zeigt alle. Bei einer Lehrkraft bleiben Verlegungen und reine Raumwechsel.",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10)
            });

            var zeileAb = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            zeileAb.Children.Add(Beschriftung("Ab (einschließlich):"));
            var dpAb = new DatePicker { SelectedDate = abVorgabe.Date, Width = 130 };
            zeileAb.Children.Add(dpAb);
            oben.Children.Add(zeileAb);

            var rbSerie = new RadioButton { Content = "Eine Dauervertretung:", GroupName = "v3reset", Margin = new Thickness(0, 0, 0, 4) };
            oben.Children.Add(rbSerie);
            var cbSerie = new ComboBox { Margin = new Thickness(20, 0, 0, 8) };
            oben.Children.Add(cbSerie);

            var rbLehrer = new RadioButton { Content = "Alle Buchungen einer Lehrkraft:", GroupName = "v3reset", Margin = new Thickness(0, 0, 0, 4) };
            oben.Children.Add(rbLehrer);
            var zeileLehrer = new WrapPanel { Margin = new Thickness(20, 0, 0, 8) };
            var cbLehrer = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 12, 0) };
            foreach (var l in _plan.AlleLehrer.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) cbLehrer.Items.Add(l);
            cbLehrer.SelectedItem = cbLehrer.Items.OfType<string>().FirstOrDefault(x =>
                string.Equals(x, lehrerVorgabe, StringComparison.OrdinalIgnoreCase));
            zeileLehrer.Children.Add(cbLehrer);
            var chkAbwesend = new CheckBox { Content = "für sie gebucht (sie fehlt)", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var chkVertreter = new CheckBox { Content = "von ihr gehalten (als Vertretung)", IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
            zeileLehrer.Children.Add(chkAbwesend);
            zeileLehrer.Children.Add(chkVertreter);
            oben.Children.Add(zeileLehrer);

            var chkAbw = new CheckBox { IsChecked = true, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
            oben.Children.Add(chkAbw);

            var txtSumme = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };
            oben.Children.Add(txtSumme);
            Grid.SetRow(oben, 0); root.Children.Add(oben);

            var liste = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(liste, 1); root.Children.Add(liste);

            var knoepfe = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnLos = new Button { Content = "Zurücksetzen…", Width = 130, Margin = new Thickness(0, 0, 8, 0) };
            var btnZu = new Button { Content = "Schließen", Width = 110, IsCancel = true };
            knoepfe.Children.Add(btnLos); knoepfe.Children.Add(btnZu);
            Grid.SetRow(knoepfe, 2); root.Children.Add(knoepfe);
            win.Content = root;

            List<VertretungsEintrag> treffer = new();
            List<Abwesenheit> abw = new();
            // Ergebnis für das Angebot „Zeitraum veröffentlichen“ nach dem Schließen des Fensters
            string? ergebnisText = null;
            DateTime? ergebnisVon = null, ergebnisBis = null;
            string? abwLehrer = null;
            bool laedt = false;

            void FuelleSerien()
            {
                laedt = true;
                string? alt = (cbSerie.SelectedItem as DauerSerie)?.SerienId ?? serienVorgabe;
                DateTime ab = dpAb.SelectedDate?.Date ?? abVorgabe.Date;
                cbSerie.Items.Clear();
                foreach (var s in Dauervertretung.Serien(_daten.Vertretungen).Where(x => x.Letzter >= ab))
                    cbSerie.Items.Add(s);
                cbSerie.SelectedItem = cbSerie.Items.OfType<DauerSerie>().FirstOrDefault(s =>
                                           string.Equals(s.SerienId, alt, StringComparison.OrdinalIgnoreCase))
                                       ?? cbSerie.Items.OfType<DauerSerie>().FirstOrDefault();
                bool gibt = cbSerie.Items.Count > 0;
                rbSerie.IsEnabled = gibt;
                rbSerie.Content = gibt ? "Eine Dauervertretung:" : "Eine Dauervertretung: (keine ab diesem Tag)";
                if (!gibt && rbSerie.IsChecked == true) rbLehrer.IsChecked = true;
                laedt = false;
            }

            void Aktualisiere()
            {
                if (laedt) return;
                liste.Items.Clear();
                treffer = new();
                abw = new();
                abwLehrer = null;
                cbSerie.IsEnabled = rbSerie.IsChecked == true;
                cbLehrer.IsEnabled = chkAbwesend.IsEnabled = chkVertreter.IsEnabled = rbLehrer.IsChecked == true;

                if (dpAb.SelectedDate is not DateTime ab) { txtSumme.Text = "Bitte ein Datum wählen."; btnLos.IsEnabled = false; return; }
                ab = ab.Date;

                if (rbSerie.IsChecked == true && cbSerie.SelectedItem is DauerSerie s)
                {
                    treffer = Dauervertretung.ZumZuruecksetzen(_daten.Vertretungen, ab, s.SerienId, null, false, false);
                    abwLehrer = s.OriginalLehrer;
                }
                else if (rbLehrer.IsChecked == true && cbLehrer.SelectedItem is string l)
                {
                    treffer = Dauervertretung.ZumZuruecksetzen(_daten.Vertretungen, ab, null, l,
                        chkAbwesend.IsChecked == true, chkVertreter.IsChecked == true);
                    if (chkAbwesend.IsChecked == true) abwLehrer = l;
                }

                if (abwLehrer != null) abw = Dauervertretung.AbwesenheitenAb(_daten.Abwesenheiten, abwLehrer, ab);
                if (abw.Count > 0)
                {
                    chkAbw.Content = $"Abwesenheit(en) von {abwLehrer} am {ab.AddDays(-1):dd.MM.} enden lassen (früher zurück): " +
                                     string.Join(", ", abw.Select(a => $"{a.VonDatum:dd.MM.}–{a.BisDatum:dd.MM.yyyy}"));
                    chkAbw.Visibility = Visibility.Visible;
                }
                else chkAbw.Visibility = Visibility.Collapsed;

                foreach (var e in treffer)
                    liste.Items.Add(new ListBoxItem { Content = Dauervertretung.ZeileText(e) });

                bool abwAendern = abw.Count > 0 && chkAbw.IsChecked == true;
                txtSumme.Text = treffer.Count == 0
                    ? $"Ab {ab:dd.MM.yyyy} gibt es keine passenden Buchungen." + (abwAendern ? " (Nur die Abwesenheit wird geändert.)" : "")
                    : $"{treffer.Count} Buchung(en) ab {ab:dd.MM.yyyy} werden entfernt " +
                      $"({treffer.Min(x => x.Datum):dd.MM.}–{treffer.Max(x => x.Datum):dd.MM.yyyy}).";
                btnLos.IsEnabled = treffer.Count > 0 || abwAendern;
            }

            if (serienVorgabe != null) rbSerie.IsChecked = true; else rbLehrer.IsChecked = true;
            FuelleSerien();
            Aktualisiere();

            dpAb.SelectedDateChanged += (_, __) => { FuelleSerien(); Aktualisiere(); };
            rbSerie.Checked += (_, __) => Aktualisiere();
            rbLehrer.Checked += (_, __) => Aktualisiere();
            cbSerie.SelectionChanged += (_, __) => Aktualisiere();
            cbLehrer.SelectionChanged += (_, __) => Aktualisiere();
            chkAbwesend.Checked += (_, __) => Aktualisiere(); chkAbwesend.Unchecked += (_, __) => Aktualisiere();
            chkVertreter.Checked += (_, __) => Aktualisiere(); chkVertreter.Unchecked += (_, __) => Aktualisiere();
            chkAbw.Checked += (_, __) => Aktualisiere(); chkAbw.Unchecked += (_, __) => Aktualisiere();

            btnLos.Click += (_, __) =>
            {
                Aktualisiere();
                if (dpAb.SelectedDate is not DateTime ab) return;
                bool abwAendern = abw.Count > 0 && chkAbw.IsChecked == true;
                if (treffer.Count == 0 && !abwAendern) return;

                string frage = treffer.Count > 0
                    ? $"{treffer.Count} Buchung(en) ab {ab:dd.MM.yyyy} entfernen?\n\n" +
                      string.Join("\n", treffer.Take(12).Select(x => "• " + Dauervertretung.ZeileText(x))) +
                      (treffer.Count > 12 ? $"\n• … {treffer.Count - 12} weitere" : "")
                    : "Keine Buchungen zu entfernen.";
                if (abwAendern)
                    frage += $"\n\nDie Abwesenheit(en) von {abwLehrer} enden am {ab.AddDays(-1):dd.MM.yyyy}.";
                if (MessageBox.Show(win, frage, "Zurücksetzen", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                        MessageBoxResult.Cancel) != MessageBoxResult.OK)
                    return;

                var weg = new HashSet<VertretungsEintrag>(treffer, ReferenceEqualityComparer.Instance);
                int n = _daten.Vertretungen.RemoveAll(x => weg.Contains(x));
                int nAbw = abwAendern ? Dauervertretung.BeendeAbwesenheiten(_daten.Abwesenheiten, abw, ab.Date) : 0;
                DateTime? von = treffer.Count > 0 ? treffer.Min(x => x.Datum).Date : null;
                DateTime? bis = treffer.Count > 0 ? treffer.Max(x => x.Datum).Date : null;
                string wer = rbSerie.IsChecked == true ? "der Dauervertretung" : $"von {cbLehrer.SelectedItem}";

                bool ok = Speichere();
                Zeichne();
                string kurz = $"{n} Buchung(en) {wer} ab {ab:dd.MM.yyyy} entfernt" + (nAbw > 0 ? $", {nAbw} Abwesenheit(en) beendet" : "");
                SetzeInfo(kurz + ".");
                _log?.Invoke($"Wocheneditor: {kurz}.");
                if (ok && von != null && bis != null) { ergebnisText = kurz + "."; ergebnisVon = von; ergebnisBis = bis; }
                win.DialogResult = true;
            };

            win.ShowDialog();
            if (ergebnisText != null && ergebnisVon != null && ergebnisBis != null)
                BieteVeroeffentlichenAn(ergebnisText, ergebnisVon.Value, ergebnisBis.Value, "Zurücksetzen");
        }
    }
}
