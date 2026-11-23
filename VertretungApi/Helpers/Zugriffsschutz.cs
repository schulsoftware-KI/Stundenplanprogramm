using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace VertretungApi.Helpers;

// =====================================================================
//  P6a (Entscheidung 229): Schuladressen und Adressbeschränkung – nur in der Einstellungsdatei, nicht in der App
//  (ein falscher Wert würde den Admin sonst aus genau der App aussperren, in der er ihn ändern müsste).
//
//    "Zugriff": {
//      "Schuladressen": [ "203.0.113.10/32", "2001:db8:1234::/48" ],   // IPv4/IPv6, ohne Präfix = genau diese Adresse
//      "DesktopNurSchule": false,   // Desktop-Schnittstellen (X-Api-Key) nur von dort – sonst 403
//      "AdminNurSchule": false      // Admin-Anmeldung und jede Anfrage mit Admin-Token nur von dort – sonst 403
//    }
//
//  `localhost` zählt nicht von selbst als Schule (zum Testen 127.0.0.1/32 und ::1/128 eintragen). Änderungen gelten
//  nach einem Neustart. Ungültige Einträge bzw. eine Beschränkung mit leerer Liste prüft die Startprüfung (Betrieb).
//  Auch ohne Beschränkung nützlich (230): Login-Bremse und Anfragebegrenzung behandeln Schuladressen anders.
//
//  Dazu die Hilfen des Anmeldeprotokolls (228): Adresse gekürzt (IPv4 /24, IPv6 /48) und das Gerät grob aus dem
//  Browser („Android · Chrome“). Die volle Adresse kennt nur die Login-Bremse im Arbeitsspeicher.
// =====================================================================

/// <summary>Ein Netz (Adresse + Präfix). Eigene Darstellung: Bits hinter dem Präfix dürfen gesetzt sein.</summary>
public sealed class Netz
{
    private readonly byte[] _basis;   // schon maskiert
    public int Praefix { get; }
    public AddressFamily Familie { get; }

    public Netz(IPAddress adresse, int praefix)
    {
        var b = adresse.GetAddressBytes();
        Praefix = praefix;
        Familie = adresse.AddressFamily;
        _basis = Maskieren(b, praefix);
    }

    public bool Enthaelt(IPAddress adresse)
    {
        if (adresse.AddressFamily != Familie) return false;
        var b = Maskieren(adresse.GetAddressBytes(), Praefix);
        for (int i = 0; i < b.Length; i++)
            if (b[i] != _basis[i]) return false;
        return true;
    }

    private static byte[] Maskieren(byte[] b, int praefix)
    {
        var e = new byte[b.Length];
        for (int i = 0; i < b.Length; i++)
        {
            int bits = Math.Clamp(praefix - i * 8, 0, 8);
            int maske = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF;
            e[i] = (byte)(b[i] & maske);
        }
        return e;
    }

    public int Laenge => Familie == AddressFamily.InterNetwork ? 32 : 128;

    public override string ToString() => $"{new IPAddress(_basis)}/{Praefix}";
}

/// <summary>Schuladressen und Adressbeschränkung (Singleton, Program.cs).</summary>
public sealed class Zugriffsschutz
{
    public const string AdresseText =
        "Adresse nicht freigegeben – die Desktop-Schnittstellen sind nur aus dem Schulnetz erreichbar (Zugriff:DesktopNurSchule).";
    public const string AdminText =
        "Admin-Konten sind nur aus dem Schulnetz nutzbar (Zugriff:AdminNurSchule). Bitte im Schulnetz anmelden.";

    public IReadOnlyList<Netz> Schuladressen { get; }
    public bool DesktopNurSchule { get; }
    public bool AdminNurSchule { get; }
    public bool HatSchuladressen => Schuladressen.Count > 0;

    public Zugriffsschutz(IConfiguration cfg)
    {
        Schuladressen = Lesen(cfg, out _);
        DesktopNurSchule = Schalter(cfg, "Zugriff:DesktopNurSchule", out _);
        AdminNurSchule = Schalter(cfg, "Zugriff:AdminNurSchule", out _);
    }

    /// <summary>Für Tests und die Startprüfung: Werte direkt.</summary>
    public Zugriffsschutz(IReadOnlyList<Netz> schuladressen, bool desktopNurSchule, bool adminNurSchule)
    {
        Schuladressen = schuladressen;
        DesktopNurSchule = desktopNurSchule;
        AdminNurSchule = adminNurSchule;
    }

    /// <summary>true = Adresse liegt in einer Schuladresse (nie bei leerer Liste; localhost nur, wenn eingetragen).</summary>
    public bool IstSchule(IPAddress? adresse)
    {
        if (adresse == null || Schuladressen.Count == 0) return false;
        var a = Normal(adresse);
        foreach (var n in Schuladressen)
            if (n.Enthaelt(a)) return true;
        return false;
    }

    /// <summary>Für das Anmeldeprotokoll: null = keine Schuladressen eingetragen.</summary>
    public bool? Schulnetz(IPAddress? adresse) => HatSchuladressen ? IstSchule(adresse) : null;

    // -----------------------------------------------------------------
    //  Einstellungsdatei lesen
    // -----------------------------------------------------------------

    /// <summary>Zugriff:Schuladressen als Netze; ungültige Einträge in <paramref name="ungueltig"/>.</summary>
    public static List<Netz> Lesen(IConfiguration cfg, out List<string> ungueltig)
    {
        var liste = new List<Netz>();
        ungueltig = new List<string>();
        foreach (var eintrag in cfg.GetSection("Zugriff:Schuladressen").Get<string[]>() ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(eintrag)) continue;
            if (NetzLesen(eintrag, out var n)) liste.Add(n!);
            else ungueltig.Add(eintrag.Trim());
        }
        return liste;
    }

    /// <summary>Schalter aus der Einstellungsdatei; fehlt er, ist er aus. ungueltig = Wert ist weder true noch false.</summary>
    public static bool Schalter(IConfiguration cfg, string schluessel, out bool ungueltig)
    {
        ungueltig = false;
        var text = cfg[schluessel];
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (bool.TryParse(text.Trim(), out var b)) return b;
        ungueltig = true;
        return false;
    }

    private static readonly Regex Ipv4 = new(
        @"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$", RegexOptions.Compiled);

    /// <summary>„203.0.113.10“, „203.0.113.0/24“, „2001:db8::/48“, „::1“. IPv4 nur in der üblichen Schreibweise.</summary>
    public static bool NetzLesen(string text, out Netz? netz)
    {
        netz = null;
        var s = text.Trim();
        if (s.Length == 0 || s.Length > 64) return false;
        string adr = s, pfx = "";
        int schraeg = s.IndexOf('/');
        if (schraeg >= 0)
        {
            adr = s[..schraeg].Trim();
            pfx = s[(schraeg + 1)..].Trim();
            if (pfx.Length == 0) return false;
        }

        IPAddress? a;
        if (adr.Contains(':'))
        {
            if (adr.Contains('%') || !IPAddress.TryParse(adr, out a) || a.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
        }
        else
        {
            if (!Ipv4.IsMatch(adr) || !IPAddress.TryParse(adr, out a)) return false;
        }

        int laenge = a.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int praefix = laenge;
        if (pfx.Length > 0)
        {
            if (pfx.Length > 3 || !pfx.All(char.IsAsciiDigit)) return false;
            praefix = int.Parse(pfx, CultureInfo.InvariantCulture);
            if (praefix > laenge) return false;
        }

        // ::ffff:a.b.c.d/n → a.b.c.d/(n-96): Anfragen werden ebenfalls als IPv4 verglichen.
        if (a.IsIPv4MappedToIPv6)
        {
            if (praefix < 96) return false;
            a = a.MapToIPv4();
            praefix -= 96;
        }
        netz = new Netz(a, praefix);
        return true;
    }

    // -----------------------------------------------------------------
    //  Anmeldeprotokoll: Adresse gekürzt, Gerät grob
    // -----------------------------------------------------------------

    public static IPAddress Normal(IPAddress adresse) => adresse.IsIPv4MappedToIPv6 ? adresse.MapToIPv4() : adresse;

    /// <summary>„203.0.113.0/24“ bzw. „2001:db8:1234::/48“; localhost als „localhost“.</summary>
    public static string Kuerzen(IPAddress? adresse)
    {
        if (adresse == null) return "unbekannt";
        var a = Normal(adresse);
        if (IPAddress.IsLoopback(a)) return "localhost";
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            b[3] = 0;
            return $"{new IPAddress(b)}/24";
        }
        for (int i = 6; i < b.Length; i++) b[i] = 0;
        return $"{new IPAddress(b)}/48";
    }

    /// <summary>Gerät grob aus dem User-Agent: „Android · Chrome“, „iPhone · Safari“, „Windows · Edge“ …</summary>
    public static string? Geraet(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        bool Hat(string s) => ua.Contains(s, StringComparison.OrdinalIgnoreCase);

        string? system =
            Hat("Android") ? "Android" :
            Hat("iPhone") ? "iPhone" :
            Hat("iPad") ? "iPad" :
            Hat("Windows") ? "Windows" :
            Hat("CrOS") ? "ChromeOS" :
            Hat("Macintosh") || Hat("Mac OS X") ? "Mac" :
            Hat("Linux") ? "Linux" : null;

        string? programm =
            Hat("Edg/") || Hat("EdgA/") || Hat("EdgiOS/") ? "Edge" :
            Hat("OPR/") || Hat("Opera") ? "Opera" :
            Hat("SamsungBrowser") ? "Samsung Internet" :
            Hat("Firefox/") || Hat("FxiOS/") ? "Firefox" :
            Hat("CriOS/") || Hat("Chrome/") || Hat("Chromium/") ? "Chrome" :
            Hat("Safari/") ? "Safari" :
            Hat("PowerShell") ? "PowerShell" :
            Hat("curl/") ? "curl" : null;

        if (system == null && programm == null) return "anderes Programm";
        return string.Join(" · ", new[] { system, programm }.Where(x => x != null));
    }
}
