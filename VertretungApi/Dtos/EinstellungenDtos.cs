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
    bool Loescht,
    string? StandardWert);      // PB-1a (319): Standard laut Katalog („true“/„false“, Zahl, Text; null = berechnet) – für
                                // den Haken eines Schalters ohne gespeicherten Wert

public sealed record EinstellungKalenderDto(string Schuljahr, DateOnly Start, DateOnly Ende);

public sealed record EinstellungenDto(
    List<EinstellungsGruppeDto> Gruppen,
    List<EinstellungDto> Einstellungen,
    EinstellungKalenderDto? Kalender,
    List<string> Hinweise,
    ZugriffInfoDto? Zugriff = null);     // P6a: nur zum Lesen (Einstellungsdatei)

/// <summary>P6a (229, 230): Schuladressen, Adressbeschränkung und Anfragebegrenzung aus der Einstellungsdatei.</summary>
public sealed record ZugriffInfoDto(
    List<string> Schuladressen,         // Netze, z. B. „203.0.113.10/32“
    bool DesktopNurSchule,
    bool AdminNurSchule,
    bool ProxyAktiv,
    bool Echtbetrieb,
    string DieseAdresse,                // Adresse dieser Anfrage, gekürzt
    bool? DieseAdresseSchule,           // null = keine Schuladressen eingetragen
    BegrenzungInfoDto Begrenzung);

public sealed record BegrenzungInfoDto(int JeKonto, int OhneAnmeldung, int Desktop, int TeuerJeKonto, int TeuerGesamt,
    int OhneAnmeldungMaxKB);

/// <summary>Was eine kürzere Frist löschen würde (Rückfrage, Entscheidung 141).</summary>
public sealed record EinstellungsLoeschungDto(string Schluessel, string Titel, int Anzahl, string Text);

public sealed record EinstellungsProtokollDto(
    int Id, DateTime Zeit, string Akteur, string Schluessel, string Titel, string? Alt, string? Neu, string? Details);

/// <summary>P6b (236): „Vorhandene Gründe entfernen…“ – Anzahl aus der Rückfrage (wird vor dem Entfernen neu gezählt).</summary>
public sealed record GruendeEntfernenDto(int Anzahl);
