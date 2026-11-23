namespace VertretungApi.Model;

// =====================================================================
//  P6 – 5.4 Datenschutzfunktionen (Entscheidungen 224–238). Beide Tabellen kommen mit der Migration
//  „Datenschutz“ (P6a); das Leseprotokoll füllt erst P6b.
// =====================================================================

/// <summary>
/// P6a (Entscheidung 228): Anmeldeprotokoll – erfolglose Anmeldungen aller Konten, erfolgreiche nur von Admin-Konten
/// und Anmeldungen mit Wiederherstellungscode. Nie das eingegebene Passwort und nie der Text eines unbekannten
/// Benutzernamens; die Adresse nur gekürzt (IPv4 /24, IPv6 /48). Frist: „Aufbewahrung der Protokolle“.
/// </summary>
public class AnmeldeProtokoll
{
    public int Id { get; set; }
    public DateTime Zeit { get; set; } = DateTime.UtcNow;   // UTC
    public int? KontoId { get; set; }                       // null = unbekanntes Konto
    public string Konto { get; set; } = "";                 // Benutzername; "" = unbekanntes Konto
    public string? Rolle { get; set; }                      // BenutzerRolle als Text
    public string Ergebnis { get; set; } = "";              // Services/Anmeldeprotokoll.cs (Ergebnisse)
    public bool Erfolgreich { get; set; }
    public string? Adresse { get; set; }                    // gekürzt, z. B. „203.0.113.0/24“
    public bool? Schulnetz { get; set; }                    // null = keine Schuladressen eingetragen
    public string? Geraet { get; set; }                     // grob, z. B. „Android · Chrome“
}

/// <summary>
/// P6b (Entscheidungen 232, 233): Leseprotokoll – je Tag, Konto, Ansicht und Gegenstand ein Eintrag mit Anzahl,
/// erster und letzter Uhrzeit. Die Tabelle entsteht schon mit P6a (eine Migration für P6, Entscheidung 224).
/// </summary>
public class LeseProtokoll
{
    public int Id { get; set; }
    public DateOnly Tag { get; set; }
    public DateTime ErsteZeit { get; set; }                 // UTC
    public DateTime LetzteZeit { get; set; }                // UTC
    public int Anzahl { get; set; }
    public int? KontoId { get; set; }
    public string Akteur { get; set; } = "";                // Benutzername
    public string Rolle { get; set; } = "";                 // BenutzerRolle als Text (P7: „Eltern“)
    public string Ansicht { get; set; } = "";               // z. B. „FehlzeitenSchueler“
    public int? SchuelerId { get; set; }                    // Einzelansicht eines Schülers
    public int? LerngruppeId { get; set; }                  // Listenansicht einer Lerngruppe
    public string? Gruppe { get; set; }                     // Klasse/Stufe bzw. Schlüssel der Gruppe
    public string Gegenstand { get; set; } = "";            // lesbar, ohne Namen
    public bool IstPseudo { get; set; }                     // für „Pseudodaten entfernen“ (P9)
}
