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
        Raumvertretung,     // nur Raumwechsel (später)
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
        Sonstiges
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

        // Raumbezug – vorerst leer, Füllung mit späterer Raumverplanung.
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

        // Raumbezug – vorerst leer (spätere Raumverplanung, Konzept §10).
        public string? OriginalRaum { get; set; } = null;
        public string? VertretungsRaum { get; set; } = null;

        public string Bemerkung { get; set; } = "";

        // Ob die Vertretung fachgerecht war (aus LehrerKompetenz bestimmt, M2).
        // null = unbekannt/nicht zutreffend (z.B. Entfall, Betreuung).
        public bool? Fachgerecht { get; set; } = null;

        // Festgehaltener Zählwert im Moment der Buchung (aus VertretungsartConfig),
        // damit spätere Konfigurationsänderungen alte Buchungen nicht rückwirkend
        // verändern.
        public int ZaehlwertSnapshot { get; set; }

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
        public Dictionary<Vertretungsart, int> ProArt { get; set; } = new();
        public int GehalteneVertretungen { get; set; }   // Summe gewichtet (Zählwerte)
        public int EigeneAusfaelle { get; set; }         // eigene ausgefallene Stunden
        public int Saldo { get; set; }                   // gehaltene − erlassene

        // Für Klassen/Fach-Auswertung:
        public int SollStunden { get; set; }             // ohne freie Tage
        public int EntfallStunden { get; set; }
        public double EntfallQuote => SollStunden > 0 ? (double)EntfallStunden / SollStunden : 0.0;
    }
}
