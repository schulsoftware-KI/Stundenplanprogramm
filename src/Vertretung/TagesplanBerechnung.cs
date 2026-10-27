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
    //  Verlegung (tagesübergreifend):
    //    - Quell-Eintrag  (an Stunde b): Art=Verlegung, VerlegtNach* gesetzt →
    //      L2s Stunde wird von b WEG verlegt (b wird frei).  Status VerlegtWeg.
    //    - Ziel-Eintrag   (an Stunde a): Art=Verlegung, VerlegtVon* gesetzt,
    //      VertretungsLehrer=L2 → an a hält L2 seine (von b) verschobene Stunde
    //      statt L1. Das tatsächlich gehaltene Fach/Klasse kommt vom Quell-
    //      Eintrag (über VerlegungsId, auch an einem anderen Tag). Status
    //      VerlegtHin.
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
        public string? Raum { get; set; }

        public ZellStatus Status { get; set; } = ZellStatus.Normal;
        /// <summary>Kurztext für die Anzeige, z. B. „verlegt von Mi 15.10. Std 5".</summary>
        public string Info { get; set; } = "";

        /// <summary>Zugrundeliegende Buchung (falls vorhanden), sonst null.</summary>
        public VertretungsEintrag? Eintrag { get; set; }

        public bool IstGeaendert => Status != ZellStatus.Normal;
        public bool IstFrei => Status == ZellStatus.Entfall || Status == ZellStatus.VerlegtWeg;
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
            foreach (var kv in plan.Belegung)
            {
                if (!string.Equals(kv.Key.WTag, wtag, StringComparison.OrdinalIgnoreCase)) continue;
                int stunde = kv.Key.Stunde;
                foreach (int unr in kv.Value)
                {
                    if (!plan.Unterrichte.TryGetValue(unr, out var teile)) continue;
                    foreach (var teil in teile)
                    {
                        ergebnis.Zellen.Add(new TagesZelle
                        {
                            Stunde = stunde,
                            UNr = unr,
                            Lehrer = teil.Lehrer,
                            OriginalLehrer = teil.Lehrer,
                            Fach = teil.Fach,
                            Klassen = new List<string>(teil.Klassen),
                            Raum = teil.Stammraum ?? teil.Fachraum,
                            Status = ZellStatus.Normal
                        });
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
                if (zelle == null && e.Art == Vertretungsart.Verlegung && e.VerlegtVonStunde.HasValue)
                {
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
                        if (!string.IsNullOrWhiteSpace(e.VertretungsRaum)) zelle.Raum = e.VertretungsRaum;
                        zelle.Info = "Raumwechsel";
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
                            }
                            zelle.Info = "verlegt von " + SlotText(e.VerlegtVonDatum, e.VerlegtVonStunde, datum);
                        }
                        break;
                }
            }

            ergebnis.Zellen = ergebnis.Zellen
                .OrderBy(z => z.Stunde)
                .ThenBy(z => string.Join(",", z.Klassen))
                .ToList();
            return ergebnis;
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
