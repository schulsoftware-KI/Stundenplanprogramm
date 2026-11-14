#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Stundenplan_V2.Raumplanung;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  PlanRaeume – Räume im Vertretungsmodul (R3-D).
    //
    //  Raum einer Grundplan-Stunde: aus dem Blatt „Raumplan“ der gewählten
    //  Plan-Spalte (Knopf 25, Planeditor, Knopf 3 „aus Untis“) – Zuordnung wie
    //  beim Grundplan-Push (GrundplanSync.SetzeRaeume): Wochentag, Stunde, UNr,
    //  Lehrkraft und Fach (ohne Fach, falls die Zeile nicht passt). Mehrere Räume
    //  eines Teils (Fachraum „A~B“) werden mit „, “ verbunden. Ohne passende
    //  Zeile einheitlich mit der App: Fachraum vor Stammraum laut UV.
    //
    //  Dazu das Raumverzeichnis (Blatt „Räume“ bzw. aus der UV erkannt – wie
    //  Knopf 25) für die Vorschläge freier Räume (Raumvertretung, Raumsperre).
    //  Die Plandatei wird nur gelesen.
    // =====================================================================

    /// <summary>Ein freier Raum als Vorschlag (Wocheneditor-Menü, Spalte „neuer Raum“).</summary>
    public class RaumVorschlag
    {
        public string Name { get; set; } = "";
        public string Anzeige { get; set; } = "";
        /// <summary>true = „weitere Räume“ (Fachräume anderer Fächer u. Ä.) – im Menü eine Ebene tiefer.</summary>
        public bool Weitere { get; set; }
    }

    public class PlanRaeume
    {
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

        public string PlanSpalte { get; private set; } = "";
        /// <summary>Es gibt ein Blatt „Raumplan“ mit Zeilen für diese Plan-Spalte.</summary>
        public bool HatRaumplan => Belegungen > 0;
        /// <summary>Zeilen des Blatts „Raumplan“ für diese Plan-Spalte mit einem Raum.</summary>
        public int Belegungen { get; private set; }
        /// <summary>Kurze Zeile für die Fenster („Räume: Raumplan „oT_1“ …“).</summary>
        public string Info { get; private set; } = "";
        /// <summary>true = die Raumdaten konnten nicht gelesen werden (Rückfall Fachraum/Stammraum).</summary>
        public bool Fehler { get; private set; }
        /// <summary>Raumverzeichnis (Blatt „Räume“ bzw. aus der UV erkannt); null bei Lesefehler.</summary>
        public RaumVerzeichnis? Verzeichnis { get; private set; }
        /// <summary>Stand der Plandatei beim Lesen (für „neu lesen, wenn geändert“).</summary>
        public DateTime DateiStand { get; private set; }

        private readonly Dictionary<string, List<string>> _mitFach = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _ohneFach = new(StringComparer.Ordinal);
        /// <summary>Räume, die nur im Raumplan bzw. in der UV vorkommen (nicht im Verzeichnis).</summary>
        private readonly List<string> _sonstigeRaeume = new();

        // =====================================================
        //  Laden
        // =====================================================

        /// <summary>
        /// Liest Raumplan und Raumverzeichnis für die Plan-Spalte von <paramref name="plan"/>.
        /// Wirft nie: Bei einem Lesefehler (z. B. Datei in Excel geöffnet) gilt der Rückfall
        /// „Fachraum vor Stammraum“, <see cref="Info"/> nennt den Grund.
        /// </summary>
        public static PlanRaeume Lade(PlanDaten plan)
        {
            var r = new PlanRaeume { PlanSpalte = plan.GewaehltePlanSpalte };
            try { r.DateiStand = File.GetLastWriteTimeUtc(plan.PlanPfad); } catch { /* nur Komfort */ }

            try
            {
                var e = RaumplanStore.LiesOhnePlan(plan.PlanPfad);
                var v = RaumVerteilung.BaueVerzeichnis(e);
                r.Verzeichnis = v;
                string spalte = (plan.GewaehltePlanSpalte ?? "").Trim();
                foreach (var z in e.Raumplan.Where(x => Cmp.Equals(x.Plan.Trim(), spalte)))
                {
                    string raum = (z.Raum ?? "").Trim();
                    if (raum.Length == 0) continue;
                    r.Belegungen++;
                    Merke(r._mitFach, Schluessel(z.WTag, z.Stunde, z.UNr, z.Lehrer, z.Fach), raum);
                    Merke(r._ohneFach, Schluessel(z.WTag, z.Stunde, z.UNr, z.Lehrer, null), raum);
                    foreach (var einzel in Zerlege(raum))
                        if (!v.Enthaelt(einzel) && !r._sonstigeRaeume.Contains(einzel, Cmp))
                            r._sonstigeRaeume.Add(einzel);
                }
                r.Info = r.HatRaumplan
                    ? $"Räume: Raumplan „{r.PlanSpalte}“ ({r.Belegungen} Belegungen; sonst Fachraum vor Stammraum)"
                    : $"Räume: kein Raumplan für „{r.PlanSpalte}“ – Fachraum vor Stammraum laut UV (Raumplan: Knopf 25)";
            }
            catch (Exception ex)
            {
                r.Fehler = true;
                r.Verzeichnis = null;
                r.Info = "⚠ Räume: Raumplan nicht lesbar (Plandatei in Excel geöffnet?) – Fachraum vor Stammraum. " + ex.Message;
            }

            // Räume aus der UV, die im Verzeichnis fehlen (nur bei Lesefehler nötig, sonst schon erkannt).
            foreach (var t in plan.Unterrichte.Values.SelectMany(x => x))
                foreach (var einzel in Zerlege(t.Fachraum).Concat(Zerlege(t.Stammraum)))
                    if ((r.Verzeichnis == null || !r.Verzeichnis.Enthaelt(einzel)) && !r._sonstigeRaeume.Contains(einzel, Cmp))
                        r._sonstigeRaeume.Add(einzel);
            r._sonstigeRaeume.Sort(Cmp);
            return r;
        }

        /// <summary>true, wenn die Plandatei seit dem Lesen geändert wurde (z. B. Knopf 25 gespeichert).</summary>
        public bool IstVeraltet(string planPfad)
        {
            try { return File.GetLastWriteTimeUtc(planPfad) != DateiStand; }
            catch { return false; }
        }

        // =====================================================
        //  Raum einer Grundplan-Stunde
        // =====================================================

        /// <summary>Raum laut Blatt „Raumplan“ (mehrere mit „, “) oder null, wenn keine Zeile passt.</summary>
        public string? AusRaumplan(string wtag, int stunde, int unr, string lehrer, string? fach)
        {
            if (_mitFach.Count == 0) return null;
            if (_mitFach.TryGetValue(Schluessel(wtag, stunde, unr, lehrer, fach ?? ""), out var l)
                || _ohneFach.TryGetValue(Schluessel(wtag, stunde, unr, lehrer, null), out l))
                return string.Join(", ", l);
            return null;
        }

        /// <summary>Rückfall wie in der App: Fachraum vor Stammraum laut UV (unverändert, z. B. „RSh1~RSb1“).</summary>
        public static string? Rueckfall(TeilInfo t)
            => !string.IsNullOrWhiteSpace(t.Fachraum) ? t.Fachraum!.Trim()
             : !string.IsNullOrWhiteSpace(t.Stammraum) ? t.Stammraum!.Trim()
             : null;

        /// <summary>
        /// Raum einer Grundplan-Stunde: Raumplan der Plan-Spalte (wenn <see cref="PlanDaten.Raeume"/>
        /// geladen ist), sonst Fachraum vor Stammraum.
        /// </summary>
        public static string? RaumVon(PlanDaten plan, string wtag, int stunde, int unr, TeilInfo teil)
            => plan.Raeume?.AusRaumplan(wtag, stunde, unr, teil.Lehrer, teil.Fach) ?? Rueckfall(teil);

        /// <summary>Unterrichtsteil zu UNr + Lehrkraft (bei mehreren Teilen derselben Lehrkraft nach Fach).</summary>
        public static TeilInfo? TeilFuer(PlanDaten plan, int unr, string lehrer, string? fach)
        {
            if (!plan.Unterrichte.TryGetValue(unr, out var teile) || teile.Count == 0) return null;
            string le = (lehrer ?? "").Trim();
            var vonLehrer = teile.Where(x => Cmp.Equals(x.Lehrer, le)).ToList();
            if (vonLehrer.Count == 0) return null;
            return vonLehrer.FirstOrDefault(x => Cmp.Equals(Norm(x.Fach), Norm(fach))) ?? vonLehrer[0];
        }

        // =====================================================
        //  Hilfen
        // =====================================================

        /// <summary>Raumangabe zerlegen: „A~B“, „A, B“, „A;B“ = mehrere Räume (alle gleichzeitig).</summary>
        public static List<string> Zerlege(string? raum)
        {
            var l = new List<string>();
            foreach (var teil in (raum ?? "").Split(new[] { '~', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = teil.Trim();
                if (t.Length > 0 && !l.Contains(t, Cmp)) l.Add(t);
            }
            return l;
        }

        /// <summary>Gleiche Räume (Reihenfolge, Trennzeichen und Groß-/Kleinschreibung egal; leer = leer).</summary>
        public static bool GleicherRaum(string? a, string? b)
            => new HashSet<string>(Zerlege(a), Cmp).SetEquals(Zerlege(b));

        /// <summary>Deckt die Abwesenheit (Datum, Stunde) ab? Stunden ≤ 0 gelten als „ganzer Tag“ (wie BetroffeneInstanzen).</summary>
        public static bool Deckt(Abwesenheit a, DateTime datum, int stunde)
        {
            var d = datum.Date;
            if (d < a.VonDatum.Date || d > a.BisDatum.Date) return false;
            if (d == a.VonDatum.Date && a.VonStunde > 0 && stunde < a.VonStunde) return false;
            if (d == a.BisDatum.Date && a.BisStunde > 0 && stunde > a.BisStunde) return false;
            return true;
        }

        /// <summary>Räume mit einer Abwesenheit „Raum“ (Raumsperre) an diesem Tag in dieser Stunde.</summary>
        public static HashSet<string> Gesperrt(IEnumerable<Abwesenheit> abwesenheiten, DateTime datum, int stunde)
        {
            var s = new HashSet<string>(Cmp);
            foreach (var a in abwesenheiten)
                if (a.Subjekt == AbwesenheitsSubjekt.Raum && !string.IsNullOrWhiteSpace(a.Name) && Deckt(a, datum, stunde))
                    foreach (var r in Zerlege(a.Name)) s.Add(r);
            return s;
        }

        /// <summary>Kurzbeschreibung eines Raums aus dem Verzeichnis, z. B. „Klassenraum, Jg 05“.</summary>
        public string Beschreibung(string raum)
        {
            var info = Verzeichnis?.Finde(raum);
            if (info == null) return "";
            return info.ArtText + (info.Fachgruppe.Length > 0 ? ", " + info.Fachgruppe : "")
                   + (info.Bereich.Length > 0 ? ", " + info.Bereich : "");
        }

        /// <summary>Alle bekannten Raumnamen (Verzeichnis + nur im Raumplan/in der UV genannte).</summary>
        public IEnumerable<string> AlleRaeume
            => (Verzeichnis?.Raeume.Select(r => r.Name) ?? Enumerable.Empty<string>()).Concat(_sonstigeRaeume);

        // =====================================================
        //  Freie Räume
        // =====================================================

        /// <summary>
        /// In dieser Stunde belegte Räume – ohne die Zelle selbst. Frei werden Räume entfallener bzw.
        /// weg verlegter Stunden. Teile derselben UNr im selben Raum (Team-Teaching) belegen ihn gemeinsam.
        /// </summary>
        public static HashSet<string> BelegteRaeume(Tagesplan tp, TagesZelle z)
        {
            var b = new HashSet<string>(Cmp);
            foreach (var c in tp.InStunde(z.Stunde))
            {
                if (ReferenceEquals(c, z) || c.IstFrei) continue;
                if (c.UNr == z.UNr && !string.IsNullOrWhiteSpace(z.Raum) && GleicherRaum(c.Raum, z.Raum)) continue;
                foreach (var r in Zerlege(c.Raum)) b.Add(r);
            }
            return b;
        }

        /// <summary>
        /// Freie Räume für die Zelle in Vorschlagsreihenfolge: Fachraum laut UV, Räume derselben
        /// Fachgruppe, Stammraum laut UV, allgemeine Räume (Klassen-/Kursräume, „als Ausweich“) aus demselben
        /// Bereich, übrige allgemeine Räume; danach als „weitere“ die übrigen Räume (z. B. Fachräume anderer
        /// Fächer). Sonderräume (z. B. Schwimmbad) nur, wenn die UV sie für diesen Unterricht nennt.
        /// Belegte und gesperrte Räume sowie <paramref name="ohne"/> (der jetzige Raum) fehlen.
        /// </summary>
        public List<RaumVorschlag> FreieRaeume(PlanDaten plan, Tagesplan tp, TagesZelle z,
                                               ISet<string> gesperrt, IEnumerable<string> ohne)
        {
            var belegt = BelegteRaeume(tp, z);
            belegt.UnionWith(gesperrt);
            var aus = new HashSet<string>(ohne.Select(x => x.Trim()), Cmp);

            var teil = TeilFuer(plan, z.UNr, z.OriginalLehrer, z.Fach);
            var fach = Zerlege(teil?.Fachraum);
            var stamm = Zerlege(teil?.Stammraum);
            var jetzt = Zerlege(z.Raum).Concat(Zerlege(z.GrundRaum)).ToList();

            var fachgruppen = new HashSet<string>(
                jetzt.Concat(fach).Select(x => Verzeichnis?.Finde(x)?.Fachgruppe ?? "").Where(x => x.Length > 0), Cmp);
            var bereiche = new HashSet<string>(
                jetzt.Concat(stamm).Select(x => Verzeichnis?.Finde(x)?.Bereich ?? "").Where(x => x.Length > 0), Cmp);

            var liste = new List<RaumVorschlag>();
            var gesehen = new HashSet<string>(Cmp);
            void Dazu(string raum, string zusatz, bool weitere)
            {
                string r = (raum ?? "").Trim();
                if (r.Length == 0 || aus.Contains(r) || belegt.Contains(r) || !gesehen.Add(r)) return;
                string b = Beschreibung(r);
                liste.Add(new RaumVorschlag
                {
                    Name = r,
                    Anzeige = r + (b.Length > 0 ? "  (" + b + ")" : "") + zusatz,
                    Weitere = weitere
                });
            }

            var raeume = Verzeichnis?.Raeume ?? new List<RaumInfo>();
            static bool Allgemein(RaumInfo info)
                => info.Art != RaumArt.Sonderraum
                   && (info.AlsAusweich || info.Art == RaumArt.Klassenraum || info.Art == RaumArt.Kursraum);

            // 1. Fachraum laut UV  2. gleiche Fachgruppe  3. Stammraum laut UV
            // 4. allgemeine Räume im selben Bereich  5. übrige allgemeine Räume  6. „weitere“
            foreach (var name in fach) Dazu(name, "  – Fachraum laut UV", false);
            foreach (var info in raeume.Where(x => x.Art != RaumArt.Sonderraum && x.Fachgruppe.Length > 0
                                                   && fachgruppen.Contains(x.Fachgruppe)))
                Dazu(info.Name, "", false);
            foreach (var name in stamm) Dazu(name, "  – Stammraum laut UV", false);
            foreach (var info in raeume.Where(x => Allgemein(x) && x.Bereich.Length > 0 && bereiche.Contains(x.Bereich)))
                Dazu(info.Name, "", false);
            foreach (var info in raeume.Where(Allgemein)) Dazu(info.Name, "", false);
            foreach (var info in raeume.Where(x => x.Art != RaumArt.Sonderraum)) Dazu(info.Name, "", true);
            foreach (var name in _sonstigeRaeume) Dazu(name, "", true);
            return liste;
        }

        // =====================================================
        //  intern
        // =====================================================

        private static readonly Regex Leerraum = new(@"\s+", RegexOptions.Compiled);
        private static string Norm(string? s) => Leerraum.Replace((s ?? "").Trim(), " ").ToUpperInvariant();

        // Wochentag über den Index (robust für „Mo“/„Montag“), sonst der Text.
        private static string Schluessel(string wtag, int stunde, int unr, string lehrer, string? fach)
        {
            int idx = SchuljahrKalender.WochentagIndex(wtag);
            string tag = idx >= 0 ? idx.ToString() : Norm(wtag);
            return $"{tag}|{stunde}|{unr}|{Norm(lehrer)}" + (fach == null ? "" : "|" + Norm(fach));
        }

        private static void Merke(Dictionary<string, List<string>> d, string k, string raum)
        {
            if (!d.TryGetValue(k, out var l)) d[k] = l = new List<string>();
            if (!l.Contains(raum, StringComparer.OrdinalIgnoreCase)) l.Add(raum);
        }
    }
}
