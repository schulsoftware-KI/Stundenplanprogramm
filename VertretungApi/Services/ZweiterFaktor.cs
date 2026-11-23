using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VertretungApi.Helpers;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P6a – zweiter Faktor (Entscheidungen 225–227, 238).
//
//  - Möglich für jedes Personalkonto (freiwillig, Frage B); Schülerkonten nie; Eltern entscheidet P7.
//  - Pflicht: Admin im Echtbetrieb immer (177), in der Entwicklung per Schalter (Standard aus); Schalter für
//    Klassenleitung/Stufenkoordination und für alle Lehrkräfte (Standard aus, DSB/Personalrat).
//  - Konto mit Pflicht, aber ohne Faktor: nach dem Passwort nur die Einrichtung (Claim „faktorEinrichten“ =
//    „einrichten“, Sperre in Program.cs). Wer den letzten Wiederherstellungscode benutzt hat, muss neu einrichten
//    („neu“). Die Reihenfolge bleibt: erst Passwort festlegen, dann Faktor.
//  - Merker (Vorgang der Anmeldung 5 Minuten, Einrichtung 10 Minuten): mit AES-GCM versiegelt (Schlüssel aus
//    Jwt:Key abgeleitet, je Zweck ein eigener), an Konto und Sicherheitsstempel gebunden. Neuer Stempel (Einrichten,
//    Zurücksetzen, Passwort, „überall abmelden“) = alle offenen Merker ungültig.
// =====================================================================

public enum CodeArt { Keiner, Totp, Wiederherstellung }

/// <summary>Ergebnis einer Code-Prüfung. Schritt = benutzter Zeitschritt (TOTP), Pruefsumme = benutzter Wiederherstellungscode.</summary>
public sealed record CodeErgebnis(CodeArt Art, long? Schritt = null, string? Pruefsumme = null)
{
    public bool Ok => Art != CodeArt.Keiner;
    public static readonly CodeErgebnis Falsch = new(CodeArt.Keiner);
}

/// <summary>Schalter der Pflicht (Einstellungen → Anmeldung).</summary>
public sealed record FaktorPflichten(bool Admin, bool Leitung, bool Lehrkraefte)
{
    public static readonly FaktorPflichten Keine = new(false, false, false);
}

public static class ZweiterFaktor
{
    public const int AnzahlCodes = 10;
    public const string ZweckVorgang = "anmeldung";
    public const string ZweckEinrichtung = "einrichtung";
    public static readonly TimeSpan VorgangGueltig = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EinrichtungGueltig = TimeSpan.FromMinutes(10);

    /// <summary>Werte des Claims bzw. der Antwort „faktorEinrichten“.</summary>
    public const string Einrichten = "einrichten";
    public const string Neu = "neu";

    public static bool Moeglich(BenutzerRolle r) => r != BenutzerRolle.Schueler;

    public static bool Aktiv(Benutzer u) => u.ZweiterFaktorSeit != null && !string.IsNullOrEmpty(u.ZweiterFaktorGeheimnis);

    public static bool Pflicht(BenutzerRolle r, FaktorPflichten p, bool produktiv) => r switch
    {
        BenutzerRolle.Admin => produktiv || p.Admin,
        BenutzerRolle.Klassenlehrer or BenutzerRolle.Stufenkoordination => p.Leitung || p.Lehrkraefte,
        BenutzerRolle.Lehrer => p.Lehrkraefte,
        _ => false
    };

    /// <summary>null = alles in Ordnung; „einrichten“ = Pflicht, aber kein Faktor; „neu“ = keine Wiederherstellungscodes mehr.</summary>
    public static string? MussEinrichten(BenutzerRolle r, bool aktiv, bool keineCodes, FaktorPflichten p, bool produktiv)
    {
        if (!Moeglich(r)) return null;
        if (aktiv) return keineCodes ? Neu : null;
        return Pflicht(r, p, produktiv) ? Einrichten : null;
    }

    public static string? MussEinrichten(Benutzer u, FaktorPflichten p, bool produktiv)
        => MussEinrichten(u.Rolle, Aktiv(u), RestCodes(u) == 0, p, produktiv);

    public static List<string> Pruefsummen(Benutzer u)
        => (u.ZweiterFaktorCodes ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static int RestCodes(Benutzer u) => Pruefsummen(u).Count;

    /// <summary>
    /// Prüft einen Code der Authenticator-App (6 Ziffern) bzw. einen Wiederherstellungscode. Ändert nichts – nach
    /// Erfolg <see cref="Anwenden"/> aufrufen und speichern.
    /// </summary>
    public static CodeErgebnis Pruefen(Benutzer u, string? eingabe, DateTimeOffset jetzt)
    {
        if (!Aktiv(u)) return CodeErgebnis.Falsch;
        var code = Totp.Bereinigen(eingabe);
        if (code.Length == Totp.Stellen && code.All(char.IsAsciiDigit))
        {
            if (!Totp.Base32Lesen(u.ZweiterFaktorGeheimnis, out var geheimnis)) return CodeErgebnis.Falsch;
            var schritt = Totp.Pruefen(geheimnis, code, Totp.Zeitschritt(jetzt), u.ZweiterFaktorSchritt);
            return schritt is long s ? new CodeErgebnis(CodeArt.Totp, Schritt: s) : CodeErgebnis.Falsch;
        }
        var wh = Totp.CodeLesen(code);
        if (wh == null) return CodeErgebnis.Falsch;
        var soll = Encoding.ASCII.GetBytes(Totp.CodePruefsumme(u.Id, wh));
        string? treffer = null;
        foreach (var p in Pruefsummen(u))
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p), soll)) treffer ??= p;
        return treffer != null ? new CodeErgebnis(CodeArt.Wiederherstellung, Pruefsumme: treffer) : CodeErgebnis.Falsch;
    }

    /// <summary>Benutzten Zeitschritt merken bzw. den benutzten Wiederherstellungscode streichen (jeder gilt einmal).</summary>
    public static void Anwenden(Benutzer u, CodeErgebnis e)
    {
        if (e.Art == CodeArt.Totp && e.Schritt is long s)
            u.ZweiterFaktorSchritt = Math.Max(s, u.ZweiterFaktorSchritt ?? long.MinValue);
        else if (e.Art == CodeArt.Wiederherstellung && e.Pruefsumme != null)
            u.ZweiterFaktorCodes = string.Join(";", Pruefsummen(u).Where(p => p != e.Pruefsumme));
    }

    /// <summary>Neue Wiederherstellungscodes (ersetzen alle alten). Rückgabe: zur Anzeige – nur dieses eine Mal.</summary>
    public static List<string> CodesSetzen(Benutzer u)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        while (codes.Count < AnzahlCodes) codes.Add(Totp.NeuerWiederherstellungscode());
        u.ZweiterFaktorCodes = string.Join(";", codes.Select(c => Totp.CodePruefsumme(u.Id, c)));
        return codes.Select(Totp.CodeAnzeige).ToList();
    }

    /// <summary>Zweiten Faktor entfernen (Ausschalten, Zurücksetzen durch den Admin, Notfallzugang).</summary>
    public static void Entfernen(Benutzer u)
    {
        u.ZweiterFaktorGeheimnis = null;
        u.ZweiterFaktorSeit = null;
        u.ZweiterFaktorSchritt = null;
        u.ZweiterFaktorCodes = null;
    }

    // -----------------------------------------------------------------
    //  Merker (AES-GCM): „Ablauf|Inhalt“, Zweck als zusätzliche Daten
    // -----------------------------------------------------------------

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxMerker = 2000;

    private static byte[] Schluessel(string jwtKey, string zweck)
        => HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwtKey), Encoding.UTF8.GetBytes("zf-merker|" + zweck));

    public static string Versiegeln(string jwtKey, string zweck, string inhalt, DateTimeOffset ablauf)
    {
        var klar = Encoding.UTF8.GetBytes(ablauf.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "|" + inhalt);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var geheim = new byte[klar.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(Schluessel(jwtKey, zweck), TagBytes))
            aes.Encrypt(nonce, klar, geheim, tag, Encoding.UTF8.GetBytes(zweck));
        var alles = new byte[NonceBytes + geheim.Length + TagBytes];
        nonce.CopyTo(alles, 0);
        geheim.CopyTo(alles, NonceBytes);
        tag.CopyTo(alles, NonceBytes + geheim.Length);
        return Base64Url(alles);
    }

    /// <summary>Inhalt eines gültigen, nicht abgelaufenen Merkers – sonst null.</summary>
    public static string? Oeffnen(string jwtKey, string zweck, string? merker, DateTimeOffset jetzt)
    {
        if (string.IsNullOrWhiteSpace(merker) || merker.Length > MaxMerker) return null;
        var alles = Base64UrlLesen(merker.Trim());
        if (alles == null || alles.Length < NonceBytes + TagBytes + 2) return null;
        var nonce = alles.AsSpan(0, NonceBytes);
        var geheim = alles.AsSpan(NonceBytes, alles.Length - NonceBytes - TagBytes);
        var tag = alles.AsSpan(alles.Length - TagBytes, TagBytes);
        var klar = new byte[geheim.Length];
        try
        {
            using var aes = new AesGcm(Schluessel(jwtKey, zweck), TagBytes);
            aes.Decrypt(nonce, geheim, tag, klar, Encoding.UTF8.GetBytes(zweck));
        }
        catch (CryptographicException)
        {
            return null;
        }
        var text = Encoding.UTF8.GetString(klar);
        int trenner = text.IndexOf('|');
        if (trenner <= 0 || !long.TryParse(text[..trenner], NumberStyles.None, CultureInfo.InvariantCulture, out var ablauf))
            return null;
        if (jetzt.ToUnixTimeSeconds() > ablauf) return null;
        return text[(trenner + 1)..];
    }

    private static string Base64Url(byte[] daten)
        => Convert.ToBase64String(daten).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? Base64UrlLesen(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return null;
        }
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}
