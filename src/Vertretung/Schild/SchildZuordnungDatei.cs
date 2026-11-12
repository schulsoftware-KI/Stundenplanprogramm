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
    //  SchildZuordnungDatei – Phase 5.2a-2: Festlegungen von Hand für die
    //  Zuordnung SchILD ↔ Stundenplan (Entscheidungen 77, 93, 113).
    //
    //  Datei „schild_zuordnung.json“ im Ordner der Plandatei (für Pseudodaten
    //  „schild_zuordnung_pseudo.json“, damit Testfestlegungen nie in die echte
    //  Zuordnung geraten). Enthält NUR Festlegungen von Hand und keine
    //  Personendaten: Lehrkräftekürzel, Fächer, Kursbezeichnungen, Klassen.
    //   - Kuerzel:  SchILD-Kürzel → Plan-Kürzel (gilt über das Schuljahr hinaus)
    //   - Faecher:  SchILD-Fach → Liste der Plan-Fächer (ebenso)
    //   - Kurse:    Schuljahr + Jahrgang + KursBez → Plan-Schlüssel
    //   - OhneKurs: Schuljahr + Klasse + Fach + Fachlehrer → Plan-Schlüssel
    //   „Plan“: null = bewusst keine Zuordnung.
    // =====================================================================

    public sealed class SchildKursFestlegung
    {
        public int Schuljahr { get; set; }
        public string Jahrgang { get; set; } = "";
        public string KursBez { get; set; } = "";
        public string? Plan { get; set; }
    }

    public sealed class SchildOhneKursFestlegung
    {
        public int Schuljahr { get; set; }
        public string Klasse { get; set; } = "";
        public string Fach { get; set; } = "";
        public string Fachlehrer { get; set; } = "";
        public string? Plan { get; set; }
    }

    public sealed class SchildZuordnungDatei
    {
        public const string DateiName = "schild_zuordnung.json";
        public const string DateiNamePseudo = "schild_zuordnung_pseudo.json";

        public int Version { get; set; } = 1;
        public string Hinweis { get; set; } =
            "Festlegungen von Hand für die Zuordnung SchILD ↔ Stundenplan (Phase 5.2a). Keine Personendaten. " +
            "„Plan“: null = bewusst keine Zuordnung. Ändern im Fenster „SchILD-Daten prüfen“, Reiter „Zuordnung“.";
        public Dictionary<string, string> Kuerzel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Faecher { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SchildKursFestlegung> Kurse { get; set; } = new();
        public List<SchildOhneKursFestlegung> OhneKurs { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Name(bool pseudo) => pseudo ? DateiNamePseudo : DateiName;

        public int Anzahl => Kuerzel.Count + Faecher.Count + Kurse.Count + OhneKurs.Count;

        // ---- Nachschlagen ----

        public string? KuerzelFuer(string schild)
            => Kuerzel.TryGetValue((schild ?? "").Trim(), out var p) ? p : null;

        public List<string>? FaecherFuer(string schild)
            => Faecher.TryGetValue((schild ?? "").Trim(), out var l) ? l : null;

        public SchildKursFestlegung? Kurs(int schuljahr, string jahrgang, string kursBez)
            => Kurse.FirstOrDefault(f => f.Schuljahr == schuljahr && SchildText.Gleich(f.Jahrgang, jahrgang)
                                         && SchildText.Gleich(f.KursBez, kursBez));

        public SchildOhneKursFestlegung? Ohne(int schuljahr, string klasse, string fach, string fachlehrer)
            => OhneKurs.FirstOrDefault(f => f.Schuljahr == schuljahr && SchildText.KlasseNorm(f.Klasse) == SchildText.KlasseNorm(klasse)
                                            && SchildText.Gleich(f.Fach, fach) && SchildText.Gleich(f.Fachlehrer, fachlehrer));

        // ---- Ändern ----

        /// <summary>plan: Schlüssel; null = keine Zuordnung. entfernen = zurück zur Automatik.</summary>
        public void SetzeKurs(int schuljahr, string jahrgang, string kursBez, string? plan, bool entfernen)
        {
            Kurse.RemoveAll(f => f.Schuljahr == schuljahr && SchildText.Gleich(f.Jahrgang, jahrgang) && SchildText.Gleich(f.KursBez, kursBez));
            if (!entfernen)
                Kurse.Add(new SchildKursFestlegung { Schuljahr = schuljahr, Jahrgang = jahrgang, KursBez = kursBez, Plan = plan });
        }

        public void SetzeOhne(int schuljahr, string klasse, string fach, string fachlehrer, string? plan, bool entfernen)
        {
            OhneKurs.RemoveAll(f => f.Schuljahr == schuljahr && SchildText.KlasseNorm(f.Klasse) == SchildText.KlasseNorm(klasse)
                                    && SchildText.Gleich(f.Fach, fach) && SchildText.Gleich(f.Fachlehrer, fachlehrer));
            if (!entfernen)
                OhneKurs.Add(new SchildOhneKursFestlegung { Schuljahr = schuljahr, Klasse = klasse, Fach = fach, Fachlehrer = fachlehrer, Plan = plan });
        }

        public void SetzeKuerzel(string schild, string? plan)
        {
            Kuerzel.Remove(schild.Trim());
            if (!string.IsNullOrWhiteSpace(plan)) Kuerzel[schild.Trim()] = plan.Trim();
        }

        public void SetzeFaecher(string schild, IEnumerable<string>? plan)
        {
            Faecher.Remove(schild.Trim());
            var l = (plan ?? Enumerable.Empty<string>()).Select(x => x.Trim()).Where(x => x.Length > 0)
                                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (l.Count > 0) Faecher[schild.Trim()] = l;
        }

        // ---- Laden / Speichern ----

        /// <summary>Lädt die Datei, falls vorhanden (sonst leer). fehler ist gesetzt, wenn sie nicht lesbar war.</summary>
        public static SchildZuordnungDatei Laden(string ordner, bool pseudo, out string? fehler)
        {
            fehler = null;
            string pfad = Path.Combine(ordner ?? "", Name(pseudo));
            if (!File.Exists(pfad)) return new SchildZuordnungDatei();
            try
            {
                var d = JsonSerializer.Deserialize<SchildZuordnungDatei>(File.ReadAllText(pfad), JsonOpt)
                        ?? throw new InvalidDataException("leere Datei");
                // Wörterbücher ohne Groß/klein-Unterschied neu aufbauen (der Deserialisierer kennt den Vergleich nicht).
                var erg = new SchildZuordnungDatei { Version = d.Version };
                foreach (var kv in d.Kuerzel ?? new())
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        erg.Kuerzel.TryAdd(kv.Key.Trim(), kv.Value.Trim());
                foreach (var kv in d.Faecher ?? new())
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null && kv.Value.Any(x => !string.IsNullOrWhiteSpace(x)))
                        erg.Faecher.TryAdd(kv.Key.Trim(), kv.Value.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList());
                erg.Kurse = (d.Kurse ?? new()).Where(f => f != null && !string.IsNullOrWhiteSpace(f.KursBez)).ToList();
                erg.OhneKurs = (d.OhneKurs ?? new()).Where(f => f != null && !string.IsNullOrWhiteSpace(f.Klasse)
                                                                && !string.IsNullOrWhiteSpace(f.Fach)).ToList();
                return erg;
            }
            catch (Exception ex)
            {
                fehler = $"{Name(pseudo)} konnte nicht gelesen werden ({ex.Message}). Es gelten keine Festlegungen von Hand; " +
                         "beim Speichern wird die Datei überschrieben.";
                return new SchildZuordnungDatei();
            }
        }

        /// <summary>Schreibt die Datei (UTF-8 ohne BOM) und gibt den Pfad zurück.</summary>
        public string Speichern(string ordner, bool pseudo)
        {
            string pfad = Path.Combine(ordner, Name(pseudo));
            var sortiert = new SchildZuordnungDatei
            {
                Version = Version,
                Kuerzel = Kuerzel.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                Faecher = Faecher.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                Kurse = Kurse.OrderBy(f => f.Schuljahr).ThenBy(f => f.Jahrgang, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(f => f.KursBez, StringComparer.OrdinalIgnoreCase).ToList(),
                OhneKurs = OhneKurs.OrderBy(f => f.Schuljahr).ThenBy(f => f.Klasse, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(f => f.Fach, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(f => f.Fachlehrer, StringComparer.OrdinalIgnoreCase).ToList()
            };
            string tmp = pfad + ".neu";
            File.WriteAllText(tmp, JsonSerializer.Serialize(sortiert, JsonOpt), new System.Text.UTF8Encoding(false));
            File.Move(tmp, pfad, true);
            return pfad;
        }
    }
}
