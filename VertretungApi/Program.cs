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
                    .Select(b => b.SicherheitsStempel)
                    .FirstOrDefaultAsync();
                if (aktuell == null || aktuell != stempel)
                    ctx.Fail("Anmeldung wurde beendet.");
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
    db.Database.EnsureCreated();
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
app.UseAuthorization();

app.MapControllers();

app.Run();
