#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  V3 – Dauervertretung (Entscheidungen 158–162, Nutzer 14.11.2026)
    //
    //  Fachlogik ohne Oberfläche (Fenster: TagesEditorDialog_Dauer.cs):
    //   - Vorschau: Eine Wochenstunde (Wochentag, Stunde, Unterricht der abwesenden Lehrkraft – 158) wird an
    //     jedem Schultag dieses Wochentags von–bis gebucht. Ferien und freie Tage laut Kalender werden
    //     übersprungen. Fehlt die Vertretungskraft an einem Tag oder ist sie in der Stunde schon belegt, wird
    //     dort NICHT gebucht (Lücke mit Hinweis – 159). Weg verlegte Stunden bleiben unangetastet.
    //   - Buchung: je Tag ein eigener VertretungsEintrag mit derselben SerienId; Zählwert wie bei einer
    //     einzelnen Vertretung (UV-Einsatz zählt nicht – 160). Ein neuer Raum (R3-D) wird mitgenommen, wenn er an
    //     dem Tag frei ist, sonst bleibt der Raum laut Plan.
    //   - Zurücksetzen (162): künftige Buchungen ab einem Datum – nur einer Serie bzw. einer Lehrkraft.
    // =====================================================================

    /// <summary>Ergebnis der Vorschau für einen Tag.</summary>
    public enum DauerTagStatus
    {
        Buchen,          // wird gebucht (ggf. ersetzt eine bestehende Buchung)
        LueckeFehlt,     // Vertretungskraft fehlt → Stunde bleibt offen (159)
        LueckeBelegt,    // Vertretungskraft ist in der Stunde schon belegt → Stunde bleibt offen (159)
        Behalten,        // bestehende Buchung bleibt (Ersetzen ausgeschaltet)
        Verlegt,         // Stunde ist weg verlegt bzw. eine Verlegung → bleibt
        KeineStunde,     // Stunde steht an dem Tag nicht im Plan
        Schulfrei        // Ferien, Feiertag, freier Tag, außerhalb des Schuljahres
    }

    /// <summary>Was gebucht werden soll (aus dem Fenster „Dauervertretung“).</summary>
    public sealed class DauerVorlage
    {
        public string WTag { get; set; } = "";
        public int Stunde { get; set; }
        public int UNr { get; set; }
        public string OriginalLehrer { get; set; } = "";
        public string Fach { get; set; } = "";
        public List<string> Klassen { get; set; } = new();

        public Vertretungsart Art { get; set; } = Vertretungsart.Vertretung;
        public string? Vertreter { get; set; }
        public VertretungsGrund Grund { get; set; } = VertretungsGrund.Krankheit;
        /// <summary>Neuer Raum für alle Tage (R3-D) oder null = Raum laut Plan bzw. der einer bestehenden Buchung.</summary>
        public string? Raum { get; set; }
        /// <summary>Bestehende Buchungen (Entfall, andere Vertretung …) ersetzen?</summary>
        public bool Ersetzen { get; set; } = true;

        public DateTime Von { get; set; }
        public DateTime Bis { get; set; }
        public string SerienId { get; set; } = "";

        public bool BrauchtVertreter => Dauervertretung.BrauchtVertreter(Art);
    }

    /// <summary>Ein Tag der Vorschau.</summary>
    public sealed class DauerTag
    {
        public DateTime Datum { get; set; }
        public DauerTagStatus Status { get; set; }
        public string Text { get; set; } = "";
        /// <summary>Zelle des effektiven Tagesplans (null bei schulfrei bzw. ohne Stunde).</summary>
        public TagesZelle? Zelle { get; set; }
        /// <summary>Raum, der gebucht wird (VertretungsRaum; null = kein Raumwechsel).</summary>
        public string? Raum { get; set; }
        /// <summary>Tag wurde zuletzt aus Untis veröffentlicht (Hinweis).</summary>
        public bool AusUntis { get; set; }

        public bool WirdGebucht => Status == DauerTagStatus.Buchen;
        public bool IstLuecke => Status == DauerTagStatus.LueckeFehlt || Status == DauerTagStatus.LueckeBelegt;
    }

    /// <summary>Überblick über eine gebuchte Dauervertretung (alle Einträge mit derselben SerienId).</summary>
    public sealed class DauerSerie
    {
        public string SerienId { get; set; } = "";
        public DateTime Erster { get; set; }
        public DateTime Letzter { get; set; }
        public int Anzahl { get; set; }
        public int Stunde { get; set; }
        public string Fach { get; set; } = "";
        public List<string> Klassen { get; set; } = new();
        public string OriginalLehrer { get; set; } = "";
        public string? Vertreter { get; set; }
        public Vertretungsart Art { get; set; }

        /// <summary>z. B. „Mo 3. Std M 07C: L52 → L5 (Vertretung), 23.11.–18.12.2026, 4 Std.“</summary>
        public string Anzeige
            => $"{UntisText.Tag(Erster)} {Stunde}. Std {Fach} {string.Join(", ", Klassen)}: {OriginalLehrer}" +
               (string.IsNullOrWhiteSpace(Vertreter) ? "" : $" → {Vertreter}") +
               $" ({Dauervertretung.ArtText(Art)}), {Erster:dd.MM.}–{Letzter:dd.MM.yyyy}, {Anzahl} Std.";

        public override string ToString() => Anzeige;
    }

    public static class Dauervertretung
    {
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

        /// <summary>Arten, die als Dauervertretung angeboten werden (in dieser Reihenfolge).</summary>
        public static readonly Vertretungsart[] Arten =
        {
            Vertretungsart.Vertretung, Vertretungsart.BetreuungAufsicht, Vertretungsart.EVA, Vertretungsart.Entfall
        };

        public static bool BrauchtVertreter(Vertretungsart art)
            => art == Vertretungsart.Vertretung || art == Vertretungsart.BetreuungAufsicht
               || art == Vertretungsart.Zusammenlegung;

        public static string ArtText(Vertretungsart art) => art switch
        {
            Vertretungsart.Vertretung => "Vertretung",
            Vertretungsart.BetreuungAufsicht => "Betreuung/Aufsicht",
            Vertretungsart.EVA => "EVA",
            Vertretungsart.Entfall => "Entfall",
            Vertretungsart.Raumvertretung => "Raumwechsel",
            Vertretungsart.Verlegung => "Verlegung",
            Vertretungsart.Zusammenlegung => "Zusammenlegung",
            _ => art.ToString()
        };

        /// <summary>Kurztext einer Buchung, z. B. „Vertretung L7“ oder „Entfall“.</summary>
        public static string BuchungText(VertretungsEintrag e)
            => ArtText(e.Art) + (string.IsNullOrWhiteSpace(e.VertretungsLehrer) || e.Art == Vertretungsart.Verlegung
                                     ? "" : " " + e.VertretungsLehrer);

        public static string NeueSerienId() => Guid.NewGuid().ToString("N");

        // =================================================================
        //  Vorgaben
        // =================================================================

        /// <summary>
        /// Vorgabe für „bis“: letzter Schultag vor den nächsten Ferien (ganze Schule) nach <paramref name="von"/>,
        /// sonst das Ende des Schuljahres.
        /// </summary>
        public static DateTime BisFerienbeginn(SchuljahrKalender kal, DateTime von)
        {
            var ferien = kal.FreieZeiten
                .Where(f => f.Typ == FreieZeitTyp.Ferien && f.GiltFuerGanzeSchule && f.Von.Date > von.Date)
                .OrderBy(f => f.Von)
                .FirstOrDefault();
            DateTime grenze = ferien != null ? ferien.Von.Date.AddDays(-1) : kal.Ende.Date;
            if (kal.Ende != default && grenze > kal.Ende.Date) grenze = kal.Ende.Date;
            // auf den letzten Schultag davor zurückgehen (höchstens drei Wochen)
            for (int i = 0; i < 21 && grenze > von.Date && !kal.IstSchultag(grenze); i++) grenze = grenze.AddDays(-1);
            return grenze < von.Date ? von.Date : grenze;
        }

        /// <summary>Letzter Schultag des Schuljahres (laut Kalender).</summary>
        public static DateTime BisSchuljahresende(SchuljahrKalender kal, DateTime von)
        {
            DateTime grenze = kal.Ende.Date;
            for (int i = 0; i < 21 && grenze > von.Date && !kal.IstSchultag(grenze); i++) grenze = grenze.AddDays(-1);
            return grenze < von.Date ? von.Date : grenze;
        }

        // =================================================================
        //  Vorschau
        // =================================================================

        /// <summary>
        /// Vorschau aller Tage des Wochentags von–bis. <paramref name="ausUntis"/> (optional) meldet Tage, die
        /// zuletzt aus Untis veröffentlicht wurden.
        /// </summary>
        public static List<DauerTag> Vorschau(DauerVorlage v, PlanDaten plan, VertretungsDaten daten,
            Func<DateTime, bool>? ausUntis = null)
        {
            var result = new List<DauerTag>();
            int wIdx = SchuljahrKalender.WochentagIndex(v.WTag);
            if (wIdx < 0 || v.Bis.Date < v.Von.Date) return result;
            var kal = daten.Kalender;

            for (var d = v.Von.Date; d <= v.Bis.Date; d = d.AddDays(1))
            {
                if ((int)d.DayOfWeek != wIdx) continue;
                var tag = new DauerTag { Datum = d, AusUntis = ausUntis?.Invoke(d) == true };
                result.Add(tag);

                if (!kal.IstSchultag(d))
                {
                    tag.Status = DauerTagStatus.Schulfrei;
                    tag.Text = "schulfrei – " + FreiText(kal, d);
                    continue;
                }

                var tp = TagesplanBerechnung.Berechne(d, plan, kal, daten.Vertretungen);
                if (!tp.IstSchultag)
                {
                    tag.Status = DauerTagStatus.Schulfrei;
                    tag.Text = "schulfrei";
                    continue;
                }
                PruefeTag(v, tag, tp, plan, daten);
            }
            return result;
        }

        private static void PruefeTag(DauerVorlage v, DauerTag tag, Tagesplan tp, PlanDaten plan, VertretungsDaten daten)
        {
            DateTime d = tag.Datum;

            // Die Stunde der Serie an diesem Tag: (Stunde, UNr, ursprüngliche Lehrkraft) – wie die Buchungen.
            var kandidaten = tp.InStunde(v.Stunde)
                .Where(z => z.UNr == v.UNr && Cmp.Equals(z.OriginalLehrer, v.OriginalLehrer))
                .ToList();
            var zelle = kandidaten.FirstOrDefault(z => z.Status != ZellStatus.VerlegtHin) ?? kandidaten.FirstOrDefault();
            if (zelle == null)
            {
                tag.Status = DauerTagStatus.KeineStunde;
                tag.Text = "Stunde steht an diesem Tag nicht im Plan";
                return;
            }
            tag.Zelle = zelle;
            var e = zelle.Eintrag;

            if (zelle.Status == ZellStatus.VerlegtWeg || zelle.Status == ZellStatus.VerlegtHin
                || e?.Art == Vertretungsart.Verlegung)
            {
                tag.Status = DauerTagStatus.Verlegt;
                tag.Text = "Verlegung (" + (string.IsNullOrWhiteSpace(zelle.Info) ? "verlegt" : zelle.Info) + ") – bleibt";
                return;
            }

            // Bestehende Buchung (außer reinem Raumwechsel und schon gleicher Buchung).
            bool gleich = e != null && e.Art == v.Art
                          && Cmp.Equals(e.VertretungsLehrer ?? "", v.BrauchtVertreter ? v.Vertreter ?? "" : "");
            bool ersetzt = e != null && e.Art != Vertretungsart.Raumvertretung && !gleich;
            if (ersetzt && !v.Ersetzen)
            {
                tag.Status = DauerTagStatus.Behalten;
                tag.Text = "bestehende Buchung bleibt: " + BuchungText(e!);
                return;
            }

            // 159: Vertretungskraft fehlt bzw. ist schon belegt → nicht buchen, Lücke mit Hinweis.
            if (v.BrauchtVertreter && !string.IsNullOrWhiteSpace(v.Vertreter))
            {
                string vt = v.Vertreter!;
                string? fehlt = FehltText(vt, d, v.Stunde, tp, daten.Abwesenheiten);
                if (fehlt != null)
                {
                    tag.Status = DauerTagStatus.LueckeFehlt;
                    tag.Text = fehlt + " – Lücke, Stunde bleibt " + (e == null ? "offen" : "wie gebucht (" + BuchungText(e) + ")");
                    return;
                }
                string? belegt = BelegtText(vt, zelle, tp);
                if (belegt != null)
                {
                    tag.Status = DauerTagStatus.LueckeBelegt;
                    tag.Text = belegt + " – Lücke, Stunde bleibt " + (e == null ? "offen" : "wie gebucht (" + BuchungText(e) + ")");
                    return;
                }
            }

            tag.Status = DauerTagStatus.Buchen;
            var teile = new List<string>();
            if (gleich) teile.Add("schon so gebucht – wird Teil der Dauervertretung");
            else if (ersetzt) teile.Add("ersetzt: " + BuchungText(e!));
            else teile.Add("wird gebucht");

            // Raum (R3-D): neuer Raum der Serie, wenn er an dem Tag frei ist; sonst der einer bestehenden Buchung.
            string? bestehend = !string.IsNullOrWhiteSpace(e?.VertretungsRaum) ? e!.VertretungsRaum!.Trim() : null;
            if (v.Art == Vertretungsart.Entfall)
            {
                tag.Raum = null;
                if (bestehend != null) teile.Add($"Raumwechsel {bestehend} entfällt");
            }
            else if (!string.IsNullOrWhiteSpace(v.Raum))
            {
                string raum = v.Raum!.Trim();
                string? warum = RaumNichtFrei(raum, zelle, tp, daten.Abwesenheiten, d);
                if (warum == null || PlanRaeume.GleicherRaum(raum, bestehend))
                {
                    tag.Raum = PlanRaeume.GleicherRaum(raum, zelle.GrundRaum) ? null : raum;
                    if (tag.Raum != null) teile.Add("Raum " + raum);
                }
                else
                {
                    tag.Raum = bestehend;
                    teile.Add($"Raum {raum} {warum} → " + (bestehend != null ? "Raum " + bestehend + " bleibt" : "Raum laut Plan"));
                }
            }
            else
            {
                tag.Raum = bestehend;
                if (bestehend != null) teile.Add("Raum " + bestehend + " bleibt");
            }

            tag.Text = string.Join(" · ", teile);
        }

        /// <summary>Text, warum die Vertretungskraft an dem Tag fehlt, sonst null.</summary>
        private static string? FehltText(string vertreter, DateTime d, int stunde, Tagesplan tp, IEnumerable<Abwesenheit> abw)
        {
            var a = abw.FirstOrDefault(x => x.Subjekt == AbwesenheitsSubjekt.Lehrer && Cmp.Equals(x.Name?.Trim() ?? "", vertreter)
                                            && PlanRaeume.Deckt(x, d, stunde));
            if (a != null) return $"{vertreter} fehlt ({GrundText(a.Grund)}, Abwesenheit {a.VonDatum:dd.MM.}–{a.BisDatum:dd.MM.})";

            // „Lehrer am Bezugstag abwesend“ bucht nur Entfall (ohne Abwesenheit): fallen alle eigenen Stunden aus,
            // fehlt die Lehrkraft vermutlich.
            var eigene = tp.Zellen.Where(z => Cmp.Equals(z.OriginalLehrer, vertreter) && !IstPseudo(z)).ToList();
            if (eigene.Count > 0 && eigene.All(z => z.Status == ZellStatus.Entfall))
                return $"{vertreter} fehlt vermutlich (alle eigenen Stunden entfallen)";
            return null;
        }

        /// <summary>Text, womit die Vertretungskraft in der Stunde schon belegt ist, sonst null.</summary>
        private static string? BelegtText(string vertreter, TagesZelle zelle, Tagesplan tp)
        {
            // UV-, Vber-, NuHo- und ufrei-Stunden geben die Lehrkraft für Vertretungen frei (wie die Vorschläge);
            // PSF belegt sie.
            var c = tp.InStunde(zelle.Stunde).FirstOrDefault(x =>
                !ReferenceEquals(x, zelle) && !x.IstFrei && Cmp.Equals(x.Lehrer, vertreter) && !GibtFrei(x.Fach));
            if (c == null) return null;
            string was = $"{c.Fach} {string.Join(", ", c.Klassen)}".Trim();
            bool eigen = Cmp.Equals(c.OriginalLehrer, vertreter) && c.Status == ZellStatus.Normal;
            return $"{vertreter} ist in der {zelle.Stunde}. Std belegt ({was}" +
                   (eigen ? ", eigener Unterricht" : ", " + (string.IsNullOrWhiteSpace(c.Info) ? "Buchung" : c.Info)) + ")";
        }

        private static string? RaumNichtFrei(string raum, TagesZelle zelle, Tagesplan tp, IEnumerable<Abwesenheit> abw, DateTime d)
        {
            var gesperrt = PlanRaeume.Gesperrt(abw, d, zelle.Stunde);
            var belegtVon = PlanRaeume.BelegtVon(tp, zelle);
            foreach (var r in PlanRaeume.Zerlege(raum))
            {
                if (gesperrt.Contains(r)) return "gesperrt";
                if (belegtVon.TryGetValue(r, out var wer)) return "belegt (" + wer + ")";
            }
            return null;
        }

        private static bool GibtFrei(string? fach)
        {
            string f = (fach ?? "").Trim();
            return TeilInfo.IstUvFach(f) || TeilInfo.IstVberFach(f)
                   || string.Equals(f, "ufrei", StringComparison.OrdinalIgnoreCase)
                   || f.StartsWith("NuHo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IstPseudo(TagesZelle z)
            => Stundenplan_V2.Raumplanung.RaumplanStore.IstOhneRaumbedarf(z.Fach);

        private static string GrundText(VertretungsGrund g) => g switch
        {
            VertretungsGrund.Pruefung => "Prüfung",
            _ => g.ToString()
        };

        private static string FreiText(SchuljahrKalender kal, DateTime d)
        {
            if (d < kal.Start.Date || d > kal.Ende.Date) return "außerhalb des Schuljahres";
            var fz = kal.FreieZeiten.FirstOrDefault(f => f.GiltFuerGanzeSchule && f.Enthaelt(d));
            if (fz == null) return "kein Schultag";
            return string.IsNullOrWhiteSpace(fz.Bezeichnung) ? fz.Typ.ToString() : fz.Bezeichnung;
        }

        // =================================================================
        //  Buchen
        // =================================================================

        /// <summary>
        /// Eintrag für einen Tag der Vorschau – wie eine einzelne Buchung im Wocheneditor (Zählwert der Art; ein
        /// UV-Einsatz zählt nicht, Entscheidung 160), dazu die SerienId.
        /// </summary>
        public static VertretungsEintrag Eintrag(DauerVorlage v, DauerTag t, PlanDaten plan, VertretungsDaten daten)
        {
            var z = t.Zelle ?? throw new InvalidOperationException("Tag ohne Stunde.");
            bool brauchtVertreter = v.BrauchtVertreter;
            string? vertreter = brauchtVertreter && !string.IsNullOrWhiteSpace(v.Vertreter) ? v.Vertreter!.Trim() : null;

            int zaehlwert = brauchtVertreter ? daten.Zaehlwert(v.Art) : 0;
            string? wtag = daten.Kalender.RasterTagFuer(t.Datum);
            if (brauchtVertreter && vertreter != null && wtag != null && plan.HatUvImSlot(vertreter, wtag, z.Stunde))
                zaehlwert = 0;

            return new VertretungsEintrag
            {
                Datum = t.Datum.Date, Stunde = z.Stunde, UNr = z.UNr,
                Art = v.Art, Grund = v.Grund,
                OriginalLehrer = z.OriginalLehrer,
                VertretungsLehrer = vertreter,
                Klassen = new List<string>(z.Klassen), Fach = z.Fach,
                Fachgerecht = (v.Art == Vertretungsart.Vertretung && vertreter != null)
                    ? plan.Fachnah(vertreter, z.Fach) : (bool?)null,
                OriginalRaum = z.GrundRaum,
                VertretungsRaum = v.Art == Vertretungsart.Entfall ? null : t.Raum,
                ZaehlwertSnapshot = zaehlwert,
                SerienId = v.SerienId
            };
        }

        // =================================================================
        //  Serien und Zurücksetzen
        // =================================================================

        /// <summary>Alle Dauervertretungen (gruppiert nach SerienId), nach erstem Tag sortiert.</summary>
        public static List<DauerSerie> Serien(IEnumerable<VertretungsEintrag> alle)
            => alle.Where(e => e.IstDauervertretung)
                   .GroupBy(e => e.SerienId.Trim(), Cmp)
                   .Select(g =>
                   {
                       var l = g.OrderBy(e => e.Datum).ToList();
                       var f = l[0];
                       return new DauerSerie
                       {
                           SerienId = g.Key,
                           Erster = f.Datum.Date,
                           Letzter = l[^1].Datum.Date,
                           Anzahl = l.Count,
                           Stunde = f.Stunde,
                           Fach = f.Fach,
                           Klassen = new List<string>(f.Klassen),
                           OriginalLehrer = f.OriginalLehrer,
                           Vertreter = f.VertretungsLehrer,
                           Art = f.Art
                       };
                   })
                   .OrderBy(s => s.Erster).ThenBy(s => s.Stunde)
                   .ToList();

        /// <summary>Letzter Tag je Serie (für die Anzeige „⟳ bis …“ im Wocheneditor).</summary>
        public static Dictionary<string, DateTime> SerienEnde(IEnumerable<VertretungsEintrag> alle)
        {
            var d = new Dictionary<string, DateTime>(Cmp);
            foreach (var e in alle)
            {
                if (!e.IstDauervertretung) continue;
                string id = e.SerienId.Trim();
                if (!d.TryGetValue(id, out var x) || e.Datum.Date > x) d[id] = e.Datum.Date;
            }
            return d;
        }

        /// <summary>
        /// Buchungen, die ab <paramref name="ab"/> zurückgesetzt werden (Entscheidung 162): entweder einer Serie
        /// (<paramref name="serienId"/>) oder einer Lehrkraft – als abwesende Lehrkraft (für sie gebucht) und/oder als
        /// Vertretungskraft. Bei der Lehrkraft bleiben Verlegungen und reine Raumwechsel.
        /// </summary>
        public static List<VertretungsEintrag> ZumZuruecksetzen(IEnumerable<VertretungsEintrag> alle, DateTime ab,
            string? serienId, string? lehrer, bool alsAbwesende, bool alsVertretung)
        {
            var q = alle.Where(e => e.Datum.Date >= ab.Date);
            if (!string.IsNullOrWhiteSpace(serienId))
                q = q.Where(e => Cmp.Equals(e.SerienId?.Trim() ?? "", serienId.Trim()));
            else if (!string.IsNullOrWhiteSpace(lehrer))
            {
                string l = lehrer.Trim();
                q = q.Where(e => e.Art != Vertretungsart.Verlegung && e.Art != Vertretungsart.Raumvertretung
                                 && ((alsAbwesende && Cmp.Equals(e.OriginalLehrer?.Trim() ?? "", l))
                                     || (alsVertretung && Cmp.Equals(e.VertretungsLehrer?.Trim() ?? "", l))));
            }
            else return new List<VertretungsEintrag>();

            return q.OrderBy(e => e.Datum).ThenBy(e => e.Stunde)
                    .ThenBy(e => string.Join(",", e.Klassen), Cmp).ToList();
        }

        /// <summary>Abwesenheiten der Lehrkraft, die am bzw. nach <paramref name="ab"/> noch laufen.</summary>
        public static List<Abwesenheit> AbwesenheitenAb(IEnumerable<Abwesenheit> alle, string lehrer, DateTime ab)
            => alle.Where(a => a.Subjekt == AbwesenheitsSubjekt.Lehrer
                               && Cmp.Equals(a.Name?.Trim() ?? "", lehrer.Trim())
                               && a.BisDatum.Date >= ab.Date)
                   .OrderBy(a => a.VonDatum).ToList();

        /// <summary>
        /// Lässt die Abwesenheiten am Tag vor <paramref name="ab"/> enden (früher zurück); beginnt eine erst am bzw.
        /// nach dem Datum, wird sie entfernt. Liefert die Zahl der geänderten bzw. entfernten Abwesenheiten.
        /// </summary>
        public static int BeendeAbwesenheiten(List<Abwesenheit> liste, IEnumerable<Abwesenheit> betroffen, DateTime ab)
        {
            int n = 0;
            foreach (var a in betroffen.ToList())
            {
                if (a.VonDatum.Date >= ab.Date)
                {
                    if (liste.Remove(a)) n++;
                    continue;
                }
                a.BisDatum = ab.Date.AddDays(-1);
                a.BisStunde = 0;   // ganzer Tag
                n++;
            }
            return n;
        }

        /// <summary>Zeile der Vorschau „Zurücksetzen“, z. B. „Mo 23.11. 3. Std M 07C (L52) – Vertretung L5 ⟳“.</summary>
        public static string ZeileText(VertretungsEintrag e)
            => $"{UntisText.Tag(e.Datum)} {e.Datum.ToString("dd.MM.", CultureInfo.InvariantCulture)}  {e.Stunde}. Std  " +
               $"{e.Fach} {string.Join(", ", e.Klassen)} ({e.OriginalLehrer}) – {BuchungText(e)}" +
               (string.IsNullOrWhiteSpace(e.VertretungsRaum) ? "" : $", Raum {e.VertretungsRaum}") +
               (e.IstDauervertretung ? "  ⟳" : "");
    }
}
