#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildDaten – Phase 5.2a: was der Leser aus einem SchILD-NRW-3-Export
    //  im Speicher behält. NUR die Spalten aus Phase5_Vorschlag §2.2; alles
    //  andere (Adresse, Konfession, Noten, Lehrkräfte-Adressen, Kennwörter …)
    //  wird schon beim Lesen verworfen und landet nie in diesen Klassen.
    //
    //  „Zeile" = Zeilennummer in der Datei (1 = Kopfzeile), nur für Meldungen.
    //  Meldungen zu Schülerdateien nennen nie Inhalte (Entscheidung 87).
    // =====================================================================

    public enum SchildDateiArt
    {
        Klassen, Kurse, KurseLehrkraefte, Basisdaten, Leistungsdaten,
        Lernplattformen, Lehrkraefte, Faecher, Jahrgaenge
    }

    public enum SchildProblemArt { Fehler, Warnung, Hinweis }

    public sealed class SchildProblem
    {
        public SchildProblemArt Art { get; set; }
        public string Datei { get; set; } = "";
        public int? Zeile { get; set; }
        public string? Spalte { get; set; }
        public string Grund { get; set; } = "";

        public string ArtText => Art switch
        {
            SchildProblemArt.Fehler => "Fehler",
            SchildProblemArt.Warnung => "Warnung",
            _ => "Hinweis"
        };
        public string ZeileText => Zeile?.ToString() ?? "";

        public override string ToString()
        {
            string ort = Datei;
            if (Zeile != null) ort += $", Zeile {Zeile}";
            if (!string.IsNullOrEmpty(Spalte)) ort += $", Spalte „{Spalte}“";
            return $"{ArtText}: {(ort.Length > 0 ? ort + ": " : "")}{Grund}";
        }
    }

    /// <summary>Gemeinsame Felder für Datensätze, die eine Person über Name + Geburtsdatum bezeichnen.</summary>
    public abstract class SchildPersonZeile
    {
        public int Zeile { get; set; }
        public string Nachname { get; set; } = "";
        /// <summary>Zusatz hinter „#“ am Nachnamen (z. B. „05B“), sonst leer.</summary>
        public string Zusatz { get; set; } = "";
        public string Vorname { get; set; } = "";
        public DateOnly Geburtsdatum { get; set; }

        /// <summary>Schlüssel zum Verknüpfen der Dateien (nie anzeigen).</summary>
        public string PersonSchluessel => SchildText.PersonSchluessel(Nachname, Zusatz, Vorname, Geburtsdatum);
    }

    public sealed class SchildSchueler : SchildPersonZeile
    {
        /// <summary>0 Neuaufnahme · 1 Warteliste · 2 Aktiv · 3 Beurlaubt · 6 Extern · 8 Abschluss · 9 Abgang.</summary>
        public int Status { get; set; }
        /// <summary>Nur bei Status 0 gelesen (Entscheidung 80).</summary>
        public DateOnly? Aufnahmedatum { get; set; }
        public int? Jahr { get; set; }
        public int? Abschnitt { get; set; }
        public string Jahrgang { get; set; } = "";
        public string Klasse { get; set; } = "";
    }

    public sealed class SchildLeistung : SchildPersonZeile
    {
        public int Jahr { get; set; }
        public int Abschnitt { get; set; }
        public string Fach { get; set; } = "";
        public string Fachlehrer { get; set; } = "";
        public string Kursart { get; set; } = "";
        public string Kurs { get; set; } = "";
    }

    public sealed class SchildLernplattform : SchildPersonZeile
    {
        public long Id { get; set; }
    }

    public sealed class SchildKlasse
    {
        public int Zeile { get; set; }
        public string InternBez { get; set; } = "";
        public string Jahrgang { get; set; } = "";
        public string Folgeklasse { get; set; } = "";
        public string Klassenlehrer { get; set; } = "";
        public int? Jahr { get; set; }
        public int? Abschnitt { get; set; }
    }

    public sealed class SchildKurs
    {
        public int Zeile { get; set; }
        /// <summary>Leer = Unterricht im Klassenverband (dann ist Klasse gesetzt).</summary>
        public string KursBez { get; set; } = "";
        public string Klasse { get; set; } = "";
        public int Jahr { get; set; }
        public int Abschnitt { get; set; }
        /// <summary>Leer = jahrgangsübergreifend; mehrere bei „05,06,07“.</summary>
        public List<string> Jahrgaenge { get; set; } = new();
        public string Fach { get; set; } = "";
        public string Kursart { get; set; } = "";
        public string Kursleiter { get; set; } = "";
        public List<string> Zusatzkraefte { get; set; } = new();
        /// <summary>Aus der optionalen KurseLehrkraefte.dat ergänzt.</summary>
        public List<string> WeitereLehrkraefte { get; set; } = new();
        public bool AltesFormat { get; set; }

        public bool IstKlassenverband => KursBez.Length == 0;
        public string JahrgangText => Jahrgaenge.Count == 0 ? "(alle)" : string.Join(",", Jahrgaenge);

        /// <summary>Passt der Kurs zum Jahrgang eines Schülers? (leer = jahrgangsübergreifend)</summary>
        public bool PasstZuJahrgang(string jahrgang)
            => Jahrgaenge.Count == 0 ||
               Jahrgaenge.Any(j => string.Equals(j, jahrgang, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<string> AlleLehrkraefte()
        {
            if (Kursleiter.Length > 0) yield return Kursleiter;
            foreach (var z in Zusatzkraefte) yield return z;
            foreach (var w in WeitereLehrkraefte) yield return w;
        }
    }

    public sealed class SchildKursLehrkraft
    {
        public int Zeile { get; set; }
        public string KursBez { get; set; } = "";
        public int? Jahr { get; set; }
        public int? Abschnitt { get; set; }
        public string Lehrkraft { get; set; } = "";
        public string Jahrgang { get; set; } = "";
    }

    public sealed class SchildLehrkraft
    {
        public int Zeile { get; set; }
        public string Kuerzel { get; set; } = "";
        public string Nachname { get; set; } = "";
        public string Vorname { get; set; } = "";
    }

    public sealed class SchildFach
    {
        public int Zeile { get; set; }
        public string Kuerzel { get; set; } = "";
        public string Bezeichnung { get; set; } = "";
    }

    public sealed class SchildJahrgang
    {
        public int Zeile { get; set; }
        public string Kuerzel { get; set; } = "";
    }

    /// <summary>Was über eine gelesene Datei bekannt ist (ohne Inhalte).</summary>
    public sealed class SchildDateiInfo
    {
        public SchildDateiArt Art { get; set; }
        public string Dateiname { get; set; } = "";
        public bool Pflicht { get; set; }
        public bool Vorhanden { get; set; }
        /// <summary>Datei wurde ausgewertet. Vorhanden, aber nicht gelesen → NichtGelesen nennt den Grund (5.2a-2).</summary>
        public bool Gelesen { get; set; }
        /// <summary>„Kopfzeile fehlerhaft“, „Datei leer“ oder „nicht lesbar“.</summary>
        public string NichtGelesen { get; set; } = "";
        public string Zeichensatz { get; set; } = "";
        public int Datenzeilen { get; set; }
        public int Spalten { get; set; }
    }

    /// <summary>Ergebnis des Lesens eines Export-Ordners.</summary>
    public sealed class SchildExport
    {
        public string Ordner { get; set; } = "";
        public bool IstPseudo { get; set; }
        public string PseudoText { get; set; } = "";

        public List<SchildDateiInfo> Dateien { get; } = new();
        public List<SchildProblem> Probleme { get; } = new();

        public List<SchildKlasse> Klassen { get; } = new();
        public List<SchildKurs> Kurse { get; } = new();
        public List<SchildKursLehrkraft> KursLehrkraefte { get; } = new();
        public List<SchildSchueler> Schueler { get; } = new();
        public List<SchildLeistung> Leistungen { get; } = new();
        public List<SchildLernplattform> Lernplattformen { get; } = new();
        public List<SchildLehrkraft> Lehrkraefte { get; } = new();
        public List<SchildFach> Faecher { get; } = new();
        public List<SchildJahrgang> Jahrgaenge { get; } = new();

        public SchildDateiInfo? Info(SchildDateiArt art) => Dateien.FirstOrDefault(d => d.Art == art);
        /// <summary>Datei ist vorhanden UND wurde gelesen (seit 5.2a-2: eine Datei mit fehlerhafter Kopfzeile zählt als fehlend).</summary>
        public bool Hat(SchildDateiArt art) => Info(art)?.Gelesen == true;
        /// <summary>Datei liegt im Ordner, konnte aber nicht ausgewertet werden.</summary>
        public bool NichtGelesen(SchildDateiArt art) => Info(art) is { Vorhanden: true, Gelesen: false };

        public void Problem(SchildProblemArt art, string datei, int? zeile, string? spalte, string grund)
            => Probleme.Add(new SchildProblem { Art = art, Datei = datei, Zeile = zeile, Spalte = spalte, Grund = grund });
    }
}
