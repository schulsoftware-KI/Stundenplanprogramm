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
    // =====================================================================
    public static class OnlineVerbindung
    {
        public const string TextDns =
            "Der Servername ließ sich nicht auflösen. Stimmt die Server-Adresse in den Online-Einstellungen? Im Schulnetz " +
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
                DefaultProxyCredentials = CredentialCache.DefaultCredentials
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

        /// <summary>Erklärung zu einem HTTP-Fehlercode, der meist nicht von der API selbst kommt – sonst null.</summary>
        public static string? StatusErklaerung(int status) => status switch
        {
            403 => "Zugriff verweigert (HTTP 403). Ist für die Desktop-Schnittstellen eine Adressbeschränkung eingestellt " +
                   "(nur die Adresse der Schule) – und sendet dieser Rechner über eine andere Adresse? Sonst blockiert ein " +
                   "Proxy der Schule die Adresse.",
            407 => TextProxyAnmeldung,
            502 or 503 or 504 => $"Der Server ist erreichbar, aber die API dahinter antwortet nicht (HTTP {status}) – der " +
                                 "Dienst ist gestoppt oder startet gerade. Später erneut versuchen bzw. die Betreuung des " +
                                 "Servers informieren.",
            _ => null
        };

        /// <summary>true, wenn die Adresse auf diesen Rechner zeigt (localhost, 127.x, ::1).</summary>
        public static bool IstLokal(Uri uri)
            => uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
