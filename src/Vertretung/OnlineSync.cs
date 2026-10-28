#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
    }

    public class OnlineEinstellungen
    {
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
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

            var dto = BaueDto(schuljahr, datum, tp);
            var url = BaseUrl.TrimEnd('/') + "/api/plan/push";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(ApiKey)) req.Headers.Add("X-Api-Key", ApiKey);
            req.Content = JsonContent.Create(dto);

            using var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
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
        public static OnlineEinstellungen? FrageEinstellungen(Window? owner, OnlineEinstellungen vorgabe)
        {
            var win = new Window
            {
                Title = "Online-Einstellungen",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid { Margin = new Thickness(14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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

            var hinweis = new TextBlock
            {
                Text = "Beispiel-URL (Entwicklung): http://localhost:5080\nDer API-Key muss mit Api:PushKey im Backend übereinstimmen.",
                Foreground = System.Windows.Media.Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10)
            };
            Grid.SetRow(hinweis, 2); Grid.SetColumn(hinweis, 1); grid.Children.Add(hinweis);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Abbrechen", Width = 90, IsCancel = true };
            panel.Children.Add(ok); panel.Children.Add(cancel);
            Grid.SetRow(panel, 3); Grid.SetColumn(panel, 1); grid.Children.Add(panel);

            win.Content = grid;

            bool bestaetigt = false;
            ok.Click += (_, __) => { bestaetigt = true; win.DialogResult = true; };

            return win.ShowDialog() == true && bestaetigt
                ? new OnlineEinstellungen { BaseUrl = txtUrl.Text.Trim(), ApiKey = txtKey.Text.Trim() }
                : null;
        }
    }
}
