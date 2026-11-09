#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Raumplanung
{
    // =====================================================================
    //  Raumplan (R1) – Datenklassen.
    //
    //  Knopf 25 „Räume zuweisen" verteilt für eine Plan-Spalte aus „Lös" die
    //  Räume auf die verplanten Unterrichtsteile und schreibt das Ergebnis in
    //  das Blatt „Raumplan" der Plandatei (eine Zeile je Raumbelegung). Das
    //  optionale Blatt „Räume" ist das Raumverzeichnis (Art, Fachgruppe,
    //  Ausweich erlaubt, Bereich). Konzept: claude/Raumplan_Vorschlag.md.
    // =====================================================================

    public enum RaumArt
    {
        Klassenraum,
        Kursraum,
        Fachraum,
        /// <summary>Wird nie ersetzt und nie als Ausweich genommen (z. B. Schwimmbad).</summary>
        Sonderraum
    }

    /// <summary>Ein Raum im Raumverzeichnis (Blatt „Räume" bzw. aus der UV abgeleitet).</summary>
    public class RaumInfo
    {
        public string Name { get; set; } = "";
        public RaumArt Art { get; set; } = RaumArt.Klassenraum;
        public string Fachgruppe { get; set; } = "";
        public bool AlsAusweich { get; set; }
        public string Bereich { get; set; } = "";
        public string Bemerkung { get; set; } = "";

        /// <summary>Position im Verzeichnis (Reihenfolge beim Ausweichen).</summary>
        public int Reihenfolge { get; set; }

        /// <summary>true = stand nicht im Blatt „Räume", wurde aus der UV erkannt.</summary>
        public bool Ergaenzt { get; set; }

        /// <summary>Anzeige: Blatt „Räume" / aus UV erkannt / aus UV ergänzt.</summary>
        public string QuelleText { get; set; } = "";

        public string ArtText => Art switch
        {
            RaumArt.Klassenraum => "Klassenraum",
            RaumArt.Kursraum => "Kursraum",
            RaumArt.Fachraum => "Fachraum",
            _ => "Sonderraum"
        };

        public string AusweichText => AlsAusweich ? "x" : "";

        public static RaumArt ParseArt(string text)
        {
            string t = (text ?? "").Trim().ToLowerInvariant();
            if (t.Contains("sonder")) return RaumArt.Sonderraum;
            if (t.Contains("fach")) return RaumArt.Fachraum;
            if (t.Contains("kurs")) return RaumArt.Kursraum;
            return RaumArt.Klassenraum;
        }
    }

    /// <summary>Raumverzeichnis: alle bekannten Räume in Verzeichnis-Reihenfolge.</summary>
    public class RaumVerzeichnis
    {
        private readonly Dictionary<string, RaumInfo> _nachName = new(StringComparer.OrdinalIgnoreCase);

        public List<RaumInfo> Raeume { get; } = new();

        /// <summary>true = aus dem Blatt „Räume" gelesen; false = komplett aus der UV abgeleitet.</summary>
        public bool AusBlatt { get; set; }

        public List<string> Hinweise { get; } = new();

        public RaumInfo? Finde(string? name)
            => name != null && _nachName.TryGetValue(name.Trim(), out var r) ? r : null;

        public bool Enthaelt(string name) => _nachName.ContainsKey(name.Trim());

        public void Hinzu(RaumInfo r)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || _nachName.ContainsKey(r.Name.Trim())) return;
            r.Name = r.Name.Trim();
            r.Reihenfolge = Raeume.Count;
            Raeume.Add(r);
            _nachName[r.Name] = r;
        }

        public IEnumerable<RaumInfo> Ergaenzte => Raeume.Where(r => r.Ergaenzt);
    }

    /// <summary>Werte der Spalte „Herkunft" im Blatt „Raumplan".</summary>
    public static class RaumHerkunft
    {
        public const string Fachraum = "Fachraum";        // Fachraum laut UV
        public const string Stammraum = "Stammraum";      // Stammraum laut UV
        public const string Abgeleitet = "abgeleitet";    // keine Angabe, Stammraum der Klasse aus der UV
        public const string Fachgruppe = "Fachgruppe";    // anderer Raum derselben Fachgruppe
        public const string Ausweich = "Ausweich";        // freier allgemeiner Raum
        public const string Zusatzraum = "Zusatzraum";    // zusätzlicher Raum zum Ausweichen (z. B. Halle beim Schwimmen)
        public const string Fest = "fest";                // Zeile mit Fix (X)
        public const string OhneRaum = "ohne Raum";
        public const string Untis = "aus Untis";          // aus der GPU001.TXT übernommen (Knopf 3)
        // Planeditor (R2): Stunde wurde verschoben, ihr Raum ist noch nicht verteilt
        // (Räume werden dort nur auf Nachfrage verteilt). Nicht in „Alle" – die Verteilung
        // selbst erzeugt diese Herkunft nie.
        public const string Offen = "offen";

        /// <summary>Reihenfolge in Übersichten.</summary>
        public static readonly string[] Alle =
            { Fachraum, Stammraum, Abgeleitet, Fachgruppe, Zusatzraum, Ausweich, Untis, Fest, OhneRaum };
    }

    /// <summary>Eine Zeile im Blatt „Raumplan" (eine Raumbelegung eines Unterrichtsteils).</summary>
    public class RaumZeile
    {
        public string Plan { get; set; } = "";
        public string WTag { get; set; } = "";
        public int Stunde { get; set; }
        public int UNr { get; set; }
        public string Lehrer { get; set; } = "";
        public string Fach { get; set; } = "";
        public string Klassen { get; set; } = "";
        public string Raum { get; set; } = "";
        public string Wunschraum { get; set; } = "";
        public string Herkunft { get; set; } = "";
        public bool Fix { get; set; }
        public string Hinweis { get; set; } = "";

        /// <summary>Nur für die Vorschau: Zeile wurde aus dem bisherigen Raumplan übernommen.</summary>
        public bool Uebernommen { get; set; }

        public string FixText => Fix ? "X" : "";

        public RaumZeile Kopie(string? plan = null) => new()
        {
            Plan = plan ?? Plan, WTag = WTag, Stunde = Stunde, UNr = UNr, Lehrer = Lehrer, Fach = Fach,
            Klassen = Klassen, Raum = Raum, Wunschraum = Wunschraum, Herkunft = Herkunft, Fix = Fix,
            Hinweis = Hinweis, Uebernommen = Uebernommen
        };

        /// <summary>Zeilen, die in der Vorschau unter „nur Ausweich und Probleme" erscheinen.</summary>
        public bool IstPruefFall =>
            Herkunft == RaumHerkunft.Ausweich
            || Herkunft == RaumHerkunft.OhneRaum
            || Herkunft == RaumHerkunft.Abgeleitet
            || (Herkunft == RaumHerkunft.Stammraum || Herkunft == RaumHerkunft.Zusatzraum)
               && !string.Equals(Raum, Wunschraum, StringComparison.OrdinalIgnoreCase)
            || Hinweis.StartsWith("Konflikt", StringComparison.OrdinalIgnoreCase);
    }

    // ---- Eingabe aus der Plandatei ----

    /// <summary>Ein Unterrichtsteil (eine UV-Zeile) mit den Raumangaben.</summary>
    public class RaumTeil
    {
        public int UNr { get; set; }
        public string Lehrer { get; set; } = "";
        public string Fach { get; set; } = "";
        public List<string> Klassen { get; set; } = new();
        public string KlassenText { get; set; } = "";
        /// <summary>Fachraum laut UV, bei „A~B" bzw. „A,B" alle (werden alle gleichzeitig gebraucht).</summary>
        public List<string> Fachraeume { get; set; } = new();
        public List<string> Stammraeume { get; set; } = new();
        /// <summary>"A", "B" oder "" (jede Woche) – je UNr wie im Solver.</summary>
        public string WochenGruppe { get; set; } = "";
        /// <summary>Pseudofach (UV, VBer, PSF…, ufrei, NuHo) – braucht keinen Raum.</summary>
        public bool BrauchtKeinenRaum { get; set; }
    }

    public class RaumEingabe
    {
        public string PlanPfad { get; set; } = "";
        /// <summary>
        /// Wählbare Pläne wie im Planeditor: Spalten aus „Lös“, gesicherte Lösungen aus
        /// „Gesichert“ („[Gesichert] X“) und der manuelle UNr-Plan aus Blatt „Plan“ („Plan“).
        /// </summary>
        public List<string> PlanSpalten { get; set; } = new();
        public string PlanSpalte { get; set; } = "";
        /// <summary>Woher die Belegung des gewählten Plans stammt (Anzeige im Fenster).</summary>
        public string PlanQuelle { get; set; } = "";

        public Dictionary<int, List<RaumTeil>> Unterrichte { get; } = new();

        /// <summary>Slots in der Reihenfolge aus „Lös" (WTag, Stunde, UNr-Liste).</summary>
        public List<(string WTag, int Stunde, List<int> UNrn)> Slots { get; } = new();
        public List<string> Tage { get; } = new();

        public Dictionary<string, List<string>> FgrPraefixe { get; set; } = new();
        public Dictionary<string, int> FgrAnzahl { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Blatt „Räume" (null, wenn es fehlt).</summary>
        public RaumVerzeichnis? VerzeichnisAusBlatt { get; set; }

        /// <summary>Alle Zeilen des Blatts „Raumplan" (alle Pläne).</summary>
        public List<RaumZeile> Raumplan { get; } = new();
        public bool RaumplanVorhanden { get; set; }

        public List<string> Hinweise { get; } = new();

        /// <summary>
        /// Kopie mit eigener Belegung und eigenem Raumplan (Planeditor, R2): UV, FGR und
        /// Raumverzeichnis werden geteilt (nur gelesen), Slots und Raumplan-Zeilen ersetzt.
        /// </summary>
        public RaumEingabe MitPlan(string planSpalte,
                                   IEnumerable<(string WTag, int Stunde, List<int> UNrn)> slots,
                                   IEnumerable<RaumZeile> raumplan)
        {
            var k = new RaumEingabe
            {
                PlanPfad = PlanPfad,
                PlanSpalte = planSpalte,
                PlanQuelle = "Planeditor (Arbeitskopie)",
                FgrPraefixe = FgrPraefixe,
                VerzeichnisAusBlatt = VerzeichnisAusBlatt,
                RaumplanVorhanden = RaumplanVorhanden
            };
            k.PlanSpalten.AddRange(PlanSpalten);
            foreach (var kv in Unterrichte) k.Unterrichte[kv.Key] = kv.Value;
            foreach (var kv in FgrAnzahl) k.FgrAnzahl[kv.Key] = kv.Value;
            foreach (var s in slots)
            {
                k.Slots.Add(s);
                if (!k.Tage.Contains(s.WTag, StringComparer.OrdinalIgnoreCase)) k.Tage.Add(s.WTag);
            }
            k.Raumplan.AddRange(raumplan);
            return k;
        }
    }

    /// <summary>Ergebnis eines Verteilungslaufs.</summary>
    public class RaumErgebnis
    {
        public string PlanSpalte { get; set; } = "";
        public bool Behalten { get; set; }
        public List<RaumZeile> Zeilen { get; } = new();

        /// <summary>Raumbelegungen (Unterricht × Raum × Stunde) je Herkunft.</summary>
        public Dictionary<string, int> JeHerkunft { get; } = new();
        public int Belegungen { get; set; }
        public int Uebernommen { get; set; }
        public int Neu { get; set; }
        public int TeileOhneAngabe { get; set; }
        public int FesteVerworfen { get; set; }
        public int FesteKonflikte { get; set; }
        public int UnbekannteUNr { get; set; }
        public List<string> Meldungen { get; } = new();

        public int Anzahl(string herkunft) => JeHerkunft.TryGetValue(herkunft, out int n) ? n : 0;
    }
}
