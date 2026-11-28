#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  ServerVerbindenDialog – PB-2 (Entscheidung 302): „Server verbinden…“
    //  ersetzt „Online-Einstellungen…“ (Wocheneditor, SchILD-Fenster).
    //
    //  - Zwei feste Verbindungen „Entwicklung“ und „Testserver“, eine aktiv.
    //  - Schlüssel verdeckt (PasswordBox); leer = der gespeicherte bleibt.
    //    Gespeichert wird mit DPAPI (PushSchluessel), an die Adresse gebunden.
    //  - „Verbindung testen“: 1. Lebenszeichen (ohne Schlüssel), 2. Name der
    //    Schule (ohne Schlüssel), 3. Schlüssel über api/schild/status, 4.
    //    Betriebsart. Der Schlüssel geht erst hinaus, wenn 1. geklappt hat.
    //  - Quelle der Vertretungen (Vertretungsmodul | Untis) wie bisher.
    //  Programmatisch gebaut (keine XAML-Datei) wie das bisherige Fenster.
    // =====================================================================
    public static class ServerVerbindenDialog
    {
        private static readonly HttpClient TestHttp = OnlineVerbindung.NeuerClient(TimeSpan.FromSeconds(10));

        private sealed class Zeile
        {
            public string Name = "";
            public RadioButton Rb = null!;
            public TextBox TxtAdresse = null!;
            public PasswordBox PwSchluessel = null!;
            public TextBlock TxtStand = null!;
            public TextBlock TxtTest = null!;
            public Button BtnTest = null!;
            public Button BtnEntfernen = null!;
            public PushSchluessel.Eintrag? Gespeichert;
            public string? LadeFehler;
            public bool Entfernen;

            public bool IstEntwicklung => Name == OnlineEinstellungen.Entwicklung;
            public string Adresse => TxtAdresse.Text.Trim();
            public string Eingabe => PwSchluessel.Password.Trim();
        }

        /// <summary>Zeigt das Fenster; true = gespeichert.</summary>
        public static bool Zeigen(Window owner, string ordner)
        {
            var einst = OnlineSync.LadeEinstellungen(ordner);
            var win = new Window
            {
                Title = "Server verbinden",
                Width = 700,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };

            var wurzel = new StackPanel { Margin = new Thickness(14) };
            wurzel.Children.Add(new TextBlock
            {
                Text = "Wohin veröffentlicht der Desktop (Grundplan, Vertretungen, Untis, SchILD-Import und Rückweg)? Eine " +
                       "Verbindung ist aktiv. Die Schlüssel werden verschlüsselt für diesen Windows-Benutzer gespeichert – nicht " +
                       "in vertretung_online.json und nicht in OneDrive.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var zEnt = BaueZeile(OnlineEinstellungen.Entwicklung, "Entwicklung – API auf diesem Rechner („dotnet run“)",
                einst.AdresseEntwicklung.Length > 0 ? einst.AdresseEntwicklung : OnlineEinstellungen.EntwicklungAdresse, wurzel);
            var zTest = BaueZeile(OnlineEinstellungen.Testserver, "Testserver – Probebetrieb, nur Pseudodaten",
                einst.AdresseTestserver, wurzel);
            (einst.IstTestserver ? zTest : zEnt).Rb.IsChecked = true;

            // U-1b (Entscheidung 120): eine Quelle je Tag – Vertretungsmodul oder Untis (wie bisher).
            var quelle = new GroupBox { Header = "Vertretungen kommen aus", Margin = new Thickness(0, 4, 0, 8) };
            var qPanel = new StackPanel { Margin = new Thickness(6) };
            var rbModul = new RadioButton
            {
                Content = "Vertretungsmodul (Wocheneditor, „Vertretung erfassen“)", GroupName = "quelle",
                IsChecked = !einst.QuelleIstUntis, Margin = new Thickness(0, 0, 0, 2)
            };
            var rbUntis = new RadioButton
            {
                Content = "Untis (Datei GPU014.TXT, Knopf „Untis-Vertretungen…“)", GroupName = "quelle",
                IsChecked = einst.QuelleIstUntis
            };
            qPanel.Children.Add(rbModul);
            qPanel.Children.Add(rbUntis);
            qPanel.Children.Add(new TextBlock
            {
                Text = "Bei „Untis“ fragt „Online veröffentlichen“ nach, bevor ein Tag aus dem Vertretungsmodul die " +
                       "Untis-Vertretungen in der App ersetzt.",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            });
            quelle.Content = qPanel;
            wurzel.Children.Add(quelle);

            wurzel.Children.Add(OrtZeile("Gespeichert in:", OnlineSync.EinstellungsPfad(ordner), ordner));
            wurzel.Children.Add(OrtZeile("Schlüssel in:", PushSchluessel.Ordner + "\\push_….dat", PushSchluessel.Ordner));
            // Liegt neben dem Programm noch eine weitere Datei, wird sie NICHT benutzt (wie bisher).
            try
            {
                string neben = Path.Combine(AppContext.BaseDirectory, "vertretung_online.json");
                if (File.Exists(neben) && !string.Equals(Path.GetFullPath(neben), Path.GetFullPath(OnlineSync.EinstellungsPfad(ordner)),
                        StringComparison.OrdinalIgnoreCase))
                    wurzel.Children.Add(new TextBlock
                    {
                        Text = "Hinweis: Neben dem Programm liegt eine weitere vertretung_online.json – sie wird nicht benutzt.",
                        Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                    });
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException
                                       or UnauthorizedAccessException) { /* nur Hinweis */ }

            var knoepfe = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var abbrechen = new Button { Content = "Abbrechen", Width = 90, IsCancel = true };
            knoepfe.Children.Add(ok);
            knoepfe.Children.Add(abbrechen);
            wurzel.Children.Add(knoepfe);
            win.Content = wurzel;

            ok.Click += (_, __) =>
            {
                string aktiv = zTest.Rb.IsChecked == true ? OnlineEinstellungen.Testserver : OnlineEinstellungen.Entwicklung;
                string? fehler = Pruefen(zEnt, zTest, aktiv);
                if (fehler != null)
                {
                    MessageBox.Show(win, fehler, "Server verbinden", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    foreach (var z in new[] { zEnt, zTest })
                    {
                        if (z.Eingabe.Length > 0) PushSchluessel.Speichern(z.Name, z.Adresse, z.Eingabe);
                        else if (z.Entfernen) PushSchluessel.Entfernen(z.Name);
                    }
                    OnlineSync.SpeichereVerbindungen(ordner, aktiv, zEnt.Adresse, zTest.Adresse,
                        rbUntis.IsChecked == true ? OnlineEinstellungen.QuelleUntis : OnlineEinstellungen.QuelleVertretungsmodul);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Security.Cryptography.CryptographicException or ArgumentException)
                {
                    MessageBox.Show(win, "Speichern fehlgeschlagen:\n\n" + ex.Message, "Server verbinden",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                win.DialogResult = true;
            };

            bool gespeichert = win.ShowDialog() == true;
            zEnt.PwSchluessel.Clear();
            zTest.PwSchluessel.Clear();
            return gespeichert;
        }

        // =====================================================
        //  Eine Verbindung (Rahmen mit Adresse, Schlüssel, Test)
        // =====================================================

        private static Zeile BaueZeile(string name, string titel, string adresse, StackPanel wurzel)
        {
            var z = new Zeile { Name = name };
            z.Gespeichert = PushSchluessel.Laden(name, out z.LadeFehler);
            z.Rb = new RadioButton { Content = titel, GroupName = "aktiv", FontWeight = FontWeights.SemiBold };
            var rahmen = new GroupBox { Header = z.Rb, Margin = new Thickness(0, 0, 0, 8) };

            var grid = new Grid { Margin = new Thickness(6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void Setze(UIElement el, int zeile, int spalte)
            {
                Grid.SetRow(el, zeile);
                Grid.SetColumn(el, spalte);
                grid.Children.Add(el);
            }

            Setze(new TextBlock { Text = "Adresse:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) }, 0, 0);
            z.TxtAdresse = new TextBox { Text = adresse, Margin = new Thickness(0, 4, 0, 4) };
            Setze(z.TxtAdresse, 0, 1);

            Setze(new TextBlock { Text = "Schlüssel:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) }, 1, 0);
            z.PwSchluessel = new PasswordBox { Margin = new Thickness(0, 4, 0, 0) };
            Setze(z.PwSchluessel, 1, 1);
            z.TxtStand = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            Setze(z.TxtStand, 2, 1);

            var leiste = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            z.BtnTest = new Button { Content = "Verbindung testen", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
            z.BtnEntfernen = new Button { Content = "Schlüssel entfernen", Padding = new Thickness(10, 2, 10, 2) };
            leiste.Children.Add(z.BtnTest);
            leiste.Children.Add(z.BtnEntfernen);
            Setze(leiste, 3, 1);
            z.TxtTest = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            Setze(z.TxtTest, 4, 1);

            rahmen.Content = grid;
            wurzel.Children.Add(rahmen);

            z.TxtAdresse.TextChanged += (_, __) => ZeigeStand(z);
            z.PwSchluessel.PasswordChanged += (_, __) => ZeigeStand(z);
            z.BtnEntfernen.Click += (_, __) =>
            {
                z.Entfernen = true;
                z.PwSchluessel.Clear();
                ZeigeStand(z);
            };
            z.BtnTest.Click += async (_, __) =>
            {
                string adr = z.Adresse;
                string? schl = SchluesselFuer(z, adr);
                string? zeichen = schl != null ? ZeichenFehler(schl) : null;
                z.BtnTest.IsEnabled = false;
                z.TxtTest.Foreground = Brushes.DimGray;
                z.TxtTest.Text = "Teste …";
                try
                {
                    (int Stufe, string Text) erg;
                    if (zeichen != null) erg = (2, zeichen);
                    else erg = await TestenAsync(z.Name, adr, schl);
                    z.TxtTest.Text = erg.Text;
                    z.TxtTest.Foreground = erg.Stufe == 0 ? Brushes.DarkGreen : erg.Stufe == 1 ? Brushes.DarkOrange : Brushes.Firebrick;
                }
                finally { z.BtnTest.IsEnabled = true; }
            };
            ZeigeStand(z);
            return z;
        }

        private static void ZeigeStand(Zeile z)
        {
            string text;
            Brush farbe = Brushes.DimGray;
            string adr = z.Adresse;
            if (z.PwSchluessel.Password.Length > 0)
                text = "neu eingegeben – wird mit „OK“ gespeichert (für diese Adresse)";
            else if (z.Entfernen)
            {
                text = z.IstEntwicklung ? "wird mit „OK“ entfernt – danach gilt der Standard der Entwicklung"
                                        : "wird mit „OK“ entfernt";
                farbe = Brushes.DarkOrange;
            }
            else if (z.Gespeichert != null)
            {
                if (OnlineSync.GleicheAdresse(z.Gespeichert.Adresse, adr))
                    text = (PushSchluessel.StandText(z.Gespeichert) ?? "gespeichert") + " – leer lassen, um ihn zu behalten";
                else
                {
                    text = $"gespeichert für {z.Gespeichert.Adresse} – für die neue Adresse bitte neu eingeben";
                    farbe = Brushes.DarkOrange;
                }
            }
            else if (z.LadeFehler != null && PushSchluessel.Vorhanden(z.Name))
            {
                text = z.LadeFehler;
                farbe = Brushes.DarkOrange;
            }
            else if (z.IstEntwicklung)
                text = "Standard der Entwicklung (CHANGE-ME-dev-key, wie in appsettings.json)";
            else
            {
                text = "kein Schlüssel gespeichert – den Push-Schlüssel des Servers einfügen (Testserver: „vertretung schluessel“)";
                farbe = Brushes.DarkOrange;
            }
            z.TxtStand.Text = text;
            z.TxtStand.Foreground = farbe;
            z.BtnEntfernen.IsEnabled = !z.Entfernen && PushSchluessel.Vorhanden(z.Name);
        }

        /// <summary>Schlüssel, der für Test bzw. Senden an diese Adresse gilt – null, wenn keiner da ist.</summary>
        private static string? SchluesselFuer(Zeile z, string adresse)
        {
            if (z.Eingabe.Length > 0) return z.Eingabe;
            if (!z.Entfernen && z.Gespeichert != null && OnlineSync.GleicheAdresse(z.Gespeichert.Adresse, adresse))
                return z.Gespeichert.Schluessel;
            if (z.IstEntwicklung && (z.Entfernen || !PushSchluessel.Vorhanden(z.Name)))
                return OnlineEinstellungen.EntwicklungSchluessel;
            return null;
        }

        /// <summary>Ein Kopf X-Api-Key verträgt nur sichtbare ASCII-Zeichen ohne Leerzeichen.</summary>
        private static string? ZeichenFehler(string schluessel)
        {
            foreach (char c in schluessel)
                if (c < '!' || c > '~')
                    return "Der Schlüssel enthält Leerzeichen, Zeilenumbrüche oder Sonderzeichen – beim Einfügen etwas mitkopiert? " +
                           "Nur den Schlüssel selbst einfügen.";
            return null;
        }

        private static string? Pruefen(Zeile zEnt, Zeile zTest, string aktiv)
        {
            foreach (var z in new[] { zEnt, zTest })
            {
                if (z.Adresse.Length > 0 && OnlineSync.AdresseFehler(z.Adresse) is string af)
                    return $"{z.Name}: {af}";
                if (z.Eingabe.Length > 0 && z.Adresse.Length == 0)
                    return $"{z.Name}: Ein Schlüssel ohne Adresse lässt sich nicht speichern – bitte die Adresse eintragen.";
                if (z.Eingabe.Length > 0 && ZeichenFehler(z.Eingabe) is string zf)
                    return $"{z.Name}: {zf}";
            }
            var za = aktiv == OnlineEinstellungen.Testserver ? zTest : zEnt;
            if (za.Adresse.Length == 0)
                return $"Für die gewählte Verbindung „{za.Name}“ fehlt die Adresse.";
            if (SchluesselFuer(za, za.Adresse) == null)
                return za.IstEntwicklung
                    ? "Für „Entwicklung“ fehlt der Schlüssel für diese Adresse – bitte eingeben oder „Schlüssel entfernen“ (dann " +
                      "gilt der Standard der Entwicklung)."
                    : "Für „Testserver“ fehlt der Schlüssel für diese Adresse. Den Push-Schlüssel des Testservers einfügen – auf " +
                      "dem Server zeigt ihn „vertretung schluessel“.";
            return null;
        }

        private static FrameworkElement OrtZeile(string titel, string text, string ordner)
        {
            var zeile = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var t = new TextBlock { Text = titel, Width = 95, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(t, Dock.Left);
            var oeffnen = new Button { Content = "Ordner öffnen", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(oeffnen, Dock.Right);
            oeffnen.Click += (_, __) =>
            {
                try
                {
                    Directory.CreateDirectory(ordner);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ordner}\"") { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                           or ArgumentException or NotSupportedException) { /* Öffnen ist nur Komfort */ }
            };
            zeile.Children.Add(t);
            zeile.Children.Add(oeffnen);
            zeile.Children.Add(new TextBlock
            {
                Text = text, ToolTip = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            });
            return zeile;
        }

        // =====================================================
        //  Verbindung testen (ohne API-Änderung)
        // =====================================================

        /// <summary>
        /// Prüft Lebenszeichen, Schule, Schlüssel und Betriebsart. Stufe 0 = in Ordnung, 1 = Hinweis, 2 = Fehler. Der
        /// Schlüssel wird nur gesendet, wenn das Lebenszeichen geklappt hat, und erscheint in keinem Text.
        /// </summary>
        public static async Task<(int Stufe, string Text)> TestenAsync(string verbindung, string adresse, string? schluessel)
        {
            string adr = (adresse ?? "").Trim();
            if (OnlineSync.AdresseFehler(adr) is string af) return (2, af);
            string basis = adr.TrimEnd('/');
            bool test = verbindung == OnlineEinstellungen.Testserver;
            var zeilen = new List<string>();
            int stufe = 0;

            // 1. Lebenszeichen – ohne Schlüssel
            try
            {
                using var antwort = await TestHttp.GetAsync(basis + "/api/gesundheit");
                int code = (int)antwort.StatusCode;
                string text = await antwort.Content.ReadAsStringAsync();
                if (code is >= 300 and < 400)
                    return (2, $"1. Lebenszeichen: HTTP {code}" +
                               (antwort.Headers.Location != null ? $" nach {antwort.Headers.Location}" : "") + ".\n" +
                               OnlineVerbindung.TextWeiterleitung + "\nDer Schlüssel wurde nicht gesendet.");
                if (code == 503 && text.Contains("Datenbank", StringComparison.Ordinal))
                    return (2, "1. Lebenszeichen: Die API läuft, aber ihre Datenbank ist nicht erreichbar (HTTP 503)." +
                               (test ? " Auf dem Server „vertretung status“ aufrufen." : " Läuft PostgreSQL bzw. stimmt die Datenbank?"));
                if (code != 200 || !LebenszeichenOk(text))
                    return (2, $"1. Lebenszeichen: An dieser Adresse antwortet keine Vertretungs-API (HTTP {code}). Stimmt die " +
                               "Adresse (ohne Pfad)?" +
                               (OnlineVerbindung.StatusErklaerung(code) is string se ? "\n" + se : "") +
                               "\nDer Schlüssel wurde nicht gesendet.");
                zeilen.Add("1. Lebenszeichen: ok (API und Datenbank erreichbar).");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                return (2, $"1. Lebenszeichen: keine Verbindung zu {basis}.\n" + (OnlineVerbindung.Erklaerung(ex) ?? ex.Message) +
                           "\nDer Schlüssel wurde nicht gesendet.");
            }

            // 2. Schule (ohne Schlüssel; Name steht in der App auch vor der Anmeldung)
            string? schule = null;
            string? testdatum = null;
            try
            {
                using var antwort = await TestHttp.GetAsync(basis + "/api/auth/optionen");
                if (antwort.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync());
                    var wurzel = doc.RootElement;
                    if (wurzel.ValueKind == JsonValueKind.Object)
                    {
                        if (wurzel.TryGetProperty("schule", out var oSchule) && oSchule.ValueKind == JsonValueKind.Object
                            && oSchule.TryGetProperty("name", out var oName) && oName.ValueKind == JsonValueKind.String)
                            schule = oName.GetString();
                        if (wurzel.TryGetProperty("testdatum", out var oTest) && oTest.ValueKind == JsonValueKind.String)
                            testdatum = oTest.GetString();
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or JsonException) { /* nur Zusatz */ }
            zeilen.Add("2. Schule: " + (string.IsNullOrWhiteSpace(schule) ? "(kein Name eingetragen bzw. nicht abfragbar)" : schule));

            // 3. Schlüssel – erst jetzt
            if (string.IsNullOrEmpty(schluessel))
            {
                zeilen.Add("3. Schlüssel: keiner eingegeben bzw. für diese Adresse gespeichert – nicht geprüft.");
                return (2, string.Join("\n", zeilen));
            }
            try
            {
                using var anfrage = new HttpRequestMessage(HttpMethod.Get, basis + "/api/schild/status?abschnitt=1");
                anfrage.Headers.Add("X-Api-Key", schluessel);
                using var antwort = await TestHttp.SendAsync(anfrage);
                int code = (int)antwort.StatusCode;
                string text = await antwort.Content.ReadAsStringAsync();
                if (code == 401)
                {
                    zeilen.Add("3. Schlüssel: passt nicht zu „Api:PushKey“ dieses Servers (HTTP 401)." +
                               (test ? " Auf dem Testserver zeigt „vertretung schluessel“ den richtigen." : ""));
                    return (2, string.Join("\n", zeilen));
                }
                if (code != 200)
                {
                    zeilen.Add($"3. Schlüssel: nicht prüfbar (HTTP {code})." +
                               (code == 404 ? " Die API kennt „api/schild/status“ nicht – Stand vor 5.2b?" : "") +
                               (OnlineVerbindung.FehlerText(text) is string ft ? " " + ft : "") +
                               (OnlineVerbindung.StatusErklaerung(code) is string se ? "\n" + se : ""));
                    return (2, string.Join("\n", zeilen));
                }

                string betrieb = "", schuljahr = "";
                bool pseudo = false;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var w = doc.RootElement;
                    if (w.ValueKind == JsonValueKind.Object)
                    {
                        if (w.TryGetProperty("betrieb", out var oBetrieb) && oBetrieb.ValueKind == JsonValueKind.String)
                            betrieb = oBetrieb.GetString() ?? "";
                        if (w.TryGetProperty("schuljahr", out var oSj) && oSj.ValueKind == JsonValueKind.String)
                            schuljahr = oSj.GetString() ?? "";
                        pseudo = w.TryGetProperty("pseudo", out var oPseudo) && oPseudo.ValueKind == JsonValueKind.True;
                    }
                }
                catch (JsonException) { /* unten „unbekannt“ */ }

                zeilen.Add("3. Schlüssel: gültig.");
                string art = betrieb.Length > 0 ? betrieb : "(unbekannt)";
                zeilen.Add($"4. Betriebsart: {art}" + (pseudo ? " – nimmt nur Pseudodaten" : " – nimmt nur echte Daten") +
                           (schuljahr.Length > 0 ? $" · Schuljahr {schuljahr}" : "") +
                           (testdatum != null ? $" · Testdatum {testdatum}" : ""));
                if (art == "Probebetrieb")
                    zeilen.Add("   Probebetrieb – nur Testdaten: keine echten Namen oder Vorfälle eintragen.");
                if (test && art == "Entwicklung")
                {
                    stufe = 1;
                    zeilen.Add("⚠ Der Testserver soll im Probebetrieb laufen – diese API läuft in der Entwicklung (Seeds und " +
                               "Testkonten offen).");
                }
                else if (test && art != "Probebetrieb")
                {
                    stufe = 1;
                    zeilen.Add($"⚠ Diese API läuft nicht im Probebetrieb ({art}) – Pseudodaten nimmt sie nicht an. Auf dem " +
                               "Testserver muss „Betrieb:Probebetrieb“ an sein.");
                }
                else if (!test && art != "Entwicklung")
                {
                    stufe = 1;
                    zeilen.Add($"⚠ Die Verbindung „Entwicklung“ zeigt auf eine API, die nicht in der Entwicklung läuft ({art}). " +
                               "Für einen Server im Internet die Verbindung „Testserver“ nehmen.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                zeilen.Add("3. Schlüssel: keine Antwort.\n" + (OnlineVerbindung.Erklaerung(ex) ?? ex.Message));
                return (2, string.Join("\n", zeilen));
            }
            catch (FormatException)
            {
                zeilen.Add("3. Schlüssel: enthält Zeichen, die in einer Anfrage nicht erlaubt sind.");
                return (2, string.Join("\n", zeilen));
            }
            return (stufe, string.Join("\n", zeilen));
        }

        private static bool LebenszeichenOk(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                       && doc.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                       && s.GetString() == "ok";
            }
            catch (JsonException) { return false; }
        }
    }
}
