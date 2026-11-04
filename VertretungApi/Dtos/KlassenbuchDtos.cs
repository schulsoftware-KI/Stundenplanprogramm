namespace VertretungApi.Dtos;

// =====================================================================
//  Klassenbuch (Phase 3.1): Stundenblatt mit Anwesenheit, Änderungshistorie
//  und Gruppenlisten (Sek-I-Teilgruppen laut Schülergruppe der UV).
//  Status der Anwesenheit: Anwesend | Fehlt | Verspaetet | Beurlaubt | Schulveranstaltung.
//  Phase 3.3: Thema und Hausaufgabe im Stundenblatt, Sichtbarkeit je Unterricht,
//  Themenliste, Hausaufgaben für Schüler.
// =====================================================================

/// <summary>Stundenblatt einer Lektion: Kopf, Rechte, Erfassungsstand und Teilnehmerliste.</summary>
public record KbStundeDto(
    DateOnly Datum,
    int Stunde,
    int GrundUNr,                          // } Schlüssel der Lektion
    string GrundLehrer,                    // }
    int UNr,
    string Fach,
    string Lehrer,                         // haltende Lehrkraft
    string? OriginalLehrer,                // nur gesetzt, wenn abweichend (Vertretung, Verlegung)
    string? Raum,
    string Status,                         // wie im Wochenplan (Normal, Vertreten, VerlegtHin, …)
    string? Info,
    IReadOnlyList<string> Klassen,
    IReadOnlyList<string> Lerngruppen,
    bool DarfSchreiben,
    string? Sperrgrund,                    // warum nicht geschrieben werden kann (Zukunft, Gruppe offen)
    int Version,                           // beim Speichern mitschicken (Konflikterkennung)
    string? ErfasstVon,
    DateTime? ErfasstAm,
    string? GeaendertVon,
    DateTime? GeaendertAm,
    IReadOnlyList<KbTeilnehmerDto> Teilnehmer,
    IReadOnlyList<KbOffeneGruppeDto> OffeneGruppen,
    IReadOnlyList<string> Hinweise,
    KbFolgeDto? Folge,                     // Doppelstunde: direkt folgende Stunde (nur wenn sie mit übernommen werden darf)
    KbInhaltDto Inhalt,                    // Phase 3.3: Thema und Hausaufgabe
    string? Meldung);                      // nur in der Antwort auf das Speichern, z. B. „… für die 3. und 4. Stunde gespeichert“

/// <summary>
/// Doppelstunde (Nachtrag 3.1): die direkt folgende Stunde desselben Unterrichts (gleiche UNr und
/// Lehrkraft laut Grundplan). Ist sie noch nicht erfasst, kann die Erfassung beim Speichern
/// mit übernommen werden; hat sie noch kein Thema, auch das Thema (Phase 3.3).
/// </summary>
public record KbFolgeDto(
    int Stunde,
    bool Erfasst,
    string? ErfasstVon,
    DateTime? ErfasstAm,
    bool HatThema);

/// <summary>
/// Thema und Hausaufgabe einer Stunde (Phase 3.3). Schreiben: haltende und ursprüngliche
/// Lehrkraft sowie Admin; Klassenleitung/Stufenkoordination lesen nur.
/// </summary>
public record KbInhaltDto(
    string? Thema,
    string? Hausaufgabe,
    DateOnly? Faellig,
    bool DarfSchreiben,
    string? Sperrgrund,                    // warum nicht (Zukunft, nur lesen)
    KbVorigeDto? Vorige,                   // vorige Stunde desselben Unterrichts mit Thema/Hausaufgabe
    DateOnly? NaechsteStunde,              // Vorschlag „fällig am“: nächste Stunde des Unterrichts laut Plan
    int UNr,                               // } Unterricht (UNr + Lehrkraft laut Plan) – für Sichtbarkeit
    string Lehrer,                         // }   und Themenliste
    bool HausaufgabenSichtbar,             // Schüler sehen die Hausaufgaben dieses Unterrichts
    bool DarfSichtbarkeit);                // Lehrkraft laut Plan oder Admin

/// <summary>Vorige Stunde desselben Unterrichts (Hinweis im Stundenblatt).</summary>
public record KbVorigeDto(DateOnly Datum, int Stunde, string? Thema, string? Hausaufgabe, DateOnly? Faellig);

/// <summary>Eine Schülerin / ein Schüler im Stundenblatt.</summary>
public record KbTeilnehmerDto(
    int Id,
    string Vorname,
    string Nachname,
    string? Klasse,                        // Klasse (Sek I) bzw. Stufe (Sek II)
    string Status,
    int? Minuten,                          // nur bei Verspaetet
    bool NichtInListe,                     // hat einen Eintrag, gehört aber nicht mehr zur Liste
    string? Bewertung,                     // Phase 3.2: Offen | Entschuldigt | Unentschuldigt (null = keine Fehlzeit)
    bool Festgelegt,                       // von KL/SK/Admin einzeln bewertet
    string? FestgelegtVon,
    bool Gesperrt);                        // festgelegt und die angemeldete Person darf die Zeile nicht ändern

/// <summary>Sek-I-Gruppe der Stunde, die noch nicht zusammengestellt ist.</summary>
public record KbOffeneGruppeDto(
    string Schluessel,
    string Fach,
    IReadOnlyList<string> Klassen,
    IReadOnlyList<string> Lehrer,
    bool DarfZusammenstellen);

/// <summary>
/// Stundenblatt speichern: alle Abweichungen der Lektion (wer fehlt, ist nicht anwesend) und –
/// Phase 3.3 – Thema/Hausaufgabe. Eine gemeinsame Version für beides.
/// </summary>
public record KbSpeichernDto(
    DateOnly Datum,
    int Stunde,
    int GrundUNr,
    string GrundLehrer,
    int Version,                           // Version des geladenen Stundenblatts (neu: 0)
    List<KbEintragDto>? Eintraege,
    bool FolgestundeUebernehmen = false,   // Doppelstunde: auch die folgende Stunde erfassen
    bool AnwesenheitErfassen = true,       // false: nur Thema/Hausaufgabe (Anwesenheit bleibt unverändert)
    KbInhaltSpeichernDto? Inhalt = null);  // null: Thema/Hausaufgabe bleiben unverändert

public record KbEintragDto(int SchuelerId, string Status, int? Minuten);

/// <summary>Thema und Hausaufgabe (leer bzw. null = löschen). Fällig nur mit Hausaufgabe.</summary>
public record KbInhaltSpeichernDto(string? Thema, string? Hausaufgabe, DateOnly? Faellig);

/// <summary>Sichtbarkeit der Hausaufgaben eines Unterrichts für Schüler (Phase 3.3, Entscheidung 30).</summary>
public record KbSichtbarkeitDto(int UNr, string Lehrer, bool Sichtbar, DateOnly? Datum);

/// <summary>Themenliste eines Unterrichts (Phase 3.3).</summary>
public record KbThemenDto(
    string Titel,
    int UNr,
    string Lehrer,
    DateOnly Von,
    DateOnly Bis,
    bool HausaufgabenSichtbar,
    bool DarfSichtbarkeit,
    IReadOnlyList<KbThemaZeileDto> Stunden);

public record KbThemaZeileDto(
    DateOnly Datum,
    int Stunde,
    int GrundUNr,
    string GrundLehrer,
    string Lehrer,                         // haltende Lehrkraft
    string Status,
    IReadOnlyList<string> Klassen,
    bool Erfasst,                          // Anwesenheit erfasst
    string? Thema,
    string? Hausaufgabe,
    DateOnly? Faellig);

/// <summary>„Was ist auf?" – Hausaufgaben eines Schülers (Phase 3.3).</summary>
public record HaListeDto(DateOnly Heute, DateOnly Von, IReadOnlyList<HaEintragDto> Aufgaben);

public record HaEintragDto(
    string Schluessel,                     // Lektion (Datum|Stunde|UNr|Lehrkraft) – zum Abhaken auf dem Gerät
    DateOnly Datum,
    int Stunde,
    string Fach,
    string Lehrer,
    string? Thema,
    string Hausaufgabe,
    DateOnly Faellig);

/// <summary>Ein Eintrag der Änderungshistorie einer Lektion.</summary>
public record KbHistorieDto(
    DateTime Zeit,
    string Akteur,
    string Art,                            // Erfasst | Anwesenheit | Bewertung | Thema | Hausaufgabe
                                           // | Eintrag | EintragGeloescht (Phase 3.4)
    string? Schueler,
    string? Alt,
    string? Neu);

/// <summary>Gruppe in der Übersicht „Meine Gruppen".</summary>
public record KbGruppeKurzDto(
    string Schluessel,
    string Fach,
    IReadOnlyList<string> Klassen,
    IReadOnlyList<string> Lehrer,
    bool Zusammengestellt,
    int Anzahl,
    string? BearbeitetVon,
    DateTime? BearbeitetAm);

/// <summary>Gruppe zum Zusammenstellen: Pool = alle Schüler der genannten Klassen.</summary>
public record KbGruppeDto(
    string Schluessel,
    string Fach,
    string Schuljahr,
    IReadOnlyList<string> Klassen,
    IReadOnlyList<string> Lehrer,
    bool Zusammengestellt,
    string? BearbeitetVon,
    DateTime? BearbeitetAm,
    bool DarfBearbeiten,
    IReadOnlyList<KbPoolDto> Pool);

public record KbPoolDto(int Id, string Vorname, string Nachname, string Klasse, bool Mitglied);

public record KbGruppeSpeichernDto(List<int>? Mitglieder);
