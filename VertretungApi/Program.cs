using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Services;

// ---------------------------------------------------------------------------
//  Phase 5.1: Befehl statt Serverstart.
//    admin-anlegen [benutzername] – Admin-Konto anlegen bzw. Passwort zurücksetzen
//    (Services/Notfallzugang.cs). Alle übrigen Argumente gehen wie gewohnt an ASP.NET Core.
// ---------------------------------------------------------------------------
string? befehl = null, befehlWert = null;
var hostArgs = args;
if (args.Length > 0 && args[0].Equals(Notfallzugang.Befehl, StringComparison.OrdinalIgnoreCase))
{
    befehl = Notfallzugang.Befehl;
    befehlWert = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
    hostArgs = args.Skip(befehlWert == null ? 1 : 2).ToArray();
}

var builder = WebApplication.CreateBuilder(hostArgs);

// Phase 5.1: Echtbetrieb = jede Umgebung außer Development. Ungeeignete Einstellungen
// (Schlüssel aus dem Repo, Woche:OhneLogin, Standardpasswort der Datenbank …) beenden
// den Start mit einer Meldung, statt unsicher zu laufen. Der Befehl admin-anlegen braucht
// nur die Datenbank und wird deshalb nicht geprüft.
bool produktiv = Betrieb.Produktiv(builder.Environment);
var pruefung = Betrieb.Pruefen(builder.Configuration, produktiv);
if (befehl == null && pruefung.Fehler.Count > 0)
{
    Betrieb.FehlerAusgeben(pruefung.Fehler);
    return 1;
}

builder.Services.AddControllers();
builder.Services.AddOpenApi();   // OpenAPI-Dokument unter /openapi/v1.json (nur Entwicklung)

// ---------------------------------------------------------------------------
//  EF Core mit umschaltbarem Provider (SQLite dev / PostgreSQL prod).
//  Gesteuert über Konfiguration:
//    "Database:Provider"            = "Sqlite" | "Postgres"
//    ConnectionStrings:<Provider>   = Verbindungszeichenfolge
// ---------------------------------------------------------------------------
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var conn = builder.Configuration.GetConnectionString(provider);

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        opt.UseNpgsql(conn ?? throw new InvalidOperationException(
            "ConnectionStrings:Postgres fehlt."));
    else
        opt.UseSqlite(conn ?? "Data Source=vertretung.db");
});

// CORS: Die PWA kommt vom selben Server und braucht kein CORS (auch der Desktop nicht – er
// ist kein Browser). In der Entwicklung offen wie bisher; im Echtbetrieb nur für die unter
// "Cors:Herkuenfte" eingetragenen Adressen (Standard: keine).
var corsHerkuenfte = builder.Configuration.GetSection("Cors:Herkuenfte").Get<string[]>() ?? Array.Empty<string>();
bool corsAktiv = !produktiv || corsHerkuenfte.Length > 0;
if (corsAktiv)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    {
        if (produktiv) p.WithOrigins(corsHerkuenfte).AllowAnyHeader().AllowAnyMethod();
        else p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
    }));

// Phase 5.1: hinter einem Reverse Proxy (nginx, IIS, Caddy …) die echte Absenderadresse und
// das Schema (https) aus X-Forwarded-For/-Proto übernehmen – nur von bekannten Proxys.
// Ohne das sähe die Login-Bremse alle Anfragen mit der Adresse des Proxys.
bool proxyAktiv = builder.Configuration.GetValue("Proxy:Aktiv", false);
if (proxyAktiv)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        foreach (var ip in Betrieb.BekannteProxys(builder.Configuration, out _))
        {
            o.KnownProxies.Add(ip);
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                o.KnownProxies.Add(ip.MapToIPv6());   // Kestrel meldet IPv4 teils als ::ffff:a.b.c.d
        }
    });

// JWT-Authentifizierung (Phase 1C). Im Echtbetrieb ist Jwt:Key durch die Startprüfung gesetzt.
var jwtKey = builder.Configuration["Jwt:Key"] ?? "dev-geheimschluessel-bitte-aendern-min-32-zeichen!";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true
        };

        // Phase 2, Schritt 4: Token nur gültig, solange das Konto existiert und sein
        // Sicherheitsstempel noch derselbe ist („auf allen Geräten abmelden" ändert ihn).
        // Schritt 5: außerdem nicht gesperrt; „muss Passwort ändern" wird als Claim
        // mitgegeben (Sperre siehe Middleware unten).
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var principal = ctx.Principal;
                var id = principal == null ? null : Rollen.BenutzerId(principal);
                var stempel = principal?.FindFirst(Rollen.StempelClaim)?.Value;
                if (id == null || string.IsNullOrEmpty(stempel))
                {
                    ctx.Fail("Anmeldung ungültig.");
                    return;
                }
                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var aktuell = await db.Benutzer.AsNoTracking()
                    .Where(b => b.Id == id.Value)
                    .Select(b => new { b.SicherheitsStempel, b.Gesperrt, b.MussPasswortAendern })
                    .FirstOrDefaultAsync();
                if (aktuell == null || aktuell.SicherheitsStempel != stempel || aktuell.Gesperrt)
                {
                    ctx.Fail("Anmeldung wurde beendet.");
                    return;
                }
                if (aktuell.MussPasswortAendern && principal!.Identity is ClaimsIdentity ident)
                    ident.AddClaim(new Claim(Rollen.PasswortPflichtClaim, "1"));
            }
        };
    });

// Zugriff auf Pläne (Phase 2, Schritt 4): nur Personal. Mit "Woche:OhneLogin": true in
// appsettings.json sind Lehrer-/Klassen-/Kurspläne auch ohne Anmeldung abrufbar
// (Änderung erst nach Neustart der API; im Echtbetrieb verweigert die Startprüfung das).
bool planOhneLogin = builder.Configuration.GetValue<bool>("Woche:OhneLogin");
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Rollen.PlanLesen, p => p.RequireAssertion(ctx =>
        planOhneLogin || Rollen.IstPersonal(ctx.User)));
});

builder.Services.AddSingleton<LoginBremse>();

var app = builder.Build();

// Datenbank anlegen bzw. auf den Stand des Programms bringen, Protokolle aufräumen
// (Data/DatenbankStart.cs).
if (!DatenbankStart.Ausfuehren(app, produktiv))
    return 1;

if (befehl == Notfallzugang.Befehl)
    return await Notfallzugang.AusfuehrenAsync(app.Services, befehlWert);

bool httpsErzwingen = app.Configuration.GetValue("Betrieb:HttpsErzwingen", true);
app.Logger.LogInformation(
    "Betrieb: {Modus} · Datenbank {Provider} · Proxy {Proxy} · HTTPS erzwingen {Https}",
    produktiv ? $"Echtbetrieb ({app.Environment.EnvironmentName})" : "Entwicklung",
    provider, proxyAktiv ? "an" : "aus", produktiv && httpsErzwingen ? "an" : "aus");
foreach (var hinweis in pruefung.Hinweise)
    app.Logger.LogWarning("Einstellungen: {Hinweis}", hinweis);

// Phase 5.1: zuerst die Angaben des Proxys übernehmen (Absenderadresse, https).
if (proxyAktiv)
    app.UseForwardedHeaders();

// Phase 5.1: Kommt eine Anfrage mit X-Forwarded-For an, die nicht ausgewertet wurde, steht
// die API hinter einem Proxy, der nicht eingetragen ist – dann bremst die Login-Bremse alle
// gemeinsam. Einmal im Log melden (ohne Adressen). Anfragen von localhost (z. B. der
// Cloudflare-Testtunnel) sind ausgenommen; die Login-Bremse zählt sie ohnehin nicht.
int proxyWarnung = 0;
app.Use(async (ctx, next) =>
{
    if (proxyWarnung == 0
        && ctx.Request.Headers.ContainsKey("X-Forwarded-For")
        && ctx.Connection.RemoteIpAddress is IPAddress absender
        && !IPAddress.IsLoopback(absender.IsIPv4MappedToIPv6 ? absender.MapToIPv4() : absender)
        && Interlocked.Exchange(ref proxyWarnung, 1) == 0)
    {
        app.Logger.LogWarning(proxyAktiv
            ? "Anfrage über einen Proxy, der nicht in Proxy:BekannteProxys steht – seine Angaben werden ignoriert. Adresse des Proxys eintragen (README, Produktionsmodus)."
            : "Anfrage über einen Proxy, aber Proxy:Aktiv ist aus – alle Anfragen erscheinen mit der Adresse des Proxys. Proxy:Aktiv und Proxy:BekannteProxys setzen (README, Produktionsmodus).");
    }
    await next();
});

if (!produktiv)
    app.MapOpenApi();

// Phase 5.1: Im Echtbetrieb nur verschlüsselt. HSTS sagt dem Browser „künftig nur https“
// (nicht für localhost); die Umleitung greift, sobald ein https-Port bekannt ist (eigener
// Endpunkt in Kestrel bzw. X-Forwarded-Proto vom Proxy).
if (produktiv && httpsErzwingen)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (corsAktiv)
    app.UseCors();

// Sicherheitsheader (Phase 2, Schritt 4). Die PWA lädt nur eigene Dateien (keine
// fremden Skripte/Schriften); API-Antworten mit Personendaten werden nicht im
// Browser-/Proxy-Cache abgelegt.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api"))
        h["Cache-Control"] = "no-store";
    await next();
});

// Statische PWA aus wwwroot ausliefern (index.html unter "/").
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();

// Phase 2, Schritt 5: Mit Startpasswort bzw. zurückgesetztem Passwort ist nur das
// Ändern des Passworts erlaubt (plus Anmelden/Abmelden). Alles andere: 403 mit Hinweis.
app.Use(async (ctx, next) =>
{
    if (ctx.User.HasClaim(Rollen.PasswortPflichtClaim, "1")
        && ctx.Request.Path.StartsWithSegments("/api")
        && !Rollen.ErlaubtBeiPasswortPflicht(ctx.Request.Path))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new
        {
            fehler = "Bitte zuerst ein eigenes Passwort festlegen.",
            passwortAendern = true
        });
        return;
    }
    await next();
});

app.UseAuthorization();

app.MapControllers();

// Phase 5.1: Lebenszeichen für Überwachung/Proxy – ohne Anmeldung, ohne Daten.
app.MapGet("/api/gesundheit", async (AppDbContext db) =>
{
    bool ok;
    try { ok = await db.Database.CanConnectAsync(); }
    catch { ok = false; }
    return ok
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "Datenbank nicht erreichbar" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.Run();
return 0;
