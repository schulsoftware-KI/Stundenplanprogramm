#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  P3 (Kleinigkeit c, Entscheidung 199): Passt der Plan im Wocheneditor zum veröffentlichten Grundplan?
    //
    //  Beim Test von V3 und von 5.2c-2 stand im Wocheneditor „Plan“ statt „oT_1“ – die Vertretungen bzw. die
    //  SchILD-Vorschau rechneten dann mit einem anderen Plan als die App. „Online veröffentlichen“ und
    //  „Zeitraum veröffentlichen…“ fragen deshalb vorher die Grundplan-Versionen der API ab
    //  (GET /api/grundplan/veroeffentlicht, X-Api-Key, ohne Personendaten) und warnen, wenn an einem der Tage
    //  ein Grundplan mit anderer Plan-Spalte gilt (oder noch keiner). Die SchILD-Vorschau nimmt dieselbe Prüfung
    //  mit den Grundplänen aus dem Status der API. Ist die API nicht erreichbar oder älter, wird nicht gewarnt –
    //  das Veröffentlichen meldet dann ohnehin den Fehler.
    // =====================================================================

    public static class GrundplanAbgleich
    {
        private static readonly HttpClient _http = OnlineVerbindung.NeuerClient(TimeSpan.FromSeconds(8));

        /// <summary>Eine veröffentlichte Grundplan-Version: Plan-Spalte und „gilt ab“.</summary>
        public sealed class PlanVersion
        {
            public string Bezeichnung { get; set; } = "";
            public DateOnly GueltigAb { get; set; }
        }

        private sealed class Antwort
        {
            public string Schuljahr { get; set; } = "";
            public List<PlanVersion> Versionen { get; set; } = new();
        }

        /// <summary>Gelten = Versionen, die im Zeitraum gelten; Andere = davon die mit einer anderen Plan-Spalte.</summary>
        public sealed record Ergebnis(List<PlanVersion> Gelten, List<PlanVersion> Andere)
        {
            public bool KeinGrundplan => Gelten.Count == 0;
            public bool Passt => Gelten.Count > 0 && Andere.Count == 0;
        }

        /// <summary>Versionen des Schuljahres aus der API; null = nicht abfragbar (dann wird nicht gewarnt).</summary>
        public static async Task<List<PlanVersion>?> VersionenAsync(OnlineEinstellungen einst, string schuljahr)
        {
            if (string.IsNullOrWhiteSpace(einst.BaseUrl) || string.IsNullOrWhiteSpace(schuljahr)) return null;
            try
            {
                var url = einst.BaseUrl.TrimEnd('/') + "/api/grundplan/veroeffentlicht?schuljahr=" + Uri.EscapeDataString(schuljahr.Trim());
                if (OnlineVerbindung.ZielFehler(url) != null) return null;   // PB-2 (V3): kein Schlüssel über http nach außen
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(einst.ApiKey)) req.Headers.Add("X-Api-Key", einst.ApiKey);
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync();
                var a = JsonSerializer.Deserialize<Antwort>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return a?.Versionen;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Welche Versionen gelten von–bis (die letzte mit „gilt ab“ ≤ von und alle, die bis „bis“ beginnen) und welche
        /// davon eine andere Plan-Spalte haben als <paramref name="planSpalte"/>.
        /// </summary>
        public static Ergebnis Pruefen(IReadOnlyList<PlanVersion> versionen, string planSpalte, DateOnly von, DateOnly bis)
        {
            var sortiert = versionen.OrderBy(v => v.GueltigAb).ToList();
            var gelten = new List<PlanVersion>();
            var amAnfang = sortiert.LastOrDefault(v => v.GueltigAb <= von);
            if (amAnfang != null) gelten.Add(amAnfang);
            gelten.AddRange(sortiert.Where(v => v.GueltigAb > von && v.GueltigAb <= bis));
            var plan = (planSpalte ?? "").Trim();
            var andere = gelten.Where(v => !string.Equals((v.Bezeichnung ?? "").Trim(), plan, StringComparison.OrdinalIgnoreCase)).ToList();
            return new Ergebnis(gelten, andere);
        }

        public static string Liste(IEnumerable<PlanVersion> versionen)
            => string.Join(", ", versionen.Select(v => $"„{v.Bezeichnung}“ (ab {Tag(v.GueltigAb)})"));

        private static string Tag(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        /// <summary>Warntext für das Veröffentlichen von Tagen; null = passt.</summary>
        public static string? WarnungVeroeffentlichen(Ergebnis e, string planSpalte, DateOnly von, DateOnly bis)
        {
            if (e.Passt) return null;
            string wann = von == bis ? $"am {Tag(von)}" : $"im Zeitraum {Tag(von)} – {Tag(bis)}";
            if (e.KeinGrundplan)
                return $"In der App gilt {wann} noch kein Grundplan. Die Vertretungen erscheinen dort erst, wenn ein Grundplan " +
                       "ab diesem Tag (bzw. ab Schuljahresbeginn) veröffentlicht ist – „Grundplan veröffentlichen…“.";
            return $"Im Wocheneditor ist der Plan „{planSpalte}“ geladen, in der App gilt {wann} der Grundplan {Liste(e.Andere)}. " +
                   "Die Vertretungen passen dann nicht zu den Stunden in der App (andere Lehrkräfte, Klassen oder Räume).";
        }

        /// <summary>
        /// Fragt vor dem Veröffentlichen nach, wenn der Plan nicht zum Grundplan der App passt. true = weiter (passt, nicht
        /// prüfbar oder „Ja“).
        /// </summary>
        /// <param name="gewaehlt">Plan im Feld „Plan“ (gilt erst nach „Aktualisieren“) – für den Hinweis.</param>
        public static async Task<bool> FragenAsync(Window owner, OnlineEinstellungen einst, string schuljahr, string planSpalte,
            string? gewaehlt, DateTime von, DateTime bis, string titel)
        {
            var versionen = await VersionenAsync(einst, schuljahr);
            if (versionen == null) return true;
            DateOnly v = DateOnly.FromDateTime(von), b = DateOnly.FromDateTime(bis);
            string? text = WarnungVeroeffentlichen(Pruefen(versionen, planSpalte, v, b), planSpalte, v, b);
            if (text == null) return true;
            if (!string.IsNullOrWhiteSpace(gewaehlt) && !string.Equals(gewaehlt.Trim(), planSpalte.Trim(), StringComparison.OrdinalIgnoreCase))
                text += $"\n\nIm Feld „Plan“ ist „{gewaehlt}“ gewählt – ein anderer Plan gilt erst nach „Aktualisieren“.";
            text += "\n\nAbhilfe: im Feld „Plan“ den veröffentlichten Grundplan wählen und „Aktualisieren“ – bzw. den Grundplan " +
                    "veröffentlichen.\n\nTrotzdem veröffentlichen?";
            return MessageBox.Show(owner, text, titel, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                   == MessageBoxResult.Yes;
        }
    }
}
