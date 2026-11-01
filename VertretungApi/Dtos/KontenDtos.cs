namespace VertretungApi.Dtos;

// =====================================================================
//  Kontenverwaltung (Phase 2, Schritt 5) – Ein- und Ausgaben von /api/konten.
// =====================================================================

/// <summary>Ein Konto in Liste und Detailansicht (nie mit Passwort-Hash).</summary>
public record KontoDto(
    int Id,
    string Benutzername,
    string Rolle,                 // Admin | Lehrer | Klassenlehrer | Stufenkoordination | Schueler
    string RollenText,            // „Lehrkraft", „Klassenleitung", …
    string? LehrerKuerzel,
    string? Zustaendig,           // Klassenleitung/Stufenkoordination: „05A" bzw. „Q1"
    int? SchuelerId,
    string? Schueler,             // „Mia Müller"
    string? Klasse,               // Klasse bzw. Stufe des Schülers
    bool Verwaist,                // Schülerkonto ohne (noch vorhandenen) Schüler
    bool Gesperrt,
    bool MussPasswortAendern,
    DateTime? AngelegtAm,         // UTC
    DateTime? LetzteAnmeldung,    // UTC
    DateTime? PasswortGeaendertAm,// UTC
    bool Ich);                    // das eigene Konto

public record KontenListeDto(IReadOnlyList<KontoDto> Konten, int Gesamt, bool Mehr);

/// <summary>Was die angemeldete Person in der Kontenverwaltung darf.</summary>
public record KontenRechteDto(
    bool Verwalten,               // Admin: alles
    bool Zuruecksetzen,           // Passwörter zurücksetzen (Admin; Klassenleitung/Stufenkoordination für ihre Schüler)
    IReadOnlyList<string> Zustaendig);

public record NeuesKontoDto(
    string Rolle,
    string? Benutzername,         // leer: Kürzel (Lehrkräfte) bzw. vorname.nachname (Schüler)
    string? LehrerKuerzel,
    int? SchuelerId,
    string? Zustaendig);

public record KontoAendernDto(
    string Rolle,
    string? Benutzername,         // leer: bleibt wie bisher
    string? LehrerKuerzel,
    int? SchuelerId,
    string? Zustaendig);

/// <summary>Ergebnis von Anlegen/Zurücksetzen: das Startpasswort wird nur dieses eine Mal geliefert.</summary>
public record StartPasswortDto(KontoDto Konto, string StartPasswort);

public record ProtokollDto(int Id, DateTime Zeit, string Akteur, string Aktion, int? KontoId, string Konto, string? Details);

// ---- Sammelanlage ----

/// <summary>Art = "Lehrer" (alle Kürzel des gültigen Grundplans) oder "Schueler" (Klassen/Stufen).</summary>
public record SammelDto(
    string Art,
    IReadOnlyList<string>? Gruppen,   // Schüler: „05A", „Q1", …
    IReadOnlyList<string>? Nur,       // optional: nur diese Kürzel bzw. Schüler-Ids (aus der Vorschau abgewählt = fehlt)
    bool Anlegen);                    // false = nur Vorschau

public record SammelZeileDto(
    string Schluessel,            // Kürzel bzw. Schüler-Id (für „Nur")
    string Anzeige,               // Kürzel bzw. „Müller, Mia"
    string? Gruppe,               // Klasse/Stufe
    string Benutzername,          // vorgesehener bzw. vorhandener Benutzername
    string Status,                // "neu" | "vorhanden" | "angelegt" | "konflikt"
    string? Hinweis,
    string? StartPasswort);       // nur nach dem Anlegen

public record SammelErgebnisDto(
    string Art,
    IReadOnlyList<SammelZeileDto> Zeilen,
    int Neu, int Vorhanden, int Angelegt, int Konflikte,
    string? Grundplan);           // Lehrkräfte: aus welchem Grundplan

public record SammelGruppeDto(string Gruppe, string Art, int Schueler, int MitKonto);   // Art: Klasse | Stufe

public record SammelUebersichtDto(
    int Lehrkraefte, int LehrkraefteMitKonto, string? Grundplan,
    IReadOnlyList<SammelGruppeDto> Gruppen);
