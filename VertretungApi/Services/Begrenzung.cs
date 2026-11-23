using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using VertretungApi.Helpers;

namespace VertretungApi.Services;

// =====================================================================
//  P6a (Entscheidung 230): Anfragebegrenzung – nur für /api (App-Dateien bleiben unbegrenzt: morgens öffnen viele
//  Geräte die App über eine Schuladresse). Werte in der Einstellungsdatei („Begrenzung“, Änderung nach Neustart):
//
//    angemeldet (JWT):              je Konto       JeKontoJeMinute        (Standard 300)
//    Desktop mit gültigem Schlüssel: je Adresse    DesktopJeMinute        (Standard 600 – „Zeitraum veröffentlichen…“)
//    ohne Anmeldung:                 je Adresse    OhneAnmeldungJeMinute  (Standard 120; Schuladressen ausgenommen)
//    teure Abrufe (PDF, CSV, Archiv, SchILD-Import, Rückweg; P6b: Auskunft):
//                                    höchstens TeuerJeKonto (2) gleichzeitig je Konto bzw. Adresse und TeuerGesamt (4)
//                                    insgesamt; weitere warten bis zu TeuerWartenSekunden (10), sonst 429
//    Anfragen ohne Anmeldung:        höchstens OhneAnmeldungMaxKB (64) KB Inhalt (Desktop-Schnittstellen ausgenommen)
//
//  0 = keine Begrenzung (nur zum Testen). Antwort 429 mit Retry-After und „Zu viele Anfragen – bitte in n Sekunden
//  erneut versuchen.“; im Log eine Warnung je Konto bzw. Adresse und 10 Minuten, ohne Inhalte.
//  Token-Bucket je Minute: Der volle Wert steht sofort bereit, nachgefüllt wird alle 10 Sekunden ein Sechstel.
// =====================================================================

public sealed class BegrenzungsWerte
{
    public int JeKontoJeMinute { get; init; } = 300;
    public int OhneAnmeldungJeMinute { get; init; } = 120;
    public int DesktopJeMinute { get; init; } = 600;
    public int TeuerJeKonto { get; init; } = 2;
    public int TeuerGesamt { get; init; } = 4;
    public int TeuerWartenSekunden { get; init; } = 10;
    public int OhneAnmeldungMaxKB { get; init; } = 64;

    public long OhneAnmeldungMaxBytes => OhneAnmeldungMaxKB * 1024L;

    /// <summary>Liest „Begrenzung:…“; ungültige Werte (keine ganze Zahl, negativ) landen in <paramref name="fehler"/>.</summary>
    public static BegrenzungsWerte Lesen(IConfiguration cfg, List<string>? fehler = null)
    {
        var std = new BegrenzungsWerte();
        int Z(string name, int standard, int max)
        {
            var text = cfg["Begrenzung:" + name];
            if (string.IsNullOrWhiteSpace(text)) return standard;
            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z) && z >= 0 && z <= max)
                return z;
            fehler?.Add($"Begrenzung:{name} ist „{text}“ – erlaubt ist eine ganze Zahl von 0 bis {max} (0 = keine Begrenzung).");
            return standard;
        }
        return new BegrenzungsWerte
        {
            JeKontoJeMinute = Z(nameof(JeKontoJeMinute), std.JeKontoJeMinute, 100_000),
            OhneAnmeldungJeMinute = Z(nameof(OhneAnmeldungJeMinute), std.OhneAnmeldungJeMinute, 100_000),
            DesktopJeMinute = Z(nameof(DesktopJeMinute), std.DesktopJeMinute, 100_000),
            TeuerJeKonto = Z(nameof(TeuerJeKonto), std.TeuerJeKonto, 100),
            TeuerGesamt = Z(nameof(TeuerGesamt), std.TeuerGesamt, 1000),
            TeuerWartenSekunden = Z(nameof(TeuerWartenSekunden), std.TeuerWartenSekunden, 300),
            OhneAnmeldungMaxKB = Z(nameof(OhneAnmeldungMaxKB), std.OhneAnmeldungMaxKB, 100_000),
        };
    }

    public string Text() =>
        $"je Konto {Wert(JeKontoJeMinute)}/min, ohne Anmeldung {Wert(OhneAnmeldungJeMinute)}/min je Adresse, " +
        $"Desktop {Wert(DesktopJeMinute)}/min, teure Abrufe {Wert(TeuerJeKonto)} je Konto und {Wert(TeuerGesamt)} insgesamt";

    private static string Wert(int z) => z == 0 ? "unbegrenzt" : z.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Markiert teure Abrufe (PDF, CSV, Archiv, SchILD-Import …): gleichzeitig nur begrenzt viele.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class TeurerAbrufAttribute : Attribute
{
}

public static class Begrenzung
{
    public static string Text(int sekunden) => $"Zu viele Anfragen – bitte in {sekunden} Sekunden erneut versuchen.";

    /// <summary>Anfragebegrenzung von ASP.NET Core einrichten (Program.cs; app.UseRateLimiter nach UseAuthentication).</summary>
    public static IServiceCollection AddBegrenzung(this IServiceCollection dienste, BegrenzungsWerte w)
    {
        dienste.AddSingleton(w);
        dienste.AddSingleton<BegrenzungsMelder>();
        dienste.AddSingleton<TeureAbrufe>();
        dienste.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => Partition(ctx, w));
            o.OnRejected = async (kontext, abbruch) =>
            {
                int sek = 10;
                if (kontext.Lease.TryGetMetadata(MetadataName.RetryAfter, out var nach))
                    sek = Math.Max(1, (int)Math.Ceiling(nach.TotalSeconds));
                await AbweisenAsync(kontext.HttpContext, sek, "Anfragebegrenzung", abbruch);
            };
        });
        return dienste;
    }

    /// <summary>429 mit Retry-After und Text; eine Warnung je Konto bzw. Adresse und 10 Minuten im Log.</summary>
    public static async Task AbweisenAsync(HttpContext ctx, int sekunden, string art, CancellationToken abbruch)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = sekunden.ToString(CultureInfo.InvariantCulture);
        ctx.RequestServices.GetRequiredService<BegrenzungsMelder>().Melden(art, Wer(ctx));
        await ctx.Response.WriteAsJsonAsync(new { fehler = Text(sekunden), sekunden }, abbruch);
    }

    /// <summary>Wer gezählt wird – für Partition und Log (Adresse gekürzt, Konto als Id).</summary>
    public static string Wer(HttpContext ctx)
    {
        if (Rollen.BenutzerId(ctx.User) is int id && ctx.User.Identity?.IsAuthenticated == true)
            return "Konto " + id.ToString(CultureInfo.InvariantCulture);
        string adr = Zugriffsschutz.Kuerzen(ctx.Connection.RemoteIpAddress);
        return (ctx.Request.Headers.ContainsKey(ApiSchluessel.Header) ? "Desktop " : "Adresse ") + adr;
    }

    private static RateLimitPartition<string> Partition(HttpContext ctx, BegrenzungsWerte w)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api")) return RateLimitPartition.GetNoLimiter("frei");

        var adresse = ctx.Connection.RemoteIpAddress;
        string adr = adresse == null ? "?" : Zugriffsschutz.Normal(adresse).ToString();
        var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();

        if (ctx.Request.Headers.ContainsKey(ApiSchluessel.Header) && ApiSchluessel.Gueltig(ctx.Request, cfg))
            return Eimer("desktop|" + adr, w.DesktopJeMinute);
        if (ctx.User.Identity?.IsAuthenticated == true && Rollen.BenutzerId(ctx.User) is int id)
            return Eimer("konto|" + id.ToString(CultureInfo.InvariantCulture), w.JeKontoJeMinute);
        if (ctx.RequestServices.GetRequiredService<Zugriffsschutz>().IstSchule(adresse))
            return RateLimitPartition.GetNoLimiter("schule");
        return Eimer("adresse|" + adr, w.OhneAnmeldungJeMinute);
    }

    private static RateLimitPartition<string> Eimer(string schluessel, int jeMinute)
    {
        if (jeMinute <= 0) return RateLimitPartition.GetNoLimiter("aus");
        return RateLimitPartition.GetTokenBucketLimiter(schluessel, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = jeMinute,
            TokensPerPeriod = Math.Max(1, jeMinute / 6),
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }
}

/// <summary>Höchstens eine Warnung je Art und Konto bzw. Adresse in 10 Minuten (ohne Inhalte).</summary>
public sealed class BegrenzungsMelder
{
    private static readonly TimeSpan Abstand = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, DateTime> _zuletzt = new();
    private readonly ILogger<BegrenzungsMelder> _log;

    public BegrenzungsMelder(ILogger<BegrenzungsMelder> log) => _log = log;

    public void Melden(string art, string wer)
    {
        var jetzt = DateTime.UtcNow;
        var schluessel = art + "|" + wer;
        if (_zuletzt.TryGetValue(schluessel, out var alt) && jetzt - alt < Abstand) return;
        _zuletzt[schluessel] = jetzt;
        if (_zuletzt.Count > 5000)
            foreach (var (k, v) in _zuletzt)
                if (jetzt - v > Abstand) _zuletzt.TryRemove(k, out _);
        _log.LogWarning("{Art}: {Wer} hat die Grenze erreicht (HTTP 429). Weitere Meldungen dazu erst in 10 Minuten.", art, wer);
    }
}

/// <summary>
/// Teure Abrufe: höchstens TeuerJeKonto gleichzeitig je Konto (bzw. Adresse) und TeuerGesamt insgesamt; weitere warten
/// bis zu TeuerWartenSekunden. Nur im Arbeitsspeicher.
/// </summary>
public sealed class TeureAbrufe
{
    private sealed class Platz
    {
        public required SemaphoreSlim Sem { get; init; }
        public int Nutzer;
    }

    private readonly bool _aus;
    private readonly int _jeKonto;
    private readonly TimeSpan _warten;
    private readonly SemaphoreSlim _gesamt;
    private readonly Dictionary<string, Platz> _plaetze = new(StringComparer.Ordinal);

    public TeureAbrufe(BegrenzungsWerte w)
    {
        _aus = w.TeuerGesamt <= 0 && w.TeuerJeKonto <= 0;
        _jeKonto = w.TeuerJeKonto <= 0 ? 1_000 : w.TeuerJeKonto;
        int gesamt = w.TeuerGesamt <= 0 ? 10_000 : w.TeuerGesamt;
        _gesamt = new SemaphoreSlim(gesamt, gesamt);
        _warten = TimeSpan.FromSeconds(w.TeuerWartenSekunden);
    }

    public int WartenSekunden => Math.Max(1, (int)_warten.TotalSeconds);

    /// <summary>Platz belegen (Dispose gibt ihn frei) – null, wenn in der Wartezeit keiner frei wurde.</summary>
    public async Task<IDisposable?> BetretenAsync(string wer, CancellationToken abbruch)
    {
        if (_aus) return new Freigabe(null, null, null);
        Platz p;
        lock (_plaetze)
        {
            if (!_plaetze.TryGetValue(wer, out var vorhanden))
            {
                vorhanden = new Platz { Sem = new SemaphoreSlim(_jeKonto, _jeKonto) };
                _plaetze[wer] = vorhanden;
            }
            p = vorhanden;
            p.Nutzer++;
        }

        long start = Stopwatch.GetTimestamp();
        bool eigen = false, gesamt = false;
        try
        {
            eigen = await p.Sem.WaitAsync(_warten, abbruch);
            if (!eigen) return null;
            var rest = _warten - Stopwatch.GetElapsedTime(start);
            gesamt = await _gesamt.WaitAsync(rest > TimeSpan.Zero ? rest : TimeSpan.Zero, abbruch);
            if (!gesamt) return null;
            return new Freigabe(this, wer, p);
        }
        finally
        {
            if (!gesamt)
            {
                if (eigen) p.Sem.Release();
                Verlassen(wer, p);
            }
        }
    }

    private void Verlassen(string wer, Platz p)
    {
        lock (_plaetze)
        {
            if (--p.Nutzer == 0) _plaetze.Remove(wer);
        }
    }

    private sealed class Freigabe : IDisposable
    {
        private TeureAbrufe? _von;
        private readonly string? _wer;
        private readonly Platz? _platz;

        public Freigabe(TeureAbrufe? von, string? wer, Platz? platz)
        {
            _von = von;
            _wer = wer;
            _platz = platz;
        }

        public void Dispose()
        {
            var von = Interlocked.Exchange(ref _von, null);
            if (von == null || _platz == null || _wer == null) return;
            von._gesamt.Release();
            _platz.Sem.Release();
            von.Verlassen(_wer, _platz);
        }
    }
}
