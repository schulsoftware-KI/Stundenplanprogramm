namespace VertretungApi.Dtos;

/// <summary>Was der Desktop als Grundplan hochlädt (Version + Kalender + alle Slots).</summary>
public record GrundplanPushDto(
    string Schuljahr,
    DateOnly GueltigAb,
    string? Bezeichnung,
    KalenderPushDto? Kalender,
    List<GrundplanSlotDto>? Slots);

public record KalenderPushDto(
    DateOnly Start,
    DateOnly Ende,
    List<string>? RasterTage,
    List<FreieZeitPushDto>? FreieZeiten);

public record FreieZeitPushDto(
    string Typ,
    DateOnly Von,
    DateOnly Bis,
    string? Bezeichnung,
    string? Klassen);

public record GrundplanSlotDto(
    string WTag,
    int WTagIndex,
    int Stunde,
    int UNr,
    string Lehrer,
    string Fach,
    string? Fachraum,
    string? Stammraum,
    string LerngruppeTyp,
    string LerngruppeKey,
    string? Klasse,
    string? Schuelergruppe,
    string Art);
