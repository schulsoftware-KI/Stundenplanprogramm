namespace VertretungApi.Dtos;

// =====================================================================
//  Wochenansicht (Phase 2, Schritt 2): Antwort der /api/woche/...-Endpunkte.
//  Enthält bewusst KEINE Gründe/Bemerkungen aus dem Vertretungsplan – nur,
//  was an der Stunde geändert ist (Status + kurzer Info-Text).
// =====================================================================

/// <summary>Plan eines Ziels (Lehrkraft, Klasse, Lerngruppe, Schüler) über eine oder mehrere Wochen.</summary>
public record WochenplanDto(
    string Ziel,                          // lehrer | klasse | gruppe | schueler
    string Wert,                          // z. B. "L1", "05A", "BIG1_EF_L68"
    DateOnly Von,                         // Montag der ersten Woche
    DateOnly Bis,                         // Sonntag der letzten Woche
    IReadOnlyList<WochenTagDto> Tage);

/// <summary>Ein Kalendertag der Woche.</summary>
public record WochenTagDto(
    DateOnly Datum,
    string WTag,                          // "Mo" … "Fr"
    string Status,                        // Unterricht | Frei | AusserhalbSchuljahr | KeinGrundplan
    string? Hinweis,                      // z. B. "Herbstferien"
    int? GrundplanVersionId,
    string? Grundplan,                    // z. B. "oT_1 (gültig ab 28.10.2026)"
    bool HatVertretungsplan,              // für diesen Tag wurde ein Vertretungsplan veröffentlicht
    IReadOnlyList<WochenStundeDto> Stunden);

/// <summary>Eine (effektive) Stunde eines Tages.</summary>
public record WochenStundeDto(
    int Stunde,
    int UNr,
    string Fach,
    string Lehrer,                        // effektiv (bei Vertretung: die vertretende Lehrkraft)
    string? OriginalLehrer,               // nur gesetzt, wenn abweichend
    string? Raum,                         // effektiv (bei Raumänderung: der neue Raum)
    string? OriginalRaum,                 // nur gesetzt, wenn der Raum geändert wurde
    IReadOnlyList<string> Lerngruppen,    // Klassen-/Kurs-/Gruppenschlüssel
    IReadOnlyList<string> Klassen,        // Rohwerte „Klasse(n)" (bei Kursen EF/Q1/Q2)
    string Art,                           // Unterricht | VBer | UV | Pseudo (PSF…)
    string Status,                        // Normal | Vertreten | Entfall | VerlegtWeg | VerlegtHin |
                                          // Betreuung | EVA | Zusammenlegung | Raumvertretung | Frei
    string? Info,
    bool Geaendert);

// =====================================================================
//  Auswahllisten für die PWA (Phase 2, Schritt 3): was man ansehen kann.
// =====================================================================

/// <summary>Alle Ziele der Grundplan-Version, die am angefragten Datum gilt.</summary>
public record AuswahlDto(
    string? Grundplan,                    // z. B. "oT_1 (gültig ab 28.10.2026)"; null = noch kein Grundplan
    IReadOnlyList<string> Lehrer,         // Kürzel
    IReadOnlyList<string> Klassen,        // z. B. "05A"
    IReadOnlyList<string> Stufen,         // Oberstufe als Ganzes: "EF", "Q1", "Q2"
    IReadOnlyList<AuswahlGruppeDto> Kurse,    // Oberstufenkurse (Schlüssel = Schülergruppe)
    IReadOnlyList<AuswahlGruppeDto> Gruppen); // sonstige Gruppen: AGs, Förder-/Differenzierungsgruppen

/// <summary>Ein Kurs bzw. eine Gruppe mit lesbarer Beschreibung.</summary>
public record AuswahlGruppeDto(
    string Schluessel,                    // z. B. "BIG1_EF_L68" – für /api/woche/gruppe/{schluessel}
    string Stufe,                         // Rohwert „Klasse(n)", z. B. "EF" oder "AG2-6"
    string Fach,                          // z. B. "BI G1"
    string Lehrer);                       // z. B. "L68, L57"
