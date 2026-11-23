using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P6a (Entscheidung 228): Anmeldeprotokoll.
//
//  Aufgezeichnet: erfolglose Anmeldungen aller Konten (Passwort bzw. Code falsch, Konto gesperrt, gebremst, Adresse
//  nicht erlaubt), erfolgreiche Anmeldungen nur von Admin-Konten und jede Anmeldung mit Wiederherstellungscode.
//  Nicht aufgezeichnet: erfolgreiche Anmeldungen von Lehrkräften und Schülern (sonst entstünde ein
//  Anwesenheitsprotokoll; es bleibt bei „letzte Anmeldung“). Unbekannte Benutzernamen ohne den eingegebenen Text –
//  dort steht oft ein vertipptes Passwort. Adresse gekürzt, „Schulnetz ja/nein“, Gerät grob (Helpers/Zugriffsschutz).
//  Kann ein Eintrag nicht geschrieben werden, geht die Anmeldung trotzdem (Warnung im Log).
//  Frist: „Aufbewahrung der Protokolle“ (Services/Aufraeumdienst.cs).
// =====================================================================

public static class Anmeldeprotokoll
{
    public static class Ergebnisse
    {
        public const string Erfolg = "Angemeldet";
        public const string ErfolgWiederherstellung = "Angemeldet mit Wiederherstellungscode";
        public const string PasswortFalsch = "Passwort falsch";
        public const string UnbekanntesKonto = "Unbekanntes Konto";
        public const string CodeFalsch = "Code falsch";
        public const string Gesperrt = "Konto gesperrt";
        public const string Gebremst = "Gebremst (zu viele Fehlversuche)";
        public const string AdresseNichtErlaubt = "Adresse nicht erlaubt";

        public static readonly string[] Alle =
            { Erfolg, ErfolgWiederherstellung, PasswortFalsch, UnbekanntesKonto, CodeFalsch, Gesperrt, Gebremst, AdresseNichtErlaubt };
    }

    public static AnmeldeProtokoll Neu(HttpContext ctx, Zugriffsschutz zugriff, Benutzer? konto, string ergebnis, bool erfolgreich)
    {
        var adresse = ctx.Connection.RemoteIpAddress;
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var geraet = Zugriffsschutz.Geraet(ua);
        return new AnmeldeProtokoll
        {
            Zeit = DateTime.UtcNow,
            KontoId = konto?.Id,
            Konto = konto?.Benutzername ?? "",
            Rolle = konto?.Rolle.ToString(),
            Ergebnis = ergebnis,
            Erfolgreich = erfolgreich,
            Adresse = Zugriffsschutz.Kuerzen(adresse),
            Schulnetz = zugriff.Schulnetz(adresse),
            Geraet = geraet != null && geraet.Length > 60 ? geraet[..60] : geraet,
        };
    }

    /// <summary>Speichert einen Eintrag für sich; ein Fehler bricht die Anmeldung nicht ab.</summary>
    public static async Task SchreibenAsync(AppDbContext db, AnmeldeProtokoll eintrag, ILogger log)
    {
        db.AnmeldeProtokolle.Add(eintrag);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            db.Entry(eintrag).State = EntityState.Detached;
            log.LogWarning(ex, "Anmeldeprotokoll: Eintrag „{Ergebnis}“ konnte nicht gespeichert werden.", eintrag.Ergebnis);
        }
    }

    /// <summary>Erfolglose Anmeldungen des Kontos seit seiner letzten Anmeldung (für den Hinweis nach der Anmeldung).</summary>
    public static async Task<(int Anzahl, DateTime? Zuletzt)> FehlversucheSeitAsync(AppDbContext db, Benutzer u, ILogger log)
    {
        try
        {
            var q = db.AnmeldeProtokolle.AsNoTracking().Where(p => p.KontoId == u.Id && !p.Erfolgreich);
            if (u.LetzteAnmeldung is DateTime seit) q = q.Where(p => p.Zeit > seit);
            int n = await q.CountAsync();
            if (n == 0) return (0, null);
            var zuletzt = await q.MaxAsync(p => (DateTime?)p.Zeit);
            return (n, zuletzt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Anmeldeprotokoll: Fehlversuche konnten nicht gezählt werden.");
            return (0, null);
        }
    }

    /// <summary>Einträge älter als die Frist löschen (0 = nicht löschen).</summary>
    public static int Aufraeumen(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = DateTime.UtcNow.AddDays(-tage);
        return db.AnmeldeProtokolle.Where(p => p.Zeit < grenze).ExecuteDelete();
    }

    public static Task<int> ZaehlenAsync(AppDbContext db, DateTime grenze)
        => db.AnmeldeProtokolle.CountAsync(p => p.Zeit < grenze);
}
