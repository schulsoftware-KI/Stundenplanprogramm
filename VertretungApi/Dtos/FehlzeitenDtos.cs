namespace VertretungApi.Dtos;

// =====================================================================
//  Fehlzeiten (Phase 3.2): Summen, Schülerdetail, Listen je Klasse/Stufe
//  bzw. Unterricht, Entschuldigungen, nicht erfasste Stunden.
//  Bewertung: Offen | Entschuldigt | Unentschuldigt.
//  Grund:     Krankheit | Arzttermin | Familiaer | Sonstiges.
// =====================================================================

/// <summary>
/// Summen für einen Schüler bzw. eine Zeile. Fehlstunden = fehlt + beurlaubt (beurlaubt zählt
/// als entschuldigt); schulische Veranstaltungen sind keine Fehlzeit und stehen nur zur Info dabei.
/// </summary>
public record FzSummeDto(
    int Fehlstunden,
    int Entschuldigt,
    int Offen,
    int Unentschuldigt,
    int Verspaetungen,
    int VerspaetungMinuten,
    int VerspaetungenOffen,
    int VerspaetungenUnentschuldigt,
    int Schulveranstaltungen);

/// <summary>Eine Fehlstunde, Verspätung, Beurlaubung oder schulische Veranstaltung.</summary>
public record FzEintragDto(
    int Id,                                // Id der Anwesenheit (für PUT eintrag/{id})
    DateOnly Datum,
    int Stunde,
    string Fach,
    string Lehrer,                         // haltende Lehrkraft
    string GrundLehrer,                    // Lehrkraft laut Plan (Kurslehrkraft)
    int GrundUNr,
    string Klassen,
    string Status,                         // Fehlt | Verspaetet | Beurlaubt | Schulveranstaltung
    int? Minuten,
    string? Bewertung,                     // Offen | Entschuldigt | Unentschuldigt; null = keine Fehlzeit
    string? Grund,
    int? EntschuldigungId,
    string? BewertetVon,
    DateTime? BewertetAm,
    bool Festgelegt,                       // von KL/SK/Admin bewertet: Kurslehrkraft kann nicht mehr ändern
    bool DarfBewerten,                     // darf entschuldigt/offen/unentschuldigt setzen
    string? Sperrgrund);                   // warum nicht (z. B. „festgelegt von koQ1“)

/// <summary>Summe je Unterricht (Fach + Lehrkraft laut Plan).</summary>
public record FzFachDto(string Fach, string Lehrer, FzSummeDto Summe);

/// <summary>Entschuldigung für einen Zeitraum.</summary>
public record FzEntschuldigungDto(
    int Id, DateOnly Von, DateOnly Bis, string Grund, string ErfasstVon, DateTime ErfasstAm, int Stunden);

/// <summary>Fehlzeiten eines Schülers.</summary>
public record FzSchuelerDto(
    int Id,
    string Vorname,
    string Nachname,
    string? Klasse,
    DateOnly Von,
    DateOnly Bis,
    bool NurEigeneStunden,                 // Lehrkraft ohne Zuständigkeit: nur Einträge aus eigenen Stunden
    string? Unterricht,                    // gefiltert auf einen Unterricht, z. B. „M L1 · Q1“
    bool DarfZeitraumEntschuldigen,        // Klassenleitung/Stufenkoordination/Admin
    FzSummeDto Summe,
    IReadOnlyList<FzFachDto> Faecher,
    IReadOnlyList<FzEintragDto> Eintraege,
    IReadOnlyList<FzEntschuldigungDto> Entschuldigungen);

/// <summary>Zeile in einer Klassen-/Stufen- bzw. Unterrichtsliste.</summary>
public record FzZeileDto(int Id, string Vorname, string Nachname, string? Klasse, FzSummeDto Summe);

/// <summary>Liste je Klasse/Stufe bzw. je Unterricht.</summary>
public record FzListeDto(
    string Titel,
    DateOnly Von,
    DateOnly Bis,
    int? ErfassteStunden,                  // nur Unterricht: erfasste Stunden im Zeitraum
    IReadOnlyList<FzZeileDto> Schueler);

/// <summary>Unterricht der Lehrkraft (für „Meine Kurse“).</summary>
public record FzUnterrichtDto(int UNr, string Lehrer, string Fach, IReadOnlyList<string> Klassen, bool Oberstufe);

/// <summary>Was darf die angemeldete Person in den Fehlzeiten sehen?</summary>
public record FzRechteDto(
    bool Lehrkraft,                        // hat ein Kürzel: eigene Stunden/Kurse
    bool Admin,
    IReadOnlyList<string> Klassen,         // wählbare Klassen/Stufen (Zuständigkeit bzw. Admin: alle)
    DateOnly ErfassungAb);

/// <summary>Nicht erfasste Stunden (offen ab dem Folgetag).</summary>
public record FzNichtErfasstDto(
    DateOnly Von,
    DateOnly Bis,
    int Anzahl,
    IReadOnlyList<FzNeLehrerDto> ProLehrer,
    IReadOnlyList<FzNeStundeDto> Stunden,
    bool Gekuerzt);                        // mehr als angezeigt (höchstens 500)

public record FzNeLehrerDto(string Lehrer, int Anzahl);

public record FzNeStundeDto(
    DateOnly Datum,
    int Stunde,
    int GrundUNr,
    string GrundLehrer,
    string Fach,
    string Lehrer,
    string? OriginalLehrer,
    IReadOnlyList<string> Klassen,
    string Status,
    bool GruppeFehlt);                     // Sek-I-Gruppe noch nicht zusammengestellt

public record FzEntschuldigungNeuDto(DateOnly Von, DateOnly Bis, string Grund);

public record FzBewertungDto(string Bewertung, string? Grund);
