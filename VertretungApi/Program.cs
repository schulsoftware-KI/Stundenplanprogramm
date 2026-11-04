using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();   // OpenAPI-Dokument unter /openapi/v1.json

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

// CORS für die spätere PWA (Lehrer-/Schüler-App). Für die Entwicklung offen;
// produktiv auf die konkrete App-Herkunft einschränken.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

// JWT-Authentifizierung (Phase 1C).
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
// (Änderung erst nach Neustart der API wirksam).
bool planOhneLogin = builder.Configuration.GetValue<bool>("Woche:OhneLogin");
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Rollen.PlanLesen, p => p.RequireAssertion(ctx =>
        planOhneLogin || Rollen.IstPersonal(ctx.User)));
});

builder.Services.AddSingleton<LoginBremse>();

var app = builder.Build();

// Entwicklung: Schema bei Start sicherstellen. Produktiv stattdessen Migrations
// verwenden (siehe README) – EnsureCreated und Migrations nicht mischen.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Datenbank");
    db.Database.EnsureCreated();

    // Phase 2, Schritt 5: fehlende Tabellen/Spalten in einer bestehenden Entwicklungs-DB
    // ergänzen, damit sie nicht bei jeder Schemaänderung gelöscht werden muss.
    if (app.Configuration.GetValue("Database:SchemaNachruesten", true))
    {
        try
        {
            foreach (var m in SchemaNachruestung.Ausfuehren(db))
                log.LogInformation("Schema nachgerüstet: {Meldung}", m);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Schema-Nachrüstung fehlgeschlagen. Abhilfe: vertretung.db löschen, API neu starten, Seeds erneut ausführen.");
        }
    }

    // Protokoll der Kontenverwaltung nur begrenzt aufbewahren (Datensparsamkeit).
    try
    {
        int geloescht = KontenService.ProtokollAufraeumen(db, app.Configuration.GetValue("Konten:ProtokollTage", 365));
        if (geloescht > 0) log.LogInformation("{Anzahl} alte Protokolleinträge gelöscht.", geloescht);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Kontenprotokoll konnte nicht aufgeräumt werden.");
    }

    // Phase 3.4: Löschfrist für Klassenbucheinträge – vorbereitet, Standard aus (0).
    // Die Frist legt die Schule in Phase 5 mit der/dem Datenschutzbeauftragten fest.
    try
    {
        int tage = app.Configuration.GetValue("Klassenbuch:EintraegeLoeschenNachTagen", 0);
        int geloescht = KlassenbuchEintragService.Aufraeumen(db, tage);
        if (geloescht > 0) log.LogInformation("{Anzahl} Klassenbucheinträge nach Ablauf der Frist gelöscht.", geloescht);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Alte Klassenbucheinträge konnten nicht gelöscht werden.");
    }
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

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

app.Run();
