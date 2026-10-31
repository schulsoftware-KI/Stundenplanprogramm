# VertretungApi – Backend (M4, Schritt 1)

ASP.NET Core Web API für die Online-Anzeige der Vertretungspläne (Lehrer-/Schüler-App).
Der Desktop (Stundenplanprogramm) **pusht** pro Tag den effektiven Plan; die Apps **holen**
ihn per REST. DB über EF Core, Provider umschaltbar: **SQLite** (Entwicklung) /
**PostgreSQL** (produktiv).

Login/Rollen (JWT) sind seit Phase 2, Schritt 4 aktiv: Alle Plan-Abfragen brauchen eine
Anmeldung (siehe „Anmeldung & Rollen"). Web-Push folgt später.

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
- `Jwt:Key` – Signaturschlüssel der Anmelde-Tokens (mind. 32 Zeichen). **Vor produktivem
  Einsatz ändern** und nicht ins (öffentliche) Repo stellen.
- `Jwt:StundenGueltig` (12) – Gültigkeit einer Anmeldung ohne „angemeldet bleiben".
- `Jwt:TageAngemeldetBleiben` (30) – Gültigkeit mit „angemeldet bleiben".
- `Woche:OhneLogin` (`false`) – `true` macht Lehrer-/Klassen-/Kurspläne auch ohne Anmeldung
  abrufbar (z. B. für einen Aushang-Bildschirm im Schulnetz). Wirkt nach Neustart der API.

Auf **PostgreSQL** umstellen: `Database:Provider` auf `Postgres` setzen und
`ConnectionStrings:Postgres` anpassen.

## Endpunkte

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| POST | `/api/plan/push` | Desktop lädt einen Tag hoch (ersetzt ihn komplett) | `X-Api-Key` |
| GET | `/api/plan/tage` | veröffentlichte Tage | JWT Admin |
| GET | `/api/plan/tag?schuljahr=..&datum=YYYY-MM-DD` | voller Tag mit Gründen | JWT Admin |
| GET | `/api/plan/lehrer/{kuerzel}?schuljahr=..&datum=..` | Stunden einer Lehrkraft | JWT Admin oder die Lehrkraft selbst |
| GET | `/api/plan/klasse/{klasse}?schuljahr=..&datum=..` | Stunden einer Klasse (**ohne Gründe**) | PlanLesen |

Die Klassen-/Schülersicht liefert **kein** `Grund`/`Bemerkung`/`Fachgerecht`. Abwesenheits-
gründe sind Personaldaten der Lehrkräfte: Eine Lehrkraft sieht in `/api/plan/lehrer` nur die
Gründe ihrer eigenen ausgefallenen Stunden, nicht die der vertretenen Kolleginnen/Kollegen.

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

Abruf (mit Token, siehe „Anmeldung & Rollen"):
```bash
curl -H "Authorization: Bearer <token>" "http://localhost:5080/api/plan/klasse/7a?schuljahr=2026_27&datum=2026-10-15"
```

## Grundplan & Wochenpläne (Phase 2)

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| POST | `/api/grundplan/push` | Desktop lädt Grundplan-Version („gültig ab") + Kalender hoch | `X-Api-Key` |
| GET | `/api/grundplan/versionen` | vorhandene Grundplan-Versionen | PlanLesen |
| GET | `/api/grundplan/kalender?schuljahr=..` | Schuljahreskalender mit Ferien/Feiertagen | PlanLesen |
| GET | `/api/grundplan/slots?lehrer=..&gruppe=..&wtag=..` | Rohdaten einer Version (Kontrolle) | PlanLesen |
| GET | `/api/woche/ich?datum=..&wochen=..` | eigener Plan (Schülerkonto: aus den eigenen Lerngruppen; Lehrkraft: eigenes Kürzel) | JWT |
| GET | `/api/woche/lehrer/{kuerzel}?datum=YYYY-MM-DD&wochen=1` | Wochenplan einer Lehrkraft | PlanLesen |
| GET | `/api/woche/klasse/{klasse}?datum=..&wochen=..` | Wochenplan einer Klasse (auch `EF`/`Q1`/`Q2` = ganze Stufe) | PlanLesen |
| GET | `/api/woche/gruppe/{schluessel}?datum=..&wochen=..` | Wochenplan einer Lerngruppe (z. B. Kurs `BIG1_EF_L68`) | PlanLesen |
| GET | `/api/woche/schueler/{id}?datum=..&wochen=..` | individueller Plan aus Klasse/Kursen | JWT Personal |
| GET | `/api/woche/auswahl?datum=..` | Auswahllisten für die PWA (Lehrkräfte, Klassen, Stufen, Kurse, Gruppen) | PlanLesen |

Wochenplan = Grundplan-Version, die am Tag gilt (größtes „gültig ab" ≤ Datum) × Kalender
(Ferien/Feiertage; Einträge mit Klassenliste nur für diese Klassen) × veröffentlichte
Vertretungstage. `datum` fehlt = heute; geliefert wird ab Montag dieser Woche, `wochen` 1–6.

Die PWA unter `/` (Ordner `wwwroot`) zeigt diese Wochenpläne nach der Anmeldung: Personal
wählt Lehrkraft/Klasse/Kurs oder sucht eine Schülerin/einen Schüler, Lehrkräfte starten mit
dem eigenen Plan, Schülerkonten sehen nur ihren eigenen Plan. Woche vor/zurück, Änderungen
farbig markiert; auf dem Handy hochkant als Tagesliste, auf breiten Bildschirmen (oder Handy
quer) als Wochenraster. Zuletzt geladene Wochen bleiben offline lesbar (im Browser, je
Benutzer getrennt; beim Abmelden gelöscht).

## Anmeldung & Rollen (Phase 2, Schritt 4)

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| GET | `/api/auth/optionen` | braucht die App eine Anmeldung? (`anmeldungNoetig`) | – |
| POST | `/api/auth/login` | `{ benutzername, passwort, angemeldetBleiben }` → Token | – |
| GET | `/api/auth/me` | wer bin ich | JWT |
| POST | `/api/auth/ueberall-abmelden` | alle Anmeldungen des eigenen Kontos beenden | JWT |
| POST | `/api/auth/abmelden/{benutzername}` | alle Anmeldungen eines Kontos beenden (z. B. Handy verloren) | JWT Admin |
| POST | `/api/auth/seed-konten` | Testkonten anlegen bzw. Schülerkonten neu verknüpfen | `X-Api-Key` |

- **Personal** = Admin, Lehrer, Klassenlehrer, Stufenkoordination.
- **PlanLesen** = nur Personal; mit `Woche:OhneLogin: true` für alle (auch ohne Anmeldung).
  Schülerkonten bekommen dort 403 – sie sehen ausschließlich `/api/woche/ich`.
- **Widerruf:** Jedes Token trägt den Sicherheitsstempel des Kontos; die API vergleicht ihn
  bei jeder Anfrage. „Überall abmelden" setzt einen neuen Stempel → alle alten Tokens 401.
- **Login-Bremse:** ab 5 Fehlversuchen je Konto (bzw. 20 je Absender-Adresse, außer localhost)
  wachsende Wartezeit (30 s … 15 min), Antwort 429 mit `Retry-After`. Benutzername ohne
  Groß-/Kleinschreibung.
- **Sicherheitsheader:** Content-Security-Policy (nur eigene Dateien), `nosniff`,
  `no-referrer`, keine Einbettung in fremde Seiten; API-Antworten `Cache-Control: no-store`.

**Testkonten** (`seed-konten`, nur Pseudodaten): `admin/admin123`, `L1/lehrer123`,
`L2/lehrer123`, `kl05A/kl123` (Klassenleitung, Kürzel L1), `s05A/schueler123` (Pseudo-Schüler
aus 05A), `sQ1/schueler123` (Pseudo-Schüler der Q1). Schülerkonten brauchen vorher
`/api/seed/pseudodaten`; nach erneutem Seeden der Pseudodaten `seed-konten` noch einmal
aufrufen (verknüpft neu).

Login in PowerShell testen:
```powershell
$body = @{ benutzername = "L1"; passwort = "lehrer123" } | ConvertTo-Json
$t = (Invoke-RestMethod -Method Post -Uri http://localhost:5080/api/auth/login -Body $body -ContentType "application/json").token
Invoke-RestMethod -Uri http://localhost:5080/api/woche/ich -Headers @{ Authorization = "Bearer $t" }
```

**Schemaänderung (Schritt 4):** `Benutzer` hat neue Spalten (`SchuelerId`,
`SicherheitsStempel`). Eine bestehende Dev-DB vorher löschen (`vertretung.db`), API starten,
Grundplan + Vertretungstag aus dem Desktop neu veröffentlichen, dann beide Seeds ausführen.

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

## Nächste Schritte

1. **Konten verwalten:** Passwort ändern/zurücksetzen, Admin legt Konten an (später Import
   der Schülerkonten aus SchILD).
2. **Klassenbuch (Phase 3):** Anwesenheit, Entschuldigungen, Einträge, Inhalte/Hausaufgaben.
3. **Web-Push:** Benachrichtigung der Apps bei Änderung.
4. **Produktiv:** Schlüssel aus Umgebungsvariablen, EF-Migrations, PostgreSQL, CORS auf die
   App-Adresse einschränken, Login-Bremse hinter dem Reverse-Proxy je echter Adresse.
