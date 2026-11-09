#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Stundenplan_V2.Raumplanung
{
    // =====================================================================
    //  RaumVerteilung – weist den Unterrichtsteilen einer Plan-Spalte Räume zu.
    //
    //  Bedarf: je Unterrichtsteil jeder Raum aus „Fachraum" (bei „A~B"/„A,B" alle,
    //  sie werden gleichzeitig gebraucht); ohne Fachraum jeder Stammraum; ohne
    //  Angabe bei genau einer Sek-I-Klasse deren Stammraum (aus der UV abgeleitet).
    //  Teile derselben UNr mit demselben Raum teilen ihn (Team-Teaching).
    //
    //  Je Zeitslot (Tage in Lös-Reihenfolge, Stunden aufsteigend):
    //    0. Zeilen mit Fix (X) aus dem bisherigen Raumplan (immer), danach im
    //       Modus „behalten und ergänzen" die übrigen passenden Zeilen.
    //    1a. Doppelstunde: Raum der Vorstunde behalten (außer er ist der Wunschraum
    //        eines anderen Unterrichts dieser Stunde).
    //    1. Wunschraum für alle (Reihenfolge: Sonderraum, Fachraum mit Fachgruppe,
    //       übrige Fachräume, Stammraum/abgeleitet, Zusatzraum; bei Gleichstand
    //       Fortsetzung der Vorstunde, Doppelstunde, mehr Wochenstunden, kleinere UNr).
    //    2. Raum der Vorstunde (Doppelstunde bleibt im selben Raum).
    //    3. anderer Raum derselben Fachgruppe.
    //    4. Stammraum laut UV.
    //    5. freier Ausweichraum (Blatt „Räume": „als Ausweich"), zuerst im selben
    //       Bereich, sonst in Verzeichnis-Reihenfolge.
    //  Sonderräume (z. B. Schwimmbad) werden nie ersetzt und nie als Ausweich
    //  genommen. Steht neben einem Sonderraum ein Fachraum (RSh1~RSb1), ist dieser
    //  ein Zusatzraum (Halle zum gelegentlichen Ausweichen): er wird nach allen
    //  anderen verteilt und nur durch einen Raum derselben Fachgruppe ersetzt.
    //  A- und B-Woche dürfen denselben Raum belegen.
    // =====================================================================
    public static class RaumVerteilung
    {
        private static readonly Regex SekIKlasse = new(@"^\d{2}[A-Za-z]{1,2}$", RegexOptions.Compiled);
        private static readonly HashSet<string> Oberstufe = new(StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

        // =====================================================
        //  Raumverzeichnis
        // =====================================================

        /// <summary>
        /// Raumverzeichnis für die Verteilung: das Blatt „Räume" (fehlende Räume aus der UV
        /// werden ergänzt) bzw. – ohne Blatt – komplett aus der UV erkannt.
        /// </summary>
        public static RaumVerzeichnis BaueVerzeichnis(RaumEingabe e)
        {
            var erkannt = ErkenneRaeume(e);
            RaumVerzeichnis v;
            if (e.VerzeichnisAusBlatt != null)
            {
                v = e.VerzeichnisAusBlatt;
                foreach (var r in erkannt)
                    if (!v.Enthaelt(r.Name)) { r.Ergaenzt = true; r.QuelleText = "aus UV ergänzt"; v.Hinzu(r); }
                var erg = v.Ergaenzte.Select(r => r.Name).ToList();
                if (erg.Count > 0)
                    v.Hinweise.Add($"{erg.Count} Räume aus der UV fehlen im Blatt „Räume“ – sie werden wie automatisch " +
                                   $"erkannt behandelt und beim Speichern unten angehängt: {Liste(erg)}.");
            }
            else
            {
                v = new RaumVerzeichnis { AusBlatt = false };
                foreach (var r in erkannt) v.Hinzu(r);
                v.Hinweise.Add($"Das Blatt „Räume“ fehlt – {v.Raeume.Count} Räume wurden aus der UV erkannt. " +
                               "Es wird beim Speichern angelegt; bitte danach in Excel prüfen (Art, Fachgruppe, " +
                               "als Ausweich, Bereich, Reihenfolge).");
            }

            foreach (var kv in e.FgrAnzahl)
            {
                var namen = v.Raeume.Where(r => r.Art != RaumArt.Sonderraum && Cmp.Equals(r.Fachgruppe, kv.Key))
                                    .Select(r => r.Name).ToList();
                if (namen.Count != kv.Value)
                    v.Hinweise.Add($"Fachgruppe {kv.Key}: FGR nennt {kv.Value} Fachräume, im Raumverzeichnis stehen " +
                                   $"{namen.Count}{(namen.Count > 0 ? " (" + string.Join(", ", namen) + ")" : "")}.");
            }
            return v;
        }

        private class RaumStat
        {
            public string Name = "";
            public int EinzelFach;                                      // als einziger Fachraum genannt
            public readonly Dictionary<string, int> Fachgruppen = new(StringComparer.OrdinalIgnoreCase);
            public bool InMehrfach;                                     // in „A~B" genannt
            public bool AlsStamm;
            public int Oberstufe, SekI;
            public readonly Dictionary<string, int> Jahrgaenge = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Erkennt Art, Fachgruppe und Bereich der Räume aus der UV (und FGR).</summary>
        public static List<RaumInfo> ErkenneRaeume(RaumEingabe e)
        {
            var stats = new Dictionary<string, RaumStat>(StringComparer.OrdinalIgnoreCase);
            RaumStat Stat(string n)
            {
                if (!stats.TryGetValue(n, out var s)) stats[n] = s = new RaumStat { Name = n };
                return s;
            }

            foreach (var teil in e.Unterrichte.Values.SelectMany(x => x))
            {
                string fg = ExcelLoader.BestimmeFachgruppe(teil.Fach, e.FgrPraefixe);
                bool fgGueltig = fg.Length > 0 && (e.FgrAnzahl.ContainsKey(fg) || e.FgrPraefixe.ContainsKey(fg));

                foreach (var r in teil.Fachraeume)
                {
                    var s = Stat(r);
                    if (teil.Fachraeume.Count == 1)
                    {
                        s.EinzelFach++;
                        if (fgGueltig) s.Fachgruppen[fg] = s.Fachgruppen.TryGetValue(fg, out int n) ? n + 1 : 1;
                    }
                    else s.InMehrfach = true;
                }
                foreach (var r in teil.Stammraeume)
                {
                    var s = Stat(r);
                    s.AlsStamm = true;
                    foreach (var kl in teil.Klassen)
                    {
                        if (Oberstufe.Contains(kl)) s.Oberstufe++;
                        else if (SekIKlasse.IsMatch(kl))
                        {
                            s.SekI++;
                            string jg = kl.Substring(0, 2);
                            s.Jahrgaenge[jg] = s.Jahrgaenge.TryGetValue(jg, out int n) ? n + 1 : 1;
                        }
                    }
                }
            }

            var liste = new List<(int Rang, RaumInfo Info)>();
            foreach (var s in stats.Values)
            {
                var info = new RaumInfo { Name = s.Name, QuelleText = "aus UV erkannt" };
                var besteFg = s.Fachgruppen.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, Cmp).FirstOrDefault();
                int rang;
                if (s.EinzelFach == 0 && s.InMehrfach && !s.AlsStamm)
                {
                    info.Art = RaumArt.Sonderraum;
                    info.Bemerkung = "nur zusammen mit anderen Räumen angegeben (z. B. Schwimmbad) – bitte prüfen";
                    rang = 4;
                }
                else if (besteFg.Value > 0 && besteFg.Value * 2 >= s.EinzelFach)
                {
                    info.Art = RaumArt.Fachraum;
                    info.Fachgruppe = besteFg.Key;
                    rang = 2;
                }
                else if (s.AlsStamm)
                {
                    info.AlsAusweich = true;
                    if (s.Oberstufe > s.SekI)
                    {
                        info.Art = RaumArt.Kursraum;
                        info.Bereich = "Oberstufe";
                        rang = 1;
                    }
                    else
                    {
                        info.Art = RaumArt.Klassenraum;
                        var jg = s.Jahrgaenge.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, Cmp).FirstOrDefault();
                        info.Bereich = jg.Value > 0 ? "Jg " + jg.Key : "";
                        rang = 0;
                    }
                }
                else
                {
                    info.Art = RaumArt.Fachraum;      // Fachraum ohne Fachgruppe (z. B. Erdkunde, Religion)
                    info.AlsAusweich = true;
                    rang = 3;
                }
                liste.Add((rang, info));
            }

            return liste
                .OrderBy(x => x.Rang)
                .ThenBy(x => x.Info.Bereich.Length == 0 ? 1 : 0)
                .ThenBy(x => x.Info.Bereich, Cmp)
                .ThenBy(x => x.Info.Fachgruppe, Cmp)
                .ThenBy(x => x.Info.Name, Cmp)
                .Select(x => x.Info)
                .ToList();
        }

        // =====================================================
        //  Verteilung
        // =====================================================

        private class Bedarf
        {
            public int UNr;
            public string WochenGruppe = "";
            public string Wunsch = "";
            public string WunschHerkunft = RaumHerkunft.Fachraum;
            public int Klasse;                 // 0 Sonderraum · 1 Fachraum mit Fachgruppe · 2 Fachraum · 3 Stammraum/abgeleitet · 4 Zusatzraum
            public bool IstSonder;
            public bool IstZusatz;
            public string Fachgruppe = "";
            public List<RaumTeil> Teile = new();
            public List<string> Fachgruppenraeume = new();   // Kandidaten Schritt 3
            public List<string> Stammraeume = new();         // Kandidaten Schritt 4
            public string Bereich = "";
            public string Schluessel = "";                   // UNr|RAUM – gleich über alle Stunden

            // je Slot
            public string? Raum;
            public string Herkunft = "";
            public string Hinweis = "";
            public bool Uebernommen;
            public bool Fortsetzung;
            public bool Doppel;
            public int Wochenstunden;
            public readonly List<RaumZeile> FesteZeilen = new();

            public Bedarf Kopie() => new()
            {
                UNr = UNr, WochenGruppe = WochenGruppe, Wunsch = Wunsch, WunschHerkunft = WunschHerkunft,
                Klasse = Klasse, IstSonder = IstSonder, IstZusatz = IstZusatz, Fachgruppe = Fachgruppe,
                Teile = Teile, Fachgruppenraeume = Fachgruppenraeume, Stammraeume = Stammraeume,
                Bereich = Bereich, Schluessel = Schluessel
            };
        }

        private class Vorlage
        {
            public readonly List<Bedarf> Bedarfe = new();
            public readonly List<RaumTeil> OhneAngabe = new();
        }

        /// <summary>Belegte Räume eines Slots, getrennt nach A- und B-Woche.</summary>
        private class Belegung
        {
            private readonly Dictionary<string, (bool A, bool B, string Durch, int UNr)> _m = new(StringComparer.OrdinalIgnoreCase);

            public bool Frei(string raum, string wg)
            {
                if (!_m.TryGetValue(raum, out var x)) return true;
                return wg == "A" ? !x.A : wg == "B" ? !x.B : !x.A && !x.B;
            }

            public void Belege(string raum, string wg, string durch, int unr)
            {
                if (_m.TryGetValue(raum, out var x))
                    _m[raum] = (x.A || wg != "B", x.B || wg != "A", x.Durch, x.UNr);
                else
                    _m[raum] = (wg != "B", wg != "A", durch, unr);
            }

            public string Durch(string raum) => _m.TryGetValue(raum, out var x) ? x.Durch : "";

            /// <summary>UNr, die den Raum zuerst belegt hat (0 = frei).</summary>
            public int UNrIn(string raum) => _m.TryGetValue(raum, out var x) ? x.UNr : 0;
        }

        private readonly record struct Zuweisung(string Raum, string Herkunft, string Hinweis);

        public static RaumErgebnis Verteile(RaumEingabe e, RaumVerzeichnis v, bool behalten)
        {
            var erg = new RaumErgebnis { PlanSpalte = e.PlanSpalte, Behalten = behalten };

            var tagRang = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < e.Tage.Count; i++) tagRang.TryAdd(e.Tage[i], i);
            string SK(string tag, int stunde) => tag.ToUpperInvariant() + "|" + stunde;

            var slotUnrs = new Dictionary<string, HashSet<int>>();
            var wochenstunden = new Dictionary<int, int>();
            foreach (var s in e.Slots)
            {
                if (!slotUnrs.TryGetValue(SK(s.WTag, s.Stunde), out var set))
                    slotUnrs[SK(s.WTag, s.Stunde)] = set = new HashSet<int>();
                foreach (int u in s.UNrn)
                    if (set.Add(u)) wochenstunden[u] = wochenstunden.TryGetValue(u, out int n) ? n + 1 : 1;
            }
            bool ImSlot(string tag, int stunde, int unr)
                => slotUnrs.TryGetValue(SK(tag, stunde), out var set) && set.Contains(unr);

            var alteJeSlot = e.Raumplan
                .Where(z => Cmp.Equals(z.Plan, e.PlanSpalte))
                .GroupBy(z => SK(z.WTag, z.Stunde))
                .ToDictionary(g => g.Key, g => g.ToList());

            var klassenStamm = KlassenStammraeume(e);
            var vorlagen = new Dictionary<int, Vorlage>();
            var ausweichCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var ergebnisJeSlot = new Dictionary<string, Dictionary<string, Zuweisung>>();
            var unbekannt = new SortedSet<int>();

            List<string> AusweichRaeume(string bereich)
            {
                if (!ausweichCache.TryGetValue(bereich, out var l))
                    ausweichCache[bereich] = l = v.Raeume
                        .Where(r => r.AlsAusweich && r.Art != RaumArt.Sonderraum)
                        .OrderBy(r => bereich.Length > 0 && Cmp.Equals(r.Bereich, bereich) ? 0 : 1)
                        .ThenBy(r => r.Reihenfolge)
                        .Select(r => r.Name).ToList();
                return l;
            }

            bool IstKandidat(Bedarf b, string raum)
            {
                if (Cmp.Equals(raum, b.Wunsch)) return true;
                if (b.IstSonder) return false;
                if (b.Fachgruppenraeume.Contains(raum, Cmp)) return true;
                if (b.IstZusatz) return false;
                if (b.Stammraeume.Contains(raum, Cmp)) return true;
                var info = v.Finde(raum);
                return info != null && info.AlsAusweich && info.Art != RaumArt.Sonderraum;
            }

            var slots = e.Slots
                .Select((s, i) => (s, i))
                .OrderBy(x => tagRang.TryGetValue(x.s.WTag, out int r) ? r : 99)
                .ThenBy(x => x.s.Stunde)
                .ThenBy(x => x.i)
                .Select(x => x.s)
                .ToList();

            foreach (var slot in slots)
            {
                string sk = SK(slot.WTag, slot.Stunde);
                var belegung = new Belegung();
                var bedarfe = new List<Bedarf>();
                var ohneAngabe = new List<RaumTeil>();
                ergebnisJeSlot.TryGetValue(SK(slot.WTag, slot.Stunde - 1), out var vorstunde);

                foreach (int unr in slot.UNrn)
                {
                    if (!e.Unterrichte.ContainsKey(unr)) { unbekannt.Add(unr); continue; }
                    if (!vorlagen.TryGetValue(unr, out var vl))
                        vorlagen[unr] = vl = BaueVorlage(unr, e.Unterrichte[unr], v, klassenStamm);
                    foreach (var vb in vl.Bedarfe)
                    {
                        var b = vb.Kopie();
                        b.Doppel = ImSlot(slot.WTag, slot.Stunde - 1, unr) || ImSlot(slot.WTag, slot.Stunde + 1, unr);
                        b.Wochenstunden = wochenstunden.TryGetValue(unr, out int n) ? n : 0;
                        b.Fortsetzung = vorstunde != null && vorstunde.TryGetValue(b.Schluessel, out var vz)
                                        && Cmp.Equals(vz.Raum, b.Wunsch);
                        bedarfe.Add(b);
                    }
                    ohneAngabe.AddRange(vl.OhneAngabe);
                }

                string DurchText(Bedarf b)
                {
                    var t = b.Teile[0];
                    return $"UNr {b.UNr} {t.Fach} {t.KlassenText}".Trim();
                }
                void Weise(Bedarf b, string raum, string herkunft, string hinweis)
                {
                    b.Raum = raum; b.Herkunft = herkunft; b.Hinweis = hinweis;
                    belegung.Belege(raum, b.WochenGruppe, DurchText(b), b.UNr);
                }
                string Belegt(Bedarf b)
                {
                    string d = belegung.Durch(b.Wunsch);
                    return d.Length > 0 ? $"{b.Wunsch} belegt ({d})" : $"{b.Wunsch} belegt";
                }

                alteJeSlot.TryGetValue(sk, out var alte);
                alte ??= new List<RaumZeile>();
                var extra = new List<RaumZeile>();

                // ---- 0a. Feste Zeilen (Fix X) ----
                foreach (var fz in alte.Where(z => z.Fix))
                {
                    var teile = e.Unterrichte.TryGetValue(fz.UNr, out var tl) ? tl : null;
                    if (!slot.UNrn.Contains(fz.UNr) || teile == null || !teile.Any(t => Cmp.Equals(t.Lehrer, fz.Lehrer)))
                    {
                        erg.FesteVerworfen++;
                        if (erg.FesteVerworfen <= 20)
                            erg.Meldungen.Add($"Feste Zeile passt nicht mehr zum Plan und entfällt: {fz.WTag} {fz.Stunde}. " +
                                              $"Std, UNr {fz.UNr} {fz.Lehrer}, Raum {fz.Raum}.");
                        continue;
                    }
                    var kopie = Kopie(fz);
                    kopie.Herkunft = RaumHerkunft.Fest;
                    string wg = teile[0].WochenGruppe;
                    if (kopie.Raum.Length > 0)
                    {
                        if (!belegung.Frei(kopie.Raum, wg) && belegung.UNrIn(kopie.Raum) != fz.UNr)
                        {
                            erg.FesteKonflikte++;
                            kopie.Hinweis = $"Konflikt: {kopie.Raum} ist auch fest belegt ({belegung.Durch(kopie.Raum)})";
                        }
                        belegung.Belege(kopie.Raum, wg, $"UNr {fz.UNr} {fz.Fach} {fz.Klassen}".Trim(), fz.UNr);
                    }

                    // Passender Bedarf (gleiche UNr, Lehrkraft, Wunschraum); bei Team-Teaching
                    // können mehrere Lehrkräfte desselben Bedarfs eine feste Zeile haben.
                    var bf = bedarfe.FirstOrDefault(b => b.UNr == fz.UNr
                                                         && (b.Raum == null || b.FesteZeilen.Count > 0)
                                                         && b.Teile.Any(t => Cmp.Equals(t.Lehrer, fz.Lehrer))
                                                         && !b.FesteZeilen.Any(x => Cmp.Equals(x.Lehrer, fz.Lehrer))
                                                         && Cmp.Equals(b.Wunsch, fz.Wunschraum));
                    if (bf != null)
                    {
                        if (bf.Raum == null)
                        {
                            bf.Raum = kopie.Raum; bf.Herkunft = RaumHerkunft.Fest; bf.Hinweis = kopie.Hinweis;
                        }
                        bf.FesteZeilen.Add(kopie);
                    }
                    else extra.Add(kopie);
                }

                // ---- 0b. Bisherige Zuweisungen übernehmen ----
                if (behalten)
                {
                    foreach (var z in alte.Where(z => !z.Fix && z.Raum.Length > 0 && z.Herkunft != RaumHerkunft.OhneRaum))
                    {
                        var b = bedarfe.FirstOrDefault(x => x.Raum == null && x.UNr == z.UNr
                                                            && x.Teile.Any(t => Cmp.Equals(t.Lehrer, z.Lehrer))
                                                            && Cmp.Equals(x.Wunsch, z.Wunschraum));
                        if (b == null || !belegung.Frei(z.Raum, b.WochenGruppe)) continue;
                        Weise(b, z.Raum, z.Herkunft.Length > 0 ? z.Herkunft : b.WunschHerkunft, z.Hinweis);
                        b.Uebernommen = true;
                    }
                }

                var offen = bedarfe.Where(b => b.Raum == null)
                    .OrderBy(b => b.Klasse)
                    .ThenBy(b => b.Fortsetzung ? 0 : 1)
                    .ThenBy(b => b.Doppel ? 0 : 1)
                    .ThenByDescending(b => b.Wochenstunden)
                    .ThenBy(b => b.UNr)
                    .ThenBy(b => b.Wunsch, Cmp)
                    .ToList();

                // ---- 1a. Doppelstunde: Raum der Vorstunde behalten, wenn er nicht der
                //          Wunschraum eines anderen Unterrichts dieser Stunde ist ----
                if (vorstunde != null)
                {
                    var wunschraeume = new HashSet<string>(offen.Select(b => b.Wunsch), Cmp);
                    foreach (var b in offen.Where(b => b.Raum == null))
                        if (vorstunde.TryGetValue(b.Schluessel, out var vz) && vz.Raum.Length > 0
                            && !Cmp.Equals(vz.Raum, b.Wunsch) && !wunschraeume.Contains(vz.Raum)
                            && IstKandidat(b, vz.Raum) && belegung.Frei(vz.Raum, b.WochenGruppe))
                            Weise(b, vz.Raum, HerkunftFuer(b, vz.Raum), "wie in der Vorstunde (Doppelstunde)");
                }

                // ---- 1. Wunschraum ----
                foreach (var b in offen.Where(b => b.Raum == null))
                    if (belegung.Frei(b.Wunsch, b.WochenGruppe))
                        Weise(b, b.Wunsch, b.WunschHerkunft, "");

                // ---- 2. Raum der Vorstunde (Doppelstunde) ----
                if (vorstunde != null)
                    foreach (var b in offen.Where(b => b.Raum == null))
                        if (vorstunde.TryGetValue(b.Schluessel, out var vz) && vz.Raum.Length > 0
                            && IstKandidat(b, vz.Raum) && belegung.Frei(vz.Raum, b.WochenGruppe))
                            Weise(b, vz.Raum, HerkunftFuer(b, vz.Raum), Belegt(b) + " – wie in der Vorstunde");

                // ---- 3. anderer Raum derselben Fachgruppe ----
                foreach (var b in offen.Where(b => b.Raum == null && !b.IstSonder))
                {
                    var r = b.Fachgruppenraeume.FirstOrDefault(x => belegung.Frei(x, b.WochenGruppe));
                    if (r != null)
                        Weise(b, r, b.IstZusatz ? RaumHerkunft.Zusatzraum : RaumHerkunft.Fachgruppe, Belegt(b));
                }

                // ---- 4. Stammraum laut UV ----
                foreach (var b in offen.Where(b => b.Raum == null && !b.IstSonder && !b.IstZusatz))
                {
                    var r = b.Stammraeume.FirstOrDefault(x => belegung.Frei(x, b.WochenGruppe));
                    if (r != null) Weise(b, r, RaumHerkunft.Stammraum, Belegt(b));
                }

                // ---- 5. freier Ausweichraum ----
                foreach (var b in offen.Where(b => b.Raum == null && !b.IstSonder && !b.IstZusatz))
                {
                    var r = AusweichRaeume(b.Bereich).FirstOrDefault(x => belegung.Frei(x, b.WochenGruppe));
                    if (r != null) Weise(b, r, RaumHerkunft.Ausweich, Belegt(b));
                }

                // ---- Rest: ohne Raum ----
                foreach (var b in offen.Where(b => b.Raum == null))
                {
                    b.Raum = "";
                    b.Herkunft = RaumHerkunft.OhneRaum;
                    b.Hinweis = b.IstSonder ? Belegt(b) + " – Sonderraum wird nicht ersetzt"
                              : b.IstZusatz ? Belegt(b) + $" – kein freier Raum der Fachgruppe {b.Fachgruppe}"
                              : Belegt(b) + " – kein freier Raum gefunden";
                }

                // ---- Ergebnis des Slots merken (für die nächste Stunde) ----
                var merk = new Dictionary<string, Zuweisung>();
                foreach (var b in bedarfe)
                    merk.TryAdd(b.Schluessel, new Zuweisung(b.Raum ?? "", b.Herkunft, b.Hinweis));
                ergebnisJeSlot[sk] = merk;

                // ---- Zeilen ----
                foreach (var b in bedarfe)
                {
                    erg.Belegungen++;
                    erg.JeHerkunft[b.Herkunft] = erg.Anzahl(b.Herkunft) + 1;
                    if (b.Uebernommen) erg.Uebernommen++;
                    else if (b.Herkunft != RaumHerkunft.Fest) erg.Neu++;

                    foreach (var t in b.Teile)
                    {
                        var fest = b.FesteZeilen.FirstOrDefault(z => Cmp.Equals(z.Lehrer, t.Lehrer));
                        if (fest != null) { erg.Zeilen.Add(fest); continue; }
                        erg.Zeilen.Add(new RaumZeile
                        {
                            Plan = e.PlanSpalte, WTag = slot.WTag, Stunde = slot.Stunde, UNr = b.UNr,
                            Lehrer = t.Lehrer, Fach = t.Fach, Klassen = t.KlassenText,
                            Raum = b.Raum ?? "", Wunschraum = b.Wunsch, Herkunft = b.Herkunft,
                            Hinweis = b.Hinweis, Uebernommen = b.Uebernommen
                        });
                    }
                }
                foreach (var z in extra)
                {
                    erg.Zeilen.Add(z);
                    erg.Belegungen++;
                    erg.JeHerkunft[RaumHerkunft.Fest] = erg.Anzahl(RaumHerkunft.Fest) + 1;
                }
                foreach (var t in ohneAngabe)
                {
                    if (extra.Any(z => z.UNr == t.UNr && Cmp.Equals(z.Lehrer, t.Lehrer))) continue;
                    erg.TeileOhneAngabe++;
                    erg.Zeilen.Add(new RaumZeile
                    {
                        Plan = e.PlanSpalte, WTag = slot.WTag, Stunde = slot.Stunde, UNr = t.UNr,
                        Lehrer = t.Lehrer, Fach = t.Fach, Klassen = t.KlassenText,
                        Herkunft = RaumHerkunft.OhneRaum, Hinweis = "keine Raumangabe in der UV"
                    });
                }
            }

            erg.UnbekannteUNr = unbekannt.Count;
            if (unbekannt.Count > 0)
                erg.Meldungen.Add($"{unbekannt.Count} UNr stehen im Plan, aber nicht in der UV: {Liste(unbekannt.Select(u => u.ToString()))}.");
            if (erg.FesteVerworfen > 20)
                erg.Meldungen.Add($"… insgesamt {erg.FesteVerworfen} feste Zeilen passen nicht mehr zum Plan.");
            return erg;
        }

        /// <summary>Herkunft eines Raums, gemessen an den Kandidaten des Bedarfs.</summary>
        private static string HerkunftFuer(Bedarf b, string raum)
        {
            if (Cmp.Equals(raum, b.Wunsch)) return b.WunschHerkunft;
            if (b.Fachgruppenraeume.Contains(raum, Cmp))
                return b.IstZusatz ? RaumHerkunft.Zusatzraum : RaumHerkunft.Fachgruppe;
            if (b.Stammraeume.Contains(raum, Cmp)) return RaumHerkunft.Stammraum;
            return RaumHerkunft.Ausweich;
        }

        // ---- Bedarfe einer UNr (gleich in allen Stunden) ----

        private static Vorlage BaueVorlage(int unr, List<RaumTeil> teile, RaumVerzeichnis v,
                                           Dictionary<string, string> klassenStamm)
        {
            var vl = new Vorlage();
            foreach (var teil in teile)
            {
                if (teil.BrauchtKeinenRaum) continue;

                var wuensche = new List<(string Raum, string Quelle)>();
                if (teil.Fachraeume.Count > 0)
                    wuensche.AddRange(teil.Fachraeume.Select(r => (r, RaumHerkunft.Fachraum)));
                else if (teil.Stammraeume.Count > 0)
                    wuensche.AddRange(teil.Stammraeume.Select(r => (r, RaumHerkunft.Stammraum)));
                else if (teil.Klassen.Count == 1 && SekIKlasse.IsMatch(teil.Klassen[0])
                         && klassenStamm.TryGetValue(teil.Klassen[0], out var abg))
                    wuensche.Add((abg, RaumHerkunft.Abgeleitet));
                else
                {
                    vl.OhneAngabe.Add(teil);
                    continue;
                }

                bool hatSonder = teil.Fachraeume.Count > 1
                                 && teil.Fachraeume.Any(r => v.Finde(r)?.Art == RaumArt.Sonderraum);

                foreach (var (raum, quelle) in wuensche)
                {
                    var info = v.Finde(raum);
                    bool sonder = info?.Art == RaumArt.Sonderraum;
                    bool zusatz = quelle == RaumHerkunft.Fachraum && hatSonder && !sonder;
                    string fg = info != null && info.Art == RaumArt.Fachraum ? info.Fachgruppe : "";
                    int klasse = sonder ? 0
                               : zusatz ? 4
                               : quelle == RaumHerkunft.Fachraum ? (fg.Length > 0 ? 1 : 2)
                               : 3;
                    var stamm = quelle == RaumHerkunft.Fachraum
                        ? teil.Stammraeume.Where(s => !Cmp.Equals(s, raum)).ToList()
                        : new List<string>();

                    string schluessel = unr + "|" + raum.ToUpperInvariant();
                    var b = vl.Bedarfe.FirstOrDefault(x => x.Schluessel == schluessel);
                    if (b != null)
                    {
                        // Team-Teaching: weiterer Teil im selben Raum.
                        b.Teile.Add(teil);
                        if (klasse < b.Klasse) b.Klasse = klasse;
                        b.IstZusatz &= zusatz;
                        foreach (var s in stamm) if (!b.Stammraeume.Contains(s, Cmp)) b.Stammraeume.Add(s);
                        continue;
                    }
                    vl.Bedarfe.Add(new Bedarf
                    {
                        UNr = unr,
                        WochenGruppe = teil.WochenGruppe,
                        Wunsch = raum,
                        WunschHerkunft = zusatz ? RaumHerkunft.Zusatzraum : quelle,
                        Klasse = klasse,
                        IstSonder = sonder,
                        IstZusatz = zusatz,
                        Fachgruppe = fg,
                        Teile = new List<RaumTeil> { teil },
                        Stammraeume = stamm,
                        Schluessel = schluessel
                    });
                }
            }

            foreach (var b in vl.Bedarfe)
            {
                if (!b.IstZusatz && b.WunschHerkunft == RaumHerkunft.Zusatzraum)
                    b.WunschHerkunft = RaumHerkunft.Fachraum;   // ein Teil braucht den Raum regulär
                if (b.Fachgruppe.Length > 0)
                    b.Fachgruppenraeume = v.Raeume
                        .Where(r => r.Art != RaumArt.Sonderraum && Cmp.Equals(r.Fachgruppe, b.Fachgruppe)
                                    && !Cmp.Equals(r.Name, b.Wunsch))
                        .Select(r => r.Name).ToList();
                string? heim = b.Stammraeume.FirstOrDefault() ?? b.Wunsch;
                b.Bereich = v.Finde(heim)?.Bereich ?? "";
                if (b.Bereich.Length == 0) b.Bereich = v.Finde(b.Wunsch)?.Bereich ?? "";
            }
            return vl;
        }

        /// <summary>Stammraum je Sek-I-Klasse: häufigster Stammraum der Teile mit genau dieser einen Klasse.</summary>
        private static Dictionary<string, string> KlassenStammraeume(RaumEingabe e)
        {
            var zaehler = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in e.Unterrichte.Values.SelectMany(x => x))
            {
                if (t.Klassen.Count != 1 || !SekIKlasse.IsMatch(t.Klassen[0]) || t.Stammraeume.Count == 0) continue;
                if (!zaehler.TryGetValue(t.Klassen[0], out var z))
                    zaehler[t.Klassen[0]] = z = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                string r = t.Stammraeume[0];
                z[r] = z.TryGetValue(r, out int n) ? n + 1 : 1;
            }
            return zaehler.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, Cmp).First().Key,
                StringComparer.OrdinalIgnoreCase);
        }

        private static RaumZeile Kopie(RaumZeile z) => new()
        {
            Plan = z.Plan, WTag = z.WTag, Stunde = z.Stunde, UNr = z.UNr, Lehrer = z.Lehrer, Fach = z.Fach,
            Klassen = z.Klassen, Raum = z.Raum, Wunschraum = z.Wunschraum, Herkunft = z.Herkunft,
            Fix = z.Fix, Hinweis = z.Hinweis, Uebernommen = true
        };

        // =====================================================
        //  Planeditor (R2): Zeilen einer Stunde OHNE Verteilung
        // =====================================================

        /// <summary>
        /// Baut für den Planeditor die Raumzeilen einer Stunde, ohne einen Raum zu vergeben
        /// („Raum offen“) – dieselben Zeilen je Teil und Raumbedarf, die <see cref="Verteile"/>
        /// erzeugen würde. Teile ohne Raumangabe: „ohne Raum – keine Raumangabe in der UV“;
        /// Pseudofächer bekommen keine Zeile.
        /// </summary>
        public sealed class ZeilenVorlage
        {
            private readonly RaumEingabe _e;
            private readonly RaumVerzeichnis _v;
            private readonly Dictionary<string, string> _klassenStamm;
            private readonly Dictionary<int, Vorlage> _vorlagen = new();

            public ZeilenVorlage(RaumEingabe e, RaumVerzeichnis v)
            {
                _e = e;
                _v = v;
                _klassenStamm = KlassenStammraeume(e);
            }

            private Vorlage? VorlageFuer(int unr)
            {
                if (!_e.Unterrichte.TryGetValue(unr, out var teile)) return null;
                if (!_vorlagen.TryGetValue(unr, out var vl))
                    _vorlagen[unr] = vl = BaueVorlage(unr, teile, _v, _klassenStamm);
                return vl;
            }

            /// <summary>Zeilen „Raum offen“ für eine UNr in einer Stunde.</summary>
            public List<RaumZeile> Offen(string plan, string wtag, int stunde, int unr)
            {
                var l = new List<RaumZeile>();
                var vl = VorlageFuer(unr);
                if (vl == null) return l;
                foreach (var b in vl.Bedarfe)
                    foreach (var t in b.Teile)
                        l.Add(new RaumZeile
                        {
                            Plan = plan, WTag = wtag, Stunde = stunde, UNr = unr,
                            Lehrer = t.Lehrer, Fach = t.Fach, Klassen = t.KlassenText,
                            Raum = "", Wunschraum = b.Wunsch, Herkunft = RaumHerkunft.Offen,
                            Hinweis = "verschoben – Raum noch nicht verteilt"
                        });
                foreach (var t in vl.OhneAngabe)
                    l.Add(new RaumZeile
                    {
                        Plan = plan, WTag = wtag, Stunde = stunde, UNr = unr,
                        Lehrer = t.Lehrer, Fach = t.Fach, Klassen = t.KlassenText,
                        Herkunft = RaumHerkunft.OhneRaum, Hinweis = "keine Raumangabe in der UV"
                    });
                return l;
            }

            /// <summary>Herkunft des Raums einer Zeile (z. B. nach „Festlegung aufheben“).</summary>
            public string HerkunftVon(RaumZeile z)
            {
                var b = VorlageFuer(z.UNr)?.Bedarfe.FirstOrDefault(x => Cmp.Equals(x.Wunsch, z.Wunschraum)
                                                                      && x.Teile.Any(t => Cmp.Equals(t.Lehrer, z.Lehrer)));
                return b == null ? RaumHerkunft.Ausweich : RaumVerteilung.HerkunftFuer(b, z.Raum);
            }
        }

        private static string Liste(IEnumerable<string> namen)
        {
            var l = namen.ToList();
            return l.Count <= 15 ? string.Join(", ", l) : string.Join(", ", l.Take(15)) + $" … (+{l.Count - 15})";
        }
    }
}
