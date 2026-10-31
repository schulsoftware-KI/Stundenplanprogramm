using System.Collections.Concurrent;
using System.Net;

namespace VertretungApi.Services;

// =====================================================================
//  Login-Bremse (Phase 2, Schritt 4): bremst das Durchprobieren von Passwörtern.
//
//  - Je Benutzername: ab dem 5. Fehlversuch eine Wartezeit, die sich mit jedem
//    weiteren Fehlversuch verdoppelt (30 s, 1 min, 2 min … höchstens 15 min).
//  - Je Absender-Adresse: dasselbe ab dem 20. Fehlversuch (gegen das Ausprobieren
//    eines Passworts bei vielen Konten). Gilt nicht für Anfragen von localhost:
//    Über den Cloudflare-Tunnel bzw. einen Reverse-Proxy kommen alle Anfragen
//    von dort, eine Sperre träfe sonst alle Nutzer gleichzeitig.
//  - Nach 30 Minuten ohne Fehlversuch ist alles vergessen; eine erfolgreiche
//    Anmeldung setzt den Zähler des Kontos zurück.
//
//  Nur im Arbeitsspeicher: Ein Neustart der API setzt alle Zähler zurück.
// =====================================================================

public class LoginBremse
{
    private const int FreiJeKonto = 5;
    private const int FreiJeAdresse = 20;
    private static readonly TimeSpan ErsteSperre = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LaengsteSperre = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Vergessen = TimeSpan.FromMinutes(30);
    private const int Aufraeumen = 5000;

    private readonly record struct Stand(int Fehlversuche, DateTimeOffset SperreBis, DateTimeOffset Zuletzt);

    private readonly ConcurrentDictionary<string, Stand> _konten = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Stand> _adressen = new();

    /// <summary>Restliche Wartezeit, wenn Konto oder Adresse gerade gesperrt sind; sonst null.</summary>
    public TimeSpan? Gesperrt(string benutzername, IPAddress? adresse)
    {
        var jetzt = DateTimeOffset.UtcNow;
        TimeSpan? rest = null;
        if (_konten.TryGetValue(benutzername, out var k) && k.SperreBis > jetzt)
            rest = k.SperreBis - jetzt;
        var a = AdressSchluessel(adresse);
        if (a != null && _adressen.TryGetValue(a, out var s) && s.SperreBis > jetzt)
        {
            var r = s.SperreBis - jetzt;
            if (rest == null || r > rest) rest = r;
        }
        return rest;
    }

    public void Fehlschlag(string benutzername, IPAddress? adresse)
    {
        Zaehle(_konten, benutzername, FreiJeKonto);
        var a = AdressSchluessel(adresse);
        if (a != null) Zaehle(_adressen, a, FreiJeAdresse);
    }

    public void Erfolg(string benutzername) => _konten.TryRemove(benutzername, out _);

    private static void Zaehle(ConcurrentDictionary<string, Stand> tabelle, string schluessel, int frei)
    {
        var jetzt = DateTimeOffset.UtcNow;
        tabelle.AddOrUpdate(schluessel,
            _ => Neu(1, jetzt, frei),
            (_, alt) => Neu(jetzt - alt.Zuletzt > Vergessen ? 1 : alt.Fehlversuche + 1, jetzt, frei));

        if (tabelle.Count > Aufraeumen)
            foreach (var (k, v) in tabelle)
                if (jetzt - v.Zuletzt > Vergessen) tabelle.TryRemove(k, out _);
    }

    private static Stand Neu(int fehlversuche, DateTimeOffset jetzt, int frei)
    {
        if (fehlversuche < frei) return new Stand(fehlversuche, DateTimeOffset.MinValue, jetzt);
        int stufe = Math.Min(fehlversuche - frei, 10);
        var dauer = TimeSpan.FromTicks(Math.Min(ErsteSperre.Ticks << stufe, LaengsteSperre.Ticks));
        return new Stand(fehlversuche, jetzt + dauer, jetzt);
    }

    private static string? AdressSchluessel(IPAddress? adresse)
    {
        if (adresse == null) return null;
        if (adresse.IsIPv4MappedToIPv6) adresse = adresse.MapToIPv4();
        return IPAddress.IsLoopback(adresse) ? null : adresse.ToString();
    }
}
