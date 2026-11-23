using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Model;
using K = VertretungApi.Services.EinstellungsKatalog;

namespace VertretungApi.Services;

// =====================================================================
//  P3 – E-1 (Entscheidung 141): Aufräumen nach Fristen – beim Start, täglich um 3 Uhr (Ortszeit des
//  Servers) und gleich nach einer bestätigten kürzeren Frist in den Einstellungen.
//    - Kontenprotokoll, Download-Protokoll, Einstellungsprotokoll: „Aufbewahrung der Protokolle“
//      (Datenschutz.ProtokollTage, 0 = nicht löschen; Entscheidung 202: eine Frist für alle drei).
//    - Klassenbucheinträge samt Verlauf: „Löschfrist der Klassenbucheinträge“ (0 = aus, Entscheidung 39).
//  Gelöscht werden nur Datensätze, nie Einstellungen.
//
//  P4a (5.3a, Entscheidungen 208, 209):
//    - Protokolle der SchILD-Importe ebenfalls unter „Aufbewahrung der Protokolle“ – außer denen, die der
//      Import noch braucht: je Schuljahr + Abschnitt + Art (Pseudo/echt) das neueste, je Schuljahr + Art das mit
//      dem spätesten Stichtag und je Art das neueste mit Schlüsselkennung (Status, „frühestens ab“, Sperre bei
//      anderem Schlüssel).
//    - „Abwesenheitsgründe der Vertretungen löschen nach … Tagen“: Grund und Bemerkung veröffentlichter
//      Vertretungen, deren Tag länger zurückliegt (der Eintrag bleibt).
//    - „Konten abgegangener Schüler löschen nach … Tagen“: gesperrte Schülerkonten mit Abgang laut SchILD, deren
//      Abgang länger zurückliegt – je Konto ein Eintrag im Kontenprotokoll; Schüler und Klassenbuchdaten bleiben.
//    - Löschen nie still (209): Zahlen im Log und je Frist ein Eintrag im Protokoll der Einstellungen
//      („Gelöscht: …“, Akteur „System (Löschfrist)“) – ohne Namen.
//  Fristen nach Datum (Einträge, Gründe, Abgänge) rechnen mit dem „Heute“ der API (Testdatum, 221), Fristen der
//  Protokolle mit der echten Uhr (Zeitstempel).
//  P6a (Entscheidung 228): das Anmeldeprotokoll ebenfalls unter „Aufbewahrung der Protokolle“.
//  P6b (Entscheidung 233): das Leseprotokoll ebenfalls (nach dem Tag des Eintrags).
// =====================================================================

public sealed record AufraeumErgebnis(int Kontenprotokoll, int Downloads, int Einstellungsprotokoll, int Eintraege,
    int SchildProtokolle = 0, int Gruende = 0, int Konten = 0, int Anmeldungen = 0, int Ansichten = 0)
{
    public int Protokolle => Kontenprotokoll + Downloads + Einstellungsprotokoll + SchildProtokolle + Anmeldungen + Ansichten;
}

/// <summary>Was die Fristen von den SchILD-Importprotokollen brauchen (ohne Zahlen der Schüler).</summary>
public sealed record SchildProtokollKurz(int Id, DateTime Zeit, string Schuljahr, int Abschnitt, bool IstPseudo,
    DateOnly GiltAb, string? Kennung);

public static class Aufraeumen
{
    public const string Akteur = "System (Löschfrist)";

    private static string D(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>Alle Fristen anwenden. Fehler landen im Log und brechen nichts ab.</summary>
    public static AufraeumErgebnis Ausfuehren(AppDbContext db, SchulWerte w, ILogger log)
    {
        int konten = 0, downloads = 0, einstellungen = 0, schild = 0, eintraege = 0, gruende = 0, kontenAbgang = 0, anmeldungen = 0;
        int ansichten = 0;
        var heute = KlassenbuchService.Heute();
        try
        {
            konten = KontenService.ProtokollAufraeumen(db, w.ProtokollTage);
            downloads = BerichtService.ProtokollAufraeumen(db, w.ProtokollTage);
            einstellungen = EinstellungsProtokollAufraeumen(db, w.ProtokollTage);
            schild = SchildProtokolleAufraeumen(db, w.ProtokollTage);
            anmeldungen = Anmeldeprotokoll.Aufraeumen(db, w.ProtokollTage);   // P6a (228)
            ansichten = Leseprotokoll.Aufraeumen(db, w.ProtokollTage);        // P6b (233)
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
        try
        {
            gruende = GruendeAufraeumen(db, w.GruendeLoeschenNachTagen, heute);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Alte Abwesenheitsgründe der Vertretungen konnten nicht gelöscht werden.");
        }
        try
        {
            kontenAbgang = AbgangKontenAufraeumen(db, w.AbgangKontenLoeschenNachTagen, heute);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Konten abgegangener Schüler konnten nicht gelöscht werden.");
        }

        if (konten > 0) log.LogInformation("{Anzahl} alte Protokolleinträge gelöscht.", konten);
        if (downloads > 0) log.LogInformation("{Anzahl} alte Einträge im Download-Protokoll gelöscht.", downloads);
        if (einstellungen > 0) log.LogInformation("{Anzahl} alte Einträge im Protokoll der Einstellungen gelöscht.", einstellungen);
        if (schild > 0) log.LogInformation("{Anzahl} alte Protokolle von SchILD-Importen gelöscht.", schild);
        if (anmeldungen > 0) log.LogInformation("{Anzahl} alte Einträge im Anmeldeprotokoll gelöscht.", anmeldungen);
        if (ansichten > 0) log.LogInformation("{Anzahl} alte Einträge im Leseprotokoll gelöscht.", ansichten);
        if (eintraege > 0) log.LogInformation("{Anzahl} Klassenbucheinträge nach Ablauf der Frist gelöscht.", eintraege);
        if (gruende > 0) log.LogInformation("Abwesenheitsgründe in {Anzahl} Vertretungen nach Ablauf der Frist gelöscht.", gruende);
        if (kontenAbgang > 0) log.LogInformation("{Anzahl} Konten abgegangener Schüler nach Ablauf der Frist gelöscht.", kontenAbgang);

        // 209: Löschen nie still – je Frist ein Eintrag mit Zahlen im Protokoll der Einstellungen.
        try
        {
            var jetzt = DateTime.UtcNow;
            void Merke(string schluessel, string text) => db.EinstellungsProtokolle.Add(new EinstellungsProtokoll
            {
                Zeit = jetzt, Akteur = Akteur, Schluessel = schluessel, Details = text
            });
            int protokolle = konten + downloads + einstellungen + schild + anmeldungen + ansichten;
            if (protokolle > 0)
                Merke(K.ProtokollTage, $"Gelöscht: {protokolle} Protokolleinträge älter als {w.ProtokollTage} Tage " +
                                       $"(Konten {konten}, Anmeldungen {anmeldungen}, Ansichten {ansichten}, " +
                                       $"Downloads {downloads}, Einstellungen {einstellungen}, SchILD-Importe {schild}).");
            if (eintraege > 0)
                Merke(K.EintraegeLoeschen, $"Gelöscht: {eintraege} Klassenbucheinträge samt Verlauf mit Datum vor dem " +
                                           $"{D(heute.AddDays(-w.EintraegeLoeschenNachTagen))}.");
            if (gruende > 0)
                Merke(K.GruendeLoeschen, $"Gelöscht: Grund bzw. Bemerkung in {gruende} Vertretungen vor dem " +
                                         $"{D(heute.AddDays(-w.GruendeLoeschenNachTagen))} (die Vertretungen bleiben).");
            if (kontenAbgang > 0)
                Merke(K.AbgangKontenLoeschen, $"Gelöscht: {kontenAbgang} gesperrte Konten abgegangener Schülerinnen und " +
                                              $"Schüler (Abgang vor dem {D(heute.AddDays(-w.AbgangKontenLoeschenNachTagen))}; " +
                                              "einzeln im Kontenprotokoll).");
            if (protokolle + eintraege + gruende + kontenAbgang > 0) db.SaveChanges();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Das Aufräumen konnte nicht im Protokoll der Einstellungen vermerkt werden.");
        }
        return new AufraeumErgebnis(konten, downloads, einstellungen, eintraege, schild, gruende, kontenAbgang, anmeldungen,
            ansichten);
    }

    public static int EinstellungsProtokollAufraeumen(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = DateTime.UtcNow.AddDays(-tage);
        return db.EinstellungsProtokolle.Where(p => p.Zeit < grenze).ExecuteDelete();
    }

    // -----------------------------------------------------------------
    //  P4a: SchILD-Importprotokolle (unter „Aufbewahrung der Protokolle“)
    // -----------------------------------------------------------------

    private static List<SchildProtokollKurz> SchildProtokolle(AppDbContext db)
        => db.SchildImportProtokolle.AsNoTracking()
            .Select(p => new SchildProtokollKurz(p.Id, p.Zeit, p.Schuljahr, p.Abschnitt, p.IstPseudo, p.GiltAb, p.SchluesselKennung))
            .ToList();

    /// <summary>
    /// Ids der Protokolle älter als <paramref name="grenze"/> – ohne die, die der Import noch braucht: je Schuljahr +
    /// Abschnitt + Art das neueste, je Schuljahr + Art das mit dem spätesten Stichtag, je Art das neueste mit Kennung.
    /// </summary>
    public static List<int> SchildZuLoeschen(IReadOnlyCollection<SchildProtokollKurz> alle, DateTime grenze)
    {
        var behalten = new HashSet<int>();
        foreach (var g in alle.GroupBy(p => (p.Schuljahr, p.Abschnitt, p.IstPseudo)))
            behalten.Add(g.OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id).First().Id);
        foreach (var g in alle.GroupBy(p => (p.Schuljahr, p.IstPseudo)))
            behalten.Add(g.OrderByDescending(p => p.GiltAb).ThenByDescending(p => p.Zeit).ThenByDescending(p => p.Id).First().Id);
        foreach (var g in alle.Where(p => !string.IsNullOrEmpty(p.Kennung)).GroupBy(p => p.IstPseudo))
            behalten.Add(g.OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id).First().Id);
        return alle.Where(p => p.Zeit < grenze && !behalten.Contains(p.Id)).Select(p => p.Id).OrderBy(id => id).ToList();
    }

    public static int SchildProtokolleAufraeumen(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var ids = SchildZuLoeschen(SchildProtokolle(db), DateTime.UtcNow.AddDays(-tage));
        int n = 0;
        foreach (var teil in ids.Chunk(500))
            n += db.SchildImportProtokolle.Where(p => teil.Contains(p.Id)).ExecuteDelete();
        return n;
    }

    // -----------------------------------------------------------------
    //  P4a: Abwesenheitsgründe der Vertretungen (VO-DV II)
    // -----------------------------------------------------------------

    /// <summary>Vertretungen mit Grund bzw. Bemerkung an Tagen vor <paramref name="grenze"/>.</summary>
    private static IQueryable<VertretungsEintragDb> MitGrundVor(AppDbContext db, DateOnly grenze)
    {
        var tage = db.Tage.Where(t => t.Datum < grenze).Select(t => t.Id);
        return db.Eintraege.Where(e => tage.Contains(e.TagId)
                                       && ((e.Grund != null && e.Grund != "") || (e.Bemerkung != null && e.Bemerkung != "")));
    }

    /// <summary>Grund und Bemerkung leeren (der Eintrag bleibt). Rückgabe: betroffene Vertretungen.</summary>
    public static int GruendeAufraeumen(AppDbContext db, int tage, DateOnly heute)
    {
        if (tage <= 0) return 0;
        int n = 0;
        // In Stücken: geladen und geleert über den Change Tracker (ohne ExecuteUpdate).
        for (int runde = 0; runde < 1000; runde++)
        {
            var teil = MitGrundVor(db, heute.AddDays(-tage)).OrderBy(x => x.Id).Take(1000).ToList();
            if (teil.Count == 0) break;
            foreach (var e in teil)
            {
                e.Grund = null;
                e.Bemerkung = null;
            }
            db.SaveChanges();
            n += teil.Count;
            foreach (var e in teil) db.Entry(e).State = EntityState.Detached;
        }
        return n;
    }

    public static Task<int> GruendeZaehlenAsync(AppDbContext db, int tage)
        => tage <= 0 ? Task.FromResult(0) : MitGrundVor(db, KlassenbuchService.Heute().AddDays(-tage)).CountAsync();

    // -----------------------------------------------------------------
    //  P4a: Konten abgegangener Schüler
    // -----------------------------------------------------------------

    /// <summary>Gesperrte Schülerkonten, deren Schüler laut SchILD vor <paramref name="grenze"/> abgegangen ist.</summary>
    private static IQueryable<Benutzer> AbgangKontenVor(AppDbContext db, DateOnly grenze)
    {
        var schueler = db.Schueler.Where(s => s.Status == "abgang" && s.AbgangAm != null && s.AbgangAm < grenze).Select(s => s.Id);
        return db.Benutzer.Where(b => b.Rolle == BenutzerRolle.Schueler && b.Gesperrt && b.SchuelerId != null
                                      && schueler.Contains(b.SchuelerId.Value));
    }

    public static int AbgangKontenAufraeumen(AppDbContext db, int tage, DateOnly heute)
    {
        if (tage <= 0) return 0;
        var grenze = heute.AddDays(-tage);
        var konten = AbgangKontenVor(db, grenze).ToList();
        if (konten.Count == 0) return 0;
        var ids = konten.Select(b => b.SchuelerId!.Value).Distinct().ToList();
        var abgang = db.Schueler.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.AbgangAm }).ToList()
            .ToDictionary(s => s.Id, s => s.AbgangAm);
        foreach (var k in konten)
        {
            string am = abgang.TryGetValue(k.SchuelerId!.Value, out var a) && a is DateOnly ad ? D(ad) : "?";
            KontenService.Protokoll(db, Akteur, KontenService.Aktion.Geloescht, k,
                $"Löschfrist für Konten abgegangener Schüler ({tage} Tage, Abgang am {am})");
            db.Benutzer.Remove(k);
        }
        db.SaveChanges();
        return konten.Count;
    }

    public static Task<int> AbgangKontenZaehlenAsync(AppDbContext db, int tage)
        => tage <= 0 ? Task.FromResult(0) : AbgangKontenVor(db, KlassenbuchService.Heute().AddDays(-tage)).CountAsync();

    // ---- Zählen vor einer kürzeren Frist (Entscheidung 141) – dieselben Grenzen wie oben ----

    /// <summary>Klassenbucheinträge, die mit dieser Frist gelöscht würden.</summary>
    public static Task<int> EintraegeZaehlenAsync(AppDbContext db, int tage)
    {
        if (tage <= 0) return Task.FromResult(0);
        var grenze = KlassenbuchService.Heute().AddDays(-tage);
        return db.KlassenbuchEintraege.CountAsync(e => e.Datum < grenze);
    }

    /// <summary>Protokolleinträge (Konten, Anmeldungen, Ansichten, Downloads, Einstellungen, SchILD-Importe), die mit dieser Frist gelöscht würden.</summary>
    public static async Task<int> ProtokolleZaehlenAsync(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = DateTime.UtcNow.AddDays(-tage);
        var schild = await db.SchildImportProtokolle.AsNoTracking()
            .Select(p => new SchildProtokollKurz(p.Id, p.Zeit, p.Schuljahr, p.Abschnitt, p.IstPseudo, p.GiltAb, p.SchluesselKennung))
            .ToListAsync();
        return await db.KontoProtokolle.CountAsync(p => p.Zeit < grenze)
             + await Anmeldeprotokoll.ZaehlenAsync(db, grenze)
             + await Leseprotokoll.ZaehlenAsync(db, tage)
             + await db.DownloadProtokolle.CountAsync(p => p.Zeit < grenze)
             + await db.EinstellungsProtokolle.CountAsync(p => p.Zeit < grenze)
             + SchildZuLoeschen(schild, grenze).Count;
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
                _log.LogInformation("Tägliches Aufräumen: {Protokolle} Protokolleinträge, {Eintraege} Klassenbucheinträge, " +
                                    "Gründe in {Gruende} Vertretungen, {Konten} Konten abgegangener Schüler gelöscht.",
                    erg.Protokolle, erg.Eintraege, erg.Gruende, erg.Konten);
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
