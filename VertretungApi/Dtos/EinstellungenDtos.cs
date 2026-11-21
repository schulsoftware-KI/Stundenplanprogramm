namespace VertretungApi.Dtos;

// =====================================================================
//  P3 – E-1 Schuleinstellungen (Controllers/EinstellungenController.cs)
// =====================================================================

/// <summary>
/// Eine Änderung. Wert "" = Standard. AbgestimmtAm/AbgestimmtMit nur bei Einstellungen mit Datenschutzwirkung:
/// null = nicht übermittelt (bleibt – außer der Wert ändert sich, dann entfällt die Abstimmung), "" = leeren.
/// </summary>
public sealed record EinstellungsAenderungDto(string? Schluessel, string? Wert, string? AbgestimmtAm = null, string? AbgestimmtMit = null);

/// <summary>Bestaetigt = true: auch speichern, wenn eine kürzere Frist Datensätze löscht (vorher 409 mit Zahlen).</summary>
public sealed record EinstellungenSpeichernDto(List<EinstellungsAenderungDto>? Aenderungen, bool Bestaetigt = false);

public sealed record EinstellungsGruppeDto(string Id, string Titel, string Text);

public sealed record EinstellungDto(
    string Schluessel,
    string Gruppe,
    string Titel,
    string Typ,                 // "text" | "schuljahr" | "datum" | "zahl" | "schalter"
    string? Wert,               // gespeicherter Wert (Datum JJJJ-MM-TT); null = Standard
    string Standard,            // Text des Standards
    string Wirksam,             // was gerade gilt (lesbar)
    string Hilfe,
    string? DsbFrage,           // gesetzt = Datenschutzwirkung (141)
    DateOnly? AbgestimmtAm,
    string? AbgestimmtMit,
    DateTime? GeaendertAm,
    string? GeaendertVon,
    int? Min,
    int? Max,
    int? MaxLaenge,
    string? Einheit,
    bool Loescht);

public sealed record EinstellungKalenderDto(string Schuljahr, DateOnly Start, DateOnly Ende);

public sealed record EinstellungenDto(
    List<EinstellungsGruppeDto> Gruppen,
    List<EinstellungDto> Einstellungen,
    EinstellungKalenderDto? Kalender,
    List<string> Hinweise);

/// <summary>Was eine kürzere Frist löschen würde (Rückfrage, Entscheidung 141).</summary>
public sealed record EinstellungsLoeschungDto(string Schluessel, string Titel, int Anzahl, string Text);

public sealed record EinstellungsProtokollDto(
    int Id, DateTime Zeit, string Akteur, string Schluessel, string Titel, string? Alt, string? Neu, string? Details);
