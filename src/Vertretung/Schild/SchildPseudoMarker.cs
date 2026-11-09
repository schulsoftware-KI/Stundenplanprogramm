#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildPseudoMarker – Phase 5.2a, Entscheidung 90.
    //  Der Pseudo-Export-Generator legt „PSEUDODATEN.json“ in den Ordner, mit
    //  einer SHA-256-Prüfsumme jeder erzeugten .dat-Datei. Ein Ordner gilt nur
    //  dann als Pseudodaten, wenn ALLE Prüfsummen stimmen und keine weitere
    //  .dat-Datei darin liegt. Sonst gilt er als echt – so kann ein echter
    //  Export, der in einen Pseudo-Ordner kopiert wurde, nie als Pseudodaten
    //  durchgehen (in 5.2b nimmt die Entwicklungs-API nur Pseudodaten an, 83).
    // =====================================================================

    public static class SchildPseudoMarker
    {
        public const string DateiName = "PSEUDODATEN.json";

        public sealed class Marker
        {
            public string Art { get; set; } = "Pseudodaten";
            public string Hinweis { get; set; } =
                "Erzeugt vom Stundenplanprogramm (Pseudo-Export). Keine echten Personen. " +
                "Nur gültig, solange alle Prüfsummen stimmen.";
            public string ErzeugtAm { get; set; } = "";
            public string Plan { get; set; } = "";
            public string Schuljahr { get; set; } = "";
            public int Abschnitt { get; set; }
            public int Startwert { get; set; }
            public string Zeichensatz { get; set; } = "";
            public string Sek1Variante { get; set; } = "";
            public Dictionary<string, string> Dateien { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Stolpersteine { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Pruefsumme(string pfad)
            => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pfad)));

        /// <summary>Schreibt den Marker; berechnet die Prüfsummen der angegebenen Dateien im Ordner.</summary>
        public static void Schreiben(string ordner, Marker marker, IEnumerable<string> dateinamen)
        {
            marker.Dateien.Clear();
            foreach (var name in dateinamen)
                marker.Dateien[name] = Pruefsumme(Path.Combine(ordner, name));
            File.WriteAllText(Path.Combine(ordner, DateiName),
                JsonSerializer.Serialize(marker, JsonOpt), new System.Text.UTF8Encoding(false));
        }

        public static Marker? Laden(string ordner)
        {
            string pfad = Path.Combine(ordner, DateiName);
            if (!File.Exists(pfad)) return null;
            try { return JsonSerializer.Deserialize<Marker>(File.ReadAllText(pfad), JsonOpt); }
            catch { return null; }
        }

        /// <summary>true = gültige Pseudodaten. text beschreibt das Ergebnis für die Anzeige.</summary>
        public static bool Pruefe(string ordner, out string text)
        {
            string pfad = Path.Combine(ordner, DateiName);
            if (!File.Exists(pfad))
            {
                text = "Echte Daten – werden nur auf diesem Rechner gelesen, es wird nichts gesendet.";
                return false;
            }

            var m = Laden(ordner);
            if (m == null || m.Dateien.Count == 0)
            {
                text = "Echte Daten (Pseudo-Markierung unlesbar) – werden nur auf diesem Rechner gelesen.";
                return false;
            }

            var probleme = new List<string>();
            foreach (var kv in m.Dateien)
            {
                string d = Path.Combine(ordner, kv.Key);
                if (!File.Exists(d)) { probleme.Add($"{kv.Key} fehlt"); continue; }
                try
                {
                    if (!string.Equals(Pruefsumme(d), kv.Value, StringComparison.OrdinalIgnoreCase))
                        probleme.Add($"{kv.Key} wurde verändert");
                }
                catch { probleme.Add($"{kv.Key} nicht lesbar"); }
            }
            var gelistet = new HashSet<string>(m.Dateien.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var d in Directory.GetFiles(ordner, "*.dat").Select(x => Path.GetFileName(x)))
                if (d != null && !gelistet.Contains(d)) probleme.Add($"{d} gehört nicht zum Pseudo-Export");

            if (probleme.Count > 0)
            {
                text = "Echte Daten – die Pseudo-Markierung passt nicht mehr (" + string.Join(", ", probleme.Take(3)) +
                       (probleme.Count > 3 ? ", …" : "") + "). Werden nur auf diesem Rechner gelesen.";
                return false;
            }

            text = $"Pseudodaten (erzeugt am {m.ErzeugtAm}" +
                   (m.Plan.Length > 0 ? $" aus Plan „{m.Plan}“" : "") + ") – keine echten Personen.";
            return true;
        }
    }
}
