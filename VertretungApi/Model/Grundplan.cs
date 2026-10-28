namespace VertretungApi.Model;

// =====================================================================
//  Grundplan (Phase 2, Schritt 1): das WOCHENRASTER des Desktop-Programms
//  als versionierte Momentaufnahme + der Schuljahreskalender.
//
//  Versionierung: Eine GrundplanVersion gilt AB einem Datum (GueltigAb).
//  Für ein beliebiges Datum ist die Version mit dem größten GueltigAb
//  <= Datum maßgeblich (Planwechsel im Schuljahr bleiben so nachvollziehbar).
//  Wird mit demselben (Schuljahr, GueltigAb) erneut gepusht, ersetzt das
//  die bestehende Version; ein anderes GueltigAb legt eine neue an.
//
//  Anbindung an Lerngruppen: Jeder Slot trägt einen LerngruppeKey (Text)
//  statt eines Fremdschlüssels. Das entkoppelt Pseudodaten und spätere
//  SchILD-Daten; die Auflösung auf Lerngruppe.Id erfolgt beim Lesen
//  (Phase 2, Schritt 2/3).
// =====================================================================

public class GrundplanVersion
{
    public int Id { get; set; }
    public string Schuljahr { get; set; } = "";
    public DateOnly GueltigAb { get; set; }
    public string Bezeichnung { get; set; } = "";     // Plan-Spalte im Desktop, z. B. "A10"
    public DateTimeOffset Erstellt { get; set; } = DateTimeOffset.UtcNow;

    public List<GrundplanSlot> Slots { get; set; } = new();
}

/// <summary>
/// Ein Eintrag des Wochenrasters: (Wochentag, Stunde) × Unterrichtsteil × Lerngruppe.
/// Gekoppelte Kurse / Team-Teaching ergeben mehrere Zeilen mit gleicher UNr.
/// </summary>
public class GrundplanSlot
{
    public int Id { get; set; }
    public int VersionId { get; set; }
    public GrundplanVersion? Version { get; set; }

    public string WTag { get; set; } = "";            // wie im Raster, z. B. "Mo"
    public int WTagIndex { get; set; }                // ISO: 1=Mo … 7=So
    public int Stunde { get; set; }
    public int UNr { get; set; }

    public string Lehrer { get; set; } = "";
    public string Fach { get; set; } = "";
    public string? Fachraum { get; set; }
    public string? Stammraum { get; set; }

    /// <summary>Klasse | Kurs | Sonstige (AG, Förder-/Differenzierungsgruppen …).</summary>
    public string LerngruppeTyp { get; set; } = "Klasse";
    /// <summary>Klasse: "05A"; Kurs: Schülergruppe, z. B. "BIG1_EF_L68"; Sonstige: Schülergruppe oder Bezeichnung.</summary>
    public string LerngruppeKey { get; set; } = "";
    /// <summary>Roher Wert aus „Klasse(n)" der UV (bei Kursen: EF/Q1/Q2).</summary>
    public string Klasse { get; set; } = "";
    public string? Schuelergruppe { get; set; }

    /// <summary>Unterricht | VBer | UV | Sperre (z. B. „ufrei"-Blocker).</summary>
    public string Art { get; set; } = "Unterricht";
}

/// <summary>Schuljahreskalender, je Schuljahr genau einer (wird beim Push ersetzt).</summary>
public class KalenderDb
{
    public int Id { get; set; }
    public string Schuljahr { get; set; } = "";
    public DateOnly Start { get; set; }
    public DateOnly Ende { get; set; }
    public string RasterTage { get; set; } = "";      // komma-separiert, z. B. "Mo,Di,Mi,Do,Fr"
    public DateTimeOffset Aktualisiert { get; set; } = DateTimeOffset.UtcNow;

    public List<FreieZeitDb> FreieZeiten { get; set; } = new();
}

public class FreieZeitDb
{
    public int Id { get; set; }
    public int KalenderId { get; set; }
    public KalenderDb? Kalender { get; set; }

    public string Typ { get; set; } = "";             // Ferien | Feiertag | BeweglicherFerientag | SchulfreiSonstiges
    public DateOnly Von { get; set; }
    public DateOnly Bis { get; set; }
    public string Bezeichnung { get; set; } = "";
    public string Klassen { get; set; } = "";         // komma-separiert; leer = ganze Schule
}
