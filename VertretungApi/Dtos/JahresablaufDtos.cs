namespace VertretungApi.Dtos;

// =====================================================================
//  P4a (5.3a, Entscheidung 205): Bereich „Jahresablauf“ in der App (nur Admin) – Karten mit Ampel.
//  Ampel: "gruen" (erledigt) · "gelb" (noch zu tun) · "rot" (überfällig) · "grau" (nur Hinweis bzw. noch nicht dran).
//  Ohne Namen: nur Zahlen, Daten, Bezeichnungen von Plänen und Gruppen.
// =====================================================================

/// <summary>Sprung in einen anderen Bereich der App (z. B. „nichtErfasst“, „schildImporte“, „gruppen“).</summary>
public sealed record JaZielDto(string Id, string Text);

/// <summary>
/// P5a: Knopf für eine Aktion mit Rückfrage („abschliessen“, „oeffnen“, „vorbereiten“) für ein Schuljahr. Gefahr = roter Knopf.
/// </summary>
public sealed record JaAktionDto(string Id, string Text, string Schuljahr, bool Gefahr = false);

/// <summary>Ein Prüfpunkt einer Karte.</summary>
public sealed record JaPunktDto(
    string Id,
    string Titel,
    string Ampel,
    string Text,
    IReadOnlyList<string>? Liste = null,     // z. B. Gruppen ohne Liste (Bezeichnungen, höchstens 60)
    int? Weitere = null,                     // so viele stehen nicht in der Liste
    IReadOnlyList<JaZielDto>? Ziele = null,
    IReadOnlyList<JaAktionDto>? Aktionen = null);   // P5a

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

// ---- P5a (215, 217): Aktionen der Karte „Schuljahreswechsel“ ----

/// <summary>„Schuljahr abschließen“: ohne Bestätigung → 409 mit Vorprüfung und Folgen.</summary>
public sealed record JaAbschliessenDto(string? Schuljahr, bool Bestaetigt = false);

/// <summary>„Wieder öffnen“ mit Begründung (Pflicht).</summary>
public sealed record JaOeffnenDto(string? Schuljahr, string? Begruendung);

/// <summary>„Neues Schuljahr vorbereiten“ (Schuljahr = das neue): ohne Bestätigung → 409 mit den geplanten Änderungen.</summary>
public sealed record JaVorbereitenDto(string? Schuljahr, bool Bestaetigt = false);

/// <summary>Rückfrage vor einer Aktion (409): was geprüft wurde, was sich ändert, was danach gilt.</summary>
public sealed record JaRueckfrageDto(
    bool Bestaetigen,
    string Titel,
    string Fehler,                           // kurzer Satz (wie andere 409-Antworten)
    IReadOnlyList<string> Warnungen,         // Vorprüfung (nur Hinweise, keine Sperre)
    IReadOnlyList<string> Aenderungen,       // was die Aktion ändert
    string Knopf);                           // Text des Bestätigungsknopfs

public sealed record JaAktionErgebnisDto(string Meldung, JaUebersichtDto Stand);

// ---- P5b-2 (219, 240, 249–251): Zuständigkeiten ins neue Schuljahr ----

/// <summary>
/// Ein Vorschlag je Konto. Art: "schild" (Klassenleitung laut Klassen.dat des neuen Jahres) · "folge" (Folgeklasse bzw.
/// Folgestufe aus der Mehrheit der bisherigen Schüler) · "entfaellt" (keine Folgeklasse – angehakt: Rolle Lehrkraft) ·
/// "hinweis" (kein Häkchen, z. B. die Folgeklasse leitet laut SchILD jemand anderes). Schlüssel leer = kein Häkchen.
/// Keine Namen von Schülern – nur Klassen, Stufen und Zahlen.
/// </summary>
public sealed record JaZustaendigkeitDto(
    string Schluessel,
    string Art,
    int KontoId,
    string Konto,                            // Benutzername
    string? Kuerzel,
    string Rolle,                            // „Klassenleitung“ bzw. „Stufenkoordination“
    string Bisher,                           // „05A“ (im Vorjahr)
    string? Neu,                             // „06A“ bzw. null (entfällt, Hinweis)
    string Text,                             // Begründung je Eintrag („05A → 06A – Folgeklasse (27 von 28 Schülern)“)
    string Aenderung,                        // was „Übernehmen“ am Konto ändert
    bool Vorausgewaehlt,
    bool Gesperrt);

public sealed record JaZustaendigkeitenDto(
    string Vorjahr,
    string Schuljahr,                        // das neue (= „Aktuelles Schuljahr“)
    bool Moeglich,
    string? Grund,                           // warum (noch) nicht möglich
    int KlassenNeu,                          // Klassen des neuen Jahres
    int KlassenMitLeitung,                   // davon mit Klassenleitung laut SchILD (Klassen.dat)
    IReadOnlyList<JaZustaendigkeitDto> Zeilen,
    DateTime? UebernommenAm,                 // UTC – zuletzt übernommen (Zeile in „Schuljahre“)
    string? UebernommenVon);

/// <summary>„Übernehmen“: die angehakten Schlüssel (aus GET) für das neue Schuljahr.</summary>
public sealed record JaZustaendigkeitenUebernehmenDto(string? Schuljahr, IReadOnlyList<string>? Schluessel);
