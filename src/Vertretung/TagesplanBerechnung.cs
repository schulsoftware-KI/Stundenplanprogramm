#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  TagesplanBerechnung – der "effektive" Tag (Konzept §15, Schritt b).
    //
    //  Legt die Buchungen (VertretungsEinträge) über den Grundplan (PlanLeser)
    //  und liefert pro Stunde/Unterricht eine Zelle mit Status:
    //    Normal | Vertreten | Entfall | VerlegtWeg | VerlegtHin |
    //    Betreuung | EVA | Zusammenlegung | Raumvertretung
    //
    //  Dieselbe Berechnung speist den Tages-Editor, die Tagesübersicht und das
    //  PDF, damit alle drei konsistent sind. Zuordnung Buchung → Grundplan-Zelle
    //  immer über (Stunde, UNr, OriginalLehrer).
    //
    //  Räume (R3-D): Der Raum einer Grundplan-Zelle kommt aus dem Raumplan der
    //  Plan-Spalte (PlanDaten.Raeume), sonst wie in der App Fachraum vor
    //  Stammraum laut UV (GrundRaum). Eine Buchung mit VertretungsRaum ändert
    //  den Raum – bei jeder Art außer Entfall/verlegt weg (RaumGeaendert).
    //
    //  Verlegung (tagesübergreifend):
    //    - Quell-Eintrag  (an Stunde b): Art=Verlegung, VerlegtNach* gesetzt →
    //      L2s Stunde wird von b WEG verlegt (b wird frei).  Status VerlegtWeg.
    //    - Ziel-Eintrag   (an Stunde a): Art=Verlegung, VerlegtVon* gesetzt,
    //      VertretungsLehrer=L2 → an a hält L2 seine (von b) verschobene Stunde
    //      statt L1. Das tatsächlich gehaltene Fach/Klasse kommt vom Quell-
    //      Eintrag (über VerlegungsId, auch an einem anderen Tag). Status
    //      VerlegtHin.
    //
    //  V4b (276): Freie Zeiten einzelner Klassen laut Kalender. Eine Stunde,
    //  deren Klassen alle frei sind und die nicht gebucht ist, bleibt Status
    //  Normal (geht also NICHT an die App – die App zeigt „Frei“ über den
    //  Kalender, und nicht ins PDF), trägt aber FreiLautKalender und den Text
    //  „frei (Bezeichnung)“ – der Wocheneditor zeigt sie grau. Ein Sondergrund
    //  an einer solchen Stunde (Eintrag Art Entfall, Anlass FreieZeit) ändert
    //  daran nichts. Jede andere Buchung geht vor.
    // =====================================================================

    public enum ZellStatus
    {
        Normal,
        Vertreten,
        Entfall,
        VerlegtWeg,     // Stunde von hier weg verlegt (frei)
        VerlegtHin,     // hierher verlegte Stunde
        Betreuung,
        EVA,
        Zusammenlegung,
        Raumvertretung
    }

    /// <summary>Eine effektive Zelle des Tagesplans (eine Stunde eines Unterrichts).</summary>
    public class TagesZelle
    {
        public int Stunde { get; set; }
        public int UNr { get; set; }

        /// <summary>Effektive Lehrkraft (bei Vertretung/VerlegtHin die neue).</summary>
        public string Lehrer { get; set; } = "";
        /// <summary>Ursprüngliche Lehrkraft (bei Vertretung/Entfall die ausgefallene).</summary>
        public string OriginalLehrer { get; set; } = "";

        public string Fach { get; set; } = "";
        public List<string> Klassen { get; set; } = new();
        /// <summary>Effektiver Raum (bei einer Buchung mit Raumänderung der neue; mehrere mit „, “).</summary>
        public string? Raum { get; set; }
        /// <summary>R3-D: Raum laut Grundplan (Raumplan, sonst Fachraum vor Stammraum); null bei Zellen ohne Grundplan-Stunde.</summary>
        public string? GrundRaum { get; set; }

        public ZellStatus Status { get; set; } = ZellStatus.Normal;
        /// <summary>Kurztext für die Anzeige, z. B. „verlegt von Mi 15.10. Std 5".</summary>
        public string Info { get; set; } = "";

        /// <summary>Zugrundeliegende Buchung (falls vorhanden), sonst null.</summary>
        public VertretungsEintrag? Eintrag { get; set; }

        /// <summary>
        /// V4b (276): Die Klassen der Stunde haben laut Kalender frei (z. B. Stufenfahrt) – abgeleiteter Entfall der
        /// Lehrkraft laut Plan. Status bleibt Normal (nicht in die App); <see cref="Eintrag"/> ist null bzw. ein
        /// Sondergrund (Art Entfall, Anlass FreieZeit).
        /// </summary>
        public KalenderFrei? FreiLautKalender { get; set; }

        public bool IstGeaendert => Status != ZellStatus.Normal;
        public bool IstFrei => Status == ZellStatus.Entfall || Status == ZellStatus.VerlegtWeg;

        /// <summary>
        /// R3-D: Die Buchung ändert den Raum (VertretungsRaum gesetzt und anders als der Raum laut Grundplan).
        /// Wird beim „Online veröffentlichen“ als <c>raumGeaendert</c> gemeldet.
        /// </summary>
        public bool RaumGeaendert => !IstFrei && !string.IsNullOrWhiteSpace(Eintrag?.VertretungsRaum)
                                     && !PlanRaeume.GleicherRaum(Raum, GrundRaum);
    }

    /// <summary>Der effektive Tagesplan (alle Zellen des Tages + Hilfszugriffe).</summary>
    public class Tagesplan
    {
        public DateTime Datum { get; set; }
        public string WTag { get; set; } = "";
        public bool IstSchultag { get; set; }
        public List<TagesZelle> Zellen { get; set; } = new();

        public IEnumerable<int> Stunden
            => Zellen.Select(z => z.Stunde).Distinct().OrderBy(s => s);

        public IEnumerable<string> KlassenDesTages
            => Zellen.SelectMany(z => z.Klassen).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        /// <summary>Alle am Tag beteiligten Lehrkräfte (effektiv UND ursprünglich).</summary>
        public IEnumerable<string> LehrerDesTages
            => Zellen.SelectMany(z => new[] { z.Lehrer, z.OriginalLehrer })
                     .Where(l => !string.IsNullOrWhiteSpace(l))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        public IEnumerable<TagesZelle> FuerKlasse(string klasse)
            => Zellen.Where(z => z.Klassen.Contains(klasse, StringComparer.OrdinalIgnoreCase))
                     .OrderBy(z => z.Stunde);

        /// <summary>Zellen, in denen die Lehrkraft effektiv unterrichtet ODER ursprünglich stand.</summary>
        public IEnumerable<TagesZelle> FuerLehrer(string lehrer)
            => Zellen.Where(z =>
                        string.Equals(z.Lehrer, lehrer, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(z.OriginalLehrer, lehrer, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(z => z.Stunde);

        public IEnumerable<TagesZelle> InStunde(int stunde)
            => Zellen.Where(z => z.Stunde == stunde);
    }

    public static class TagesplanBerechnung
    {
        /// <summary>
        /// Berechnet den effektiven Tagesplan. <paramref name="alleEintraege"/> sind
        /// ALLE Buchungen (für Verlegungs-Partner über VerlegungsId, ggf. anderer
        /// Tag); angewandt werden nur die mit Datum == <paramref name="datum"/>.
        /// </summary>
        public static Tagesplan Berechne(
            DateTime datum, PlanDaten plan, SchuljahrKalender kal,
            IEnumerable<VertretungsEintrag> alleEintraege)
        {
            var ergebnis = new Tagesplan { Datum = datum.Date };

            if (!kal.IstSchultag(datum))
            {
                ergebnis.IstSchultag = false;
                return ergebnis;
            }
            string? wtag = kal.RasterTagFuer(datum);
            if (wtag == null)
            {
                ergebnis.IstSchultag = false;
                return ergebnis;
            }
            ergebnis.IstSchultag = true;
            ergebnis.WTag = wtag;

            var alle = alleEintraege.ToList();

            // Verlegungs-Partner über die ganze Buchungsmenge (tagesübergreifend).
            var verlegungById = alle
                .Where(e => !string.IsNullOrWhiteSpace(e.VerlegungsId))
                .GroupBy(e => e.VerlegungsId)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            VertretungsEintrag? Partner(VertretungsEintrag e)
                => string.IsNullOrWhiteSpace(e.VerlegungsId) || !verlegungById.TryGetValue(e.VerlegungsId, out var l)
                    ? null
                    : l.FirstOrDefault(x => !ReferenceEquals(x, e));

            // 1) Grundplan-Zellen des Tages aufbauen.
            var grundZellen = new List<TagesZelle>();
            foreach (var kv in plan.Belegung)
            {
                if (!string.Equals(kv.Key.WTag, wtag, StringComparison.OrdinalIgnoreCase)) continue;
                int stunde = kv.Key.Stunde;
                foreach (int unr in kv.Value)
                {
                    if (!plan.Unterrichte.TryGetValue(unr, out var teile)) continue;
                    foreach (var teil in teile)
                    {
                        // R3-D: Raumplan der Plan-Spalte, sonst Fachraum vor Stammraum (wie die App).
                        string? raum = PlanRaeume.RaumVon(plan, kv.Key.WTag, stunde, unr, teil);
                        var grundZelle = new TagesZelle
                        {
                            Stunde = stunde,
                            UNr = unr,
                            Lehrer = teil.Lehrer,
                            OriginalLehrer = teil.Lehrer,
                            Fach = teil.Fach,
                            Klassen = new List<string>(teil.Klassen),
                            Raum = raum,
                            GrundRaum = raum,
                            Status = ZellStatus.Normal
                        };
                        ergebnis.Zellen.Add(grundZelle);
                        grundZellen.Add(grundZelle);
                    }
                }
            }

            // 2) Buchungen dieses Tages über die passenden Zellen legen.
            foreach (var e in alle.Where(e => e.Datum.Date == datum.Date))
            {
                var zelle = ergebnis.Zellen.FirstOrDefault(z =>
                    z.Stunde == e.Stunde && z.UNr == e.UNr &&
                    string.Equals(z.OriginalLehrer, e.OriginalLehrer, StringComparison.OrdinalIgnoreCase));

                // Ziel einer Verlegung ohne passende Grundzelle: trotzdem anzeigen.
                bool neueZelle = false;
                if (zelle == null && e.Art == Vertretungsart.Verlegung && e.VerlegtVonStunde.HasValue)
                {
                    neueZelle = true;
                    zelle = new TagesZelle
                    {
                        Stunde = e.Stunde,
                        UNr = e.UNr,
                        Lehrer = e.OriginalLehrer,
                        OriginalLehrer = e.OriginalLehrer,
                        Fach = e.Fach,
                        Klassen = new List<string>(e.Klassen)
                    };
                    ergebnis.Zellen.Add(zelle);
                }
                if (zelle == null) continue;

                zelle.Eintrag = e;
                // V4b (276/277): Sondergrund an einer freien Stunde laut Kalender – bleibt „frei“ (unten), keine Änderung.
                if (e.Art == Vertretungsart.Entfall && e.Anlass == EntfallAnlass.FreieZeit) continue;
                switch (e.Art)
                {
                    case Vertretungsart.Entfall:
                        zelle.Status = ZellStatus.Entfall;
                        zelle.Info = "entfällt";
                        break;

                    case Vertretungsart.Vertretung:
                        zelle.Status = ZellStatus.Vertreten;
                        if (!string.IsNullOrWhiteSpace(e.VertretungsLehrer)) zelle.Lehrer = e.VertretungsLehrer!;
                        zelle.Info = e.Fachgerecht == false ? "Vertretung (fachfremd)" : "Vertretung";
                        break;

                    case Vertretungsart.BetreuungAufsicht:
                        zelle.Status = ZellStatus.Betreuung;
                        if (!string.IsNullOrWhiteSpace(e.VertretungsLehrer)) zelle.Lehrer = e.VertretungsLehrer!;
                        zelle.Info = "Betreuung/Aufsicht";
                        break;

                    case Vertretungsart.EVA:
                        zelle.Status = ZellStatus.EVA;
                        if (!string.IsNullOrWhiteSpace(e.VertretungsLehrer)) zelle.Lehrer = e.VertretungsLehrer!;
                        zelle.Info = "Eigenverantw. Arbeiten";
                        break;

                    case Vertretungsart.Zusammenlegung:
                        zelle.Status = ZellStatus.Zusammenlegung;
                        if (!string.IsNullOrWhiteSpace(e.VertretungsLehrer)) zelle.Lehrer = e.VertretungsLehrer!;
                        zelle.Info = "Zusammenlegung";
                        break;

                    case Vertretungsart.Raumvertretung:
                        zelle.Status = ZellStatus.Raumvertretung;
                        zelle.Info = "Raumwechsel";   // Raum: unten (R3-D, für alle Arten)
                        break;

                    case Vertretungsart.Verlegung:
                        var partner = Partner(e);
                        if (e.VerlegtNachStunde.HasValue)
                        {
                            // Quell-Seite: Stunde wird weg verlegt → hier frei.
                            zelle.Status = ZellStatus.VerlegtWeg;
                            zelle.Info = "verlegt nach " + SlotText(e.VerlegtNachDatum, e.VerlegtNachStunde, datum);
                        }
                        else // VerlegtVon → Ziel-Seite: hierher verlegte Stunde.
                        {
                            zelle.Status = ZellStatus.VerlegtHin;
                            if (!string.IsNullOrWhiteSpace(e.VertretungsLehrer)) zelle.Lehrer = e.VertretungsLehrer!;
                            if (partner != null)
                            {
                                zelle.Fach = partner.Fach;
                                if (partner.Klassen.Count > 0) zelle.Klassen = new List<string>(partner.Klassen);
                                zelle.UNr = partner.UNr;
                                // R3-D: Verlegung auf eine freie Stunde – Raum der verlegten Stunde
                                // (auf eine belegte Stunde bleibt der Raum dieser Stunde, wie in der App).
                                if (neueZelle) zelle.Raum = RaumDerStunde(plan, partner);
                            }
                            zelle.Info = "verlegt von " + SlotText(e.VerlegtVonDatum, e.VerlegtVonStunde, datum);
                        }
                        break;
                }

                // R3-D: Raumänderung der Buchung (Raumvertretung, aber auch Vertretung, Betreuung, EVA,
                // Zusammenlegung, hierher verlegt). Entfall/verlegt weg: kein Raum.
                if (!zelle.IstFrei && !string.IsNullOrWhiteSpace(e.VertretungsRaum))
                    zelle.Raum = e.VertretungsRaum!.Trim();
            }

            // 3) V4b (276): freie Zeiten einzelner Klassen – ungebuchte Grundplan-Stunden bzw. solche mit Sondergrund.
            if (kal.FreieZeiten.Any(f => !f.GiltFuerGanzeSchule))
            {
                foreach (var z in grundZellen)
                {
                    var e = z.Eintrag;
                    bool sondergrundFrei = e != null && e.Art == Vertretungsart.Entfall && e.Anlass == EntfallAnlass.FreieZeit;
                    if (e != null && !sondergrundFrei) continue;
                    if (!KalenderEntfaelle.IstUnterricht(z.Fach, z.Klassen)) continue;
                    var frei = KalenderEntfaelle.FreiFuer(kal, datum, z.Klassen);
                    if (frei == null && sondergrundFrei)
                        frei = new KalenderFrei
                        {
                            Bezeichnung = (string.IsNullOrWhiteSpace(e!.Bemerkung) ? "freie Zeit" : e.Bemerkung.Trim()) +
                                          ", nicht mehr im Kalender",
                            Wert = 0
                        };
                    if (frei == null) continue;
                    z.FreiLautKalender = frei;
                    z.Info = $"frei ({frei.Bezeichnung})";
                }
            }
            else
            {
                // Sondergrund an einer freien Stunde, deren Eintrag inzwischen aus dem Kalender entfernt wurde.
                foreach (var z in grundZellen.Where(x => x.Eintrag?.Art == Vertretungsart.Entfall
                                                         && x.Eintrag.Anlass == EntfallAnlass.FreieZeit))
                {
                    z.FreiLautKalender = new KalenderFrei
                    {
                        Bezeichnung = (string.IsNullOrWhiteSpace(z.Eintrag!.Bemerkung) ? "freie Zeit" : z.Eintrag.Bemerkung.Trim()) +
                                      ", nicht mehr im Kalender",
                        Wert = 0
                    };
                    z.Info = $"frei ({z.FreiLautKalender.Bezeichnung})";
                }
            }

            ergebnis.Zellen = ergebnis.Zellen
                .OrderBy(z => z.Stunde)
                .ThenBy(z => string.Join(",", z.Klassen))
                .ToList();
            return ergebnis;
        }

        /// <summary>Raum einer Buchung an ihrer Grundplan-Stelle (Wochentag aus dem Datum, Teil über UNr + Lehrkraft).</summary>
        private static string? RaumDerStunde(PlanDaten plan, VertretungsEintrag e)
        {
            var teil = PlanRaeume.TeilFuer(plan, e.UNr, e.OriginalLehrer, e.Fach);
            if (teil == null) return null;
            string? wtag = plan.RasterTage.FirstOrDefault(t => SchuljahrKalender.WochentagIndex(t) == (int)e.Datum.DayOfWeek);
            return wtag == null ? PlanRaeume.Rueckfall(teil) : PlanRaeume.RaumVon(plan, wtag, e.Stunde, e.UNr, teil);
        }

        private static string SlotText(DateTime? d, int? stunde, DateTime bezugstag)
        {
            string std = stunde.HasValue ? $"Std {stunde.Value}" : "";
            if (d.HasValue && d.Value.Date != bezugstag.Date)
                return $"{d.Value:ddd dd.MM.} {std}".Trim();
            return std;
        }
    }
}
