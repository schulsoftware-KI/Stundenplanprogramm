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

Beim Start wird die SQLite-Datei `vertretung.db` angelegt (via `EnsureCreated`). Seit Phase 2,
Schritt 5 ergänzt die API beim Start außerdem **fehlende Tabellen und Spalten** in einer schon
vorhandenen Dev-DB (Schema-Nachrüstung, siehe Log „Schema nachgerüstet: …“). Die DB muss bei
neuen Feldern also nicht mehr gelöscht werden. Nur bei Typänderungen, Umbenennungen oder neuen
Indizes auf alten Tabellen hilft weiterhin: `vertretung.db` löschen und neu seeden.
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
- `Database:SchemaNachruesten` (`true`) – fehlende Tabellen/Spalten beim Start ergänzen (nur
  Entwicklung; produktiv mit EF-Migrations auf `false`).
- `Konten:ProtokollTage` (365) – so lange bleiben Einträge im Kontenprotokoll; ältere werden
  beim Start gelöscht.

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
| POST | `/api/auth/passwort` | eigenes Passwort ändern `{ altesPasswort, neuesPasswort, angemeldetBleiben }` → neues Token | JWT |
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
`L2/lehrer123`, `kl05A/kl123` (Klassenleitung 05A, Kürzel L1), `koQ1/ko123`
(Stufenkoordination Q1, Kürzel L2, ab Schritt 5), `s05A/schueler123` (Pseudo-Schüler
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

## Konten verwalten (Phase 2, Schritt 5)

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| GET | `/api/konten/rechte` | was darf ich in der Kontenverwaltung? | KontenSehen |
| GET | `/api/konten?q=&rolle=&filter=&limit=` | Kontenliste (`rolle`: Admin, Lehrer, Klassenlehrer, Stufenkoordination, Schueler, Personal; `filter`: pflicht, nie, gesperrt, verwaist) | KontenSehen |
| GET | `/api/konten/{id}` | ein Konto | KontenSehen |
| POST | `/api/konten/{id}/passwort` | Passwort zurücksetzen → `{ konto, startPasswort }` | KontenSehen |
| POST | `/api/konten/{id}/sperren` · `/entsperren` | Konto sperren / entsperren | KontenSehen |
| POST | `/api/konten` | Konto anlegen `{ rolle, benutzername?, lehrerKuerzel?, schuelerId?, zustaendig? }` → `{ konto, startPasswort }` | Admin |
| PUT | `/api/konten/{id}` | Benutzername, Rolle, Kürzel, Zuständigkeit, Schüler ändern | Admin |
| POST | `/api/konten/{id}/abmelden` | alle Anmeldungen des Kontos beenden | Admin |
| DELETE | `/api/konten/{id}` | Konto löschen | Admin |
| GET | `/api/konten/protokoll?konto=&limit=` | Kontenprotokoll (neueste zuerst) | Admin |
| GET | `/api/konten/sammel/uebersicht` | Lehrkräfte und Klassen/Stufen mit Anzahl vorhandener Konten | Admin |
| POST | `/api/konten/sammel` | Sammelanlage `{ art: "Lehrer"\|"Schueler", gruppen: ["05A","Q1"], nur?: [...], anlegen }` | Admin |

- **KontenSehen** = Admin (alle Konten) sowie Klassenleitung/Stufenkoordination: nur die
  Schülerkonten ihrer Klasse(n) bzw. Stufe(n) (Feld `Zustaendig`, z. B. „05A“ oder „Q1“) –
  sehen, Passwort zurücksetzen, sperren/entsperren, sonst nichts.
- **Startpasswort:** Anlegen und Zurücksetzen erzeugen ein lesbares Einmalpasswort
  (z. B. `kamo-tefi-37`). Es steht nur in dieser einen Antwort und wird nie im Klartext
  gespeichert. Das Konto ist dann auf „muss Passwort ändern“ gesetzt: Nach der Anmeldung
  antwortet die API auf alles außer `/api/auth/passwort|me|optionen|login|ueberall-abmelden`
  mit **403** `{ passwortAendern: true }`.
- **Passwortregeln** (`Helpers/PasswortRegeln.cs`): Personal mindestens 10, Schüler mindestens
  8 Zeichen; nicht Benutzername/Name, nicht das bisherige, keine bekannten Passwörter, keine
  Wiederholungen oder Tastenfolgen. Keine Pflicht-Sonderzeichen (BSI/NIST).
- **Benutzernamen:** Lehrkräfte = Kürzel, Schüler = `vorname.nachname` (Umlaute ausgeschrieben,
  bei Doppelung mit Zahl, z. B. `mia.mueller2`). Ohne Groß-/Kleinschreibung eindeutig.
- **Sammelanlage:** Lehrkräfte aus dem gültigen Grundplan, Schüler je Klasse/Stufe. Erst
  Vorschau (`anlegen: false`), dann Anlegen (optional nur die Schlüssel in `nur`). Vorhandene
  Konten werden übersprungen, Kürzel mit Sonderzeichen als „konflikt“ gemeldet. Höchstens
  2000 Konten je Aufruf; die Passwort-Hashes werden parallel berechnet.
- **Schutzregeln:** letztes aktives Admin-Konto nicht sperr-/lösch-/herabstufbar; das eigene
  Konto wird hier nicht gesperrt, gelöscht oder zurückgesetzt. Jede Änderung beendet die
  Anmeldungen des Kontos (neuer Sicherheitsstempel). Gesperrte Konten: Login 403.
- **Zeiten** stehen in der DB als UTC und werden beim Lesen als UTC gekennzeichnet (JSON mit „Z“),
  die App zeigt sie in Ortszeit.
- **Protokoll** (`KontoProtokoll`): angelegt, geändert, Passwort geändert/zurückgesetzt,
  gesperrt, entsperrt, abgemeldet, gelöscht – mit Akteur und Zeit, nie mit Passwort.
- **Pseudodaten neu erzeugen** löscht Schülerkonten, die auf Pseudo-Schüler zeigen (die
  Testkonten `s05A`/`sQ1` werden nur gelöst und von `seed-konten` neu verknüpft). Konten
  echter Schüler bleiben unberührt.

In der PWA: Kontomenü → „Passwort ändern“ (alle) bzw. „Konten verwalten“ (Admin) /
„Schülerpasswörter“ (Klassenleitung, Stufenkoordination). Startpasswörter erscheinen als
Zugangszettel zum Drucken oder als CSV-Datei (Excel, Semikolon) und nur dieses eine Mal.

## Klassenbuch (Phase 3.1): Anwesenheit und Gruppenlisten

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| GET | `/api/klassenbuch/stunde?datum=&stunde=&unr=&lehrer=` | Stundenblatt: Kopf, Rechte, Erfassungsstand, Teilnehmer mit Status (`unr`/`lehrer` = `grundUNr`/`grundLehrer` aus dem Wochenplan) | Personal + Recht |
| PUT | `/api/klassenbuch/stunde` | Anwesenheit speichern `{ datum, stunde, grundUNr, grundLehrer, version, eintraege: [{ schuelerId, status, minuten? }] }` → Stundenblatt | Personal + Recht |
| GET | `/api/klassenbuch/stunde/historie?…` | Änderungshistorie der Stunde (neueste zuerst) | Personal + Recht |
| GET | `/api/klassenbuch/gruppen` | „Meine Gruppen“: Sek-I-Gruppen der Lehrkraft (Admin: alle), offene zuerst | Personal |
| GET | `/api/klassenbuch/gruppe?schluessel=` | Gruppe mit Pool (alle Schüler der genannten Klassen) und Mitgliedern | Gruppenlehrkraft, KL/SK der Klassen, Admin |
| PUT | `/api/klassenbuch/gruppe?schluessel=` | Gruppe zusammenstellen `{ mitglieder: [ids] }` | Gruppenlehrkraft, Admin |

- **Lektion** = eine Stunde mit Klassenbuch, Schlüssel (Datum, Stunde, UNr laut Grundplan,
  Lehrkraft laut Grundplan) – derselbe Schlüssel, über den das Vertretungs-Overlay zuordnet.
  Sie entsteht beim ersten Speichern (Tabelle `KlassenbuchLektionen`). Der Wochenplan liefert
  je Stunde `grundUNr`, `grundLehrer`, `klassenbuch` (gibt es eins?) und `erfasst`.
- **Klassenbuch gibt es** nur für Unterricht, der stattfindet (nicht Entfall, verlegt, frei),
  mit Schülern aus einer Klasse oder einem Kurs. Gruppen ohne Klassenangabe („Sonstige“, z. B.
  „AG2-6“), Bereitschaften und PSF haben keins.
- **Teilnehmer:** Klasse → alle Schüler der Klasse; Kurs → Kursmitglieder; Klasse mit
  Schülergruppe (Religion, WP, AG über Klassen, z. B. `ER_05A05B05C05D_L88`) → Mitglieder der
  zusammengestellten Gruppe (`Lerngruppe` Typ `Gruppe`). Ist die Gruppe noch nicht
  zusammengestellt, kann für die Stunde nichts erfasst werden (`offeneGruppen`). Schüler einer
  Klasse mit klassenbezogen freiem Tag (Klassenfahrt) stehen nicht in der Liste.
- **Status:** `Fehlt`, `Verspaetet` (Minuten 1–240), `Beurlaubt`, `Schulveranstaltung` (zählt
  später nicht als Fehlzeit). Gespeichert werden nur Abweichungen; „erfasst von/am“ trennt
  „alle anwesend“ von „nicht erfasst“.
- **Rechte:** lesen und schreiben dürfen die haltende Lehrkraft (auch Vertretung, EVA,
  Verlegung), die ursprüngliche Lehrkraft, Klassenleitung/Stufenkoordination (zuständig für
  eine Klasse/Stufe der Stunde) und Admin. Keine Erfassung für künftige Tage, **keine
  Änderungsfrist**.
- **Historie** (`KlassenbuchProtokolle`): erste Erfassung, jede Statusänderung (alt → neu) und
  jede Änderung an Gruppenlisten – mit Akteur und Zeit.
- **Doppelstunde:** Folgt direkt dieselbe Stunde (gleiche UNr und Lehrkraft laut Grundplan,
  mit Klassenbuch), enthält das Stundenblatt `folge` (`stunde`, `erfasst`). Mit
  `folgestundeUebernehmen: true` erfasst das Speichern die 2. Stunde mit: „verspätet“ wird dort
  „anwesend“, die übrigen Status werden übernommen. Eine schon erfasste 2. Stunde wird nie
  überschrieben. `meldung` in der Antwort sagt, was gespeichert wurde. In der Historie der
  2. Stunde steht „übernommen aus der 1. Stunde“.
- **Gleichzeitiges Speichern:** Das Stundenblatt trägt eine `version`. Hat inzwischen jemand
  anderes gespeichert, antwortet die API mit 409 `{ konflikt: true }`; die App lädt dann neu.
- **Schülerplan:** Ist eine Sek-I-Gruppe zusammengestellt, sehen nur ihre Mitglieder diese
  Stunden (nur die eigene Religions-/WP-/AG-Gruppe). Nicht zusammengestellt: wie bisher alle
  Schüler der Klasse.
- **Pseudodaten neu erzeugen** löscht die Klassenbuchdaten der Pseudo-Schüler und alle
  Gruppenlisten (die Lehrkräfte stellen sie mit den neuen Schülern neu zusammen).
- **Schema:** neue Tabellen `KlassenbuchLektionen`, `Anwesenheiten`, `KlassenbuchProtokolle`
  und Spalten `Lerngruppen.BearbeitetVon/BearbeitetAm` – ergänzt die Schema-Nachrüstung beim
  Start (DB muss nicht gelöscht werden). Die alte, nie benutzte Tabelle `Lektionen` (Phase 1A)
  bleibt in bestehenden Entwicklungs-DBs ungenutzt liegen.

In der PWA: Stunden mit Klassenbuch tragen für Berechtigte ein Buch-Symbol (erfasst: Häkchen)
und öffnen per Antippen das Stundenblatt. Name antippen = fehlt ⇄ anwesend, „⋯“ = weitere
Status. Bei Doppelstunden bietet die 1. Stunde den Schalter „Auch für die 2. Stunde übernehmen“. Kontomenü → „Meine Gruppen“ (Admin: „Gruppenlisten“). Namen und Anwesenheiten liegen
nur im Arbeitsspeicher, nie im Browser-Speicher (ein Offline-Speicher für freigegebene
Schulverwaltungs-PCs folgt später).

## Produktiv: Migrations statt EnsureCreated

`EnsureCreated` ist nur für die Entwicklung gedacht und verträgt sich **nicht** mit Migrations.
Für den Schulserver (PostgreSQL) stattdessen EF-Core-Migrations verwenden:

```
dotnet tool install --global dotnet-ef
dotnet ef migrations add Init
dotnet ef database update
```

Dann in `Program.cs` den `EnsureCreated`-Block entfernen bzw. durch `db.Database.Migrate()`
ersetzen und `Database:SchemaNachruesten` auf `false` setzen.

## Nächste Schritte

1. **Schülerkonten aus SchILD** (Phase 5): Import statt Pseudodaten, Zuordnung über `ExtId`.
2. **Klassenbuch (Phase 3):** 3.2 Entschuldigungen + Fehlzeiten, 3.3 Inhalte/Hausaufgaben,
   3.4 Klassenbucheinträge.
3. **Web-Push:** Benachrichtigung der Apps bei Änderung.
4. **Produktiv:** Schlüssel aus Umgebungsvariablen, EF-Migrations, PostgreSQL, CORS auf die
   App-Adresse einschränken, Login-Bremse hinter dem Reverse-Proxy je echter Adresse.
