#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  PlanLeser – liest den GRUNDPLAN aus der Plandatei (nur lesend).
    //
    //  UV   U-Nr | … | Wst | Lehrer | Fach | Klasse(n) | … | Fachraum | Stammraum
    //       -> UNr -> eine/mehrere Teile (gekoppelte Kurse / Team-Teaching).
    //       -> Kompetenzen: Lehrer -> Fächer.
    //       -> Vertretungsbereitschaft: Pseudo-Fach "VBer…" in der UV.
    //  Lös  WTag | Stunde | <Planname> … (pro Slot Komma-Liste von UNr).
    //  StD  Name | … | Std.Folge | …  -> max. aufeinanderfolgende Stunden/Tag.
    //
    //  WICHTIG für die Vertretungs-Vorschläge:
    //   - Eine im Plan PLATZIERTE Vber-Stunde (Fach "VBer…") belegt zwar den
    //     Slot, zählt aber NICHT als echtes Unterrichten: die Lehrkraft steht
    //     dort bereit und kann vertreten ("Vber wird durch die Vertretung
    //     ersetzt"). -> LehrerProSlot enthält nur ECHTES Unterrichten;
    //     VberProSlot hält die Vber-Standby-Slots separat.
    //   - Nutzbare Hohlstunden (NuHo, Kriterien aus NuHoAnalyse): Stunden 2–5,
    //     echte Lücke strikt innerhalb der Unterrichtsspanne des Tages,
    //     Std.Folge eingehalten, nur Vber-Lehrer.
    // =====================================================================

    public class TeilInfo
    {
        public string Lehrer { get; set; } = "";
        public string Fach { get; set; } = "";
        public List<string> Klassen { get; set; } = new();
        public string? Fachraum { get; set; }
        public string? Stammraum { get; set; }

        public bool IstVber => IstVberFach(Fach);
        public bool IstUv => IstUvFach(Fach);

        public static bool IstVberFach(string? fach)
            => !string.IsNullOrWhiteSpace(fach)
               && fach.Trim().StartsWith("VBer", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// UV-Stunde: Pseudo-Fach „UV" bzw. „UV1", „UV2" … (UV gefolgt von nur
        /// Ziffern). Solche Lehrkräfte können vertreten, OHNE dass der
        /// Vertretungszähler erhöht wird.
        /// </summary>
        public static bool IstUvFach(string? fach)
        {
            if (string.IsNullOrWhiteSpace(fach)) return false;
            string f = fach.Trim();
            if (!f.StartsWith("UV", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 2; i < f.Length; i++)
                if (!char.IsDigit(f[i])) return false;   // "UVabc" ist KEIN UV-Fach
            return true;
        }
    }

    public class PlanDaten
    {
        public string PlanPfad { get; set; } = "";
        public string GewaehltePlanSpalte { get; set; } = "";
        public List<string> PlanSpalten { get; set; } = new();

        public Dictionary<int, List<TeilInfo>> Unterrichte { get; set; } = new();
        public Dictionary<(string WTag, int Stunde), List<int>> Belegung { get; set; } = new();

        // Nur ECHTES Unterrichten (ohne Vber-Standby).
        public Dictionary<(string WTag, int Stunde), HashSet<string>> LehrerProSlot { get; set; } = new();
        // Vber-Standby-Slots: Lehrkräfte mit platzierter Vber-Stunde.
        public Dictionary<(string WTag, int Stunde), HashSet<string>> VberProSlot { get; set; } = new();
        // UV-Slots: Lehrkräfte mit platzierter UV-Stunde (Vertretung ohne Zählererhöhung).
        public Dictionary<(string WTag, int Stunde), HashSet<string>> UvProSlot { get; set; } = new();
        // Je Lehrkraft die am Tag belegten Stunden (echt UND Vber) – für NuHo/Spanne.
        public Dictionary<string, Dictionary<string, SortedSet<int>>> LehrerStundenAmTag { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> AlleLehrer { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Vertretungsbereit { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> LehrerFaecher { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Std.Folge je Lehrkraft (aus StD). Fehlt ein Wert -> null (keine Vorgabe).
        public Dictionary<string, int?> StdFolge { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> RasterTage { get; set; } = new();

        // NuHo-Grenzen (wie NuHoAnalyse: Stunden 2..5).
        public const int ErsterNuHoSlot = 2;
        public const int LetzterNuHoSlot = 5;

        /// <summary>Lehrkräfte, die im Slot ECHT unterrichten (ohne Vber-Standby).</summary>
        public HashSet<string> LehrerImSlot(string wtag, int stunde)
            => LehrerProSlot.TryGetValue((wtag, stunde), out var s)
                ? s : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Hat die Lehrkraft in diesem Slot eine platzierte Vber-Stunde (Standby)?</summary>
        public bool HatVberImSlot(string lehrer, string wtag, int stunde)
            => VberProSlot.TryGetValue((wtag, stunde), out var s) && s.Contains(lehrer);

        /// <summary>Hat die Lehrkraft in diesem Slot eine platzierte UV-Stunde?
        /// (Vertretung durch diese Lehrkraft erhöht den Zähler NICHT.)</summary>
        public bool HatUvImSlot(string lehrer, string wtag, int stunde)
            => UvProSlot.TryGetValue((wtag, stunde), out var s) && s.Contains(lehrer);

        public bool Fachnah(string lehrer, string fach)
            => LehrerFaecher.TryGetValue(lehrer, out var f) && f.Contains(fach);

        /// <summary>
        /// Hat die (vertretungsbereite) Lehrkraft an (wtag, stunde) eine NUTZBARE
        /// Hohlstunde? Kriterien: Stunde 2–5, Lehrkraft dort frei (kein echter
        /// Unterricht, auch keine Vber-Stunde), echte Lücke strikt zwischen erster
        /// und letzter belegter Stunde des Tages, und das Füllen überschreitet die
        /// Std.Folge nicht.
        /// </summary>
        public bool HatNutzbareHohlstunde(string lehrer, string wtag, int stunde)
        {
            if (!Vertretungsbereit.Contains(lehrer)) return false;
            if (stunde < ErsterNuHoSlot || stunde > LetzterNuHoSlot) return false;
            if (LehrerImSlot(wtag, stunde).Contains(lehrer)) return false;   // unterrichtet echt
            if (HatUvImSlot(lehrer, wtag, stunde)) return false;             // das ist Gruppe "UV"
            if (HatVberImSlot(lehrer, wtag, stunde)) return false;           // das ist Gruppe "Vber"

            if (!LehrerStundenAmTag.TryGetValue(lehrer, out var proTag) ||
                !proTag.TryGetValue(wtag, out var belegt) || belegt.Count == 0)
                return false;

            int erste = belegt.Min;
            int letzte = belegt.Max;
            if (stunde <= erste || stunde >= letzte) return false;           // keine echte Lücke
            if (belegt.Contains(stunde)) return false;

            int? sf = StdFolge.TryGetValue(lehrer, out var v) ? v : null;
            if (sf.HasValue)
            {
                int links = 0; for (int j = stunde - 1; belegt.Contains(j); j--) links++;
                int rechts = 0; for (int j = stunde + 1; belegt.Contains(j); j++) rechts++;
                if (links + 1 + rechts > sf.Value) return false;
            }
            return true;
        }
    }

    public static class PlanLeser
    {
        private const string SheetUV = "UV";
        private const string SheetLoes = "Lös";
        private const string SheetStD = "StD";
        private const string StandardPlanSpalte = "Plan";

        public static List<string> LiesPlanSpalten(string planPfad)
        {
            var result = new List<string>();
            using var wb = new XLWorkbook(planPfad);
            if (!wb.Worksheets.TryGetWorksheet(SheetLoes, out var ws)) return result;
            var kopf = ws.Row(1);
            int letzteSpalte = kopf.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 3; c <= letzteSpalte; c++)
            {
                string name = kopf.Cell(c).GetString().Trim();
                if (name.Length > 0) result.Add(name);
            }
            return result;
        }

        public static PlanDaten Lies(string planPfad, string? planSpalte = null)
        {
            var daten = new PlanDaten { PlanPfad = planPfad };
            using var wb = new XLWorkbook(planPfad);

            LiesUv(wb, daten);
            LiesLoes(wb, daten, planSpalte);
            LiesStd(wb, daten);
            BaueSlotMengen(daten);

            return daten;
        }

        // ---------------- UV ----------------

        private static void LiesUv(XLWorkbook wb, PlanDaten d)
        {
            if (!wb.Worksheets.TryGetWorksheet(SheetUV, out var ws))
                throw new InvalidOperationException("Sheet 'UV' nicht gefunden.");

            var spalte = KopfSpalten(ws);
            int cU = Spalte(spalte, "U-Nr", "UNr");
            int cLe = Spalte(spalte, "Lehrer");
            int cFa = Spalte(spalte, "Fach");
            int cKl = Spalte(spalte, "Klasse(n)", "Klassen");
            int cFr = SpalteOptional(spalte, "Fachraum");
            int cSr = SpalteOptional(spalte, "Stammraum");

            if (cU < 0 || cLe < 0 || cFa < 0 || cKl < 0)
                throw new InvalidOperationException(
                    "UV: Pflichtspalten (U-Nr, Lehrer, Fach, Klasse(n)) nicht vollständig gefunden.");

            foreach (var row in ws.RowsUsed().Skip(1))
            {
                string uStr = row.Cell(cU).GetString().Trim();
                if (!int.TryParse(uStr, out int unr))
                {
                    if (double.TryParse(uStr, out var ud)) unr = (int)ud;
                    else continue;
                }

                string lehrer = row.Cell(cLe).GetString().Trim();
                if (lehrer.Length == 0) continue;
                string fach = row.Cell(cFa).GetString().Trim();
                var klassen = SplitKlassen(row.Cell(cKl).GetString());

                var teil = new TeilInfo
                {
                    Lehrer = lehrer,
                    Fach = fach,
                    Klassen = klassen,
                    Fachraum = cFr >= 0 ? Leer2Null(row.Cell(cFr).GetString()) : null,
                    Stammraum = cSr >= 0 ? Leer2Null(row.Cell(cSr).GetString()) : null
                };

                if (!d.Unterrichte.TryGetValue(unr, out var liste))
                    d.Unterrichte[unr] = liste = new List<TeilInfo>();
                liste.Add(teil);

                d.AlleLehrer.Add(lehrer);
                if (fach.Length > 0)
                {
                    if (!d.LehrerFaecher.TryGetValue(lehrer, out var faecher))
                        d.LehrerFaecher[lehrer] = faecher = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    faecher.Add(fach);
                    if (TeilInfo.IstVberFach(fach))
                        d.Vertretungsbereit.Add(lehrer);
                }
            }
        }

        // ---------------- Lös ----------------

        private static void LiesLoes(XLWorkbook wb, PlanDaten d, string? planSpalte)
        {
            if (!wb.Worksheets.TryGetWorksheet(SheetLoes, out var ws))
                throw new InvalidOperationException("Sheet 'Lös' nicht gefunden.");

            var kopf = ws.Row(1);
            int letzteSpalte = kopf.LastCellUsed()?.Address.ColumnNumber ?? 0;

            for (int c = 3; c <= letzteSpalte; c++)
            {
                string name = kopf.Cell(c).GetString().Trim();
                if (name.Length > 0) d.PlanSpalten.Add(name);
            }

            int zielCol = -1; string zielName = "";
            if (!string.IsNullOrWhiteSpace(planSpalte))
                for (int c = 3; c <= letzteSpalte; c++)
                    if (string.Equals(kopf.Cell(c).GetString().Trim(), planSpalte, StringComparison.OrdinalIgnoreCase))
                    { zielCol = c; zielName = planSpalte!; break; }
            if (zielCol < 0)
                for (int c = 3; c <= letzteSpalte; c++)
                    if (string.Equals(kopf.Cell(c).GetString().Trim(), StandardPlanSpalte, StringComparison.OrdinalIgnoreCase))
                    { zielCol = c; zielName = StandardPlanSpalte; break; }
            if (zielCol < 0)
                for (int c = letzteSpalte; c >= 3; c--)
                {
                    string name = kopf.Cell(c).GetString().Trim();
                    if (name.Length > 0) { zielCol = c; zielName = name; break; }
                }
            if (zielCol < 0)
                throw new InvalidOperationException("Lös: keine Plan-Spalte gefunden.");

            d.GewaehltePlanSpalte = zielName;

            var tage = new List<string>();
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                string wtag = row.Cell(1).GetString().Trim();
                string stStr = row.Cell(2).GetString().Trim();
                if (wtag.Length == 0 || !int.TryParse(stStr, out int stunde)) continue;

                if (!tage.Contains(wtag)) tage.Add(wtag);

                var unrs = new List<int>();
                foreach (var tok in row.Cell(zielCol).GetString().Split(','))
                {
                    string t = tok.Trim();
                    if (t.Length == 0) continue;
                    if (int.TryParse(t, out int u)) unrs.Add(u);
                    else if (double.TryParse(t, out var ud)) unrs.Add((int)ud);
                }
                d.Belegung[(wtag, stunde)] = unrs;
            }
            d.RasterTage = tage;
        }

        // ---------------- StD (Std.Folge) ----------------

        private static void LiesStd(XLWorkbook wb, PlanDaten d)
        {
            if (!wb.Worksheets.TryGetWorksheet(SheetStD, out var ws)) return;
            var spalte = KopfSpalten(ws);
            int cName = Spalte(spalte, "Name");
            int cFolge = Spalte(spalte, "Std.Folge", "Std Folge", "StdFolge");
            if (cName < 0 || cFolge < 0) return;

            foreach (var row in ws.RowsUsed().Skip(1))
            {
                string name = row.Cell(cName).GetString().Trim();
                if (name.Length == 0) continue;
                string v = row.Cell(cFolge).GetString().Trim();
                if (int.TryParse(v, out int f) && f > 0) d.StdFolge[name] = f;
                else if (double.TryParse(v, out var fd) && fd > 0) d.StdFolge[name] = (int)fd;
            }
        }

        // ---------------- Slot-Mengen (echt / Vber / je Tag) ----------------

        private static void BaueSlotMengen(PlanDaten d)
        {
            foreach (var kv in d.Belegung)
            {
                var echt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var vber = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var uv   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (int unr in kv.Value)
                {
                    if (!d.Unterrichte.TryGetValue(unr, out var teile)) continue;
                    foreach (var t in teile)
                    {
                        if (t.IstUv)        uv.Add(t.Lehrer);     // UV-Standby (kein echter Unterricht)
                        else if (t.IstVber) vber.Add(t.Lehrer);   // Vber-Standby
                        else                echt.Add(t.Lehrer);   // echter Unterricht

                        // Tag-Belegung (echt UND Vber UND UV) für Spanne/NuHo.
                        if (!d.LehrerStundenAmTag.TryGetValue(t.Lehrer, out var proTag))
                            d.LehrerStundenAmTag[t.Lehrer] = proTag = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
                        if (!proTag.TryGetValue(kv.Key.WTag, out var set))
                            proTag[kv.Key.WTag] = set = new SortedSet<int>();
                        set.Add(kv.Key.Stunde);
                    }
                }

                d.LehrerProSlot[kv.Key] = echt;
                if (vber.Count > 0) d.VberProSlot[kv.Key] = vber;
                if (uv.Count > 0)   d.UvProSlot[kv.Key]   = uv;
            }
        }

        // ---------------- Hilfsfunktionen ----------------

        private static Dictionary<string, int> KopfSpalten(IXLWorksheet ws)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in ws.Row(1).CellsUsed())
            {
                string h = c.GetString().Trim();
                if (h.Length > 0 && !map.ContainsKey(h))
                    map[h] = c.Address.ColumnNumber;
            }
            return map;
        }

        private static int Spalte(Dictionary<string, int> map, params string[] namen)
        {
            foreach (var n in namen)
                if (map.TryGetValue(n, out int c)) return c;
            return -1;
        }

        private static int SpalteOptional(Dictionary<string, int> map, params string[] namen)
            => Spalte(map, namen);

        private static List<string> SplitKlassen(string s)
            => string.IsNullOrWhiteSpace(s)
                ? new List<string>()
                : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private static string? Leer2Null(string s)
            => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
