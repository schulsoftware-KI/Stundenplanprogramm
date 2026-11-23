using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P6b – 5.4 Auskunft nach Art. 15 DSGVO (Entscheidungen 234, 235, 238).
//
//  Schüler (234): Angaben nach Art. 15 Abs. 1 (Speicherdauer aus den aktuellen Fristen) und alle gespeicherten Daten
//  über alle Schuljahre – Stammdaten, Klassen/Kurse/Gruppen mit Gültigkeit, Konto mit Konten- und Anmeldeprotokoll,
//  jede Fehlstunde mit Bewertung und Grund, Entschuldigungen, Summen je Halbjahr, Schülereinträge (Kürzel der Verfasser
//  wie im Klassenbuch-PDF), Änderungsverlauf, Leseprotokoll OHNE Namen (Einzelansichten mit Datum, Ansicht und Rolle;
//  Listenansichten seiner Lerngruppen als Zahl je Monat und Rolle – EuGH C-579/21), Rückweg nach SchILD.
//  Konten (235): Personalkonten kompakt – Konto, Zuständigkeit, Name laut SchILD, Konten- und Anmeldeprotokoll,
//  Abwesenheitsgründe in veröffentlichten Vertretungen, Leseprotokoll als handelnde Person und Tätigkeiten im
//  Klassenbuch als Zahlen je Monat, Downloads je Monat und Art.
//
//  Das Dokument besteht aus Abschnitten (AuskunftAbschnitt) – P7 ergänzt Abschnitte für Elternkonten (238). Das PDF
//  baut Services/AuskunftPdf.cs. Wer die Auskunft bekommt (Identität, Eltern Minderjähriger), regelt die Schule.
//  Im Kontenprotokoll steht statt des Namens die Rolle der handelnden Person (wie im Leseprotokoll).
// =====================================================================

public abstract record AuskunftTeil;

/// <summary>Absatz; Leise = kleiner und grau (Erläuterung).</summary>
public sealed record AuskunftAbsatz(string Text, bool Leise = false) : AuskunftTeil;

/// <summary>Zwischenüberschrift innerhalb eines Abschnitts.</summary>
public sealed record AuskunftZwischen(string Titel) : AuskunftTeil;

/// <summary>Begriff – Wert (wie eine Definitionsliste).</summary>
public sealed record AuskunftPaare(IReadOnlyList<KeyValuePair<string, string>> Zeilen) : AuskunftTeil;

/// <summary>Tabelle; Breiten relativ, Leer = Text, wenn es keine Zeilen gibt.</summary>
public sealed record AuskunftTabelle(
    IReadOnlyList<string> Spalten, IReadOnlyList<float> Breiten, IReadOnlyList<IReadOnlyList<string>> Zeilen, string Leer)
    : AuskunftTeil;

public sealed class AuskunftAbschnitt
{
    public string Titel { get; }
    public List<AuskunftTeil> Teile { get; } = new();

    public AuskunftAbschnitt(string titel) => Titel = titel;

    public AuskunftAbschnitt Absatz(string text, bool leise = false) { Teile.Add(new AuskunftAbsatz(text, leise)); return this; }
    public AuskunftAbschnitt Zwischen(string titel) { Teile.Add(new AuskunftZwischen(titel)); return this; }

    public AuskunftAbschnitt Paare(params (string Name, string Wert)[] zeilen)
    {
        Teile.Add(new AuskunftPaare(zeilen.Select(z => new KeyValuePair<string, string>(z.Name, z.Wert)).ToList()));
        return this;
    }

    public AuskunftAbschnitt Tabelle(string[] spalten, float[] breiten, IEnumerable<string[]> zeilen, string leer)
    {
        Teile.Add(new AuskunftTabelle(spalten, breiten, zeilen.Select(z => (IReadOnlyList<string>)z).ToList(), leer));
        return this;
    }
}

public sealed record AuskunftDokument(
    string Titel, string Untertitel, string Schule, DateTime ErstelltAm, string ErstelltVon, bool IstPseudo,
    IReadOnlyList<AuskunftAbschnitt> Abschnitte, string Dateiname);

public static class Auskunft
{
    public const string DownloadArt = "Auskunft";   // Download-Protokoll (Art)
    private const string Leer = "–";
    // Monatsnamen fest (ohne Kultur – der Server kann ohne Sprachdaten laufen, „InvariantGlobalization“)
    private static readonly string[] Monate =
        { "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };

    // -----------------------------------------------------------------
    //  Formate
    // -----------------------------------------------------------------

    internal static string D(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    internal static string D(DateOnly? d) => d is DateOnly x ? D(x) : Leer;

    /// <summary>UTC → Ortszeit des Servers.</summary>
    internal static string Z(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    internal static string Z(DateTime? utc) => utc is DateTime x ? Z(x) : Leer;

    /// <summary>Monat „2026-11“ → „November 2026“.</summary>
    internal static string MonatText(string schluessel)
        => DateTime.TryParseExact(schluessel + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? $"{Monate[t.Month - 1]} {t.Year}" : schluessel;

    internal static string Monat(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    internal static string Monat(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    internal static string N(int n) => n == 0 ? Leer : n.ToString(CultureInfo.InvariantCulture);

    private static string Ja(bool b) => b ? "ja" : "nein";

    private static string RolleText(string? rolle)
        => Enum.TryParse<BenutzerRolle>(rolle, out var r) && Enum.IsDefined(r) ? KontenService.RollenText(r) : rolle ?? Leer;

    private static string StatusText(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "aktiv" => "aktiv",
        "beurlaubt" => "beurlaubt",
        "extern" => "extern",
        "abgang" => "abgegangen",
        var s => s,
    };

    private static string TypText(LerngruppeTyp t) => t switch
    {
        LerngruppeTyp.Klasse => "Klasse",
        LerngruppeTyp.Kurs => "Kurs",
        _ => "Gruppe",
    };

    private static string BewertungText(AnwesenheitStatus st, FehlBewertung b) => st switch
    {
        AnwesenheitStatus.Beurlaubt => "entschuldigt (beurlaubt)",
        AnwesenheitStatus.Schulveranstaltung => "keine Fehlzeit",
        _ => b switch
        {
            FehlBewertung.Entschuldigt => "entschuldigt",
            FehlBewertung.Unentschuldigt => "unentschuldigt",
            _ => "offen",
        },
    };

    private static string KategorieText(KbEintragKategorie k) => k switch
    {
        KbEintragKategorie.Lob => "Lob",
        KbEintragKategorie.Hinweis => "Hinweis",
        KbEintragKategorie.Ermahnung => "Ermahnung",
        KbEintragKategorie.HausaufgabeVergessen => "Hausaufgabe vergessen",
        KbEintragKategorie.MaterialVergessen => "Material vergessen",
        KbEintragKategorie.Vorkommnis => "Vorkommnis",
        _ => k.ToString(),
    };

    /// <summary>Frist als Text: 0 = keine automatische Löschung.</summary>
    private static string Frist(int tage, string sonst)
        => tage > 0 ? $"{tage} Tage" : sonst;

    // -----------------------------------------------------------------
    //  Summen je Halbjahr (wie FehlzeitenService.Summe)
    // -----------------------------------------------------------------

    public sealed record FehlZeile(DateOnly Datum, string Schuljahr, AnwesenheitStatus Status, int? Minuten, FehlBewertung Bewertung);

    public sealed record HalbjahrSumme(string Schuljahr, int Halbjahr, int Fehlstunden, int Entschuldigt, int Offen,
        int Unentschuldigt, int Verspaetungen, int Minuten, int VerspaetungenOffen, int Veranstaltungen);

    /// <param name="halbjahrAb">2. Halbjahr ab je Schuljahr (fehlt: alles 1. Halbjahr).</param>
    public static List<HalbjahrSumme> Summen(IEnumerable<FehlZeile> zeilen, IReadOnlyDictionary<string, DateOnly?> halbjahrAb)
    {
        var erg = new List<HalbjahrSumme>();
        foreach (var g in zeilen
                     .GroupBy(z => (z.Schuljahr, Hj: halbjahrAb.TryGetValue(z.Schuljahr, out var h) && h is DateOnly ab && z.Datum >= ab ? 2 : 1))
                     .OrderBy(g => g.Key.Schuljahr, StringComparer.Ordinal).ThenBy(g => g.Key.Hj))
        {
            int fehl = 0, ent = 0, offen = 0, unent = 0, versp = 0, min = 0, vOffen = 0, veranst = 0;
            foreach (var z in g)
            {
                switch (z.Status)
                {
                    case AnwesenheitStatus.Fehlt:
                        fehl++;
                        if (z.Bewertung == FehlBewertung.Entschuldigt) ent++;
                        else if (z.Bewertung == FehlBewertung.Unentschuldigt) unent++;
                        else offen++;
                        break;
                    case AnwesenheitStatus.Beurlaubt:
                        fehl++;
                        ent++;
                        break;
                    case AnwesenheitStatus.Verspaetet:
                        versp++;
                        min += z.Minuten ?? 0;
                        if (z.Bewertung == FehlBewertung.Offen) vOffen++;
                        break;
                    case AnwesenheitStatus.Schulveranstaltung:
                        veranst++;
                        break;
                }
            }
            erg.Add(new HalbjahrSumme(g.Key.Schuljahr, g.Key.Hj, fehl, ent, offen, unent, versp, min, vOffen, veranst));
        }
        return erg;
    }

    // -----------------------------------------------------------------
    //  Listenansichten der Lerngruppen eines Schülers (Leseprotokoll)
    // -----------------------------------------------------------------

    public sealed record Mitgliedschaft(string Bezeichnung, string Schuljahr, DateOnly? Von, DateOnly? Bis, int LerngruppeId);

    public sealed record ListenAnsicht(DateOnly Tag, string Rolle, string Ansicht, int? LerngruppeId, string? Gruppe, int Anzahl);

    /// <summary>
    /// Gehört eine Listenansicht zu den Lerngruppen des Schülers? Gleiche Lerngruppe (Id) bzw. Bezeichnung in „Gruppe“,
    /// und der Tag der Ansicht liegt im Schuljahr und in der Gültigkeit der Zugehörigkeit. Der Jahrgang (Stufenlisten der
    /// Oberstufe, „Stufe Q1“) zählt nur im aktuellen Schuljahr.
    /// </summary>
    public static bool BetrifftSchueler(ListenAnsicht a, IReadOnlyList<Mitgliedschaft> mitglied, string? stufe, string aktuellesSchuljahr)
    {
        string sj = EinstellungsKatalog.SchuljahrVon(a.Tag);
        var teile = LeseAnsichten.Teile(a.Gruppe).Select(t => t.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var m in mitglied)
        {
            if (m.Schuljahr != sj || !Gueltigkeit.GiltAm(m.Von, m.Bis, a.Tag)) continue;
            if (a.LerngruppeId == m.LerngruppeId) return true;
            if (teile.Contains(m.Bezeichnung.Trim().ToUpperInvariant())) return true;
        }
        return !string.IsNullOrWhiteSpace(stufe) && sj == aktuellesSchuljahr && FehlzeitenService.IstOberstufe(stufe)
               && teile.Contains(stufe.Trim().ToUpperInvariant());
    }

    // -----------------------------------------------------------------
    //  Gemeinsame Abschnitte
    // -----------------------------------------------------------------

    private static AuskunftAbschnitt Rechte(AuskunftAbschnitt a)
        => a.Paare(
            ("Ihre Rechte", "Auskunft (Art. 15), Berichtigung (Art. 16), Löschung (Art. 17), Einschränkung der Verarbeitung " +
                            "(Art. 18) und Widerspruch (Art. 21 DSGVO) – bei der Schule. Beschwerde bei der Aufsichtsbehörde " +
                            "(in Nordrhein-Westfalen: Landesbeauftragte für Datenschutz und Informationsfreiheit NRW)."),
            ("Automatisierte Entscheidung", "Keine automatisierte Entscheidung einschließlich Profiling (Art. 22 DSGVO). " +
                                            "Die App zählt und zeigt an; entscheiden und bewerten tun Personen."));

    private static string Verantwortlich(SchulWerte w)
        => string.IsNullOrWhiteSpace(w.SchuleName) ? "die Schule (vertreten durch die Schulleitung)"
            : $"{w.SchuleName} (vertreten durch die Schulleitung)";

    private const string Auftragsverarbeiter =
        "Betreiber des Servers, auf dem die App läuft (Auftragsverarbeitung nach Art. 28 DSGVO) – [wird mit der Einrichtung " +
        "des Servers eingetragen]";

    private static (string Name, string Wert)[] KontoZeilen(Benutzer k)
    {
        var z = new List<(string, string)>
        {
            ("Benutzername", k.Benutzername),
            ("Rolle", KontenService.RollenText(k.Rolle)),
            ("Angelegt", Z(k.AngelegtAm)),
            ("Zuletzt angemeldet", k.LetzteAnmeldung is DateTime la ? Z(la) : "nie"),
            ("Passwort", k.MussPasswortAendern ? "Startpasswort – noch kein eigenes festgelegt"
                : k.PasswortGeaendertAm is DateTime pg ? $"geändert am {Z(pg)}" : "eigenes Passwort"),
            ("Gesperrt", Ja(k.Gesperrt)),
        };
        if (k.Rolle != BenutzerRolle.Schueler)
            z.Add(("Zweiter Faktor", ZweiterFaktor.Aktiv(k) ? $"eingerichtet am {Z(k.ZweiterFaktorSeit)}" : "nicht eingerichtet"));
        return z.ToArray();
    }

    /// <summary>Wer im Kontenprotokoll gehandelt hat – als Rolle (ohne Namen der Beschäftigten).</summary>
    private static string Durch(string akteur, string selbst, IReadOnlyDictionary<string, BenutzerRolle> rollen)
    {
        if (string.Equals(akteur, selbst, StringComparison.OrdinalIgnoreCase)) return "selbst";
        if (akteur.StartsWith("System", StringComparison.OrdinalIgnoreCase) || akteur.StartsWith("Konsole", StringComparison.OrdinalIgnoreCase)
            || akteur.StartsWith("Übernahme", StringComparison.OrdinalIgnoreCase))
            return "System";
        return rollen.TryGetValue(akteur, out var r) ? KontenService.RollenText(r) : "Personal der Schule";
    }

    private static async Task<IReadOnlyDictionary<string, BenutzerRolle>> RollenAsync(AppDbContext db, IEnumerable<string> namen)
    {
        var liste = namen.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (liste.Count == 0) return new Dictionary<string, BenutzerRolle>(StringComparer.OrdinalIgnoreCase);
        var zeilen = await db.Benutzer.AsNoTracking().Where(b => liste.Contains(b.Benutzername))
            .Select(b => new { b.Benutzername, b.Rolle }).ToListAsync();
        var d = new Dictionary<string, BenutzerRolle>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in zeilen) d[z.Benutzername] = z.Rolle;
        return d;
    }

    private static async Task KontoAbschnitteAsync(AppDbContext db, AuskunftAbschnitt a, Benutzer k)
    {
        a.Paare(KontoZeilen(k));

        var prot = await db.KontoProtokolle.AsNoTracking().Where(p => p.KontoId == k.Id)
            .OrderBy(p => p.Zeit).ThenBy(p => p.Id).ToListAsync();
        var rollen = await RollenAsync(db, prot.Select(p => p.Akteur));
        a.Zwischen("Kontenprotokoll (Änderungen an diesem Konto)");
        a.Tabelle(new[] { "Zeit", "Aktion", "durch", "Einzelheiten" }, new[] { 2.2f, 2.6f, 2f, 5f },
            prot.Select(p => new[] { Z(p.Zeit), p.Aktion, Durch(p.Akteur, k.Benutzername, rollen), p.Details ?? "" }),
            "Keine Einträge (Einträge werden nach der Aufbewahrungsfrist gelöscht).");

        var anm = await db.AnmeldeProtokolle.AsNoTracking().Where(p => p.KontoId == k.Id)
            .OrderBy(p => p.Zeit).ThenBy(p => p.Id).ToListAsync();
        a.Zwischen("Anmeldeprotokoll");
        a.Absatz("Erfolglose Anmeldungen; erfolgreiche nur bei Admin-Konten und mit Wiederherstellungscode. Die Adresse ist " +
                 "gekürzt gespeichert.", leise: true);
        a.Tabelle(new[] { "Zeit", "Ergebnis", "Adresse", "Gerät" }, new[] { 2.2f, 3.6f, 2.4f, 2.6f },
            anm.Select(p => new[] { Z(p.Zeit), p.Ergebnis, p.Adresse ?? Leer, p.Geraet ?? Leer }),
            "Keine Einträge.");
    }

    // =================================================================
    //  Schüler (234)
    // =================================================================

    public static async Task<AuskunftDokument?> SchuelerAsync(AppDbContext db, Schuleinstellungen einst, int schuelerId,
        string erstelltVon, bool leseprotokollAn)
    {
        var s = await db.Schueler.AsNoTracking().FirstOrDefaultAsync(x => x.Id == schuelerId);
        if (s == null) return null;
        var w = await einst.LesenAsync(db);
        var heute = KlassenbuchService.Heute();

        var zug = await db.Zugehoerigkeiten.AsNoTracking().Where(z => z.SchuelerId == s.Id)
            .Select(z => new
            {
                z.LerngruppeId, z.Schuljahr, z.GueltigVon, z.GueltigBis, z.Quelle,
                z.Lerngruppe!.Typ, z.Lerngruppe.Bezeichnung, z.Lerngruppe.Fach, z.Lerngruppe.Kursart, LgSchuljahr = z.Lerngruppe.Schuljahr,
            })
            .ToListAsync();
        var klassen = await KlassenbuchService.KlassenAsync(db, new List<int> { s.Id }, w.Schuljahr);
        string? klasse = klassen.TryGetValue(s.Id, out var kl) ? kl : null;
        string name = $"{s.Vorname} {s.Nachname}".Trim();

        var abschnitte = new List<AuskunftAbschnitt>();

        // ---- 1. Angaben nach Art. 15 Abs. 1 ----
        var a1 = new AuskunftAbschnitt("1. Angaben nach Art. 15 Abs. 1 DSGVO");
        a1.Paare(
            ("Verantwortlich", Verantwortlich(w)),
            ("Zwecke", "Unterrichtsorganisation (Stunden- und Vertretungsplan), Führung des Klassenbuchs (Anwesenheit, " +
                       "Fehlzeiten, Entschuldigungen, Einträge) und Zugang zur App – nach dem Schulgesetz NRW und der " +
                       "Verordnung über die zur Verarbeitung zugelassenen Daten von Schülerinnen, Schülern und Eltern (VO-DV I)."),
            ("Kategorien", "Stammdaten (Name, Jahrgang, Status), Klassen, Kurse und Gruppen, Konto der App, Anwesenheit und " +
                           "Fehlzeiten mit Bewertung" + (w.GrundErfassen
                               ? " und Grund der Entschuldigung (Krankheit und Arzttermin sind Gesundheitsdaten, Art. 9 DSGVO)"
                               : " (ein Grund der Entschuldigung wird zurzeit nicht erfasst)") +
                           ", Klassenbucheinträge, Protokolle (Änderungen, Anmeldungen" + (leseprotokollAn ? ", Ansichten" : "") + ")."),
            ("Empfänger", "In der Schule: Lehrkräfte der Lerngruppen (für ihre Stunden), Klassenleitung bzw. " +
                          "Stufenkoordination, Schulverwaltung. Außerhalb: Rückmeldung der Fehlstunden an die " +
                          "Schülerverwaltung der Schule (SchILD-NRW, ohne Namen). " + Auftragsverarbeiter + "."),
            ("Speicherdauer", $"Klassenbucheinträge: {Frist(w.EintraegeLoeschenNachTagen, "keine automatische Löschung eingestellt")}" +
                              $" nach ihrem Datum; Konto nach dem Abgang: {Frist(w.AbgangKontenLoeschenNachTagen, "keine automatische Löschung eingestellt")};" +
                              $" Protokolle: {Frist(w.ProtokollTage, "keine automatische Löschung eingestellt")}; Fehlzeiten und " +
                              "Zugehörigkeiten: nach dem Löschkonzept der Schule (VO-DV I) – in der App ohne eigene Frist."),
            ("Herkunft", "Schülerverwaltung der Schule (SchILD-NRW): Name, Jahrgang, Klassen und Kurse. Eingaben im Klassenbuch " +
                         "durch Lehrkräfte, Klassenleitung, Stufenkoordination und Verwaltung."));
        Rechte(a1);
        abschnitte.Add(a1);

        // ---- 2. Stammdaten ----
        var a2 = new AuskunftAbschnitt("2. Stammdaten");
        var stamm = new List<(string, string)>
        {
            ("Name", name),
            ("Jahrgang", string.IsNullOrWhiteSpace(s.Stufe) ? Leer : s.Stufe),
            ("Klasse (heute)", klasse ?? Leer),
            ("Status", StatusText(s.Status)),
        };
        if (s.AbgangAm != null) stamm.Add(("Abgang am", D(s.AbgangAm)));
        stamm.Add(("Kennung aus SchILD vorhanden", Ja(!string.IsNullOrWhiteSpace(s.ExtId))));
        if (s.ImportAm != null) stamm.Add(("Zuletzt aus SchILD übernommen", Z(s.ImportAm)));
        a2.Paare(stamm.ToArray());
        a2.Absatz("Die Kennung ist eine Prüfsumme aus Name und Geburtsdatum; Geburtsdatum und SchILD-Nummer selbst sind in der " +
                  "App nicht gespeichert.", leise: true);
        abschnitte.Add(a2);

        // ---- 3. Klassen, Kurse und Gruppen ----
        var a3 = new AuskunftAbschnitt("3. Klassen, Kurse und Gruppen");
        a3.Tabelle(new[] { "Schuljahr", "Art", "Bezeichnung", "Fach", "gültig von", "bis", "Quelle" },
            new[] { 1.4f, 1.2f, 2.6f, 1.8f, 1.6f, 1.6f, 1.2f },
            zug.OrderBy(z => z.Schuljahr, StringComparer.Ordinal).ThenBy(z => z.Typ).ThenBy(z => z.Bezeichnung, StringComparer.OrdinalIgnoreCase)
                .ThenBy(z => z.GueltigVon)
                .Select(z => new[]
                {
                    z.Schuljahr, TypText(z.Typ), z.Bezeichnung,
                    string.IsNullOrWhiteSpace(z.Fach) ? Leer : z.Kursart == Kursart.Keine ? z.Fach! : $"{z.Fach} ({z.Kursart})",
                    z.GueltigVon is DateOnly v ? D(v) : "Schuljahresbeginn", z.GueltigBis is DateOnly b ? D(b) : "offen",
                    string.IsNullOrWhiteSpace(z.Quelle) ? Leer : z.Quelle!,
                }),
            "Keine Zugehörigkeiten gespeichert.");
        abschnitte.Add(a3);

        // ---- 4. Konto ----
        var a4 = new AuskunftAbschnitt("4. Konto der App");
        var konten = await db.Benutzer.AsNoTracking().Where(b => b.SchuelerId == s.Id).OrderBy(b => b.Id).ToListAsync();
        if (konten.Count == 0) a4.Absatz("Kein Konto angelegt.");
        foreach (var k in konten) await KontoAbschnitteAsync(db, a4, k);
        abschnitte.Add(a4);

        // ---- 5. Fehlzeiten ----
        var a5 = new AuskunftAbschnitt("5. Anwesenheit und Fehlzeiten");
        var anw = await db.Anwesenheiten.AsNoTracking().Where(x => x.SchuelerId == s.Id)
            .Select(x => new
            {
                x.Lektion!.Datum, x.Lektion.Stunde, x.Lektion.Fach, x.Lektion.Schuljahr, x.Status, x.Minuten, x.Bewertung,
                x.Grund, x.BewertetVon, x.BewertetAm,
            })
            .ToListAsync();
        anw = anw.OrderBy(x => x.Datum).ThenBy(x => x.Stunde).ToList();
        var schuljahre = anw.Select(x => x.Schuljahr).Distinct().ToList();
        var kalender = await db.Kalender.AsNoTracking().Where(k => schuljahre.Contains(k.Schuljahr)).ToListAsync();
        var hjAb = new Dictionary<string, DateOnly?>(StringComparer.Ordinal);
        foreach (var kal in kalender)
            hjAb[kal.Schuljahr] = FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, kal.Schuljahr == w.Schuljahr ? w.HalbjahrAb : null);
        var summen = Summen(anw.Select(x => new FehlZeile(x.Datum, x.Schuljahr, x.Status, x.Minuten, x.Bewertung)), hjAb);

        a5.Absatz("Gespeichert wird nur, was von „anwesend“ abweicht. Fehlstunden = fehlt + beurlaubt (beurlaubt zählt als " +
                  "entschuldigt); schulische Veranstaltungen zählen nicht als Fehlzeit.", leise: true);
        a5.Zwischen("Summen je Halbjahr");
        a5.Tabelle(new[] { "Schuljahr", "Halbjahr", "Fehlst.", "entsch.", "offen", "unent.", "Versp.", "Min.", "Schulveranst." },
            new[] { 1.4f, 1.1f, 1f, 1f, 1f, 1f, 1f, 1f, 1.4f },
            summen.Select(x => new[]
            {
                x.Schuljahr, $"{x.Halbjahr}.", N(x.Fehlstunden), N(x.Entschuldigt), N(x.Offen), N(x.Unentschuldigt),
                x.VerspaetungenOffen > 0 ? $"{x.Verspaetungen} ({x.VerspaetungenOffen} offen)" : N(x.Verspaetungen),
                N(x.Minuten), N(x.Veranstaltungen),
            }),
            "Keine Fehlzeiten gespeichert.");

        var ents = await db.Entschuldigungen.AsNoTracking().Where(e => e.SchuelerId == s.Id)
            .OrderBy(e => e.Von).ThenBy(e => e.Id).ToListAsync();
        a5.Zwischen("Entschuldigungen (Zeiträume)");
        a5.Tabelle(new[] { "Zeitraum", "Grund", "eingetragen von", "am" }, new[] { 3f, 2f, 2f, 2.2f },
            ents.Select(e => new[]
            {
                e.Von == e.Bis ? D(e.Von) : $"{D(e.Von)} – {D(e.Bis)}", FehlzeitenService.GrundText(e.Grund), e.ErfasstVon, Z(e.ErfasstAm),
            }),
            "Keine Entschuldigungen eingetragen.");

        a5.Zwischen("Jede Stunde");
        a5.Tabelle(new[] { "Datum", "Std.", "Fach", "Status", "Bewertung", "Grund", "bewertet von, am" },
            new[] { 1.6f, 0.7f, 1.2f, 1.9f, 2.2f, 1.6f, 2.8f },
            anw.Select(x => new[]
            {
                D(x.Datum), $"{x.Stunde}.", x.Fach, KlassenbuchService.StatusText(x.Status, x.Minuten), BewertungText(x.Status, x.Bewertung),
                (x.Status is AnwesenheitStatus.Fehlt or AnwesenheitStatus.Verspaetet) && x.Bewertung == FehlBewertung.Entschuldigt
                    && x.Grund is FehlGrund g ? FehlzeitenService.GrundText(g) : Leer,
                x.BewertetVon != null ? $"{x.BewertetVon}, {Z(x.BewertetAm)}" : Leer,
            }),
            "Keine Fehlzeiten gespeichert.");
        abschnitte.Add(a5);

        // ---- 6. Klassenbucheinträge ----
        var a6 = new AuskunftAbschnitt("6. Klassenbucheinträge über die Schülerin bzw. den Schüler");
        var eintraege = await db.KlassenbuchEintraege.AsNoTracking()
            .Where(e => e.SchuelerId == s.Id && e.Art == KbEintragArt.Schueler)
            .OrderBy(e => e.Datum).ThenBy(e => e.Stunde).ThenBy(e => e.Id).ToListAsync();
        a6.Tabelle(new[] { "Datum", "Fach", "Kategorie", "Text", "von" }, new[] { 1.9f, 1.2f, 2f, 6f, 1.3f },
            eintraege.Select(e => new[]
            {
                e.Stunde is int st ? $"{D(e.Datum)}, {st}." : D(e.Datum), string.IsNullOrWhiteSpace(e.Fach) ? Leer : e.Fach,
                KategorieText(e.Kategorie), e.Text ?? "", e.VerfasstVon,
            }),
            "Keine Einträge.");
        a6.Absatz("Klasseneinträge (über die ganze Lerngruppe einer Stunde) stehen hier nicht – sie betreffen die Lerngruppe. " +
                  "„von“ ist der Benutzername der Verfasserin bzw. des Verfassers (wie im Klassenbuch-PDF).", leise: true);
        abschnitte.Add(a6);

        // ---- 7. Änderungsverlauf ----
        var a7 = new AuskunftAbschnitt("7. Änderungsverlauf im Klassenbuch");
        var verlauf = await db.KlassenbuchProtokolle.AsNoTracking().Where(p => p.SchuelerId == s.Id)
            .OrderBy(p => p.Zeit).ThenBy(p => p.Id).ToListAsync();
        var lektionIds = verlauf.Where(p => p.LektionId != null).Select(p => p.LektionId!.Value).Distinct().ToList();
        var lektionen = lektionIds.Count == 0 ? new Dictionary<int, string>()
            : (await db.Lektionen.AsNoTracking().Where(l => lektionIds.Contains(l.Id))
                    .Select(l => new { l.Id, l.Datum, l.Stunde, l.Fach }).ToListAsync())
                .ToDictionary(l => l.Id, l => $"{D(l.Datum)}, {l.Stunde}. ({l.Fach})");
        a7.Tabelle(new[] { "Zeit", "Art", "Stunde", "vorher", "nachher", "durch" }, new[] { 2.1f, 1.6f, 2.4f, 2.4f, 2.8f, 1.3f },
            verlauf.Select(p => new[]
            {
                Z(p.Zeit), p.Art, p.LektionId is int li && lektionen.TryGetValue(li, out var lt) ? lt : Leer, p.Alt ?? Leer, p.Neu ?? Leer,
                p.Akteur,
            }),
            "Keine Einträge.");
        abschnitte.Add(a7);

        // ---- 8. Leseprotokoll ----
        var a8 = new AuskunftAbschnitt("8. Wer hat die Daten angesehen (Leseprotokoll)");
        if (!leseprotokollAn)
            a8.Absatz("Das Leseprotokoll ist zurzeit ausgeschaltet – solange werden keine Ansichten aufgezeichnet.");
        var einzeln = await db.LeseProtokolle.AsNoTracking().Where(p => p.SchuelerId == s.Id)
            .OrderBy(p => p.Tag).ThenBy(p => p.ErsteZeit).ToListAsync();
        a8.Zwischen("Einzelansichten");
        a8.Tabelle(new[] { "Datum", "Ansicht", "Rolle", "Anzahl" }, new[] { 1.6f, 5f, 2.4f, 1f },
            einzeln.Select(p => new[] { D(p.Tag), LeseAnsichten.Text(p.Ansicht), RolleText(p.Rolle), N(p.Anzahl) }),
            "Keine Einträge.");

        var mitglied = zug.Select(z => new Mitgliedschaft(z.Bezeichnung, z.LgSchuljahr.Length > 0 ? z.LgSchuljahr : z.Schuljahr,
            z.GueltigVon, z.GueltigBis, z.LerngruppeId)).ToList();
        var listen = (await db.LeseProtokolle.AsNoTracking()
                .Where(p => p.SchuelerId == null && (p.LerngruppeId != null || p.Gruppe != null))
                .Select(p => new ListenAnsicht(p.Tag, p.Rolle, p.Ansicht, p.LerngruppeId, p.Gruppe, p.Anzahl))
                .ToListAsync())
            .Where(p => LeseAnsichten.IstListe(p.Ansicht) && BetrifftSchueler(p, mitglied, s.Stufe, w.Schuljahr))
            .GroupBy(p => (Monat: Monat(p.Tag), p.Rolle))
            .OrderBy(g => g.Key.Monat, StringComparer.Ordinal).ThenBy(g => RolleText(g.Key.Rolle), StringComparer.Ordinal)
            .Select(g => new[] { MonatText(g.Key.Monat), RolleText(g.Key.Rolle), N(g.Sum(x => x.Anzahl)) })
            .ToList();
        a8.Zwischen("Listenansichten der Klassen, Kurse und Gruppen");
        a8.Tabelle(new[] { "Monat", "Rolle", "Ansichten" }, new[] { 2.4f, 2.4f, 1.2f }, listen, "Keine Einträge.");
        a8.Absatz("Genannt werden Datum, Ansicht und Rolle – nicht die Namen der Beschäftigten, die im Auftrag der Schule " +
                  "zugegriffen haben. Listenansichten (Stundenblatt, Klassen- und Kurslisten) sind als Zahl je Monat und Rolle " +
                  "angegeben; ob die Schülerin bzw. der Schüler darin zu sehen war, hängt vom Zeitraum der Liste ab. Eigene " +
                  "Ansichten (z. B. „Meine Fehlzeiten“) werden nicht aufgezeichnet.", leise: true);
        abschnitte.Add(a8);

        // ---- 9. Rückweg nach SchILD ----
        var a9 = new AuskunftAbschnitt("9. Rückmeldung der Fehlstunden an SchILD-NRW");
        var abrufe = await db.DownloadProtokolle.AsNoTracking().Where(p => p.Art == SchildRueckweg.ArtAbruf)
            .OrderBy(p => p.Zeit).ToListAsync();
        var fehlTage = anw.Where(x => x.Status is AnwesenheitStatus.Fehlt or AnwesenheitStatus.Beurlaubt).Select(x => x.Datum).ToList();
        bool kennung = !string.IsNullOrWhiteSpace(s.ExtId);
        a9.Tabelle(new[] { "übergeben am", "Zeitraum der Fehlstunden" }, new[] { 2.2f, 4f },
            abrufe.Where(p => kennung && fehlTage.Any(t => t >= p.Von && t <= p.Bis))
                .Select(p => new[] { Z(p.Zeit), $"{D(p.Von)} – {D(p.Bis)}" }),
            kennung ? "Keine Fehlstunden übergeben." : "Ohne Kennung aus SchILD werden keine Fehlstunden übergeben.");
        a9.Absatz("Übergeben werden die Fehlstunden des Zeitraums mit der Kennung (Prüfsumme) – ohne Namen. Die " +
                  "Schülerverwaltung der Schule ordnet sie zu.", leise: true);
        abschnitte.Add(a9);

        string untertitel = klasse != null ? $"{name} ({klasse})" : name;
        return new AuskunftDokument("Auskunft nach Art. 15 DSGVO", untertitel, w.SchuleName, DateTime.Now, erstelltVon, s.IstPseudo,
            abschnitte, BerichtService.SichererName($"Auskunft_Art15_Schueler_{s.Id}_{heute:yyyy-MM-dd}", ".pdf"));
    }

    /// <summary>„Schüler-Id n (Klasse)“ für das Download-Protokoll – ohne Namen.</summary>
    public static async Task<string> SchuelerWasAsync(AppDbContext db, int schuelerId, string schuljahr)
    {
        var klassen = await KlassenbuchService.KlassenAsync(db, new List<int> { schuelerId }, schuljahr);
        return LeseAnsichten.SchuelerGegenstand(schuelerId, klassen.TryGetValue(schuelerId, out var k) ? k : null);
    }

    // =================================================================
    //  Personalkonten (235)
    // =================================================================

    public static async Task<AuskunftDokument> KontoAsync(AppDbContext db, Schuleinstellungen einst, Benutzer k, string erstelltVon,
        bool leseprotokollAn)
    {
        var w = await einst.LesenAsync(db);
        var heute = KlassenbuchService.Heute();
        string? kuerzel = string.IsNullOrWhiteSpace(k.LehrerKuerzel) ? null : k.LehrerKuerzel.Trim();
        string? lehrkraft = null;
        if (kuerzel != null)
        {
            string ku = kuerzel.ToUpperInvariant();
            lehrkraft = await db.Lehrkraefte.AsNoTracking().Where(l => l.Kuerzel.ToUpper() == ku)
                .Select(l => l.Vorname + " " + l.Nachname).FirstOrDefaultAsync();
        }

        var abschnitte = new List<AuskunftAbschnitt>();

        var a1 = new AuskunftAbschnitt("1. Angaben nach Art. 15 Abs. 1 DSGVO");
        a1.Paare(
            ("Verantwortlich", Verantwortlich(w)),
            ("Zwecke", "Zugang zur App, Unterrichts- und Vertretungsorganisation, Führung des Klassenbuchs – nach dem " +
                       "Schulgesetz NRW und der Verordnung über die zur Verarbeitung zugelassenen Daten der Lehrerinnen und " +
                       "Lehrer (VO-DV II)."),
            ("Kategorien", "Konto (Benutzername, Rolle, Kürzel, Zuständigkeit), Name laut SchILD, Abwesenheitsgründe in " +
                           "veröffentlichten Vertretungen, Protokolle (Konto, Anmeldungen, Downloads" +
                           (leseprotokollAn ? ", Ansichten" : "") + "), Tätigkeiten im Klassenbuch."),
            ("Empfänger", "Schulverwaltung (Admin-Konten). Abwesenheitsgründe sehen nur die Verwaltung und die Lehrkraft selbst. " +
                          Auftragsverarbeiter + "."),
            ("Speicherdauer", $"Abwesenheitsgründe der Vertretungen: {Frist(w.GruendeLoeschenNachTagen, "keine automatische Löschung eingestellt")}" +
                              $" nach dem Tag; Protokolle: {Frist(w.ProtokollTage, "keine automatische Löschung eingestellt")}; " +
                              "Konto: bis zur Löschung durch die Verwaltung."),
            ("Herkunft", "Vertretungsplanung der Schule (Desktop-Programm), Schülerverwaltung (SchILD-NRW: Name zum Kürzel), " +
                         "Eingaben in der App."));
        Rechte(a1);
        abschnitte.Add(a1);

        var a2 = new AuskunftAbschnitt("2. Konto");
        var extra = new List<(string, string)>();
        if (kuerzel != null) extra.Add(("Kürzel", kuerzel));
        if (lehrkraft != null) extra.Add(("Name laut SchILD", lehrkraft));
        if (!string.IsNullOrWhiteSpace(k.Zustaendig)) extra.Add(("Zuständig für", string.Join(", ", KontenService.Zustaendigkeiten(k.Zustaendig))));
        if (extra.Count > 0) a2.Paare(extra.ToArray());
        await KontoAbschnitteAsync(db, a2, k);

        var durchKonto = await db.KontoProtokolle.AsNoTracking()
            .Where(p => p.Akteur == k.Benutzername && p.KontoId != k.Id).Select(p => p.Zeit).ToListAsync();
        if (durchKonto.Count > 0)
        {
            a2.Zwischen("Änderungen an anderen Konten durch dieses Konto (Zahl je Monat)");
            a2.Tabelle(new[] { "Monat", "Änderungen" }, new[] { 2.4f, 1.2f },
                durchKonto.GroupBy(x => Monat(x)).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new[] { MonatText(g.Key), N(g.Count()) }),
                "Keine.");
        }
        abschnitte.Add(a2);

        // ---- 3. Abwesenheitsgründe ----
        var a3 = new AuskunftAbschnitt("3. Abwesenheitsgründe in veröffentlichten Vertretungen");
        var gruende = new List<string[]>();
        if (kuerzel != null)
        {
            string ku = kuerzel.ToUpperInvariant();
            var zeilen = await db.Eintraege.AsNoTracking()
                .Where(e => e.OriginalLehrer != null && e.OriginalLehrer.ToUpper() == ku
                            && ((e.Grund != null && e.Grund != "") || (e.Bemerkung != null && e.Bemerkung != "")))
                .Select(e => new { e.Tag!.Datum, e.Stunde, e.Klassen, e.Fach, e.Grund, e.Bemerkung })
                .ToListAsync();
            gruende = zeilen.OrderBy(e => e.Datum).ThenBy(e => e.Stunde)
                .Select(e => new[] { D(e.Datum), $"{e.Stunde}.", $"{e.Klassen} {e.Fach}".Trim(), e.Grund ?? Leer, e.Bemerkung ?? Leer })
                .ToList();
        }
        a3.Tabelle(new[] { "Datum", "Std.", "Klasse, Fach", "Grund", "Bemerkung" }, new[] { 1.6f, 0.7f, 2.2f, 2.4f, 4f }, gruende,
            kuerzel == null ? "Dem Konto ist kein Kürzel zugeordnet." : "Keine Gründe gespeichert.");
        a3.Absatz("Die Vertretungsdatei im Desktop-Programm der Schule kann weitere bzw. ältere Gründe enthalten.", leise: true);
        abschnitte.Add(a3);

        // ---- 4. Leseprotokoll als handelnde Person ----
        var a4 = new AuskunftAbschnitt("4. Ansichten durch dieses Konto (Leseprotokoll)");
        if (!leseprotokollAn) a4.Absatz("Das Leseprotokoll ist zurzeit ausgeschaltet – solange werden keine Ansichten aufgezeichnet.");
        var lese = await db.LeseProtokolle.AsNoTracking().Where(p => p.KontoId == k.Id)
            .Select(p => new { p.Tag, p.Ansicht, p.Anzahl }).ToListAsync();
        a4.Tabelle(new[] { "Monat", "Ansicht", "Anzahl" }, new[] { 2.2f, 5f, 1.2f },
            lese.GroupBy(p => (Monat: Monat(p.Tag), p.Ansicht))
                .OrderBy(g => g.Key.Monat, StringComparer.Ordinal).ThenBy(g => LeseAnsichten.Text(g.Key.Ansicht), StringComparer.Ordinal)
                .Select(g => new[] { MonatText(g.Key.Monat), LeseAnsichten.Text(g.Key.Ansicht), N(g.Sum(x => x.Anzahl)) }),
            "Keine Einträge.");
        abschnitte.Add(a4);

        // ---- 5. Tätigkeiten im Klassenbuch ----
        var a5 = new AuskunftAbschnitt("5. Tätigkeiten im Klassenbuch (Zahlen je Monat)");
        string bn = k.Benutzername;
        var erfasst = await db.Lektionen.AsNoTracking().Where(l => l.ErfasstVon == bn && l.ErfasstAm != null)
            .Select(l => l.ErfasstAm!.Value).ToListAsync();
        var bewertet = await db.Anwesenheiten.AsNoTracking().Where(a => a.BewertetVon == bn && a.BewertetAm != null)
            .Select(a => a.BewertetAm!.Value).ToListAsync();
        var entschuldigt = await db.Entschuldigungen.AsNoTracking().Where(e => e.ErfasstVon == bn).Select(e => e.ErfasstAm).ToListAsync();
        var geschrieben = await db.KlassenbuchEintraege.AsNoTracking().Where(e => e.VerfasstVon == bn).Select(e => e.VerfasstAm).ToListAsync();
        var monate = erfasst.Concat(bewertet).Concat(entschuldigt).Concat(geschrieben).Select(x => Monat(x)).Distinct()
            .OrderBy(m => m, StringComparer.Ordinal).ToList();
        int Zaehle(List<DateTime> liste, string m) => liste.Count(x => Monat(x) == m);
        a5.Tabelle(new[] { "Monat", "Stunden erfasst", "Bewertungen", "Entschuldigungen", "Einträge" }, new[] { 2.2f, 1.6f, 1.6f, 1.8f, 1.4f },
            monate.Select(m => new[]
            {
                MonatText(m), N(Zaehle(erfasst, m)), N(Zaehle(bewertet, m)), N(Zaehle(entschuldigt, m)), N(Zaehle(geschrieben, m)),
            }),
            "Keine Tätigkeiten gespeichert.");
        a5.Absatz("Einzelheiten (welche Stunde, welcher Eintrag) stehen im Klassenbuch bzw. Kursheft (PDF) der Klasse. Gezählt " +
                  "wird die erste Erfassung einer Stunde, jede Bewertung (zuletzt), jede eingetragene Entschuldigung und jeder " +
                  "geschriebene Eintrag.", leise: true);
        abschnitte.Add(a5);

        // ---- 6. Downloads ----
        var a6 = new AuskunftAbschnitt("6. Downloads mit Schülerdaten (Zahl je Monat und Art)");
        var dl = await db.DownloadProtokolle.AsNoTracking().Where(p => p.Akteur == bn).Select(p => new { p.Zeit, p.Art }).ToListAsync();
        a6.Tabelle(new[] { "Monat", "Art", "Anzahl" }, new[] { 2.2f, 4f, 1.2f },
            dl.GroupBy(p => (Monat: Monat(p.Zeit), p.Art)).OrderBy(g => g.Key.Monat, StringComparer.Ordinal).ThenBy(g => g.Key.Art, StringComparer.Ordinal)
                .Select(g => new[] { MonatText(g.Key.Monat), DownloadText(g.Key.Art), N(g.Count()) }),
            "Keine Downloads.");
        abschnitte.Add(a6);

        string untertitel = $"Konto {k.Benutzername} ({KontenService.RollenText(k.Rolle)})";
        return new AuskunftDokument("Auskunft nach Art. 15 DSGVO", untertitel, w.SchuleName, DateTime.Now, erstelltVon, false,
            abschnitte, BerichtService.SichererName($"Auskunft_Art15_Konto_{k.Id}_{heute:yyyy-MM-dd}", ".pdf"));
    }

    private static string DownloadText(string art) => art switch
    {
        "KlassenbuchPdf" => "Klassenbuch (PDF)",
        "KursheftPdf" => "Kursheft (PDF)",
        "FehlzeitenCsv" => "Fehlzeiten (CSV)",
        "Archiv" => "Jahresarchiv (Zip)",
        SchildRueckweg.ArtAbruf => "Rückweg nach SchILD (abgeholt)",
        SchildRueckweg.ArtDatei => "Rückweg nach SchILD (Datei)",
        SchildRueckweg.ArtProbe => "Rückweg nach SchILD (Probedatei)",
        DownloadArt => "Auskunft (Art. 15)",
        _ => art,
    };
}
