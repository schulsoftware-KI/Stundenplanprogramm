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
    bool Ich,                     // das eigene Konto
    // ---- Phase 5.2c-2 (Entscheidungen 194, 197) ----
    string? Lehrkraft = null,     // Name laut Lehrkraefte.dat („Anna Schulz“) – nur für den Admin
    bool NameGeaendert = false,   // Schülerkonto: Benutzername „vorname.nachname“ passt nicht mehr zum Namen laut SchILD
    string? BenutzernameVorschlag = null, // nur in der Einzelansicht (Admin): freier Name nach dem neuen Namen
    bool WiederAktiv = false,     // Schülerkonto gesperrt, laut SchILD aber (wieder) aktiv – z. B. Wiedereintritt
    string? GesperrtDurch = null, // Einzelansicht (Admin): wer zuletzt gesperrt hat, mit Grund und Datum
    // ---- P6a (Entscheidungen 225–227) ----
    bool ZweiterFaktor = false,   // zweiter Faktor eingerichtet
    DateTime? ZweiterFaktorSeit = null, // UTC
    bool FaktorFehlt = false,     // Pflicht für die Rolle, aber noch kein zweiter Faktor
    // ---- P5a (Entscheidung 219) ----
    string? ZustaendigSchuljahr = null,  // Schuljahr der Zuständigkeit (z. B. „2026/27“)
    bool ZustaendigAnderesJahr = false,  // gilt nicht für das aktuelle Schuljahr (nur noch für Daten jenes Jahres)
    string? AktuellesSchuljahr = null);  // „Aktuelles Schuljahr“ (für den Haken beim Ändern)

public record KontenListeDto(IReadOnlyList<KontoDto> Konten, int Gesamt, bool Mehr);

/// <summary>Was die angemeldete Person in der Kontenverwaltung darf.</summary>
public record KontenRechteDto(
    bool Verwalten,               // Admin: alles
    bool Zuruecksetzen,           // Passwörter zurücksetzen (Admin; Klassenleitung/Stufenkoordination für ihre Schüler)
    bool Sperren,                 // sperren/entsperren (ebenso)
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
    string? Zustaendig,
    bool ZustaendigAktuellesSchuljahr = false);   // P5a (219): unveränderte Zuständigkeit für das aktuelle Schuljahr übernehmen

/// <summary>Ergebnis von Anlegen/Zurücksetzen: das Startpasswort wird nur dieses eine Mal geliefert.</summary>
public record StartPasswortDto(KontoDto Konto, string StartPasswort);

public record ProtokollDto(int Id, DateTime Zeit, string Akteur, string Aktion, int? KontoId, string Konto, string? Details);

// ---- Sammelanlage ----

/// <summary>Art = "Lehrer" (alle Kürzel des gültigen Grundplans) oder "Schueler" (Klassen/Stufen).</summary>
public record SammelDto(
    string Art,
    IReadOnlyList<string>? Gruppen,   // Schüler: „05A", „Q1", …
    IReadOnlyList<string>? Nur,       // optional: nur diese Kürzel bzw. Schüler-Ids (aus der Vorschau abgewählt = fehlt)
    bool Anlegen,                     // false = nur Vorschau
    bool KlassenleitungEintragen = false); // 5.2c-2 (192): Lehrkräfte – Klassenleitung laut SchILD gleich als Rolle eintragen

public record SammelZeileDto(
    string Schluessel,            // Kürzel bzw. Schüler-Id (für „Nur")
    string Anzeige,               // Kürzel bzw. „Müller, Mia"
    string? Gruppe,               // Klasse/Stufe
    string Benutzername,          // vorgesehener bzw. vorhandener Benutzername
    string Status,                // "neu" | "vorhanden" | "angelegt" | "konflikt"
    string? Hinweis,
    string? StartPasswort,        // nur nach dem Anlegen
    string? Name = null);         // 5.2c-2 (194): Lehrkräfte – Name laut Lehrkraefte.dat

public record SammelErgebnisDto(
    string Art,
    IReadOnlyList<SammelZeileDto> Zeilen,
    int Neu, int Vorhanden, int Angelegt, int Konflikte,
    string? Grundplan);           // Lehrkräfte: aus welchem Grundplan

public record SammelGruppeDto(string Gruppe, string Art, int Schueler, int MitKonto);   // Art: Klasse | Stufe

public record SammelUebersichtDto(
    int Lehrkraefte, int LehrkraefteMitKonto, string? Grundplan,
    IReadOnlyList<SammelGruppeDto> Gruppen,
    int Klassenleitungen = 0);        // 5.2c-2: Klassen mit Klassenleitung laut SchILD (Häkchen in der Sammelanlage)

// ---- Phase 5.2c-2 (Entscheidung 192): Vorschläge aus SchILD (Klassenleitung laut Klassen.dat) ----

/// <summary>Ein Vorschlag. Schluessel „u|Konto-Id|Klasse“ (übernehmen) bzw. „e|Konto-Id|Klasse“ (entfernen); leer = nur Hinweis.</summary>
public record SchildVorschlagDto(
    string Schluessel,
    string Art,                       // uebernehmen | entfernen | hinweis | keinKonto
    int? KontoId,
    string? Konto,                    // Benutzername
    string Kuerzel,
    string Klasse,
    string Text,
    string? Aenderung,                // „Rolle Lehrkraft → Klassenleitung, zuständig 05A“
    bool Gesperrt);

public record SchildVorschlaegeDto(
    string? Schuljahr,
    int Klassen,                      // Klassen im Schuljahr
    int KlassenMitLeitung,            // davon mit Klassenleitung laut SchILD
    IReadOnlyList<SchildVorschlagDto> Vorschlaege);

public record SchildVorschlaegeAnwendenDto(IReadOnlyList<string>? Schluessel);

public record SchildVorschlaegeErgebnisDto(int Angewendet, int NichtMehrAktuell, IReadOnlyList<string> Konten);
