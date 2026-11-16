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
}
