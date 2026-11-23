using System.Collections.Concurrent;
using System.Net;
using VertretungApi.Helpers;

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
//  P6a (Entscheidung 230):
//  - Die Sperre je Adresse gilt nicht für Schuladressen (Zugriff:Schuladressen): Im Schul-WLAN erscheinen alle
//    Geräte mit einer gemeinsamen öffentlichen Adresse – 20 Tippfehler am ersten Schultag sperrten sonst die
//    Anmeldung für die ganze Schule. Die Sperre je Konto gilt dort unverändert.
//  - Allgemein für Codes nutzbar: Falsche Codes des zweiten Faktors zählen beim Konto mit (gleicher Zähler wie das
//    Passwort; das richtige Passwort setzt ihn bei einem Konto mit zweitem Faktor erst nach dem richtigen Code
//    zurück). Weitere Codes (Einladungscodes in P7, Einrichtungscode in P8) bekommen einen eigenen Bereich.
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
    private readonly Zugriffsschutz _zugriff;

    public LoginBremse(Zugriffsschutz zugriff)
    {
        _zugriff = zugriff;
    }

    /// <summary>Schlüssel je Konto; bereich "" = Anmeldung (Passwort und zweiter Faktor).</summary>
    private static string KontoSchluessel(string bereich, string benutzername)
        => bereich.Length == 0 ? benutzername : bereich + "\u0001" + benutzername;

    /// <summary>Restliche Wartezeit, wenn Konto oder Adresse gerade gesperrt sind; sonst null.</summary>
    public TimeSpan? Gesperrt(string benutzername, IPAddress? adresse, string bereich = "")
    {
        var jetzt = DateTimeOffset.UtcNow;
        TimeSpan? rest = null;
        if (_konten.TryGetValue(KontoSchluessel(bereich, benutzername), out var k) && k.SperreBis > jetzt)
            rest = k.SperreBis - jetzt;
        var a = AdressSchluessel(adresse);
        if (a != null && _adressen.TryGetValue(a, out var s) && s.SperreBis > jetzt)
        {
            var r = s.SperreBis - jetzt;
            if (rest == null || r > rest) rest = r;
        }
        return rest;
    }

    public void Fehlschlag(string benutzername, IPAddress? adresse, string bereich = "")
    {
        Zaehle(_konten, KontoSchluessel(bereich, benutzername), FreiJeKonto);
        var a = AdressSchluessel(adresse);
        if (a != null) Zaehle(_adressen, a, FreiJeAdresse);
    }

    public void Erfolg(string benutzername, string bereich = "") => _konten.TryRemove(KontoSchluessel(bereich, benutzername), out _);

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

    private string? AdressSchluessel(IPAddress? adresse)
    {
        if (adresse == null) return null;
        if (adresse.IsIPv4MappedToIPv6) adresse = adresse.MapToIPv4();
        if (IPAddress.IsLoopback(adresse)) return null;
        if (_zugriff.IstSchule(adresse)) return null;   // P6a (230)
        return adresse.ToString();
    }
}
