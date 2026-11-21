using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P3 – E-1 Schuleinstellungen in der App (Entscheidungen 138–141, 199–203).
//
//  Katalog: welche Schulwerte es gibt (Gruppe, Typ, Hilfetext, Standard, Frage an den DSB).
//  Schuleinstellungen (Singleton): liest die Tabelle „Einstellungen“ mit Zwischenspeicher und
//  löst die Werte auf (SchulWerte). Eine Änderung in der App verwirft den Zwischenspeicher – sie
//  wirkt also sofort, ohne Neustart (139).
//
//  Reihenfolge je Schlüssel: Zeile in der Datenbank (Wert null = Standard) → nur wenn die Zeile fehlt:
//  Wert aus appsettings (Startwert, z. B. „Klassenbuch:ErfassungAb“) → Standard. Beim ersten Start mit
//  E-1 legt Uebernehmen() die Zeilen an und übernimmt dabei die Dateiwerte (140); danach gelten nur
//  noch die Zeilen, Dateiwerte dieser Schlüssel werden ignoriert (Hinweis im Log, wenn sie abweichen).
//
//  Standardwerte ohne schulspezifische Konstanten (138): Schuljahr nach dem Datum (ab 1. August das
//  neue), „Erfassung ab“ = erster Schultag laut Kalender, „Thema ab“ = „Erfassung ab“, 2. Halbjahr =
//  1. Februar (wie bisher in FehlzeitenService.HalbjahrAb).
// =====================================================================

public enum EinstellungsTyp { Text, Schuljahr, Datum, Zahl, Schalter }

public sealed record EinstellungsGruppe(string Id, string Titel, string Text);

/// <summary>Beschreibung einer Einstellung.</summary>
/// <param name="DateiSchluessel">Schlüssel in appsettings, aus dem der Startwert übernommen wird (140).</param>
/// <param name="StandardWert">Standard für Text/Zahl/Schalter (null: berechnet bzw. leer).</param>
/// <param name="DsbFrage">gesetzt = Datenschutzwirkung: Frage an den DSB und „abgestimmt am / mit“ (141).</param>
/// <param name="NullText">Text für den Wert 0 bei Zahlen (z. B. „aus“).</param>
/// <param name="Loescht">Eine kürzere Frist löscht Datensätze – vorher zählen und nachfragen (141).</param>
public sealed record EinstellungsDef(
    string Schluessel, string Gruppe, string Titel, EinstellungsTyp Typ, string Hilfe, string StandardText,
    string? DateiSchluessel = null, string? StandardWert = null, string? DsbFrage = null,
    int Min = 0, int Max = 0, int MaxLaenge = 0, string? Einheit = null, string? NullText = null, bool Loescht = false)
{
    public bool MitDsb => DsbFrage != null;
}

public static class EinstellungsKatalog
{
    public const string SchuleName = "Schule.Name";
    public const string SchuleKurzname = "Schule.Kurzname";
    public const string Schuljahr = "Schuljahr.Aktuell";
    public const string ErfassungAb = "Schuljahr.ErfassungAb";
    public const string ThemaAb = "Schuljahr.ThemaAb";
    public const string HalbjahrAb = "Schuljahr.HalbjahrAb";
    public const string EintraegeLoeschen = "Datenschutz.EintraegeLoeschenNachTagen";
    public const string ProtokollTage = "Datenschutz.ProtokollTage";
    public const string BerichteNurAdmin = "Datenschutz.BerichteNurAdmin";
    public const string SitzungStunden = "Anmeldung.SitzungStunden";
    public const string AngemeldetBleibenTage = "Anmeldung.AngemeldetBleibenTage";

    public const int MaxAbgestimmtMit = 120;

    public static readonly IReadOnlyList<EinstellungsGruppe> Gruppen = new[]
    {
        new EinstellungsGruppe("schule", "Schule",
            "Name und Kurzname der Schule. Beide sind auch vor der Anmeldung sichtbar (Anmeldeseite, Name der App)."),
        new EinstellungsGruppe("schuljahr", "Schuljahr",
            "Gelten für das ganze Schuljahr. Daten müssen im Schuljahr laut Kalender liegen (der Kalender kommt mit " +
            "„Grundplan veröffentlichen…“ aus dem Desktop)."),
        new EinstellungsGruppe("datenschutz", "Datenschutz",
            "Vor dem Echtbetrieb mit der bzw. dem Datenschutzbeauftragten abstimmen und „abgestimmt am / mit“ eintragen. " +
            "Eine kürzere Frist löscht ältere Daten sofort – vorher steht da, wie viele."),
        new EinstellungsGruppe("anmeldung", "Anmeldung",
            "Gilt für neue Anmeldungen; bestehende laufen bis zu ihrem Ende. Vor dem Echtbetrieb mit der bzw. dem " +
            "Datenschutzbeauftragten abstimmen."),
    };

    public static readonly IReadOnlyList<EinstellungsDef> Alle = new[]
    {
        new EinstellungsDef(SchuleName, "schule", "Name der Schule", EinstellungsTyp.Text,
            "Steht auf der Anmeldeseite und im Kopf der PDFs (Klassenbuch, Kursheft).",
            "leer", MaxLaenge: 120),
        new EinstellungsDef(SchuleKurzname, "schule", "Kurzname (Name der App)", EinstellungsTyp.Text,
            "Name der App im Fenstertitel und auf dem Startbildschirm des Handys – am besten höchstens 12 Zeichen " +
            "(z. B. „GSG“). Eine schon installierte App behält ihren Namen meist, bis sie neu installiert wird.",
            "„Stundenplan“", MaxLaenge: 30),

        new EinstellungsDef(Schuljahr, "schuljahr", "Aktuelles Schuljahr", EinstellungsTyp.Schuljahr,
            "Mit diesem Schuljahr rechnen der SchILD-Import und später der Halbjahres- und Schuljahreswechsel. " +
            "Schreibweise „2026/27“.",
            "nach dem Datum (ab 1. August das neue Schuljahr)", DateiSchluessel: "DefaultSchuljahr"),
        new EinstellungsDef(ErfassungAb, "schuljahr", "Anwesenheit erfasst ab", EinstellungsTyp.Datum,
            "Ab diesem Tag zählen Stunden ohne Anwesenheit als „nicht erfasst“ (Klassenbuch → „Nicht erfasst“, " +
            "Übersicht, PDF, CSV).",
            "erster Schultag laut Kalender", DateiSchluessel: "Klassenbuch:ErfassungAb"),
        new EinstellungsDef(ThemaAb, "schuljahr", "Thema erfasst ab", EinstellungsTyp.Datum,
            "Ab diesem Tag zählen erfasste Stunden ohne Thema als offen („Thema fehlt“). Nicht vor „Anwesenheit " +
            "erfasst ab“.",
            "wie „Anwesenheit erfasst ab“", DateiSchluessel: "Klassenbuch:ThemaAb"),
        new EinstellungsDef(HalbjahrAb, "schuljahr", "2. Halbjahr ab", EinstellungsTyp.Datum,
            "Erster Tag des 2. Halbjahres – für die Zeiträume „1. Halbjahr“ und „2. Halbjahr“ der Fehlzeiten und " +
            "den Stichtag des SchILD-Imports im 2. Abschnitt.",
            "1. Februar", DateiSchluessel: "Klassenbuch:HalbjahrAb"),

        new EinstellungsDef(EintraegeLoeschen, "datenschutz", "Löschfrist der Klassenbucheinträge", EinstellungsTyp.Zahl,
            "Schüler- und Klasseneinträge werden samt ihrem Verlauf gelöscht, wenn ihr Datum länger als so viele " +
            "Tage zurückliegt. 0 = nicht löschen.",
            "0 (aus)", DateiSchluessel: "Klassenbuch:EintraegeLoeschenNachTagen", StandardWert: "0",
            DsbFrage: "Wie lange werden Klassenbucheinträge aufbewahrt (Löschkonzept, VO-DV I)?",
            Min: 0, Max: 3650, Einheit: "Tage", NullText: "aus", Loescht: true),
        new EinstellungsDef(ProtokollTage, "datenschutz", "Aufbewahrung der Protokolle", EinstellungsTyp.Zahl,
            "So lange bleiben das Kontenprotokoll, das Download-Protokoll und das Protokoll dieser Einstellungen; " +
            "ältere Einträge werden gelöscht. 0 = nicht löschen (nicht empfohlen).",
            "365 Tage", DateiSchluessel: "Konten:ProtokollTage", StandardWert: "365",
            DsbFrage: "Wie lange werden die Protokolle der Konten, Downloads und Einstellungen aufbewahrt?",
            Min: 0, Max: 3650, Einheit: "Tage", NullText: "nicht löschen", Loescht: true),
        new EinstellungsDef(BerichteNurAdmin, "datenschutz", "Downloads nur für die Verwaltung", EinstellungsTyp.Schalter,
            "An: PDFs und CSV-Dateien mit Schülerdaten (Klassenbuch, Kursheft, Fehlzeiten, Jahresarchiv) lädt nur die " +
            "Verwaltung herunter. Aus: auch Klassenleitung, Stufenkoordination und Lehrkräfte für ihre Klassen und Kurse.",
            "aus", DateiSchluessel: "Berichte:NurAdmin", StandardWert: "false",
            DsbFrage: "Dürfen Lehrkräfte Dateien mit Schülerdaten herunterladen – auch auf private Geräte?"),

        new EinstellungsDef(SitzungStunden, "anmeldung", "Dauer einer Anmeldung", EinstellungsTyp.Zahl,
            "So lange gilt eine Anmeldung ohne „Angemeldet bleiben“ höchstens; sie endet auch, wenn der Browser " +
            "geschlossen wird.",
            "12 Stunden", DateiSchluessel: "Jwt:StundenGueltig", StandardWert: "12",
            DsbFrage: "Wie lange darf eine Anmeldung ohne „Angemeldet bleiben“ gelten?",
            Min: 1, Max: 24, Einheit: "Stunden"),
        new EinstellungsDef(AngemeldetBleibenTage, "anmeldung", "„Angemeldet bleiben“", EinstellungsTyp.Zahl,
            "So lange bleibt angemeldet, wer „Angemeldet bleiben“ wählt (nur auf eigenen Geräten). 0 = die Anmeldeseite " +
            "bietet „Angemeldet bleiben“ nicht an.",
            "30 Tage", DateiSchluessel: "Jwt:TageAngemeldetBleiben", StandardWert: "30",
            DsbFrage: "Ist „Angemeldet bleiben“ auf eigenen Geräten erlaubt – und wie lange?",
            Min: 0, Max: 90, Einheit: "Tage", NullText: "aus"),
    };

    private static readonly Dictionary<string, EinstellungsDef> NachSchluessel =
        Alle.ToDictionary(d => d.Schluessel, StringComparer.Ordinal);

    public static EinstellungsDef? Finde(string? schluessel)
        => schluessel != null && NachSchluessel.TryGetValue(schluessel, out var d) ? d : null;

    private static readonly Regex SchuljahrMuster = new(@"^(\d{4})\s*/\s*(\d{2}|\d{4})$", RegexOptions.Compiled);
    private static readonly Regex Leerraum = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Prüft und vereinheitlicht eine Eingabe. Leer = Standard (null). fehler = verständlicher Grund, sonst null.
    /// </summary>
    public static string? Normalisieren(EinstellungsDef d, string? eingabe, out string? fehler)
    {
        fehler = null;
        var s = (eingabe ?? "").Trim();
        if (s.Length == 0) return null;
        switch (d.Typ)
        {
            case EinstellungsTyp.Text:
                if (s.Any(char.IsControl)) { fehler = "Bitte ohne Zeilenumbrüche und Steuerzeichen."; return null; }
                s = Leerraum.Replace(s, " ");
                if (d.MaxLaenge > 0 && s.Length > d.MaxLaenge) { fehler = $"Höchstens {d.MaxLaenge} Zeichen."; return null; }
                return s;

            case EinstellungsTyp.Schuljahr:
            {
                var m = SchuljahrMuster.Match(s);
                if (!m.Success) { fehler = "Bitte so schreiben: 2026/27."; return null; }
                int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                bool passt = m.Groups[2].Value.Length == 2 ? b == (a + 1) % 100 : b == a + 1;
                if (!passt || a < 2000 || a > 2099) { fehler = "Das Schuljahr passt nicht (z. B. 2026/27)."; return null; }
                return $"{a}/{(a + 1) % 100:00}";
            }

            case EinstellungsTyp.Datum:
            {
                if (!DateOnly.TryParseExact(s, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy" }, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var t) || t.Year < 2000 || t.Year > 2099)
                { fehler = "Bitte ein Datum wählen."; return null; }
                return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            case EinstellungsTyp.Zahl:
            {
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
                { fehler = "Bitte eine ganze Zahl eintragen."; return null; }
                if (z < d.Min || z > d.Max) { fehler = $"Erlaubt sind {d.Min} bis {d.Max}{(d.Einheit != null ? " " + d.Einheit : "")}."; return null; }
                return z.ToString(CultureInfo.InvariantCulture);
            }

            case EinstellungsTyp.Schalter:
            {
                var k = s.ToLowerInvariant();
                if (k is "true" or "ja" or "an" or "1") return "true";
                if (k is "false" or "nein" or "aus" or "0") return "false";
                fehler = "Bitte an oder aus wählen.";
                return null;
            }
        }
        fehler = "Unbekannter Typ.";
        return null;
    }

    /// <summary>Lesbarer Wert für Anzeige und Protokoll (null = „Standard (…)“).</summary>
    public static string Text(EinstellungsDef d, string? wert)
    {
        if (wert == null) return $"Standard ({d.StandardText})";
        switch (d.Typ)
        {
            case EinstellungsTyp.Datum:
                return DateOnly.TryParseExact(wert, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                    ? t.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : wert;
            case EinstellungsTyp.Zahl:
                if (wert == "0" && d.NullText != null) return $"0 ({d.NullText})";
                return d.Einheit != null ? $"{wert} {d.Einheit}" : wert;
            case EinstellungsTyp.Schalter:
                return wert == "true" ? "an" : "aus";
            case EinstellungsTyp.Text:
                return $"„{wert}“";
            default:
                return wert;
        }
    }

    /// <summary>Schuljahr zu einem Tag: ab 1. August das neue (z. B. 15.09.2026 → „2026/27“, 15.03.2027 → „2026/27“).</summary>
    public static string SchuljahrVon(DateOnly d)
    {
        int a = d.Month >= 8 ? d.Year : d.Year - 1;
        return $"{a}/{(a + 1) % 100:00}";
    }

    internal static DateOnly? AlsDatum(string? roh)
        => DateOnly.TryParseExact(roh, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    internal static int AlsZahl(string? roh, int standard)
        => int.TryParse(roh, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z) ? z : standard;

    internal static bool AlsSchalter(string? roh, bool standard)
        => roh == "true" ? true : roh == "false" ? false : standard;

    internal static int StandardZahl(string schluessel) => AlsZahl(Finde(schluessel)?.StandardWert, 0);
}

/// <summary>Aufgelöste Schulwerte – so, wie sie gerade gelten.</summary>
public sealed record SchulWerte(
    string SchuleName,
    string SchuleKurzname,
    string Schuljahr,
    DateOnly ErfassungAb,
    bool ErfassungAbAusKalender,   // Standard: erster Schultag laut Kalender (false: Einstellung bzw. heute)
    DateOnly ThemaAb,
    DateOnly? HalbjahrAb,          // Einstellung; null = Regel „1. Februar“ (FehlzeitenService.HalbjahrAb)
    int EintraegeLoeschenNachTagen,
    int ProtokollTage,
    bool BerichteNurAdmin,
    int SitzungStunden,
    int AngemeldetBleibenTage)
{
    public const string StandardAppName = "Stundenplan";

    /// <summary>Name der App (Titel, Manifest): Kurzname, sonst „Stundenplan“.</summary>
    public string AppName => string.IsNullOrWhiteSpace(SchuleKurzname) ? StandardAppName : SchuleKurzname;
}

/// <summary>Liest die Schuleinstellungen mit Zwischenspeicher (Singleton, Program.cs).</summary>
public sealed class Schuleinstellungen
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<Schuleinstellungen> _log;
    private readonly object _sperre = new();
    private IReadOnlyDictionary<string, Einstellung>? _zeilen;
    private int _stand;                      // steigt mit jeder Änderung (verhindert, dass ein alter Lesevorgang überschreibt)
    private DateTime _letzteWarnung = DateTime.MinValue;

    public Schuleinstellungen(IConfiguration cfg, ILogger<Schuleinstellungen> log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Nach einer Änderung: beim nächsten Lesen neu aus der Datenbank.</summary>
    public void Verwerfen()
    {
        lock (_sperre)
        {
            _zeilen = null;
            _stand++;
        }
    }

    public async Task<IReadOnlyDictionary<string, Einstellung>> ZeilenAsync(AppDbContext db, CancellationToken ct = default)
    {
        int stand;
        lock (_sperre)
        {
            if (_zeilen != null) return _zeilen;
            stand = _stand;
        }
        try
        {
            var liste = await db.Einstellungen.AsNoTracking().ToListAsync(ct);
            return Merken(liste, stand);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fehlschlag(ex);
        }
    }

    public IReadOnlyDictionary<string, Einstellung> Zeilen(AppDbContext db)
    {
        int stand;
        lock (_sperre)
        {
            if (_zeilen != null) return _zeilen;
            stand = _stand;
        }
        try
        {
            return Merken(db.Einstellungen.AsNoTracking().ToList(), stand);
        }
        catch (Exception ex)
        {
            return Fehlschlag(ex);
        }
    }

    private IReadOnlyDictionary<string, Einstellung> Merken(List<Einstellung> liste, int stand)
    {
        var d = liste.ToDictionary(e => e.Schluessel, StringComparer.Ordinal);
        lock (_sperre)
        {
            if (_stand == stand) _zeilen = d;
        }
        return d;
    }

    /// <summary>Tabelle nicht lesbar (z. B. fehlende Migration): Dateiwerte bzw. Standard, Warnung höchstens alle 5 Minuten.</summary>
    private IReadOnlyDictionary<string, Einstellung> Fehlschlag(Exception ex)
    {
        bool melden;
        lock (_sperre)
        {
            melden = DateTime.UtcNow - _letzteWarnung > TimeSpan.FromMinutes(5);
            if (melden) _letzteWarnung = DateTime.UtcNow;
        }
        if (melden)
            _log.LogWarning(ex, "Die Schuleinstellungen konnten nicht gelesen werden – es gelten die Werte aus appsettings bzw. der Standard.");
        return new Dictionary<string, Einstellung>(StringComparer.Ordinal);
    }

    /// <summary>Roher Wert eines Schlüssels: Zeile (null = Standard) bzw. – nur ohne Zeile – der Dateiwert.</summary>
    public string? Roh(IReadOnlyDictionary<string, Einstellung> zeilen, string schluessel)
    {
        if (zeilen.TryGetValue(schluessel, out var z)) return z.Wert;
        var d = EinstellungsKatalog.Finde(schluessel);
        if (d?.DateiSchluessel == null) return null;
        return EinstellungsKatalog.Normalisieren(d, _cfg[d.DateiSchluessel], out _);
    }

    public async Task<SchulWerte> LesenAsync(AppDbContext db, CancellationToken ct = default)
    {
        var zeilen = await ZeilenAsync(db, ct);
        var (sj, brauchtKalender) = Vorab(zeilen);
        DateOnly? start = null;
        if (brauchtKalender)
        {
            try
            {
                start = await db.Kalender.AsNoTracking().Where(k => k.Schuljahr == sj)
                    .Select(k => (DateOnly?)k.Start).FirstOrDefaultAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Kalender nicht lesbar."); }
        }
        return Bauen(zeilen, sj, start);
    }

    public SchulWerte Lesen(AppDbContext db)
    {
        var zeilen = Zeilen(db);
        var (sj, brauchtKalender) = Vorab(zeilen);
        DateOnly? start = null;
        if (brauchtKalender)
        {
            try
            {
                start = db.Kalender.AsNoTracking().Where(k => k.Schuljahr == sj)
                    .Select(k => (DateOnly?)k.Start).FirstOrDefault();
            }
            catch (Exception ex) { _log.LogDebug(ex, "Kalender nicht lesbar."); }
        }
        return Bauen(zeilen, sj, start);
    }

    private (string Schuljahr, bool BrauchtKalender) Vorab(IReadOnlyDictionary<string, Einstellung> zeilen)
    {
        string sj = Roh(zeilen, EinstellungsKatalog.Schuljahr) ?? EinstellungsKatalog.SchuljahrVon(KlassenbuchService.Heute());
        bool brauchtKalender = EinstellungsKatalog.AlsDatum(Roh(zeilen, EinstellungsKatalog.ErfassungAb)) == null;
        return (sj, brauchtKalender);
    }

    /// <summary>Löst alle Werte auf. kalenderStart: erster Schultag des Schuljahres (nur nötig ohne „Erfassung ab“).</summary>
    public SchulWerte Bauen(IReadOnlyDictionary<string, Einstellung> zeilen, string schuljahr, DateOnly? kalenderStart)
    {
        string? R(string k) => Roh(zeilen, k);
        var erfassung = EinstellungsKatalog.AlsDatum(R(EinstellungsKatalog.ErfassungAb));
        bool ausKalender = erfassung == null && kalenderStart != null;
        var erfassungAb = erfassung ?? kalenderStart ?? KlassenbuchService.Heute();
        var themaAb = EinstellungsKatalog.AlsDatum(R(EinstellungsKatalog.ThemaAb)) ?? erfassungAb;
        int Z(string k) => EinstellungsKatalog.AlsZahl(R(k), EinstellungsKatalog.StandardZahl(k));
        return new SchulWerte(
            SchuleName: R(EinstellungsKatalog.SchuleName) ?? "",
            SchuleKurzname: R(EinstellungsKatalog.SchuleKurzname) ?? "",
            Schuljahr: schuljahr,
            ErfassungAb: erfassungAb,
            ErfassungAbAusKalender: ausKalender,
            ThemaAb: themaAb,
            HalbjahrAb: EinstellungsKatalog.AlsDatum(R(EinstellungsKatalog.HalbjahrAb)),
            EintraegeLoeschenNachTagen: Z(EinstellungsKatalog.EintraegeLoeschen),
            ProtokollTage: Z(EinstellungsKatalog.ProtokollTage),
            BerichteNurAdmin: EinstellungsKatalog.AlsSchalter(R(EinstellungsKatalog.BerichteNurAdmin), false),
            SitzungStunden: Z(EinstellungsKatalog.SitzungStunden),
            AngemeldetBleibenTage: Z(EinstellungsKatalog.AngemeldetBleibenTage));
    }

    // -----------------------------------------------------------------
    //  Übernahme beim Start (Entscheidung 140)
    // -----------------------------------------------------------------

    public const string UebernahmeAkteur = "Übernahme (appsettings)";

    /// <summary>
    /// Legt fehlende Zeilen an – mit dem Wert aus appsettings, sonst Standard – und protokolliert die übernommenen
    /// Werte. Weicht bei vorhandenen Zeilen ein Dateiwert ab, steht ein Hinweis im Log (der Dateiwert gilt nicht).
    /// </summary>
    public static void Uebernehmen(AppDbContext db, IConfiguration cfg, ILogger log)
    {
        var vorhanden = db.Einstellungen.AsNoTracking().ToDictionary(e => e.Schluessel, StringComparer.Ordinal);
        var jetzt = DateTime.UtcNow;
        var uebernommen = new List<string>();
        int neu = 0;

        foreach (var d in EinstellungsKatalog.Alle)
        {
            string? dateiWert = null;
            if (d.DateiSchluessel != null && !string.IsNullOrWhiteSpace(cfg[d.DateiSchluessel]))
            {
                dateiWert = EinstellungsKatalog.Normalisieren(d, cfg[d.DateiSchluessel], out var fehler);
                if (fehler != null)
                    log.LogWarning("Einstellungen: „{Datei}“ in appsettings ist ungültig ({Fehler}) – es gilt der Standard.",
                        d.DateiSchluessel, fehler);
            }

            if (vorhanden.TryGetValue(d.Schluessel, out var zeile))
            {
                if (dateiWert != null && dateiWert != zeile.Wert)
                    log.LogInformation("Einstellungen: „{Datei}“ steht in appsettings ({DateiWert}) – es gilt der Wert aus der App " +
                        "(Kontomenü → Einstellungen): {Wert}.", d.DateiSchluessel, EinstellungsKatalog.Text(d, dateiWert),
                        EinstellungsKatalog.Text(d, zeile.Wert));
                continue;
            }

            db.Einstellungen.Add(new Einstellung
            {
                Schluessel = d.Schluessel,
                Wert = dateiWert,
                GeaendertAm = dateiWert != null ? jetzt : null,
                GeaendertVon = dateiWert != null ? UebernahmeAkteur : null,
            });
            neu++;
            if (dateiWert != null) uebernommen.Add($"{d.Titel}: {EinstellungsKatalog.Text(d, dateiWert)}");
        }

        if (neu == 0) return;
        db.EinstellungsProtokolle.Add(new EinstellungsProtokoll
        {
            Zeit = jetzt,
            Akteur = "System",
            Schluessel = "*",
            Neu = vorhanden.Count == 0 ? "Startwerte angelegt" : $"{neu} neue Einstellung(en) angelegt",
            Details = uebernommen.Count > 0 ? "aus appsettings übernommen: " + string.Join("; ", uebernommen) : "alle mit Standard",
        });
        db.SaveChanges();
        log.LogInformation("Einstellungen: {Anzahl} Einstellung(en) angelegt, {Uebernommen} Wert(e) aus appsettings übernommen. " +
            "Ab jetzt gelten die Werte in der App (Kontomenü → Einstellungen).", neu, uebernommen.Count);
    }
}
