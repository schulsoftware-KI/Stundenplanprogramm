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
- `Betrieb:HttpsErzwingen` (`true`) – im Echtbetrieb HSTS und Umleitung auf https (Phase 5.1).
- `Proxy:Aktiv` (`false`) / `Proxy:BekannteProxys` (`[]`) – hinter einem Reverse Proxy die echte
  Absenderadresse übernehmen (Phase 5.1).
- `Cors:Herkuenfte` (`[]`) – im Echtbetrieb erlaubte fremde Herkünfte (normalerweise keine).

Im **Echtbetrieb** (jede Umgebung außer Development) gelten zusätzliche Regeln – siehe
„Produktionsmodus (Phase 5.1)“ unten.

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
| POST | `/api/auth/seed-konten` | Testkonten anlegen bzw. Schülerkonten neu verknüpfen (nur Entwicklung, nicht bei echten Schülern) | `X-Api-Key` |

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

## Fehlzeiten und Entschuldigungen (Phase 3.2)

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| GET | `/api/fehlzeiten/rechte` | was darf ich sehen (Lehrkraft, Admin, Klassen/Stufen) | Personal |
| GET | `/api/fehlzeiten/nicht-erfasst?ansicht=ich\|zustaendig\|alle&lehrer=&klasse=` | Stunden ohne Anwesenheit (ab `Klassenbuch:ErfassungAb`, offen ab dem Folgetag; höchstens 500 + Anzahl je Lehrkraft) | ich: Lehrkraft · zustaendig: KL/SK · alle: Admin |
| GET | `/api/fehlzeiten/unterrichte` | „Meine Kurse“ (Unterrichte der Lehrkraft im gültigen Grundplan) | Personal |
| GET | `/api/fehlzeiten/unterricht?unr=&lehrer=` | Schüler eines Unterrichts mit Summen | die Lehrkraft selbst, Admin |
| GET | `/api/fehlzeiten/klasse/{klasse}` | Schüler einer Klasse bzw. Stufe (EF/Q1/Q2) mit Summen | KL/SK zuständig, Admin |
| GET | `/api/fehlzeiten/schueler/{id}?unr=&lehrer=` | Fehlzeiten eines Schülers (Summen, je Fach, Einträge, Entschuldigungen) | Personal (Lehrkräfte ohne Zuständigkeit: nur Einträge aus eigenen Stunden) |
| GET | `/api/fehlzeiten/ich` | eigene Fehlzeiten | Schülerkonto |
| POST | `/api/fehlzeiten/schueler/{id}/entschuldigung` | Zeitraum entschuldigen `{ von, bis, grund }` | KL/SK zuständig, Admin |
| DELETE | `/api/fehlzeiten/entschuldigung/{id}` | Entschuldigung löschen (Stunden wieder „offen“) | KL/SK zuständig, Admin |
| PUT | `/api/fehlzeiten/eintrag/{id}` | eine Fehlstunde/Verspätung bewerten `{ bewertung, grund? }` | KL/SK zuständig, Admin; Kurslehrkraft der Oberstufe in eigenen Kursstunden |

Ohne `von`/`bis` gilt das laufende Schuljahr laut Kalender.

- **Zählung:** Fehlstunden = „fehlt“ + „beurlaubt“ (beurlaubt = entschuldigt); Verspätungen getrennt
  (Anzahl, Minuten); „schulische Veranstaltung“ ist keine Fehlzeit.
- **Bewertung** je Fehlstunde/Verspätung: `Offen`, `Entschuldigt` (mit Grund `Krankheit`,
  `Arzttermin`, `Familiaer`, `Sonstiges` – kein Freitext) oder `Unentschuldigt`. Unentschuldigt
  wird nur von Hand gesetzt, es gibt keine Frist.
- **Entschuldigung (Zeitraum):** Sie deckt alle Fehlstunden und Verspätungen von–bis ab, auch
  später nachgetragene (beim Speichern im Stundenblatt). Wird sie gelöscht, sind die Stunden wieder
  „offen“, außer ein anderer Zeitraum deckt sie ab.
- **Oberstufe:** Die Kurslehrkraft (Lehrkraft laut Plan, nicht die Vertretung) bewertet in ihren
  eigenen Kursstunden. Bewertungen von KL/SK/Admin und Zeitraum-Entschuldigungen sind
  **festgelegt**; dann kann die Kurslehrkraft nichts mehr ändern. Setzt die Stufenkoordination
  eine Stunde zurück auf „offen“, ist sie wieder frei.
- **Festgelegt im Stundenblatt:** Eine von KL/SK/Admin **einzeln** bewertete Fehlstunde/Verspätung
  kann die Fach- bzw. Kurslehrkraft auch über die Anwesenheit nicht mehr ändern (Teilnehmer mit
  `festgelegt`, `festgelegtVon`, `gesperrt`; `PUT /api/klassenbuch/stunde` antwortet sonst 403).
  KL/SK/Admin dürfen ändern (die App fragt einmal nach). Über einen Zeitraum entschuldigte Stunden
  bleiben für die Lehrkraft änderbar.
- **Historie** (`KlassenbuchProtokolle`, Art `Bewertung` bzw. `Entschuldigung`): jede Bewertung
  alt → neu, jede Entschuldigung (eingetragen/gelöscht).
- **Schema:** neue Tabelle `Entschuldigungen`, neue Spalten in `Anwesenheiten` (`Bewertung`, `Grund`,
  `EntschuldigungId`, `BewertetVon`, `BewertetAm`, `Festgelegt`). Die Schema-Nachrüstung ergänzt sie;
  die DB muss nicht gelöscht werden.
- **Einstellung** `Klassenbuch:ErfassungAb` (Standard `2026-11-02`): Ab diesem Tag zählen Stunden
  ohne Anwesenheit als „nicht erfasst“.

In der PWA: Kontomenü → „Fehlzeiten“ (Personal) bzw. „Meine Fehlzeiten“ (Schüler). Reiter
„Nicht erfasst“ (Antippen öffnet das Stundenblatt), „Meine Kurse“ und „Klasse/Stufe“ bzw.
„Klassen“ (Admin); Schülerdetail mit Summen, Entschuldigungen und Einträgen. Im Stundenblatt
steht bei einer fehlenden Person „entschuldigt“ bzw. „unentschuldigt“.

## Unterrichtsinhalt und Hausaufgaben (Phase 3.3)

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| GET | `/api/klassenbuch/stunde?…` | Stundenblatt – jetzt mit `inhalt` (Thema, Hausaufgabe, fällig, Rechte, vorige Stunde, Vorschlag „nächste Stunde“, Sichtbarkeit) | wie 3.1 |
| PUT | `/api/klassenbuch/stunde` | zusätzlich `inhalt: { thema, hausaufgabe, faellig }` und `anwesenheitErfassen` (false = nur Thema/Hausaufgabe) | Anwesenheit wie 3.1; Inhalt: haltende/ursprüngliche Lehrkraft, Admin |
| GET | `/api/klassenbuch/themen?unr=&lehrer=&von=&bis=` | Themenliste eines Unterrichts (neueste zuerst, höchstens 400) | die Lehrkraft selbst, Admin |
| PUT | `/api/klassenbuch/sichtbarkeit` | `{ uNr, lehrer, sichtbar, datum? }` – Hausaufgaben des Unterrichts für Schüler sichtbar/ausgeblendet | Lehrkraft laut Plan, Admin |
| GET | `/api/hausaufgaben/ich` | „Was ist auf?“ – eigene Hausaufgaben nach Fälligkeit (Stunden der letzten 5 Wochen, bis 7 Tage überfällig) | Schülerkonto |
| GET | `/api/fehlzeiten/nicht-erfasst?…&art=alle\|anwesenheit\|thema` | zusätzlich Stunden ohne Thema (`themaFehlt`, `anwesenheitFehlt`, Zähler `ohneAnwesenheit`/`ohneThema`) | wie 3.2 |

- **Thema** bis 300 Zeichen (einzeilig), **Hausaufgabe** bis 2000 Zeichen (mehrzeilig) mit
  **fällig am**. Nur Text; die App macht `https://`-Links klickbar. Keine Dateien.
- **Gespeichert** wird zusammen mit der Anwesenheit (eine Version, Konfliktschutz wie 3.1). Thema und
  Hausaufgabe gibt es erst ab dem Tag der Stunde. Bei noch nicht zusammengestellter Gruppe lässt
  sich das Thema allein speichern (`anwesenheitErfassen: false`) – die Stunde bleibt „nicht erfasst“.
- **Rechte:** schreiben dürfen die haltende und die ursprüngliche Lehrkraft sowie Admin;
  Klassenleitung/Stufenkoordination lesen. Im Wochenplan (`/api/woche/…`) stehen `thema`,
  `hausaufgabe`, `hausaufgabeFaellig` für: Admin, die Lehrkräfte der Stunde, KL/SK der Klasse/Stufe
  und Lehrkräfte, die die Klasse bzw. Stufe laut Grundplan unterrichten. Schüler sehen in
  `/api/woche/ich` das Thema ihrer Stunden immer, die Hausaufgabe nur, wenn der Unterricht sie nicht
  ausgeblendet hat. Ohne Anmeldung: nichts.
- **Fälligkeit:** Vorschlag = nächster Tag, an dem derselbe Unterricht (UNr + Lehrkraft laut Plan)
  stattfindet – Ferien, freie Tage und veröffentlichter Entfall übersprungen (sucht 4 Wochen voraus,
  sonst eine Woche).
- **Doppelstunde:** Mit „Auch für die n. Stunde übernehmen“ geht das Thema in die 2. Stunde, wenn dort
  noch keins steht (nie überschrieben). Die Hausaufgabe steht nur in der 1. Stunde.
- **Sichtbarkeit** je Unterricht (Tabelle `UnterrichtEinstellungen`: Schuljahr, UNr, Lehrkraft;
  Standard sichtbar). Betrifft nur Hausaufgaben, nicht das Thema. Lehrkräfte sehen immer alles.
- **Historie:** Arten `Thema` und `Hausaufgabe` (alt → neu, mit Fälligkeit) in der Lektion;
  `Sichtbarkeit` als eigener Eintrag (Gruppe = `UNr|LEHRKRAFT`).
- **Nicht erfasst:** erfasste Stunden ohne Thema zählen ab `Klassenbuch:ThemaAb` (Standard
  `2026-11-03`), Betreuung ausgenommen.
- **Schema:** neue Spalten `Thema`, `Hausaufgabe`, `HausaufgabeFaellig` in `KlassenbuchLektionen`,
  neue Tabelle `UnterrichtEinstellungen`. Die Schema-Nachrüstung ergänzt sie; die DB muss nicht
  gelöscht werden. Eine Lektion kann jetzt auch ohne Anwesenheit existieren („erfasst“ =
  `ErfasstAm` gesetzt).

In der PWA (`wwwroot/hausaufgaben.js`, dazu klassenbuch.js/fehlzeiten.js/app.js): Im Stundenblatt
über der Anwesenheitsliste „Thema der Stunde“, „Hausaufgabe“, „Fällig am“ (Schnellwahl nächste
Stunde / in 1 Woche), Hinweis auf die vorige Stunde und der Schalter „Hausaufgaben für Schüler
sichtbar“. „Fehlzeiten → Meine Kurse → Kurs → Themen“: Themenliste mit demselben Schalter.
Schüler: Leiste „Hausaufgaben“ über dem Plan und Kontomenü „Hausaufgaben“ (Abhaken nur auf dem
Gerät, offline lesbar); im Plan „HA“-Marke, Antippen zeigt Thema und Hausaufgabe. Personal:
Thema/Hausaufgabe landen nie im Browser-Speicher. Service-Worker v9.

## Klassenbucheinträge (Phase 3.4)

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| GET | `/api/eintraege/stunde?datum=&stunde=&unr=&lehrer=` | Einträge einer Stunde (nur, was man lesen darf), Schreibrecht, `schuelerErlaubt` | wer das Stundenblatt öffnen darf |
| POST | `/api/eintraege/stunde` | `{ datum, stunde, grundUNr, grundLehrer, art: Klasse\|Schueler, schuelerIds, kategorie, text }` – je Schüler ein Datensatz | haltende/ursprüngliche Lehrkraft, KL/SK (Schüler nur der eigenen Klasse/Stufe), Admin |
| POST | `/api/eintraege/schueler/{id}` | Schülereintrag **ohne Stunde** `{ datum, kategorie, text }` (z. B. Pause, Ausflug) | KL/SK des Schülers, Admin |
| PUT | `/api/eintraege/{id}` | ändern `{ kategorie, text, version }` (409 bei veralteter Version) | Verfasser, Admin |
| DELETE | `/api/eintraege/{id}` | löschen | Verfasser, Admin |
| GET | `/api/eintraege/{id}/historie` | Änderungen eines Eintrags | wer den Eintrag lesen darf |
| GET | `/api/eintraege/schueler/{id}?von=&bis=&unr=&lehrer=` | Einträge eines Schülers mit Zählern je Kategorie | Personal (Lehrkräfte: nur eigene) |
| GET | `/api/eintraege/klasse/{klasse}?von=&bis=` | alle Einträge einer Klasse/Stufe | KL/SK zuständig, Admin |
| GET | `/api/eintraege/unterricht?unr=&lehrer=&von=&bis=` | Einträge eines Unterrichts + Klasseneinträge seiner Sek-I-Klassen aus anderen Stunden (`weitere`) | die Lehrkraft selbst, Admin |

- **Arten/Kategorien:** Schülereintrag – `Lob`, `Hinweis`, `Ermahnung`, `HausaufgabeVergessen`,
  `MaterialVergessen`; Klasseneintrag – `Vorkommnis`, `Hinweis`, `Lob`. Text bis 500 Zeichen (Pflicht
  bei Hinweis, Ermahnung, Vorkommnis). Keine formalen Ordnungsmaßnahmen.
- **Lesen:** Klasseneinträge – Lehrkräfte der Klasse bzw. des Kurses laut Grundplan, die Lehrkräfte
  der Stunde, KL/SK, Admin. Schülereinträge – Verfasser, KL/SK des Schülers, Admin. **Schüler sehen
  keine Einträge** (alle Endpunkte nur Personal).
- **Speichern** sofort (eigene Tabelle, nicht über die Version der Lektion). Nicht für künftige Stunden.
- **Historie:** Art `Eintrag` (angelegt, alt → neu) mit `EintragId`, bei Stunden zusätzlich mit dem
  Stundenschlüssel in `Gruppe` – erscheint in der Änderungshistorie des Stundenblatts. Beim Löschen
  werden die bisherigen Zeilen des Eintrags entfernt; es bleibt `EintragGeloescht` „gelöscht
  (Kategorie, Datum)“ ohne Text und ohne Schüler.
- **Löschfrist:** `Klassenbuch:EintraegeLoeschenNachTagen` (Standard `0` = aus); beim Start werden
  ältere Einträge samt Historie gelöscht. Festlegung in Phase 5 mit der/dem DSB.
- **Schema:** neue Tabelle `KlassenbuchEintraege`, neue Spalte `KlassenbuchProtokolle.EintragId`.
  Die Schema-Nachrüstung ergänzt beides; die DB muss nicht gelöscht werden. Ein neuer
  Pseudodaten-Seed löscht die Einträge mit.

In der PWA (`wwwroot/eintraege.js`, Präfix `ke…`, dazu klassenbuch.js/fehlzeiten.js): Karte
„Einträge“ im Stundenblatt mit „+ Eintrag für die Klasse“; Schülereinträge über „⋯“ beim Namen
(Statusdialog, auch für mehrere Schüler); Marken unter den Namen. Kontomenü „Fehlzeiten“ heißt jetzt
„Klassenbuch“: neuer Reiter „Einträge“ (KL/SK/Admin, Filter nach Kategorie), „Meine Kurse“ mit
Fehlzeiten | Themen | Einträge, Schülerdetail mit Abschnitt „Einträge“ und „Eintrag ohne Stunde“.
Einträge landen nie im Browser-Speicher. Service-Worker v10.

## Auswertungen am Bildschirm (Phase 4.1)

Entscheidungen 40–43 (Nutzer 04.11.2026). Keine Schemaänderung – nur neue Einstellung und Endpunkte.

| Methode | Pfad | Zweck | Wer |
|---|---|---|---|
| GET | `/api/fehlzeiten/zeitraeume` | 1. Halbjahr, 2. Halbjahr, Schuljahr + `standard` (laufendes Halbjahr) | alle Angemeldeten |
| GET | `/api/fehlzeiten/offen/{klasse}` | offene Fälle der Klasse/Stufe im ganzen Schuljahr bis heute, älteste zuerst (`aelteste`, `neueste`, `tage`) | KL/SK zuständig, Admin |

- **Zeitraum:** Alle Listen nehmen weiterhin `von`/`bis`; die PWA schickt den gewählten Zeitraum
  überall mit (Klasse/Stufe, Unterricht, Themen, Einträge, Schüler, „Meine Fehlzeiten“). Das
  2. Halbjahr beginnt laut `Klassenbuch:HalbjahrAb` (`2027-02-01`); fehlt die Einstellung oder liegt
  sie nicht im Schuljahr, gilt der 1. Februar.
- **Soll und Fehlquote:** `FzZeileDto.soll`, `FzFachDto.soll`, `FzSchuelerDto.soll` = erfasste Stunden
  im Zeitraum, in denen der Schüler laut Liste dabei sein sollte (Klasse/Kurs; bei Sek-I-Gruppen nur
  die Mitglieder) plus jede erfasste Stunde, in der er einen Eintrag hat. Teilnahme nach den heutigen
  Zuordnungen (wie im Stundenblatt). Fehlquote = Fehlstunden ÷ Soll – nur Anzeige, keine Schwellen.
- **Listen** (`klasse/{k}`, `unterricht`) tragen zusätzlich `gesamt` (Summe aller Zeilen) und `soll`.

In der PWA (`fehlzeiten.js`): Zeitraumleiste „1. Halbjahr · 2. Halbjahr · Schuljahr · Eigener“,
Konferenzübersicht (Fehlstunden, davon entschuldigt/offen/unentschuldigt, Quote, Verspätungen,
Minuten; sortierbar, Gesamtzeile, „Nur Schüler mit offenen Stunden“, Hinweis „Noch offen: …“),
bei Klasse/Stufe Umschalter „Übersicht | Offene Fälle“ mit „Entschuldigung eintragen“ je Schüler,
Fehlquote im Schülerdetail und je Fach. Service-Worker v12.

## Klassenbuch und Kursheft als PDF (Phase 4.2)

Entscheidungen 44–47 und 50 (Nutzer 04.11.2026). Neues Paket **QuestPDF 2026.9.1** (wie im Desktop,
Community-Lizenz). Neue Tabelle `DownloadProtokolle` – **Schemaänderung per Nachrüstung, DB muss NICHT
gelöscht werden.** Neue Einstellung `Berichte:NurAdmin` (Standard `false`).

| Methode | Pfad | Zweck | Wer |
|---|---|---|---|
| GET | `/api/berichte/unterrichte?klasse=05A` | Unterrichte der Klasse/Stufe (Auswahl „Kursheft“) | KL/SK zuständig, Admin |
| GET | `/api/berichte/klassenbuch?klasse=&von=&bis=&schuelerEintraege=` | Klassenbuch als PDF | KL/SK zuständig, Admin |
| GET | `/api/berichte/kursheft?unr=&lehrer=&von=&bis=&schuelerEintraege=` | Kursheft als PDF | Lehrkraft laut Plan, KL/SK der Klassen/Stufe, Admin |
| GET | `/api/berichte/protokoll?limit=` | Download-Protokoll (wer, wann, was, Zeitraum) | Admin |

- **Inhalt** (`Services/BerichtService.cs` → `Services/KlassenbuchPdf.cs`): A4 hoch, je Unterrichtstag eine
  Tabelle (Std · Fach/Lehrkraft/Status · Thema/Hausaufgabe/Klasseneinträge · Anwesenheit mit Namen, „erfasst
  von/am“, „geändert von/am“). Entfall/Verlegung und nicht erfasste Stunden bleiben als Zeile sichtbar.
  Anhang: Fehlzeiten je Schüler (wie die Konferenzübersicht, mit Fehlquote und Gesamtzeile). Kopf/Fuß jeder
  Seite: Art, Klasse/Kurs, Zeitraum, Schuljahr, „Enthält personenbezogene Daten“, erstellt am/von, „Seite x
  von y“. Zeitraum ohne `von`/`bis` = Schuljahr; er endet spätestens heute.
- **Schülereinträge** nur mit `schuelerEintraege=true` und nur, wenn KL/SK/Admin druckt – und nur für Schüler,
  für die die Person zuständig ist (im Kursheft nur Einträge aus diesem Unterricht).
- **Protokoll:** jeder Download (ohne Inhalt), Frist wie das Kontenprotokoll (`Konten:ProtokollTage`).
  `Berichte:NurAdmin = true` erlaubt Downloads nur noch der Verwaltung (`/api/fehlzeiten/rechte` meldet
  `berichte`).

In der PWA (`fehlzeiten.js`): Knopf „Klassenbuch als PDF …“ (Klasse/Stufe, Übersicht) bzw. „Kursheft als
PDF …“ (Unterricht: Fehlzeiten/Themen/Einträge). Dialog mit Auswahl ganzes Klassenbuch bzw. Kursheft eines
Unterrichts der Klasse, gewähltem Zeitraum, Schalter „Schülereinträge aufnehmen“ (Standard aus) und Hinweis
„Die Datei enthält Schülerdaten …“. Das PDF wird nur heruntergeladen, nichts im Browser-Speicher.
Service-Worker v13.

## Export für die Verwaltung und Jahresarchiv (Phase 4.3)

Entscheidungen 48–50 und 60–65 (Nutzer 04./05.11.2026). **Keine Schemaänderung** (die Spalte `Art` im
Download-Protokoll ist Text; neue Werte `FehlzeitenCsv` und `Archiv`). Code: `Services/ExportService.cs`.

| Methode | Pfad | Zweck | Wer |
|---|---|---|---|
| GET | `/api/berichte/fehlzeiten-csv/pruefen?klasse=05A&von=&bis=` (bzw. `alle=true`) | Vorab: offene Fehlstunden/Verspätungen, nicht erfasste Stunden | KL/SK zuständig, Admin (`alle` nur Admin) |
| GET | `/api/berichte/fehlzeiten-csv?klasse=05A&von=&bis=` (bzw. `alle=true`) | Fehlzeiten als CSV | KL/SK zuständig, Admin (`alle` nur Admin) |
| GET | `/api/berichte/archiv?von=&bis=&schuelerEintraege=` | Jahresarchiv als Zip | Admin |
| GET | `/api/berichte/protokoll?limit=&art=&akteur=` | Download-Protokoll, Filter nach Art und Person | Admin |

- **CSV:** Semikolon, UTF-8 mit BOM, Formelschutz (wie die Zugangszettel). Spalten `SchILD-Id; Name; Vorname;
  Klasse; Kurs; Fehlstunden; entschuldigt; unentschuldigt; offen; Verspätungen; Minuten`. Eine Zeile je
  Schüler (Kurs leer = Summe); in der Oberstufe zusätzlich je Kurs eine Zeile (Kurs = Lerngruppen-Schlüssel,
  jeder Kurs des Schülers, auch ohne Fehlzeiten). Zählung wie die Konferenzübersicht. Die SchILD-Id bleibt
  bis zum Import echter Daten (Phase 5) leer. `alle=true` = alle Klassen und Stufen, jede Person einmal.
- **Vorabprüfung:** nicht erfasst = Stunden ohne Anwesenheit ab `Klassenbuch:ErfassungAb` bis gestern; der
  Export bleibt trotzdem möglich.
- **Jahresarchiv:** je Sek-I-Klasse ein Klassenbuch, je Oberstufenkurs (UNr + Lehrkraft laut Plan) ein
  Kursheft – ermittelt aus dem effektiven Plan des Zeitraums. Zip mit `Klassen/…`, `Kurse/<Stufe>/…` und
  `Inhalt.txt` (Zeitraum, erstellt von/am, Hinweis Schülerdaten, Dateiliste, nicht erstellte Hefte). Die
  Plantage werden einmal berechnet; die Zip entsteht als Zwischendatei im Temp-Ordner und wird nach dem
  Senden gelöscht. Bricht die Verbindung ab, endet die Erzeugung zwischen zwei Heften. Ein fehlerhaftes Heft
  bricht das Archiv nicht ab (Log + `Inhalt.txt`).
- **Protokoll:** jeder CSV-/Archiv-Download wie die PDFs (wer, wann, was, Zeitraum – ohne Inhalt).
  `Berichte:NurAdmin = true` sperrt auch den CSV-Export für alle außer der Verwaltung.

> Über den Cloudflare-Quick-Tunnel kann eine lange Anfrage nach etwa 100 Sekunden abbrechen (Fehler 524).
> Das Jahresarchiv für ein ganzes Halbjahr deshalb am PC über `http://localhost:5080` erstellen.

In der PWA: Knöpfe „Fehlzeiten als CSV …“ (Klasse/Stufe, Übersicht; Admin wahlweise alle Klassen) und
„Jahresarchiv …“ (Admin) neben „Klassenbuch als PDF …“; Reiter „Downloads“ in „Konten verwalten“ (Admin).
Hilfethemen dazu in `hilfe.js`. Service-Worker v15.

## Produktionsmodus (Phase 5.1)

**Echtbetrieb ist jede Umgebung außer `Development`** (`ASPNETCORE_ENVIRONMENT`). Ohne Angabe startet
ASP.NET Core als `Production` – das ist der sichere Standard für den Server. Visual Studio und
`dotnet run` setzen über `Properties/launchSettings.json` weiterhin `Development`.

### Was sich im Echtbetrieb ändert

| | Entwicklung | Echtbetrieb |
|---|---|---|
| Startprüfung | – | Start bricht ab bei fehlendem/zu kurzem/Repo-`Jwt:Key` (mind. 32 Zeichen) oder `Api:PushKey` (mind. 24 Zeichen), gleichen Schlüsseln, `Woche:OhneLogin: true`, fehlender Postgres-Verbindung oder Passwort `postgres` bzw. Platzhalter aus der Vorlage |
| Seeds (`/api/seed/pseudodaten`, `/api/auth/seed-konten`) | erlaubt, solange keine echten Schüler da sind (sonst 409) | 404 |
| Erstes Admin-Konto | `seed-konten` | Befehl `admin-anlegen` (siehe unten) |
| Datenbank | SQLite: EnsureCreated + Schema-Nachrüstung | PostgreSQL mit **EF-Migrations** (`Data/Migrations`); ohne Migrations startet die API nicht |
| HTTPS | – | HSTS + Umleitung auf https (`Betrieb:HttpsErzwingen`) |
| CORS | offen | nur `Cors:Herkuenfte` (Standard: keine – die PWA kommt vom selben Server) |
| OpenAPI (`/openapi/v1.json`) | ja | nein |

Immer (beide Modi): Push-Schlüssel wird in konstanter Zeit verglichen (`Helpers/ApiSchluessel.cs`);
`GET /api/gesundheit` → `{ status: "ok" }` bzw. 503, ohne Anmeldung und ohne Daten (für Überwachung/Proxy);
beim Start eine Zeile „Betrieb: … · Datenbank … · Proxy … · HTTPS erzwingen …“ und ggf. Hinweise
(z. B. `AllowedHosts` noch „*“, SQLite im Echtbetrieb).

### Schlüssel und Passwörter

Nie ins Repo (es ist öffentlich). Zwei Wege, beliebig kombinierbar:
- **Umgebungsvariablen** (doppelter Unterstrich statt Doppelpunkt): `Jwt__Key`, `Api__PushKey`,
  `ConnectionStrings__Postgres`, `Database__Provider=Postgres`.
- **`appsettings.Production.json`** neben der API (steht in `.gitignore`). Vorlage ohne Werte:
  `appsettings.Production.Beispiel.json` – kopieren, umbenennen, Werte eintragen. Die Platzhalter
  „HIER-EIGEN…“ erkennt die Startprüfung.

Zufälligen Schlüssel erzeugen (PowerShell, zweimal ausführen – je einer für `Jwt:Key` und `Api:PushKey`):
```powershell
$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```
Den `Api:PushKey` im Desktop unter „Online-Einstellungen…“ eintragen.

### Migrations (PostgreSQL)

Die Migrations gelten nur für PostgreSQL; `Data/EntwurfsFabrik.cs` baut dafür den Kontext immer mit
Npgsql (ohne Datenbankverbindung). Die API bringt die Datenbank **beim Start selbst** auf den neuesten
Stand (`Data/DatenbankStart.cs`) – `dotnet ef database update` ist nicht nötig.

Die erste Migration **„Grundlinie“** liegt im Repo (`Data/Migrations`, erzeugt am 07.11.2026 mit EF Core 10)
und legt eine leere Datenbank vollständig an. **Nach jeder Änderung am Datenmodell** kommt eine weitere
Migration dazu – im Ordner `VertretungApi`:
```powershell
dotnet tool install --global dotnet-ef        # nur einmal
dotnet ef migrations add <Name> -o Data/Migrations
```
(Namen z. B. `Zugehoerigkeit_Gueltigkeit`; „Grundlinie“ ist schon vergeben). Den Ordner `Data/Migrations`
danach ins Repo übernehmen.
Fehlt nach einer Modelländerung die Migration, bricht der Start mit „pending changes“ und einem Hinweis
ab, statt mit unvollständigem Schema zu laufen. SQLite in der Entwicklung bleibt bei EnsureCreated +
Schema-Nachrüstung und braucht keine Migrations.

### Probelauf mit PostgreSQL auf dem eigenen PC

1. PostgreSQL installieren (`winget install PostgreSQL.PostgreSQL.17`, Passwort für `postgres` merken).
   Bricht der Download mit „403“ ab, den Installer im Browser laden: postgresql.org/download/windows →
   „Download the installer“ → Version 17, Windows x86-64; Komponenten „PostgreSQL Server“ und „Command Line
   Tools“, Stack Builder am Ende abwählen (bzw. einfach abbrechen).
   In der „SQL Shell (psql)“ als `postgres` (viermal Enter, dann das `postgres`-Passwort):
   ```sql
   CREATE ROLE vertretung LOGIN PASSWORD 'eigenes-passwort' CREATEDB;
   CREATE DATABASE vertretung OWNER vertretung;
   CREATE DATABASE vertretung_dev OWNER vertretung;
   ```
   **Passwort nur aus Buchstaben, Ziffern und Bindestrichen.** Die psql-Konsole unter Windows arbeitet mit
   Codeseite 850 (Warnung „Konsolencodeseite …“): Sonderzeichen wie `§` oder Umlaute kommen dort anders an als
   später aus der API – die Anmeldung scheitert dann mit `28P01: Passwort-Authentifizierung … fehlgeschlagen`.
   Abhilfe: `ALTER ROLE vertretung WITH PASSWORD 'nur-buchstaben-und-ziffern';` und dasselbe Passwort in
   `ConnectionStrings:Postgres` eintragen. (Ein `;` würde zudem die Verbindungsangabe zerschneiden.)
2. Neu bauen. Die Migration „Grundlinie“ liegt im Repo – `dotnet-ef` braucht man erst, wenn sich das
   Datenmodell ändert (siehe „Migrations“).
3. **Entwicklung auf PostgreSQL** (alle Funktionen mit Pseudodaten – prüft die Abfragen auf PostgreSQL):
   ```powershell
   $env:Database__Provider = "Postgres"
   $env:ConnectionStrings__Postgres = "Host=localhost;Database=vertretung_dev;Username=vertretung;Password=eigenes-passwort"
   dotnet run
   ```
   Log: „Datenbank auf den neuesten Stand gebracht: …_Grundlinie“. Dann wie gewohnt Grundplan und einen
   Vertretungstag veröffentlichen, `seed/pseudodaten`, `auth/seed-konten`, in der App testen. Die beiden
   `$env:`-Zeilen gelten nur für dieses Terminal.
4. **Echtbetrieb lokal** (Startprüfung, Sperren, Notfallzugang): `appsettings.Production.json` aus der
   Vorlage anlegen (Datenbank `vertretung`, eigene Schlüssel, `"HttpsErzwingen": false` für den Test über
   http), dann in einem **neuen** Terminal (ohne die `$env:`-Zeilen):
   ```powershell
   dotnet run --launch-profile Produktionstest -- admin-anlegen     # zeigt ein Startpasswort
   dotnet run --launch-profile Produktionstest
   ```
   Anmelden mit `admin` + Startpasswort, eigenes Passwort festlegen. Seeds antworten jetzt mit 404.

**Startet die API nicht, weil es mit PostgreSQL hakt,** steht die Ursache mit Abhilfe in der Zeile `crit:` im Log
(`Data/DatenbankStart.cs`, ohne Passwort):

| Log-Meldung | Häufige Ursache | Abhilfe |
|---|---|---|
| Anmeldung abgelehnt (28P01/28000) | falsches Passwort, Sonderzeichen im Passwort, Umgebungsvariable überstimmt die Datei | `ConnectionStrings:Postgres` prüfen, ggf. `ALTER ROLE … WITH PASSWORD '…';` |
| Datenbank gibt es nicht (3D000) | Tippfehler bei `Database=` oder nicht angelegt | `CREATE DATABASE … OWNER …;` |
| Rechte fehlen (42501) | Datenbank gehört `postgres` statt dem API-Benutzer | `ALTER DATABASE … OWNER TO …;` |
| Tabellen nicht aus den Migrations (42P07) | Datenbank früher per EnsureCreated gefüllt | neue, leere Datenbank verwenden |
| Keine Verbindung / nicht rechtzeitig geantwortet | PostgreSQL-Dienst läuft nicht, falscher Host/Port | Dienst „postgresql-x64-17“ starten |
| nicht auf den Stand gebracht | Modell geändert, Migration fehlt | `dotnet ef migrations add <Name> -o Data/Migrations` |

### Notfallzugang: Admin-Konto über die Konsole

```powershell
dotnet VertretungApi.dll admin-anlegen [benutzername]        # Server (veröffentlichte Fassung)
dotnet run -- admin-anlegen [benutzername]                   # Entwicklung
```
Legt das Konto als Admin an (Standardname `admin`) bzw. gibt einem vorhandenen **Admin**-Konto ein neues
Startpasswort, entsperrt es und beendet alle Anmeldungen. Konten anderer Rollen werden nicht angefasst.
Das Startpasswort steht nur auf der Konsole; bei der ersten Anmeldung muss ein eigenes festgelegt werden.
Kontenprotokoll: Akteur „Konsole“. Gedacht für das erste Konto im Echtbetrieb und für „Admin-Passwort
vergessen“.

### HTTPS und Reverse Proxy

- **Direkt mit Zertifikat** (ohne Proxy), z. B. in `appsettings.Production.json`:
  ```json
  "Kestrel": { "Endpoints": {
    "Http":  { "Url": "http://*:80" },
    "Https": { "Url": "https://*:443",
               "Certificate": { "Path": "C:\\Vertretung\\zertifikat.pfx", "Password": "…" } } } }
  ```
- **Hinter einem Reverse Proxy** (nginx, IIS, Caddy, Relais), der TLS übernimmt:
  `"Proxy": { "Aktiv": true, "BekannteProxys": ["10.0.0.5"] }` (Adresse des Proxys; ein Proxy auf
  demselben Rechner braucht keinen Eintrag). Die API übernimmt dann Absenderadresse und `https` aus
  `X-Forwarded-For`/`-Proto` – sonst sähe die **Login-Bremse** alle Anfragen mit der Adresse des Proxys und
  sperrte nach 20 Fehlversuchen alle gemeinsam. Kommen Proxy-Angaben an, die nicht ausgewertet werden,
  steht einmal eine Warnung im Log. Die Umleitung von http auf https übernimmt am besten der Proxy (sonst
  `"https_port": 443` setzen).
- `AllowedHosts` auf die eigene Adresse beschränken, z. B. `"klassenbuch.meine-schule.de;localhost"`.
- Der Desktop pusht an die https-Adresse („Online-Einstellungen…“).

### Logs

Die API schreibt keine Namen oder Inhalte ins Log (Kategorien, Zahlen, Klassen-/Kursbezeichnungen).
Im Echtbetrieb EF-Befehle nur ab `Warning` (Vorlage). In der Postgres-Verbindung **kein**
`Include Error Detail=true` – sonst können Datenbankwerte in Fehlermeldungen landen (die Startprüfung weist
darauf hin).

### Sicherung und Rücksicherung

Skripte in `Betrieb/` (Einstellungen jeweils in einer Datei neben dem Skript, nicht im Repo):
- **Windows:** `sicherung.ps1` – `pg_dump` + 7-Zip (AES-256, verschlüsselte Dateinamen). Einstellungen in
  `sicherung.einstellungen.ps1` (Beispiel im Kopf des Skripts).
- **Linux:** `sicherung.sh` – `pg_dump` + gpg (AES-256). Einstellungen in `sicherung.conf`.

Beide: ohne Angabe = neue Sicherung `vertretung_JJJJ-MM-TT_hhmmss.dump.7z|.gpg`, Sicherungen älter als
`Tage` (30) werden gelöscht; bricht `pg_dump` ab, bleibt keine halbe Datei liegen. Mit `-Pruefen` bzw.
`--pruefen` = **Rücksicherungstest**: neueste Sicherung entschlüsseln, in die Testdatenbank
`vertretung_pruefung` zurückspielen, Datensätze zählen (Konten, Schüler, Lektionen, Anwesenheiten,
Einträge …), Testdatenbank wieder löschen; Fehler, wenn keine Konten darin sind. Empfehlung: täglich
sichern (Aufgabenplanung/cron), monatlich prüfen. Die **Passphrase** (mind. 12 Zeichen, nur Buchstaben,
Ziffern, Bindestriche) getrennt vom Server aufbewahren – ohne sie ist jede Sicherung wertlos.
Sicherungen enthalten Schülerdaten: Ordner nur für Administratoren lesbar.

**Ernstfall – Datenbank aus einer Sicherung wiederherstellen** (API vorher beenden):
```powershell
& "C:\Program Files\7-Zip\7z.exe" e vertretung_2026-11-07_023000.dump.7z -oC:\Temp\rueck   # fragt nach der Passphrase
dropdb -U vertretung vertretung
createdb -U vertretung vertretung
pg_restore -U vertretung --no-owner --no-privileges -d vertretung C:\Temp\rueck\sicherung.dump
```
(Linux: `gpg --decrypt datei.dump.gpg > sicherung.dump`, dann dieselben drei Befehle.) Danach die Datei
`sicherung.dump` löschen und die API starten.

## SchILD-Daten prüfen und Pseudo-Export (Phase 5.2a, Desktop)

Nur Desktop, **keine API- und keine Schemaänderung, es wird nichts gesendet.** Im Vertretungs-Wocheneditor
(Button 24) öffnet **„SchILD-Daten…“** (neben „Grundplan veröffentlichen…“) das Fenster „SchILD-Daten prüfen“.
Es nutzt die oben unter „Plan“ gewählte Plan-Spalte. Code: `src/Vertretung/Schild/`.

- **Prüflauf:** Ordner mit einem Export aus SchILD-NRW 3 wählen (Textdateien `.dat`, Trennzeichen `|`,
  Kopfzeile, ANSI oder UTF-8 mit BOM). Gelesen werden nur die Spalten aus `Phase5_Vorschlag` §2.2; alles
  andere (Noten, Adressen, Konfession, Kennwörter, Lehrkräfte-Adressen …) wird beim Lesen verworfen, Dateien
  wie `SchuelerZusatzdaten.dat` werden gar nicht geöffnet. Reiter **Übersicht** (Schüler je Status und
  Jahrgang, Klassen, Kurse, Zugehörigkeiten, Abgleich der Klassen mit dem Plan) und **Probleme** (Datei,
  Zeile, Spalte, Grund – bei Schülerdateien nie ein Inhalt; gleichartige Meldungen zusammengefasst).
  **„Bericht kopieren“** liefert einen Text ohne Schülerdaten (Zahlen, Zeilennummern, Klassen-, Kurs- und
  Fachbezeichnungen, Lehrkräftekürzel). Abschnitt wählbar (Standard: häufigster Abschnitt der aktiven Schüler).
- **Pseudo-Export erzeugen…:** schreibt aus dem Grundplan `Klassen.dat`, `Kurse.dat` (neues Format),
  `SchuelerBasisdaten.dat` (50 Spalten), `SchuelerLeistungsdaten.dat` (19), `Lehrkraefte.dat`, `Faecher.dat`,
  `Jahrgaenge.dat` und optional `SchuelerLernplattformen.dat` (SchILD-ID) mit fiktiven Personen; Standardordner
  `SchILD-Pseudo_<Datum>` neben der Plandatei. Oberstufe: Kurswahl wie beim bisherigen Seed; Sek-I-Gruppen
  (Religion, WP) überschneidungsfrei aufgeteilt, Variante „gemischt“ (Jg. 05–07 ohne Kurs, 08–10 mit Kurs).
  Absichtliche Stolpersteine für die Zuordnung (Fach-/Kürzelabweichungen, SchILD-Kursbezeichnungen,
  Kooperationskurs, `#Klasse`-Paar, Status 0/1/3/6/9) zeigt das Programm nach dem Erzeugen an.
  **`PSEUDODATEN.json`** trägt die SHA-256-Prüfsumme jeder Datei: Nur wenn alle stimmen und keine weitere
  `.dat` im Ordner liegt, gilt er als Pseudodaten. Ein Ordner mit fremden `.dat`-Dateien wird nie überschrieben.
- **Formatprofil:** Standard = amtliche Beschreibung. Weicht ein echter Export ab, mit „Formatprofil als Datei
  anlegen…“ die Vorlage `schild_profil.json` neben die Plandatei legen und dort nur Dateinamen,
  Spaltenüberschriften, Trennzeichen oder Zeichensatz (`auto` | `utf-8` | `windows-1252`) ändern – ohne
  Neukompilieren.
- **Echte Exporte** nur auf dem Verwaltungsrechner ablegen (nicht in OneDrive/Teams) und nach Gebrauch löschen.
  `*.dat` steht in der `.gitignore` (greift beim Hochladen im Browser nicht!).
- Kommt in **5.2a-2:** Zuordnung SchILD-Kurse/Sek-I-Gruppen/Kürzel/Fächer ↔ Plan mit `schild_zuordnung.json`;
  danach 5.2b Import.

## Nächste Schritte

1. **Schülerkonten aus SchILD** (Phase 5): Import statt Pseudodaten, Zuordnung über `ExtId`.
2. **Auswertungen (Phase 4):** ~~4.1 am Bildschirm~~, ~~4.2 Klassenbuch/Kursheft als PDF~~,
   ~~4.3 CSV-Export, Jahresarchiv, Download-Protokoll~~. Genaues SchILD-Importformat in Phase 5.
3. **Web-Push:** Benachrichtigung der Apps bei Änderung.
4. ~~**Produktiv:** Schlüssel aus Umgebungsvariablen, EF-Migrations, PostgreSQL, CORS auf die
   App-Adresse einschränken, Login-Bremse hinter dem Reverse-Proxy je echter Adresse.~~ Phase 5.1.
5. **Phase 5.2:** SchILD-NRW-3-Schnittstelle (`.dat`-Dateien) – Import per Desktop statt Pseudodaten-Seed.
