#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  OnlineSync – M4, Schritt 2: der Desktop pusht den effektiven Tag an
    //  die VertretungApi (REST). Übertragen werden nur die GEÄNDERTEN Zellen
    //  (der eigentliche Vertretungsplan). Endpoint: POST /api/plan/push,
    //  geschützt per Header X-Api-Key.
    //
    //  Server-URL und API-Key werden in "vertretung_online.json" neben der
    //  Plandatei gespeichert (OnlineEinstellungen).
    // =====================================================================

    // ---- DTOs (spiegeln die API) ----

    public class PlanPushDto
    {
        public string Schuljahr { get; set; } = "";
        public DateOnly Datum { get; set; }
        public List<PlanEintragDto> Eintraege { get; set; } = new();
    }

    public class PlanEintragDto
    {
        public int Stunde { get; set; }
        public string Klassen { get; set; } = "";
        public string Fach { get; set; } = "";
        public string? Lehrer { get; set; }
        public string? OriginalLehrer { get; set; }
        public string? Raum { get; set; }
        public string Art { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Grund { get; set; }
        public string? Bemerkung { get; set; }
        public bool? Fachgerecht { get; set; }
        public bool IstAenderung { get; set; }

        // Phase 2, Schritt 2: damit die API die Änderung exakt der Grundplan-
        // Stunde zuordnen kann (Wochenansicht).
        /// <summary>UNr der gehaltenen Stunde (bei „verlegt hin": die verlegte).</summary>
        public int? UNr { get; set; }
        /// <summary>UNr der Grundplan-Stunde, an deren Stelle die Zelle tritt.</summary>
        public int? GrundUNr { get; set; }

        // U-1a: ausdrücklich gemeldete Abweichungen vom Grundplan (für den Untis-Import, U-1b).
        // Das Vertretungsmodul setzt sie nicht – die API zeigt dann wie bisher den Raum nur bei
        // „Raumvertretung“ und das Fach nur bei „verlegt hin“.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RaumGeaendert { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FachGeaendert { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? KlassenGeaendert { get; set; }
    }

    public class OnlineEinstellungen
    {
        public const string QuelleVertretungsmodul = "Vertretungsmodul";
        public const string QuelleUntis = "Untis";

        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        /// <summary>
        /// U-1b (Entscheidung 120): Woher kommen die Vertretungen? „Vertretungsmodul“ (Standard) oder „Untis“
        /// (Datei GPU014.TXT, Knopf „Untis-Vertretungen…“). Bei „Untis“ fragt „Online veröffentlichen“ nach.
        /// </summary>
        public string Quelle { get; set; } = QuelleVertretungsmodul;

        [JsonIgnore]
        public bool QuelleIstUntis => string.Equals(Quelle, QuelleUntis, StringComparison.OrdinalIgnoreCase);
    }

    public class OnlineSync
    {
        private const string EinstellungDatei = "vertretung_online.json";
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";

        // ---- Push ----

        /// <summary>Pusht die GEÄNDERTEN Zellen des effektiven Tages. Gibt die Serverantwort zurück.</summary>
        public async Task<string> PushTagAsync(string schuljahr, DateTime datum, Tagesplan tp)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
                throw new InvalidOperationException("Keine Server-URL konfiguriert (Online-Einstellungen).");

            return await PushAsync(BaueDto(schuljahr, datum, tp));
        }

        /// <summary>
        /// U-1b: pusht einen fertigen Tag (z. B. aus der Untis-Datei). Der Tag wird in der API komplett ersetzt –
        /// ein Tag ohne Einträge wird dort leer.
        /// </summary>
        public async Task<string> PushAsync(PlanPushDto dto, System.Threading.CancellationToken abbruch = default)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
                throw new InvalidOperationException("Keine Server-URL konfiguriert (Online-Einstellungen).");
            var url = BaseUrl.TrimEnd('/') + "/api/plan/push";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(ApiKey)) req.Headers.Add("X-Api-Key", ApiKey);
            req.Content = JsonContent.Create(dto);

            using var resp = await _http.SendAsync(req, abbruch);
            string body = await resp.Content.ReadAsStringAsync(abbruch);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Server {(int)resp.StatusCode}: {body}");
            return body;
        }

        /// <summary>Übersetzt den effektiven Tag in das Push-DTO (nur geänderte Zellen).</summary>
        public static PlanPushDto BaueDto(string schuljahr, DateTime datum, Tagesplan tp)
        {
            var dto = new PlanPushDto
            {
                Schuljahr = schuljahr,
                Datum = DateOnly.FromDateTime(datum.Date)
            };

            foreach (var z in tp.Zellen.Where(z => z.IstGeaendert))
            {
                dto.Eintraege.Add(new PlanEintragDto
                {
                    Stunde = z.Stunde,
                    Klassen = string.Join(",", z.Klassen),
                    Fach = z.Fach,
                    Lehrer = string.IsNullOrWhiteSpace(z.Lehrer) ? null : z.Lehrer,
                    OriginalLehrer = string.IsNullOrWhiteSpace(z.OriginalLehrer) ? null : z.OriginalLehrer,
                    Raum = z.Raum,
                    Art = ArtText(z.Status),
                    Status = z.Status.ToString(),
                    Grund = z.Eintrag?.Grund.ToString(),
                    Bemerkung = string.IsNullOrWhiteSpace(z.Info) ? z.Eintrag?.Bemerkung : z.Info,
                    Fachgerecht = z.Eintrag?.Fachgerecht,
                    IstAenderung = true,
                    UNr = z.UNr > 0 ? z.UNr : null,
                    // Bei „verlegt hin" ersetzt TagesplanBerechnung z.UNr durch die
                    // verlegte UNr; die Buchung behält die UNr der Grundplan-Zelle.
                    GrundUNr = (z.Eintrag?.UNr ?? z.UNr) > 0 ? (z.Eintrag?.UNr ?? z.UNr) : null
                });
            }
            return dto;
        }

        private static string ArtText(ZellStatus s) => s switch
        {
            ZellStatus.Vertreten => "Vertretung",
            ZellStatus.Entfall => "Entfall",
            ZellStatus.VerlegtWeg => "Verlegung",
            ZellStatus.VerlegtHin => "Verlegung",
            ZellStatus.Betreuung => "Betreuung",
            ZellStatus.EVA => "EVA",
            ZellStatus.Zusammenlegung => "Zusammenlegung",
            ZellStatus.Raumvertretung => "Raumvertretung",
            _ => "Normal"
        };

        // ---- Einstellungen (JSON neben der Plandatei) ----

        /// <summary>V2: vollständiger Pfad der Einstellungsdatei im Ordner der Plandatei.</summary>
        public static string EinstellungsPfad(string ordner) => Path.Combine(ordner, EinstellungDatei);

        /// <summary>
        /// V2: Fehlermeldung beim Veröffentlichen, bei 401/404 mit Hinweis, wo die Einstellungen
        /// liegen (der Schlüssel selbst wird nie angezeigt).
        /// </summary>
        public static string FehlerText(Exception ex, string ordner)
        {
            string text = ex.Message;
            if (ex is HttpRequestException)
            {
                if (text.StartsWith("Server 401", StringComparison.Ordinal))
                    text += "\n\nDer API-Key passt nicht zu „Api:PushKey“ der laufenden API." +
                            "\nEinstellungen: " + EinstellungsPfad(ordner);
                else if (text.StartsWith("Server 404", StringComparison.Ordinal))
                    text += "\n\nPrüfe die Server-URL: nur die Adresse ohne Pfad (z. B. https://….trycloudflare.com)." +
                            "\nEinstellungen: " + EinstellungsPfad(ordner);
            }
            return text;
        }

        public static OnlineEinstellungen LadeEinstellungen(string ordner)
        {
            try
            {
                var pfad = Path.Combine(ordner, EinstellungDatei);
                if (!File.Exists(pfad)) return new OnlineEinstellungen();
                var json = File.ReadAllText(pfad);
                return JsonSerializer.Deserialize<OnlineEinstellungen>(json) ?? new OnlineEinstellungen();
            }
            catch { return new OnlineEinstellungen(); }
        }

        public static void SpeichereEinstellungen(string ordner, OnlineEinstellungen e)
        {
            var pfad = Path.Combine(ordner, EinstellungDatei);
            var json = JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(pfad, json);
        }

        /// <summary>
        /// Kleiner programmatisch gebauter Dialog (kein XAML) für Server-URL und
        /// API-Key. Gibt die bearbeiteten Einstellungen zurück oder null bei Abbruch.
        /// </summary>
        /// <param name="ordner">V2: Ordner der Einstellungsdatei (Ordner der Plandatei) – wird angezeigt.</param>
        public static OnlineEinstellungen? FrageEinstellungen(Window? owner, OnlineEinstellungen vorgabe, string? ordner = null)
        {
            var win = new Window
            {
                Title = "Online-Einstellungen",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid { Margin = new Thickness(14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock Label(string t, int r)
            {
                var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 10, 6) };
                Grid.SetRow(tb, r); Grid.SetColumn(tb, 0); grid.Children.Add(tb); return tb;
            }

            Label("Server-URL:", 0);
            var txtUrl = new TextBox { Text = vorgabe.BaseUrl, Margin = new Thickness(0, 6, 0, 6) };
            Grid.SetRow(txtUrl, 0); Grid.SetColumn(txtUrl, 1); grid.Children.Add(txtUrl);

            Label("API-Key:", 1);
            var txtKey = new TextBox { Text = vorgabe.ApiKey, Margin = new Thickness(0, 6, 0, 6) };
            Grid.SetRow(txtKey, 1); Grid.SetColumn(txtKey, 1); grid.Children.Add(txtKey);

            // U-1b (Entscheidung 120): eine Quelle je Tag – Vertretungsmodul oder Untis.
            Label("Vertretungen\nkommen aus:", 2);
            var quelle = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            var rbModul = new RadioButton
            {
                Content = "Vertretungsmodul (Wocheneditor, „Vertretung erfassen“)", GroupName = "quelle",
                IsChecked = !vorgabe.QuelleIstUntis, Margin = new Thickness(0, 0, 0, 2)
            };
            var rbUntis = new RadioButton
            {
                Content = "Untis (Datei GPU014.TXT, Knopf „Untis-Vertretungen…“)", GroupName = "quelle",
                IsChecked = vorgabe.QuelleIstUntis
            };
            quelle.Children.Add(rbModul);
            quelle.Children.Add(rbUntis);
            quelle.Children.Add(new TextBlock
            {
                Text = "Bei „Untis“ fragt „Online veröffentlichen“ nach, bevor ein Tag aus dem Vertretungsmodul die " +
                       "Untis-Vertretungen in der App ersetzt.",
                Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            Grid.SetRow(quelle, 2); Grid.SetColumn(quelle, 1); grid.Children.Add(quelle);

            var hinweis = new TextBlock
            {
                Text = "Beispiel-URL (Entwicklung): http://localhost:5080\nDer API-Key muss mit Api:PushKey im Backend übereinstimmen.",
                Foreground = System.Windows.Media.Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10)
            };
            Grid.SetRow(hinweis, 4); Grid.SetColumn(hinweis, 1); grid.Children.Add(hinweis);

            // V2 (Nutzer 09.11.2026): zeigen, welche Datei benutzt wird – jede Plandatei-Kopie hat ihre eigene.
            if (!string.IsNullOrWhiteSpace(ordner))
            {
                string pfad = EinstellungsPfad(ordner);
                Label("Gespeichert in:", 3);
                var ort = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
                var zeile = new DockPanel();
                var oeffnen = new Button { Content = "Ordner öffnen", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(oeffnen, Dock.Right);
                oeffnen.Click += (_, __) =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ordner}\"") { UseShellExecute = true }); }
                    catch { /* Öffnen ist nur Komfort */ }
                };
                zeile.Children.Add(oeffnen);
                zeile.Children.Add(new TextBlock
                {
                    Text = pfad, ToolTip = pfad, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                ort.Children.Add(zeile);

                // Liegt neben dem Programm noch eine weitere Datei, wird sie NICHT benutzt.
                try
                {
                    string neben = Path.Combine(AppContext.BaseDirectory, EinstellungDatei);
                    if (File.Exists(neben) &&
                        !string.Equals(Path.GetFullPath(neben), Path.GetFullPath(pfad), StringComparison.OrdinalIgnoreCase))
                        ort.Children.Add(new TextBlock
                        {
                            Text = "Hinweis: Neben dem Programm liegt eine weitere vertretung_online.json – sie wird nicht benutzt.",
                            Foreground = System.Windows.Media.Brushes.DarkOrange,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 4, 0, 0)
                        });
                }
                catch { /* nur Hinweis */ }

                Grid.SetRow(ort, 3); Grid.SetColumn(ort, 1); grid.Children.Add(ort);
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Abbrechen", Width = 90, IsCancel = true };
            panel.Children.Add(ok); panel.Children.Add(cancel);
            Grid.SetRow(panel, 5); Grid.SetColumn(panel, 1); grid.Children.Add(panel);

            win.Content = grid;

            bool bestaetigt = false;
            ok.Click += (_, __) => { bestaetigt = true; win.DialogResult = true; };

            return win.ShowDialog() == true && bestaetigt
                ? new OnlineEinstellungen
                {
                    BaseUrl = txtUrl.Text.Trim(),
                    ApiKey = txtKey.Text.Trim(),
                    Quelle = rbUntis.IsChecked == true ? OnlineEinstellungen.QuelleUntis : OnlineEinstellungen.QuelleVertretungsmodul
                }
                : null;
        }
    }
}
