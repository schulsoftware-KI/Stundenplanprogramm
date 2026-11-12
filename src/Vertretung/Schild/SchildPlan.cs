#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildPlan – Phase 5.2a: was der Prüflauf vom Stundenplan braucht
    //  (aus dem Grundplan der gewählten Plan-Spalte, wie „Grundplan
    //  veröffentlichen…“, ohne zu senden).
    //
    //  - Klassen und Stufen für den Abgleich (5.2a-1).
    //  - Kurse (5.2a-2): Oberstufenkurse (Kurs-Slots EF/Q1/Q2, Schlüssel =
    //    Schülergruppe der UV bzw. „Q1 LsG3“) und Sek-I-Gruppen (Klassen-Slots
    //    mit Schülergruppe, z. B. „ER_05A05B05C05D_L88“) mit Fächern, Kursart,
    //    Nummer, Lehrkräften und Zeiten.
    //  - Klassenunterricht ohne Schülergruppe je Klasse (Fach, Lehrkraft).
    //  AGs ohne Klasse („Sonstige“) gehören nicht dazu (Entscheidung 9).
    //  Alle Vergleichswerte in Großbuchstaben (Kürzel, Fächer, Klassen).
    // =====================================================================

    public sealed class SchildPlanKurs
    {
        public string Schluessel { get; set; } = "";
        /// <summary>Schlüssel in Großbuchstaben (zum Vergleichen).</summary>
        public string Gross { get; set; } = "";
        public bool Oberstufe { get; set; }
        public HashSet<string> Stufen { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Klassen { get; } = new(StringComparer.Ordinal);
        /// <summary>Basis-Fächer (z. B. „P“ und „PH“), Großbuchstaben.</summary>
        public HashSet<string> Faecher { get; } = new(StringComparer.Ordinal);
        /// <summary>Fächer wie im Plan („P G1“, „PH G1“) – nur zur Anzeige.</summary>
        public SortedSet<string> FachTexte { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Lehrer { get; } = new(StringComparer.Ordinal);
        public HashSet<(int Tag, int Stunde)> Zeiten { get; } = new();
        /// <summary>GK | LK | VTF | ZK | "" (aus dem Plan-Fach „D G1“, „M L2“, „E Ver1“ …).</summary>
        public string Art { get; set; } = "";
        public int Nr { get; set; }

        internal readonly Dictionary<string, int> ArtZaehler = new(StringComparer.Ordinal);
        internal readonly Dictionary<int, int> NrZaehler = new();

        public HashSet<string> Jahrgaenge()
        {
            var j = new HashSet<string>(Stufen, StringComparer.Ordinal);
            foreach (var k in Klassen) j.Add(k.Length >= 2 ? k[..2] : k);
            return j;
        }

        public string StufeText => Oberstufe
            ? string.Join(", ", Stufen.OrderBy(s => s, StringComparer.Ordinal))
            : string.Join(", ", Klassen.OrderBy(s => s, StringComparer.Ordinal));

        public string FachText => string.Join(", ", FachTexte);
        public string LehrerText => string.Join(", ", Lehrer.OrderBy(s => s, StringComparer.Ordinal));

        /// <summary>Text für Auswahllisten: Schlüssel · Fächer · Lehrkräfte.</summary>
        public string Anzeige => $"{Schluessel}  ·  {FachText}  ·  {LehrerText}";
    }

    public sealed class SchildPlanInfo
    {
        public string Bezeichnung { get; set; } = "";
        /// <summary>Gesetzt, wenn die Schülergruppen der UV nicht lesbar waren (z. B. Datei in Excel offen).</summary>
        public string? Hinweis { get; set; }
        public HashSet<string> Klassen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Stufen { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Oberstufenkurse und Sek-I-Gruppen, Schlüssel in Großbuchstaben.</summary>
        public Dictionary<string, SchildPlanKurs> Kurse { get; } = new(StringComparer.Ordinal);
        /// <summary>Basis-Fächer aus Klassen- und Kursunterricht (Großbuchstaben).</summary>
        public HashSet<string> Faecher { get; } = new(StringComparer.Ordinal);
        /// <summary>Lehrkräfte aus Klassen- und Kursunterricht (Großbuchstaben).</summary>
        public HashSet<string> Kuerzel { get; } = new(StringComparer.Ordinal);
        /// <summary>Kürzel in der Schreibweise des Plans (Anzeige), Schlüssel = Großbuchstaben.</summary>
        public Dictionary<string, string> KuerzelAnzeige { get; } = new(StringComparer.Ordinal);
        /// <summary>Klassenunterricht ohne Schülergruppe: Klasse → (Fach, Lehrkraft), alles Großbuchstaben.</summary>
        public Dictionary<string, HashSet<(string Fach, string Lehrer)>> KlassenUnterricht { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, string> TagNamen { get; } = new();

        public SchildPlanKurs? Kurs(string? schluessel)
            => schluessel != null && Kurse.TryGetValue(schluessel.Trim().ToUpperInvariant(), out var p) ? p : null;

        public string KuerzelText(string upper) => KuerzelAnzeige.TryGetValue(upper, out var k) ? k : upper;

        public static SchildPlanInfo Aus(GrundplanPushDto dto, string? hinweis = null)
        {
            var p = new SchildPlanInfo { Bezeichnung = dto.Bezeichnung ?? "", Hinweis = hinweis };
            foreach (var s in dto.Slots)
            {
                if (s.Art != "Unterricht") continue;
                string lehrer = (s.Lehrer ?? "").Trim(), key = (s.LerngruppeKey ?? "").Trim();
                if (lehrer.Length == 0 || key.Length == 0) continue;
                string L = lehrer.ToUpperInvariant();
                var (basis, art, nr) = SchildGenerator.ZerlegePlanFach(s.Fach);
                p.TagNamen.TryAdd(s.WTagIndex, s.WTag);

                SchildPlanKurs kurs;
                if (s.LerngruppeTyp == "Kurs")
                {
                    string stufe = (s.Klasse ?? "").Trim().ToUpperInvariant();
                    if (stufe.Length > 0) p.Stufen.Add(stufe);
                    kurs = p.HoleKurs(key);
                    if (stufe.Length > 0) kurs.Stufen.Add(stufe);
                }
                else if (s.LerngruppeTyp == "Klasse")
                {
                    string kl = key.ToUpperInvariant();
                    p.Klassen.Add(kl);
                    string sg = (s.Schuelergruppe ?? "").Trim();
                    if (sg.Length == 0)
                    {
                        if (!p.KlassenUnterricht.TryGetValue(kl, out var ku))
                            p.KlassenUnterricht[kl] = ku = new HashSet<(string, string)>();
                        ku.Add((basis, L));
                        p.Faecher.Add(basis);
                        p.Kuerzel.Add(L);
                        p.KuerzelAnzeige.TryAdd(L, lehrer);
                        continue;
                    }
                    kurs = p.HoleKurs(sg);
                    kurs.Klassen.Add(kl);
                }
                else continue;   // AGs u. Ä. ohne Klasse (Entscheidung 9)

                p.Faecher.Add(basis);
                p.Kuerzel.Add(L);
                p.KuerzelAnzeige.TryAdd(L, lehrer);
                kurs.Faecher.Add(basis);
                kurs.FachTexte.Add(s.Fach ?? "");
                kurs.Lehrer.Add(L);
                kurs.Zeiten.Add((s.WTagIndex, s.Stunde));
                if (art.Length > 0) { kurs.ArtZaehler.TryGetValue(art, out int n); kurs.ArtZaehler[art] = n + 1; }
                if (nr > 0) { kurs.NrZaehler.TryGetValue(nr, out int n); kurs.NrZaehler[nr] = n + 1; }
            }
            foreach (var k in p.Kurse.Values)
            {
                k.Oberstufe = k.Stufen.Count > 0 && k.Klassen.Count == 0;
                k.Art = k.ArtZaehler.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                                    .Select(kv => kv.Key).FirstOrDefault() ?? "";
                k.Nr = k.NrZaehler.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).FirstOrDefault();
            }
            return p;
        }

        private SchildPlanKurs HoleKurs(string key)
        {
            string k = key.Trim().ToUpperInvariant();
            if (!Kurse.TryGetValue(k, out var kurs)) Kurse[k] = kurs = new SchildPlanKurs { Schluessel = key.Trim(), Gross = k };
            return kurs;
        }
    }
}
