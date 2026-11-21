namespace VertretungApi.Model;

// =====================================================================
//  Phase 5.2b (Entscheidung 164): Protokoll der SchILD-Importe.
//  Ohne Namen und ohne Personendaten – nur wer (Desktop + Windows-Benutzer), wann, welcher
//  Abschnitt, ab wann die Änderungen gelten und die Zahlen der Vorschau. Geschrieben ab
//  Lieferung b-2 (POST /api/schild/import); die Tabelle kommt schon mit b-1, damit 5.2b
//  mit einer einzigen Migration („SchildImport“) auskommt.
// =====================================================================

public class SchildImportProtokoll
{
    public int Id { get; set; }
    public DateTime Zeit { get; set; } = DateTime.UtcNow;   // UTC
    public string Akteur { get; set; } = "";                // z. B. "Desktop (verwaltung)"
    public string Schuljahr { get; set; } = "";
    public int Abschnitt { get; set; }
    public DateOnly GiltAb { get; set; }
    public bool IstPseudo { get; set; }

    public int Neu { get; set; }
    public int Geaendert { get; set; }
    public int Klassenwechsel { get; set; }
    public int Kurswechsel { get; set; }
    public int Abgaenge { get; set; }
    public int Unveraendert { get; set; }
    public int Uebersprungen { get; set; }

    /// <summary>Prüfsumme des Datenbankstands, auf der die Vorschau beruhte (Entscheidung 78).</summary>
    public string? StandPruefsumme { get; set; }

    // ---- Phase 5.2c-1 (Entscheidungen 188–190, Migration „SchildAbgleich“) ----
    /// <summary>Kennung des HMAC-Schlüssels („1A2B-3C4D“, verrät den Schlüssel nicht); leer = Import vor 5.2c.</summary>
    public string? SchluesselKennung { get; set; }
    /// <summary>Neu-Zuordnung über Name + Klasse nach einem verlorenen Schlüssel (190).</summary>
    public bool NeuZuordnung { get; set; }
    /// <summary>Bei einer Neu-Zuordnung: Kennung des Schlüssels beim letzten Import davor.</summary>
    public string? KennungVorher { get; set; }
    /// <summary>„Dieselbe Person?“ – zusammengeführte Paare (188).</summary>
    public int Zusammengefuehrt { get; set; }
    /// <summary>Bei einer Neu-Zuordnung: Schüler, die über Name + Klasse neu zugeordnet wurden.</summary>
    public int NeuZugeordnet { get; set; }
}

// =====================================================================
//  Phase 5.2c (Entscheidung 193, Tabelle schon mit der Migration „SchildAbgleich“; gefüllt ab 5.2c-2):
//  Namen der Lehrkräfte aus Lehrkraefte.dat – nur Kürzel, Nachname, Vorname und nur für Kürzel, die im
//  Grundplan vorkommen. Pseudo-Namen gehen mit den Pseudodaten.
// =====================================================================

public class Lehrkraft
{
    public int Id { get; set; }
    public string Kuerzel { get; set; } = "";
    public string Nachname { get; set; } = "";
    public string Vorname { get; set; } = "";
    public bool IstPseudo { get; set; }
    public DateTime? ImportAm { get; set; }   // UTC
}
