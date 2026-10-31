namespace VertretungApi.Model;

/// <summary>Art eines Plan-Eintrags (entspricht der Vertretungsart im Desktop).</summary>
public enum PlanArt
{
    Normal,
    Vertretung,
    Entfall,
    Verlegung,
    Betreuung,
    EVA,
    Raumvertretung,
    Zusammenlegung,
    Sonstiges
}

public enum BenutzerRolle { Admin, Lehrer, Schueler, Klassenlehrer, Stufenkoordination }

/// <summary>Ein veröffentlichter (effektiver) Vertretungstag für ein Datum.</summary>
public class VertretungsTag
{
    public int Id { get; set; }
    public DateOnly Datum { get; set; }
    public string Schuljahr { get; set; } = "";
    public DateTimeOffset Aktualisiert { get; set; } = DateTimeOffset.UtcNow;

    public List<VertretungsEintragDb> Eintraege { get; set; } = new();
}

/// <summary>Eine Stunde des effektiven Tages (Normal, Vertretung, Entfall, …).</summary>
public class VertretungsEintragDb
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public VertretungsTag? Tag { get; set; }

    public int Stunde { get; set; }
    public string Klassen { get; set; } = "";        // komma-separiert
    public string Fach { get; set; } = "";
    public string? Lehrer { get; set; }              // effektiv (Vertreter/aktuell)
    public string? OriginalLehrer { get; set; }
    public string? Raum { get; set; }

    public PlanArt Art { get; set; }
    public string Status { get; set; } = "";         // Text des ZellStatus
    public string? Grund { get; set; }               // NICHT in der Schülersicht
    public string? Bemerkung { get; set; }           // NICHT in der Schülersicht
    public bool? Fachgerecht { get; set; }
    public bool IstAenderung { get; set; }           // weicht vom Grundplan ab?

    // Phase 2, Schritt 2: Zuordnung zum Grundplan für die Wochenansicht.
    // Ältere Desktop-Versionen senden beides nicht (null) -> Zuordnung dann
    // ersatzweise über Stunde + Lehrkraft + Klassen.
    public int? UNr { get; set; }                    // UNr der gehaltenen Stunde (bei Verlegung: der verlegten)
    public int? GrundUNr { get; set; }               // UNr der Grundplan-Stunde, an deren Stelle der Eintrag tritt
}

/// <summary>Backend-Konto (Admin/Lehrkraft/Schüler). Login per JWT (Phase 1C, Phase 2 Schritt 4).</summary>
public class Benutzer
{
    public int Id { get; set; }
    public string Benutzername { get; set; } = "";
    public string PasswortHash { get; set; } = "";
    public BenutzerRolle Rolle { get; set; }
    public string? LehrerKuerzel { get; set; }       // Lehrkräfte (auch Klassenlehrer/Stufenkoordination)

    // Phase 2, Schritt 4:
    // Schülerkonto -> Schülerdatensatz. Bewusst ohne Fremdschlüssel: Die Pseudodaten werden
    // beim Neu-Seeden ersetzt; seed-konten verknüpft die Testkonten dann neu.
    public int? SchuelerId { get; set; }

    // Steht in jedem ausgegebenen Token und wird bei jeder Anfrage verglichen. Ein neuer Wert
    // („auf allen Geräten abmelden", Passwortänderung, verlorenes Handy) macht alle bisherigen
    // Tokens dieses Kontos sofort ungültig.
    public string SicherheitsStempel { get; set; } = Guid.NewGuid().ToString("N");
}
