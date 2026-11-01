using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VertretungApi.Model;

namespace VertretungApi.Helpers;

// =====================================================================
//  Passwortregeln und Startpasswörter (Phase 2, Schritt 5).
//
//  Linie wie BSI/NIST: Länge statt Zwangs-Sonderzeichen.
//    - Personal mindestens 10 Zeichen, Schüler mindestens 8, höchstens 128.
//    - nicht der Benutzername bzw. der eigene Name, nicht das bisherige Passwort,
//    - keine offensichtlichen Passwörter (Liste, Wiederholungen, Tastenfolgen).
//  Startpasswörter: gut lesbar und diktierbar („kamo-tefi-37"), ohne leicht
//  verwechselbare Zeichen; nur einmal angezeigt, nie im Klartext gespeichert.
// =====================================================================

public static class PasswortRegeln
{
    public const int MinPersonal = 10;
    public const int MinSchueler = 8;
    public const int Max = 128;

    public static int MindestLaenge(BenutzerRolle rolle) => rolle == BenutzerRolle.Schueler ? MinSchueler : MinPersonal;

    // Häufige Passwörter (klein, ohne angehängte Ziffern/Zeichen verglichen).
    private static readonly HashSet<string> Haeufig = new(StringComparer.Ordinal)
    {
        "passwort", "password", "kennwort", "geheim", "geheimnis", "schule", "schueler", "schüler",
        "lehrer", "lehrerin", "admin", "administrator", "willkommen", "welcome", "hallo", "hello",
        "qwertz", "qwerty", "qwertzuiop", "qwertyuiop", "asdfgh", "asdfghjkl", "yxcvbnm", "ichliebedich",
        "iloveyou", "fussball", "fußball", "sommer", "winter", "fruehling", "herbst", "sonne", "schatz",
        "mausi", "hase", "stundenplan", "vertretung", "start", "starten", "test", "testtest", "login",
        "anmelden", "abc", "abcdef", "abcdefgh", "letmein", "dragon", "monkey", "master", "football",
        "superman", "batman", "pokemon", "minecraft", "fortnite", "schalke", "dortmund", "bayern",
    };

    /// <summary>
    /// Prüft ein neues Passwort. null = in Ordnung, sonst ein verständlicher Hinweis.
    /// namen: Benutzername und ggf. Vor-/Nachname bzw. Kürzel (dürfen nicht vorkommen).
    /// </summary>
    public static string? Pruefen(string? passwort, BenutzerRolle rolle, IEnumerable<string?> namen, string? altesPasswort = null)
    {
        passwort ??= "";
        int min = MindestLaenge(rolle);
        if (passwort.Length < min)
            return $"Das Passwort muss mindestens {min} Zeichen lang sein.";
        if (passwort.Length > Max)
            return $"Das Passwort darf höchstens {Max} Zeichen lang sein.";
        if (passwort.Trim().Length != passwort.Length)
            return "Das Passwort darf nicht mit einem Leerzeichen beginnen oder enden.";
        if (altesPasswort != null && passwort == altesPasswort)
            return "Das neue Passwort muss sich vom bisherigen unterscheiden.";

        var klein = passwort.ToLowerInvariant();
        foreach (var n in namen)
        {
            var name = (n ?? "").Trim().ToLowerInvariant();
            if (name.Length >= 3 && klein.Contains(name))
                return "Das Passwort darf deinen Namen bzw. Benutzernamen nicht enthalten.";
            // Teile von „vorname.nachname" einzeln prüfen (ab 4 Zeichen).
            foreach (var teil in name.Split('.', '-', ' ', '_'))
                if (teil.Length >= 4 && klein.Contains(teil))
                    return "Das Passwort darf deinen Namen bzw. Benutzernamen nicht enthalten.";
        }

        if (passwort.Distinct().Count() <= 2)
            return "Das Passwort ist zu einfach (fast nur ein Zeichen wiederholt).";

        if (IstFolge(klein))
            return "Das Passwort ist zu einfach (Zeichen- oder Tastenfolge).";

        var kern = klein.TrimEnd("0123456789!?.-_#*+ ".ToCharArray()).TrimStart("0123456789!?.-_#*+ ".ToCharArray());
        if (kern.Length == 0 || Haeufig.Contains(kern) || Haeufig.Contains(klein))
            return "Dieses Passwort ist zu bekannt und leicht zu erraten. Bitte ein anderes wählen.";

        return null;
    }

    /// <summary>Kurzer Regeltext für die App.</summary>
    public static string RegelText(BenutzerRolle rolle)
        => $"Mindestens {MindestLaenge(rolle)} Zeichen. Am besten mehrere Wörter, z. B. „Tafel Kreide Pausenhof“. " +
           "Nicht der eigene Name, keine bekannten Passwörter wie „passwort123“.";

    /// <summary>Gleichmäßig auf- oder absteigende Zeichen („12345678", „abcdefgh") bzw. Tastenreihen.</summary>
    private static bool IstFolge(string s)
    {
        if (s.Length < 4) return false;
        bool auf = true, ab = true;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] != s[i - 1] + 1) auf = false;
            if (s[i] != s[i - 1] - 1) ab = false;
        }
        if (auf || ab) return true;

        foreach (var reihe in new[] { "1234567890ß", "qwertzuiopü", "asdfghjklöä", "yxcvbnm", "qwertyuiop", "zxcvbnm" })
        {
            if (reihe.Contains(s)) return true;
            var rueck = new string(reihe.Reverse().ToArray());
            if (rueck.Contains(s)) return true;
        }
        return false;
    }

    // Startpasswort: zwei Silbengruppen + zwei Ziffern, z. B. "kamo-tefi-37" (12 Zeichen).
    // Ohne l/i/o-Verwechslungen mit 1/0: Konsonanten ohne l, Vokale ohne i und o.
    private const string Konsonanten = "bdfghkmnprstvwz";
    private const string Vokale = "aeu";
    private const string Ziffern = "23456789";

    public static string StartPasswort()
    {
        var sb = new StringBuilder();
        for (int gruppe = 0; gruppe < 2; gruppe++)
        {
            for (int silbe = 0; silbe < 2; silbe++)
            {
                sb.Append(Konsonanten[RandomNumberGenerator.GetInt32(Konsonanten.Length)]);
                sb.Append(Vokale[RandomNumberGenerator.GetInt32(Vokale.Length)]);
            }
            sb.Append('-');
        }
        sb.Append(Ziffern[RandomNumberGenerator.GetInt32(Ziffern.Length)]);
        sb.Append(Ziffern[RandomNumberGenerator.GetInt32(Ziffern.Length)]);
        return sb.ToString();
    }

    /// <summary>
    /// Benutzername-Vorschlag „vorname.nachname": klein, Umlaute ausgeschrieben (ä → ae, ß → ss),
    /// Akzente entfernt, Leerzeichen → „-", nur a–z, 0–9, „.", „-".
    /// </summary>
    public static string NameVorschlag(string vorname, string nachname)
    {
        string Teil(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant()
                .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
            var ohneAkzent = new StringBuilder();
            foreach (var c in s.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) ohneAkzent.Append(c);
            var erlaubt = new StringBuilder();
            foreach (var c in ohneAkzent.ToString())
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9') erlaubt.Append(c);
                else if (c is ' ' or '-' or '\'' && erlaubt.Length > 0 && erlaubt[^1] != '-') erlaubt.Append('-');
            }
            return erlaubt.ToString().Trim('-');
        }
        var v = Teil(vorname);
        var n = Teil(nachname);
        var name = v.Length > 0 && n.Length > 0 ? $"{v}.{n}" : v + n;
        if (name.Length == 0) name = "schueler";
        return name.Length > 40 ? name[..40].TrimEnd('.', '-') : name;
    }

    /// <summary>Erlaubte Zeichen für frei gewählte Benutzernamen (Admin).</summary>
    public static string? BenutzernameFehler(string name)
    {
        if (name.Length < 2 || name.Length > 40) return "Der Benutzername muss 2 bis 40 Zeichen lang sein.";
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                return "Im Benutzernamen sind nur Buchstaben a–z, Ziffern und . - _ erlaubt (keine Umlaute, keine Leerzeichen).";
        return null;
    }
}
