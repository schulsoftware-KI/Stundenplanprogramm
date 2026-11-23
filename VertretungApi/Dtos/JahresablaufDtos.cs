namespace VertretungApi.Dtos;

// =====================================================================
//  P4a (5.3a, Entscheidung 205): Bereich „Jahresablauf“ in der App (nur Admin) – Karten mit Ampel.
//  Ampel: "gruen" (erledigt) · "gelb" (noch zu tun) · "rot" (überfällig) · "grau" (nur Hinweis bzw. noch nicht dran).
//  Ohne Namen: nur Zahlen, Daten, Bezeichnungen von Plänen und Gruppen.
// =====================================================================

/// <summary>Sprung in einen anderen Bereich der App (z. B. „nichtErfasst“, „schildImporte“, „gruppen“).</summary>
public sealed record JaZielDto(string Id, string Text);

/// <summary>Ein Prüfpunkt einer Karte.</summary>
public sealed record JaPunktDto(
    string Id,
    string Titel,
    string Ampel,
    string Text,
    IReadOnlyList<string>? Liste = null,     // z. B. Gruppen ohne Liste (Bezeichnungen, höchstens 60)
    int? Weitere = null,                     // so viele stehen nicht in der Liste
    IReadOnlyList<JaZielDto>? Ziele = null);

public sealed record JaKarteDto(
    string Id,
    string Titel,
    string Ampel,
    string Text,
    DateOnly? Stichtag,
    DateOnly? SichtbarAb,
    bool Aktiv,                              // false = noch nicht dran bzw. vorbei (nur der Text)
    IReadOnlyList<JaPunktDto> Punkte);

public sealed record JaUebersichtDto(
    DateOnly Heute,
    bool Testdatum,
    string Schuljahr,
    DateOnly? KalenderStart,
    DateOnly? KalenderEnde,
    IReadOnlyList<JaKarteDto> Karten);
