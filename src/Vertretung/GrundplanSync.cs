#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClosedXML.Excel;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  GrundplanSync – Phase 2, Schritt 1: der Desktop pusht den GRUNDPLAN
    //  (Wochenraster aus der Plandatei) samt Schuljahreskalender an die
    //  VertretungApi. Endpoint: POST /api/grundplan/push (Header X-Api-Key).
    //
    //  Aufbau der Slots: je (Wochentag, Stunde) × UNr × Unterrichtsteil ×
    //  Klasse(n)-Eintrag eine Zeile. Jede Zeile trägt einen LerngruppeKey:
    //    Klasse   "05A"-Muster                  -> Key = "05A"
    //    Kurs     Klasse(n) = EF/Q1/Q2          -> Key = Schülergruppe der UV
    //                                              (z. B. "BIG1_EF_L68"), sonst "EF BIG1"
    //    Sonstige AG, Förder-/Differenz.-Gruppen -> Key = Schülergruppe bzw. Bezeichnung
    //  Art: Unterricht | VBer | UV | Pseudo (PSF…, nicht zu vertreten) |
    //       Sperre ("ufrei"-Blocker der Teilzeitkräfte).
    //
    //  Die Plandatei wird nur gelesen (die Schülergruppe liest dieser Code
    //  selbst aus dem UV-Blatt; PlanLeser bleibt unverändert).
    // =====================================================================

    // ---- DTOs (spiegeln die API) ----

    public class GrundplanPushDto
    {
        public string Schuljahr { get; set; } = "";
        public DateOnly GueltigAb { get; set; }
        public string? Bezeichnung { get; set; }
        public KalenderPushDto? Kalender { get; set; }
        public List<GrundplanSlotDto> Slots { get; set; } = new();
    }

    public class KalenderPushDto
    {
        public DateOnly Start { get; set; }
        public DateOnly Ende { get; set; }
        public List<string> RasterTage { get; set; } = new();
        public List<FreieZeitPushDto> FreieZeiten { get; set; } = new();
    }

    public class FreieZeitPushDto
    {
        public string Typ { get; set; } = "";
        public DateOnly Von { get; set; }
        public DateOnly Bis { get; set; }
        public string? Bezeichnung { get; set; }
        public string? Klassen { get; set; }
    }

    public class GrundplanSlotDto
    {
        public string WTag { get; set; } = "";
        public int WTagIndex { get; set; }
        public int Stunde { get; set; }
        public int UNr { get; set; }
        public string Lehrer { get; set; } = "";
        public string Fach { get; set; } = "";
        public string? Fachraum { get; set; }
        public string? Stammraum { get; set; }
        public string LerngruppeTyp { get; set; } = "Klasse";
        public string LerngruppeKey { get; set; } = "";
        public string? Klasse { get; set; }
        public string? Schuelergruppe { get; set; }
        public string Art { get; set; } = "Unterricht";
    }

    public static class GrundplanSync
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private static readonly Regex KlassenMuster =
            new(@"^\d{2}[A-Za-z]{1,2}$", RegexOptions.Compiled);

        private static readonly HashSet<string> OberstufenStufen =
            new(StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };

        // ---- Push ----

        /// <summary>Sendet den Grundplan. Gibt die Serverantwort (JSON-Text) zurück.</summary>
        public static async Task<string> PushAsync(OnlineEinstellungen einst, GrundplanPushDto dto)
        {
            if (string.IsNullOrWhiteSpace(einst.BaseUrl))
                throw new InvalidOperationException("Keine Server-URL konfiguriert (Online-Einstellungen).");

            var url = einst.BaseUrl.TrimEnd('/') + "/api/grundplan/push";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(einst.ApiKey)) req.Headers.Add("X-Api-Key", einst.ApiKey);
            req.Content = JsonContent.Create(dto);

            using var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Server {(int)resp.StatusCode}: {body}");
            return body;
        }

        /// <summary>
        /// Wertet die Serverantwort aus: true, wenn eine vorhandene Version mit
        /// demselben Gültig-ab-Datum ersetzt wurde (ASP.NET liefert camelCase).
        /// </summary>
        public static bool WarVersionErsetzt(string antwortJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(antwortJson);
                return doc.RootElement.TryGetProperty("versionErsetzt", out var v)
                       && v.ValueKind == JsonValueKind.True;
            }
            catch { return false; }
        }

        // ---- DTO aufbauen ----

        /// <param name="hinweis">
        /// Nicht null, wenn die Schülergruppen aus dem UV-Blatt nicht gelesen werden
        /// konnten (z. B. Datei in Excel geöffnet). Die Kurse bekommen dann den
        /// Ersatzschlüssel „Stufe Fach" – das sollte vor dem Push angezeigt werden,
        /// weil sich die Kursschlüssel dadurch gegenüber früheren Pushes ändern.
        /// </param>
        public static GrundplanPushDto Baue(
            string schuljahr, SchuljahrKalender kalender, PlanDaten plan, DateTime gueltigAb,
            out string? hinweis)
        {
            var dto = new GrundplanPushDto
            {
                Schuljahr = schuljahr,
                GueltigAb = DateOnly.FromDateTime(gueltigAb.Date),
                Bezeichnung = plan.GewaehltePlanSpalte,
                Kalender = BaueKalender(kalender)
            };

            var gruppen = LiesSchuelergruppen(plan.PlanPfad, out hinweis);

            // Reihenfolge der Tage wie im Raster (TryAdd: robust auch bei „Mo"/„mo").
            var rang = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plan.RasterTage.Count; i++) rang.TryAdd(plan.RasterTage[i], i);

            var gesehen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in plan.Belegung
                .OrderBy(k => rang.TryGetValue(k.Key.WTag, out int r) ? r : 99)
                .ThenBy(k => k.Key.Stunde))
            {
                string wtag = kv.Key.WTag;
                int stunde = kv.Key.Stunde;
                int idx = SchuljahrKalender.WochentagIndex(wtag);
                int isoTag = idx < 0 ? 0 : (idx == 0 ? 7 : idx);   // 1=Mo … 7=So

                foreach (int unr in kv.Value)
                {
                    if (!plan.Unterrichte.TryGetValue(unr, out var teile)) continue;

                    foreach (var teil in teile)
                    {
                        string fach = Regex.Replace(teil.Fach.Trim(), @"\s+", " ");
                        string art = teil.IstUv ? "UV"
                                   : teil.IstVber ? "VBer"
                                   : teil.IstPseudo ? "Pseudo"
                                   : string.Equals(fach, "ufrei", StringComparison.OrdinalIgnoreCase) ? "Sperre"
                                   : "Unterricht";

                        gruppen.TryGetValue(SgKey(unr, teil.Lehrer, teil.Fach), out var sg);

                        var tokens = teil.Klassen.Count > 0 ? teil.Klassen : new List<string> { "" };
                        foreach (var tok in tokens)
                        {
                            var (typ, key) = Lerngruppe(tok, fach, sg);

                            string dedup = $"{wtag}|{stunde}|{unr}|{teil.Lehrer}|{fach}|{typ}|{key}";
                            if (!gesehen.Add(dedup)) continue;

                            dto.Slots.Add(new GrundplanSlotDto
                            {
                                WTag = wtag,
                                WTagIndex = isoTag,
                                Stunde = stunde,
                                UNr = unr,
                                Lehrer = teil.Lehrer,
                                Fach = fach,
                                Fachraum = teil.Fachraum,
                                Stammraum = teil.Stammraum,
                                LerngruppeTyp = typ,
                                LerngruppeKey = key,
                                Klasse = tok,
                                Schuelergruppe = string.IsNullOrWhiteSpace(sg) ? null : sg,
                                Art = art
                            });
                        }
                    }
                }
            }
            return dto;
        }

        private static (string typ, string key) Lerngruppe(string token, string fach, string? sg)
        {
            string t = token.Trim();
            bool hatSg = !string.IsNullOrWhiteSpace(sg);

            if (KlassenMuster.IsMatch(t)) return ("Klasse", t.ToUpperInvariant());
            if (OberstufenStufen.Contains(t))
                return ("Kurs", hatSg ? sg!.Trim() : $"{t.ToUpperInvariant()} {fach.Replace(" ", "")}");
            return ("Sonstige", hatSg ? sg!.Trim() : t);
        }

        private static KalenderPushDto BaueKalender(SchuljahrKalender kal) => new()
        {
            Start = DateOnly.FromDateTime(kal.Start.Date),
            Ende = DateOnly.FromDateTime(kal.Ende.Date),
            RasterTage = new List<string>(kal.RasterTage),
            FreieZeiten = kal.FreieZeiten.Select(f => new FreieZeitPushDto
            {
                Typ = f.Typ.ToString(),
                Von = DateOnly.FromDateTime(f.Von.Date),
                Bis = DateOnly.FromDateTime(f.Bis.Date),
                Bezeichnung = f.Bezeichnung,
                Klassen = string.Join(",", f.BetroffeneKlassen)
            }).ToList()
        };

        // ---- Schülergruppe aus dem UV-Blatt (PlanLeser kennt die Spalte nicht) ----

        private static string SgKey(int unr, string lehrer, string fach)
            => $"{unr}|{lehrer.Trim()}|{fach.Trim()}";

        private static Dictionary<string, string> LiesSchuelergruppen(string planPfad, out string? fehler)
        {
            fehler = null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var wb = new XLWorkbook(planPfad);
                if (!wb.Worksheets.TryGetWorksheet("UV", out var ws))
                {
                    fehler = "Blatt 'UV' nicht gefunden – Kurse werden ohne Schülergruppe geschlüsselt.";
                    return map;
                }

                var spalten = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in ws.Row(1).CellsUsed())
                {
                    string h = c.GetString().Trim();
                    if (h.Length > 0 && !spalten.ContainsKey(h)) spalten[h] = c.Address.ColumnNumber;
                }

                int cU = Finde(spalten, "U-Nr", "UNr");
                int cLe = Finde(spalten, "Lehrer");
                int cFa = Finde(spalten, "Fach");
                int cSg = Finde(spalten, "Schülergruppe", "Schuelergruppe");
                if (cU < 0 || cLe < 0 || cFa < 0 || cSg < 0)
                {
                    fehler = "In der UV fehlt eine der Spalten U-Nr, Lehrer, Fach oder Schülergruppe – " +
                             "Kurse werden ohne Schülergruppe geschlüsselt.";
                    return map;
                }

                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    string uStr = row.Cell(cU).GetString().Trim();
                    if (!int.TryParse(uStr, out int unr))
                    {
                        if (double.TryParse(uStr, out var ud)) unr = (int)ud; else continue;
                    }
                    string sg = row.Cell(cSg).GetString().Trim();
                    if (sg.Length == 0) continue;

                    string k = SgKey(unr, row.Cell(cLe).GetString(), row.Cell(cFa).GetString());
                    if (!map.ContainsKey(k)) map[k] = sg;
                }
            }
            catch (Exception ex)
            {
                // Datei evtl. in Excel geöffnet o. Ä.: ohne Schülergruppen weiterarbeiten
                // (Kurs-Key fällt dann auf "<Stufe> <Fach>" zurück) – aber Bescheid geben.
                fehler = "Die Schülergruppen konnten nicht aus der Plandatei gelesen werden " +
                         "(ist sie in Excel geöffnet?). Kurse werden dann als „Stufe Fach\" geschlüsselt.\n" +
                         "Fehler: " + ex.Message;
                map.Clear();
            }
            return map;
        }

        private static int Finde(Dictionary<string, int> map, params string[] namen)
        {
            foreach (var n in namen)
                if (map.TryGetValue(n, out int c)) return c;
            return -1;
        }
    }
}
