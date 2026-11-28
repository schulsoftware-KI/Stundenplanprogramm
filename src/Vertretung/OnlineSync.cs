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
    //  PB-2 (302): Die Datei behält nur Adressen, Auswahl und Quelle; der
    //  Schlüssel liegt je Verbindung mit DPAPI im Benutzerprofil
    //  (PushSchluessel). Laden, Umzug und „Server verbinden…“:
    //  OnlineSync_Verbindung.cs, ServerVerbindenDialog.cs.
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
        // Ohne Merker zeigt die API den Raum nur bei „Raumvertretung“ und das Fach nur bei „verlegt hin“.
        // R3-D: Das Vertretungsmodul setzt RaumGeaendert, wenn eine Buchung den Raum ändert.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RaumGeaendert { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FachGeaendert { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? KlassenGeaendert { get; set; }
    }

    public class OnlineEinstellungen
    {
        public const string QuelleVertretungsmodul = "Vertretungsmodul";
        public const string QuelleUntis = "Untis";

        // PB-2 (302): zwei feste Verbindungen, eine davon aktiv.
        public const string Entwicklung = "Entwicklung";
        public const string Testserver = "Testserver";
        public const string EntwicklungAdresse = "http://localhost:5080";
        /// <summary>V1: öffentlicher Entwicklungswert (wie Api:PushKey in appsettings.json) – gilt ohne gespeicherten Schlüssel.</summary>
        public const string EntwicklungSchluessel = "CHANGE-ME-dev-key";

        /// <summary>Adresse der aktiven Verbindung.</summary>
        public string BaseUrl { get; set; } = "";
        /// <summary>PB-2: Push-Schlüssel der aktiven Verbindung – nur im Speicher, nie in vertretung_online.json.</summary>
        [JsonIgnore]
        public string ApiKey { get; set; } = "";
        /// <summary>
        /// U-1b (Entscheidung 120): Woher kommen die Vertretungen? „Vertretungsmodul“ (Standard) oder „Untis“
        /// (Datei GPU014.TXT, Knopf „Untis-Vertretungen…“). Bei „Untis“ fragt „Online veröffentlichen“ nach.
        /// </summary>
        public string Quelle { get; set; } = QuelleVertretungsmodul;

        /// <summary>PB-2: Name der aktiven Verbindung („Entwicklung“ | „Testserver“).</summary>
        [JsonIgnore] public string Verbindung { get; set; } = Entwicklung;
        [JsonIgnore] public string AdresseEntwicklung { get; set; } = "";
        [JsonIgnore] public string AdresseTestserver { get; set; } = "";
        /// <summary>Woher der Schlüssel kommt (für Anzeige und Tooltip, nie der Schlüssel selbst).</summary>
        [JsonIgnore] public string SchluesselHerkunft { get; set; } = "";
        /// <summary>true = in vertretung_online.json steht noch ein Schlüssel (alte Form, nicht umgezogen).</summary>
        [JsonIgnore] public bool SchluesselInDatei { get; set; }
        /// <summary>
        /// Grund, warum nicht gesendet werden kann (keine bzw. ungültige Adresse, http nach außen, Schlüssel fehlt oder gehört
        /// zu einer anderen Adresse) – sonst null.
        /// </summary>
        [JsonIgnore] public string? Problem { get; set; }

        [JsonIgnore]
        public bool QuelleIstUntis => string.Equals(Quelle, QuelleUntis, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IstTestserver => string.Equals(Verbindung, Testserver, StringComparison.OrdinalIgnoreCase);

        /// <summary>Adresse der genannten Verbindung.</summary>
        public string AdresseVon(string verbindung) =>
            string.Equals(verbindung, Testserver, StringComparison.OrdinalIgnoreCase) ? AdresseTestserver : AdresseEntwicklung;
    }

    public partial class OnlineSync
    {
        private const string EinstellungDatei = "vertretung_online.json";
        // P1: Proxy von Windows mit Anmeldung des Windows-Benutzers (OnlineVerbindung).
        private static readonly HttpClient _http = OnlineVerbindung.NeuerClient(TimeSpan.FromSeconds(20));

        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";

        // ---- Push ----

        /// <summary>Pusht die GEÄNDERTEN Zellen des effektiven Tages. Gibt die Serverantwort zurück.</summary>
        public async Task<string> PushTagAsync(string schuljahr, DateTime datum, Tagesplan tp)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
                throw new InvalidOperationException("Keine Server-Adresse eingetragen („Server verbinden…“).");

            return await PushAsync(BaueDto(schuljahr, datum, tp));
        }

        /// <summary>
        /// U-1b: pusht einen fertigen Tag (z. B. aus der Untis-Datei). Der Tag wird in der API komplett ersetzt –
        /// ein Tag ohne Einträge wird dort leer.
        /// </summary>
        public async Task<string> PushAsync(PlanPushDto dto, System.Threading.CancellationToken abbruch = default)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
                throw new InvalidOperationException("Keine Server-Adresse eingetragen („Server verbinden…“).");
            var url = BaseUrl.TrimEnd('/') + "/api/plan/push";
            // PB-2 (V3): den Schlüssel nie unverschlüsselt an einen anderen Rechner.
            if (OnlineVerbindung.ZielFehler(url) is string ziel) throw new InvalidOperationException(ziel);

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

            // V4b (276, 282): freie Stunden laut Kalender (auch mit Sondergrund) gehen nie als Änderung an die App –
            // die App zeigt sie über den Kalender als „Frei“. Zählwerte, Anlässe und Kommentare gehen nie mit.
            foreach (var z in tp.Zellen.Where(z => z.IstGeaendert && z.FreiLautKalender == null
                                                   && z.Eintrag?.Anlass != EntfallAnlass.FreieZeit))
            {
                dto.Eintraege.Add(new PlanEintragDto
                {
                    Stunde = z.Stunde,
                    Klassen = string.Join(",", z.Klassen),
                    Fach = z.Fach,
                    Lehrer = string.IsNullOrWhiteSpace(z.Lehrer) ? null : z.Lehrer,
                    OriginalLehrer = string.IsNullOrWhiteSpace(z.OriginalLehrer) ? null : z.OriginalLehrer,
                    // R3-D: Raum laut Raumplan (wie im Grundplan der App) bzw. der neue Raum der Buchung.
                    Raum = string.IsNullOrWhiteSpace(z.Raum) ? null : z.Raum,
                    RaumGeaendert = z.RaumGeaendert ? true : null,
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
                    text += "\n\n" + SchluesselFalschText(LadeEinstellungen(ordner));
                else if (text.StartsWith("Server 404", StringComparison.Ordinal))
                    text += "\n\nPrüfe die Server-Adresse: nur die Adresse ohne Pfad (z. B. https://probe.example.de)." +
                            "\n" + OrtText(ordner);
                else
                {
                    // P1: typische Antworten eines Reverse Proxys bzw. Proxys (403, 407, 502–504)
                    // P6a: dazu 403 „Adresse nicht freigegeben“ und 429 „Zu viele Anfragen“ – mit dem Text der API statt JSON.
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"^Server (\d{3}): ?(.*)$",
                        System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (m.Success && OnlineVerbindung.FehlerText(m.Groups[2].Value) is string apiText)
                        text = $"Server {m.Groups[1].Value}: {apiText}";
                    string? s = m.Success ? OnlineVerbindung.StatusErklaerung(int.Parse(m.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture)) : null;
                    if (s != null) text += "\n\n" + s;
                }
            }
            // P1 (Entscheidung 172): Proxy, Firewall, DNS, Zertifikat, Zeitüberschreitung verständlich erklären.
            if (!text.StartsWith("Server ", StringComparison.Ordinal))
            {
                string? erkl = OnlineVerbindung.Erklaerung(ex);
                if (erkl != null) text += "\n\n" + erkl + "\n" + OrtText(ordner);
            }
            return text;
        }

        // PB-2: LadeEinstellungen, Speichern, Umzug des Schlüssels und „Server verbinden…“ stehen in OnlineSync_Verbindung.cs.
    }
}
