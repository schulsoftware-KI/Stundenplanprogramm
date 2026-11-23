using System.Globalization;

namespace VertretungApi.Helpers;

// =====================================================================
//  P4a (Entscheidung 221): Testdatum – nur in der Entwicklung.
//
//  „Entwicklung:Heute“ (Einstellungsdatei bzw. Umgebungsvariable Entwicklung__Heute, z. B. 2027-01-18)
//  verschiebt das „Heute“ der API auf diesen festen Tag: Schuljahr nach dem Datum, Halbjahr, Stundenblatt
//  („liegt in der Zukunft“), Gültigkeit der Zugehörigkeiten, Fristen nach Datum (Einträge, Abwesenheitsgründe,
//  Konten abgegangener Schüler), Bereich „Jahresablauf“. So lassen sich Halbjahres- und Schuljahreswechsel
//  auch im November testen.
//
//  Nicht verschoben werden Zeitstempel (Protokolle, Anmeldung, Login-Bremse, Fristen der Protokolle) – sie
//  laufen mit der echten Uhr.
//
//  Im Echtbetrieb verweigert die Startprüfung den Start, wenn der Wert gesetzt ist (Betrieb.Pruefen, wie 5.1).
//  Die App zeigt ein Band „Testdatum …“ (GET api/auth/optionen → testdatum).
// =====================================================================

public static class Testdatum
{
    public const string Schluessel = "Entwicklung:Heute";

    /// <summary>Gesetztes Testdatum (null = echtes Datum).</summary>
    public static DateOnly? Wert { get; private set; }

    /// <summary>Wert aus der Konfiguration (null = nicht gesetzt bzw. ungültig; fehler nennt dann den Grund).</summary>
    public static DateOnly? Lesen(IConfiguration cfg, out string? fehler)
    {
        fehler = null;
        var roh = cfg[Schluessel];
        if (string.IsNullOrWhiteSpace(roh)) return null;
        if (DateOnly.TryParseExact(roh.Trim(), new[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var tag) && tag.Year >= 2000 && tag.Year <= 2099)
            return tag;
        fehler = $"{Schluessel} (Testdatum) ist kein Datum („{roh.Trim()}“) – Schreibweise 2027-01-18.";
        return null;
    }

    /// <summary>Beim Start (Program.cs) – nur in der Entwicklung.</summary>
    public static void Setzen(DateOnly? tag) => Wert = tag;

    /// <summary>„Heute“ der API: das Testdatum bzw. das Datum dieses Rechners.</summary>
    public static DateOnly Heute() => Wert ?? DateOnly.FromDateTime(DateTime.Now);

    public static string? Text => Wert?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
