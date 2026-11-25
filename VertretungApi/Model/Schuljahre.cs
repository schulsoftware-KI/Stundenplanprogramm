namespace VertretungApi.Model;

// =====================================================================
//  P5 – 5.3b Schuljahreswechsel (Entscheidungen 214–223, 239–242; Migration „Schuljahre“ in P5a).
//
//  Je Schuljahr eine Zeile – sie entsteht beim ersten Jahresabschluss bzw. beim Vorbereiten des neuen Schuljahres.
//  Ohne Zeile gilt ein Schuljahr als laufend. Die Spalten für Archiv-Export, Aufbewahrung und Löschen (P5c, 222/223)
//  kommen schon mit dieser Migration – P5b und P5c brauchen keine eigene (239).
//  Daten der Schüler stehen hier nie – nur Zeitpunkte, Benutzernamen, Begründungen, Prüfsumme und Größe.
// =====================================================================

/// <summary>Status eines Schuljahres (Text in der Spalte „Status“).</summary>
public static class SchuljahrStatus
{
    public const string Laufend = "laufend";
    public const string Abgeschlossen = "abgeschlossen";
}

/// <summary>Ein Schuljahr mit Abschluss, Archiv und Löschung (Tabelle „Schuljahre“).</summary>
public class SchuljahrDb
{
    public int Id { get; set; }
    public string Schuljahr { get; set; } = "";             // „2026/27“ (eindeutig)
    public string Status { get; set; } = SchuljahrStatus.Laufend;

    // P5a (215): Jahresabschluss und Wiedereröffnen (zuletzt; der Verlauf steht im Protokoll der Einstellungen)
    public DateTime? AbgeschlossenAm { get; set; }          // UTC
    public string? AbgeschlossenVon { get; set; }
    public DateTime? GeoeffnetAm { get; set; }              // UTC – zuletzt wieder geöffnet
    public string? GeoeffnetVon { get; set; }
    public string? GeoeffnetGrund { get; set; }             // Begründung (Pflicht)

    // P5a (217): „Neues Schuljahr vorbereiten“ – an der Zeile des neuen Jahres. An der Zeile des bisherigen Jahres bleiben
    // dessen Daten „Anwesenheit/Thema erfasst ab“ und „2. Halbjahr ab“ stehen (für den späteren Jahresabschluss).
    public DateTime? VorbereitetAm { get; set; }            // UTC
    public string? VorbereitetVon { get; set; }
    public DateOnly? ErfassungAb { get; set; }
    public DateOnly? ThemaAb { get; set; }
    public DateOnly? HalbjahrAb { get; set; }

    // P5b (219): Zuständigkeiten für dieses Schuljahr bestätigt (Vorschläge per Häkchen) – für die Ampel der Karte
    public DateTime? ZustaendigkeitenAm { get; set; }       // UTC
    public string? ZustaendigkeitenVon { get; set; }

    // P5c (222): Archiv-Export (die Datei selbst speichert der Server nie)
    public DateTime? ArchivAm { get; set; }                 // UTC
    public string? ArchivVon { get; set; }
    public string? ArchivPruefsumme { get; set; }           // SHA-256 der Zip-Datei
    public long? ArchivGroesse { get; set; }                // Bytes
    public bool? ArchivMitSchuelerEintraegen { get; set; }
    public DateTime? AufbewahrtAm { get; set; }             // UTC – „Archiv-Export geprüft und aufbewahrt“
    public string? AufbewahrtVon { get; set; }
    public string? AufbewahrtOrt { get; set; }              // wo der Export liegt (Freitext, nie das Kennwort)
    public DateOnly? AufbewahrenBis { get; set; }

    // P5c (223): Löschen des ganzen Schuljahres (Zahlen ohne Namen) – seit P5c-2 (Services/JahresablaufLoeschen.cs). Die
    // Zeile bleibt danach mit Status „abgeschlossen“: kein Wiederöffnen, keine Pushes für das Jahr.
    public DateTime? GeloeschtAm { get; set; }              // UTC
    public string? GeloeschtVon { get; set; }
    public string? GeloeschtText { get; set; }
}
