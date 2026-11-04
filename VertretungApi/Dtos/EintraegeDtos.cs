namespace VertretungApi.Dtos;

// =====================================================================
//  Klassenbucheinträge (Phase 3.4, Entscheidungen 31–39).
//  Art:       Schueler | Klasse
//  Kategorie: Lob | Hinweis | Ermahnung | HausaufgabeVergessen | MaterialVergessen  (Schülereintrag)
//             Vorkommnis | Hinweis | Lob                                            (Klasseneintrag)
//  Text bis 500 Zeichen; Pflicht bei Hinweis, Ermahnung und Vorkommnis.
// =====================================================================

/// <summary>Ein Klassenbucheintrag, wie ihn die angemeldete Person sehen darf.</summary>
public record KeEintragDto(
    int Id,
    string Art,                            // Schueler | Klasse
    string Kategorie,
    string? Text,
    DateOnly Datum,
    int? Stunde,                           // null = ohne Stunde (Klassenleitung/Stufenkoordination/Admin)
    int? GrundUNr,                         // } Schlüssel der Stunde (zum Öffnen des Stundenblatts)
    string? GrundLehrer,                   // }
    string? Lehrer,                        // haltende Lehrkraft
    string Fach,
    IReadOnlyList<string> Klassen,
    int? SchuelerId,
    string? Schueler,                      // „Vorname Nachname“
    string? SchuelerKlasse,                // Klasse (Sek I) bzw. Stufe (Sek II)
    string VerfasstVon,
    DateTime VerfasstAm,
    string? GeaendertVon,
    DateTime? GeaendertAm,
    int Version,                           // beim Ändern mitschicken (Konflikterkennung)
    bool DarfAendern);                     // Verfasser oder Admin

/// <summary>Einträge einer Stunde (Stundenblatt).</summary>
public record KeStundeDto(
    bool DarfSchreiben,                    // neue Einträge in dieser Stunde
    string? Sperrgrund,                    // warum nicht (z. B. künftige Stunde)
    IReadOnlyList<int> SchuelerErlaubt,    // für diese Schüler darf die Person Schülereinträge anlegen
    IReadOnlyList<KeEintragDto> Eintraege, // nur, was die Person lesen darf
    string? Meldung);                      // nur in der Antwort auf das Anlegen

/// <summary>Neuer Eintrag in einer Stunde. Art Schueler: je Schüler-Id ein eigener Datensatz.</summary>
public record KeNeuStundeDto(
    DateOnly Datum,
    int Stunde,
    int GrundUNr,
    string GrundLehrer,
    string Art,
    List<int>? SchuelerIds,
    string Kategorie,
    string? Text);

/// <summary>Schülereintrag ohne Stunde (Klassenleitung/Stufenkoordination/Admin), z. B. Pause, Ausflug.</summary>
public record KeNeuOhneStundeDto(DateOnly Datum, string Kategorie, string? Text);

/// <summary>Eintrag ändern (nur Verfasser und Admin): Kategorie und Text.</summary>
public record KeAendernDto(string Kategorie, string? Text, int Version);

public record KeZaehlerDto(string Kategorie, int Anzahl);

/// <summary>
/// Einträge eines Schülers, einer Klasse/Stufe bzw. eines Unterrichts – mit Zähler je Kategorie
/// (Entscheidung 37: nur Zähler, keine automatischen Hinweise).
/// </summary>
public record KeListeDto(
    string Titel,
    DateOnly Von,
    DateOnly Bis,
    bool NurEigene,                        // Schülereinträge: nur die eigenen (Lehrkraft ohne Zuständigkeit)
    bool DarfOhneStunde,                   // Schülerdetail: Eintrag ohne Stunde anlegen (KL/SK/Admin)
    IReadOnlyList<KeZaehlerDto> Zaehler,
    IReadOnlyList<KeEintragDto> Eintraege,
    bool Gekuerzt,                         // mehr als angezeigt
    IReadOnlyList<KeEintragDto>? Weitere,  // Unterricht: Klasseneinträge der Klasse(n) aus anderen Stunden
    IReadOnlyList<string>? WeitereKlassen);
