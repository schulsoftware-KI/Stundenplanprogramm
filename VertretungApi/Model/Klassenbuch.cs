namespace VertretungApi.Model;

// =====================================================================
//  Klassenbuch-Datenmodell (Phase 1A, erweitert in Phase 3.1).
//  Klasse (Sek I) und Kurs (Sek II) werden EINHEITLICH als "Lerngruppe"
//  modelliert; Schüler ↔ Lerngruppe ist n:m über Zugehoerigkeit.
//
//  Phase 3.1:
//   - Lerngruppe Typ "Gruppe": Teilgruppe der Sek I mit Schülergruppe laut
//     Grundplan (Religion, WP, Differenzierung, AG über Klassen). Die
//     Fachlehrkraft stellt sie selbst aus den Schülern der genannten Klassen
//     zusammen. Bezeichnung = Schülergruppe, z. B. "ER_05A05B05C05D_L88".
//     Gibt es den Datensatz, gilt die Gruppe als „zusammengestellt".
//   - Lektion: eine konkrete Stunde mit Klassenbuch. Identifiziert wie im
//     Wochenplan über (Datum, Stunde, UNr laut Grundplan, Lehrkraft laut
//     Grundplan) – unabhängig von Lerngruppen-Ids, die ein Pseudodaten-Seed
//     neu vergibt. Entsteht erst beim ersten Speichern.
//   - Anwesenheit: nur Abweichungen (fehlt, verspätet, beurlaubt, schulische
//     Veranstaltung). Wer keinen Eintrag hat, war anwesend – sofern die
//     Lektion als „erfasst" vermerkt ist.
//   - KlassenbuchProtokoll: Änderungshistorie (wer, wann, alt → neu).
//
//  Alle Datensätze tragen IstPseudo: in der Entwicklung nur synthetische
//  Daten; echte SchILD-Daten kommen erst im Produktivsystem (Phase 5).
// =====================================================================

/// <summary>Klasse (Sek I), Kurs (Sek II) oder Gruppe (Sek-I-Teilgruppe, Phase 3.1).</summary>
public enum LerngruppeTyp { Klasse, Kurs, Gruppe }

public enum Kursart { Keine, GK, LK, VK, PJK }   // Grund-/Leistungs-/Vertiefungs-/Projektkurs

public class Lerngruppe
{
    public int Id { get; set; }
    public LerngruppeTyp Typ { get; set; }
    public string Bezeichnung { get; set; } = "";   // "05A", "Q1 M LK1" bzw. Schülergruppe
    public string Stufe { get; set; } = "";          // "05".."10", "EF", "Q1", "Q2"
    public string? Fach { get; set; }                // bei Kurs/Gruppe gesetzt
    public Kursart Kursart { get; set; } = Kursart.Keine;
    public string? LehrerKuerzel { get; set; }
    public string Schuljahr { get; set; } = "";
    public bool IstPseudo { get; set; }

    // Phase 3.1 (Gruppe): zuletzt zusammengestellt von/am (UTC).
    public string? BearbeitetVon { get; set; }
    public DateTime? BearbeitetAm { get; set; }

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

/// <summary>Verknüpft einen Schüler mit einer Lerngruppe (Klasse/Kurs/Gruppe) im Schuljahr.</summary>
public class Zugehoerigkeit
{
    public int Id { get; set; }
    public int SchuelerId { get; set; }
    public Schueler? Schueler { get; set; }
    public int LerngruppeId { get; set; }
    public Lerngruppe? Lerngruppe { get; set; }
    public string Schuljahr { get; set; } = "";
}

/// <summary>
/// Eine konkrete Stunde mit Klassenbuch (Phase 3.1), Tabelle "KlassenbuchLektionen".
/// Schlüssel: (Datum, Stunde, GrundUNr, GrundLehrer). Die übrigen Felder sind eine
/// Momentaufnahme beim letzten Speichern (Grundlage für spätere Auswertungen).
/// Löst die ungenutzte Phase-1A-Lektion ab (Tabelle "Lektionen", nur eine Lerngruppe).
/// </summary>
public class Lektion
{
    public int Id { get; set; }
    public DateOnly Datum { get; set; }
    public int Stunde { get; set; }
    public int GrundUNr { get; set; }                  // UNr laut Grundplan (Zusatzstunde: aus der Vertretung)
    public string GrundLehrer { get; set; } = "";      // Lehrkraft laut Grundplan, GROSS geschrieben
    public string Schuljahr { get; set; } = "";

    public int UNr { get; set; }                       // gehaltene UNr (bei Verlegung: die verlegte)
    public string Fach { get; set; } = "";
    public string Lehrer { get; set; } = "";           // haltende Lehrkraft
    public string Status { get; set; } = "Normal";     // Normal | Vertreten | VerlegtHin | Betreuung | EVA | …
    public string Lerngruppen { get; set; } = "";      // komma-separiert
    public string Klassen { get; set; } = "";          // komma-separiert (Rohwerte, bei Kursen EF/Q1/Q2)

    public string? ErfasstVon { get; set; }            // erste Erfassung (Benutzername)
    public DateTime? ErfasstAm { get; set; }           // UTC
    public string? GeaendertVon { get; set; }          // letzte Änderung
    public DateTime? GeaendertAm { get; set; }         // UTC
    public int Version { get; set; }                   // erhöht sich bei jedem Speichern (Konflikterkennung)
    public bool IstPseudo { get; set; }

    public List<Anwesenheit> Anwesenheiten { get; set; } = new();
}

/// <summary>Status einer Schülerin / eines Schülers in einer Lektion. Anwesend wird nicht gespeichert.</summary>
public enum AnwesenheitStatus { Anwesend = 0, Fehlt = 1, Verspaetet = 2, Beurlaubt = 3, Schulveranstaltung = 4 }

/// <summary>Abweichung von „anwesend" in einer Lektion (Phase 3.1).</summary>
public class Anwesenheit
{
    public int Id { get; set; }
    public int LektionId { get; set; }
    public Lektion? Lektion { get; set; }
    public int SchuelerId { get; set; }
    public Schueler? Schueler { get; set; }
    public AnwesenheitStatus Status { get; set; }
    public int? Minuten { get; set; }                  // nur bei Verspaetet
    public string? GeaendertVon { get; set; }
    public DateTime GeaendertAm { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Änderungshistorie des Klassenbuchs (Phase 3.1): jede Änderung an der Anwesenheit
/// (alt → neu), die erste Erfassung einer Lektion und Änderungen an Gruppenlisten.
/// Keine Änderungsfrist – dafür ist jede Änderung nachvollziehbar.
/// </summary>
public class KlassenbuchProtokoll
{
    public int Id { get; set; }
    public DateTime Zeit { get; set; } = DateTime.UtcNow;   // UTC
    public string Akteur { get; set; } = "";                // Benutzername
    public string Art { get; set; } = "";                   // Anwesenheit | Erfasst | Gruppe
    public int? LektionId { get; set; }
    public string? Gruppe { get; set; }                     // Schülergruppe bei Art "Gruppe"
    public int? SchuelerId { get; set; }
    public string? Alt { get; set; }
    public string? Neu { get; set; }
    public bool IstPseudo { get; set; }
}
