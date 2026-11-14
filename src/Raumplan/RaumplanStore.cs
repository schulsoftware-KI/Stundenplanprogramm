#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using Stundenplan_V2.Vertretung;

namespace Stundenplan_V2.Raumplanung
{
    // =====================================================================
    //  RaumplanStore – liest UV, Lös, FGR, „Räume" und „Raumplan" aus der
    //  Plandatei und schreibt „Raumplan" (und bei Bedarf „Räume") zurück.
    //
    //  Blatt „Raumplan": Plan | WTag | Stunde | UNr | Lehrer | Fach | Klasse(n) |
    //                    Raum | Wunschraum | Herkunft | Fix (X) | Hinweis
    //    eine Zeile je Raumbelegung eines Unterrichtsteils; mehrere Pläne in
    //    einem Blatt (Spalte „Plan" = Name des Plans wie im Planeditor: Spalte aus
    //    „Lös", „[Gesichert] X" aus „Gesichert" oder „Plan" aus Blatt „Plan").
    //    Spalten werden über die Kopfzeile gefunden (Reihenfolge egal).
    //  Blatt „Räume":    Raum | Art | Fachgruppe | als Ausweich | Bereich | Bemerkung
    //    Reihenfolge der Zeilen = Reihenfolge beim Ausweichen.
    // =====================================================================
    public static class RaumplanStore
    {
        public const string BlattRaumplan = "Raumplan";
        public const string BlattRaeume = "Räume";
        private const string SheetUV = "UV";
        private const string SheetLoes = "Lös";
        private const string SheetGesichert = "Gesichert";
        private const string SheetUnrPlan = "Plan";
        /// <summary>Präfix gesicherter Lösungen – wie im Hauptfenster/Planeditor.</summary>
        public const string GesichertPraefix = "[Gesichert] ";
        private const string SheetFGR = "FGR";
        private const string StandardPlanSpalte = "Plan";

        private static readonly string[] KopfRaumplan =
            { "Plan", "WTag", "Stunde", "UNr", "Lehrer", "Fach", "Klasse(n)", "Raum", "Wunschraum", "Herkunft", "Fix (X)", "Hinweis" };

        private static readonly string[] KopfRaeume =
            { "Raum", "Art", "Fachgruppe", "als Ausweich", "Bereich", "Bemerkung" };

        // =====================================================
        //  Lesen
        // =====================================================

        public static RaumEingabe Lies(string planPfad, string? planSpalte)
            => Lies(planPfad, planSpalte, mitLoes: true);

        /// <summary>
        /// Wie <see cref="Lies(string, string?)"/>, aber ohne das Blatt „Lös" – für den
        /// Planeditor (R2), der die Belegung selbst mitbringt (<see cref="RaumEingabe.MitPlan"/>).
        /// </summary>
        public static RaumEingabe LiesOhnePlan(string planPfad)
            => Lies(planPfad, null, mitLoes: false);

        private static RaumEingabe Lies(string planPfad, string? planSpalte, bool mitLoes)
        {
            var e = new RaumEingabe { PlanPfad = planPfad };
            using var wb = new XLWorkbook(planPfad);

            LiesUv(wb, e);
            if (mitLoes) LiesPlan(wb, e, planSpalte);
            LiesFgr(wb, e);
            e.VerzeichnisAusBlatt = LiesRaeume(wb);
            LiesRaumplan(wb, e);
            return e;
        }

        private static void LiesUv(XLWorkbook wb, RaumEingabe e)
        {
            if (!wb.Worksheets.TryGetWorksheet(SheetUV, out var ws))
                throw new InvalidOperationException("Blatt 'UV' nicht gefunden.");

            var sp = KopfSpalten(ws);
            int cU = Spalte(sp, "U-Nr", "UNr");
            int cLe = Spalte(sp, "Lehrer");
            int cFa = Spalte(sp, "Fach");
            int cKl = Spalte(sp, "Klasse(n)", "Klassen");
            int cFr = Spalte(sp, "Fachraum");
            int cSr = Spalte(sp, "Stammraum");
            int cUg = Spalte(sp, "U-Gruppen");
            int cIg = Spalte(sp, "Ignore (i)", "Ignore");

            if (cU < 0 || cLe < 0 || cFa < 0 || cKl < 0)
                throw new InvalidOperationException(
                    "UV: Pflichtspalten (U-Nr, Lehrer, Fach, Klasse(n)) nicht vollständig gefunden.");
            if (cFr < 0 && cSr < 0)
                e.Hinweise.Add("In der UV fehlen die Spalten „Fachraum“ und „Stammraum“ – es gibt keine Raumwünsche.");

            // A-/B-Woche je UNr wie im Solver (ExcelLoader): aus der ersten nicht
            // ignorierten Zeile der UNr, sonst aus der ersten Zeile.
            var wocheAktiv = new Dictionary<int, string>();
            var wocheErste = new Dictionary<int, string>();

            // Wie PlanLeser: auch per „Ignore (i)" ignorierte Zeilen zählen mit – sie
            // stehen im Plan, solange ihre UNr verplant ist (wie im Vertretungsmodul).
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (!LiesZahl(row.Cell(cU).GetString(), out int unr)) continue;
                string lehrer = row.Cell(cLe).GetString().Trim();
                if (lehrer.Length == 0) continue;
                string fach = row.Cell(cFa).GetString().Trim();
                string klText = row.Cell(cKl).GetString().Trim();

                var teil = new RaumTeil
                {
                    UNr = unr,
                    Lehrer = lehrer,
                    Fach = fach,
                    KlassenText = klText,
                    Klassen = klText.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
                    Fachraeume = cFr > 0 ? SplitRaeume(row.Cell(cFr).GetString()) : new List<string>(),
                    Stammraeume = cSr > 0 ? SplitRaeume(row.Cell(cSr).GetString()) : new List<string>(),
                    BrauchtKeinenRaum = IstOhneRaumbedarf(fach)
                };

                if (!e.Unterrichte.TryGetValue(unr, out var liste))
                    e.Unterrichte[unr] = liste = new List<RaumTeil>();
                liste.Add(teil);

                string wg = cUg > 0 ? WochenGruppe(row.Cell(cUg).GetString()) : "";
                bool ignoriert = cIg > 0 && IstIgnore(row.Cell(cIg).GetString());
                wocheErste.TryAdd(unr, wg);
                if (!ignoriert) wocheAktiv.TryAdd(unr, wg);
            }

            foreach (var kv in e.Unterrichte)
            {
                string wg = wocheAktiv.TryGetValue(kv.Key, out var a) ? a
                          : wocheErste.TryGetValue(kv.Key, out var b) ? b : "";
                foreach (var t in kv.Value) t.WochenGruppe = wg;
            }
        }

        /// <summary>
        /// Liest die Belegung des gewählten Plans. Wählbar sind dieselben Pläne wie im
        /// Planeditor: die Spalten aus „Lös", die gesicherten Lösungen aus „Gesichert"
        /// („[Gesichert] X") und der manuelle UNr-Plan aus Blatt „Plan" (Name „Plan"; er geht
        /// einer gleichnamigen Lös-Spalte vor – wie im Hauptfenster). Ohne Wunsch wie bisher:
        /// Lös-Spalte „Plan", sonst die letzte Lös-Spalte.
        /// </summary>
        private static void LiesPlan(XLWorkbook wb, RaumEingabe e, string? planSpalte)
        {
            var loes = SpaltenVon(wb, SheetLoes);
            var gesichert = SpaltenVon(wb, SheetGesichert);
            bool unrPlan = wb.Worksheets.TryGetWorksheet(SheetUnrPlan, out var wsPlan);

            void Dazu(string name)
            {
                if (!e.PlanSpalten.Contains(name, StringComparer.OrdinalIgnoreCase)) e.PlanSpalten.Add(name);
            }
            foreach (var (_, name) in loes) Dazu(name);
            foreach (var (_, name) in gesichert) Dazu(GesichertPraefix + name);
            if (unrPlan) Dazu(StandardPlanSpalte);
            if (e.PlanSpalten.Count == 0)
                throw new InvalidOperationException("Kein Plan gefunden (Blätter „Lös“, „Gesichert“ und „Plan“ fehlen oder sind leer).");

            string? ziel = null;
            string wunsch = (planSpalte ?? "").Trim();
            if (wunsch.Length > 0)
                ziel = e.PlanSpalten.FirstOrDefault(p => string.Equals(p, wunsch, StringComparison.OrdinalIgnoreCase));
            ziel ??= loes.Select(x => x.Name).FirstOrDefault(n => string.Equals(n, StandardPlanSpalte, StringComparison.OrdinalIgnoreCase));
            ziel ??= loes.Count > 0 ? loes[^1].Name : e.PlanSpalten[0];
            e.PlanSpalte = ziel;

            if (unrPlan && string.Equals(ziel, StandardPlanSpalte, StringComparison.OrdinalIgnoreCase))
            {
                e.PlanQuelle = "Blatt „Plan“ (manueller UNr-Plan)";
                LiesUnrPlan(wsPlan!, e);
                return;
            }
            var ausLoes = loes.FirstOrDefault(x => string.Equals(x.Name, ziel, StringComparison.OrdinalIgnoreCase));
            if (ausLoes.Name != null)
            {
                e.PlanQuelle = "Spalte aus Blatt „Lös“";
                LiesPlanSpalte(wb.Worksheet(SheetLoes), ausLoes.Spalte, e);
                return;
            }
            var ausGesichert = gesichert.First(x => string.Equals(GesichertPraefix + x.Name, ziel, StringComparison.OrdinalIgnoreCase));
            e.PlanQuelle = "gesicherte Lösung (Blatt „Gesichert“)";
            LiesPlanSpalte(wb.Worksheet(SheetGesichert), ausGesichert.Spalte, e);
        }

        // Benannte Plan-Spalten eines Blatts im Lös-Format (A = WTag, B = Stunde, ab C je Plan).
        private static List<(int Spalte, string Name)> SpaltenVon(XLWorkbook wb, string blatt)
        {
            var l = new List<(int Spalte, string Name)>();
            if (!wb.Worksheets.TryGetWorksheet(blatt, out var ws)) return l;
            var kopf = ws.Row(1);
            int letzte = kopf.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 3; c <= letzte; c++)
            {
                string name = kopf.Cell(c).GetString().Trim();
                if (name.Length > 0) l.Add((c, name));
            }
            return l;
        }

        // Lös-Format: je Zeile WTag, Stunde und in der Plan-Spalte die UNrn mit Komma.
        private static void LiesPlanSpalte(IXLWorksheet ws, int spalte, RaumEingabe e)
        {
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                string wtag = row.Cell(1).GetString().Trim();
                if (wtag.Length == 0 || !LiesZahl(row.Cell(2).GetString(), out int stunde)) continue;
                if (!e.Tage.Contains(wtag, StringComparer.OrdinalIgnoreCase)) e.Tage.Add(wtag);

                var unrs = new List<int>();
                foreach (var tok in row.Cell(spalte).GetString().Split(','))
                    if (LiesZahl(tok, out int u) && !unrs.Contains(u)) unrs.Add(u);
                e.Slots.Add((wtag, stunde, unrs));
            }
        }

        // Blatt „Plan" (manueller UNr-Plan): je Zeile WTag, Stunde und ab Spalte C je Zelle eine
        // UNr bis zur ersten leeren Zelle – wie LadeUnrPlanAusExcel im Hauptfenster.
        private static void LiesUnrPlan(IXLWorksheet ws, RaumEingabe e)
        {
            foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > 1))
            {
                string wtag = row.Cell(1).GetString().Trim();
                if (wtag.Length == 0 || !LiesZahl(row.Cell(2).GetString(), out int stunde)) continue;
                if (!e.Tage.Contains(wtag, StringComparer.OrdinalIgnoreCase)) e.Tage.Add(wtag);

                var unrs = new List<int>();
                for (int c = 3; ; c++)
                {
                    var zelle = row.Cell(c);
                    if (zelle.IsEmpty()) break;
                    if (LiesZahl(zelle.GetString(), out int u) && !unrs.Contains(u)) unrs.Add(u);
                }
                e.Slots.Add((wtag, stunde, unrs));
            }
        }

        private static void LiesFgr(XLWorkbook wb, RaumEingabe e)
        {
            e.FgrPraefixe = ExcelLoader.LiesFachgruppenPraefixe(wb);
            if (!wb.Worksheets.TryGetWorksheet(SheetFGR, out var ws)) return;
            var used = ws.RangeUsed();
            if (used == null) return;

            IEnumerable<IXLRangeRow> zeilen = used.RowsUsed();
            if (ExcelLoader.FgrHatKopfzeile(wb)) zeilen = zeilen.Skip(1);
            foreach (var row in zeilen)
            {
                string gruppe = row.Cell(1).GetString().Trim();
                if (gruppe.Length == 0) continue;
                if (int.TryParse(row.Cell(2).GetString().Trim(), out int n)) e.FgrAnzahl[gruppe] = n;
            }
        }

        /// <summary>Liest das Blatt „Räume" (null, wenn es fehlt oder leer ist).</summary>
        private static RaumVerzeichnis? LiesRaeume(XLWorkbook wb)
        {
            if (!wb.Worksheets.TryGetWorksheet(BlattRaeume, out var ws)) return null;
            var sp = KopfSpalten(ws);
            int cR = Spalte(sp, "Raum");
            if (cR < 0) return null;
            int cA = Spalte(sp, "Art");
            int cF = Spalte(sp, "Fachgruppe");
            int cW = Spalte(sp, "als Ausweich", "Ausweich");
            int cB = Spalte(sp, "Bereich");
            int cN = Spalte(sp, "Bemerkung");

            var v = new RaumVerzeichnis { AusBlatt = true };
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                string name = row.Cell(cR).GetString().Trim();
                if (name.Length == 0) continue;
                if (v.Enthaelt(name))
                {
                    v.Hinweise.Add($"Blatt „Räume“: {name} steht mehrfach drin – es gilt die erste Zeile.");
                    continue;
                }
                v.Hinzu(new RaumInfo
                {
                    Name = name,
                    Art = cA > 0 ? RaumInfo.ParseArt(row.Cell(cA).GetString()) : RaumArt.Klassenraum,
                    Fachgruppe = cF > 0 ? row.Cell(cF).GetString().Trim() : "",
                    AlsAusweich = cW > 0 && IstJa(row.Cell(cW).GetString()),
                    Bereich = cB > 0 ? row.Cell(cB).GetString().Trim() : "",
                    Bemerkung = cN > 0 ? row.Cell(cN).GetString().Trim() : "",
                    QuelleText = "Blatt „Räume“"
                });
            }
            return v;
        }

        private static void LiesRaumplan(XLWorkbook wb, RaumEingabe e)
        {
            if (!wb.Worksheets.TryGetWorksheet(BlattRaumplan, out var ws)) return;
            e.RaumplanVorhanden = true;
            e.Raumplan.AddRange(LiesRaumplanZeilen(ws));
        }

        /// <summary>
        /// U-1a (R3): nur die Zeilen des Blatts „Raumplan“ für einen Plan – für den Grundplan-Push
        /// (die App zeigt dann den zugewiesenen Raum). null, wenn es das Blatt nicht gibt. Wirft, wenn
        /// die Plandatei nicht lesbar ist (z. B. in Excel geöffnet).
        /// </summary>
        public static List<RaumZeile>? LiesRaumplanFuer(string planPfad, string planSpalte)
        {
            using var wb = new XLWorkbook(planPfad);
            if (!wb.Worksheets.TryGetWorksheet(BlattRaumplan, out var ws)) return null;
            return LiesRaumplanZeilen(ws)
                .Where(z => string.Equals(z.Plan.Trim(), (planSpalte ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static List<RaumZeile> LiesRaumplanZeilen(IXLWorksheet ws)
        {
            var liste = new List<RaumZeile>();
            var sp = KopfSpalten(ws);
            int cP = Spalte(sp, "Plan"), cT = Spalte(sp, "WTag"), cS = Spalte(sp, "Stunde"), cU = Spalte(sp, "UNr", "U-Nr");
            if (cP < 0 || cT < 0 || cS < 0 || cU < 0) return liste;
            int cL = Spalte(sp, "Lehrer"), cF = Spalte(sp, "Fach"), cK = Spalte(sp, "Klasse(n)", "Klassen");
            int cR = Spalte(sp, "Raum"), cW = Spalte(sp, "Wunschraum"), cH = Spalte(sp, "Herkunft");
            int cX = Spalte(sp, "Fix (X)", "Fix"), cN = Spalte(sp, "Hinweis");

            string Txt(IXLRow r, int c) => c > 0 ? r.Cell(c).GetString().Trim() : "";

            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (!LiesZahl(Txt(row, cS), out int stunde) || !LiesZahl(Txt(row, cU), out int unr)) continue;
                string plan = Txt(row, cP), wtag = Txt(row, cT);
                if (plan.Length == 0 || wtag.Length == 0) continue;
                liste.Add(new RaumZeile
                {
                    Plan = plan,
                    WTag = wtag,
                    Stunde = stunde,
                    UNr = unr,
                    Lehrer = Txt(row, cL),
                    Fach = Txt(row, cF),
                    Klassen = Txt(row, cK),
                    Raum = Txt(row, cR),
                    Wunschraum = Txt(row, cW),
                    Herkunft = Txt(row, cH),
                    Fix = cX > 0 && IstJa(Txt(row, cX)),
                    Hinweis = Txt(row, cN)
                });
            }
            return liste;
        }

        // =====================================================
        //  Schreiben
        // =====================================================

        /// <summary>
        /// Ersetzt im Blatt „Raumplan" alle Zeilen der Plan-Spalte durch die neuen Zeilen
        /// (Zeilen anderer Pläne bleiben). Legt „Räume" an, falls es fehlt, bzw. hängt dort
        /// Räume an, die aus der UV ergänzt wurden. Wirft bei gesperrter Datei (Excel offen).
        /// </summary>
        public static void Speichern(string planPfad, string planSpalte, IEnumerable<RaumZeile> neueZeilen,
                                     RaumVerzeichnis verzeichnis)
        {
            using var wb = new XLWorkbook(planPfad);

            // Zeilen anderer Pläne frisch aus der Datei holen (nicht aus dem Stand beim Öffnen).
            var andere = new List<RaumZeile>();
            int? position = null;
            if (wb.Worksheets.TryGetWorksheet(BlattRaumplan, out var alt) && alt != null)
            {
                andere = LiesRaumplanZeilen(alt)
                    .Where(z => !string.Equals(z.Plan, planSpalte, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // Blatt neu anlegen (an derselben Stelle) – so bleiben keine alten Filter,
                // Formate oder Zeilen stehen.
                position = alt.Position;
                alt.Delete();
            }

            var alle = andere.Concat(neueZeilen).ToList();
            var ws = position.HasValue
                ? wb.Worksheets.Add(BlattRaumplan, position.Value)
                : wb.Worksheets.Add(BlattRaumplan);
            SchreibeRaumplan(ws, alle);

            SchreibeRaeume(wb, verzeichnis);

            BlattReihenfolge.Anwenden(wb);
            wb.Save();
        }

        /// <summary>Legt nur das Blatt „Räume" an bzw. ergänzt es (ohne Raumplan).</summary>
        public static void RaeumeSpeichern(string planPfad, RaumVerzeichnis verzeichnis)
        {
            using var wb = new XLWorkbook(planPfad);
            SchreibeRaeume(wb, verzeichnis);
            BlattReihenfolge.Anwenden(wb);
            wb.Save();
        }

        private static void SchreibeRaumplan(IXLWorksheet ws, List<RaumZeile> zeilen)
        {
            for (int c = 0; c < KopfRaumplan.Length; c++)
                ws.Cell(1, c + 1).Value = KopfRaumplan[c];
            ws.Row(1).Style.Font.Bold = true;

            int r = 2;
            foreach (var z in zeilen)
            {
                ws.Cell(r, 1).Value = z.Plan;
                ws.Cell(r, 2).Value = z.WTag;
                ws.Cell(r, 3).Value = z.Stunde;
                ws.Cell(r, 4).Value = z.UNr;
                ws.Cell(r, 5).Value = z.Lehrer;
                ws.Cell(r, 6).Value = z.Fach;
                ws.Cell(r, 7).Value = z.Klassen;
                ws.Cell(r, 8).Value = z.Raum;
                ws.Cell(r, 9).Value = z.Wunschraum;
                ws.Cell(r, 10).Value = z.Herkunft;
                ws.Cell(r, 11).Value = z.FixText;
                ws.Cell(r, 12).Value = z.Hinweis;
                r++;
            }

            ws.Range(1, 1, Math.Max(1, r - 1), KopfRaumplan.Length).SetAutoFilter();
            ws.SheetView.FreezeRows(1);
        }

        private static void SchreibeRaeume(XLWorkbook wb, RaumVerzeichnis v)
        {
            if (!wb.Worksheets.TryGetWorksheet(BlattRaeume, out var ws))
            {
                ws = wb.Worksheets.Add(BlattRaeume);
                for (int c = 0; c < KopfRaeume.Length; c++)
                    ws.Cell(1, c + 1).Value = KopfRaeume[c];
                ws.Row(1).Style.Font.Bold = true;
                int r = 2;
                foreach (var raum in v.Raeume) SchreibeRaumZeile(ws, r++, raum);
                ws.SheetView.FreezeRows(1);
                return;
            }

            // Blatt gibt es schon: nur fehlende Räume (aus der UV erkannt) unten anhängen;
            // vorhandene Zeilen bleiben unverändert.
            var sp = KopfSpalten(ws);
            int cR = Spalte(sp, "Raum");
            if (cR < 0) return;
            var vorhanden = new HashSet<string>(
                ws.RowsUsed().Skip(1).Select(x => x.Cell(cR).GetString().Trim()).Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            int naechste = (ws.LastRowUsed()?.RowNumber() ?? 1) + 1;
            foreach (var raum in v.Ergaenzte.Where(x => !vorhanden.Contains(x.Name)))
            {
                ws.Cell(naechste, cR).Value = raum.Name;
                Setze(ws, naechste, sp, "Art", raum.ArtText);
                Setze(ws, naechste, sp, "Fachgruppe", raum.Fachgruppe);
                Setze(ws, naechste, sp, "als Ausweich", raum.AusweichText, "Ausweich");
                Setze(ws, naechste, sp, "Bereich", raum.Bereich);
                Setze(ws, naechste, sp, "Bemerkung",
                    (raum.Bemerkung.Length > 0 ? raum.Bemerkung + " · " : "") + $"ergänzt am {DateTime.Today:dd.MM.yyyy}");
                naechste++;
            }
        }

        private static void SchreibeRaumZeile(IXLWorksheet ws, int r, RaumInfo raum)
        {
            ws.Cell(r, 1).Value = raum.Name;
            ws.Cell(r, 2).Value = raum.ArtText;
            ws.Cell(r, 3).Value = raum.Fachgruppe;
            ws.Cell(r, 4).Value = raum.AusweichText;
            ws.Cell(r, 5).Value = raum.Bereich;
            ws.Cell(r, 6).Value = raum.Bemerkung;
        }

        private static void Setze(IXLWorksheet ws, int r, Dictionary<string, int> sp, string spalte, string wert,
                                  string? ersatz = null)
        {
            int c = ersatz == null ? Spalte(sp, spalte) : Spalte(sp, spalte, ersatz);
            if (c > 0) ws.Cell(r, c).Value = wert;
        }

        // =====================================================
        //  Hilfsfunktionen
        // =====================================================

        /// <summary>Raumangabe zerlegen: „A~B" bzw. „A,B" = alle Räume werden gebraucht.</summary>
        public static List<string> SplitRaeume(string? s)
        {
            var liste = new List<string>();
            foreach (var teil in (s ?? "").Split(new[] { '~', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = teil.Trim();
                if (t.Length > 0 && !liste.Contains(t, StringComparer.OrdinalIgnoreCase)) liste.Add(t);
            }
            return liste;
        }

        /// <summary>Pseudofächer ohne Raumbedarf: UV/UV1…, VBer…, PSF…, ufrei, NuHo….</summary>
        public static bool IstOhneRaumbedarf(string? fach)
        {
            string f = (fach ?? "").Trim();
            return TeilInfo.IstUvFach(f) || TeilInfo.IstVberFach(f) || TeilInfo.IstPseudoFach(f)
                   || string.Equals(f, "ufrei", StringComparison.OrdinalIgnoreCase)
                   || f.StartsWith("NuHo", StringComparison.OrdinalIgnoreCase);
        }

        private static string WochenGruppe(string uGruppen)
        {
            string ug = (uGruppen ?? "").Trim().ToUpperInvariant();
            if (ug.Contains("A-WOCHE") || ug == "A") return "A";
            if (ug.Contains("B-WOCHE") || ug == "B") return "B";
            return "";
        }

        private static bool IstIgnore(string s)
        {
            string v = (s ?? "").Trim().ToLowerInvariant();
            return v == "i" || v == "x";
        }

        private static bool IstJa(string s)
        {
            string v = (s ?? "").Trim().ToLowerInvariant();
            return v == "x" || v == "ja" || v == "j" || v == "1" || v == "true";
        }

        private static bool LiesZahl(string s, out int wert)
        {
            string t = (s ?? "").Trim();
            if (int.TryParse(t, out wert)) return true;
            if (double.TryParse(t, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)
                || double.TryParse(t, out d))
            {
                wert = (int)d;
                return true;
            }
            wert = 0;
            return false;
        }

        private static Dictionary<string, int> KopfSpalten(IXLWorksheet ws)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in ws.Row(1).CellsUsed())
            {
                string h = c.GetString().Trim();
                if (h.Length > 0 && !map.ContainsKey(h)) map[h] = c.Address.ColumnNumber;
            }
            return map;
        }

        private static int Spalte(Dictionary<string, int> map, params string[] namen)
        {
            foreach (var n in namen)
                if (map.TryGetValue(n, out int c)) return c;
            return -1;
        }
    }
}
