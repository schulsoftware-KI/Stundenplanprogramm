#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  UntisImportProtokoll – U-1b (claude/Untis_Vorschlag.md §3 Punkt 4):
    //  kleines Protokoll „untis_import.json“ neben der Plandatei – wann welche
    //  Datei für welche Tage veröffentlicht wurde, OHNE Inhalte. Je Tag zusätzlich
    //  Zeitpunkt und Prüfsumme des zuletzt gesendeten Stands (für die Rückfrage im
    //  Wocheneditor und später für U-2 „nur geänderte Tage senden“).
    //  Außerdem der zuletzt gewählte Ordner (nicht die Datei).
    // =====================================================================

    public sealed class UntisImportLauf
    {
        public string Zeit { get; set; } = "";
        public string Datei { get; set; } = "";
        public bool Demo { get; set; }
        public string Plan { get; set; } = "";
        public string Von { get; set; } = "";
        public string Bis { get; set; } = "";
        public int TageGeplant { get; set; }
        public int TageGesendet { get; set; }
        public int Eintraege { get; set; }
        public bool Abgebrochen { get; set; }
        public string? Fehler { get; set; }
    }

    public sealed class UntisTagStand
    {
        public string Zeit { get; set; } = "";
        public string Pruefsumme { get; set; } = "";
        public int Eintraege { get; set; }
    }

    public sealed class UntisImportProtokoll
    {
        public const string DateiName = "untis_import.json";
        private const int MaxLaeufe = 100;

        public string Hinweis { get; set; } =
            "Protokoll des Untis-Imports (U-1b): wann welche Datei für welche Tage veröffentlicht wurde – ohne Inhalte.";
        public string? Ordner { get; set; }
        public List<UntisImportLauf> Laeufe { get; set; } = new();
        public SortedDictionary<string, UntisTagStand> Tage { get; set; } = new(StringComparer.Ordinal);

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Pfad(string planOrdner) => Path.Combine(planOrdner, DateiName);

        public static UntisImportProtokoll Laden(string planOrdner)
        {
            try
            {
                string p = Pfad(planOrdner);
                if (!File.Exists(p)) return new UntisImportProtokoll();
                var prot = JsonSerializer.Deserialize<UntisImportProtokoll>(File.ReadAllText(p), JsonOpt) ?? new UntisImportProtokoll();
                prot.Tage = new SortedDictionary<string, UntisTagStand>(
                    prot.Tage ?? new SortedDictionary<string, UntisTagStand>(), StringComparer.Ordinal);
                prot.Laeufe ??= new List<UntisImportLauf>();
                return prot;
            }
            catch { return new UntisImportProtokoll(); }
        }

        public void Speichern(string planOrdner)
        {
            if (Laeufe.Count > MaxLaeufe) Laeufe = Laeufe.Skip(Laeufe.Count - MaxLaeufe).ToList();
            File.WriteAllText(Pfad(planOrdner), JsonSerializer.Serialize(this, JsonOpt), new UTF8Encoding(false));
        }

        public static string TagSchluessel(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Prüfsumme des gesendeten Tages (aus den Einträgen; für U-2).</summary>
        public static string Pruefsumme(PlanPushDto dto)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto.Eintraege))))[..16];

        public void TagGesendet(PlanPushDto dto)
        {
            Tage[dto.Datum.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = new UntisTagStand
            {
                Zeit = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Pruefsumme = Pruefsumme(dto),
                Eintraege = dto.Eintraege.Count
            };
        }

        /// <summary>Für die Rückfrage im Wocheneditor: „zuletzt am … aus Untis veröffentlicht“ oder null.</summary>
        public static string? TagText(string planOrdner, DateTime tag)
        {
            var p = Laden(planOrdner);
            if (!p.Tage.TryGetValue(TagSchluessel(tag), out var s)) return null;
            string zeit = DateTime.TryParseExact(s.Zeit, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var z)
                ? z.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : s.Zeit;
            return $"Der {tag:dd.MM.yyyy} wurde zuletzt am {zeit} aus Untis veröffentlicht ({s.Eintraege} Einträge).";
        }
    }
}
