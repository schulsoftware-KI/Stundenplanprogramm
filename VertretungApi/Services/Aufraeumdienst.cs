using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;

namespace VertretungApi.Services;

// =====================================================================
//  P3 – E-1 (Entscheidung 141): Aufräumen nach Fristen – beim Start, täglich um 3 Uhr (Ortszeit des
//  Servers) und gleich nach einer bestätigten kürzeren Frist in den Einstellungen.
//    - Kontenprotokoll, Download-Protokoll, Einstellungsprotokoll: „Aufbewahrung der Protokolle“
//      (Datenschutz.ProtokollTage, 0 = nicht löschen; Entscheidung 202: eine Frist für alle drei).
//    - Klassenbucheinträge samt Verlauf: „Löschfrist der Klassenbucheinträge“ (0 = aus, Entscheidung 39).
//  Bisher lief das nur beim Start (DatenbankStart). Gelöscht werden nur Datensätze, nie Einstellungen.
// =====================================================================

public sealed record AufraeumErgebnis(int Kontenprotokoll, int Downloads, int Einstellungsprotokoll, int Eintraege)
{
    public int Protokolle => Kontenprotokoll + Downloads + Einstellungsprotokoll;
}

public static class Aufraeumen
{
    /// <summary>Alle Fristen anwenden. Fehler landen im Log und brechen nichts ab.</summary>
    public static AufraeumErgebnis Ausfuehren(AppDbContext db, SchulWerte w, ILogger log)
    {
        int konten = 0, downloads = 0, einstellungen = 0, eintraege = 0;
        try
        {
            konten = KontenService.ProtokollAufraeumen(db, w.ProtokollTage);
            downloads = BerichtService.ProtokollAufraeumen(db, w.ProtokollTage);
            einstellungen = EinstellungsProtokollAufraeumen(db, w.ProtokollTage);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Die Protokolle konnten nicht aufgeräumt werden.");
        }
        try
        {
            eintraege = KlassenbuchEintragService.Aufraeumen(db, w.EintraegeLoeschenNachTagen);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Alte Klassenbucheinträge konnten nicht gelöscht werden.");
        }

        if (konten > 0) log.LogInformation("{Anzahl} alte Protokolleinträge gelöscht.", konten);
        if (downloads > 0) log.LogInformation("{Anzahl} alte Einträge im Download-Protokoll gelöscht.", downloads);
        if (einstellungen > 0) log.LogInformation("{Anzahl} alte Einträge im Protokoll der Einstellungen gelöscht.", einstellungen);
        if (eintraege > 0) log.LogInformation("{Anzahl} Klassenbucheinträge nach Ablauf der Frist gelöscht.", eintraege);
        return new AufraeumErgebnis(konten, downloads, einstellungen, eintraege);
    }

    public static int EinstellungsProtokollAufraeumen(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = DateTime.UtcNow.AddDays(-tage);
        return db.EinstellungsProtokolle.Where(p => p.Zeit < grenze).ExecuteDelete();
    }

    // ---- Zählen vor einer kürzeren Frist (Entscheidung 141) – dieselben Grenzen wie oben ----

    /// <summary>Klassenbucheinträge, die mit dieser Frist gelöscht würden.</summary>
    public static Task<int> EintraegeZaehlenAsync(AppDbContext db, int tage)
    {
        if (tage <= 0) return Task.FromResult(0);
        var grenze = KlassenbuchService.Heute().AddDays(-tage);
        return db.KlassenbuchEintraege.CountAsync(e => e.Datum < grenze);
    }

    /// <summary>Protokolleinträge (Konten, Downloads, Einstellungen), die mit dieser Frist gelöscht würden.</summary>
    public static async Task<int> ProtokolleZaehlenAsync(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = DateTime.UtcNow.AddDays(-tage);
        return await db.KontoProtokolle.CountAsync(p => p.Zeit < grenze)
             + await db.DownloadProtokolle.CountAsync(p => p.Zeit < grenze)
             + await db.EinstellungsProtokolle.CountAsync(p => p.Zeit < grenze);
    }
}

/// <summary>Hintergrunddienst: räumt täglich um 3 Uhr auf (der Lauf beim Start steht in DatenbankStart).</summary>
public sealed class Aufraeumdienst : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Schuleinstellungen _einstellungen;
    private readonly ILogger<Aufraeumdienst> _log;

    public Aufraeumdienst(IServiceScopeFactory scopes, Schuleinstellungen einstellungen, ILogger<Aufraeumdienst> log)
    {
        _scopes = scopes;
        _einstellungen = einstellungen;
        _log = log;
    }

    /// <summary>Zeit bis zum nächsten Lauf um 3 Uhr (Ortszeit).</summary>
    public static TimeSpan BisNaechsterLauf(DateTime jetzt)
    {
        var ziel = jetzt.Date.AddHours(3);
        if (ziel <= jetzt) ziel = ziel.AddDays(1);
        return ziel - jetzt;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(BisNaechsterLauf(DateTime.Now), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var werte = await _einstellungen.LesenAsync(db, stop);
                var erg = Aufraeumen.Ausfuehren(db, werte, _log);
                _log.LogInformation("Tägliches Aufräumen: {Protokolle} Protokolleinträge, {Eintraege} Klassenbucheinträge gelöscht.",
                    erg.Protokolle, erg.Eintraege);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Tägliches Aufräumen fehlgeschlagen – nächster Versuch morgen.");
            }
        }
    }
}
