namespace VertretungApi.Dtos;

// =====================================================================
//  Fehlzeiten (Phase 3.2): Summen, Schülerdetail, Listen je Klasse/Stufe
//  bzw. Unterricht, Entschuldigungen, nicht erfasste Stunden.
//  Bewertung: Offen | Entschuldigt | Unentschuldigt.
//  Grund:     Krankheit | Arzttermin | Familiaer | Sonstiges.
//
//  Phase 4.1 (Auswertungen, Entscheidungen 40–43): Zeiträume (Halbjahre,
//  Schuljahr), „Soll" = erfasste Stunden, in denen der Schüler laut Liste
//  dabei sein sollte (Grundlage der Fehlquote = Fehlstunden ÷ Soll),
//  Gesamtzeile je Liste, offene Fälle (ganzes Schuljahr, älteste zuerst).
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

/// <summary>Summe je Unterricht (Fach + Lehrkraft laut Plan); Soll = erfasste Stunden dieses Unterrichts.</summary>
public record FzFachDto(string Fach, string Lehrer, FzSummeDto Summe, int Soll);

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
    IReadOnlyList<FzEntschuldigungDto> Entschuldigungen,
    int Soll);                             // Phase 4.1: erfasste Stunden im Zeitraum (Grundlage der Fehlquote)

/// <summary>Zeile in einer Klassen-/Stufen- bzw. Unterrichtsliste; Soll = erfasste Stunden des Schülers.</summary>
public record FzZeileDto(int Id, string Vorname, string Nachname, string? Klasse, FzSummeDto Summe, int Soll);

/// <summary>Liste je Klasse/Stufe bzw. je Unterricht.</summary>
public record FzListeDto(
    string Titel,
    DateOnly Von,
    DateOnly Bis,
    int? ErfassteStunden,                  // nur Unterricht: erfasste Stunden im Zeitraum
    IReadOnlyList<FzZeileDto> Schueler,
    FzSummeDto Gesamt,                     // Phase 4.1: Summe über alle Zeilen
    int Soll);                             // Phase 4.1: Summe der Soll-Stunden aller Zeilen

/// <summary>Unterricht der Lehrkraft (für „Meine Kurse“).</summary>
public record FzUnterrichtDto(int UNr, string Lehrer, string Fach, IReadOnlyList<string> Klassen, bool Oberstufe);

/// <summary>Was darf die angemeldete Person in den Fehlzeiten sehen?</summary>
public record FzRechteDto(
    bool Lehrkraft,                        // hat ein Kürzel: eigene Stunden/Kurse
    bool Admin,
    IReadOnlyList<string> Klassen,         // wählbare Klassen/Stufen (Zuständigkeit bzw. Admin: alle)
    DateOnly ErfassungAb,
    bool Berichte);                        // Phase 4.2: darf PDFs herunterladen (Berichte:NurAdmin)

/// <summary>Nicht erfasste Stunden (offen ab dem Folgetag); Phase 3.3: auch Stunden ohne Thema.</summary>
public record FzNichtErfasstDto(
    DateOnly Von,
    DateOnly Bis,
    int Anzahl,
    IReadOnlyList<FzNeLehrerDto> ProLehrer,
    IReadOnlyList<FzNeStundeDto> Stunden,
    bool Gekuerzt,                         // mehr als angezeigt (höchstens 500)
    int OhneAnwesenheit,                   // davon ohne Anwesenheit
    int OhneThema,                         // davon ohne Thema
    DateOnly ErfassungAb,                  // Anwesenheit zählt ab diesem Tag
    DateOnly ThemaAb);                     // Thema zählt ab diesem Tag

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
    bool GruppeFehlt,                      // Sek-I-Gruppe noch nicht zusammengestellt
    bool AnwesenheitFehlt,                 // Phase 3.3
    bool ThemaFehlt);

public record FzEntschuldigungNeuDto(DateOnly Von, DateOnly Bis, string Grund);

public record FzBewertungDto(string Bewertung, string? Grund);

// =====================================================================
//  Phase 4.1: Zeiträume und offene Fälle
// =====================================================================

/// <summary>Wählbarer Zeitraum: hj1 (1. Halbjahr), hj2 (2. Halbjahr), sj (Schuljahr).</summary>
public record FzZeitraumDto(string Id, string Text, DateOnly Von, DateOnly Bis);

/// <summary>Zeiträume des laufenden Schuljahres; Standard = laufendes Halbjahr (Entscheidung 40).</summary>
public record FzZeitraeumeDto(
    string? Schuljahr,
    DateOnly Heute,
    string Standard,                       // Id des Standard-Zeitraums
    IReadOnlyList<FzZeitraumDto> Zeitraeume);

/// <summary>Ein Schüler mit offenen Fehlstunden/Verspätungen (Entscheidung 43).</summary>
public record FzOffenZeileDto(
    int Id,
    string Vorname,
    string Nachname,
    string? Klasse,
    int Fehlstunden,                       // offene Fehlstunden
    int Verspaetungen,                     // offene Verspätungen
    DateOnly Aelteste,                     // Datum der ältesten offenen Stunde
    DateOnly Neueste,                      // Datum der neuesten offenen Stunde
    int Tage);                             // offen seit … Tagen (heute − älteste)

/// <summary>Offene Fälle einer Klasse/Stufe im ganzen Schuljahr, älteste zuerst.</summary>
public record FzOffenDto(string Titel, DateOnly Von, DateOnly Bis, IReadOnlyList<FzOffenZeileDto> Schueler);
