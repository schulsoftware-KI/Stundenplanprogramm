using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VertretungApi.Helpers;

// =====================================================================
//  P6a (Entscheidung 225): zweiter Faktor nach RFC 6238 (TOTP) – HMAC-SHA1, 6 Ziffern, 30 Sekunden, ±1 Zeitschritt.
//  Geht mit jeder gängigen Authenticator-App. Maßgeblich ist die Uhr des Servers (NTP); das Testdatum (221)
//  verschiebt nur „heute“, nicht die Uhr. Jeder Code gilt nur einmal: Der Aufrufer übergibt den zuletzt benutzten
//  Zeitschritt, ältere und gleiche Schritte werden abgelehnt.
//
//  Dazu die Wiederherstellungscodes: 10 Zeichen ohne verwechselbare Zeichen (kein I, L, O, 0, 1), angezeigt als
//  „ABCDE-FGHJK“, gespeichert nur als Prüfsumme (SHA-256 mit der Konto-Id).
// =====================================================================

public static class Totp
{
    public const int Sekunden = 30;
    public const int Stellen = 6;
    public const int GeheimnisBytes = 20;   // 160 Bit

    public static byte[] NeuesGeheimnis() => RandomNumberGenerator.GetBytes(GeheimnisBytes);

    public static long Zeitschritt(DateTimeOffset zeit) => zeit.ToUnixTimeSeconds() / Sekunden;

    /// <summary>Code für einen Zeitschritt (RFC 4226, dynamische Kürzung).</summary>
    public static string Code(byte[] geheimnis, long schritt)
    {
        var zaehler = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(zaehler, schritt);
        var hash = HMACSHA1.HashData(geheimnis, zaehler);
        int versatz = hash[^1] & 0x0F;
        int wert = ((hash[versatz] & 0x7F) << 24)
                 | (hash[versatz + 1] << 16)
                 | (hash[versatz + 2] << 8)
                 | hash[versatz + 3];
        return (wert % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Prüft einen 6-stelligen Code für jetzt ±1 Zeitschritt. Rückgabe: der passende Zeitschritt (zum Merken) bzw.
    /// null. Schritte bis einschließlich <paramref name="zuletzt"/> gelten nicht mehr.
    /// </summary>
    public static long? Pruefen(byte[] geheimnis, string? code, long jetzt, long? zuletzt)
    {
        if (code == null || code.Length != Stellen || !code.All(char.IsAsciiDigit)) return null;
        var eingabe = Encoding.ASCII.GetBytes(code);
        long? treffer = null;
        for (long d = -1; d <= 1; d++)
        {
            long s = jetzt + d;
            if (zuletzt is long z && s <= z) continue;
            bool gleich = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(geheimnis, s)), eingabe);
            if (gleich && treffer == null) treffer = s;
        }
        return treffer;
    }

    /// <summary>Eingabe vereinheitlichen: Leerzeichen und Bindestriche weg („123 456“ → „123456“).</summary>
    public static string Bereinigen(string? eingabe)
        => new string((eingabe ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());

    // -----------------------------------------------------------------
    //  Base32 (RFC 4648, ohne Auffüllzeichen) – so erwarten es die Authenticator-Apps
    // -----------------------------------------------------------------

    private const string Base32Zeichen = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32(byte[] daten)
    {
        var sb = new StringBuilder((daten.Length * 8 + 4) / 5);
        int puffer = 0, bits = 0;
        foreach (var b in daten)
        {
            puffer = (puffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Zeichen[(puffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            puffer &= 0xFF;   // höchstens 4 Bits sind noch offen
        }
        if (bits > 0) sb.Append(Base32Zeichen[(puffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static bool Base32Lesen(string? text, out byte[] daten)
    {
        daten = Array.Empty<byte>();
        var s = (text ?? "").Trim().TrimEnd('=').ToUpperInvariant();
        if (s.Length == 0) return false;
        var liste = new List<byte>(s.Length * 5 / 8);
        int puffer = 0, bits = 0;
        foreach (var c in s)
        {
            int w = Base32Zeichen.IndexOf(c);
            if (w < 0) return false;
            puffer = (puffer << 5) | w;
            bits += 5;
            if (bits >= 8)
            {
                liste.Add((byte)((puffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
            puffer &= 0xFF;   // nur die noch nicht ausgegebenen Bits sind wichtig (höchstens 7 + 5)
        }
        daten = liste.ToArray();
        return daten.Length > 0;
    }

    /// <summary>„ABCD EFGH IJKL …“ zum Abtippen.</summary>
    public static string Gruppiert(string base32)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < base32.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(base32[i]);
        }
        return sb.ToString();
    }

    /// <summary>otpauth-Adresse für den QR-Code (Key Uri Format der Authenticator-Apps).</summary>
    public static string Uri(string aussteller, string konto, string base32)
    {
        string a = Kodieren(aussteller.Replace(':', ' ').Trim());
        string k = Kodieren(konto.Replace(':', ' ').Trim());
        return $"otpauth://totp/{a}:{k}?secret={base32}&issuer={a}&algorithm=SHA1&digits={Stellen}&period={Sekunden}";
    }

    private static string Kodieren(string s) => System.Uri.EscapeDataString(s.Length == 0 ? "Stundenplan" : s);

    // -----------------------------------------------------------------
    //  Wiederherstellungscodes
    // -----------------------------------------------------------------

    public const string CodeZeichen = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";   // ohne I, L, O, 0, 1
    public const int CodeLaenge = 10;

    public static string NeuerWiederherstellungscode()
    {
        var c = new char[CodeLaenge];
        for (int i = 0; i < c.Length; i++) c[i] = CodeZeichen[RandomNumberGenerator.GetInt32(CodeZeichen.Length)];
        return new string(c);
    }

    /// <summary>„ABCDE-FGHJK“.</summary>
    public static string CodeAnzeige(string code) => code.Length == CodeLaenge ? code[..5] + "-" + code[5..] : code;

    /// <summary>Eingabe eines Wiederherstellungscodes vereinheitlichen; null = kann keiner sein.</summary>
    public static string? CodeLesen(string? eingabe)
    {
        var s = Bereinigen(eingabe).ToUpperInvariant();
        if (s.Length != CodeLaenge) return null;
        foreach (var c in s)
            if (CodeZeichen.IndexOf(c) < 0) return null;
        return s;
    }

    public static string CodePruefsumme(int kontoId, string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"zf-wh|{kontoId.ToString(CultureInfo.InvariantCulture)}|{code}")));
}
