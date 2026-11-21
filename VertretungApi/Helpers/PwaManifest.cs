using System.Text.Encodings.Web;
using System.Text.Json;
using VertretungApi.Services;

namespace VertretungApi.Helpers;

// =====================================================================
//  P3 (E-1): Manifest der PWA (früher die feste Datei wwwroot/manifest.webmanifest). Name der App =
//  Kurzname der Schule aus den Einstellungen, sonst „Stundenplan“ (Entscheidung 201: auch vor der
//  Anmeldung sichtbar). Farben, Icons und Pfade wie bisher.
// =====================================================================

public static class PwaManifest
{
    private static readonly JsonSerializerOptions Optionen = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // Umlaute lesbar; ausgeliefert als JSON, nicht in HTML
    };

    /// <param name="werte">null = Einstellungen nicht lesbar → Standardname</param>
    public static string Json(SchulWerte? werte)
    {
        string name = werte?.AppName ?? SchulWerte.StandardAppName;
        string schule = werte?.SchuleName ?? "";
        var m = new
        {
            name,
            short_name = name,
            description = schule.Length > 0
                ? $"Wochenplan mit tagesaktuellen Vertretungen – {schule}"
                : "Wochenplan mit tagesaktuellen Vertretungen",
            lang = "de",
            start_url = "./",
            scope = "./",
            display = "standalone",
            orientation = "any",
            background_color = "#eef2f8",
            theme_color = "#2340a8",
            icons = new object[]
            {
                new { src = "icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = "icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
                new { src = "icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
            },
        };
        return JsonSerializer.Serialize(m, Optionen);
    }
}
