#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildNutzlast – Phase 5.2b-2b (Entscheidung 166): Was der Desktop an
    //  POST /api/schild/import schickt. Spiegelt VertretungApi/Dtos/
    //  SchildImportDtos.cs (JSON in camelCase).
    //
    //  Je Schüler: HMAC-Prüfsummen, Name, Vorname, Jahrgang, Klasse (Sek I),
    //  SchILD-Status, bei Status 0 das Aufnahmedatum und die Plan-Schlüssel der
    //  zugeordneten Kurse bzw. Sek-I-Gruppen (bei „gemeinsam“ der gemeinsame
    //  Plan-Kurs, 136). NIE: Geburtsdatum, SchILD-ID im Klartext, Noten,
    //  Kursart des Schülers, Daten der Lehrkräfte, Status 1 (Warteliste).
    //
    //  Übertragen werden nur Zuordnungen, die feststehen (automatisch, ohne
    //  Lehrkraft, von Hand) – offene, Vorschläge, Konflikte und Zeilen mit
    //  Zeitkonflikt nicht (Punkt H); die Vorschau nennt sie.
    //
    //  Eingabe der Prüfsummen (170): getrimmt, mehrfache Leerzeichen
    //  zusammengefasst, Unicode NFC, Großbuchstaben; Nachname ohne „#Klasse“
    //  (der Zusatz ändert sich mit der Klasse), Geburtsdatum JJJJ-MM-TT; je Art
    //  ein eigenes Präfix, damit sich die Prüfsummen nicht gleichen können.
    //  Diese Eingabe darf sich nie ändern – sonst erkennt der nächste Import
    //  die Schüler nicht wieder.
    // =====================================================================

    public sealed class SchildImportNutzlast
    {
        public bool Probelauf { get; set; } = true;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Vorschau { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<int>? Abgaenge { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Akteur { get; set; }
        public string Schuljahr { get; set; } = "";
        public int Abschnitt { get; set; }
        public DateOnly GiltAb { get; set; }
        public bool Pseudo { get; set; }
        public string? Plan { get; set; }
        public List<string> PlanSchluessel { get; set; } = new();
        public List<SchildNutzlastSchueler> Schueler { get; set; } = new();

        // ---- 5.2c-1 (Entscheidungen 188–190) ----
        /// <summary>Kennung des Schlüssels („1A2B-3C4D“) – verrät ihn nicht; die API vergleicht sie mit dem letzten Import.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Kennung { get; set; }
        /// <summary>Neu-Zuordnung über Name + Klasse (Schlüssel verloren).</summary>
        public bool Neuzuordnung { get; set; }
        /// <summary>Gewählte Paare (zusammenführen bzw. zuordnen); null = Vorauswahl der API.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<SchildPaar>? Paare { get; set; }

        /// <summary>Dieselbe Nutzlast mit anderen Paaren (neue Vorschau nach geänderten Häkchen).</summary>
        public SchildImportNutzlast MitPaaren(List<SchildPaar>? paare) => new()
        {
            Probelauf = true, Schuljahr = Schuljahr, Abschnitt = Abschnitt, GiltAb = GiltAb, Pseudo = Pseudo, Plan = Plan,
            PlanSchluessel = PlanSchluessel, Schueler = Schueler, Kennung = Kennung, Neuzuordnung = Neuzuordnung, Paare = paare
        };

        /// <summary>
        /// Dieselbe Nutzlast als Import (probelauf = false) mit Prüfsumme der Vorschau, bestätigten Abgängen und genau den Paaren,
        /// die die Vorschau angewendet hat.
        /// </summary>
        public SchildImportNutzlast AlsImport(string vorschau, List<int> abgaenge, string akteur, List<SchildPaar> paare) => new()
        {
            Probelauf = false, Vorschau = vorschau, Abgaenge = abgaenge, Akteur = akteur,
            Schuljahr = Schuljahr, Abschnitt = Abschnitt, GiltAb = GiltAb, Pseudo = Pseudo, Plan = Plan,
            PlanSchluessel = PlanSchluessel, Schueler = Schueler, Kennung = Kennung, Neuzuordnung = Neuzuordnung, Paare = paare
        };
    }

    /// <summary>5.2c-1: ein Paar Datensatz der Datenbank (Id) ↔ Schüler des Exports (Prüfsumme).</summary>
    public sealed class SchildPaar
    {
        public int Id { get; set; }
        public string ExtId { get; set; } = "";
    }

    public sealed class SchildNutzlastSchueler
    {
        public string ExtId { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ExtIdSchild { get; set; }
        public string? ExtIdVornameGeb { get; set; }
        public string? ExtIdNachnameGeb { get; set; }
        public string Nachname { get; set; } = "";
        public string Vorname { get; set; } = "";
        public string Stufe { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Klasse { get; set; }
        public int Status { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateOnly? Aufnahme { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Kurse { get; set; }
    }

    /// <summary>Ergebnis des Aufbaus: Nutzlast oder Sperren, dazu was nicht übertragen wird (ohne Namen).</summary>
    public sealed class SchildNutzlastErgebnis
    {
        public SchildImportNutzlast? Nutzlast { get; set; }
        /// <summary>Gründe, aus denen der Desktop nichts sendet (ohne Namen).</summary>
        public List<string> Sperren { get; } = new();
        /// <summary>Zuordnungen, die nicht übertragen werden: „„D-G1“ (Q1) – offen – 23 Teilnehmer“.</summary>
        public List<string> NichtUebertragen { get; } = new();
        public int NichtUebertragenZeilen { get; set; }
        public int NichtUebertragenTeilnehmer { get; set; }
        public int Schueler { get; set; }
        public int Abgaenge { get; set; }
        public int Warteliste { get; set; }
        public int MitSchildId { get; set; }
        public int Kursplaetze { get; set; }
        public int ZugeordneteKurse { get; set; }
    }

    public static class SchildNutzlast
    {
        private static readonly HashSet<string> Oberstufe = new(StringComparer.OrdinalIgnoreCase) { "EF", "Q1", "Q2" };
        private static readonly Regex Leerraum = new(@"\s+", RegexOptions.Compiled);

        // ---- Prüfsummen (Entscheidungen 82, 85, 170) ----

        /// <summary>Eingabe einer Prüfsumme: trimmen, Leerzeichen zusammenfassen, NFC, Großbuchstaben.</summary>
        public static string Norm(string? s)
            => Leerraum.Replace((s ?? "").Normalize(NormalizationForm.FormC).Trim(), " ").ToUpperInvariant();

        private static string Geb(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public static string ExtId(SchildSchluessel k, string nachname, string vorname, DateOnly geb)
            => k.Hmac($"P1\n{Norm(nachname)}\n{Norm(vorname)}\n{Geb(geb)}");

        public static string ExtIdVornameGeb(SchildSchluessel k, string vorname, DateOnly geb)
            => k.Hmac($"V1\n{Norm(vorname)}\n{Geb(geb)}");

        public static string ExtIdNachnameGeb(SchildSchluessel k, string nachname, DateOnly geb)
            => k.Hmac($"N1\n{Norm(nachname)}\n{Geb(geb)}");

        public static string ExtIdSchild(SchildSchluessel k, long schildId)
            => k.Hmac("S1\n" + schildId.ToString(CultureInfo.InvariantCulture));

        // ---- Zuordnung ----

        /// <summary>Wird diese Zuordnung übertragen? Nur feste Zuordnungen ohne Zeitkonflikt (Punkt H).</summary>
        public static bool WirdUebertragen(SchildZuordnungZeile u)
            => u.Plan != null && !u.IstOffen
               && u.Status is SchildZuordnungStatus.Automatisch or SchildZuordnungStatus.OhneLehrkraft or SchildZuordnungStatus.VonHand;

        /// <summary>Zuordnungen mit Teilnehmern, die nicht übertragen werden (Klassenunterricht zählt nicht dazu).</summary>
        public static List<SchildZuordnungZeile> NichtUebertragbar(SchildZuordnungErgebnis z)
            => z.Zeilen.Where(u => u.Teilnehmer > 0 && !WirdUebertragen(u) && u.Status != SchildZuordnungStatus.Klassenunterricht)
                       .ToList();

        // ---- Schüler ----

        /// <summary>Jahrgang für die API: „05“ … „10“, „EF“, „Q1“, „Q2“ (einstellige Zahl mit führender Null).</summary>
        public static string Stufe(SchildSchueler s)
        {
            string j = (s.Jahrgang ?? "").Trim().ToUpperInvariant();
            if (j.Length == 1 && char.IsDigit(j[0])) j = "0" + j;
            if (j.Length > 0) return j;
            string k = SchildText.KlasseNorm(s.Klasse);
            if (Oberstufe.Contains(k)) return k;
            return k.Length >= 2 && char.IsDigit(k[0]) && char.IsDigit(k[1]) ? k[..2] : "";
        }

        public static bool IstOberstufe(SchildSchueler s)
            => Oberstufe.Contains(SchildText.KlasseNorm(s.Klasse)) || Oberstufe.Contains(Stufe(s));

        /// <summary>Schüler des Abschnitts, die übertragen werden (Status 0, 2, 3, 6, 8, 9), in der Reihenfolge der Datei.</summary>
        public static List<SchildSchueler> Schueler(SchildExport ex, int jahr, int abschnitt, out int warteliste)
        {
            var liste = new List<SchildSchueler>();
            var gesehen = new HashSet<string>(StringComparer.Ordinal);
            int w = 0;
            foreach (var s in ex.Schueler.OrderBy(x => x.Zeile))
            {
                if ((s.Jahr != null && s.Jahr != jahr) || (s.Abschnitt != null && s.Abschnitt != abschnitt)) continue;
                if (s.Status == 1) { w++; continue; }
                if (s.Status is not (0 or 2 or 3 or 6 or 8 or 9)) continue;
                if (!gesehen.Add(s.PersonSchluessel)) continue;   // doppelt: der Prüflauf meldet einen Fehler
                liste.Add(s);
            }
            warteliste = w;
            return liste;
        }

        /// <summary>
        /// Baut die Nutzlast (Probelauf). Voraussetzung: Prüflauf ohne Fehler, Zuordnung möglich – sonst Sperre.
        /// </summary>
        public static SchildNutzlastErgebnis Baue(SchildExport ex, SchildPruefErgebnis erg, SchildPlanInfo plan,
            SchildSchluessel schluessel, string schuljahr, DateOnly giltAb)
        {
            var r = new SchildNutzlastErgebnis();
            var z = erg.Zuordnung;
            if (erg.Anzahl(SchildProblemArt.Fehler) > 0)
                r.Sperren.Add($"Der Prüflauf hat {erg.Anzahl(SchildProblemArt.Fehler)} Fehler – erst beheben.");
            if (z == null || !z.Moeglich)
                r.Sperren.Add("Ohne Zuordnung SchILD ↔ Plan ist kein Import möglich (Reiter „Zuordnung“).");
            if (schluessel.Pseudo != ex.IstPseudo)
                r.Sperren.Add("Der Schlüssel passt nicht zur Art der Daten (Pseudodaten/echte Daten).");
            if (r.Sperren.Count > 0 || z == null) return r;

            // ---- Zuordnungen: Plan-Schlüssel je Schüler ----
            var kurse = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var planSchluessel = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in z.Zeilen)
            {
                if (!WirdUebertragen(u)) continue;
                string p = u.Plan!;
                planSchluessel.Add(p);
                foreach (var pk in u.Teiln.Keys)
                {
                    if (!kurse.TryGetValue(pk, out var set)) kurse[pk] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(p);
                }
            }
            r.ZugeordneteKurse = planSchluessel.Count;
            foreach (var u in NichtUebertragbar(z))
            {
                r.NichtUebertragenZeilen++;
                r.NichtUebertragenTeilnehmer += u.Teilnehmer;
                string was = u.Art == SchildZuordnungArt.OhneKurs ? u.Schild : $"„{u.KursBez}“ ({u.JahrgangAnzeige})";
                r.NichtUebertragen.Add($"{was} – {u.StatusText} – {u.Teilnehmer} Teilnehmer");
            }

            // ---- Klassen, die ab jetzt aus SchILD kommen (Sek I, im Plan vorhanden) ----
            foreach (var k in ex.Klassen)
            {
                if ((k.Jahr != null && k.Jahr != erg.Jahr) || (k.Abschnitt != null && k.Abschnitt != erg.Abschnitt)) continue;
                string kn = SchildText.KlasseNorm(k.InternBez);
                if (kn.Length == 0 || Oberstufe.Contains(kn) || Oberstufe.Contains((k.Jahrgang ?? "").Trim())) continue;
                if (plan.Klassen.Contains(kn)) planSchluessel.Add(kn);
            }

            // ---- SchILD-ID (optional, SchuelerLernplattformen.dat) ----
            var ids = new Dictionary<string, long>(StringComparer.Ordinal);
            if (ex.Hat(SchildDateiArt.Lernplattformen))
                foreach (var lp in ex.Lernplattformen) ids.TryAdd(lp.PersonSchluessel, lp.Id);

            // ---- Schüler ----
            var schueler = Schueler(ex, erg.Jahr, erg.Abschnitt, out int warteliste);
            r.Warteliste = warteliste;
            var dtos = new List<SchildNutzlastSchueler>(schueler.Count);
            int ohneJahrgang = 0;
            foreach (var s in schueler)
            {
                string stufe = Stufe(s);
                if (stufe.Length == 0) { ohneJahrgang++; continue; }
                bool abgang = s.Status is 8 or 9;
                bool istExtern = s.Status == 6;
                string? klasse = null;
                if (!istExtern && !IstOberstufe(s))
                {
                    string kn = SchildText.KlasseNorm(s.Klasse);
                    klasse = kn.Length > 0 ? kn : null;
                }
                var d = new SchildNutzlastSchueler
                {
                    ExtId = ExtId(schluessel, s.Nachname, s.Vorname, s.Geburtsdatum),
                    ExtIdVornameGeb = ExtIdVornameGeb(schluessel, s.Vorname, s.Geburtsdatum),
                    ExtIdNachnameGeb = ExtIdNachnameGeb(schluessel, s.Nachname, s.Geburtsdatum),
                    Nachname = s.Nachname,
                    Vorname = s.Vorname,
                    Stufe = stufe,
                    Klasse = klasse,
                    Status = s.Status,
                    Aufnahme = s.Status == 0 ? s.Aufnahmedatum : null
                };
                if (ids.TryGetValue(s.PersonSchluessel, out long id))
                {
                    d.ExtIdSchild = ExtIdSchild(schluessel, id);
                    r.MitSchildId++;
                }
                if (!abgang && kurse.TryGetValue(s.PersonSchluessel, out var ks) && ks.Count > 0)
                {
                    d.Kurse = ks.ToList();
                    r.Kursplaetze += ks.Count;
                }
                if (abgang) r.Abgaenge++;
                dtos.Add(d);
            }
            if (ohneJahrgang > 0)
                r.Sperren.Add($"{ohneJahrgang} Schüler ohne Jahrgang (SchuelerBasisdaten.dat, Spalte „Jahrgang“) – bitte in " +
                              "SchILD ergänzen und neu exportieren.");
            if (dtos.Count == 0)
                r.Sperren.Add("Im gewählten Abschnitt stehen keine Schüler, die übertragen werden könnten.");
            r.Schueler = dtos.Count;
            if (r.Sperren.Count > 0) return r;

            r.Nutzlast = new SchildImportNutzlast
            {
                Probelauf = true,
                Schuljahr = schuljahr,
                Abschnitt = erg.Abschnitt,
                GiltAb = giltAb,
                Pseudo = ex.IstPseudo,
                Plan = string.IsNullOrWhiteSpace(plan.Bezeichnung) ? null : plan.Bezeichnung.Trim(),
                PlanSchluessel = planSchluessel.ToList(),
                Schueler = dtos,
                Kennung = schluessel.Kennung
            };
            return r;
        }
    }
}
