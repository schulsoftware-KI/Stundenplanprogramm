using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P5b-2 – 5.3b Zuständigkeiten ins neue Schuljahr (Entscheidungen 219, 240, 249–251).
//
//  Jahresablauf → Karte „Schuljahreswechsel“ → Punkt „Zuständigkeiten“ → Knopf „Zuständigkeiten für 2027/28 übernehmen…“
//  (sichtbar, sobald „Aktuelles Schuljahr“ das neue ist und es Klassen des neuen Jahres gibt, also nach dem
//  Jahreswechsel-Import). Eine Zeile je Konto, das (nur) für das Vorjahr zuständig ist (Klassenleitung, Stufenkoordination):
//
//    Klassenleitung  (1) laut Klassen.dat des neuen Jahres leitet das Kürzel des Kontos Klasse(n) Y → „laut SchILD“,
//                        vorausgewählt (Vorrang, 219).
//                    (2) sonst je bisheriger Klasse die Folgeklasse aus den Daten (240): die Klasse, in der jetzt mindestens
//                        die Hälfte der bisherigen Schüler (dieselben Datensätze, also gleiche Prüfsumme) ist →
//                        vorausgewählt; leitet sie laut SchILD jemand anderes: nur ein Hinweis (kein Häkchen).
//                    (3) keine Folgeklasse (Mehrheit abgegangen, Wechsel in die Oberstufe, keine Mehrheit) → „entfällt“,
//                        nicht vorausgewählt (250); angehakt: Zuständigkeit weg, Rolle Lehrkraft.
//    Stufenkoordination  Folgestufe aus der Mehrheit der Schüler (EF → Q1 → Q2, Jahrgang 07 → 08); Q2 → „entfällt“.
//
//  Übernehmen (251): nur, was die API dann noch genauso vorschlägt (Schlüssel = Konto + bisher + neu); setzt Zuständigkeit
//  und Schuljahr der Zuständigkeit, erneuert den Sicherheitsstempel (alte Anmeldungen enden), Kontenprotokoll je Konto, in
//  „Schuljahre“ des neuen Jahres ZustaendigkeitenAm/-Von, Protokoll der Einstellungen („Jahresablauf“, nur Zahlen). Ohne
//  Häkchen bleibt die Zuständigkeit im Vorjahr (Nachträge, 242). Gesperrte Konten sind nie vorausgewählt.
//
//  Bisherige Schüler einer Klasse bzw. Stufe = Schüler, deren letzte Klassen-Zeile im Vorjahr (Oberstufe: die Stufe ihrer
//  Kurse) dazu gehört und bis zum letzten Schultag des Vorjahres reicht (wer vorher abgegangen ist, zählt nicht). Im neuen
//  Jahr: die letzte Klassen-Zeile bzw. die Stufe der Kurse; ohne Zeile = abgegangen bzw. nicht mehr da.
//  Die Rechnung (ZustaendigkeitVorschlaege) braucht keine Datenbank – zum Prüfen im Nachbau.
// =====================================================================

public static partial class Jahresablauf
{
    public const string ZkArtSchild = "schild", ZkArtFolge = "folge", ZkArtEntfaellt = "entfaellt", ZkArtHinweis = "hinweis";

    private const int ZkMax = 2000;

    /// <summary>Ein Konto mit Zuständigkeit im Vorjahr.</summary>
    public sealed record ZkKonto(int Id, string Benutzername, BenutzerRolle Rolle, string? Kuerzel, string Zustaendig, bool Gesperrt);

    /// <summary>Ein Schüler: Klasse bzw. Stufe am Ende des Vorjahres und im neuen Jahr (null = keine Zeile).</summary>
    public sealed record ZkSchueler(int Id, string? KlasseVj, string? StufeVj, string? KlasseNeu, string? StufeNeu);

    /// <summary>Ergebnis eines Eintrags der Zuständigkeit (z. B. „05A“): Folgeklasse/-stufe oder null mit Grund.</summary>
    public sealed record ZkFolgeErgebnis(string Eintrag, bool IstKlasse, string? Folge, int Anzahl, int Gesamt, string Grund);

    /// <summary>„07B“ → „07“, „EF“ → „EF“.</summary>
    public static string ZkStufeVon(string gruppe)
    {
        var g = (gruppe ?? "").Trim().ToUpperInvariant();
        return g.Length >= 2 && char.IsDigit(g[0]) && char.IsDigit(g[1]) ? g[..2] : g;
    }

    /// <summary>Folgeklasse bzw. Folgestufe eines Eintrags aus der Mehrheit der bisherigen Schüler (≥ Hälfte, 240).</summary>
    public static ZkFolgeErgebnis ZkFolge(string eintrag, IReadOnlySet<string> klassenVj, IReadOnlyList<ZkSchueler> schueler)
    {
        string e = eintrag.Trim().ToUpperInvariant();
        bool istKlasse = klassenVj.Contains(e);
        var bisher = istKlasse
            ? schueler.Where(s => s.KlasseVj != null && Cmp.Equals(s.KlasseVj, e)).ToList()
            : schueler.Where(s => s.StufeVj != null && Cmp.Equals(s.StufeVj, e)).ToList();
        int n = bisher.Count;
        if (n == 0)
            return new ZkFolgeErgebnis(e, istKlasse, null, 0, 0, istKlasse
                ? $"in {e} gab es am Ende des Vorjahres keine Schüler"
                : $"zur Stufe {e} gab es am Ende des Vorjahres keine Schüler");

        var ziele = bisher.Select(s => istKlasse ? s.KlasseNeu : s.StufeNeu).ToList();
        var beste = ziele.Where(z => z != null).GroupBy(z => z!, Cmp)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Ziel: g.Key, Anzahl: g.Count())).FirstOrDefault();
        if (beste.Ziel != null && beste.Anzahl * 2 >= n)
            return new ZkFolgeErgebnis(e, istKlasse, beste.Ziel, beste.Anzahl, n, "");

        int weg = bisher.Count(s => s.StufeNeu == null);
        string grund;
        if (weg * 2 >= n)
            grund = $"{weg} von {n} Schülern sind nicht mehr da (Abgang bzw. Abschluss)";
        else if (istKlasse)
        {
            var stufe = bisher.Where(s => s.KlasseNeu == null && s.StufeNeu != null).GroupBy(s => s.StufeNeu!, Cmp)
                .OrderByDescending(g => g.Count()).Select(g => (Stufe: g.Key, Anzahl: g.Count())).FirstOrDefault();
            grund = stufe.Stufe != null && stufe.Anzahl * 2 >= n
                ? $"{stufe.Anzahl} von {n} Schülern sind jetzt in der Oberstufe ({stufe.Stufe}) – dort gibt es keine Klasse"
                : $"keine Klasse mit mindestens der Hälfte der {n} Schüler" +
                  (beste.Ziel != null ? $" (am meisten: {beste.Ziel} mit {beste.Anzahl})" : "");
        }
        else
            grund = $"keine Stufe mit mindestens der Hälfte der {n} Schüler" +
                    (beste.Ziel != null ? $" (am meisten: {beste.Ziel} mit {beste.Anzahl})" : "");
        return new ZkFolgeErgebnis(e, istKlasse, null, beste.Ziel != null ? beste.Anzahl : 0, n, grund);
    }

    private static string ZkFolgeText(ZkFolgeErgebnis f, string vorjahr, string neu)
        => f.Folge != null
            ? $"{f.Eintrag} ({vorjahr}) → {f.Folge} ({neu}) – Folge{(f.IstKlasse ? "klasse" : "stufe")} ({f.Anzahl} von {f.Gesamt} Schülern)"
            : $"{f.Eintrag} ({vorjahr}) entfällt – {f.Grund}";

    /// <summary>
    /// Vorschläge je Konto (249–251). <paramref name="leitungNeu"/>: Klassen des neuen Jahres → Klassenleitung laut Klassen.dat
    /// (Plan-Kürzel; leer = keine Angabe). <paramref name="klassenVj"/>: Klassen des Vorjahres (groß geschrieben).
    /// </summary>
    public static List<JaZustaendigkeitDto> ZustaendigkeitVorschlaege(string vorjahr, string neu, IReadOnlyList<ZkKonto> konten,
        IReadOnlyList<ZkSchueler> schueler, IReadOnlySet<string> klassenVj,
        IReadOnlyDictionary<string, IReadOnlyList<string>> leitungNeu)
    {
        var liste = new List<JaZustaendigkeitDto>();
        bool mitLeitung = leitungNeu.Values.Any(v => v.Count > 0);

        foreach (var k in konten.OrderBy(x => x.Benutzername, Cmp))
        {
            var eintraege = KontenService.Zustaendigkeiten(k.Zustaendig);
            if (eintraege.Count == 0) continue;
            string bisher = string.Join(", ", eintraege);
            string rolle = KontenService.RollenText(k.Rolle);
            string? kz = string.IsNullOrWhiteSpace(k.Kuerzel) ? null : k.Kuerzel.Trim();
            var folgen = eintraege.Select(x => ZkFolge(x, klassenVj, schueler)).ToList();
            var texte = folgen.Select(f => ZkFolgeText(f, vorjahr, neu)).ToList();

            string? zielText = null;
            string art;
            var hinweise = new List<string>();

            // (1) Klassenleitung laut Klassen.dat des neuen Jahres (Vorrang, 219)
            var lautSchild = k.Rolle == BenutzerRolle.Klassenlehrer && kz != null && mitLeitung
                ? leitungNeu.Where(kv => kv.Value.Any(x => Cmp.Equals(x, kz))).Select(kv => kv.Key)
                    .OrderBy(x => x, Cmp).ToList()
                : new List<string>();
            if (lautSchild.Count > 0)
            {
                art = ZkArtSchild;
                zielText = string.Join(", ", lautSchild);
                var anders = folgen.Where(f => f.Folge != null && !lautSchild.Contains(f.Folge, Cmp)).Select(f => f.Folge!).ToList();
                texte = new List<string> { $"{bisher} ({vorjahr}) → {zielText} ({neu}) – laut SchILD (Klassen.dat {neu})" };
                if (anders.Count > 0)
                    hinweise.Add($"Folgeklasse aus den Daten wäre {string.Join(", ", anders.Distinct(Cmp))} – SchILD geht vor.");
            }
            else
            {
                // (2) Folgeklasse bzw. Folgestufe; leitet die Folgeklasse laut SchILD jemand anderes: nur Hinweis
                var frei = new List<string>();
                foreach (var fe in folgen.Where(x => x.Folge != null))
                {
                    if (fe.IstKlasse && k.Rolle == BenutzerRolle.Klassenlehrer
                                     && leitungNeu.TryGetValue(fe.Folge!, out var laut) && laut.Count > 0)
                    {
                        hinweise.Add($"Die Folgeklasse {fe.Folge} leitet laut SchILD {string.Join(", ", laut)} – dafür kein " +
                                     "Vorschlag (bei Bedarf von Hand: Konten verwalten → „Bearbeiten“).");
                        continue;
                    }
                    if (!frei.Contains(fe.Folge!, Cmp)) frei.Add(fe.Folge!);
                }
                if (frei.Count > 0)
                {
                    art = ZkArtFolge;
                    zielText = string.Join(", ", frei);
                }
                else if (hinweise.Count > 0) art = ZkArtHinweis;
                else if (kz == null)
                {
                    art = ZkArtHinweis;
                    hinweise.Add("Ohne Lehrerkürzel lässt sich die Rolle nicht auf Lehrkraft setzen – bei Bedarf von Hand ändern.");
                }
                else art = ZkArtEntfaellt;
            }

            string aenderung = art switch
            {
                ZkArtSchild or ZkArtFolge => $"zuständig {bisher} ({vorjahr}) → {zielText} ({neu})",
                ZkArtEntfaellt => $"Zuständigkeit {bisher} ({vorjahr}) entfällt – Rolle {rolle} → Lehrkraft",
                _ => "keine Änderung (kein Häkchen)"
            };
            string text = string.Join(" · ", texte) + (hinweise.Count > 0 ? " · " + string.Join(" ", hinweise) : "");
            string schluessel = art == ZkArtHinweis ? "" : $"z|{k.Id}|{bisher}|{zielText ?? "-"}";
            bool vorausgewaehlt = (art == ZkArtSchild || art == ZkArtFolge) && !k.Gesperrt;
            liste.Add(new JaZustaendigkeitDto(schluessel, art, k.Id, k.Benutzername, kz, rolle, bisher, zielText, text, aenderung,
                vorausgewaehlt, k.Gesperrt));
        }

        var rang = new Dictionary<string, int> { [ZkArtSchild] = 0, [ZkArtFolge] = 1, [ZkArtEntfaellt] = 2, [ZkArtHinweis] = 3 };
        return liste.OrderBy(z => rang[z.Art]).ThenBy(z => z.Bisher, Cmp).ThenBy(z => z.Konto, Cmp).ToList();
    }

    // -----------------------------------------------------------------
    //  Daten laden
    // -----------------------------------------------------------------

    private sealed record ZkLg(int Id, LerngruppeTyp Typ, string Bezeichnung, string Stufe, string? Klassenleitung);

    private sealed record ZkZeile(int SchuelerId, int LerngruppeId, DateOnly? Von, DateOnly? Bis);

    /// <summary>Klasse (letzte Klassen-Zeile) bzw. Stufe (Kurse) eines Schülers in einem Jahr; null = keine Zeile.</summary>
    private static (string? Klasse, string? Stufe) ZkGruppe(IEnumerable<ZkZeile> zeilen, IReadOnlyDictionary<int, ZkLg> lgs)
    {
        var eigene = zeilen.Where(z => lgs.ContainsKey(z.LerngruppeId)).ToList();
        if (eigene.Count == 0) return (null, null);
        var kl = eigene.Where(z => lgs[z.LerngruppeId].Typ == LerngruppeTyp.Klasse)
            .OrderByDescending(z => z.Bis ?? DateOnly.MaxValue).ThenByDescending(z => z.Von ?? DateOnly.MinValue)
            .FirstOrDefault();
        if (kl != null)
        {
            string klasse = lgs[kl.LerngruppeId].Bezeichnung.Trim().ToUpperInvariant();
            return (klasse, ZkStufeVon(klasse));
        }
        string? stufe = eigene.Where(z => lgs[z.LerngruppeId].Typ == LerngruppeTyp.Kurs)
            .Select(z => lgs[z.LerngruppeId].Stufe.Trim().ToUpperInvariant()).Where(x => x.Length > 0)
            .GroupBy(x => x, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault();
        return (null, stufe);
    }

    private sealed record ZkStand(string Vorjahr, string Neu, bool Moeglich, string? Grund, int KlassenNeu, int KlassenMitLeitung,
        List<JaZustaendigkeitDto> Zeilen, SchuljahrDb? Zeile);

    /// <summary>Vorschläge für den Wechsel <paramref name="w"/>.Schuljahr − 1 → <paramref name="w"/>.Schuljahr.</summary>
    private static async Task<ZkStand> ZustaendigkeitenStandAsync(AppDbContext db, SchulWerte w)
    {
        string neu = w.Schuljahr;
        string vorjahr = Verschieben(neu, -1);
        var zeile = await db.Schuljahre.AsNoTracking().FirstOrDefaultAsync(s => s.Schuljahr == neu);
        var leer = new List<JaZustaendigkeitDto>();
        if (vorjahr == neu)
            return new ZkStand(vorjahr, neu, false, $"„Aktuelles Schuljahr“ ({neu}) hat keine erkennbare Form (z. B. 2027/28).", 0, 0,
                leer, zeile);
        if (zeile?.Status == SchuljahrStatus.Abgeschlossen)
            return new ZkStand(vorjahr, neu, false, SchuljahrRechte.NurLesenText(neu), 0, 0, leer, zeile);

        var lgNeu = await db.Lerngruppen.AsNoTracking()
            .Where(l => l.Schuljahr == neu && (l.Typ == LerngruppeTyp.Klasse || l.Typ == LerngruppeTyp.Kurs))
            .Select(l => new ZkLg(l.Id, l.Typ, l.Bezeichnung, l.Stufe, l.Klassenleitung))
            .ToListAsync();
        // wie das Stundenblatt: je Bezeichnung die Klasse mit der größten Id
        var klassenNeu = lgNeu.Where(l => l.Typ == LerngruppeTyp.Klasse)
            .GroupBy(l => l.Bezeichnung.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First(), StringComparer.OrdinalIgnoreCase);
        if (klassenNeu.Count == 0)
            return new ZkStand(vorjahr, neu, false, $"Für {neu} gibt es noch keine Klassen – erst den Jahreswechsel-Import aus SchILD " +
                                                    "ausführen (Desktop: „SchILD-Daten…“).", 0, 0, leer, zeile);
        var leitungNeu = klassenNeu.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<string>)SchildImportService.KuerzelListe(kv.Value.Klassenleitung), StringComparer.OrdinalIgnoreCase);
        int mitLeitung = leitungNeu.Count(kv => kv.Value.Count > 0);

        var konten = (await db.Benutzer.AsNoTracking()
                .Where(b => (b.Rolle == BenutzerRolle.Klassenlehrer || b.Rolle == BenutzerRolle.Stufenkoordination)
                            && b.Zustaendig != null && b.Zustaendig != "" && b.ZustaendigSchuljahr == vorjahr)
                .Select(b => new { b.Id, b.Benutzername, b.Rolle, b.LehrerKuerzel, b.Zustaendig, b.Gesperrt })
                .ToListAsync())
            .Select(b => new ZkKonto(b.Id, b.Benutzername, b.Rolle, b.LehrerKuerzel, b.Zustaendig!, b.Gesperrt))
            .ToList();
        if (konten.Count == 0)
            return new ZkStand(vorjahr, neu, true, null, klassenNeu.Count, mitLeitung, leer, zeile);

        var lgVj = await db.Lerngruppen.AsNoTracking()
            .Where(l => l.Schuljahr == vorjahr && (l.Typ == LerngruppeTyp.Klasse || l.Typ == LerngruppeTyp.Kurs))
            .Select(l => new ZkLg(l.Id, l.Typ, l.Bezeichnung, l.Stufe, l.Klassenleitung))
            .ToListAsync();
        var lgVjById = lgVj.ToDictionary(l => l.Id);
        var lgNeuById = lgNeu.ToDictionary(l => l.Id);
        var klassenVj = lgVj.Where(l => l.Typ == LerngruppeTyp.Klasse).Select(l => l.Bezeichnung.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Zeilen des Vorjahres, die bis zum letzten Schultag reichen (wer vorher abgegangen ist, zählt nicht)
        var kalVj = (await KalenderLadenAsync(db, vorjahr)).FirstOrDefault();
        DateOnly? bisMind = kalVj?.Letzter;
        var zugVj = (await db.Zugehoerigkeiten.AsNoTracking().Where(z => z.Schuljahr == vorjahr)
                .Select(z => new ZkZeile(z.SchuelerId, z.LerngruppeId, z.GueltigVon, z.GueltigBis)).ToListAsync())
            .Where(z => bisMind == null || z.Bis == null || z.Bis >= bisMind)
            .ToList();
        var zugNeu = await db.Zugehoerigkeiten.AsNoTracking().Where(z => z.Schuljahr == neu)
            .Select(z => new ZkZeile(z.SchuelerId, z.LerngruppeId, z.GueltigVon, z.GueltigBis)).ToListAsync();
        var neuJe = zugNeu.GroupBy(z => z.SchuelerId).ToDictionary(g => g.Key, g => g.ToList());

        var schueler = new List<ZkSchueler>();
        foreach (var je in zugVj.GroupBy(z => z.SchuelerId))
        {
            var (klVj, stVj) = ZkGruppe(je, lgVjById);
            if (klVj == null && stVj == null) continue;
            (string? klN, string? stN) = neuJe.TryGetValue(je.Key, out var zn)
                ? ZkGruppe(zn, lgNeuById) : ((string?)null, (string?)null);
            schueler.Add(new ZkSchueler(je.Key, klVj, stVj, klN, stN));
        }

        var zeilen = ZustaendigkeitVorschlaege(vorjahr, neu, konten, schueler, klassenVj, leitungNeu);
        return new ZkStand(vorjahr, neu, true, null, klassenNeu.Count, mitLeitung, zeilen, zeile);
    }

    /// <summary>GET api/jahresablauf/zustaendigkeiten – die Liste mit Häkchen.</summary>
    public static async Task<JaZustaendigkeitenDto> ZustaendigkeitenAsync(AppDbContext db, Schuleinstellungen einst)
    {
        var w = await einst.LesenAsync(db);
        var st = await ZustaendigkeitenStandAsync(db, w);
        return new JaZustaendigkeitenDto(st.Vorjahr, st.Neu, st.Moeglich, st.Grund, st.KlassenNeu, st.KlassenMitLeitung, st.Zeilen,
            st.Zeile?.ZustaendigkeitenAm, st.Zeile?.ZustaendigkeitenVon);
    }

    /// <summary>POST api/jahresablauf/zustaendigkeiten – die angehakten Vorschläge übernehmen (251).</summary>
    public static async Task<AktionErgebnis> ZustaendigkeitenUebernehmenAsync(AppDbContext db, Schuleinstellungen einst,
        string? eingabe, IReadOnlyList<string>? schluessel, string akteur, ILogger log)
    {
        var jahr = SchuljahrLesen(eingabe, out var fehler);
        if (fehler != null) return new AktionErgebnis(400, Fehler: fehler);
        var w = await einst.LesenAsync(db);
        if (jahr != w.Schuljahr)
            return new AktionErgebnis(400, Fehler: $"Zuständigkeiten übernehmen geht nur für das aktuelle Schuljahr ({w.Schuljahr}).");
        var wahl = (schluessel ?? Array.Empty<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        if (wahl.Count == 0) return new AktionErgebnis(400, Fehler: "Kein Konto angehakt.");
        if (wahl.Count > ZkMax) return new AktionErgebnis(400, Fehler: $"Höchstens {ZkMax} Konten auf einmal.");

        var st = await ZustaendigkeitenStandAsync(db, w);
        if (!st.Moeglich) return new AktionErgebnis(409, Fehler: st.Grund);
        var aktuell = st.Zeilen.Where(z => z.Schluessel.Length > 0)
            .ToDictionary(z => z.Schluessel, StringComparer.Ordinal);
        var gueltig = wahl.Where(aktuell.ContainsKey).Select(x => aktuell[x]).ToList();
        int nichtMehr = wahl.Count - gueltig.Count;
        if (gueltig.Count == 0)
            return new AktionErgebnis(409, Fehler: "Die gewählten Vorschläge sind nicht mehr aktuell – die Liste neu laden.");

        var ids = gueltig.Select(z => z.KontoId).ToList();
        var konten = await db.Benutzer.Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id);
        var jetzt = DateTime.UtcNow;
        int uebernommen = 0, entfallen = 0;
        foreach (var vs in gueltig)
        {
            if (!konten.TryGetValue(vs.KontoId, out var u)) { nichtMehr++; continue; }
            string vorher = KontenService.Zuordnung(u);
            string was;
            if (vs.Art == ZkArtEntfaellt)
            {
                u.Rolle = BenutzerRolle.Lehrer;
                u.Zustaendig = null;
                u.ZustaendigSchuljahr = null;
                was = $"Zuständigkeit {vs.Bisher} ({st.Vorjahr}) entfällt im Schuljahr {st.Neu} (keine Folgeklasse bzw. Folgestufe)";
                entfallen++;
            }
            else
            {
                u.Zustaendig = KontenService.ZustaendigText(vs.Neu);
                u.ZustaendigSchuljahr = st.Neu;
                was = $"Zuständigkeit ins Schuljahr {st.Neu} übernommen: {vs.Bisher} ({st.Vorjahr}) → {vs.Neu} ({st.Neu}), " +
                      (vs.Art == ZkArtSchild ? "laut SchILD" : "Folgeklasse bzw. Folgestufe aus den Daten");
                uebernommen++;
            }
            // Rolle und Zuständigkeit stehen im Token: alte Anmeldungen beenden (wie beim Bearbeiten).
            u.SicherheitsStempel = KontenService.NeuerStempel();
            KontenService.Protokoll(db, akteur, KontenService.Aktion.Geaendert, u,
                $"{was}; {vorher} → {KontenService.Zuordnung(u)} (Jahresablauf)");
        }

        var zeileNeu = await db.Schuljahre.FirstOrDefaultAsync(s => s.Schuljahr == st.Neu);
        if (zeileNeu == null)
        {
            zeileNeu = new SchuljahrDb { Schuljahr = st.Neu };
            db.Schuljahre.Add(zeileNeu);
        }
        zeileNeu.ZustaendigkeitenAm = jetzt;
        zeileNeu.ZustaendigkeitenVon = akteur;
        Protokoll(db, akteur, $"Zuständigkeiten ins Schuljahr {st.Neu} übernommen",
            $"{uebernommen} übernommen, {entfallen} entfallen" + (nichtMehr > 0 ? $", {nichtMehr} nicht mehr aktuell" : "") +
            $" (von {st.Zeilen.Count} Konten mit Zuständigkeit für {st.Vorjahr})");
        await db.SaveChangesAsync();
        log.LogInformation("Jahresablauf: Zuständigkeiten ins Schuljahr {Neu} übernommen von {Akteur} ({Uebernommen} übernommen, " +
                           "{Entfallen} entfallen, {NichtMehr} nicht mehr aktuell).", st.Neu, akteur, uebernommen, entfallen, nichtMehr);

        var teile = new List<string>();
        if (uebernommen > 0) teile.Add($"{Anzahl(uebernommen, "Zuständigkeit", "Zuständigkeiten")} für {st.Neu} übernommen");
        if (entfallen > 0) teile.Add($"{Anzahl(entfallen, "Zuständigkeit entfallen", "Zuständigkeiten entfallen")} (Rolle Lehrkraft)");
        if (nichtMehr > 0) teile.Add($"{nichtMehr} nicht mehr aktuell");
        return new AktionErgebnis(200, Meldung: string.Join(" · ", teile) + ". Die Konten werden auf ihren Geräten abgemeldet.");
    }

    // -----------------------------------------------------------------
    //  Punkt „Zuständigkeiten“ der Karte (Ampel, 249)
    // -----------------------------------------------------------------

    /// <summary>
    /// Punkt für den Wechsel <paramref name="jahr"/> → <paramref name="neu"/>. <paramref name="nurWennOffen"/>: null, wenn kein
    /// Konto mehr nur für <paramref name="jahr"/> zuständig ist (für ein schon abgeschlossenes Vorjahr).
    /// </summary>
    private static async Task<JaPunktDto?> ZustaendigkeitPunktAsync(AppDbContext db, SchulWerte w, string jahr, string neu,
        bool nurWennOffen = false)
    {
        const string titel = "Zuständigkeiten (Klassenleitung, Stufenkoordination)";
        var liste = await db.Benutzer.AsNoTracking()
            .Where(b => b.Zustaendig != null && b.Zustaendig != ""
                        && (b.Rolle == BenutzerRolle.Klassenlehrer || b.Rolle == BenutzerRolle.Stufenkoordination))
            .Select(b => b.ZustaendigSchuljahr)
            .ToListAsync();
        int nurAlt = liste.Count(j => j == jahr);
        if (nurWennOffen && nurAlt == 0) return null;
        var jeJahr = liste.GroupBy(j => string.IsNullOrWhiteSpace(j) ? "ohne Schuljahr" : j!)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {Anzahl(g.Count(), "Konto", "Konten")}")
            .ToList();
        IReadOnlyList<string>? jeJahrListe = jeJahr.Count > 0 ? jeJahr : null;
        var ziele = new[] { new JaZielDto("konten", "Konten verwalten") };
        string id = nurWennOffen ? "zustaendigVorjahr" : "zustaendig";
        if (liste.Count == 0)
            return new JaPunktDto(id, titel, Grau, "Keine Konten mit Zuständigkeit.", Ziele: ziele);

        string grundsatz = $"Eine Zuständigkeit gilt nur für die Daten ihres Schuljahres: Wer nur für {jahr} zuständig ist, sieht " +
                           $"die Klassen von {neu} nicht und betreut deren Schülerkonten nicht.";
        if (w.Schuljahr != neu)
            return new JaPunktDto(id, titel, Grau,
                $"{grundsatz} Nach „Schuljahr {neu} vorbereiten…“ und dem Jahreswechsel-Import schlägt hier ein Knopf je Konto " +
                $"die Zuständigkeit für {neu} vor (laut SchILD bzw. Folgeklasse).", jeJahrListe, Ziele: ziele);
        if (!await db.Lerngruppen.AnyAsync(l => l.Schuljahr == neu && l.Typ == LerngruppeTyp.Klasse))
            return new JaPunktDto(id, titel, Grau,
                $"{grundsatz} Für {neu} gibt es noch keine Klassen – der Knopf „Zuständigkeiten für {neu} übernehmen…“ erscheint " +
                "nach dem Jahreswechsel-Import aus SchILD.", jeJahrListe, Ziele: ziele);

        var zeile = await db.Schuljahre.AsNoTracking().FirstOrDefaultAsync(s => s.Schuljahr == neu);
        var aktion = new[] { new JaAktionDto("zustaendigkeiten", $"Zuständigkeiten für {neu} übernehmen…", neu) };
        string uebernommen = zeile?.ZustaendigkeitenAm != null
            ? $"Übernommen {Zeitpunkt(zeile.ZustaendigkeitenAm, zeile.ZustaendigkeitenVon)}." : "";
        if (nurAlt == 0)
            return new JaPunktDto(id, titel, Gruen,
                $"Kein Konto ist mehr nur für {jahr} zuständig. {uebernommen}".TrimEnd(), jeJahrListe, Ziele: ziele);
        if (zeile?.ZustaendigkeitenAm != null)
            return new JaPunktDto(id, titel, Gruen,
                $"{uebernommen} {Anzahl(nurAlt, "Konto bleibt", "Konten bleiben")} bewusst für {jahr} zuständig (ohne Häkchen, z. B. " +
                "für Nachträge) – ändern über den Knopf bzw. Konto → „Bearbeiten“.", jeJahrListe, Ziele: ziele, Aktionen: aktion);
        return new JaPunktDto(id, titel, Gelb,
            $"{Anzahl(nurAlt, "Konto ist", "Konten sind")} nur für {jahr} zuständig: Diese Personen sehen die Klassen von {neu} " +
            $"nicht und betreuen deren Schülerkonten nicht. Der Knopf schlägt je Konto die Zuständigkeit für {neu} vor: laut " +
            "SchILD (Klassen.dat) bzw. die Folgeklasse, in die die meisten bisherigen Schüler gegangen sind (mit Häkchen, " +
            "nichts geschieht von selbst).",
            jeJahrListe, Ziele: ziele, Aktionen: aktion);
    }
}
