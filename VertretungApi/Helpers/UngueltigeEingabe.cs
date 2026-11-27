using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace VertretungApi.Helpers;

// =====================================================================
//  PB-1a (Entscheidung 318): Antwort auf ungültige Eingaben – für alle Aufrufer (App, Desktop, PowerShell).
//
//  Mit [ApiController] lehnt ASP.NET Anfragen schon vor dem Controller ab, wenn ein Pflichtfeld fehlt (nicht nullbare
//  Texte und Listen in Eingabe-DTOs), das JSON unlesbar ist oder ein Wert nicht passt (Datum, Zahl). Vorher kam dann ein
//  englisches „ValidationProblem“ ohne das Feld „fehler“ – die App zeigte nur „Fehler 400“ (Befund T6-2).
//
//  Jetzt: 400 mit { fehler: "<deutscher Text>", felder: ["grund", …] }. Die eingegebenen Werte stehen weder in der Antwort
//  noch im Log (Warnung mit Methode, Pfadvorlage und Feldnamen). App und Desktop zeigen „fehler“ schon heute.
// =====================================================================

public static class UngueltigeEingabe
{
    public const string Nachsatz = "Bitte die Seite neu laden; bleibt es, sag dem Admin Bescheid.";
    /// <summary>Für die Schnittstellen des Desktops ([DesktopSchnittstelle]) – dort gibt es keine Seite zum Neuladen.</summary>
    public const string NachsatzDesktop = "Vermutlich passen Desktop-Programm und Server nicht zusammen – beide auf denselben Stand bringen.";
    private const int MaxFelder = 8;

    public static IActionResult Antwort(ActionContext ctx)
    {
        // Schlüssel = Name des ganzen Körpers (z. B. „dto“ bei leerem Körper) ist kein Feld.
        var koerper = ctx.ActionDescriptor.Parameters
            .Where(p => p.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(p => p.Name)
            .ToList();
        var (felder, unlesbar) = Auswerten(ctx.ModelState, koerper);
        bool desktop = ctx.HttpContext.GetEndpoint()?.Metadata.GetMetadata<DesktopSchnittstelleAttribute>() != null;
        string nachsatz = desktop ? NachsatzDesktop : Nachsatz;
        string liste = felder.Count > 0 ? $" ({string.Join(", ", felder)})" : "";
        string text = unlesbar
            ? $"Die Angaben konnten nicht gelesen werden{liste}. {nachsatz}"
            : felder.Count > 0
                ? $"Die Angaben sind unvollständig oder ungültig{liste}. {nachsatz}"
                : $"Die Angaben fehlen. {nachsatz}";

        try
        {
            var log = ctx.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Eingabe");
            // Pfadvorlage statt Pfad (z. B. „api/fehlzeiten/schueler/{id}/entschuldigung“) – im Pfad können Werte stehen.
            string vorlage = ctx.ActionDescriptor.AttributeRouteInfo?.Template ?? "(ohne Vorlage)";
            log?.LogWarning("Ungültige Eingabe: {Methode} {Vorlage} – {Art}, Felder: {Felder}",
                ctx.HttpContext.Request.Method, vorlage, unlesbar ? "nicht lesbar" : "unvollständig/ungültig",
                felder.Count > 0 ? string.Join(", ", felder) : "–");
        }
        catch (ObjectDisposedException)
        {
            // Anfrage schon beendet – die Antwort zählt, das Log nicht.
        }

        return new BadRequestObjectResult(new { fehler = text, felder });
    }

    /// <summary>Feldnamen (klein, ohne Index) und ob das JSON nicht lesbar war. Werte und englische Texte bleiben draußen.</summary>
    /// <param name="koerper">Namen der Parameter, die aus dem Körper der Anfrage kommen (kein Feld, sondern „Angaben fehlen“).</param>
    public static (List<string> Felder, bool Unlesbar) Auswerten(ModelStateDictionary ms, IReadOnlyCollection<string> koerper)
    {
        var felder = new List<string>();
        bool unlesbar = false;
        foreach (var paar in ms)
        {
            if (paar.Value is not { Errors.Count: > 0 } eintrag) continue;
            string schluessel = paar.Key;
            // Fehler des JSON-Lesers: Schlüssel „$“ bzw. „$.grund“, die Ausnahme steckt im Fehler.
            if (schluessel.StartsWith('$') || eintrag.Errors.Any(e => e.Exception != null)) unlesbar = true;
            if (koerper.Contains(schluessel, StringComparer.OrdinalIgnoreCase)) continue;
            string feld = Feldname(schluessel);
            if (feld.Length > 0 && !felder.Contains(feld) && felder.Count < MaxFelder) felder.Add(feld);
        }
        return (felder, unlesbar);
    }

    /// <summary>„$.grund“ → „grund“, „Slots[3].Fach“ → „slots.fach“, „GrundLehrer“ → „grundLehrer“, „$“/„“ → „“.</summary>
    public static string Feldname(string schluessel)
    {
        var s = schluessel.Trim();
        if (s.StartsWith('$')) s = s.TrimStart('$').TrimStart('.');
        var teile = new List<string>();
        foreach (var roh in s.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            int klammer = roh.IndexOf('[');
            var teil = klammer >= 0 ? roh[..klammer] : roh;
            teil = new string(teil.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (teil.Length == 0) continue;
            teile.Add(char.ToLowerInvariant(teil[0]) + teil[1..]);
        }
        var name = string.Join(".", teile);
        return name.Length > 60 ? name[..60] : name;
    }
}
