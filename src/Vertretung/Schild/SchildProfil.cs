#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildProfil – Formatprofil für die SchILD-NRW-3-Textschnittstelle
    //  (Phase 5.2a, Entscheidung 69). Standard = amtliche Beschreibung
    //  (SVWS-Wiki, Fassung oldid 7235). Weicht ein echter Export ab, wird nur
    //  die Datei „schild_profil.json“ im Ordner der Plandatei angepasst – ohne
    //  Neukompilieren. Fehlende Angaben in der Datei kommen aus dem Standard.
    //
    //  Spalten[Feld] = Liste der akzeptierten Header-Texte; verglichen wird
    //  normalisiert (Leerzeichen, Groß/klein, Punkt am Ende egal).
    // =====================================================================

    public sealed class SchildDateiProfil
    {
        public string Datei { get; set; } = "";
        public bool Pflicht { get; set; }
        public Dictionary<string, List<string>> Spalten { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SchildProfil
    {
        public const string DateiName = "schild_profil.json";

        public string Beschreibung { get; set; } =
            "Formatprofil für die SchILD-NRW-3-Textschnittstelle (Stundenplanprogramm, Phase 5.2a). " +
            "Zeichensatz: auto | utf-8 | windows-1252. Spalten: je Feld die akzeptierten Spaltenüberschriften.";
        public int Version { get; set; } = 1;
        public string Trennzeichen { get; set; } = "|";
        public string Zeichensatz { get; set; } = "auto";
        public Dictionary<string, SchildDateiProfil> Dateien { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public SchildDateiProfil Fuer(SchildDateiArt art)
            => Dateien.TryGetValue(art.ToString(), out var p) ? p : Standard().Dateien[art.ToString()];

        public List<string> Namen(SchildDateiArt art, string feld)
            => Fuer(art).Spalten.TryGetValue(feld, out var l) && l.Count > 0 ? l : new List<string> { feld };

        // ---- Standard (amtliche Beschreibung) ----

        public static SchildProfil Standard()
        {
            var p = new SchildProfil();
            void D(SchildDateiArt art, string datei, bool pflicht, params (string Feld, string[] Namen)[] spalten)
            {
                var dp = new SchildDateiProfil { Datei = datei, Pflicht = pflicht };
                foreach (var (feld, namen) in spalten) dp.Spalten[feld] = namen.ToList();
                p.Dateien[art.ToString()] = dp;
            }
            string[] N(params string[] n) => n;
            var person = new[]
            {
                ("Nachname", N("Nachname", "Name")), ("Vorname", N("Vorname")), ("Geburtsdatum", N("Geburtsdatum"))
            };

            D(SchildDateiArt.Klassen, "Klassen.dat", true,
                ("InternBez", N("InternBez")), ("Jahrgang", N("Jahrgang")), ("Folgeklasse", N("Folgeklasse")),
                ("Klassenlehrer", N("Klassenlehrer")), ("Jahr", N("Jahr")), ("Abschnitt", N("Abschnitt")));
            D(SchildDateiArt.Kurse, "Kurse.dat", true,
                ("KursBez", N("KursBez")), ("Klasse", N("Klasse")), ("Jahr", N("Jahr")), ("Abschnitt", N("Abschnitt")),
                ("Jahrgang", N("Jahrgang")), ("Fach", N("Fach")), ("Kursart", N("Kursart")),
                ("Kursleiter", N("Kursleiter")), ("Zusatzkraft", N("Zusatzkraft", "Weitere Zusatzkraft")),
                ("Klassenunterricht", N("Klassenunterricht")));
            D(SchildDateiArt.KurseLehrkraefte, "KurseLehrkraefte.dat", false,
                ("KursBez", N("KursBez")), ("Jahr", N("Jahr")), ("Abschnitt", N("Abschnitt")),
                ("Lehrkraft", N("Lehrkraft")), ("Jahrgang", N("Jahrgang")));
            D(SchildDateiArt.Basisdaten, "SchuelerBasisdaten.dat", true,
                person.Concat(new[]
                {
                    ("Status", N("Status")), ("Aufnahmedatum", N("Aufnahmedatum")), ("Jahr", N("Jahr")),
                    ("Abschnitt", N("Abschnitt")), ("Jahrgang", N("Jahrgang")), ("Klasse", N("Klasse"))
                }).ToArray());
            D(SchildDateiArt.Leistungsdaten, "SchuelerLeistungsdaten.dat", true,
                person.Concat(new[]
                {
                    ("Jahr", N("Jahr")), ("Abschnitt", N("Abschnitt")), ("Fach", N("Fach")),
                    ("Fachlehrer", N("Fachlehrer")), ("Kursart", N("Kursart")), ("Kurs", N("Kurs"))
                }).ToArray());
            D(SchildDateiArt.Lernplattformen, "SchuelerLernplattformen.dat", false,
                person.Concat(new[] { ("ID", N("ID", "Schild-ID")) }).ToArray());
            D(SchildDateiArt.Lehrkraefte, "Lehrkraefte.dat", false,
                ("InternKrz", N("InternKrz")), ("Nachname", N("Nachname")), ("Vorname", N("Vorname")));
            D(SchildDateiArt.Faecher, "Faecher.dat", false,
                ("InternKrz", N("InternKrz")), ("Bezeichnung", N("Bezeichnung")));
            D(SchildDateiArt.Jahrgaenge, "Jahrgaenge.dat", false,
                ("InternKrz", N("InternKrz")));
            return p;
        }

        // ---- Laden / Vorlage ----

        /// <summary>
        /// Lädt „schild_profil.json“ aus dem Ordner, falls vorhanden, und ergänzt fehlende Angaben aus
        /// dem Standard. quelle beschreibt, welches Profil gilt; fehler ist gesetzt, wenn die Datei
        /// nicht lesbar war (dann gilt der Standard).
        /// </summary>
        public static SchildProfil Laden(string ordner, out string quelle, out string? fehler)
        {
            fehler = null;
            var std = Standard();
            string pfad = Path.Combine(ordner ?? "", DateiName);
            if (!File.Exists(pfad)) { quelle = "Standard (amtliche Beschreibung)"; return std; }

            try
            {
                var eigen = JsonSerializer.Deserialize<SchildProfil>(File.ReadAllText(pfad), JsonOpt);
                if (eigen == null) throw new InvalidDataException("leere Datei");
                if (!string.IsNullOrEmpty(eigen.Trennzeichen)) std.Trennzeichen = eigen.Trennzeichen;
                if (!string.IsNullOrWhiteSpace(eigen.Zeichensatz)) std.Zeichensatz = eigen.Zeichensatz.Trim();
                foreach (var kv in eigen.Dateien ?? new())
                {
                    if (!std.Dateien.TryGetValue(kv.Key, out var ziel) || kv.Value == null) continue;
                    if (!string.IsNullOrWhiteSpace(kv.Value.Datei)) ziel.Datei = kv.Value.Datei.Trim();
                    foreach (var sp in kv.Value.Spalten ?? new())
                        if (sp.Value != null && sp.Value.Any(n => !string.IsNullOrWhiteSpace(n)))
                            ziel.Spalten[sp.Key] = sp.Value.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                }
                quelle = $"eigenes Profil ({DateiName})";
                return std;
            }
            catch (Exception ex)
            {
                fehler = $"{DateiName} konnte nicht gelesen werden ({ex.Message}) – es gilt das Standardprofil.";
                quelle = "Standard (amtliche Beschreibung)";
                return Standard();
            }
        }

        /// <summary>Schreibt das Standardprofil als Vorlage in den Ordner. Gibt den Pfad zurück.</summary>
        public static string VorlageSpeichern(string ordner)
        {
            string pfad = Path.Combine(ordner, DateiName);
            File.WriteAllText(pfad, JsonSerializer.Serialize(Standard(), JsonOpt), new System.Text.UTF8Encoding(false));
            return pfad;
        }
    }
}
