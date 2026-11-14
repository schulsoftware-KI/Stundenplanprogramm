#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  U-1b – Untis-Übergangsschnittstelle: Datenklassen.
    //  Gelesen wird die Untis-Exportdatei GPU014.TXT („Export Vertretungen“,
    //  22 Felder). Absenzgrund (Feld 16) und Text zur Vertretung (Feld 17)
    //  werden nie gelesen – UntisZeile hat dafür keine Felder (Entscheidung 123).
    //  Konzept und Entscheidungen 117–135: claude/Untis_Vorschlag.md.
    // =====================================================================

    public enum UntisProblemArt { Fehler, Warnung, Hinweis }

    public sealed class UntisProblem
    {
        public UntisProblemArt Art { get; set; }
        public int? Zeile { get; set; }
        public string? Spalte { get; set; }
        public string Grund { get; set; } = "";

        public string ArtText => Art switch
        {
            UntisProblemArt.Fehler => "Fehler",
            UntisProblemArt.Warnung => "Warnung",
            _ => "Hinweis"
        };
        public string ZeileText => Zeile?.ToString(CultureInfo.InvariantCulture) ?? "";

        public override string ToString()
        {
            string ort = Zeile != null ? $"Zeile {Zeile}" : "";
            if (!string.IsNullOrEmpty(Spalte)) ort += (ort.Length > 0 ? ", " : "") + Spalte;
            return $"{ArtText}: {(ort.Length > 0 ? ort + ": " : "")}{Grund}";
        }
    }

    public sealed class UntisZahl
    {
        public string Bereich { get; set; } = "";
        public string Angabe { get; set; } = "";
        public string Wert { get; set; } = "";
    }

    /// <summary>Eine gelesene Zeile der GPU014.TXT – ohne Absenzgrund und Text (Felder 16/17).</summary>
    public sealed class UntisZeile
    {
        public int Nr { get; set; }                       // Zeilennummer in der Datei (ab 1)
        public int Vertretungsnummer { get; set; }        // Feld 1
        public DateTime Datum { get; set; }               // Feld 2
        public int Stunde { get; set; }                   // Feld 3
        public int UNr { get; set; }                      // Feld 5 (0 = ohne)
        public string Absent { get; set; } = "";          // Feld 6
        public string Vertreter { get; set; } = "";       // Feld 7
        public string Fach { get; set; } = "";            // Feld 8 (Leerzeichen zusammengefasst)
        public string VFach { get; set; } = "";           // Feld 10
        public string Raum { get; set; } = "";            // Feld 12
        public string VRaum { get; set; } = "";           // Feld 13
        public List<string> Klassen { get; set; } = new();    // Feld 15
        public long Bits { get; set; }                    // Feld 18
        public List<string> VKlassen { get; set; } = new();   // Feld 19
        public string Buchstabe { get; set; } = "";       // Feld 20
        public string Geaendert { get; set; } = "";       // Feld 21 (JJJJMMTTHHMM)

        public bool Bit(int n) => (Bits & (1L << n)) != 0;
    }

    /// <summary>Ergebnis des Lesens (noch ohne Abgleich mit dem Plan).</summary>
    public sealed class UntisDatei
    {
        public string Pfad { get; set; } = "";
        public string Zeichensatz { get; set; } = "";
        public char Trennzeichen { get; set; } = ';';
        public int ZeilenGesamt { get; set; }             // nicht leere Zeilen
        public List<UntisZeile> Zeilen { get; } = new();
        public List<UntisProblem> Probleme { get; } = new();
        /// <summary>Zeilen, in denen Feld 16 (Absenzgrund) gefüllt ist – nur gezählt, nie gelesen.</summary>
        public int MitAbsenzgrund { get; set; }
        /// <summary>Zeilen, in denen Feld 17 (Text) gefüllt ist – nur gezählt, nie gelesen.</summary>
        public int MitText { get; set; }
        public DateTime? LetzteAenderung { get; set; }
        public bool IstDemo { get; set; }
        public string DemoText { get; set; } = "";
    }

    /// <summary>Ein übersetzter Eintrag: was gesendet wird (Dto) und was die Vorschau zeigt.</summary>
    public sealed class UntisEintrag
    {
        public DateTime Datum { get; set; }
        public int Zeile { get; set; }
        public string UntisArt { get; set; } = "";
        /// <summary>„ja“ (Grundplan-Stunde gefunden), „Kurs“ (Klausur), „zusätzlich“ (absichtlich), „nein“.</summary>
        public string Gegenstueck { get; set; } = "";
        public PlanEintragDto Dto { get; set; } = new();

        public int Stunde => Dto.Stunde;
        public string KlassenText { get; set; } = "";
        public string FachText { get; set; } = "";
        public string LehrerText { get; set; } = "";
        public string RaumText { get; set; } = "";
        public string AppText { get; set; } = "";
        /// <summary>Zeilenfarbe der Vorschau (wie die Legende im Wocheneditor; Klausur pink wie in der App).</summary>
        public string Farbe { get; set; } = "#FFFFFF";
    }

    /// <summary>Ein Tag zur Auswahl in der Vorschau.</summary>
    public sealed class UntisTagAuswahl
    {
        public DateTime Datum { get; set; }
        public int Anzahl { get; set; }
        public bool Schultag { get; set; }
        public string Anzeige => UntisText.Tag(Datum) + $" {Datum:dd.MM.yyyy} – " +
                                 (Anzahl == 1 ? "1 Eintrag" : $"{Anzahl} Einträge");
    }

    public sealed class UntisErgebnis
    {
        public string PlanName { get; set; } = "";
        public SortedDictionary<DateTime, List<UntisEintrag>> Tage { get; } = new();
        public List<UntisProblem> Probleme { get; } = new();
        public List<UntisZahl> Zahlen { get; } = new();
        public DateTime? ErsterTag { get; set; }
        public DateTime? LetzterTag { get; set; }
        public int OffenAbHeute { get; set; }
        public int OhneGegenstueck { get; set; }

        public int Anzahl(UntisProblemArt art) => Probleme.Count(p => p.Art == art);

        public List<UntisEintrag> EintraegeAm(DateTime tag)
            => Tage.TryGetValue(tag.Date, out var l) ? l : new List<UntisEintrag>();

        /// <summary>Push für einen Tag: alle Einträge (auch keiner – dann wird der Tag in der App leer).</summary>
        public PlanPushDto PushFuer(string schuljahr, DateTime tag) => new()
        {
            Schuljahr = schuljahr,
            Datum = DateOnly.FromDateTime(tag.Date),
            Eintraege = EintraegeAm(tag).Select(e => e.Dto).ToList()
        };

        /// <summary>Bericht für die Zwischenablage – ohne Absenzgründe und Texte (die werden nie gelesen).</summary>
        public string Bericht(UntisDatei datei, int maxProbleme = 200)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Untis-Vertretungen (GPU014.TXT) – Bericht");
            sb.AppendLine($"Datei: {System.IO.Path.GetFileName(datei.Pfad)} ({(datei.IstDemo ? "Demo-Datei" : "echte Datei")})");
            sb.AppendLine($"Plan: {PlanName}");
            sb.AppendLine($"Stand: {DateTime.Now:dd.MM.yyyy HH:mm}");
            sb.AppendLine();
            string bereich = "";
            foreach (var z in Zahlen)
            {
                if (z.Bereich != bereich) { bereich = z.Bereich; sb.AppendLine(bereich + ":"); }
                sb.AppendLine($"  {z.Angabe}: {z.Wert}");
            }
            sb.AppendLine();
            sb.AppendLine($"Probleme: {Anzahl(UntisProblemArt.Fehler)} Fehler, {Anzahl(UntisProblemArt.Warnung)} Warnungen, " +
                          $"{Anzahl(UntisProblemArt.Hinweis)} Hinweise");
            foreach (var p in Probleme.Take(maxProbleme)) sb.AppendLine("  " + p);
            if (Probleme.Count > maxProbleme) sb.AppendLine($"  … und {Probleme.Count - maxProbleme} weitere");
            sb.AppendLine();
            sb.AppendLine("Absenzgründe (Feld 16) und Texte zur Vertretung (Feld 17) werden nicht gelesen.");
            return sb.ToString();
        }
    }

    internal static class UntisText
    {
        private static readonly string[] Kurz = { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };
        public static string Tag(DateTime d) => Kurz[(int)d.DayOfWeek];
        public static string TagDatum(DateTime d) => $"{Tag(d)} {d.ToString("dd.MM.", CultureInfo.InvariantCulture)}";
    }
}
