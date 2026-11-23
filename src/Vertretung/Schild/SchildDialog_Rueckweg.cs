#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// P4b (5.3a, Entscheidungen 210–213): Reiter „Rückweg“ – Fehlstunden mit dem Abholcode aus der App abholen (Push-Schlüssel
    /// + Code; nur Prüfsummen, keine Namen), über den Schlüssel dieses Windows-Benutzers den Schülern des SchILD-Ordners
    /// zuordnen, Vorschau zeigen und SchuelerFehlstunden.dat in einen neuen Ordner schreiben (Probedatei: Klassen/Stufen,
    /// kürzerer Zeitraum). Geschriebene Zeiträume merkt sich schild_rueckweg.json (ohne Personendaten); die App bekommt eine
    /// Meldung ohne Namen. Abgeholte Daten und Vorschau liegen nur im Arbeitsspeicher und werden beim Schließen freigegeben.
    /// </summary>
    public partial class SchildDialog
    {
        private SchildRueckwegAbruf? _rwAbruf;
        private SchildRueckwegErgebnis? _rwErgebnis;
        private SchildSchluessel? _rwSchluessel;
        private string? _rwBericht;
        private string? _rwGeschrieben;      // Pfad der zuletzt geschriebenen Datei (für das Band)
        private bool _rwSetzen;

        private const string RwHilfe =
            "Rückweg nach SchILD (P4b)\n\n" +
            "1. App (Admin): Jahresablauf → „Rückweg nach SchILD“ → „Fehlstunden für SchILD bereitstellen…“ → Zeitraum → " +
            "„Abholcode erzeugen“ (8 Zeichen, 15 Minuten, ein Abruf).\n" +
            "2. Hier: den aktuellen SchILD-Export wählen und prüfen, den Abschnitt wählen (1. bzw. 2. Halbjahr), den Code eingeben → " +
            "„Abrufen“. Die App liefert nur Prüfsummen – Namen, Geburtsdatum und Klasse kommen aus diesem Ordner.\n" +
            "3. Vorschau prüfen, ggf. Probedatei (Klassen/Stufen, kürzerer Zeitraum) → „Datei schreiben…“ → in SchILD von Hand " +
            "importieren.\n\n" +
            "Übertragen: entschuldigt (J, auch beurlaubt) und unentschuldigt (N). Nicht: offen, Verspätungen, schulische " +
            "Veranstaltung. Fach und Lehrkraft über die Fächer- und Kürzeltabelle (Reiter „Zuordnung“).\n\n" +
            "Vor der ersten echten Übertragung: SchILD-Datenbank sichern, Probedatei für eine Klasse und wenige Tage importieren, " +
            "mit der Konferenzübersicht der App vergleichen und prüfen, ob ein zweiter Import desselben Zeitraums doppelt zählt. Bis " +
            "dahin bleibt die Fehlzeiten-CSV der App der sichere Weg.";

        // =====================================================
        //  Zustand und Anzeige
        // =====================================================

        /// <summary>null = bereit; sonst der Grund, warum (noch) nichts abgeholt bzw. zugeordnet werden kann.</summary>
        private string? RwVoraussetzung()
        {
            if (_export == null || _ergebnis == null)
                return "Zuerst den aktuellen SchILD-Export wählen und den Prüflauf starten (Namen, Geburtsdaten und Klassen kommen " +
                       "aus diesem Ordner).";
            int f = _ergebnis.Anzahl(SchildProblemArt.Fehler);
            if (f > 0) return $"Der Prüflauf hat {f} Fehler (Reiter „Probleme“) – erst beheben.";
            if (_planInfo == null)
                return "Kein Plan verfügbar – der Rückweg braucht die Fächer- und Kürzeltabelle (Grundplan der im Wocheneditor " +
                       "gewählten Plan-Spalte).";
            if (_planInfo.Hinweis != null) return "Zuordnung nicht möglich: " + _planInfo.Hinweis;
            var zu = _ergebnis.Zuordnung;
            if (zu == null || !zu.Moeglich)
                return "Ohne Zuordnung SchILD ↔ Plan (Fächer, Kürzel) ist kein Rückweg möglich (Reiter „Zuordnung“).";
            return null;
        }

        private void RwBand(string text, Color farbe)
        {
            TxtRwHinweis.Text = text;
            BrdRwHinweis.Background = new SolidColorBrush(farbe);
        }

        private void RwAnzeigen()
        {
            if (TxtRwHinweis == null || BtnRwAbrufen == null) return;
            string? vor = RwVoraussetzung();
            bool an = !_laeuft;
            var r = _rwErgebnis;
            string ungespeichert = _zuordnungGeaendert
                ? "\n(Die Zuordnung hat ungespeicherte Festlegungen – sie gelten auch hier; bitte „Zuordnungen speichern“.)" : "";

            if (vor != null)
            {
                RwBand(vor, FarbeNeutral);
                TxtRwKopf.Text = "Rückweg";
            }
            else if (_rwAbruf == null)
            {
                RwBand($"Bereit (Abschnitt {_ergebnis!.Abschnitt} von {_ergebnis.Jahr}/{(_ergebnis.Jahr + 1) % 100:00}, " +
                       $"{SchildSchluessel.ArtText(_export!.IstPseudo)}). In der App (Admin) unter Jahresablauf → „Rückweg nach " +
                       "SchILD“ einen Abholcode erzeugen und hier eingeben → „Abrufen“." + ungespeichert, FarbeNeutral);
                TxtRwKopf.Text = "Rückweg";
            }
            else if (r == null)
            {
                RwBand("Abgeholt – die Zuordnung zu den Schülern des Ordners ist nicht möglich (siehe Bericht).", FarbeRot);
                TxtRwKopf.Text = "Rückweg (gesperrt)";
            }
            else if (r.Sperren.Count > 0)
            {
                RwBand("Keine Datei möglich:\n• " + string.Join("\n• ", r.Sperren), FarbeRot);
                TxtRwKopf.Text = "Rückweg (gesperrt)";
            }
            else if (_rwGeschrieben != null)
            {
                RwBand($"Datei geschrieben: {_rwGeschrieben}\nJetzt in SchILD von Hand importieren. Für eine weitere Datei (z. B. " +
                       "nach einer Probedatei das ganze Halbjahr) Auswahl ändern und noch einmal „Datei schreiben…“.", FarbeGruen);
                TxtRwKopf.Text = "Rückweg ✓";
            }
            else if (r.Zeilen.Count == 0)
            {
                RwBand("Keine Fehlstunde für die Datei (Auswahl, Abschnitt und Zeitraum prüfen – Gründe im Bericht)." + ungespeichert,
                       FarbeGelb);
                TxtRwKopf.Text = "Rückweg (leer)";
            }
            else
            {
                RwBand((r.Probe ? "PROBEDATEI – " : "") +
                       $"Vorschau: {r.Fehlstunden} Fehlstunden (J {r.J}, N {r.N}) von {r.Schueler} Schülern in {r.Zeilen.Count} " +
                       $"Zeilen · Jahr {r.Jahr}, Abschnitt {r.Abschnitt}, {SchildText.DatumText(r.Von)} – " +
                       $"{SchildText.DatumText(r.Bis)}, {r.KlassenText}." +
                       (r.Warnungen.Count > 0 ? $" {r.Warnungen.Count} Hinweis(e) im Bericht." : "") +
                       " Prüfen, dann „Datei schreiben…“." + ungespeichert, FarbeGelb);
                TxtRwKopf.Text = r.Probe ? "Rückweg (Probedatei)" : "Rückweg (Vorschau)";
            }

            BtnRwAbrufen.IsEnabled = an && vor == null && RwCodeNorm().Length == 8;
            BtnRwSchreiben.IsEnabled = an && vor == null && r != null && r.Sperren.Count == 0 && r.Zeilen.Count > 0;
            BtnRwBericht.IsEnabled = an && _rwBericht != null;
            bool wahl = an && _rwAbruf != null && vor == null;
            LbRwKlassen.IsEnabled = wahl;
            DpRwVon.IsEnabled = wahl;
            DpRwBis.IsEnabled = wahl;
            BtnRwAlle.IsEnabled = wahl;
            TxtRwCode.IsEnabled = an;
            RwZeigeMerkliste();
        }

        private string RwCodeNorm()
            => new string((TxtRwCode.Text ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

        private void RwZeigeMerkliste()
        {
            if (_export == null) { TxtRwMerk.Text = ""; return; }
            var m = SchildRueckweg.MerklisteLaden(_planOrdner, _export.IstPseudo, out var fehler);
            var letzte = m.Eintraege.OrderByDescending(e => e.Am, StringComparer.Ordinal).Take(4).ToList();
            TxtRwMerk.Text = fehler ?? (letzte.Count == 0
                ? $"Bisher geschrieben ({SchildRueckweg.MerkName(_export.IstPseudo)}): noch nichts."
                : $"Bisher geschrieben ({SchildRueckweg.MerkName(_export.IstPseudo)}, ohne Personendaten): " +
                  string.Join(" · ", letzte.Select(e => e.Text)) + (m.Eintraege.Count > 4 ? $" · … ({m.Eintraege.Count} insgesamt)" : ""));
        }

        /// <summary>Klassen/Stufen des Abschnitts in die Auswahl der Probedatei (Auswahl bleibt, soweit vorhanden).</summary>
        private void RwKlassenFuellen()
        {
            var alt = new HashSet<string>(LbRwKlassen.SelectedItems.Cast<object>().Select(o => o.ToString() ?? ""),
                StringComparer.OrdinalIgnoreCase);
            _rwSetzen = true;
            try
            {
                LbRwKlassen.Items.Clear();
                if (_export != null && _ergebnis != null)
                    foreach (var g in SchildRueckweg.Gruppen(_export, _ergebnis.Jahr, _ergebnis.Abschnitt))
                    {
                        LbRwKlassen.Items.Add(g);
                        if (alt.Contains(g)) LbRwKlassen.SelectedItems.Add(g);
                    }
            }
            finally { _rwSetzen = false; }
        }

        /// <summary>Nach Prüflauf, anderem Abschnitt, neuer Festlegung oder Abruf: Zuordnung und Vorschau neu berechnen.</summary>
        private void RwBerechnen()
        {
            if (TxtRwBericht == null) return;
            _rwErgebnis = null;
            _rwBericht = null;
            _rwGeschrieben = null;
            DgRueckweg.ItemsSource = null;
            TxtRwAnzahl.Text = "";
            if (_rwAbruf == null || RwVoraussetzung() != null)
            {
                TxtRwBericht.Text = RwHilfe;
                RwAnzeigen();
                return;
            }
            var ex = _export!;
            var erg = _ergebnis!;
            var k = RwSchluessel(ex.IstPseudo, out string? kf);
            if (k == null)
            {
                TxtRwBericht.Text = (kf ?? "Kein Schlüssel.") + "\n\n" + RwHilfe;
                RwAnzeigen();
                return;
            }
            HashSet<string>? klassen = null;
            if (LbRwKlassen.SelectedItems.Count > 0)
                klassen = new HashSet<string>(LbRwKlassen.SelectedItems.Cast<object>().Select(o => o.ToString() ?? ""),
                    StringComparer.OrdinalIgnoreCase);
            var opt = new SchildRueckwegOptionen
            {
                Jahr = erg.Jahr,
                Abschnitt = erg.Abschnitt,
                Von = DpRwVon.SelectedDate is DateTime dv ? DateOnly.FromDateTime(dv) : null,
                Bis = DpRwBis.SelectedDate is DateTime db ? DateOnly.FromDateTime(db) : null,
                Klassen = klassen
            };
            var r = SchildRueckweg.Berechne(_rwAbruf, ex, erg.Zuordnung!, k, opt);
            bool utf8 = SchildRueckweg.Utf8(_profil, ex);
            if (r.Sperren.Count == 0)
            {
                SchildRueckweg.Text(r, _profil, out int nichtAnsi);
                r.NichtAnsi = nichtAnsi;
                if (!utf8 && nichtAnsi > 0)
                    r.Warnungen.Add($"{nichtAnsi} Zeilen enthalten Zeichen, die es in ANSI nicht gibt (werden „?“) – im Formatprofil " +
                                    "„Rueckweg“ → „Zeichensatz“: „utf-8“ setzen, wenn SchILD UTF-8 liest.");
            }
            _rwErgebnis = r;
            _rwBericht = SchildRueckweg.Bericht(_rwAbruf, r, SchildRueckweg.ZeichensatzText(utf8));
            TxtRwBericht.Text = _rwBericht;
            DgRueckweg.ItemsSource = r.Zeilen;
            if (r.Sperren.Count == 0)
                TxtRwAnzahl.Text = $"{r.Zeilen.Count} Zeilen · {r.Fehlstunden} Fehlstunden · {r.Schueler} Schüler";
            RwAnzeigen();
        }

        /// <summary>Gespeicherter Schlüssel dieses Windows-Benutzers (wie beim Import) – nie ein neuer.</summary>
        private SchildSchluessel? RwSchluessel(bool pseudo, out string? fehler)
        {
            fehler = null;
            if (_rwSchluessel != null && _rwSchluessel.Pseudo == pseudo) return _rwSchluessel;
            _rwSchluessel?.Vergessen();
            _rwSchluessel = null;
            if (!SchildSchluessel.Vorhanden(pseudo))
            {
                fehler = $"Für diesen Windows-Benutzer gibt es auf diesem Rechner keinen Schlüssel ({SchildSchluessel.ArtText(pseudo)}). " +
                         "Ohne den Schlüssel des Imports lassen sich die Fehlstunden keinem Schüler zuordnen. Abhilfe: Reiter " +
                         "„Import“ → „Schlüssel einlesen…“ mit der Sicherung.";
                return null;
            }
            _rwSchluessel = SchildSchluessel.Laden(pseudo, out fehler);
            return _rwSchluessel;
        }

        // =====================================================
        //  Knöpfe
        // =====================================================

        private void TxtRwCode_TextChanged(object sender, TextChangedEventArgs e) => RwAnzeigen();

        private void LbRwKlassen_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_rwSetzen || _laeuft) return;
            RwBerechnen();
        }

        private void DpRw_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_rwSetzen || _laeuft) return;
            RwBerechnen();
        }

        private void BtnRwAlle_Click(object sender, RoutedEventArgs e)
        {
            _rwSetzen = true;
            try
            {
                LbRwKlassen.UnselectAll();
                DpRwVon.SelectedDate = null;
                DpRwBis.SelectedDate = null;
            }
            finally { _rwSetzen = false; }
            RwBerechnen();
        }

        private void BtnRwBericht_Click(object sender, RoutedEventArgs e)
        {
            if (_rwBericht == null) return;
            try
            {
                Clipboard.SetText(_rwBericht);
                TxtStatus.Text = "Bericht des Rückwegs in die Zwischenablage kopiert (ohne Schülernamen).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Zwischenablage nicht verfügbar:\n\n" + ex.Message, "Bericht",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnRwAbrufen_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft) return;
            string? vor = RwVoraussetzung();
            if (vor != null)
            {
                MessageBox.Show(this, vor, "Rückweg nach SchILD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string code = RwCodeNorm();
            if (code.Length != 8)
            {
                MessageBox.Show(this, "Der Abholcode hat 8 Zeichen (z. B. „ABCD-EFGH“).", "Rückweg nach SchILD",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var ex = _export!;
            bool pseudo = ex.IstPseudo;
            var k = RwSchluessel(pseudo, out string? kf);
            if (k == null)
            {
                MessageBox.Show(this, kf ?? "Der Schlüssel ist nicht lesbar.", "Rückweg nach SchILD", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            var client = ImClient();
            if (client == null) return;
            var uri = client.AdresseUri!;
            if (!pseudo && uri.Scheme != Uri.UriSchemeHttps && !OnlineVerbindung.IstLokal(uri))
            {
                MessageBox.Show(this, $"Echte Fehlstunden werden nur über HTTPS abgeholt – die Server-Adresse „{client.Adresse}“ " +
                                      "beginnt mit http://. Bitte die https://-Adresse in die Online-Einstellungen eintragen.",
                    "Rückweg nach SchILD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? fehler = null;
            SchildRueckwegAbruf? a = null;
            ImStart("Frage den Status der API ab …");
            try
            {
                var st = await client.StatusAsync(ImSchuljahr(), _ergebnis!.Abschnitt);
                _imStatus = st;
                ImZeigeStatus(st, client.Adresse);
                if (st.Pseudo != pseudo)
                    fehler = st.Pseudo
                        ? "Die API läuft in der Entwicklung und gibt nur Pseudodaten heraus (Entscheidung 83) – für echte Daten den " +
                          "Server im Echtbetrieb verwenden bzw. hier einen Pseudo-Export wählen."
                        : "Die API läuft im Echtbetrieb und gibt keine Pseudodaten heraus – bitte den echten SchILD-Export wählen.";
                else if (st.KennungLetzterImport != null &&
                         !string.Equals(k.Kennung, st.KennungLetzterImport, StringComparison.OrdinalIgnoreCase))
                    fehler = $"Der Schlüssel dieses Rechners (Kennung {k.Kennung}) ist ein anderer als beim letzten Import (Kennung " +
                             $"{st.KennungLetzterImport}) – damit ließe sich keine Fehlstunde zuordnen. Abhilfe: Reiter „Import“ → " +
                             "„Schlüssel einlesen…“. Der Abholcode bleibt gültig.";
                else
                {
                    TxtStatus.Text = "Hole die Fehlstunden ab …";
                    a = await client.RueckwegAbrufAsync(code, pseudo, k.Kennung);
                }
            }
            catch (SchildServerFehler sf) { fehler = sf.Message; }
            catch (Exception allg) { fehler = "Der Abruf ist fehlgeschlagen:\n\n" + allg.Message; }
            finally { ImEnde(); }

            if (a == null)
            {
                MessageBox.Show(this, fehler ?? "Der Abruf ist fehlgeschlagen.", "Rückweg nach SchILD", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _rwAbruf = a;
            TxtRwCode.Text = "";
            var z = a.Zahlen;
            TxtRwAbruf.Text = $"Abgeholt: Schuljahr {a.Schuljahr}, {SchildText.DatumText(a.Von)} – {SchildText.DatumText(a.Bis)} " +
                              $"(Code von {a.Akteur}) · {z.Entschuldigt + z.Unentschuldigt + z.Beurlaubt} Fehlstunden von {z.Schueler} " +
                              $"Schülern · nicht übertragen: offen {z.Offen}, Verspätungen {z.Verspaetungen}.";
            _rwSetzen = true;
            try
            {
                foreach (var dp in new[] { DpRwVon, DpRwBis })
                {
                    dp.SelectedDate = null;
                    dp.DisplayDateStart = a.Von.ToDateTime(TimeOnly.MinValue);
                    dp.DisplayDateEnd = a.Bis.ToDateTime(TimeOnly.MinValue);
                }
            }
            finally { _rwSetzen = false; }
            _log?.Invoke($"SchILD-Rückweg: abgeholt ({SchildSchluessel.ArtText(pseudo)}, {SchildText.DatumText(a.Von)} – " +
                         $"{SchildText.DatumText(a.Bis)}) – {a.Zeilen.Count} Fehlstunden.");
            RwBerechnen();
            if (_rwErgebnis != null && _rwErgebnis.Sperren.Count > 0)
                MessageBox.Show(this, "Abgeholt, aber keine Datei möglich:\n\n• " + string.Join("\n• ", _rwErgebnis.Sperren),
                    "Rückweg nach SchILD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void BtnRwSchreiben_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft || _rwErgebnis == null || _rwAbruf == null || _export == null) return;
            var r = _rwErgebnis;
            var a = _rwAbruf;
            bool pseudo = _export.IstPseudo;
            if (r.Sperren.Count > 0 || r.Zeilen.Count == 0) return;

            var merk = SchildRueckweg.MerklisteLaden(_planOrdner, pseudo, out string? merkFehler);
            var ueber = SchildRueckweg.Ueberschneidungen(merk, r);

            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = $"Ordner wählen – darin wird ein neuer Ordner „{(pseudo ? "SchILD-Rueckweg-Pseudo_" : "SchILD-Rueckweg_")}…“ angelegt"
            };
            if (Directory.Exists(_planOrdner)) dlg.InitialDirectory = _planOrdner;
            if (dlg.ShowDialog(this) != true) return;
            string basis = (pseudo ? "SchILD-Rueckweg-Pseudo_" : "SchILD-Rueckweg_") + DateTime.Today.ToString("yyyy-MM-dd");
            string ziel = Path.Combine(dlg.FolderName, basis);
            for (int n = 2; Directory.Exists(ziel) || File.Exists(ziel); n++) ziel = Path.Combine(dlg.FolderName, $"{basis}_{n}");

            if (!pseudo && SchildSchluessel.IstCloudOrdner(ziel) &&
                MessageBox.Show(this, $"Der Ordner liegt in OneDrive, Teams bzw. einem anderen Cloud-Ordner:\n{ziel}\n\nDie Datei " +
                                      "enthält Namen, Geburtsdaten und Fehlstunden. Bitte nur auf diesem Rechner ablegen (z. B. " +
                                      "C:\\SchILD\\) und nach dem Import in SchILD löschen.\n\nTrotzdem dort schreiben?",
                    "Rückweg nach SchILD", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            bool utf8 = SchildRueckweg.Utf8(_profil, _export);
            string frage =
                $"SchuelerFehlstunden.dat schreiben?\n\nNeuer Ordner: {ziel}\n" +
                $"{r.Zeilen.Count} Zeilen · {r.Fehlstunden} Fehlstunden (J {r.J}, N {r.N}) von {r.Schueler} Schülern\n" +
                $"Jahr {r.Jahr}, Abschnitt {r.Abschnitt}, {SchildText.DatumText(r.Von)} – {SchildText.DatumText(r.Bis)}, " +
                $"{r.KlassenText}{(r.Probe ? " – PROBEDATEI" : "")} · Zeichensatz {SchildRueckweg.ZeichensatzText(utf8)}" +
                (ueber.Count > 0
                    ? "\n\n⚠ Für diesen Abschnitt und Zeitraum wurde schon eine Datei geschrieben – ein zweiter Import in SchILD kann " +
                      "doppelt zählen:\n• " + string.Join("\n• ", ueber.Select(x => x.Text))
                    : "") +
                (merkFehler != null ? "\n\n" + merkFehler : "") +
                (r.Warnungen.Count > 0 ? $"\n\n{r.Warnungen.Count} Hinweis(e) – siehe Bericht." : "") +
                (pseudo ? "\n\nPseudodaten: nur zum Ansehen – nicht in ein echtes SchILD importieren."
                        : "\n\nVor der ersten echten Übertragung: SchILD-Datenbank sichern und zuerst eine Probedatei (eine Klasse, " +
                          "wenige Tage) importieren und mit der Konferenzübersicht der App vergleichen.");
            if (MessageBox.Show(this, frage, "Rückweg nach SchILD", MessageBoxButton.YesNo,
                    ueber.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
                    ueber.Count > 0 ? MessageBoxResult.No : MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return;

            string datei;
            try
            {
                Directory.CreateDirectory(ziel);
                datei = Path.Combine(ziel, string.IsNullOrWhiteSpace(_profil.Rueckweg.Datei)
                    ? "SchuelerFehlstunden.dat" : Path.GetFileName(_profil.Rueckweg.Datei));
                string text = SchildRueckweg.Text(r, _profil, out _);
                File.WriteAllBytes(datei, SchildRueckweg.Bytes(text, utf8));
                if (pseudo)
                    SchildPseudoMarker.Schreiben(ziel, new SchildPseudoMarker.Marker
                    {
                        Hinweis = "Rückweg nach SchILD aus Pseudodaten (Stundenplanprogramm) – keine echten Personen. Nicht in ein " +
                                  "echtes SchILD importieren.",
                        ErzeugtAm = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                        Plan = _planInfo?.Bezeichnung ?? "",
                        Schuljahr = a.Schuljahr,
                        Abschnitt = r.Abschnitt,
                        Zeichensatz = SchildRueckweg.ZeichensatzText(utf8)
                    }, new[] { Path.GetFileName(datei) });
            }
            catch (Exception fx)
            {
                MessageBox.Show(this, "Die Datei konnte nicht geschrieben werden:\n\n" + fx.Message, "Rückweg nach SchILD",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Merkliste (ohne Personendaten)
            string? merkHinweis = null;
            merk.Eintraege.Add(new SchildRueckweg.MerkEintrag
            {
                Jahr = r.Jahr, Abschnitt = r.Abschnitt, Von = r.Von, Bis = r.Bis, Probe = r.Probe, Klassen = r.Klassen,
                Zeilen = r.Zeilen.Count, Fehlstunden = r.Fehlstunden, Am = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Ordner = Path.GetFileName(ziel)
            });
            try { SchildRueckweg.MerklisteSpeichern(_planOrdner, pseudo, merk); }
            catch (Exception mx) { merkHinweis = $"{SchildRueckweg.MerkName(pseudo)} konnte nicht gespeichert werden ({mx.Message})."; }

            // Meldung an die App (ohne Namen) – für Download-Protokoll und Jahresablauf
            string? meldeFehler = null;
            var client = ImClient();
            if (client == null) meldeFehler = "Keine Server-Adresse.";
            else
            {
                ImStart("Melde die Datei an die App …");
                try
                {
                    await client.RueckwegMeldenAsync(new SchildRueckwegMeldung
                    {
                        Merkzeichen = a.Merkzeichen, Von = r.Von, Bis = r.Bis, Jahr = r.Jahr, Abschnitt = r.Abschnitt, Probe = r.Probe,
                        Klassen = r.Klassen == null ? null : string.Join(", ", r.Klassen), Zeilen = r.Zeilen.Count,
                        Fehlstunden = r.Fehlstunden, Entschuldigt = r.J, Unentschuldigt = r.N, Schueler = r.Schueler
                    });
                }
                catch (SchildServerFehler sf) { meldeFehler = sf.Message; }
                catch (Exception allg) { meldeFehler = allg.Message; }
                finally { ImEnde(); }
            }

            _rwGeschrieben = datei;
            _log?.Invoke($"SchILD-Rückweg: {(r.Probe ? "Probedatei" : "Datei")} geschrieben ({SchildSchluessel.ArtText(pseudo)}, Jahr " +
                         $"{r.Jahr}, Abschnitt {r.Abschnitt}, {SchildText.DatumText(r.Von)} – {SchildText.DatumText(r.Bis)}, " +
                         $"{r.KlassenText}) – {r.Zeilen.Count} Zeilen, {r.Fehlstunden} Fehlstunden in {ziel}");
            RwAnzeigen();
            MessageBox.Show(this,
                $"Geschrieben:\n{datei}\n\n{r.Fehlstunden} Fehlstunden in {r.Zeilen.Count} Zeilen. In SchILD-NRW 3 von Hand " +
                "importieren (Import der Textdatei SchuelerFehlstunden.dat) und danach die Datei bzw. den Ordner löschen." +
                (merkHinweis != null ? "\n\n" + merkHinweis : "") +
                (meldeFehler != null
                    ? "\n\nDie Meldung an die App ist fehlgeschlagen – der Jahresablauf zeigt nur den Abruf:\n" + meldeFehler
                    : "\n\nDie App hat die Datei im Download-Protokoll vermerkt (ohne Namen)."),
                "Rückweg nach SchILD", MessageBoxButton.OK,
                meldeFehler != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        /// <summary>Beim Schließen: abgeholte Fehlstunden, Vorschau (mit Namen) und Schlüssel freigeben.</summary>
        private void RwSchliessen()
        {
            DgRueckweg.ItemsSource = null;
            _rwAbruf = null;
            _rwErgebnis = null;
            _rwBericht = null;
            _rwSchluessel?.Vergessen();
            _rwSchluessel = null;
        }
    }
}
