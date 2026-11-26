#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  Datenmodell des Vertretungsmoduls
    //
    //  Grundprinzip (siehe Konzept §3): Aus dem WOCHENPLAN des Grundprogramms
    //  (Sheet "Lös": pro ZeitSlot [WTag, Stunde] die belegten UNr) werden
    //  zusammen mit dem SchuljahrKalender konkrete STUNDEN-INSTANZEN gebildet:
    //      StundenInstanz = Datum + Stunde + Unterricht (+ optional Raum später)
    //  Jede Abwesenheit, Vertretung, jeder Entfall bezieht sich auf genau eine
    //  solche Instanz. Alle Raumfelder sind bewusst schon vorhanden, aber
    //  nullable und vorerst leer, damit die spätere Raumverplanung ohne
    //  Schema-Bruch andocken kann (Konzept §10).
    // =====================================================================

    /// <summary>Wen betrifft eine Abwesenheit?</summary>
    public enum AbwesenheitsSubjekt
    {
        Lehrer,
        Klasse,
        Raum
    }

    /// <summary>Art einer Vertretung/Planabweichung (Untis-Vorbild).</summary>
    public enum Vertretungsart
    {
        Vertretung,         // reguläre Vertretung (fachgerecht/fachfremd s. VertretungsEintrag)
        BetreuungAufsicht,  // Aufsicht ohne Unterricht
        EVA,                // eigenverantwortliches Arbeiten
        Raumvertretung,     // nur Raumwechsel (R3-D: neuer Raum in VertretungsRaum)
        Verlegung,          // Stunde auf anderen Termin geschoben
        Entfall,            // Stunde fällt aus
        Zusammenlegung      // Klassen/Kurse zusammengelegt
    }

    /// <summary>Grund einer Abwesenheit/eines Entfalls.</summary>
    public enum VertretungsGrund
    {
        Krankheit,
        Fortbildung,
        Klassenfahrt,
        Exkursion,
        Pruefung,
        Dienstbefreiung,
        Sonstiges,
        Praktikum   // V4b (288): neu, als letzter Wert angehängt (ältere Programmstände lesen ihn als „Krankheit“)
    }

    /// <summary>
    /// V4 (275): Anlass eines Entfalls – was fehlt, nicht warum. Lehrkraft fehlt (Krankheit, Fortbildung,
    /// Begleitung einer Fahrt …) → 0; Klasse/Kurs fehlt (die Lehrkraft ist freigesetzt) → −1; Raum gesperrt → 0;
    /// freie Zeit einer Klasse laut Kalender → Wert des Kalendereintrags (V4b).
    /// </summary>
    public enum EntfallAnlass
    {
        Lehrkraft,
        Klasse,
        Raum,
        FreieZeit
    }

    /// <summary>
    /// V4b (287): Anlass der ausfallenden Lehrkraft einer vertretenen Stunde (eine ANDERE Lehrkraft hält sie).
    /// Lehrkraft fehlt (krank/dienstlich) → 0 (Vorgabe); Lehrkraft nicht verhindert → −1 (sie hätte eigentlich einen
    /// Entfall gehabt). null am Eintrag = unbekannt (Buchungen bis V4a) → 0.
    /// </summary>
    public enum AusfallAnlass
    {
        Lehrkraft,
        NichtVerhindert
    }

    /// <summary>Typ eines unterrichtsfreien Zeitraums im Schuljahr.</summary>
    public enum FreieZeitTyp
    {
        Ferien,
        Feiertag,
        BeweglicherFerientag,
        SchulfreiSonstiges   // z.B. Studientag, pädagogischer Tag
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// Eine konkrete Unterrichtsstunde an einem konkreten Datum – abgeleitet aus
    /// dem Wochenplan × Kalender. Trägt alle Angaben, die Anzeige, Vertretung und
    /// Zähler brauchen. Wird NICHT dauerhaft gespeichert, sondern bei Bedarf aus
    /// Grundplan + Kalender neu erzeugt (VertretungsService, M2).
    /// </summary>
    public class StundenInstanz
    {
        public DateTime Datum { get; set; }          // Tagesdatum (Zeit = 00:00)
        public int Stunde { get; set; }              // Unterrichtsstunde (wie im Raster)
        public int UNr { get; set; }                 // Bezug zum UnterrichtsBlock des Grundplans
        public string Lehrer { get; set; } = "";
        public string Fach { get; set; } = "";
        public List<string> Klassen { get; set; } = new();

        // R3-D: Raum laut Raumplan der Plan-Spalte, sonst Fachraum vor Stammraum laut UV.
        public string? Raum { get; set; } = null;

        public string WochenTag => Datum.DayOfWeek.ToString();
    }

    /// <summary>
    /// Ein unterrichtsfreier Zeitraum (Ferien, Feiertag, beweglicher Ferientag,
    /// sonstiger schulfreier Tag). Optional nur für bestimmte Klassen gültig
    /// (z.B. Stufenfahrt); leere Klassenliste = ganze Schule.
    /// </summary>
    public class FreieZeit
    {
        public FreieZeitTyp Typ { get; set; }
        public DateTime Von { get; set; }            // inklusive
        public DateTime Bis { get; set; }            // inklusive
        public string Bezeichnung { get; set; } = "";
        public List<string> BetroffeneKlassen { get; set; } = new(); // leer = ganze Schule

        /// <summary>
        /// V4b (276): Zählwert der Entfälle, die eine freie Zeit einzelner Klassen auslöst – 0 (Vorgabe) oder −1.
        /// Nur bei Einträgen mit Klassen; ganze Schule: ohne Bedeutung (keine Stunden).
        /// </summary>
        public int EntfallZaehlwert { get; set; }

        public bool GiltFuerGanzeSchule => BetroffeneKlassen.Count == 0;

        /// <summary>Liegt <paramref name="datum"/> (Tagesanteil) in diesem Zeitraum?</summary>
        public bool Enthaelt(DateTime datum)
        {
            var d = datum.Date;
            return d >= Von.Date && d <= Bis.Date;
        }
    }

    /// <summary>
    /// Eine erfasste Abwesenheit (Auslöser der Vertretungsplanung). Spannt einen
    /// Bereich von (VonDatum, VonStunde) bis (BisDatum, BisStunde) auf.
    /// </summary>
    public class Abwesenheit
    {
        public AbwesenheitsSubjekt Subjekt { get; set; }
        public string Name { get; set; } = "";       // Lehrerkürzel / Klasse / Raum
        public DateTime VonDatum { get; set; }
        public int VonStunde { get; set; }
        public DateTime BisDatum { get; set; }
        public int BisStunde { get; set; }
        public VertretungsGrund Grund { get; set; } = VertretungsGrund.Sonstiges;
        public string Bemerkung { get; set; } = "";
    }

    /// <summary>
    /// Ein Vertretungs-/Entfall-/Verlegungseintrag für GENAU EINE Stunden-Instanz.
    /// Dies ist die dauerhaft gespeicherte Einheit (Sheet "Vertretungen").
    /// </summary>
    public class VertretungsEintrag
    {
        public DateTime Datum { get; set; }
        public int Stunde { get; set; }
        public int UNr { get; set; }

        public Vertretungsart Art { get; set; }
        public VertretungsGrund Grund { get; set; } = VertretungsGrund.Sonstiges;

        public string OriginalLehrer { get; set; } = "";
        public string? VertretungsLehrer { get; set; } = null;   // null = Entfall/keine Lehrkraft

        public List<string> Klassen { get; set; } = new();
        public string Fach { get; set; } = "";

        // R3-D: Raum laut Grundplan beim Buchen (Raumplan, sonst Fachraum vor Stammraum) und – bei einer
        // Raumänderung – der neue Raum. VertretungsRaum gilt bei jeder Art außer Entfall/Verlegung-Quelle
        // (Raumvertretung = nur der Raum ändert sich, die Lehrkraft bleibt).
        public string? OriginalRaum { get; set; } = null;
        public string? VertretungsRaum { get; set; } = null;

        public string Bemerkung { get; set; } = "";

        // Ob die Vertretung fachgerecht war (aus LehrerKompetenz bestimmt, M2).
        // null = unbekannt/nicht zutreffend (z.B. Entfall, Betreuung).
        public bool? Fachgerecht { get; set; } = null;

        // Festgehaltener Zählwert im Moment der Buchung (aus VertretungsartConfig),
        // damit spätere Konfigurationsänderungen alte Buchungen nicht rückwirkend
        // verändern. V4: Er zählt für genau eine Lehrkraft (Zaehlung.Zaehlperson, 274);
        // ein Entfall bekommt den Wert seines Anlasses (275), ein Sondergrund den
        // vom Planer gesetzten Wert (277).
        public int ZaehlwertSnapshot { get; set; }

        // ---- V4: Zählung (Entscheidungen 274, 275, 277) ----
        /// <summary>Anlass eines Entfalls (nur bei Art = Entfall); null = unbekannt (ältere Dateien) bzw. kein Entfall.</summary>
        public EntfallAnlass? Anlass { get; set; } = null;
        /// <summary>Zählwert vom Planer selbst gesetzt (−1/0/+1) – dann ist <see cref="Zaehlkommentar"/> Pflicht.</summary>
        public bool Sondergrund { get; set; }
        /// <summary>Kommentar zum Sondergrund (eine Zeile, bis 200 Zeichen); bleibt im Desktop, geht nie in die App.</summary>
        public string Zaehlkommentar { get; set; } = "";

        // ---- V4b (287, 296): ausfallende Lehrkraft einer vertretenen Stunde ----
        // Nur, wenn eine ANDERE Lehrkraft die Stunde hält (Vertretung, Betreuung, Zusammenlegung, EVA mit Aufsicht –
        // Zaehlung.HatAusfall). Die Buchung zählt dann für zwei Lehrkräfte: die haltende (ZaehlwertSnapshot) und die
        // laut Plan (ZaehlwertAusfall). Spalten 24–27 im Blatt „Vertretungen“.
        /// <summary>Anlass der ausfallenden Lehrkraft; null = unbekannt (Buchungen bis V4a, Wert 0).</summary>
        public AusfallAnlass? AnlassAusfall { get; set; } = null;
        /// <summary>Zählwert der ausfallenden Lehrkraft (Anlass bzw. Sondergrund).</summary>
        public int ZaehlwertAusfall { get; set; }
        /// <summary>Zählwert der ausfallenden Lehrkraft vom Planer selbst gesetzt (−1/0/+1, Kommentar Pflicht).</summary>
        public bool SondergrundAusfall { get; set; }
        /// <summary>Kommentar zum Sondergrund der ausfallenden Lehrkraft (eine Zeile, bis 200 Zeichen; nie in die App).</summary>
        public string KommentarAusfall { get; set; } = "";

        // ---- Verlegung (M3a, Konzept §15) ----
        // Eine Verlegung besteht aus ZWEI verknüpften Einträgen (Ziel + Quelle)
        // mit derselben VerlegungsId. Beide tragen Art = Verlegung. Die Von/Nach-
        // Felder halten die jeweils ANDERE Hälfte fest – tagesübergreifend möglich:
        //   - Ziel-Eintrag  (an der freigewordenen Stunde a): VerlegtVon* zeigt auf
        //     die Quelle (die verschobene Stunde b).
        //   - Quell-Eintrag (an der nun freien Stunde b):     VerlegtNach* zeigt auf
        //     das Ziel (Stunde a).
        // Bei allen Nicht-Verlegungen bleiben diese Felder leer/null.
        public string VerlegungsId { get; set; } = "";
        public DateTime? VerlegtVonDatum { get; set; } = null;
        public int? VerlegtVonStunde { get; set; } = null;
        public DateTime? VerlegtNachDatum { get; set; } = null;
        public int? VerlegtNachStunde { get; set; } = null;

        public bool IstVerlegung => Art == Vertretungsart.Verlegung;

        // ---- Dauervertretung (V3, Entscheidungen 158–162) ----
        // Alle Buchungen einer Dauervertretung (eine Wochenstunde, jede Woche bis zu einem Datum) tragen dieselbe
        // SerienId; leer = Einzelbuchung. Jede Stunde bleibt ein eigener Eintrag (zählt wie eine einzelne
        // Vertretung, Entscheidung 160). Wird ein Tag der Serie einzeln umgebucht, gehört er nicht mehr dazu.
        public string SerienId { get; set; } = "";

        public bool IstDauervertretung => !string.IsNullOrWhiteSpace(SerienId);
    }

    /// <summary>
    /// Konfigurierbarer Zählwert je Vertretungsart – der eigentliche
    /// Untis-Mechanismus (Konzept §5). Standardwerte s. <see cref="Standard"/>.
    /// </summary>
    public class VertretungsartConfig
    {
        public Vertretungsart Art { get; set; }
        public int Zaehlwert { get; set; }               // +1 / 0 / -1
        public bool FachgerechtUnterscheiden { get; set; } // nur bei Art=Vertretung sinnvoll

        /// <summary>Liefert die Standardkonfiguration aller Arten.</summary>
        public static List<VertretungsartConfig> Standard() => new()
        {
            new() { Art = Vertretungsart.Vertretung,        Zaehlwert = +1, FachgerechtUnterscheiden = true  },
            new() { Art = Vertretungsart.BetreuungAufsicht, Zaehlwert = +1, FachgerechtUnterscheiden = false },
            new() { Art = Vertretungsart.EVA,               Zaehlwert =  0, FachgerechtUnterscheiden = false },
            new() { Art = Vertretungsart.Raumvertretung,    Zaehlwert =  0, FachgerechtUnterscheiden = false },
            new() { Art = Vertretungsart.Verlegung,         Zaehlwert =  0, FachgerechtUnterscheiden = false },
            new() { Art = Vertretungsart.Entfall,           Zaehlwert =  0, FachgerechtUnterscheiden = false },
            new() { Art = Vertretungsart.Zusammenlegung,    Zaehlwert = +1, FachgerechtUnterscheiden = false },
        };
    }

    /// <summary>
    /// Fächer/Kompetenzen eines Lehrers – Basis für fachgerecht/fachfremd und für
    /// die Vertretungsvorschläge. Wird in M2 primär aus der UV-Tabelle abgeleitet
    /// (KompetenzAusUv); <see cref="ManuellErgaenzt"/> hält Fächer, die sich nicht
    /// aus der UV ergeben und von Hand eingetragen wurden.
    /// </summary>
    public class LehrerKompetenz
    {
        public string Lehrer { get; set; } = "";
        public HashSet<string> Faecher { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ManuellErgaenzt { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        public bool KannFach(string fach)
            => !string.IsNullOrWhiteSpace(fach)
               && (Faecher.Contains(fach) || ManuellErgaenzt.Contains(fach));
    }

    /// <summary>
    /// Aggregierter Zählerstand – je Lehrer bzw. je Klasse/Fach. Wird aus den
    /// VertretungsEinträgen berechnet (VertretungsZaehler, M2) und kann als
    /// Cache ins Sheet "Zaehler" geschrieben werden.
    /// </summary>
    public class ZaehlerStand
    {
        public string Bezug { get; set; } = "";          // Lehrerkürzel bzw. Klasse|Fach
        public Dictionary<Vertretungsart, int> ProArt { get; set; } = new();   // gehaltene Stunden je Art (Anzahl)
        public int GehalteneVertretungen { get; set; }   // V4: „Gehalten (Punkte)“ – Zählwerte ohne Entfälle und Sondergründe
        public int EigeneAusfaelle { get; set; }         // V4: „Entfall gesamt“ – eigene entfallene Stunden (Anzahl)
        public int Saldo { get; set; }                   // V4 (274): Summe aller Zählwerte der Lehrkraft

        // V4 (280): Aufschlüsselung der Entfälle und Sondergründe der Lehrkraft
        public int Entfall0 { get; set; }                // Entfälle mit Zählwert 0 (ohne Sondergrund), Anzahl
        public int EntfallMinus1 { get; set; }           // Entfälle mit Zählwert −1 (ohne Sondergrund), Anzahl
        public int Sondergruende { get; set; }           // Buchungen mit Sondergrund, Anzahl
        public int SondergrundPunkte { get; set; }       // Summe ihrer Zählwerte

        // V4b (293): ausfallende Lehrkraft vertretener Stunden (ohne Sondergrund; Sondergründe stehen oben mit)
        public int Vertreten0 { get; set; }              // vertreten, Lehrkraft fehlt bzw. unbekannt (0), Anzahl
        public int VertretenMinus1 { get; set; }         // vertreten, Lehrkraft nicht verhindert (−1), Anzahl

        // Für Klassen/Fach-Auswertung:
        public int SollStunden { get; set; }             // ohne freie Tage
        public int EntfallStunden { get; set; }
        public int FreiLautKalender { get; set; }        // V4b (280): Entfälle aus freien Zeiten der Klasse (Kalender)
        public double EntfallQuote => SollStunden > 0 ? (double)EntfallStunden / SollStunden : 0.0;
    }
}
