using System.Globalization;

namespace VertretungApi.Helpers;

// =====================================================================
//  PB-1a (Entscheidung 310): Zeitzone des Programms statt Uhr des Rechners.
//
//  „Betrieb:Zeitzone“ (Einstellungsdatei bzw. Umgebungsvariable Betrieb__Zeitzone; Standard Europe/Berlin) legt fest, in
//  welcher Zone die API rechnet: „heute“ (Testdatum.Heute), Tagesgrenzen der Fristen, Tag einer Zeile im Leseprotokoll,
//  Uhrzeiten in PDFs, Archiven und Texten der App, Dateinamen mit Datum und das tägliche Aufräumen um 3 Uhr – unabhängig
//  davon, ob der Server auf UTC steht. Gespeichert wird weiter UTC (DateTime.UtcNow); nur Anzeige und Tagesrechnung
//  gehen über diesen Helfer.
//
//  Gesetzt wird die Zone beim Start (Program.cs) vor allem anderen; ein unbekannter Name beendet den Start
//  (Betrieb.Pruefen, auch in der Entwicklung). Windows kennt „Europe/Berlin“ über ICU; der Windows-Name
//  „W. Europe Standard Time“ geht ebenfalls.
//
//  Regel: Außer in dieser Datei kommen im Programm kein DateTime.Now, DateTime.Today, ToLocalTime(), DateTimeKind.Local
//  und TimeZoneInfo.Local vor – sonst schleicht sich die Uhr des Rechners wieder ein (Prüfskript im Chat, PB-1a).
// =====================================================================

public static class Ortszeit
{
    public const string Schluessel = "Betrieb:Zeitzone";
    public const string Standard = "Europe/Berlin";

    /// <summary>Zone des Programms (beim Start gesetzt; bis dahin Europe/Berlin bzw. – ohne Zonendaten – UTC).</summary>
    public static TimeZoneInfo Zone { get; private set; } = Finden(Standard, out _) ?? TimeZoneInfo.Utc;

    /// <summary>Name der Zone, wie eingestellt (z. B. „Europe/Berlin“).</summary>
    public static string Name { get; private set; } = Standard;

    /// <summary>Beim Start (Program.cs).</summary>
    public static void Setzen(TimeZoneInfo zone, string name)
    {
        Zone = zone;
        Name = name;
    }

    /// <summary>Zone laut Einstellung (leer = Standard). null = unbekannt; <paramref name="fehler"/> nennt dann den Grund.</summary>
    public static TimeZoneInfo? Lesen(IConfiguration cfg, out string name, out string? fehler)
    {
        var roh = cfg[Schluessel];
        name = string.IsNullOrWhiteSpace(roh) ? Standard : roh.Trim();
        var zone = Finden(name, out var grund);
        fehler = zone != null ? null
            : $"{Schluessel} „{name}“ ist keine bekannte Zeitzone ({grund}) – Schreibweise wie „Europe/Berlin“ " +
              "(Umgebungsvariable Betrieb__Zeitzone bzw. Einstellungsdatei).";
        return zone;
    }

    /// <summary>Zone nach IANA-Namen („Europe/Berlin“) bzw. Windows-Namen („W. Europe Standard Time“); null = unbekannt.</summary>
    public static TimeZoneInfo? Finden(string name, out string grund)
    {
        grund = "unbekannt";
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        if (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('/') || name.Contains('\\'))
        {
            grund = "kein Name einer Zeitzone";
            return null;
        }
        if (Suchen(name, ref grund) is TimeZoneInfo direkt) return direkt;
        // Windows ↔ IANA (ICU): dann geht auf jedem System jede der beiden Schreibweisen.
        string? anders = TimeZoneInfo.TryConvertIanaIdToWindowsId(name, out var w) ? w
                       : TimeZoneInfo.TryConvertWindowsIdToIanaId(name, out var i) ? i
                       // Windows ohne ICU kennt keine IANA-Namen – für den Standard den Windows-Namen direkt versuchen.
                       : string.Equals(name, Standard, StringComparison.OrdinalIgnoreCase) ? "W. Europe Standard Time" : null;
        return anders != null ? Suchen(anders, ref grund) : null;
    }

    private static TimeZoneInfo? Suchen(string id, ref string grund)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or ArgumentException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            grund = "die Zonendaten des Systems sind fehlerhaft";
            return null;
        }
        catch (System.Security.SecurityException)
        {
            grund = "die Zonendaten des Systems sind nicht lesbar";
            return null;
        }
    }

    // -----------------------------------------------------------------
    //  Rechnen
    // -----------------------------------------------------------------

    /// <summary>Wanduhr in der Zone des Programms (Kind = Unspecified – nur anzeigen bzw. damit rechnen, nie speichern).</summary>
    public static DateTime Jetzt => AusUtc(DateTime.UtcNow);

    /// <summary>Heutiges Datum in der Zone (echte Uhr; das Testdatum der Entwicklung steht in Testdatum.Heute).</summary>
    public static DateOnly Heute => DateOnly.FromDateTime(Jetzt);

    /// <summary>Gespeicherter UTC-Zeitpunkt → Wanduhr in der Zone des Programms.</summary>
    public static DateTime AusUtc(DateTime utc) => AusUtc(utc, Zone);

    /// <summary>Gespeicherter UTC-Zeitpunkt → Tag in der Zone des Programms.</summary>
    public static DateOnly TagAusUtc(DateTime utc) => DateOnly.FromDateTime(AusUtc(utc));

    /// <summary>Wanduhr in der Zone des Programms → UTC (Kind = Utc).</summary>
    public static DateTime NachUtc(DateTime ortszeit) => NachUtc(ortszeit, Zone);

    /// <summary>Beginn eines Tages (0 Uhr in der Zone des Programms) als UTC.</summary>
    public static DateTime TagesbeginnUtc(DateOnly tag) => NachUtc(tag.ToDateTime(TimeOnly.MinValue));

    public static DateTime AusUtc(DateTime utc, TimeZoneInfo zone)
        => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone),
            DateTimeKind.Unspecified);

    /// <summary>
    /// Wanduhr → UTC. Liegt sie in der Lücke der Umstellung auf Sommerzeit (Berlin: letzter Sonntag im März 2:00–2:59),
    /// gilt der Versatz davor (2:30 → wie 3:30 Sommerzeit). Doppelte Zeiten im Oktober (2:00–2:59) gelten als Normalzeit
    /// (die spätere) – so wie TimeZoneInfo es vorgibt.
    /// </summary>
    public static DateTime NachUtc(DateTime ortszeit, TimeZoneInfo zone)
    {
        var t = DateTime.SpecifyKind(ortszeit, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(t))
            return TimeZoneInfo.ConvertTimeToUtc(t.AddHours(-3), zone).AddHours(3);
        return TimeZoneInfo.ConvertTimeToUtc(t, zone);
    }

    /// <summary>
    /// Wartezeit bis zum nächsten Zeitpunkt „<paramref name="stunde"/> Uhr“ in der Zone (Aufräumen um 3 Uhr, P3). Gibt
    /// es die Uhrzeit an einem Tag nicht (Lücke der Umstellung), gilt die erste gültige Zeit danach; jede Uhrzeit kommt je
    /// Tag höchstens einmal vor (doppelte Zeiten im Oktober: die spätere).
    /// </summary>
    public static TimeSpan BisNaechsteUhrzeit(DateTime jetztUtc, int stunde, TimeZoneInfo zone)
    {
        var jetzt = DateTime.SpecifyKind(jetztUtc, DateTimeKind.Utc);
        var tag = AusUtc(jetzt, zone).Date;
        for (int i = 0; i <= 2; i++)
        {
            var ziel = NachUtc(tag.AddDays(i).AddHours(stunde), zone);
            if (ziel > jetzt) return ziel - jetzt;
        }
        return TimeSpan.FromHours(24);   // nicht erreichbar (jeder Tag hat die Uhrzeit bzw. die Zeit danach)
    }

    // -----------------------------------------------------------------
    //  Startlog
    // -----------------------------------------------------------------

    /// <summary>„Zeitzone Europe/Berlin · jetzt 27.11.2026 18:05 · Uhr des Rechners: UTC (weicht ab …)“.</summary>
    public static string StartText()
    {
        var utc = DateTime.UtcNow;
        var rechner = TimeZoneInfo.Local;
        bool gleich = rechner.GetUtcOffset(utc) == Zone.GetUtcOffset(utc);
        return $"Zeitzone {Name} · jetzt {AusUtc(utc).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} · " +
               $"Uhr des Rechners: {rechner.Id}" +
               (gleich ? "" : $" (weicht ab – die API rechnet trotzdem mit {Name})");
    }
}
