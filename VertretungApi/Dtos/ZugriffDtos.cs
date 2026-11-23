namespace VertretungApi.Dtos;

// =====================================================================
//  P6a (Entscheidung 228): Reiter „Zugriffe“ der Kontenverwaltung – Teil „Anmeldungen“ (Anmeldeprotokoll).
//  P6b (Entscheidungen 232, 233): Teil „Ansichten“ (Leseprotokoll).
// =====================================================================

public sealed record AnmeldungEintragDto(
    int Id,
    DateTime Zeit,          // UTC
    int? KontoId,
    string Konto,           // "" = unbekanntes Konto (der eingegebene Name wird nie gespeichert)
    string? Rolle,
    string? RollenText,
    string Ergebnis,
    bool Erfolgreich,
    string? Adresse,        // gekürzt
    bool? Schulnetz,        // null = keine Schuladressen eingetragen
    string? Geraet);

public sealed record AnmeldungenDto(
    List<AnmeldungEintragDto> Eintraege,
    int Gesamt,             // passend zum Filter
    int Erfolglos24h,       // letzte 24 Stunden, ohne Filter
    int Konten24h,          // davon verschiedene Konten (unbekannte zählen zusammen als eins)
    bool Schuladressen,     // sind Schuladressen eingetragen? (sonst ist „Schulnetz“ leer)
    List<string> Ergebnisse);

/// <summary>P6b (233): ein Eintrag des Leseprotokolls – je Tag, Konto, Ansicht und Gegenstand.</summary>
public sealed record AnsichtEintragDto(
    int Id,
    DateOnly Tag,
    DateTime ErsteZeit,     // UTC
    DateTime LetzteZeit,    // UTC
    int Anzahl,
    int? KontoId,
    string Akteur,
    string Rolle,
    string RollenText,
    string Ansicht,
    string AnsichtText,
    bool Liste,             // Listenansicht (Klasse, Unterricht, Stundenblatt …)
    string Gegenstand,      // ohne Namen
    int? SchuelerId,
    string? Schueler,       // Name für den Admin (nicht gespeichert, beim Lesen ergänzt)
    bool IstPseudo);

public sealed record AnsichtArtDto(string Id, string Text);

public sealed record AnsichtenDto(
    List<AnsichtEintragDto> Eintraege,
    int Gesamt,             // Einträge passend zum Filter
    int Aufrufe,            // Summe der Anzahl passend zum Filter
    bool An,                // Schalter „Leseprotokoll“ (Einstellungen → Datenschutz)
    string? Schueler,       // Name des gefilterten Schülers
    List<AnsichtArtDto> Ansichten);
