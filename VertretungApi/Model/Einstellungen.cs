namespace VertretungApi.Model;

// =====================================================================
//  P3 – E-1 Schuleinstellungen (Entscheidungen 139–141, 199–203; Migration „Einstellungen“).
//
//  Schulwerte stehen in der Datenbank und werden vom Admin in der App geändert – ohne Neustart,
//  protokolliert. Was vor dem Start feststehen muss (Datenbank, Schlüssel, Adresse, Proxy, HTTPS),
//  bleibt in der Einstellungsdatei (139). Welche Schlüssel es gibt: Services/Schuleinstellungen.cs.
// =====================================================================

/// <summary>Eine Schuleinstellung. Wert null = Standard (heutiges Verhalten, Entscheidung 70).</summary>
public class Einstellung
{
    /// <summary>Fester Schlüssel, z. B. "Schuljahr.ErfassungAb" (Primärschlüssel).</summary>
    public string Schluessel { get; set; } = "";

    /// <summary>Text, Datum (JJJJ-MM-TT), ganze Zahl oder true/false; null = Standard.</summary>
    public string? Wert { get; set; }

    public DateTime? GeaendertAm { get; set; }   // UTC
    public string? GeaendertVon { get; set; }    // Benutzername bzw. „Übernahme (appsettings)“

    /// <summary>Bei Einstellungen mit Datenschutzwirkung (141): abgestimmt am / mit (z. B. „DSB Frau Muster“).</summary>
    public DateOnly? AbgestimmtAm { get; set; }
    public string? AbgestimmtMit { get; set; }
}

/// <summary>
/// Protokoll der Einstellungen (141): wer hat wann was von welchem Wert auf welchen geändert – wie das
/// Kontenprotokoll, mit derselben Aufbewahrungsfrist (Entscheidung 202).
/// </summary>
public class EinstellungsProtokoll
{
    public int Id { get; set; }
    public DateTime Zeit { get; set; } = DateTime.UtcNow;   // UTC
    public string Akteur { get; set; } = "";                // Benutzername bzw. „System“
    public string Schluessel { get; set; } = "";
    public string? Alt { get; set; }                        // lesbarer Text, z. B. „02.11.2026“, „Standard (…)“
    public string? Neu { get; set; }
    public string? Details { get; set; }                    // z. B. „abgestimmt am 01.12.2026 mit DSB“
}
