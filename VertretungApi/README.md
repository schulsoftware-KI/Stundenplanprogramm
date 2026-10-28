# VertretungApi – Backend (M4, Schritt 1)

ASP.NET Core Web API für die Online-Anzeige der Vertretungspläne (Lehrer-/Schüler-App).
Der Desktop (Stundenplanprogramm) **pusht** pro Tag den effektiven Plan; die Apps **holen**
ihn per REST. DB über EF Core, Provider umschaltbar: **SQLite** (Entwicklung) /
**PostgreSQL** (produktiv).

Dieser erste Schritt enthält Datenmodell + Persistenz + die Kern-Endpunkte. Login/Rollen
(JWT) und Web-Push folgen in den nächsten Schritten.

## Projekt einrichten

Eigenes Projekt, **getrennt** von der WPF-App. Entweder zur bestehenden Solution hinzufügen
(Rechtsklick auf die Projektmappe → Hinzufügen → Vorhandenes Projekt → `VertretungApi.csproj`)
oder separat öffnen.

Pakete werden beim ersten Build von NuGet geholt. Schlägt NuGet andere, zu .NET 10 passende
Versionen vor, diese übernehmen.

## Starten (Entwicklung, SQLite)

```
cd VertretungApi
dotnet run
```

Beim Start wird die SQLite-Datei `vertretung.db` angelegt (via `EnsureCreated`).
API dann unter `http://localhost:5080`, OpenAPI-Doc unter `http://localhost:5080/openapi/v1.json`.

## Konfiguration

`appsettings.json`:
- `Database:Provider` = `Sqlite` oder `Postgres`
- `ConnectionStrings:Sqlite` / `ConnectionStrings:Postgres`
- `Api:PushKey` – Schlüssel, den der Desktop beim Push im Header `X-Api-Key` mitschickt.
  **Vor produktivem Einsatz ändern.**

Auf **PostgreSQL** umstellen: `Database:Provider` auf `Postgres` setzen und
`ConnectionStrings:Postgres` anpassen.

## Endpunkte

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| POST | `/api/plan/push` | Desktop lädt einen Tag hoch (ersetzt ihn komplett) | `X-Api-Key` |
| GET | `/api/plan/tag?schuljahr=..&datum=YYYY-MM-DD` | voller Tag (Admin/Aushang) | – |
| GET | `/api/plan/lehrer/{kuerzel}?schuljahr=..&datum=..` | Stunden einer Lehrkraft (voll) | – |
| GET | `/api/plan/klasse/{klasse}?schuljahr=..&datum=..` | Stunden einer Klasse (**ohne Gründe**) | – |

Die Klassen-/Schülersicht liefert **kein** `Grund`/`Bemerkung`/`Fachgerecht`.

## Push testen (curl)

```bash
curl -X POST http://localhost:5080/api/plan/push \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: CHANGE-ME-dev-key" \
  -d '{
    "schuljahr": "2026_27",
    "datum": "2026-10-15",
    "eintraege": [
      {"stunde":1,"klassen":"7a","fach":"D","lehrer":"L50","originalLehrer":"L15",
       "raum":"R101","art":"Vertretung","status":"Vertreten","grund":"Krankheit",
       "bemerkung":null,"fachgerecht":true,"istAenderung":true},
      {"stunde":3,"klassen":"7a","fach":"M","lehrer":null,"originalLehrer":"L15",
       "raum":null,"art":"Entfall","status":"Entfall","grund":"Krankheit",
       "bemerkung":null,"fachgerecht":null,"istAenderung":true}
    ]
  }'
```

Abruf:
```bash
curl "http://localhost:5080/api/plan/klasse/7a?schuljahr=2026_27&datum=2026-10-15"
curl "http://localhost:5080/api/plan/lehrer/L50?schuljahr=2026_27&datum=2026-10-15"
```

## Grundplan & Wochenpläne (Phase 2)

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| POST | `/api/grundplan/push` | Desktop lädt Grundplan-Version („gültig ab") + Kalender hoch | `X-Api-Key` |
| GET | `/api/grundplan/versionen` | vorhandene Grundplan-Versionen | – |
| GET | `/api/grundplan/kalender?schuljahr=..` | Schuljahreskalender mit Ferien/Feiertagen | – |
| GET | `/api/grundplan/slots?lehrer=..&gruppe=..&wtag=..` | Rohdaten einer Version (Kontrolle) | – |
| GET | `/api/woche/lehrer/{kuerzel}?datum=YYYY-MM-DD&wochen=1` | Wochenplan einer Lehrkraft | – |
| GET | `/api/woche/klasse/{klasse}?datum=..&wochen=..` | Wochenplan einer Klasse (auch `EF`/`Q1`/`Q2` = ganze Stufe) | – |
| GET | `/api/woche/gruppe/{schluessel}?datum=..&wochen=..` | Wochenplan einer Lerngruppe (z. B. Kurs `BIG1_EF_L68`) | – |
| GET | `/api/woche/schueler/{id}?datum=..&wochen=..` | individueller Plan aus Klasse/Kursen | JWT (Personal) |

Wochenplan = Grundplan-Version, die am Tag gilt (größtes „gültig ab" ≤ Datum) × Kalender
(Ferien/Feiertage; Einträge mit Klassenliste nur für diese Klassen) × veröffentlichte
Vertretungstage. `datum` fehlt = heute; geliefert wird ab Montag dieser Woche, `wochen` 1–6.

`POST /api/seed/pseudodaten` erzeugt die Pseudo-Klassen/-Kurse aus dem zuletzt gepushten
Grundplan (Schlüssel passen dann zu den Wochenplänen). Ohne Grundplan bzw. mit
`?quelle=synthetisch` entstehen frei erfundene Daten wie in Phase 1B.

## Produktiv: Migrations statt EnsureCreated

`EnsureCreated` ist nur für die Entwicklung gedacht und verträgt sich **nicht** mit Migrations.
Für den Schulserver (PostgreSQL) stattdessen EF-Core-Migrations verwenden:

```
dotnet tool install --global dotnet-ef
dotnet ef migrations add Init
dotnet ef database update
```

Dann in `Program.cs` den `EnsureCreated`-Block entfernen bzw. durch `db.Database.Migrate()`
ersetzen.

## Nächste Schritte (M4)

1. **Desktop-Push-Anbindung:** im Stundenplanprogramm einen `OnlineSync`-Client (HttpClient),
   der aus `TagesplanBerechnung` den effektiven Tag in `PlanPushDto` übersetzt und beim
   Speichern einer Buchung automatisch pusht (Button/Checkbox „online veröffentlichen").
2. **Auth:** Benutzer-Login (JWT), Rollen Admin/Lehrer/Schüler; GETs rollenabhängig schützen.
3. **Web-Push:** Benachrichtigung der Apps bei Änderung.
4. **Tunnel-Test:** API lokal + ngrok/Cloudflare, Handy-App dagegen testen.
5. **PWA** (M5 Lehrer, M6 Schüler).
