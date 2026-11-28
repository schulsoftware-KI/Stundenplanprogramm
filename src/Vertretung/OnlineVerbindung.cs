#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  OnlineVerbindung – P1 (seit 18.11.2026, Entscheidung 172: gemieteter
    //  Server). Der Verwaltungsrechner sendet jetzt über das Internet. Hier:
    //
    //  - ein gemeinsamer HttpClient-Handler: Proxy aus den Windows-Einstellungen,
    //    bei einem Proxy mit Anmeldung die Anmeldedaten des Windows-Benutzers;
    //  - verständliche Meldungen bei Proxy-, Firewall-, DNS- und Zertifikats-
    //    fehlern sowie bei typischen Antworten eines Reverse Proxys (403, 407,
    //    502–504).
    //
    //  Genutzt von OnlineSync (Vertretungen, Untis), GrundplanSync und dem
    //  SchILD-Import. Meldungen nennen nur die Server-Adresse, nie den Schlüssel.
    //
    //  P6a (Entscheidungen 229, 230): Die API kann die Desktop-Schnittstellen auf die
    //  Adressen der Schule beschränken (403 „Adresse nicht freigegeben“) und bremst zu
    //  viele Anfragen (429 mit Wartezeit). Beides wird hier verständlich erklärt; der
    //  Text der API („fehler“) erscheint statt der rohen JSON-Antwort.
    //
    //  PB-2 (V3, V4): Anfragen mit Schlüssel nur über https – außer an diesen Rechner
    //  (ZielFehler) – und ohne automatische Weiterleitung: HttpClient behielte den Kopf
    //  X-Api-Key auch bei einer Weiterleitung an eine andere Adresse.
    // =====================================================================
    public static class OnlineVerbindung
    {
        public const string TextDns =
            "Der Servername ließ sich nicht auflösen. Stimmt die Server-Adresse („Server verbinden…“)? Im Schulnetz " +
            "kann auch ein DNS-Filter oder Proxy der Grund sein – dann die IT bitten, die Adresse freizugeben.";

        public const string TextAbgelehnt =
            "Der Server hat die Verbindung abgelehnt: An dieser Adresse bzw. diesem Port läuft keine API. Entwicklung: Läuft " +
            "„dotnet run“ (http://localhost:5080)? Echtbetrieb: Server-Adresse prüfen (https://…, ohne Port und ohne Pfad).";

        public const string TextKeineAntwort =
            "Der Server antwortet nicht. Mögliche Gründe: Der Server ist aus oder nicht erreichbar, die Adresse stimmt nicht, " +
            "oder eine Firewall bzw. ein Proxy der Schule blockiert ausgehende Verbindungen (HTTPS, Port 443) – dann die IT " +
            "bitten, die Server-Adresse freizugeben.";

        public const string TextZertifikat =
            "Die sichere Verbindung (HTTPS) kam nicht zustande – das Zertifikat des Servers wird nicht anerkannt. Mögliche " +
            "Gründe: Das Zertifikat ist abgelaufen oder gilt für eine andere Adresse, die Uhrzeit dieses Rechners stimmt nicht, " +
            "oder ein Proxy bzw. Virenscanner der Schule prüft HTTPS-Verbindungen mit einem eigenen Zertifikat (dann die IT " +
            "bitten, die Server-Adresse davon auszunehmen). Es wurden keine Daten übertragen.";

        public const string TextProxy =
            "Der Proxy der Schule lässt die Verbindung nicht durch. Die Proxy-Einstellungen von Windows prüfen bzw. die IT " +
            "bitten, die Server-Adresse freizugeben.";

        public const string TextProxyAnmeldung =
            "Der Proxy der Schule verlangt eine Anmeldung, die mit dem Windows-Konto nicht gelang (HTTP 407). Die IT bitten, " +
            "die Server-Adresse ohne Anmeldung freizugeben.";

        public const string TextZeit =
            "Zeitüberschreitung – der Server hat nicht rechtzeitig geantwortet. Später erneut versuchen; tritt es immer wieder " +
            "auf, Verbindung und Server prüfen (auch eine Firewall kann Verbindungen ins Leere laufen lassen).";

        /// <summary>HttpClient mit dem Proxy von Windows (Anmeldung am Proxy mit dem Windows-Konto).</summary>
        public static HttpClient NeuerClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
                AllowAutoRedirect = false   // PB-2 (V4): der Schlüssel folgt keiner Weiterleitung
            };
            return new HttpClient(handler) { Timeout = timeout };
        }

        /// <summary>
        /// Erklärung für einen Fehler unterhalb von HTTP (DNS, Verbindung, Zertifikat, Proxy, Zeit) – null, wenn der
        /// Fehler keiner davon ist (z. B. eine Antwort der API mit Fehlercode).
        /// </summary>
        public static string? Erklaerung(Exception ex)
        {
            // Erst die genaueste Ursache in der Kette suchen …
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                switch (e)
                {
                    case AuthenticationException:
                        return TextZertifikat;
                    case SocketException se:
                        switch (se.SocketErrorCode)
                        {
                            case SocketError.HostNotFound:
                            case SocketError.NoData:
                            case SocketError.TryAgain:
                                return TextDns;
                            case SocketError.ConnectionRefused:
                                return TextAbgelehnt;
                            case SocketError.TimedOut:
                            case SocketError.HostUnreachable:
                            case SocketError.NetworkUnreachable:
                            case SocketError.HostDown:
                            case SocketError.NetworkDown:
                                return TextKeineAntwort;
                        }
                        break;
                    case TimeoutException:
                        return TextZeit;
                }
            }
            // … dann die Einordnung von HttpClient.
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is HttpRequestException h)
                {
                    if (h.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) return TextProxyAnmeldung;
                    switch (h.HttpRequestError)
                    {
                        case HttpRequestError.NameResolutionError: return TextDns;
                        case HttpRequestError.ConnectionError: return TextKeineAntwort;
                        case HttpRequestError.SecureConnectionError: return TextZertifikat;
                        case HttpRequestError.ProxyTunnelError: return TextProxy;
                    }
                }
                if (e is TaskCanceledException) return TextZeit;
            }
            return null;
        }

        public const string TextAdresse =
            "Zugriff verweigert (HTTP 403). Meldet die API „Adresse nicht freigegeben“, sind die Desktop-Schnittstellen nur " +
            "aus dem Schulnetz erlaubt (Einstellung „Zugriff:DesktopNurSchule“ des Servers) – und dieser Rechner sendet über " +
            "eine andere Adresse (z. B. von zu Hause, über ein Mobilfunknetz oder ein VPN). Im Schulnetz erneut versuchen bzw. " +
            "die Betreuung des Servers bitten, die Adresse der Schule in „Zugriff:Schuladressen“ einzutragen. Sonst blockiert " +
            "ein Proxy der Schule die Adresse.";

        public const string TextZuViele =
            "Zu viele Anfragen (HTTP 429) – die API bremst diesen Rechner kurz (Anfragebegrenzung). Einen Moment warten und " +
            "erneut versuchen; beim Veröffentlichen eines langen Zeitraums ggf. in Teilen. Tritt es oft auf, die Betreuung des " +
            "Servers bitten, „Begrenzung:DesktopJeMinute“ zu erhöhen.";

        public const string TextNurHttps =
            "über http:// ginge der Schlüssel unverschlüsselt durchs Netz. http:// gilt nur für diesen Rechner (localhost); " +
            "für jeden anderen Server die Adresse mit https:// eintragen („Server verbinden…“).";

        public const string TextWeiterleitung =
            "Der Server leitet auf eine andere Adresse weiter. Der Desktop folgt Weiterleitungen nicht (der Schlüssel ginge " +
            "sonst mit). Die Adresse so eintragen, wie sie nach dem Laden im Browser steht (meist https://…, ohne Pfad) – " +
            "unter „Server verbinden…“.";

        /// <summary>
        /// PB-2 (V3): Fehlertext, wenn an diese Adresse kein Schlüssel gehen darf (http:// an einen anderen Rechner bzw.
        /// keine gültige Adresse) – sonst null.
        /// </summary>
        public static string? ZielFehler(string url)
        {
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u)
                || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                return $"„{url}“ ist keine gültige Server-Adresse („Server verbinden…“).";
            if (u.Scheme == Uri.UriSchemeHttp && !IstLokal(u))
                return $"Nicht gesendet: Die Adresse {u.GetLeftPart(UriPartial.Authority)} beginnt mit http:// – " + TextNurHttps;
            return null;
        }

        /// <summary>Erklärung zu einem HTTP-Fehlercode, der meist nicht von der API selbst kommt – sonst null.</summary>
        public static string? StatusErklaerung(int status) => status switch
        {
            301 or 302 or 303 or 307 or 308 => $"HTTP {status}: " + TextWeiterleitung,
            403 => TextAdresse,
            407 => TextProxyAnmeldung,
            429 => TextZuViele,
            502 or 503 or 504 => $"Der Server ist erreichbar, aber die API dahinter antwortet nicht (HTTP {status}) – der " +
                                 "Dienst ist gestoppt oder startet gerade. Später erneut versuchen bzw. die Betreuung des " +
                                 "Servers informieren.",
            _ => null
        };

        /// <summary>
        /// P6a: Text „fehler“ aus einer JSON-Antwort der API (z. B. „Adresse nicht freigegeben …“, „Zu viele Anfragen – bitte
        /// in 12 Sekunden erneut versuchen.“) – sonst null.
        /// </summary>
        public static string? FehlerText(string? antwort)
        {
            if (string.IsNullOrWhiteSpace(antwort)) return null;
            var t = antwort.TrimStart();
            if (!t.StartsWith("{", StringComparison.Ordinal)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(t);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("fehler", out var f)
                    && f.ValueKind == System.Text.Json.JsonValueKind.String)
                    return f.GetString();
            }
            catch (System.Text.Json.JsonException) { }
            return null;
        }

        /// <summary>true, wenn die Adresse auf diesen Rechner zeigt (localhost, 127.x, ::1).</summary>
        public static bool IstLokal(Uri uri)
            => uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
