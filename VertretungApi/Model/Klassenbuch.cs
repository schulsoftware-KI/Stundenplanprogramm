namespace VertretungApi.Model;

// =====================================================================
//  Klassenbuch-Datenmodell (M-Phase 1A). Klasse (Sek I) und Kurs (Sek II)
//  werden EINHEITLICH als "Lerngruppe" modelliert; Schüler ↔ Lerngruppe
//  ist n:m über Zugehoerigkeit. Eine Lektion ist eine konkret gehaltene
//  Stunde einer Lerngruppe (Grundlage für Anwesenheit in Phase 3).
//
//  Alle Datensätze tragen IstPseudo: in der Entwicklung nur synthetische
//  Daten; echte SchILD-Daten kommen erst im Produktivsystem (Phase 5).
// =====================================================================

public enum LerngruppeTyp { Klasse, Kurs }

public enum Kursart { Keine, GK, LK, VK, PJK }   // Grund-/Leistungs-/Vertiefungs-/Projektkurs

public class Lerngruppe
{
    public int Id { get; set; }
    public LerngruppeTyp Typ { get; set; }
    public string Bezeichnung { get; set; } = "";   // "05A" oder "Q1 M LK1"
    public string Stufe { get; set; } = "";          // "05".."10", "EF", "Q1", "Q2"
    public string? Fach { get; set; }                // bei Kurs gesetzt
    public Kursart Kursart { get; set; } = Kursart.Keine;
    public string? LehrerKuerzel { get; set; }
    public string Schuljahr { get; set; } = "";
    public bool IstPseudo { get; set; }

    public List<Zugehoerigkeit> Mitglieder { get; set; } = new();
}

public class Schueler
{
    public int Id { get; set; }
    public string Nachname { get; set; } = "";
    public string Vorname { get; set; } = "";
    public string Stufe { get; set; } = "";          // Jahrgang des Schülers
    public string? ExtId { get; set; }               // SchILD-Id (später)
    public bool IstPseudo { get; set; }

    public List<Zugehoerigkeit> Zugehoerigkeiten { get; set; } = new();
}

/// <summary>Verknüpft einen Schüler mit einer Lerngruppe (Klasse/Kurs) im Schuljahr.</summary>
public class Zugehoerigkeit
{
    public int Id { get; set; }
    public int SchuelerId { get; set; }
    public Schueler? Schueler { get; set; }
    public int LerngruppeId { get; set; }
    public Lerngruppe? Lerngruppe { get; set; }
    public string Schuljahr { get; set; } = "";
}

/// <summary>Eine konkret gehaltene Stunde einer Lerngruppe (für Anwesenheit, Phase 3).</summary>
public class Lektion
{
    public int Id { get; set; }
    public DateOnly Datum { get; set; }
    public int Stunde { get; set; }
    public int LerngruppeId { get; set; }
    public Lerngruppe? Lerngruppe { get; set; }
    public int UNr { get; set; }
    public string Fach { get; set; } = "";
    public string? Lehrer { get; set; }
    public string Status { get; set; } = "gehalten";   // gehalten/vertreten/entfallen
    public bool IstPseudo { get; set; }
}
