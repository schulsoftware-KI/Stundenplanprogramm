#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildRueckweg – P4b (5.3a, Entscheidungen 210–213): Rückweg der Fehlstunden nach SchILD.
    //
    //  Die API liefert je Fehlstunde nur die Prüfsumme (ExtId), Datum, Stunde, Fach und Lehrkraft laut Plan und die
    //  Bewertung – keine Namen (210). Hier werden die Prüfsummen mit dem Schlüssel dieses Windows-Benutzers aus den
    //  Schülern des SchILD-Ordners gebildet (gleiche Eingabe wie beim Import, SchildNutzlast.ExtId) und so Nachname (mit
    //  „#Klasse“ wie im Export), Vorname, Geburtsdatum und Klasse des Abschnitts ergänzt.
    //
    //  Inhalt von SchuelerFehlstunden.dat (211): je Fehlstunde J (entschuldigt, auch beurlaubt) bzw. N (unentschuldigt);
    //  zusammenhängende Stunden eines Tages mit gleichem Fach, gleicher Bewertung und gleicher Lehrkraft → eine Zeile
    //  von_Std.–bis_Std., Fehlstd. = Anzahl. Fach über die Fächertabelle der Zuordnung (Plan → SchILD; mehrdeutig → das
    //  Fach aus den Leistungsdaten des Schülers), Lehrkraft über die Kürzeltabelle (ohne festes Kürzel bleibt die Spalte
    //  leer). Jahr = Beginnjahr, Abschnitt = gewählter Abschnitt; Stunden außerhalb des Abschnitts (1./2. Halbjahr laut
    //  API) werden nicht geschrieben.
    //
    //  Probedatei (213): nur ausgewählte Klassen/Stufen bzw. ein kürzerer Zeitraum. Übertragene Zeiträume merkt sich der
    //  Desktop in schild_rueckweg.json (bzw. _pseudo) neben der Plandatei – ohne Personendaten (212).
    //
    //  Bericht und Log: nur Zahlen, Fächer, Kürzel, Klassen – keine Namen.
    // =====================================================================

    // ---- Antwort der API (camelCase, VertretungApi/Dtos/RueckwegDtos.cs) ----

    public sealed class SchildRueckwegZahlen
    {
        public int Entschuldigt { get; set; }
        public int Unentschuldigt { get; set; }
        public int Beurlaubt { get; set; }
        public int Offen { get; set; }
        public int Verspaetungen { get; set; }
        public int Schulveranstaltung { get; set; }
        public int Doppelt { get; set; }
        public int OhnePruefsumme { get; set; }
        public int SchuelerOhnePruefsumme { get; set; }
        public int Schueler { get; set; }
    }

    public sealed class SchildRueckwegZeileDto
    {
        public string ExtId { get; set; } = "";
        public DateOnly Datum { get; set; }
        public int Stunde { get; set; }
        public string Fach { get; set; } = "";
        public string Lehrkraft { get; set; } = "";
        /// <summary>entschuldigt | unentschuldigt | beurlaubt</summary>
        public string Art { get; set; } = "";
    }

    public sealed class SchildRueckwegAbruf
    {
        public string Schuljahr { get; set; } = "";
        public DateOnly Von { get; set; }
        public DateOnly Bis { get; set; }
        public bool Pseudo { get; set; }
        public DateOnly? KalenderStart { get; set; }
        public DateOnly? KalenderEnde { get; set; }
        public DateOnly? HalbjahrAb { get; set; }
        public string Akteur { get; set; } = "";
        public string Merkzeichen { get; set; } = "";
        public SchildRueckwegZahlen Zahlen { get; set; } = new();
        public int NichtErfasst { get; set; }
        public List<SchildRueckwegZeileDto> Zeilen { get; set; } = new();
    }

    /// <summary>Desktop → API: welche Datei geschrieben wurde (ohne Namen).</summary>
    public sealed class SchildRueckwegMeldung
    {
        public string Merkzeichen { get; set; } = "";
        public DateOnly Von { get; set; }
        public DateOnly Bis { get; set; }
        public int Jahr { get; set; }
        public int Abschnitt { get; set; }
        public bool Probe { get; set; }
        public string? Klassen { get; set; }
        public int Zeilen { get; set; }
        public int Fehlstunden { get; set; }
        public int Entschuldigt { get; set; }
        public int Unentschuldigt { get; set; }
        public int Schueler { get; set; }
    }

    // ---- Berechnung ----

    public sealed class SchildRueckwegOptionen
    {
        public int Jahr { get; set; }
        public int Abschnitt { get; set; }
        /// <summary>Probedatei: kürzerer Zeitraum (null = der abgeholte).</summary>
        public DateOnly? Von { get; set; }
        public DateOnly? Bis { get; set; }
        /// <summary>Probedatei: nur diese Klassen (Sek I, „05A“) bzw. Stufen („EF“); null = alle.</summary>
        public HashSet<string>? Klassen { get; set; }
    }

    /// <summary>Eine Zeile der Datei – enthält Namen, bleibt im Fenster (nie in Bericht oder Log).</summary>
    public sealed class SchildFehlZeile
    {
        public string Nachname { get; set; } = "";       // mit „#Zusatz“ wie im Export
        public string Vorname { get; set; } = "";
        public DateOnly Geburtsdatum { get; set; }
        public string Klasse { get; set; } = "";
        public DateOnly Datum { get; set; }
        public string Fach { get; set; } = "";
        public int VonStd { get; set; }
        public int BisStd { get; set; }
        public int Anzahl { get; set; }
        public bool Entschuldigt { get; set; }
        public string Lehrkraft { get; set; } = "";
        public int Beurlaubt { get; set; }               // davon beurlaubt (nur Anzeige)

        public string Name => $"{Nachname}, {Vorname}";
        public string DatumText => SchildText.DatumText(Datum);
        public string StundenText => VonStd == BisStd ? $"{VonStd}." : $"{VonStd}.–{BisStd}.";
        public string JN => Entschuldigt ? (Beurlaubt == Anzahl ? "J (beurlaubt)" : "J") : "N";
    }

    public sealed class SchildRueckwegErgebnis
    {
        public List<string> Sperren { get; } = new();
        public List<string> Warnungen { get; } = new();
        public List<SchildFehlZeile> Zeilen { get; } = new();
        public int Jahr { get; set; }
        public int Abschnitt { get; set; }
        /// <summary>Zeitraum der Datei (abgeholt ∩ Abschnitt ∩ Probe-Zeitraum).</summary>
        public DateOnly Von { get; set; }
        public DateOnly Bis { get; set; }
        public bool Probe { get; set; }
        public List<string>? Klassen { get; set; }
        public int Schueler { get; set; }
        public int Fehlstunden { get; set; }
        public int J { get; set; }
        public int N { get; set; }
        public int Beurlaubt { get; set; }
        // nicht übertragen
        public int OhneTreffer { get; set; }
        public int SchuelerOhneTreffer { get; set; }
        public int AusserhalbAbschnitt { get; set; }
        public int AusserhalbZeitraum { get; set; }
        public int AndereKlassen { get; set; }
        public int PruefsummeDoppelt { get; set; }
        public SortedDictionary<string, int> OhneFach { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedDictionary<string, int> FachMehrdeutig { get; } = new(StringComparer.OrdinalIgnoreCase);
        // übertragen, aber ohne Lehrkraft
        public SortedDictionary<string, int> OhneKuerzel { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int NichtAnsi { get; set; }
        public string KlassenText => Klassen == null ? "alle Klassen" : "Klassen/Stufen " + string.Join(", ", Klassen);
    }

    public static class SchildRueckweg
    {
        public const string Entschuldigt = "entschuldigt", Unentschuldigt = "unentschuldigt", Beurlaubt = "beurlaubt";
        private static readonly HashSet<string> Oberstufe = new(StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };

        private static string D(DateOnly d) => SchildText.DatumText(d);

        /// <summary>Klasse (Sek I) bzw. Stufe (Oberstufe) eines Schülers für die Auswahl der Probedatei.</summary>
        public static string Gruppe(SchildSchueler s)
        {
            string stufe = SchildNutzlast.Stufe(s);
            if (SchildNutzlast.IstOberstufe(s)) return stufe.Length > 0 ? stufe : SchildText.KlasseNorm(s.Klasse);
            string k = SchildText.KlasseNorm(s.Klasse);
            return k.Length > 0 ? k : stufe;
        }

        /// <summary>Klassen und Stufen des Abschnitts (sortiert: Sek I, dann EF, Q1, Q2).</summary>
        public static List<string> Gruppen(SchildExport ex, int jahr, int abschnitt)
            => SchildNutzlast.Schueler(ex, jahr, abschnitt, out _).Select(Gruppe).Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => Oberstufe.Contains(g) ? 1 : 0).ThenBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Zeitraum des Abschnitts laut API (nur Abschnitt 1/2 mit bekanntem „2. Halbjahr ab“), sonst null.</summary>
        public static (DateOnly Von, DateOnly Bis)? AbschnittZeitraum(SchildRueckwegAbruf a, int abschnitt)
        {
            if (a.HalbjahrAb is not DateOnly h || a.KalenderStart is not DateOnly start || a.KalenderEnde is not DateOnly ende)
                return null;
            return abschnitt switch
            {
                1 => (start, h.AddDays(-1)),
                2 => (h, ende),
                _ => null
            };
        }

        public static SchildRueckwegErgebnis Berechne(SchildRueckwegAbruf a, SchildExport ex, SchildZuordnungErgebnis zu,
            SchildSchluessel schluessel, SchildRueckwegOptionen opt)
        {
            var r = new SchildRueckwegErgebnis { Jahr = opt.Jahr, Abschnitt = opt.Abschnitt };
            if (a.Pseudo != ex.IstPseudo)
                r.Sperren.Add(a.Pseudo
                    ? "Die Fehlstunden sind Pseudodaten, der SchILD-Ordner enthält echte Daten – bitte einen Pseudo-Export wählen."
                    : "Die Fehlstunden sind echte Daten, der SchILD-Ordner enthält Pseudodaten – bitte den echten Export wählen.");
            if (schluessel.Pseudo != ex.IstPseudo)
                r.Sperren.Add("Der Schlüssel passt nicht zur Art der Daten (Pseudodaten/echte Daten).");
            int jahrApi = SchildGenerator.Jahr(a.Schuljahr);
            if (jahrApi != opt.Jahr)
                r.Sperren.Add($"Die Fehlstunden gehören zum Schuljahr {a.Schuljahr}, der gewählte Abschnitt des Exports zum Jahr " +
                              $"{opt.Jahr} – bitte den Export dieses Schuljahres wählen.");
            if (!zu.Moeglich) r.Sperren.Add("Ohne Zuordnung SchILD ↔ Plan (Fächer, Kürzel) ist kein Rückweg möglich.");

            // ---- Zeitraum der Datei ----
            DateOnly von = a.Von, bis = a.Bis;
            var abz = AbschnittZeitraum(a, opt.Abschnitt);
            DateOnly? abVon = abz?.Von, abBis = abz?.Bis;
            if (abVon is DateOnly av && av > von) von = av;
            if (abBis is DateOnly ab && ab < bis) bis = ab;
            bool kuerzer = false;
            if (opt.Von is DateOnly pv && pv > von) { von = pv; kuerzer = true; }
            if (opt.Bis is DateOnly pb && pb < bis) { bis = pb; kuerzer = true; }
            r.Von = von;
            r.Bis = bis;
            r.Probe = kuerzer || opt.Klassen != null;
            r.Klassen = opt.Klassen?.OrderBy(g => Oberstufe.Contains(g) ? 1 : 0).ThenBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            if (bis < von)
                r.Sperren.Add(abVon is DateOnly sv && abBis is DateOnly sb
                    ? $"Der abgeholte Zeitraum ({D(a.Von)} – {D(a.Bis)}) liegt nicht im Abschnitt {opt.Abschnitt} " +
                      $"({D(sv)} – {D(sb)}) – bitte den passenden Abschnitt wählen."
                    : "Der gewählte Zeitraum ist leer.");
            if (r.Sperren.Count > 0) return r;

            // ---- Schüler des Abschnitts über die Prüfsumme ----
            var schueler = SchildNutzlast.Schueler(ex, opt.Jahr, opt.Abschnitt, out _);
            var nachExt = new Dictionary<string, SchildSchueler>(StringComparer.Ordinal);
            var doppelt = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in schueler)
            {
                string e = SchildNutzlast.ExtId(schluessel, s.Nachname, s.Vorname, s.Geburtsdatum);
                if (!nachExt.TryAdd(e, s)) doppelt.Add(e);
            }
            foreach (var e in doppelt) nachExt.Remove(e);

            // Fächer des Schülers laut Leistungsdaten (für mehrdeutige Plan-Fächer)
            var leistung = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var l in ex.Leistungen.Where(x => x.Jahr == opt.Jahr && x.Abschnitt == opt.Abschnitt))
            {
                if (!leistung.TryGetValue(l.PersonSchluessel, out var set))
                    leistung[l.PersonSchluessel] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(l.Fach.Trim());
            }

            // ---- Fehlstunden zuordnen ----
            var ohneTreffer = new HashSet<string>(StringComparer.Ordinal);
            var stunden = new List<(SchildSchueler S, DateOnly Datum, int Stunde, string Fach, bool J, bool Beurlaubt, string Lehrkraft)>();
            foreach (var z in a.Zeilen)
            {
                if ((abVon is DateOnly av2 && z.Datum < av2) || (abBis is DateOnly ab2 && z.Datum > ab2)) { r.AusserhalbAbschnitt++; continue; }
                if (z.Datum < von || z.Datum > bis) { r.AusserhalbZeitraum++; continue; }
                if (doppelt.Contains(z.ExtId)) { r.PruefsummeDoppelt++; continue; }
                if (!nachExt.TryGetValue(z.ExtId, out var s))
                {
                    r.OhneTreffer++;
                    ohneTreffer.Add(z.ExtId);
                    continue;
                }
                if (opt.Klassen != null && !opt.Klassen.Contains(Gruppe(s))) { r.AndereKlassen++; continue; }

                string basis = SchildGenerator.ZerlegePlanFach(z.Fach).Basis;
                string? fach = null;
                if (basis.Length > 0 && zu.FaecherRueck.TryGetValue(basis, out var kand) && kand.Count > 0)
                {
                    if (kand.Count == 1) fach = kand[0];
                    else if (leistung.TryGetValue(s.PersonSchluessel, out var lf))
                    {
                        var passend = kand.Where(f => lf.Contains(f)).ToList();
                        if (passend.Count == 1) fach = passend[0];
                    }
                    if (fach == null)
                    {
                        Zaehle(r.FachMehrdeutig, $"{basis} → {string.Join("/", kand)}");
                        continue;
                    }
                }
                else
                {
                    Zaehle(r.OhneFach, basis.Length > 0 ? basis : "(ohne Fach)");
                    continue;
                }

                string krz = (z.Lehrkraft ?? "").Trim().ToUpperInvariant();
                string lehrkraft = "";
                if (krz.Length > 0)
                {
                    if (zu.KuerzelRueck.TryGetValue(krz, out var kk) && kk.Count == 1) lehrkraft = kk[0];
                    else Zaehle(r.OhneKuerzel, krz + (kk != null && kk.Count > 1 ? " (mehrdeutig)" : ""));
                }
                bool j = z.Art != Unentschuldigt;
                stunden.Add((s, z.Datum, z.Stunde, fach, j, z.Art == Beurlaubt, lehrkraft));
            }
            r.SchuelerOhneTreffer = ohneTreffer.Count;

            // ---- zusammenhängende Stunden zu einer Zeile ----
            foreach (var g in stunden.GroupBy(w => (w.S.PersonSchluessel, w.Datum, Fach: w.Fach.ToUpperInvariant(), w.J,
                         Lehrkraft: w.Lehrkraft.ToUpperInvariant())))
            {
                SchildFehlZeile? offen = null;
                foreach (var x in g.OrderBy(y => y.Stunde))
                {
                    if (offen != null && x.Stunde == offen.BisStd) continue;                 // dieselbe Stunde (doppelt)
                    if (offen != null && x.Stunde == offen.BisStd + 1)
                    {
                        offen.BisStd = x.Stunde;
                        offen.Anzahl++;
                        if (x.Beurlaubt) offen.Beurlaubt++;
                        continue;
                    }
                    if (offen != null) r.Zeilen.Add(offen);
                    offen = new SchildFehlZeile
                    {
                        Nachname = x.S.Zusatz.Length > 0 ? $"{x.S.Nachname}#{x.S.Zusatz}" : x.S.Nachname,
                        Vorname = x.S.Vorname,
                        Geburtsdatum = x.S.Geburtsdatum,
                        Klasse = x.S.Klasse.Trim(),
                        Datum = x.Datum,
                        Fach = x.Fach,
                        VonStd = x.Stunde,
                        BisStd = x.Stunde,
                        Anzahl = 1,
                        Entschuldigt = x.J,
                        Lehrkraft = x.Lehrkraft,
                        Beurlaubt = x.Beurlaubt ? 1 : 0
                    };
                }
                if (offen != null) r.Zeilen.Add(offen);
            }
            r.Zeilen.Sort((p, q) =>
            {
                int c = string.Compare(Gruppe2(p.Klasse), Gruppe2(q.Klasse), StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.Compare(p.Nachname, q.Nachname, StringComparison.CurrentCultureIgnoreCase);
                if (c == 0) c = string.Compare(p.Vorname, q.Vorname, StringComparison.CurrentCultureIgnoreCase);
                if (c == 0) c = p.Geburtsdatum.CompareTo(q.Geburtsdatum);
                if (c == 0) c = p.Datum.CompareTo(q.Datum);
                return c != 0 ? c : p.VonStd.CompareTo(q.VonStd);
            });

            r.Fehlstunden = r.Zeilen.Sum(x => x.Anzahl);
            r.J = r.Zeilen.Where(x => x.Entschuldigt).Sum(x => x.Anzahl);
            r.N = r.Fehlstunden - r.J;
            r.Beurlaubt = r.Zeilen.Sum(x => x.Beurlaubt);
            r.Schueler = r.Zeilen.Select(x => (x.Nachname, x.Vorname, x.Geburtsdatum)).Distinct().Count();

            // ---- Warnungen (ohne Namen) ----
            var zahlen = a.Zahlen;
            if (zahlen.Offen > 0)
                r.Warnungen.Add($"{zahlen.Offen} offene Fehlstunden im abgeholten Zeitraum werden nicht übertragen – erst in der App " +
                                "bewerten, dann neu abholen.");
            if (a.NichtErfasst > 0)
                r.Warnungen.Add($"{a.NichtErfasst} Stunden ohne erfasste Anwesenheit im abgeholten Zeitraum.");
            if (r.OhneTreffer > 0)
                r.Warnungen.Add($"{r.OhneTreffer} Fehlstunden von {r.SchuelerOhneTreffer} Schülern ohne Treffer im Export (anderer " +
                                "Abschnitt bzw. Export, inzwischen abgegangen oder Name/Geburtsdatum seit dem letzten Import " +
                                "geändert – dann zuerst importieren).");
            if (r.OhneFach.Count > 0)
                r.Warnungen.Add($"{r.OhneFach.Values.Sum()} Fehlstunden mit Plan-Fächern ohne SchILD-Fach (Reiter „Zuordnung“, " +
                                "Fächer): " + string.Join(", ", r.OhneFach.Select(kv => $"{kv.Key} ({kv.Value})")));
            if (r.FachMehrdeutig.Count > 0)
                r.Warnungen.Add($"{r.FachMehrdeutig.Values.Sum()} Fehlstunden mit mehrdeutigem SchILD-Fach (auch die Leistungsdaten " +
                                "entscheiden nicht): " + string.Join(", ", r.FachMehrdeutig.Select(kv => $"{kv.Key} ({kv.Value})")));
            if (r.OhneKuerzel.Count > 0)
                r.Warnungen.Add("Ohne SchILD-Kürzel (Spalte „Lehrkraft“ bleibt leer): " +
                                string.Join(", ", r.OhneKuerzel.Select(kv => $"{kv.Key} ({kv.Value})")));
            if (r.PruefsummeDoppelt > 0)
                r.Warnungen.Add($"{r.PruefsummeDoppelt} Fehlstunden gehören zu einer Prüfsumme, die im Export zweimal vorkommt " +
                                "(gleicher Name und Geburtsdatum) – nicht übertragen.");
            return r;
        }

        private static string Gruppe2(string klasse)
        {
            string k = SchildText.KlasseNorm(klasse);
            return (Oberstufe.Contains(k) ? "1" : "0") + k;
        }

        private static void Zaehle(SortedDictionary<string, int> d, string schluessel)
        {
            d.TryGetValue(schluessel, out int n);
            d[schluessel] = n + 1;
        }

        // ---- Datei ----

        /// <summary>Zeichensatz der Datei: laut Profil (Rückweg, sonst allgemein), bei „auto“ wie die gelesenen Basisdaten.</summary>
        public static bool Utf8(SchildProfil profil, SchildExport ex)
        {
            string z = (profil.Rueckweg.Zeichensatz.Length > 0 ? profil.Rueckweg.Zeichensatz : profil.Zeichensatz).Trim().ToLowerInvariant();
            if (z is "utf-8" or "utf8") return true;
            if (z is "windows-1252" or "ansi" or "cp1252") return false;
            return (ex.Info(SchildDateiArt.Basisdaten)?.Zeichensatz ?? "").Contains("UTF-8", StringComparison.OrdinalIgnoreCase);
        }

        public static string ZeichensatzText(bool utf8) => utf8 ? "UTF-8 mit BOM" : "ANSI (Windows-1252)";

        private static bool Ansi(string s)
        {
            var b = SchildText.KodiereCp1252(s);
            for (int i = 0; i < s.Length; i++) if (b[i] == (byte)'?' && s[i] != '?') return false;
            return true;
        }

        /// <summary>Text der Datei (Kopf + Zeilen, CRLF). nichtAnsi = Zeilen mit Zeichen, die ANSI nicht kennt.</summary>
        public static string Text(SchildRueckwegErgebnis r, SchildProfil profil, out int nichtAnsi)
        {
            string t = string.IsNullOrEmpty(profil.Trennzeichen) ? "|" : profil.Trennzeichen;
            var kopf = profil.Rueckweg.Kopf.Count == SchildRueckwegProfil.StandardKopf.Length
                ? profil.Rueckweg.Kopf : SchildRueckwegProfil.StandardKopf.ToList();
            var sb = new StringBuilder();
            sb.Append(string.Join(t, kopf.Select(k => SchildText.Zelle(k, t)))).Append("\r\n");
            nichtAnsi = 0;
            string jahr = r.Jahr.ToString(CultureInfo.InvariantCulture), abschnitt = r.Abschnitt.ToString(CultureInfo.InvariantCulture);
            foreach (var z in r.Zeilen)
            {
                var zellen = new[]
                {
                    z.Nachname, z.Vorname, D(z.Geburtsdatum), z.Klasse, jahr, abschnitt, D(z.Datum), z.Fach,
                    z.VonStd.ToString(CultureInfo.InvariantCulture), z.BisStd.ToString(CultureInfo.InvariantCulture),
                    z.Anzahl.ToString(CultureInfo.InvariantCulture), z.Entschuldigt ? "J" : "N", z.Lehrkraft
                };
                string zeile = string.Join(t, zellen.Select(c => SchildText.Zelle(c, t)));
                if (!Ansi(zeile)) nichtAnsi++;
                sb.Append(zeile).Append("\r\n");
            }
            return sb.ToString();
        }

        public static byte[] Bytes(string text, bool utf8)
        {
            if (!utf8) return SchildText.KodiereCp1252(text);
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            return bom.Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();
        }

        /// <summary>Bericht ohne Namen (Bericht kopieren, Log).</summary>
        public static string Bericht(SchildRueckwegAbruf a, SchildRueckwegErgebnis r, string zeichensatz)
        {
            var z = a.Zahlen;
            var sb = new StringBuilder();
            sb.AppendLine($"Rückweg nach SchILD – {(a.Pseudo ? "Pseudodaten" : "echte Daten")}");
            sb.AppendLine($"Abgeholt: Schuljahr {a.Schuljahr}, {D(a.Von)} – {D(a.Bis)} (Code von {a.Akteur})");
            sb.AppendLine($"  übertragbar laut App: {z.Entschuldigt + z.Unentschuldigt + z.Beurlaubt} Fehlstunden (entschuldigt " +
                          $"{z.Entschuldigt}, beurlaubt {z.Beurlaubt}, unentschuldigt {z.Unentschuldigt}) von {z.Schueler} Schülern");
            sb.AppendLine($"  nicht übertragen laut App: offen {z.Offen}, Verspätungen {z.Verspaetungen}, schulische " +
                          $"Veranstaltung {z.Schulveranstaltung}, ohne Prüfsumme {z.OhnePruefsumme} ({z.SchuelerOhnePruefsumme} " +
                          $"Schüler), doppelt erfasst {z.Doppelt}; nicht erfasste Stunden {a.NichtErfasst}");
            sb.AppendLine();
            if (r.Sperren.Count > 0)
            {
                sb.AppendLine("Keine Datei möglich:");
                foreach (var s in r.Sperren) sb.AppendLine("  • " + s);
                return sb.ToString();
            }
            sb.AppendLine($"Datei: Jahr {r.Jahr}, Abschnitt {r.Abschnitt}, {D(r.Von)} – {D(r.Bis)}, {r.KlassenText}" +
                          (r.Probe ? " – PROBEDATEI" : ""));
            sb.AppendLine($"  {r.Zeilen.Count} Zeilen, {r.Fehlstunden} Fehlstunden (J {r.J}, davon beurlaubt {r.Beurlaubt}; N {r.N}) " +
                          $"von {r.Schueler} Schülern · Zeichensatz {zeichensatz}");
            sb.AppendLine($"  nicht in der Datei: außerhalb des Abschnitts {r.AusserhalbAbschnitt}, außerhalb des Zeitraums " +
                          $"{r.AusserhalbZeitraum}, andere Klassen {r.AndereKlassen}, ohne Treffer im Export {r.OhneTreffer} " +
                          $"({r.SchuelerOhneTreffer} Schüler), ohne SchILD-Fach {r.OhneFach.Values.Sum()}, Fach mehrdeutig " +
                          $"{r.FachMehrdeutig.Values.Sum()}, Prüfsumme doppelt {r.PruefsummeDoppelt}");
            if (r.Warnungen.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Hinweise:");
                foreach (var w in r.Warnungen) sb.AppendLine("  • " + w);
            }
            return sb.ToString();
        }

        // ---- übertragene Zeiträume (212) ----

        public sealed class MerkEintrag
        {
            public int Jahr { get; set; }
            public int Abschnitt { get; set; }
            public DateOnly Von { get; set; }
            public DateOnly Bis { get; set; }
            public bool Probe { get; set; }
            /// <summary>null = alle Klassen.</summary>
            public List<string>? Klassen { get; set; }
            public int Zeilen { get; set; }
            public int Fehlstunden { get; set; }
            public string Am { get; set; } = "";
            public string Ordner { get; set; } = "";

            public string Text => $"{Am}: Jahr {Jahr}, Abschnitt {Abschnitt}, {D(Von)} – {D(Bis)}, " +
                                  (Klassen == null ? "alle Klassen" : string.Join(", ", Klassen)) +
                                  (Probe ? " (Probedatei)" : "") + $" – {Fehlstunden} Fehlstunden";
        }

        public sealed class Merkliste
        {
            public string Hinweis { get; set; } =
                "Rückweg nach SchILD: bereits geschriebene Dateien (ohne Personendaten) – für die Warnung bei Überschneidungen.";
            public List<MerkEintrag> Eintraege { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string MerkName(bool pseudo) => pseudo ? "schild_rueckweg_pseudo.json" : "schild_rueckweg.json";

        public static Merkliste MerklisteLaden(string planOrdner, bool pseudo, out string? fehler)
        {
            fehler = null;
            string pfad = Path.Combine(planOrdner ?? "", MerkName(pseudo));
            if (!File.Exists(pfad)) return new Merkliste();
            try { return JsonSerializer.Deserialize<Merkliste>(File.ReadAllText(pfad), JsonOpt) ?? new Merkliste(); }
            catch (Exception ex)
            {
                fehler = $"{MerkName(pseudo)} ist nicht lesbar ({ex.Message}) – die Warnung bei Überschneidungen fehlt.";
                return new Merkliste();
            }
        }

        public static void MerklisteSpeichern(string planOrdner, bool pseudo, Merkliste m)
            => File.WriteAllText(Path.Combine(planOrdner, MerkName(pseudo)), JsonSerializer.Serialize(m, JsonOpt),
                new UTF8Encoding(false));

        /// <summary>Frühere Dateien, die sich mit dieser überschneiden (Jahr, Abschnitt, Zeitraum, Klassen).</summary>
        public static List<MerkEintrag> Ueberschneidungen(Merkliste m, SchildRueckwegErgebnis r)
            => m.Eintraege.Where(e => e.Jahr == r.Jahr && e.Abschnitt == r.Abschnitt && e.Von <= r.Bis && e.Bis >= r.Von
                                      && (e.Klassen == null || r.Klassen == null
                                          || e.Klassen.Intersect(r.Klassen, StringComparer.OrdinalIgnoreCase).Any()))
                          .ToList();
    }
}
