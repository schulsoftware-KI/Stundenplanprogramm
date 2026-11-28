#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  OnlineSync_Verbindung – PB-2 (Entscheidung 302, Rest von E-2 in P8):
    //  Verbindungen „Entwicklung“ und „Testserver“, eine davon aktiv.
    //
    //  vertretung_online.json (neben der Plandatei, oft OneDrive) enthält nur
    //  noch Adressen, Auswahl und Quelle:
    //    { "Format": 2, "Aktiv": "Entwicklung",
    //      "Verbindungen": { "Entwicklung": "http://localhost:5080", "Testserver": "" },
    //      "BaseUrl": "<Adresse der aktiven Verbindung>", "Quelle": "Vertretungsmodul" }
    //  „BaseUrl“ bleibt für ältere Desktop-Stände lesbar (Rückweg). Der
    //  Schlüssel liegt je Verbindung mit DPAPI im Benutzerprofil (PushSchluessel)
    //  und ist an die Adresse gebunden (V2).
    //
    //  Alte Form (bis PB-1b): { "BaseUrl", "ApiKey", "Quelle" } – wird weiter
    //  gelesen; der Schlüssel zieht nach Rückfrage um und wird aus der Datei
    //  gelöscht (SchluesselUmziehen). Zuordnung nach der Adresse: localhost →
    //  „Entwicklung“, sonst „Testserver“.
    // =====================================================================

    /// <summary>Inhalt von vertretung_online.json (alte und neue Form).</summary>
    internal sealed class OnlineDatei
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Format { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Aktiv { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Verbindungen { get; set; }
        public string? BaseUrl { get; set; }
        /// <summary>Nur die alte Form – wird nie geschrieben (immer null beim Speichern).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ApiKey { get; set; }
        public string? Quelle { get; set; }
    }

    public partial class OnlineSync
    {
        public const int DateiFormat = 2;

        private static readonly JsonSerializerOptions DateiLesen = new() { PropertyNameCaseInsensitive = true };
        private static readonly JsonSerializerOptions DateiSchreiben = new() { WriteIndented = true };

        /// <summary>F1: „Nein“ beim Umzug gilt bis zum nächsten Programmstart (dann wird wieder gefragt).</summary>
        private static bool _umzugAbgelehnt;

        // =====================================================
        //  Lesen
        // =====================================================

        /// <summary>Liest die Datei; null, wenn es sie nicht gibt oder sie nicht lesbar ist (dann fehler).</summary>
        private static OnlineDatei? LiesDatei(string ordner, out string? fehler)
        {
            fehler = null;
            try
            {
                var pfad = EinstellungsPfad(ordner);
                if (!File.Exists(pfad)) return null;
                return JsonSerializer.Deserialize<OnlineDatei>(File.ReadAllText(pfad), DateiLesen);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException or ArgumentException)
            {
                fehler = ex.Message;
                return null;
            }
        }

        /// <summary>true, wenn in vertretung_online.json noch ein Schlüssel steht (alte Form).</summary>
        public static bool DateiHatSchluessel(string ordner) =>
            LiesDatei(ordner, out _) is OnlineDatei d && !string.IsNullOrWhiteSpace(d.ApiKey);

        /// <summary>
        /// Adresse, Schlüssel und Quelle der aktiven Verbindung (wirft nie). Den Schlüssel liefert PushSchluessel – bzw.
        /// bei der alten Form noch die Datei (bis zum Umzug). Ist etwas nicht in Ordnung, steht der Grund in Problem.
        /// </summary>
        public static OnlineEinstellungen LadeEinstellungen(string ordner)
        {
            var d = LiesDatei(ordner, out string? lesefehler) ?? new OnlineDatei();
            var e = new OnlineEinstellungen
            {
                Quelle = string.IsNullOrWhiteSpace(d.Quelle) ? OnlineEinstellungen.QuelleVertretungsmodul : d.Quelle.Trim()
            };
            string basis = (d.BaseUrl ?? "").Trim();

            if (d.Format >= DateiFormat && d.Verbindungen != null)
            {
                e.AdresseEntwicklung = Wert(d.Verbindungen, OnlineEinstellungen.Entwicklung);
                e.AdresseTestserver = Wert(d.Verbindungen, OnlineEinstellungen.Testserver);
                e.Verbindung = string.Equals(d.Aktiv, OnlineEinstellungen.Testserver, StringComparison.OrdinalIgnoreCase)
                    ? OnlineEinstellungen.Testserver : OnlineEinstellungen.Entwicklung;
            }
            else
            {
                // Alte Form (bis PB-1b): eine Adresse. localhost → „Entwicklung“, sonst „Testserver“.
                bool lokal = basis.Length == 0 || IstLokaleAdresse(basis);
                e.Verbindung = lokal ? OnlineEinstellungen.Entwicklung : OnlineEinstellungen.Testserver;
                e.AdresseEntwicklung = lokal ? basis : "";
                e.AdresseTestserver = lokal ? "" : basis;
                // Rückweg (alte Datei nach PB-2): die Testserver-Adresse steht noch beim gespeicherten Schlüssel.
                if (e.AdresseTestserver.Length == 0
                    && PushSchluessel.Laden(OnlineEinstellungen.Testserver, out _) is PushSchluessel.Eintrag t)
                    e.AdresseTestserver = t.Adresse;
            }
            e.BaseUrl = e.AdresseVon(e.Verbindung);

            string? problem = null;
            if (!string.IsNullOrWhiteSpace(d.ApiKey))
            {
                e.ApiKey = d.ApiKey.Trim();
                e.SchluesselInDatei = true;
                e.SchluesselHerkunft = "steht noch in vertretung_online.json (nicht umgezogen)";
            }
            else
                problem = SchluesselHolen(e);

            if (e.BaseUrl.Length == 0)
                e.Problem = lesefehler != null
                    ? "vertretung_online.json ist nicht lesbar (" + lesefehler + ") – unter „Server verbinden…“ neu eintragen."
                    : $"Für die Verbindung „{e.Verbindung}“ ist keine Adresse eingetragen („Server verbinden…“).";
            else
                e.Problem = AdresseFehler(e.BaseUrl) ?? problem;
            return e;
        }

        private static string Wert(Dictionary<string, string> d, string name) =>
            d.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? "";

        /// <summary>Setzt ApiKey der aktiven Verbindung aus dem DPAPI-Speicher; liefert ggf. das Problem.</summary>
        private static string? SchluesselHolen(OnlineEinstellungen e)
        {
            var g = PushSchluessel.Laden(e.Verbindung, out string? fehler);
            if (g != null)
            {
                if (GleicheAdresse(g.Adresse, e.BaseUrl) || e.BaseUrl.Length == 0)
                {
                    if (e.BaseUrl.Length > 0) e.ApiKey = g.Schluessel;
                    e.SchluesselHerkunft = PushSchluessel.StandText(g) ?? "gespeichert";
                    return null;
                }
                e.SchluesselHerkunft = "gehört zu einer anderen Adresse";
                return $"Der gespeicherte Schlüssel der Verbindung „{e.Verbindung}“ gehört zur Adresse {g.Adresse}, eingestellt " +
                       $"ist {e.BaseUrl}. Er wird deshalb nicht gesendet – unter „Server verbinden…“ den Schlüssel für die " +
                       "eingestellte Adresse eingeben.";
            }
            if (!e.IstTestserver && !PushSchluessel.Vorhanden(e.Verbindung))
            {
                e.ApiKey = OnlineEinstellungen.EntwicklungSchluessel;   // V1
                e.SchluesselHerkunft = "Standard der Entwicklung";
                return null;
            }
            e.SchluesselHerkunft = "fehlt";
            return (fehler ?? $"Für „{e.Verbindung}“ ist kein Schlüssel gespeichert.") +
                   (e.IstTestserver ? " Auf dem Testserver zeigt „vertretung schluessel“ den Push-Schlüssel." : "");
        }

        // =====================================================
        //  Adressen
        // =====================================================

        public static bool IstLokaleAdresse(string adresse) =>
            Uri.TryCreate((adresse ?? "").Trim(), UriKind.Absolute, out var u) && OnlineVerbindung.IstLokal(u);

        /// <summary>
        /// Prüft eine Server-Adresse: gültig, http(s), ohne Pfad; http:// nur für diesen Rechner (V3). null = in Ordnung.
        /// </summary>
        public static string? AdresseFehler(string? adresse)
        {
            string a = (adresse ?? "").Trim();
            if (a.Length == 0) return "Keine Server-Adresse eingetragen.";
            if (!Uri.TryCreate(a, UriKind.Absolute, out var u)
                || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(u.Host))
                return $"„{a}“ ist keine gültige Server-Adresse (Beispiel: https://probe.example.de bzw. http://localhost:5080).";
            if (u.UserInfo.Length > 0 || (u.AbsolutePath.Length > 0 && u.AbsolutePath != "/") || u.Query.Length > 0
                || u.Fragment.Length > 0)
                return $"Nur die Adresse ohne Pfad eintragen (z. B. {u.GetLeftPart(UriPartial.Authority)}) – „{a}“ enthält mehr.";
            if (u.Scheme == Uri.UriSchemeHttp && !OnlineVerbindung.IstLokal(u))
                return $"„{a}“ beginnt mit http:// – " + OnlineVerbindung.TextNurHttps;
            return null;
        }

        /// <summary>Gleiche Adresse (Schema, Rechner, Port; ohne Groß-/Kleinschreibung und Schrägstrich am Ende)?</summary>
        public static bool GleicheAdresse(string? a, string? b)
        {
            static string Norm(string? x)
            {
                string t = (x ?? "").Trim();
                return Uri.TryCreate(t, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority).ToLowerInvariant()
                                                                      : t.TrimEnd('/').ToLowerInvariant();
            }
            return Norm(a) == Norm(b);
        }

        // =====================================================
        //  Schreiben
        // =====================================================

        /// <summary>Schreibt die Datei über eine Zwischendatei (schlägt etwas fehl, bleibt die alte Datei).</summary>
        private static void SchreibeText(string ordner, string json)
        {
            string pfad = EinstellungsPfad(ordner);
            string neu = pfad + ".neu";
            File.WriteAllText(neu, json);
            File.Move(neu, pfad, overwrite: true);
        }

        /// <summary>Speichert Verbindungen, Auswahl und Quelle (neue Form, nie ein Schlüssel).</summary>
        public static void SpeichereVerbindungen(string ordner, string aktiv, string adresseEntwicklung, string adresseTestserver,
                                                 string quelle)
        {
            if (DateiHatSchluessel(ordner))
                throw new InvalidOperationException("In vertretung_online.json steht noch ein Schlüssel – zuerst umziehen.");
            bool test = string.Equals(aktiv, OnlineEinstellungen.Testserver, StringComparison.OrdinalIgnoreCase);
            var d = new OnlineDatei
            {
                Format = DateiFormat,
                Aktiv = test ? OnlineEinstellungen.Testserver : OnlineEinstellungen.Entwicklung,
                Verbindungen = new Dictionary<string, string>
                {
                    [OnlineEinstellungen.Entwicklung] = (adresseEntwicklung ?? "").Trim(),
                    [OnlineEinstellungen.Testserver] = (adresseTestserver ?? "").Trim()
                },
                BaseUrl = ((test ? adresseTestserver : adresseEntwicklung) ?? "").Trim(),
                ApiKey = null,
                Quelle = string.IsNullOrWhiteSpace(quelle) ? OnlineEinstellungen.QuelleVertretungsmodul : quelle
            };
            SchreibeText(ordner, JsonSerializer.Serialize(d, DateiSchreiben));
        }

        /// <summary>Ändert nur die Quelle – alles andere in der Datei bleibt, wie es ist (auch die alte Form).</summary>
        public static void SpeichereQuelle(string ordner, string quelle)
        {
            string pfad = EinstellungsPfad(ordner);
            JsonObject obj;
            if (File.Exists(pfad))
            {
                var knoten = JsonNode.Parse(File.ReadAllText(pfad));
                obj = knoten as JsonObject ?? throw new IOException("vertretung_online.json ist nicht lesbar (kein JSON-Objekt).");
                // gleich geschriebene Schlüssel („quelle“) ersetzen statt verdoppeln
                foreach (var name in obj.Select(kv => kv.Key)
                             .Where(k => string.Equals(k, "Quelle", StringComparison.OrdinalIgnoreCase)).ToList())
                    obj.Remove(name);
            }
            else
            {
                obj = new JsonObject
                {
                    ["Format"] = DateiFormat,
                    ["Aktiv"] = OnlineEinstellungen.Entwicklung,
                    ["Verbindungen"] = new JsonObject
                    {
                        [OnlineEinstellungen.Entwicklung] = "",
                        [OnlineEinstellungen.Testserver] = ""
                    },
                    ["BaseUrl"] = ""
                };
            }
            obj["Quelle"] = quelle;
            SchreibeText(ordner, obj.ToJsonString(DateiSchreiben));
        }

        // =====================================================
        //  Umzug des Schlüssels aus der Datei (302, F1)
        // =====================================================

        /// <summary>
        /// Steht ein Schlüssel in vertretung_online.json: Rückfrage, dann in den DPAPI-Speicher der passenden Verbindung
        /// und aus der Datei löschen. true = kein Schlüssel (mehr) in der Datei.
        /// </summary>
        public static bool SchluesselUmziehen(Window owner, string ordner, string neinText)
        {
            var d = LiesDatei(ordner, out _);
            if (d == null || string.IsNullOrWhiteSpace(d.ApiKey)) return true;

            var e = LadeEinstellungen(ordner);
            string ziel = e.Verbindung;
            string adresse = e.BaseUrl;
            if (adresse.Length == 0 && !e.IstTestserver)
            {
                adresse = OnlineEinstellungen.EntwicklungAdresse;
                e.AdresseEntwicklung = adresse;
            }
            var vorhanden = PushSchluessel.Laden(ziel, out _);
            string pfad = EinstellungsPfad(ordner);
            string frage =
                "In der Datei\n" + pfad + "\nsteht der Push-Schlüssel noch lesbar. Die Datei liegt neben der Plandatei – oft in " +
                "OneDrive.\n\n" +
                $"Ja = in den geschützten Speicher dieses Windows-Benutzers umziehen (Verbindung „{ziel}“, Adresse {adresse}) und " +
                "aus der Datei löschen (empfohlen)." +
                (vorhanden != null ? $" Der dort gespeicherte Schlüssel von „{ziel}“ ({PushSchluessel.StandText(vorhanden)}) wird ersetzt." : "") +
                "\n\nNein = " + neinText;
            if (MessageBox.Show(owner, frage, "Schlüssel umziehen", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return false;

            try
            {
                PushSchluessel.Speichern(ziel, adresse, d.ApiKey.Trim());
                var neu = new OnlineDatei
                {
                    Format = DateiFormat,
                    Aktiv = ziel,
                    Verbindungen = new Dictionary<string, string>
                    {
                        [OnlineEinstellungen.Entwicklung] = e.AdresseEntwicklung,
                        [OnlineEinstellungen.Testserver] = e.AdresseTestserver
                    },
                    BaseUrl = adresse,
                    ApiKey = null,
                    Quelle = e.Quelle
                };
                SchreibeText(ordner, JsonSerializer.Serialize(neu, DateiSchreiben));
                if (DateiHatSchluessel(ordner))
                    throw new IOException("Der Schlüssel steht nach dem Speichern immer noch in der Datei.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Der Umzug ist fehlgeschlagen – vertretung_online.json bleibt, wie sie war.\n\n" + ex.Message,
                    "Schlüssel umziehen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            _umzugAbgelehnt = false;
            MessageBox.Show(owner, $"Umgezogen: Der Schlüssel der Verbindung „{ziel}“ liegt jetzt verschlüsselt unter\n" +
                                   PushSchluessel.Pfad(ziel) + "\n\nIn vertretung_online.json steht er nicht mehr.",
                "Schlüssel umziehen", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        // =====================================================
        //  Eine Stelle für alle, die senden (302)
        // =====================================================

        /// <summary>
        /// Adresse + Schlüssel der aktiven Verbindung für einen Vorgang mit Fenster: bietet einmal je Programmstart den
        /// Umzug an, öffnet „Server verbinden…“, wenn etwas fehlt. null = abgebrochen (nichts senden).
        /// </summary>
        public static OnlineEinstellungen? Verbindung(Window owner, string ordner)
        {
            if (!_umzugAbgelehnt && DateiHatSchluessel(ordner)
                && !SchluesselUmziehen(owner, ordner, "vorerst nichts ändern – der Schlüssel aus der Datei gilt weiter; gefragt " +
                                                      "wird beim nächsten Programmstart wieder."))
                _umzugAbgelehnt = true;

            var e = LadeEinstellungen(ordner);
            if (e.BaseUrl.Length == 0)
            {
                var neu = ServerVerbinden(owner, ordner);
                if (neu == null) return null;
                e = neu;
            }
            if (e.Problem != null)
            {
                if (MessageBox.Show(owner, e.Problem + "\n\n„Server verbinden…“ jetzt öffnen?", "Server verbinden",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                    return null;
                var neu = ServerVerbinden(owner, ordner);
                if (neu == null || neu.Problem != null) return null;
                e = neu;
            }
            return e;
        }

        /// <summary>
        /// Öffnet „Server verbinden…“. Steht noch ein Schlüssel in der Datei, muss er vorher umziehen. Liefert die neuen
        /// Einstellungen bzw. null (abgebrochen).
        /// </summary>
        public static OnlineEinstellungen? ServerVerbinden(Window owner, string ordner)
        {
            if (DateiHatSchluessel(ordner)
                && !SchluesselUmziehen(owner, ordner, "„Server verbinden…“ wird nicht geöffnet – es speichert keine Schlüssel mehr " +
                                                      "in der Datei. Bis zum Umzug gilt der Schlüssel aus der Datei unverändert."))
                return null;
            return ServerVerbindenDialog.Zeigen(owner, ordner) ? LadeEinstellungen(ordner) : null;
        }

        // =====================================================
        //  Texte und Anzeige „veröffentlicht nach: …“
        // =====================================================

        /// <summary>„Verbindung „Testserver“ (https://…) – ändern unter „Server verbinden…“.“</summary>
        public static string OrtText(OnlineEinstellungen e) =>
            $"Verbindung „{e.Verbindung}“ ({(e.BaseUrl.Length > 0 ? e.BaseUrl : "ohne Adresse")}) – ändern unter „Server verbinden…“.";

        public static string OrtText(string ordner) => OrtText(LadeEinstellungen(ordner));

        /// <summary>Text bei 401 (der Schlüssel selbst erscheint nie).</summary>
        public static string SchluesselFalschText(OnlineEinstellungen e) =>
            $"Der Schlüssel der Verbindung „{e.Verbindung}“ passt nicht zu „Api:PushKey“ dieses Servers." +
            (e.IstTestserver ? " Auf dem Testserver zeigt „vertretung schluessel“ den richtigen." : "") +
            "\n" + OrtText(e);

        private static readonly Brush Violett = Gefroren(Color.FromRgb(0x6A, 0x1B, 0x9A));

        private static Brush Gefroren(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>Anzeige „veröffentlicht nach: …“ (Wocheneditor, SchILD- und Untis-Fenster).</summary>
        public static void ZeigeZiel(TextBlock tb, string ordner)
        {
            var e = LadeEinstellungen(ordner);
            if (e.BaseUrl.Length == 0)
            {
                tb.Text = "veröffentlicht nach: – (keine Adresse, „Server verbinden…“)";
                tb.Foreground = Brushes.DarkOrange;
                tb.FontWeight = FontWeights.Normal;
                tb.ToolTip = e.Problem;
                return;
            }
            tb.Text = $"veröffentlicht nach: {e.Verbindung} · {e.BaseUrl}" + (e.Problem != null ? "  ⚠" : "") +
                      (e.SchluesselInDatei ? " (Schlüssel noch in der Datei)" : "");
            tb.Foreground = e.Problem != null ? Brushes.DarkOrange : e.IstTestserver ? Violett : Brushes.DimGray;
            tb.FontWeight = e.IstTestserver ? FontWeights.SemiBold : FontWeights.Normal;
            tb.ToolTip = e.Problem ?? $"Schlüssel: {e.SchluesselHerkunft}. Ändern unter „Server verbinden…“.";
        }
    }
}
