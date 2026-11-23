namespace VertretungApi.Dtos;

// =====================================================================
//  P4b (5.3a, Entscheidungen 210–213): Rückweg der Fehlstunden nach SchILD.
//  App (Admin, JWT): Vorschau mit Zahlen und Abholcode. Desktop (X-Api-Key + Abholcode): Abruf der Fehlstunden – nur
//  mit Prüfsumme (ExtId), Datum, Stunde, Fach und Lehrkraft laut Grundplan und Bewertung, nie mit Namen.
// =====================================================================

/// <summary>Wählbarer Zeitraum: „hj1“, „hj2“ bzw. „sj“ (ohne 2. Halbjahr).</summary>
public sealed record RwZeitraumDto(string Id, string Text, DateOnly Von, DateOnly Bis);

/// <summary>Zahlen eines Zeitraums. Übertragen werden entschuldigt, unentschuldigt und beurlaubt (= entschuldigt).</summary>
public sealed record RwZahlenDto(
    int Entschuldigt,
    int Unentschuldigt,
    int Beurlaubt,
    int Offen,                  // Fehlstunden ohne Bewertung – nicht übertragen
    int Verspaetungen,          // SchILD kennt keine Minuten – nicht übertragen
    int Schulveranstaltung,     // nicht übertragen
    int Doppelt,                // dieselbe Stunde zweimal erfasst – eine zählt (unentschuldigt vor entschuldigt)
    int OhnePruefsumme,         // Fehlstunden von Schülern ohne Prüfsumme (nicht aus SchILD importiert) – kein Treffer
    int SchuelerOhnePruefsumme,
    int Schueler);              // Schüler mit übertragbaren Fehlstunden (mit Prüfsumme)

/// <summary>Frühere Bereitstellung laut Download-Protokoll (Abruf, vollständige Datei bzw. Probedatei).</summary>
public sealed record RwFruehererDto(string Art, DateTime Zeit, string Akteur, DateOnly Von, DateOnly Bis, string Was);

/// <summary>Gültiger Abholcode – ohne den Code selbst (der steht nur einmal in der Antwort auf „erzeugen“).</summary>
public sealed record RwCodeStandDto(DateTime GueltigBis, DateOnly Von, DateOnly Bis, string Akteur);

public sealed record RwVorschauDto(
    string Schuljahr,
    DateOnly Heute,
    bool Testdatum,
    DateOnly? KalenderStart,
    DateOnly? KalenderEnde,
    DateOnly? HalbjahrAb,
    IReadOnlyList<RwZeitraumDto> Zeitraeume,
    string Standard,
    DateOnly Von,
    DateOnly Bis,
    bool UeberHalbjahr,         // Zeitraum reicht über „2. Halbjahr ab“ – SchILD braucht je Abschnitt eine Datei
    RwZahlenDto Zahlen,
    int NichtErfasst,           // Stunden ohne Anwesenheit im Zeitraum (ab „Anwesenheit erfasst ab“, bis gestern)
    IReadOnlyList<RwFruehererDto> Frueher,
    RwCodeStandDto? Code,
    bool Pseudo);

public sealed record RwCodeAnfrageDto(DateOnly Von, DateOnly Bis);

public sealed record RwCodeDto(string Code, DateTime GueltigBis, DateOnly Von, DateOnly Bis, int Minuten);

/// <summary>Desktop → API: Abruf mit Code. Pseudo und Kennung prüft die API, bevor sie den Code verbraucht.</summary>
public sealed record RwAbrufAnfrageDto(string? Code, bool Pseudo, string? Kennung, string? Desktop);

/// <summary>Eine Fehlstunde: Prüfsumme des Schülers, Datum, Stunde, Fach und Lehrkraft laut Grundplan, Bewertung.</summary>
public sealed record RwZeileDto(string ExtId, DateOnly Datum, int Stunde, string Fach, string Lehrkraft, string Art);

public sealed record RwAbrufDto(
    string Schuljahr,
    DateOnly Von,
    DateOnly Bis,
    bool Pseudo,
    DateOnly? KalenderStart,
    DateOnly? KalenderEnde,
    DateOnly? HalbjahrAb,
    string Akteur,              // wer den Code erzeugt hat
    string Merkzeichen,         // für die Meldung „Datei geschrieben“ (2 Stunden gültig)
    RwZahlenDto Zahlen,
    int NichtErfasst,
    IReadOnlyList<RwZeileDto> Zeilen);

/// <summary>Desktop → API: welche Datei geschrieben wurde (ohne Namen) – für Download-Protokoll und Jahresablauf.</summary>
public sealed record RwDateiMeldungDto(
    string? Merkzeichen,
    DateOnly Von,
    DateOnly Bis,
    int Jahr,
    int Abschnitt,
    bool Probe,
    string? Klassen,            // Probedatei: Klassen/Stufen, z. B. „05A, Q1“; null = alle
    int Zeilen,
    int Fehlstunden,
    int Entschuldigt,
    int Unentschuldigt,
    int Schueler);
