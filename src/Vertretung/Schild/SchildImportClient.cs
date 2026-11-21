#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildImportClient – Phase 5.2b-2b: der Weg Desktop → API für den
    //  SchILD-Import (wie GrundplanSync: Server-Adresse + X-Api-Key aus
    //  vertretung_online.json neben der Plandatei).
    //
    //    GET  api/schild/status          – Betriebsart, Kalender, Schülerzahlen, letzter Import, Vorschlag
    //    POST api/schild/import          – Probelauf (Vorschau) bzw. Import
    //    POST api/schild/seed-entfernen  – nur Entwicklung
    //
    //  Fehler kommen als SchildServerFehler mit verständlichem Text (auch für
    //  Proxy, Firewall, DNS und Zertifikat – OnlineVerbindung). Der API-Key
    //  erscheint nie in einer Meldung.
    // =====================================================================

    // ---- Antworten der API (camelCase, siehe VertretungApi/Controllers/SchildController.cs) ----

    public sealed class SchildStatusAntwort
    {
        public string Betrieb { get; set; } = "";
        /// <summary>true = Entwicklung (nimmt nur Pseudodaten), false = Echtbetrieb (nur echte Daten).</summary>
        public bool Pseudo { get; set; }
        public string Nimmt { get; set; } = "";
        public string Schuljahr { get; set; } = "";
        public int Abschnitt { get; set; }
        public List<string> Schuljahre { get; set; } = new();
        public SchildZeitraumDto? Kalender { get; set; }
        public DateOnly? HalbjahrAb { get; set; }
        public List<SchildGrundplanDto> Grundplaene { get; set; } = new();
        public SchildSchuelerZahlenDto Schueler { get; set; } = new();
        public bool SeedEntfernenMoeglich { get; set; }
        public SchildLetzterImportDto? LetzterImport { get; set; }
        public DateOnly? FruehestensAb { get; set; }
        public DateOnly? VorschlagGiltAb { get; set; }
        /// <summary>5.2c-1: Kennung des Schlüssels beim letzten Import dieser Art (null = noch keiner bzw. vor 5.2c).</summary>
        public string? KennungLetzterImport { get; set; }
    }

    public sealed class SchildZeitraumDto
    {
        public DateOnly Start { get; set; }
        public DateOnly Ende { get; set; }
    }

    public sealed class SchildGrundplanDto
    {
        public string Bezeichnung { get; set; } = "";
        public DateOnly GueltigAb { get; set; }
    }

    public sealed class SchildSchuelerZahlenDto
    {
        public int Echt { get; set; }
        public int Seed { get; set; }
        public int PseudoImportiert { get; set; }
        /// <summary>Importierte Schüler der Art, die diese API annimmt (Entwicklung: Pseudo, Echtbetrieb: echt).</summary>
        public int Importiert { get; set; }
    }

    public sealed class SchildLetzterImportDto
    {
        public int Id { get; set; }
        public DateTime Zeit { get; set; }
        public string Akteur { get; set; } = "";
        public int Abschnitt { get; set; }
        public DateOnly GiltAb { get; set; }
        public int Neu { get; set; }
        public int Geaendert { get; set; }
        public int Klassenwechsel { get; set; }
        public int Kurswechsel { get; set; }
        public int Abgaenge { get; set; }
        public int Unveraendert { get; set; }
        public int Uebersprungen { get; set; }
        public string? Kennung { get; set; }
        public bool NeuZuordnung { get; set; }
        public int Zusammengefuehrt { get; set; }
        public int NeuZugeordnet { get; set; }
    }

    public sealed class SchildSperreDto
    {
        public string Art { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public sealed class SchildZahlenDto
    {
        public int ImExport { get; set; }
        public int Neu { get; set; }
        public int NameGeaendert { get; set; }
        public int Klassenwechsel { get; set; }
        public int Kurswechsel { get; set; }
        public int StatusGeaendert { get; set; }
        public int AbgangLautSchild { get; set; }
        public int FehltImExport { get; set; }
        public int Unveraendert { get; set; }
        public int AbgangNichtInDatenbank { get; set; }
        public int UnbekannteSchluessel { get; set; }
        public int NeueLerngruppen { get; set; }
        public int GruppenAusSchild { get; set; }
        // 5.2c-1
        public int DieselbePerson { get; set; }
        public int Zusammengefuehrt { get; set; }
        public int ZuordnungPaare { get; set; }
        public int NeuZugeordnet { get; set; }
        // 5.2c-2
        public int KlassenleitungNeu { get; set; }
        public int KlassenleitungGeaendert { get; set; }
        public int Lehrkraefte { get; set; }
        public int LehrkraefteNeu { get; set; }
        public int LehrkraefteGeaendert { get; set; }
        public int LehrkraefteEntfernt { get; set; }
        public int PaarGruppen { get; set; }
        public int PaarGruppenMitVorschlag { get; set; }
    }

    /// <summary>Zeile der Vorschau (enthält Namen – bleibt im Fenster, nie im Bericht oder Log).</summary>
    public sealed class SchildVorschauZeileDto
    {
        public string Kategorie { get; set; } = "";
        public int? Id { get; set; }
        public string Name { get; set; } = "";
        public string? Klasse { get; set; }
        public string Stufe { get; set; } = "";
        public string? Aenderung { get; set; }
        public bool Vorausgewaehlt { get; set; }
        public bool KontoWirdGesperrt { get; set; }
        public string? Hinweis { get; set; }
        /// <summary>5.2c-1: bei Paaren die Prüfsumme des Schülers im Export.</summary>
        public string? PartnerExtId { get; set; }
        /// <summary>5.2c-1: Paar ist in dieser Vorschau angewendet.</summary>
        public bool Gewaehlt { get; set; }
        /// <summary>5.2c-2: Gruppe voneinander abhängiger Paare (Namensgleiche); null = eindeutiges Paar.</summary>
        public int? Gruppe { get; set; }
        /// <summary>5.2c-2: gehört zum Vorschlag seiner Gruppe (Neu-Zuordnung, „Vorschlag übernehmen“).</summary>
        public bool Vorschlag { get; set; }
    }

    public sealed class SchildUnbekanntDto
    {
        public string Schluessel { get; set; } = "";
        public string Art { get; set; } = "";
        public int Schueler { get; set; }
    }

    public sealed class SchildLerngruppeDto
    {
        public string Typ { get; set; } = "";
        public string Schluessel { get; set; } = "";
    }

    public sealed class SchildGruppeAusSchildDto
    {
        public string Typ { get; set; } = "";
        public string Schluessel { get; set; } = "";
        public int VonHand { get; set; }
        public int AusSchild { get; set; }
        public bool AbSchuljahresbeginn { get; set; }
    }

    /// <summary>Vorschau (Probelauf, Sperre beim Import oder neue Vorschau bei veralteter Prüfsumme).</summary>
    public sealed class SchildVorschauAntwort
    {
        public bool Probelauf { get; set; }
        public string Schuljahr { get; set; } = "";
        public int Abschnitt { get; set; }
        public DateOnly GiltAb { get; set; }
        public bool Pseudo { get; set; }
        public bool Neuzuordnung { get; set; }
        public string? Kennung { get; set; }
        public string? KennungLetzterImport { get; set; }
        public string? Grundplan { get; set; }
        public DateOnly? GrundplanAb { get; set; }
        public bool Gesperrt { get; set; }
        public List<SchildSperreDto> Sperren { get; set; } = new();
        public bool SeedEntfernenMoeglich { get; set; }
        public List<string> Hinweise { get; set; } = new();
        public SchildZahlenDto Zahlen { get; set; } = new();
        public List<SchildVorschauZeileDto> Schueler { get; set; } = new();
        public List<SchildUnbekanntDto> UnbekannteSchluessel { get; set; } = new();
        public List<SchildLerngruppeDto> NeueLerngruppen { get; set; } = new();
        public List<SchildGruppeAusSchildDto> GruppenAusSchild { get; set; } = new();
        public string Stand { get; set; } = "";
        /// <summary>Prüfsumme der Vorschau – geht beim Import zurück an die API.</summary>
        public string Vorschau { get; set; } = "";
        /// <summary>Bericht ohne Namen (Zahlen, Gruppenbezeichnungen).</summary>
        public string Bericht { get; set; } = "";
    }

    /// <summary>Antwort nach einem ausgeführten Import.</summary>
    public sealed class SchildImportErgebnisDto
    {
        public bool Importiert { get; set; }
        public int ProtokollId { get; set; }
        public DateTime Zeit { get; set; }
        public string Schuljahr { get; set; } = "";
        public int Abschnitt { get; set; }
        public DateOnly GiltAb { get; set; }
        public bool Pseudo { get; set; }
        public SchildZahlenDto Zahlen { get; set; } = new();
        public int AbgaengeBestaetigt { get; set; }
        public int Uebersprungen { get; set; }
        public bool Neuzuordnung { get; set; }
        public string? Kennung { get; set; }
        public List<string> Hinweise { get; set; } = new();
        public string Bericht { get; set; } = "";
    }

    public sealed class SchildSeedEntfernenDto
    {
        public int SeedSchueler { get; set; }
        public int ImportierteSchueler { get; set; }
        public string Hinweis { get; set; } = "";
    }

    /// <summary>Ausgang eines Imports: ausgeführt, gesperrt (Vorschau mit Sperren) oder Vorschau veraltet (neue Vorschau).</summary>
    public sealed class SchildImportAusgang
    {
        public SchildImportErgebnisDto? Ergebnis { get; init; }
        public SchildVorschauAntwort? Vorschau { get; init; }
        public bool Veraltet { get; init; }
    }

    /// <summary>Fehler beim Senden – Text ist für die Anzeige gedacht (ohne Schlüssel, ohne Namen).</summary>
    public sealed class SchildServerFehler : Exception
    {
        public int? Status { get; }
        public SchildServerFehler(string text, int? status = null, Exception? innen = null) : base(text, innen) => Status = status;
    }

    public sealed class SchildImportClient
    {
        private static readonly HttpClient Http = OnlineVerbindung.NeuerClient(Timeout.InfiniteTimeSpan);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly TimeSpan KurzeZeit = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LangeZeit = TimeSpan.FromMinutes(5);

        private readonly OnlineEinstellungen _einst;
        private readonly string _ordner;

        public SchildImportClient(OnlineEinstellungen einst, string ordner)
        {
            _einst = einst;
            _ordner = ordner;
        }

        public string Adresse => (_einst.BaseUrl ?? "").Trim().TrimEnd('/');

        public Uri? AdresseUri => Uri.TryCreate(Adresse, UriKind.Absolute, out var u) ? u : null;

        public async Task<SchildStatusAntwort> StatusAsync(string schuljahr, int abschnitt)
        {
            string url = $"/api/schild/status?schuljahr={Uri.EscapeDataString(schuljahr)}&abschnitt={abschnitt}";
            var (code, body) = await SendeAsync(HttpMethod.Get, url, null, KurzeZeit);
            if (code != 200) throw Fehler(code, body, "Status");
            return Lies<SchildStatusAntwort>(body, "Status");
        }

        public async Task<SchildVorschauAntwort> VorschauAsync(SchildImportNutzlast nutzlast)
        {
            var (code, body) = await SendeAsync(HttpMethod.Post, "/api/schild/import", nutzlast, LangeZeit);
            if (code == 200 || code == 409) return Lies<SchildVorschauAntwort>(body, "Vorschau");
            throw Fehler(code, body, "Vorschau");
        }

        public async Task<SchildImportAusgang> ImportierenAsync(SchildImportNutzlast nutzlast)
        {
            var (code, body) = await SendeAsync(HttpMethod.Post, "/api/schild/import", nutzlast, LangeZeit);
            if (code == 200) return new SchildImportAusgang { Ergebnis = Lies<SchildImportErgebnisDto>(body, "Import") };
            if (code == 409)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("vorschauVeraltet", out var v)
                        && v.ValueKind == JsonValueKind.True && root.TryGetProperty("vorschau", out var neu)
                        && neu.ValueKind == JsonValueKind.Object)
                        return new SchildImportAusgang
                        {
                            Veraltet = true,
                            Vorschau = neu.Deserialize<SchildVorschauAntwort>(Json)
                        };
                }
                catch (JsonException) { /* unten als Sperre lesen */ }
                return new SchildImportAusgang { Vorschau = Lies<SchildVorschauAntwort>(body, "Import") };
            }
            throw Fehler(code, body, "Import");
        }

        public async Task<SchildSeedEntfernenDto> SeedEntfernenAsync()
        {
            var (code, body) = await SendeAsync(HttpMethod.Post, "/api/schild/seed-entfernen", null, LangeZeit);
            if (code != 200) throw Fehler(code, body, "Seed-Schüler entfernen");
            return Lies<SchildSeedEntfernenDto>(body, "Seed-Schüler entfernen");
        }

        // ---------------------------------------------------------------

        private async Task<(int Code, string Body)> SendeAsync(HttpMethod methode, string pfad, object? inhalt, TimeSpan zeit)
        {
            if (AdresseUri == null)
                throw new SchildServerFehler("Keine gültige Server-Adresse in den Online-Einstellungen.\nEinstellungen: " +
                                             OnlineSync.EinstellungsPfad(_ordner));
            using var cts = new CancellationTokenSource(zeit);
            using var req = new HttpRequestMessage(methode, Adresse + pfad);
            if (!string.IsNullOrWhiteSpace(_einst.ApiKey)) req.Headers.Add("X-Api-Key", _einst.ApiKey);
            if (inhalt != null) req.Content = JsonContent.Create(inhalt, inhalt.GetType(), options: Json);
            try
            {
                using var resp = await Http.SendAsync(req, cts.Token);
                string body = await resp.Content.ReadAsStringAsync(cts.Token);
                return ((int)resp.StatusCode, body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                string erkl = OnlineVerbindung.Erklaerung(ex) ??
                              (ex is OperationCanceledException ? OnlineVerbindung.TextZeit : ex.Message);
                throw new SchildServerFehler($"Keine Verbindung zu {Adresse}.\n\n{erkl}\n\nEinstellungen: " +
                                             OnlineSync.EinstellungsPfad(_ordner), null, ex);
            }
        }

        private T Lies<T>(string body, string was) where T : class
        {
            try
            {
                var t = JsonSerializer.Deserialize<T>(body, Json);
                if (t != null) return t;
            }
            catch (JsonException) { }
            throw new SchildServerFehler($"{was}: Die Antwort der API ist unverständlich – passt die API zu diesem " +
                                         "Desktop-Stand (5.2b-2a oder neuer)?");
        }

        private SchildServerFehler Fehler(int code, string body, string was)
        {
            string? text = FehlerAusAntwort(body);
            string erkl = code switch
            {
                401 => "Der API-Key passt nicht zu „Api:PushKey“ der API.\nEinstellungen: " + OnlineSync.EinstellungsPfad(_ordner),
                404 => "Die API kennt diese Funktion nicht (404). Stimmt die Server-Adresse (nur die Adresse, ohne Pfad)? Ist " +
                       "die API auf dem Stand 5.2b-2a oder neuer? „Seed-Schüler entfernen“ gibt es nur in der Entwicklung.\n" +
                       "Einstellungen: " + OnlineSync.EinstellungsPfad(_ordner),
                400 => "Die API hat die Nutzlast abgelehnt.",
                409 => "Die API hat abgelehnt.",
                413 => "Die Nutzlast ist dem Server zu groß (HTTP 413) – die Grenze des Reverse Proxys erhöhen.",
                500 => "Fehler in der API (HTTP 500) – im Log der API steht die Ursache.",
                _ => OnlineVerbindung.StatusErklaerung(code) ?? $"Unerwartete Antwort der API (HTTP {code})."
            };
            return new SchildServerFehler($"{was} fehlgeschlagen.\n\n{erkl}" + (text != null && code != 401 ? "\n\n" + text : ""), code);
        }

        /// <summary>Text der Fehlerantwort: {fehler, details[]} oder eine Zeichenkette; sonst gekürzter Rohtext.</summary>
        private static string? FehlerAusAntwort(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                if (r.ValueKind == JsonValueKind.String) return r.GetString();
                if (r.ValueKind == JsonValueKind.Object)
                {
                    var teile = new List<string>();
                    if (r.TryGetProperty("fehler", out var f) && f.ValueKind == JsonValueKind.String) teile.Add(f.GetString() ?? "");
                    if (r.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Array)
                        teile.AddRange(d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                                        .Select(x => "• " + x.GetString()).Take(20));
                    if (r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && teile.Count == 0)
                        teile.Add(t.GetString() ?? "");
                    if (teile.Count > 0) return string.Join("\n", teile);
                }
            }
            catch (JsonException) { }
            string roh = body.Trim();
            return roh.Length > 300 ? roh[..300] + " …" : roh;
        }
    }
}
