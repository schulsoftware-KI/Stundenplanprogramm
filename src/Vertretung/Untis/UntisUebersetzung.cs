#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  UntisUebersetzung – U-1b: ordnet jede GPU014-Zeile dem Grundplan der
    //  gewählten Plan-Spalte zu und übersetzt sie in einen Eintrag für
    //  POST /api/plan/push (Entscheidungen 121, 122, 127, 129–134).
    //
    //  Zuordnung: Datum → Wochentag, Stunde, U-Nr, absente Lehrkraft (Feld 6).
    //  Art: der Buchstabe (Feld 20) entscheidet, die Bits (Feld 18) sind die
    //  Rückfallebene; Bits 21/22 werden ignoriert.
    //  Merker (132): raumGeaendert (Feld 13 ≠ 12), fachGeaendert (Feld 10 ≠ 8),
    //  klassenGeaendert (Feld 19 ≠ 15) – nur bei gefundener Grundplan-Stunde.
    //  Absenzgrund und Text kennt diese Klasse nicht (UntisLeser liest sie nicht);
    //  „Grund“ und „Bemerkung“ bleiben im Push immer leer.
    // =====================================================================

    public static class UntisUebersetzung
    {
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
        private static readonly HashSet<string> Klau = new(StringComparer.OrdinalIgnoreCase) { "Klau" };

        /// <summary>Art einer Zeile: Text für Übersicht/Vorschau, Status (null = nicht übernommen), zusätzliche Stunde?</summary>
        internal readonly record struct ArtInfo(string Text, string? Status, bool Zusatz, string? Hinweis);

        // Interner Status „Offen“ = offene Vertretung (wird als „Vertreten“ mit Lehrkraft „???“ gesendet, 130).
        private const string Offen = "Offen";

        internal static ArtInfo ArtVon(UntisZeile z)
        {
            string b = z.Buchstabe;
            bool hat = z.Vertreter.Length > 0;

            ArtInfo Sonder()
            {
                var kl = (z.VKlassen.Count > 0 ? z.VKlassen : z.Klassen).Where(k => !Klau.Contains(k)).ToList();
                if (kl.Count == 0) return new("Sondereinsatz ohne Klasse (Klausuraufsicht)", null, false, null);
                return z.UNr > 0
                    ? new ArtInfo("Sondereinsatz", "Vertreten", false, null)
                    : new ArtInfo("Sondereinsatz (zusätzliche Stunde)", "Vertreten", true, null);
            }
            ArtInfo RaumV()
            {
                if (hat && z.Absent.Length > 0 && !Cmp.Equals(z.Vertreter, z.Absent))
                    return new("Raumvertretung mit anderer Lehrkraft", "Vertreten", false,
                               "Raumvertretung mit anderer Lehrkraft – als Vertretung übernommen.");
                return new("Raumvertretung", "Raumvertretung", false, null);
            }
            ArtInfo Weg() => new("Verlegung (alte Stelle)", "VerlegtWeg", false, null);
            ArtInfo EntfallOderWeg() => z.Bit(3) ? Weg() : new ArtInfo("Entfall", "Entfall", false, null);

            if (z.Bit(20)) return new("nicht drucken", null, false, null);
            switch (b)
            {
                case "E": return new("Klausur", "Klausur", false, null);
                case "B": return new("Pausenaufsichtsvertretung", null, false, null);
                case "A": return Sonder();
                case "C": return EntfallOderWeg();
                case "T": return Weg();
                case "F": return new("Verlegung (neue Stelle)", "VerlegtHin", false, null);
                case "W":
                    return z.Bit(7) || (hat && !z.Bit(3))
                        ? new ArtInfo("Tausch (neue Stelle)", "VerlegtHin", false, null)
                        : new ArtInfo("Tausch (alte Stelle)", "VerlegtWeg", false, null);
                case "S": return new("Betreuung", "Betreuung", false, null);
                case "R": return RaumV();
                case "L": return new("Freisetzung", "Entfall", false, null);
                case "P": return new("Teil-Vertretung", "Vertreten", false, null);
                case "~": return new("Lehrertausch", "Vertreten", false, null);
                case "":
                    if (z.Bit(17)) return new("Pausenaufsichtsvertretung", null, false, null);
                    if (z.Bit(5)) return new("Plus als Vertreter (EVA)", "EVA", false, null);
                    if (z.Bit(0)) return EntfallOderWeg();
                    if (z.Bit(3)) return Weg();
                    if (z.Bit(7)) return new("Verlegung (neue Stelle)", "VerlegtHin", false, null);
                    if (z.Bit(4)) return new("Freisetzung", "Entfall", false, null);
                    if (z.Bit(18)) return new("Stunde unterrichtsfrei", "Entfall", false, null);
                    if (z.Bit(1)) return new("Betreuung", "Betreuung", false, null);
                    if (z.Bit(2)) return Sonder();
                    if (z.Bit(16)) return RaumV();
                    if (z.Bit(6)) return new("Teil-Vertretung", "Vertreten", false, null);
                    return hat ? new ArtInfo("Vertretung", "Vertreten", false, null) : new ArtInfo("offene Vertretung", Offen, false, null);
                default:
                    return new($"unbekannte Art „{b}“", "Vertreten", false,
                               $"Unbekannte Vertretungsart „{b}“ – als Vertretung übernommen.");
            }
        }

        private static string ArtFuerStatus(string status) => status switch
        {
            "Vertreten" or Offen => "Vertretung",
            "Entfall" => "Entfall",
            "VerlegtWeg" or "VerlegtHin" => "Verlegung",
            "Betreuung" => "Betreuung",
            "EVA" => "EVA",
            "Raumvertretung" => "Raumvertretung",
            "Klausur" => "Klausur",
            _ => "Vertretung"
        };

        // ---------------- Index des Grundplans ----------------

        private sealed class PlanIndex
        {
            public readonly PlanDaten Plan;
            public readonly Dictionary<int, string> WTagFuer = new();                       // DayOfWeek → WTag
            public readonly Dictionary<(int Dow, int Stunde), List<(int UNr, TeilInfo Teil)>> Slot = new();
            public readonly Dictionary<int, HashSet<int>> Stunden = new();                  // DayOfWeek → Stunden
            public readonly HashSet<string> Klassen = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Lehrer = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>(Fach normalisiert, Klasse/Stufe) → (UNr, Lehrkraft), für Klausuren.</summary>
            public readonly Dictionary<(string Fach, string Klasse), List<(int UNr, string Lehrer)>> Kurse = new();

            public PlanIndex(PlanDaten p)
            {
                Plan = p;
                foreach (var w in p.RasterTage)
                {
                    int i = SchuljahrKalender.WochentagIndex(w);
                    if (i >= 0) WTagFuer.TryAdd(i, w);
                }
                foreach (var kv in p.Belegung)
                {
                    int i = SchuljahrKalender.WochentagIndex(kv.Key.WTag);
                    if (i < 0) continue;
                    if (!Stunden.TryGetValue(i, out var st)) Stunden[i] = st = new HashSet<int>();
                    st.Add(kv.Key.Stunde);
                    foreach (int unr in kv.Value)
                    {
                        if (!p.Unterrichte.TryGetValue(unr, out var teile)) continue;
                        if (!Slot.TryGetValue((i, kv.Key.Stunde), out var l)) Slot[(i, kv.Key.Stunde)] = l = new();
                        foreach (var t in teile) l.Add((unr, t));
                    }
                }
                foreach (var unr in p.Unterrichte.Keys.OrderBy(u => u))
                    foreach (var t in p.Unterrichte[unr])
                        foreach (var k in t.Klassen)
                        {
                            Klassen.Add(k);
                            var key = (UntisLeser.Norm(t.Fach).ToUpperInvariant(), k.ToUpperInvariant());
                            if (!Kurse.TryGetValue(key, out var kl)) Kurse[key] = kl = new();
                            kl.Add((unr, t.Lehrer));
                        }
                foreach (var l in p.AlleLehrer) Lehrer.Add(l);
            }
        }

        // ---------------- Übersetzen ----------------

        /// <param name="buchungsTage">Tage mit Buchungen im Vertretungsmodul (für die Warnung, Entscheidung 120).</param>
        public static UntisErgebnis Uebersetze(UntisDatei datei, PlanDaten plan, SchuljahrKalender kal,
                                               ISet<DateTime> buchungsTage, DateTime heute)
        {
            var ix = new PlanIndex(plan);
            var erg = new UntisErgebnis { PlanName = plan.GewaehltePlanSpalte };
            erg.Probleme.AddRange(datei.Probleme);

            var arten = new Dictionary<string, int>();
            var arteReihe = new List<string>();
            void ZaehleArt(string t) { if (!arten.ContainsKey(t)) { arten[t] = 0; arteReihe.Add(t); } arten[t]++; }
            var nicht = new Dictionary<string, int>();
            var nichtReihe = new List<string>();
            int zugeordnet = 0, ohne = 0, zusatz = 0, klausur = 0, doppelt = 0;
            int raumW = 0, fachW = 0, klassenW = 0, offenAbHeute = 0, offen = 0;
            var gesehen = new HashSet<string>(StringComparer.Ordinal);
            var kursFehlt = new SortedDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var keinSchultag = new SortedDictionary<DateTime, List<int>>();
            var unbekLehrer = new SortedDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var unbekKlassen = new SortedDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            // Wegverlegungen (U-Nr, Lehrkraft): helfen, die U-Nr einer hierher verlegten Stunde zu finden.
            var wegPaare = new HashSet<(int, string)>();
            foreach (var z in datei.Zeilen)
                if (ArtVon(z).Status == "VerlegtWeg") wegPaare.Add((z.UNr, z.Absent.ToUpperInvariant()));

            foreach (var z in datei.Zeilen)
            {
                var art = ArtVon(z);
                ZaehleArt(art.Text);
                if (art.Status == null)
                {
                    if (!nicht.ContainsKey(art.Text)) { nicht[art.Text] = 0; nichtReihe.Add(art.Text); }
                    nicht[art.Text]++;
                    continue;
                }
                string status = art.Status;
                DateTime d = z.Datum;
                int dow = (int)d.DayOfWeek;
                if (!kal.IstSchultag(d) || !ix.WTagFuer.ContainsKey(dow))
                {
                    if (!keinSchultag.TryGetValue(d, out var l)) keinSchultag[d] = l = new List<int>();
                    l.Add(z.Nr);
                    continue;
                }
                if (!ix.Stunden.TryGetValue(dow, out var stunden) || !stunden.Contains(z.Stunde))
                {
                    erg.Probleme.Add(new UntisProblem
                    {
                        Art = UntisProblemArt.Fehler, Zeile = z.Nr, Spalte = "Stunde (Feld 3)",
                        Grund = $"{d:dd.MM.yyyy}: Stunde {z.Stunde} gibt es im Zeitraster des Plans nicht – nicht übernommen."
                    });
                    continue;
                }
                if (art.Hinweis != null)
                    erg.Probleme.Add(new UntisProblem { Art = UntisProblemArt.Hinweis, Zeile = z.Nr, Grund = art.Hinweis });

                foreach (var l in new[] { z.Absent, z.Vertreter })
                    if (l.Length > 0 && !ix.Lehrer.Contains(l)) Merke(unbekLehrer, l, z.Nr);
                foreach (var k in z.Klassen.Concat(z.VKlassen))
                    if (!ix.Klassen.Contains(k) && !Klau.Contains(k)) Merke(unbekKlassen, k, z.Nr);

                var klAlt = z.Klassen;
                var klNeu = z.VKlassen.Count > 0 ? z.VKlassen : z.Klassen;
                string fachAlt = z.Fach;
                string fachNeu = z.VFach.Length > 0 ? z.VFach : z.Fach;
                var rAlt = UntisLeser.Liste(z.Raum);
                var rVert = UntisLeser.Liste(z.VRaum);
                var rNeu = rVert.Count > 0 ? rVert : rAlt;
                bool raumG = rVert.Count > 0 && !GleicheMenge(rVert, rAlt);
                bool fachG = z.VFach.Length > 0 && !Cmp.Equals(z.VFach, z.Fach);
                bool klassenG = z.VKlassen.Count > 0 && !GleicheMenge(z.VKlassen, z.Klassen);

                var dto = new PlanEintragDto
                {
                    Stunde = z.Stunde,
                    Fach = fachNeu,
                    Art = ArtFuerStatus(status),
                    Status = status == Offen ? "Vertreten" : status,
                    Grund = null,          // nie – der Absenzgrund wird nicht gelesen (123)
                    Bemerkung = null,      // Feld 17 wird nicht übernommen
                    Fachgerecht = null,
                    IstAenderung = true
                };
                List<string> klassen = klNeu;
                string gegen;
                bool zusaetzlich = false;
                // Gezählt wird erst nach der Prüfung auf doppelte Zeilen.
                bool zZugeordnet = false, zOhne = false, zZusatz = false, zKlausur = false;
                bool zRaumW = false, zFachW = false, zKlassenW = false, zOffen = false;

                if (status == "Klausur")
                {
                    // 131: Kurs aus Feld 10, Stufe aus Feld 19; Plan-Kurs gleichen Namens in der Stufe suchen.
                    string kurs = z.VFach.Length > 0 ? z.VFach : z.Fach;
                    var stufe = z.VKlassen.Count > 0 ? z.VKlassen : z.Klassen;
                    var kand = new List<(int UNr, string Lehrer)>();
                    if (stufe.Count > 0)
                    {
                        foreach (var s in stufe)
                            if (ix.Kurse.TryGetValue((kurs.ToUpperInvariant(), s.ToUpperInvariant()), out var kl)) kand.AddRange(kl);
                    }
                    else
                    {
                        foreach (var kv in ix.Kurse)
                            if (kv.Key.Fach == kurs.ToUpperInvariant()) kand.AddRange(kv.Value);
                    }
                    kand = kand.GroupBy(k => (k.UNr, k.Lehrer.ToUpperInvariant())).Select(g => g.First())
                               .OrderBy(k => k.UNr).ThenBy(k => k.Lehrer.ToUpperInvariant(), StringComparer.Ordinal).ToList();
                    var imSlot = new HashSet<(int, string)>(
                        (ix.Slot.TryGetValue((dow, z.Stunde), out var sl) ? sl : new List<(int UNr, TeilInfo Teil)>())
                        .Select(x => (x.UNr, x.Teil.Lehrer.ToUpperInvariant())));
                    var bevorzugt = kand.Where(k => imSlot.Contains((k.UNr, k.Lehrer.ToUpperInvariant()))).ToList();
                    var wahl = bevorzugt.Count > 0 ? bevorzugt[0] : kand.Count > 0 ? kand[0] : ((int UNr, string Lehrer)?)null;

                    dto.Fach = kurs;
                    klassen = stufe;
                    dto.Lehrer = z.Vertreter.Length > 0 ? z.Vertreter : null;
                    dto.Raum = Raum(rNeu);
                    if (wahl != null)
                    {
                        dto.UNr = wahl.Value.UNr;
                        dto.GrundUNr = wahl.Value.UNr;
                        dto.OriginalLehrer = wahl.Value.Lehrer;
                        gegen = "Kurs";
                    }
                    else
                    {
                        Merke(kursFehlt, $"„{kurs}“ ({string.Join(", ", stufe)})", z.Nr);
                        gegen = "zusätzlich";
                        zusaetzlich = true;
                    }
                    zKlausur = true;
                }
                else if (art.Zusatz || (status == "VerlegtHin" && z.UNr == 0))
                {
                    // Sondereinsatz ohne U-Nr bzw. Verlegung auf eine freie Stunde: zusätzliche Stunde (keine Warnung).
                    dto.Lehrer = z.Vertreter.Length > 0 ? z.Vertreter : (z.Absent.Length > 0 ? z.Absent : null);
                    dto.Raum = Raum(rNeu);
                    if (status == "VerlegtHin") dto.UNr = VerlegteUNr(ix, z, fachNeu, klNeu, wegPaare);
                    gegen = "zusätzlich";
                    zusaetzlich = true;
                    zZusatz = true;
                }
                else
                {
                    var teile = z.UNr > 0 && ix.Slot.TryGetValue((dow, z.Stunde), out var slot)
                        ? slot.Where(x => x.UNr == z.UNr).Select(x => x.Teil).ToList()
                        : new List<TeilInfo>();
                    string? orig = null;
                    if (teile.Count > 0)
                    {
                        var lehrer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var t in teile) lehrer.TryAdd(t.Lehrer, t.Lehrer);
                        if (z.Absent.Length > 0) { if (lehrer.TryGetValue(z.Absent, out var o)) orig = o; }
                        else if (z.Vertreter.Length > 0 && lehrer.TryGetValue(z.Vertreter, out var o2)) orig = o2;
                        else if (lehrer.Count == 1) orig = lehrer.Values.First();
                    }
                    if (z.UNr > 0) { dto.GrundUNr = z.UNr; dto.UNr = z.UNr; }

                    if (orig != null)
                    {
                        dto.OriginalLehrer = orig;
                        gegen = "ja";
                        zZugeordnet = true;
                    }
                    else
                    {
                        dto.OriginalLehrer = z.Absent.Length > 0 ? z.Absent : null;
                        gegen = "nein";
                        zOhne = true;
                        bool ohneWirkung = status is "Entfall" or "VerlegtWeg" or "Raumvertretung";
                        string warum = teile.Count == 0 && z.UNr > 0 ? $"U-Nr {z.UNr} liegt im Plan nicht in dieser Stunde"
                                     : z.UNr <= 0 ? "ohne U-Nr"
                                     : z.Absent.Length > 0 ? $"{z.Absent} unterrichtet U-Nr {z.UNr} in dieser Stunde nicht"
                                     : $"absente Lehrkraft fehlt, U-Nr {z.UNr} hat mehrere Lehrkräfte";
                        erg.Probleme.Add(new UntisProblem
                        {
                            Art = UntisProblemArt.Warnung, Zeile = z.Nr,
                            Grund = $"Kein Gegenstück im Plan „{plan.GewaehltePlanSpalte}“: {UntisText.TagDatum(d)}, {z.Stunde}. Std., " +
                                    $"{warum} – {(ohneWirkung ? "hat in der App keine Wirkung" : "erscheint als zusätzliche Stunde")}."
                        });
                    }

                    if (status is "Entfall" or "VerlegtWeg")
                    {
                        klassen = klAlt;
                        dto.Fach = fachAlt;
                        dto.Lehrer = null;
                    }
                    else if (status == "Raumvertretung")
                    {
                        klassen = klAlt;
                        dto.Fach = fachAlt;
                        dto.Lehrer = z.Vertreter.Length > 0 ? z.Vertreter : dto.OriginalLehrer;
                        dto.Raum = Raum(rVert);
                        if (rVert.Count == 0)
                            erg.Probleme.Add(new UntisProblem
                            {
                                Art = UntisProblemArt.Hinweis, Zeile = z.Nr, Spalte = "Vertretungsraum (Feld 13)",
                                Grund = "Raumvertretung ohne Vertretungsraum."
                            });
                    }
                    else
                    {
                        dto.Lehrer = status switch
                        {
                            Offen => "???",
                            "VerlegtHin" => z.Vertreter.Length > 0 ? z.Vertreter : dto.OriginalLehrer,
                            _ => z.Vertreter.Length > 0 ? z.Vertreter : null
                        };
                        if (status == "VerlegtHin") dto.UNr = VerlegteUNr(ix, z, fachNeu, klNeu, wegPaare);
                        if (orig != null)
                        {
                            dto.Raum = raumG ? Raum(rNeu) : null;
                            dto.RaumGeaendert = raumG ? true : null;
                            dto.FachGeaendert = fachG ? true : null;
                            dto.KlassenGeaendert = klassenG ? true : null;
                            zRaumW = raumG;
                            zFachW = fachG;
                            zKlassenW = klassenG;
                        }
                        else
                        {
                            dto.Raum = Raum(rNeu);
                            zusaetzlich = true;
                        }
                    }
                    zOffen = status == Offen;
                }
                dto.Klassen = string.Join(",", klassen);

                string schluessel = string.Join("|", d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), dto.Stunde, dto.Status,
                    dto.GrundUNr?.ToString(CultureInfo.InvariantCulture) ?? "", (dto.OriginalLehrer ?? "").ToUpperInvariant(),
                    (dto.Lehrer ?? "").ToUpperInvariant(),
                    string.Join(",", klassen.Select(k => k.ToUpperInvariant()).OrderBy(k => k, StringComparer.Ordinal)),
                    dto.Fach.ToUpperInvariant(), (dto.Raum ?? "").ToUpperInvariant());
                if (!gesehen.Add(schluessel))
                {
                    erg.Probleme.Add(new UntisProblem
                    {
                        Art = UntisProblemArt.Hinweis, Zeile = z.Nr, Grund = "Doppelte Zeile – nur einmal übernommen."
                    });
                    doppelt++;
                    continue;
                }
                if (zZugeordnet) zugeordnet++;
                if (zOhne) ohne++;
                if (zZusatz) zusatz++;
                if (zKlausur) klausur++;
                if (zRaumW) raumW++;
                if (zFachW) fachW++;
                if (zKlassenW) klassenW++;
                if (zOffen) { offen++; if (d >= heute.Date) offenAbHeute++; }

                var e = new UntisEintrag
                {
                    Datum = d, Zeile = z.Nr, UntisArt = art.Text, Gegenstueck = gegen, Dto = dto,
                    KlassenText = Wechsel(string.Join(", ", klAlt), string.Join(", ", klassen),
                                          klAlt.Count > 0 && !GleicheMenge(klAlt, klassen)),
                    FachText = Wechsel(fachAlt, dto.Fach, fachAlt.Length > 0 && !Cmp.Equals(fachAlt, dto.Fach)),
                    LehrerText = LehrerText(status, dto, zusaetzlich),
                    RaumText = RaumText(status, rAlt, rNeu, dto),
                    AppText = AppText(status, zusaetzlich),
                    Farbe = gegen == "nein" ? "#EEEEEE" : Farbe(status)
                };
                if (!erg.Tage.TryGetValue(d, out var tagListe)) erg.Tage[d] = tagListe = new List<UntisEintrag>();
                tagListe.Add(e);
            }

            foreach (var kv in kursFehlt)
                erg.Probleme.Add(new UntisProblem
                {
                    Art = UntisProblemArt.Hinweis, Zeile = kv.Value[0], Spalte = "Vertretungsfach (Feld 10)",
                    Grund = $"Klausur: Kurs {kv.Key} nicht im Plan – erscheint als zusätzliche Stunde ohne Teilnehmerliste " +
                            $"({Zeilen(kv.Value)})."
                });
            foreach (var kv in keinSchultag)
                erg.Probleme.Add(new UntisProblem
                {
                    Art = UntisProblemArt.Hinweis, Zeile = kv.Value[0], Spalte = "Datum (Feld 2)",
                    Grund = $"{UntisText.Tag(kv.Key)} {kv.Key:dd.MM.yyyy} ist laut Kalender kein Schultag (Wochenende, Ferien, " +
                            $"Feiertag oder außerhalb des Schuljahres) – {Zeilen(kv.Value)} nicht gesendet."
                });
            foreach (var kv in unbekLehrer)
                erg.Probleme.Add(new UntisProblem
                {
                    Art = UntisProblemArt.Warnung, Zeile = kv.Value[0], Spalte = "Lehrkraft (Feld 6/7)",
                    Grund = $"Kürzel „{kv.Key}“ steht nicht im Plan ({Zeilen(kv.Value)})."
                });
            foreach (var kv in unbekKlassen)
                erg.Probleme.Add(new UntisProblem
                {
                    Art = UntisProblemArt.Warnung, Zeile = kv.Value[0], Spalte = "Klasse (Feld 15/19)",
                    Grund = $"Klasse „{kv.Key}“ steht nicht im Plan ({Zeilen(kv.Value)})."
                });
            foreach (var t in nichtReihe)
                erg.Probleme.Add(new UntisProblem
                {
                    Art = UntisProblemArt.Hinweis,
                    Grund = $"Nicht übernommen (kein Unterricht): {t} – {nicht[t]} Zeile(n)."
                });
            if (ohne >= 20 && ohne * 5 >= zugeordnet + ohne)
                erg.Probleme.Add(new UntisProblem
                {
                    Art = UntisProblemArt.Warnung,
                    Grund = $"{ohne} Zeilen ohne Gegenstück im Plan „{plan.GewaehltePlanSpalte}“. Passt die Plan-Spalte im " +
                            "Wocheneditor? Wurde der Plan in Untis geändert, die GPU001.TXT neu importieren (Knopf 3) und den " +
                            "Grundplan neu veröffentlichen."
                });
            if (offenAbHeute > 0)
                erg.Probleme.Add(new UntisProblem
                {
                    Art = UntisProblemArt.Hinweis,
                    Grund = $"{offenAbHeute} offene Vertretung(en) ab heute – die App zeigt dort „???“ statt einer Lehrkraft."
                });

            var daten = datei.Zeilen.Select(z => z.Datum).ToList();
            erg.ErsterTag = daten.Count > 0 ? daten.Min() : null;
            erg.LetzterTag = daten.Count > 0 ? daten.Max() : null;
            erg.OffenAbHeute = offenAbHeute;
            erg.OhneGegenstueck = ohne;

            // Vertretungsmodul: Buchungen an Tagen im Zeitraum der Datei (Entscheidung 120).
            if (erg.ErsterTag != null && buchungsTage.Count > 0)
            {
                var tage = buchungsTage.Where(t => t >= erg.ErsterTag && t <= erg.LetzterTag).OrderBy(t => t).ToList();
                if (tage.Count > 0)
                    erg.Probleme.Add(new UntisProblem
                    {
                        Art = UntisProblemArt.Warnung,
                        Grund = $"Für {tage.Count} Tag(e) im Zeitraum der Datei gibt es Buchungen im Vertretungsmodul " +
                                $"(z. B. {string.Join(", ", tage.Take(3).Select(t => t.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)))}" +
                                $"{(tage.Count > 3 ? " …" : "")}). Beim Veröffentlichen ersetzen die Untis-Vertretungen diese Tage in der App."
                    });
            }

            SortiereProbleme(erg.Probleme);
            BaueZahlen(erg, datei, arten, arteReihe, nicht, nichtReihe, zugeordnet, ohne, zusatz, klausur, doppelt,
                       raumW, fachW, klassenW, offen, offenAbHeute, kal);
            return erg;
        }

        /// <summary>Alle Schultage im Zeitraum (Entscheidung 134): auch Tage ohne Zeilen (werden leer gesendet).</summary>
        public static List<DateTime> Schultage(SchuljahrKalender kal, DateTime von, DateTime bis)
            => kal.Schultage(von.Date, bis.Date).ToList();

        // ---------------- Hilfen ----------------

        /// <summary>U-Nr der hierher verlegten Stunde: Lehrkraft (Feld 7) + Fach (Feld 10) + Klassen im Plan.</summary>
        private static int? VerlegteUNr(PlanIndex ix, UntisZeile z, string fach, List<string> klassen, HashSet<(int, string)> wegPaare)
        {
            if (z.Vertreter.Length == 0) return null;
            var kl = new HashSet<string>(klassen, StringComparer.OrdinalIgnoreCase);
            var kand = new SortedSet<int>();
            foreach (var kv in ix.Plan.Unterrichte)
                foreach (var t in kv.Value)
                    if (Cmp.Equals(t.Lehrer, z.Vertreter) && Cmp.Equals(UntisLeser.Norm(t.Fach), fach)
                        && (kl.Count == 0 || t.Klassen.Any(k => kl.Contains(k))))
                        kand.Add(kv.Key);
            if (kand.Count == 0) return null;
            var bev = kand.Where(u => wegPaare.Contains((u, z.Vertreter.ToUpperInvariant()))).ToList();
            return bev.Count > 0 ? bev.Min() : kand.Min;
        }

        private static void Merke<TKey>(IDictionary<TKey, List<int>> d, TKey key, int nr) where TKey : notnull
        {
            if (!d.TryGetValue(key, out var l)) d[key] = l = new List<int>();
            l.Add(nr);
        }

        private static string Zeilen(List<int> nrn)
        {
            if (nrn.Count == 1) return $"Zeile {nrn[0]}";
            var teil = string.Join(", ", nrn.Take(8));
            return $"{nrn.Count} Zeilen: {teil}{(nrn.Count > 8 ? ", …" : "")}";
        }

        private static string? Raum(List<string> raeume) => raeume.Count == 0 ? null : string.Join(", ", raeume);

        private static bool GleicheMenge(IEnumerable<string> a, IEnumerable<string> b)
            => new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);

        private static string Wechsel(string alt, string neu, bool geaendert)
            => geaendert && alt.Length > 0 ? $"{alt} → {neu}" : neu;

        private static string LehrerText(string status, PlanEintragDto dto, bool zusaetzlich)
        {
            string orig = dto.OriginalLehrer ?? "";
            string neu = string.IsNullOrEmpty(dto.Lehrer) ? "–" : dto.Lehrer!;
            if (status == "Klausur")
                return $"Aufsicht {neu}" + (orig.Length > 0 ? $" (Kurs {orig})" : "");
            if (status is "Entfall" or "VerlegtWeg" or "Raumvertretung") return orig.Length > 0 ? orig : neu;
            if (zusaetzlich && orig.Length == 0) return neu;
            return orig.Length > 0 && !Cmp.Equals(orig, neu) ? $"{orig} → {neu}" : neu;
        }

        private static string RaumText(string status, List<string> rAlt, List<string> rNeu, PlanEintragDto dto)
        {
            string alt = string.Join(", ", rAlt);
            if (status is "Entfall" or "VerlegtWeg") return alt;
            string neu = dto.Raum ?? alt;
            return alt.Length > 0 && !Cmp.Equals(alt, neu) ? $"{alt} → {neu}" : neu;
        }

        private static string AppText(string status, bool zusaetzlich)
        {
            string t = status switch
            {
                "Entfall" => "entfällt",
                "VerlegtWeg" => "verlegt (frei)",
                "VerlegtHin" => "hierher verlegt",
                "Betreuung" => "Betreuung/Aufsicht",
                "EVA" => "EVA",
                "Raumvertretung" => "Raumwechsel",
                "Klausur" => "Klausur",
                Offen => "Vertretung (???)",
                _ => "Vertretung"
            };
            return zusaetzlich && status is not ("Entfall" or "VerlegtWeg" or "Raumvertretung") ? t + " · zusätzliche Stunde" : t;
        }

        private static string Farbe(string status) => status switch
        {
            "Entfall" => "#FFCDD2",
            "VerlegtWeg" or "VerlegtHin" => "#FFE0B2",
            "Betreuung" or "EVA" => "#BBDEFB",
            "Raumvertretung" => "#FFF9C4",
            "Klausur" => "#F8BBD0",
            _ => "#C8E6C9"
        };

        private static void SortiereProbleme(List<UntisProblem> probleme)
        {
            var sortiert = probleme.Select((p, i) => (p, i))
                .OrderBy(x => (int)x.p.Art).ThenBy(x => x.p.Zeile ?? int.MaxValue).ThenBy(x => x.i)
                .Select(x => x.p).ToList();
            probleme.Clear();
            probleme.AddRange(sortiert);
        }

        private static void BaueZahlen(UntisErgebnis erg, UntisDatei datei, Dictionary<string, int> arten, List<string> artenReihe,
            Dictionary<string, int> nicht, List<string> nichtReihe, int zugeordnet, int ohne, int zusatz, int klausur, int doppelt,
            int raumW, int fachW, int klassenW, int offen, int offenAbHeute, SchuljahrKalender kal)
        {
            void Z(string b, string a, string w) => erg.Zahlen.Add(new UntisZahl { Bereich = b, Angabe = a, Wert = w });
            string trenn = datei.Trennzeichen == ',' ? "Komma" : "Semikolon";

            Z("Datei", "Name", System.IO.Path.GetFileName(datei.Pfad));
            Z("Datei", "Art", datei.IstDemo ? "Demo-Datei (Prüfsumme passt)" : "echte Datei");
            Z("Datei", "Zeichensatz · Trennzeichen", $"{datei.Zeichensatz} · {trenn}");
            Z("Datei", "Zeilen", $"{datei.ZeilenGesamt} (davon {datei.Zeilen.Count} lesbar)");
            if (datei.LetzteAenderung != null)
                Z("Datei", "Letzte Änderung in Untis", datei.LetzteAenderung.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
            Z("Datei", "Absenzgründe (Feld 16)", datei.MitAbsenzgrund == 0 ? "leer" : $"in {datei.MitAbsenzgrund} Zeilen vorhanden – nicht gelesen");
            Z("Datei", "Texte zur Vertretung (Feld 17)", datei.MitText == 0 ? "leer" : $"in {datei.MitText} Zeilen vorhanden – nicht gelesen");

            int mitZeilen = erg.Tage.Count;
            if (erg.ErsterTag != null)
            {
                Z("Tage", "Zeitraum der Datei", $"{erg.ErsterTag:dd.MM.yyyy} bis {erg.LetzterTag:dd.MM.yyyy}");
                int schultage = kal.Schultage(erg.ErsterTag.Value, erg.LetzterTag!.Value).Count();
                Z("Tage", "Schultage im Zeitraum", $"{schultage} (davon {mitZeilen} mit Einträgen, {Math.Max(0, schultage - mitZeilen)} ohne – werden leer gesendet)");
            }

            foreach (var a in artenReihe.OrderByDescending(x => arten[x]).ThenBy(x => x, StringComparer.Ordinal))
                Z("Untis-Arten", a, arten[a].ToString(CultureInfo.InvariantCulture));

            int gesendet = erg.Tage.Values.Sum(l => l.Count);
            Z("Übernahme", "Einträge zum Senden", gesendet.ToString(CultureInfo.InvariantCulture));
            Z("Übernahme", "der Grundplan-Stunde zugeordnet", zugeordnet.ToString(CultureInfo.InvariantCulture));
            Z("Übernahme", "zusätzliche Stunden (Sondereinsatz ohne U-Nr, Verlegung auf freie Stunde)", zusatz.ToString(CultureInfo.InvariantCulture));
            Z("Übernahme", "Klausur-Zeilen", klausur.ToString(CultureInfo.InvariantCulture));
            Z("Übernahme", "ohne Gegenstück im Plan (Warnung)", ohne.ToString(CultureInfo.InvariantCulture));
            if (doppelt > 0) Z("Übernahme", "doppelte Zeilen (nur einmal)", doppelt.ToString(CultureInfo.InvariantCulture));
            foreach (var t in nichtReihe)
                Z("Übernahme", $"nicht übernommen: {t}", nicht[t].ToString(CultureInfo.InvariantCulture));
            Z("Wechsel in Vertretungen", "Raum · Fach · Klassen", $"{raumW} · {fachW} · {klassenW}");
            Z("Offene Vertretungen (???)", "gesamt · ab heute", $"{offen} · {offenAbHeute}");
        }
    }
}
