using System.Security.Cryptography;
using System.Text;

namespace VertretungApi.Services;

// =====================================================================
//  P4b (5.3a, Entscheidung 210): Abholcodes für den Rückweg nach SchILD.
//
//  Der Admin erzeugt in der App einen Code (8 Zeichen, 15 Minuten gültig, ein Abruf). Der Desktop holt damit –
//  zusammen mit dem Push-Schlüssel – die Fehlstunden des Zeitraums ab. Es gibt höchstens einen gültigen Code; ein neuer
//  ersetzt den alten. Nach 5 falschen Versuchen verfällt der Code. Alles liegt nur im Arbeitsspeicher: Ein Neustart der
//  API macht jeden Code ungültig (dann einfach einen neuen erzeugen).
//
//  Nach dem Abruf bekommt der Desktop ein Merkzeichen, mit dem er bis zu 2 Stunden lang melden kann, welche Datei er
//  geschrieben hat (vollständig bzw. Probedatei) – für den Jahresablauf. Zeiten sind echte Zeiten (kein Testdatum).
// =====================================================================

public sealed class RueckwegCodes
{
    /// <summary>Ohne leicht verwechselbare Zeichen (0/O, 1/I/L).</summary>
    private const string Zeichen = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Laenge = 8;
    public static readonly TimeSpan Gueltigkeit = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MeldenBis = TimeSpan.FromHours(2);
    public const int MaxFehlversuche = 5;
    public const int MaxMeldungen = 10;

    public sealed record Auftrag(string Schuljahr, DateOnly Von, DateOnly Bis, bool Pseudo, string Akteur,
        DateTime ErzeugtUtc, DateTime GueltigBisUtc);

    public sealed record Abruf(string Schuljahr, DateOnly Von, DateOnly Bis, bool Pseudo, string Akteur, string Desktop,
        DateTime GueltigBisUtc);

    private readonly object _sperre = new();
    private byte[]? _hash;          // SHA-256 des Codes – der Code selbst wird nicht aufbewahrt
    private Auftrag? _auftrag;
    private int _fehlversuche;
    private readonly Dictionary<string, (Abruf A, int Meldungen)> _abrufe = new(StringComparer.Ordinal);

    /// <summary>Code ohne Bindestrich und Leerzeichen, Großbuchstaben.</summary>
    public static string Norm(string? code)
        => new string((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

    /// <summary>„ABCD-EFGH“ zum Abschreiben.</summary>
    public static string Anzeige(string code) => code.Length == Laenge ? code[..4] + "-" + code[4..] : code;

    private static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    /// <summary>Neuer Code; ein noch gültiger alter verfällt.</summary>
    public (string Code, Auftrag Auftrag) Erzeugen(string schuljahr, DateOnly von, DateOnly bis, bool pseudo, string akteur)
    {
        var sb = new StringBuilder(Laenge);
        for (int i = 0; i < Laenge; i++) sb.Append(Zeichen[RandomNumberGenerator.GetInt32(Zeichen.Length)]);
        string code = sb.ToString();
        var jetzt = DateTime.UtcNow;
        var a = new Auftrag(schuljahr, von, bis, pseudo, akteur, jetzt, jetzt + Gueltigkeit);
        lock (_sperre)
        {
            _hash = Hash(code);
            _auftrag = a;
            _fehlversuche = 0;
        }
        return (code, a);
    }

    /// <summary>Der gültige Code (ohne den Code selbst) bzw. null.</summary>
    public Auftrag? Aktuell()
    {
        lock (_sperre)
        {
            if (_auftrag != null && _auftrag.GueltigBisUtc <= DateTime.UtcNow) Leeren();
            return _auftrag;
        }
    }

    public bool Verwerfen()
    {
        lock (_sperre)
        {
            bool war = _auftrag != null && _auftrag.GueltigBisUtc > DateTime.UtcNow;
            Leeren();
            return war;
        }
    }

    private void Leeren()
    {
        _hash = null;
        _auftrag = null;
        _fehlversuche = 0;
    }

    /// <summary>
    /// Prüft den Code in konstanter Zeit. Treffer → Auftrag, der Code ist verbraucht. Sonst null und ein Grund
    /// („abgelaufen“, „falsch“, „verfallen“ nach zu vielen Versuchen, „keiner“).
    /// </summary>
    public Auftrag? Einloesen(string? code, out string grund)
    {
        string c = Norm(code);
        lock (_sperre)
        {
            grund = "";
            if (_auftrag == null || _hash == null) { grund = "keiner"; return null; }
            if (_auftrag.GueltigBisUtc <= DateTime.UtcNow) { Leeren(); grund = "abgelaufen"; return null; }
            bool passt = c.Length == Laenge && CryptographicOperations.FixedTimeEquals(Hash(c), _hash);
            if (!passt)
            {
                _fehlversuche++;
                if (_fehlversuche >= MaxFehlversuche) { Leeren(); grund = "verfallen"; }
                else grund = "falsch";
                return null;
            }
            var a = _auftrag;
            Leeren();
            return a;
        }
    }

    /// <summary>Merkzeichen für die Meldung „Datei geschrieben“ (zufällig, 2 Stunden gültig).</summary>
    public string AbrufMerken(Abruf a)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        lock (_sperre)
        {
            var jetzt = DateTime.UtcNow;
            foreach (var alt in _abrufe.Where(kv => kv.Value.A.GueltigBisUtc <= jetzt).Select(kv => kv.Key).ToList())
                _abrufe.Remove(alt);
            _abrufe[token] = (a, 0);
        }
        return token;
    }

    /// <summary>Abruf zum Merkzeichen (zählt eine Meldung) bzw. null, wenn unbekannt, abgelaufen oder zu oft gemeldet.</summary>
    public Abruf? Melden(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        lock (_sperre)
        {
            if (!_abrufe.TryGetValue(token.Trim(), out var e)) return null;
            if (e.A.GueltigBisUtc <= DateTime.UtcNow || e.Meldungen >= MaxMeldungen)
            {
                if (e.A.GueltigBisUtc <= DateTime.UtcNow) _abrufe.Remove(token.Trim());
                return null;
            }
            _abrufe[token.Trim()] = (e.A, e.Meldungen + 1);
            return e.A;
        }
    }
}
