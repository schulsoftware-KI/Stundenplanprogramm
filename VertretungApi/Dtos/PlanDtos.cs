namespace VertretungApi.Dtos;

/// <summary>Was der Desktop pro Tag hochlädt (ersetzt den Tag komplett).</summary>
public record PlanPushDto(
    string Schuljahr,
    DateOnly Datum,
    List<PlanEintragDto> Eintraege);

/// <summary>Voller Eintrag (Admin-/Lehrer-/Aushang-Sicht, mit Grund/Bemerkung).</summary>
public record PlanEintragDto(
    int Stunde,
    string Klassen,
    string Fach,
    string? Lehrer,
    string? OriginalLehrer,
    string? Raum,
    string Art,
    string Status,
    string? Grund,
    string? Bemerkung,
    bool? Fachgerecht,
    bool IstAenderung,
    int? UNr = null,          // Phase 2, Schritt 2: UNr der gehaltenen Stunde
    int? GrundUNr = null);    // UNr der ersetzten Grundplan-Stunde

/// <summary>Öffentliche Sicht (Schüler): OHNE Grund/Bemerkung/Fachgerecht.</summary>
public record PlanEintragOeffentlichDto(
    int Stunde,
    string Klassen,
    string Fach,
    string? Lehrer,
    string? OriginalLehrer,
    string? Raum,
    string Art,
    string Status,
    bool IstAenderung);
