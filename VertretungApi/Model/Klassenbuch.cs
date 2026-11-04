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
//  Phase 3.2 (Entschuldigungen/Fehlzeiten):
//   - Jede Fehlstunde/Verspätung (Anwesenheit) trägt eine Bewertung
//     (offen/entschuldigt/unentschuldigt) samt Grund-Kategorie.
//   - Entschuldigung: Zeitraum (von–bis) eines Schülers; deckt alle
//     Fehlstunden und Verspätungen darin ab – auch später nachgetragene.
//     „Beurlaubt" gilt immer als entschuldigt, „schulische Veranstaltung"
//     ist keine Fehlzeit.
//
//  Phase 3.3 (Unterrichtsinhalt + Hausaufgaben, Entscheidungen 19–30):
//   - Lektion trägt Thema und Hausaufgabe (mit Fälligkeit). Gespeichert wird
//     im Stundenblatt zusammen mit der Anwesenheit (eine Version).
//   - UnterrichtEinstellung: je Unterricht (Schuljahr, UNr, Lehrkraft laut
//     Plan), ob Schüler die Hausaufgaben sehen (Standard: ja).
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

    // Phase 3.3: Unterrichtsinhalt und Hausaufgabe. Schreiben dürfen die haltende und die
    // ursprüngliche Lehrkraft sowie Admin; jede Änderung kommt in die Historie.
    public string? Thema { get; set; }                 // bis 300 Zeichen
    public string? Hausaufgabe { get; set; }           // bis 2000 Zeichen, Zeilenumbrüche erlaubt
    public DateOnly? HausaufgabeFaellig { get; set; }  // gesetzt, sobald eine Hausaufgabe eingetragen ist

    public List<Anwesenheit> Anwesenheiten { get; set; } = new();
}

/// <summary>
/// Einstellungen eines Unterrichts (Phase 3.3), Tabelle "UnterrichtEinstellungen".
/// Unterricht = UNr + Lehrkraft laut Plan im Schuljahr. Ohne Datensatz gelten die Standardwerte.
/// Keine personenbezogenen Schülerdaten – bleibt bei einem neuen Pseudodaten-Seed erhalten.
/// </summary>
public class UnterrichtEinstellung
{
    public int Id { get; set; }
    public string Schuljahr { get; set; } = "";
    public int UNr { get; set; }
    public string Lehrer { get; set; } = "";              // GROSS geschrieben
    public bool HausaufgabenSichtbar { get; set; } = true; // Entscheidung 30: Standard sichtbar
    public string? GeaendertVon { get; set; }
    public DateTime? GeaendertAm { get; set; }            // UTC
}

/// <summary>Status einer Schülerin / eines Schülers in einer Lektion. Anwesend wird nicht gespeichert.</summary>
public enum AnwesenheitStatus { Anwesend = 0, Fehlt = 1, Verspaetet = 2, Beurlaubt = 3, Schulveranstaltung = 4 }

/// <summary>Bewertung einer Fehlstunde bzw. Verspätung (Phase 3.2).</summary>
public enum FehlBewertung { Offen = 0, Entschuldigt = 1, Unentschuldigt = 2 }

/// <summary>Grund einer Entschuldigung – nur als Kategorie, kein Freitext (Phase 3.2).</summary>
public enum FehlGrund { Krankheit = 0, Arzttermin = 1, Familiaer = 2, Sonstiges = 3 }

/// <summary>Abweichung von „anwesend" in einer Lektion (Phase 3.1, Bewertung Phase 3.2).</summary>
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

    // Phase 3.2: Bewertung (gilt für Fehlt und Verspaetet; Beurlaubt ist immer entschuldigt).
    public FehlBewertung Bewertung { get; set; } = FehlBewertung.Offen;
    public FehlGrund? Grund { get; set; }
    public int? EntschuldigungId { get; set; }         // gesetzt, wenn ein Zeitraum sie abdeckt
    public string? BewertetVon { get; set; }
    public DateTime? BewertetAm { get; set; }          // UTC
    // Von Klassenleitung/Stufenkoordination/Admin bewertet (einzeln oder per Zeitraum):
    // Die Kurslehrkraft (Oberstufe) kann die Bewertung dann nicht mehr ändern.
    public bool Festgelegt { get; set; }
}

/// <summary>
/// Entschuldigung eines Schülers für einen Zeitraum (Phase 3.2). Eingetragen von Klassenleitung
/// bzw. Stufenkoordination oder Admin; deckt alle Fehlstunden und Verspätungen von
/// <see cref="Von"/> bis <see cref="Bis"/> ab.
/// </summary>
public class Entschuldigung
{
    public int Id { get; set; }
    public int SchuelerId { get; set; }
    public Schueler? Schueler { get; set; }
    public DateOnly Von { get; set; }
    public DateOnly Bis { get; set; }
    public FehlGrund Grund { get; set; }
    public string ErfasstVon { get; set; } = "";
    public DateTime ErfasstAm { get; set; } = DateTime.UtcNow;   // UTC
    public bool IstPseudo { get; set; }
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
    public string Art { get; set; } = "";                   // Anwesenheit | Erfasst | Gruppe | Bewertung | Entschuldigung
                                                            // | Thema | Hausaufgabe | Sichtbarkeit (Phase 3.3)
    public int? LektionId { get; set; }
    public string? Gruppe { get; set; }                     // Schülergruppe bei Art "Gruppe"; "UNr|LEHRER" bei "Sichtbarkeit"
    public int? SchuelerId { get; set; }
    public string? Alt { get; set; }
    public string? Neu { get; set; }
    public bool IstPseudo { get; set; }
}
