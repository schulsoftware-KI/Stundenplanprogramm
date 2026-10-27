using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VertretungApi.Data;

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
    });
builder.Services.AddAuthorization();

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

// Statische PWA aus wwwroot ausliefern (index.html unter "/").
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
