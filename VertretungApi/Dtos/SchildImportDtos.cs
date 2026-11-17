namespace VertretungApi.Dtos;

// =====================================================================
//  Phase 5.2b-2 (Entscheidung 166): Was der Desktop an POST /api/schild/import schickt.
//
//  Erst als Probelauf (Probelauf = true): Die API rechnet den Abgleich und liefert die
//  Vorschau samt Prüfsumme. Dann als Import (Probelauf = false) mit derselben Nutzlast,
//  der Prüfsumme der Vorschau und den bestätigten Abgängen.
//
//  Nie dabei: Geburtsdatum, SchILD-ID im Klartext, Noten, Kursart des Schülers, Daten der
//  Lehrkräfte, Status 1 (Warteliste). Schüler werden über HMAC-Prüfsummen erkannt, die nur
//  der Desktop bilden kann (Schlüssel auf dem Verwaltungsrechner).
// =====================================================================

public class SchildImportDto
{
    /// <summary>true = nur rechnen und die Vorschau liefern (Standard), false = importieren.</summary>
    public bool Probelauf { get; set; } = true;
    /// <summary>Beim Import: Prüfsumme der Vorschau, auf der die Bestätigung beruht.</summary>
    public string? Vorschau { get; set; }
    /// <summary>Beim Import: bestätigte Abgänge – Ids der Schüler aus der Vorschau („Abgang laut SchILD“, „fehlt im Export“).</summary>
    public List<int>? Abgaenge { get; set; }
    /// <summary>Windows-Benutzer des Desktops (nur fürs Importprotokoll).</summary>
    public string? Akteur { get; set; }

    public string Schuljahr { get; set; } = "";
    public int Abschnitt { get; set; }
    /// <summary>„Änderungen gelten ab“ (Entscheidung 79).</summary>
    public DateOnly GiltAb { get; set; }
    /// <summary>Pseudodaten (gültiger Marker im Desktop, Entscheidung 83/90).</summary>
    public bool Pseudo { get; set; }
    /// <summary>Plan-Spalte, mit der der Desktop zugeordnet hat (nur zum Vergleich mit dem Grundplan).</summary>
    public string? Plan { get; set; }
    /// <summary>Alle Plan-Schlüssel, die ab jetzt aus SchILD kommen – auch Gruppen ohne Schüler (Klassen, Kurse, Gruppen).</summary>
    public List<string>? PlanSchluessel { get; set; }
    public List<SchildSchuelerDto>? Schueler { get; set; }
}

public class SchildSchuelerDto
{
    /// <summary>HMAC aus Nachname (ohne #Klasse) + Vorname + Geburtsdatum.</summary>
    public string ExtId { get; set; } = "";
    /// <summary>HMAC aus der SchILD-ID (falls exportiert).</summary>
    public string? ExtIdSchild { get; set; }
    public string? ExtIdVornameGeb { get; set; }
    public string? ExtIdNachnameGeb { get; set; }

    public string Nachname { get; set; } = "";
    public string Vorname { get; set; } = "";
    /// <summary>Jahrgang: „05“ … „10“, „EF“, „Q1“, „Q2“.</summary>
    public string Stufe { get; set; } = "";
    /// <summary>Sek I: Plan-Schlüssel der Klasse („05A“); Oberstufe leer.</summary>
    public string? Klasse { get; set; }
    /// <summary>SchILD-Status: 0 Neuaufnahme · 2 aktiv · 3 beurlaubt · 6 extern · 8 Abschluss · 9 Abgang.</summary>
    public int Status { get; set; }
    /// <summary>Nur bei Status 0: Aufnahmedatum.</summary>
    public DateOnly? Aufnahme { get; set; }
    /// <summary>Plan-Schlüssel der zugeordneten Kurse bzw. Sek-I-Gruppen (bei „gemeinsam“ der gemeinsame Plan-Kurs).</summary>
    public List<string>? Kurse { get; set; }
}
