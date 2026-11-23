using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace VertretungApi.Helpers;

// =====================================================================
//  Phase 5.1: gemeinsame Prüfung des Push-Schlüssels (Header X-Api-Key) für alle
//  Endpunkte, an die der Desktop Daten schickt. Der Vergleich läuft über die SHA-256-
//  Prüfsummen in konstanter Zeit – so lässt sich der Schlüssel nicht Zeichen für Zeichen
//  über Antwortzeiten erraten.
//
//  Ohne Api:PushKey ist der Push offen. Das kommt nur in der Entwicklung vor: Im
//  Echtbetrieb startet die API ohne Schlüssel gar nicht (Helpers/Betrieb.cs).
//
//  P6a (229): [DesktopSchnittstelle] an allen Endpunkten mit X-Api-Key – Adressbeschränkung und Schlüssel vor dem
//  Lesen des Inhalts (siehe unten).
// =====================================================================

public static class ApiSchluessel
{
    public const string Header = "X-Api-Key";
    public const string Fehlertext = "Ungültiger oder fehlender X-Api-Key.";

    public static bool Gueltig(HttpRequest request, IConfiguration cfg)
    {
        var erwartet = cfg["Api:PushKey"];
        if (string.IsNullOrEmpty(erwartet)) return true;
        if (!request.Headers.TryGetValue(Header, out var werte) || werte.Count != 1) return false;
        var gesendet = werte.ToString();
        if (string.IsNullOrEmpty(gesendet)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(gesendet));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(erwartet));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>
/// P6a (Entscheidung 229): Desktop-Schnittstelle (X-Api-Key). Läuft vor dem Lesen des Anfrageinhalts:
/// mit Zugriff:DesktopNurSchule von anderen Adressen 403 „Adresse nicht freigegeben“ – vor der Prüfung des Schlüssels;
/// danach der Schlüssel selbst (401). Die Aktionen prüfen den Schlüssel wie bisher zusätzlich. Markiert den Endpunkt
/// außerdem für die Anfragebegrenzung (ohne die Grenze von 64 KB für Anfragen ohne Anmeldung).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class DesktopSchnittstelleAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    /// <summary>Nach [NurEntwicklung] (Order 0): Seeds im Echtbetrieb bleiben 404.</summary>
    public int Order => 10;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var http = context.HttpContext;
        var zugriff = http.RequestServices.GetRequiredService<Zugriffsschutz>();
        if (zugriff.DesktopNurSchule && !zugriff.IstSchule(http.Connection.RemoteIpAddress))
        {
            context.Result = new ObjectResult(new { fehler = Zugriffsschutz.AdresseText, adresseNichtFreigegeben = true })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }
        var cfg = http.RequestServices.GetRequiredService<IConfiguration>();
        if (!ApiSchluessel.Gueltig(http.Request, cfg))
            context.Result = new UnauthorizedObjectResult(ApiSchluessel.Fehlertext);
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

/// <summary>
/// Phase 5.1: Endpunkt nur in der Entwicklung (Seeds, Testkonten). Im Echtbetrieb antwortet
/// er mit 404, als gäbe es ihn nicht. Läuft vor dem Lesen des Anfrageinhalts.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NurEntwicklungAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var env = context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        if (Betrieb.Produktiv(env)) context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
