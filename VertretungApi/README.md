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
- `Jwt:StundenGueltig` (12) / `Jwt:TageAngemeldetBleiben` (30) – **seit P3 nur noch Startwerte** für die
  Schuleinstellungen „Dauer einer Anmeldung“ und „Angemeldet bleiben“ (siehe „Schuleinstellungen (P3, E-1)“).
- `Woche:OhneLogin` (`false`) – `true` macht Lehrer-/Klassen-/Kurspläne auch ohne Anmeldung
  abrufbar (z. B. für einen Aushang-Bildschirm im Schulnetz). Wirkt nach Neustart der API.
- `Database:SchemaNachruesten` (`true`) – fehlende Tabellen/Spalten beim Start ergänzen (nur
  Entwicklung; produktiv mit EF-Migrations auf `false`).
- `Konten:ProtokollTage` (365), `DefaultSchuljahr`, `Klassenbuch:ErfassungAb`, `Klassenbuch:ThemaAb`,
  `Klassenbuch:HalbjahrAb`, `Klassenbuch:EintraegeLoeschenNachTagen`, `Berichte:NurAdmin` – **seit P3 nur noch
  Startwerte**: Beim ersten Start mit P3 übernimmt die API sie in die Schuleinstellungen; danach gilt, was der Admin in
  der App einstellt (Kontomenü → „Einstellungen“), und die Dateiwerte werden ignoriert (Hinweis im Log, wenn sie abweichen).
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

### Räume, Klausuren und geteilte Stunden in Vertretungen (U-1a)

Vorbereitung für die Untis-Übergangsschnittstelle (`claude/Untis_Vorschlag.md`, Entscheidungen 127–133) und R3.
**Schemaänderung:** `GrundplanSlots.Raum` sowie `Eintraege.RaumGeaendert`, `FachGeaendert`, `KlassenGeaendert`
(alle optional) – SQLite ergänzt die Spalten beim Start selbst, PostgreSQL braucht die Migration `U1a_RaumUndWechsel`
(siehe „Migrations“).

- **Raum im Grundplan:** „Grundplan veröffentlichen…“ sendet je Eintrag den Raum aus dem Blatt „Raumplan“ der
  gewählten Plan-Spalte (Knopf 25, Planeditor oder Knopf 3 „aus Untis“; Zuordnung über Wochentag, Stunde, UNr,
  Lehrkraft und Fach). Die App zeigt diesen Raum; ohne passende Zeile wie bisher Fachraum vor Stammraum. Die
  Rückfrage vor dem Veröffentlichen nennt „Räume: n von m Einträgen aus dem Raumplan …“.
- **Wechsel innerhalb einer Vertretung:** Meldet ein Eintrag `raumGeaendert`, `fachGeaendert` bzw.
  `klassenGeaendert` = true, übernimmt die API Raum (alter Raum rot gestrichen), Fach bzw. Klassen (weggefallene
  Klassen verlieren Plan und Klassenbuch-Quelle, neue bekommen sie) – bei jeder Art. Ohne Merker bleibt alles wie
  vorher: Raum nur bei „Raumvertretung“, Fach nur bei „verlegt hin“, Klassen unverändert. Das Vertretungsmodul
  setzt seit R3-D `raumGeaendert`, wenn eine Buchung den Raum ändert (siehe „Räume im Vertretungsmodul“); Fach und
  Klassen meldet nur der Untis-Import.
- **Geteilte Stunde:** Bekommt eine Grundplan-Stunde zwei Vertretungen mit verschiedenen Lehrkräften (Gruppe
  geteilt), wird die zweite eine eigene Stunde mit ihren Klassen; im Klassenbuch hat sie einen eigenen Schlüssel
  (GrundLehrer = vertretende Lehrkraft, ohne Lehrkraft „Kürzel·2“).
- **Status `Klausur`** (Art `Klausur`): Lehrkraft = Aufsicht, `originalLehrer` = Kurslehrkraft, `grundUNr` = UNr
  des Kurses. Zuordnung nur exakt (Stunde, UNr, Kurslehrkraft): Fällt die Klausur in eine Stunde des Kurses, wird
  diese zur Klausur; sonst erscheint eine zusätzliche Stunde mit den Teilnehmern des Kurses. Raum und Fach (Kurs)
  gelten immer. Klassenbuch ja (Anwesenheit), Thema nicht nötig. App: pink, Abzeichen „Klausur“.
- **Abwesend, niemand übernimmt** (z. B. EVA ohne Lehrkraft): `originalLehrer` gesetzt, `lehrer` leer → die
  abwesende Lehrkraft erscheint gestrichen. **Offene Vertretung:** `lehrer` = `???` (wie in Untis).

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
| GET | `/api/konten?q=&rolle=&filter=&limit=` | Kontenliste (`rolle`: Admin, Lehrer, Klassenlehrer, Stufenkoordination, Schueler, Personal; `filter`: pflicht, nie, gesperrt, verwaist, seit 5.2c-2 wiederaktiv, namegeaendert) | KontenSehen |
| GET | `/api/konten/{id}` | ein Konto | KontenSehen |
| POST | `/api/konten/{id}/passwort` | Passwort zurücksetzen → `{ konto, startPasswort }` | KontenSehen |
| POST | `/api/konten/{id}/sperren` · `/entsperren` | Konto sperren / entsperren | KontenSehen |
| POST | `/api/konten` | Konto anlegen `{ rolle, benutzername?, lehrerKuerzel?, schuelerId?, zustaendig? }` → `{ konto, startPasswort }` | Admin |
| PUT | `/api/konten/{id}` | Benutzername, Rolle, Kürzel, Zuständigkeit, Schüler ändern | Admin |
| POST | `/api/konten/{id}/abmelden` | alle Anmeldungen des Kontos beenden | Admin |
| DELETE | `/api/konten/{id}` | Konto löschen | Admin |
| GET | `/api/konten/protokoll?konto=&limit=` | Kontenprotokoll (neueste zuerst) | Admin |
| GET | `/api/konten/sammel/uebersicht` | Lehrkräfte und Klassen/Stufen mit Anzahl vorhandener Konten | Admin |
| POST | `/api/konten/sammel` | Sammelanlage `{ art: "Lehrer"\|"Schueler", gruppen: ["05A","Q1"], nur?: [...], anlegen, klassenleitungEintragen? }` | Admin |
| GET | `/api/konten/schild-vorschlaege` | 5.2c-2: Vorschläge aus SchILD (Klassenleitung laut Klassen.dat ↔ Konten) | Admin |
| POST | `/api/konten/schild-vorschlaege` | gewählte Vorschläge übernehmen `{ schluessel: ["u\|12\|05A", "e\|7\|07B"] }` | Admin |

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
Den `Api:PushKey` im Desktop unter „Online-Einstellungen…“ eintragen. Das Fenster zeigt, in welcher Datei die
Einstellungen stehen (`vertretung_online.json` neben der geöffneten Plandatei – jede Kopie der Plandatei hat ihre
eigene), mit Knopf „Ordner öffnen“. Bei „Server 401“ bzw. „Server 404“ nennt die Fehlermeldung diesen Pfad.

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
(vergeben sind „Grundlinie“, – seit U-1a, Raum im Grundplan und Wechsel-Merker der Vertretungen –
`U1a_RaumUndWechsel`, – seit 5.2b-1, Gültigkeit der Zugehörigkeiten und Felder für den SchILD-Import –
`SchildImport` und – seit 5.2c-1, Kennung des Schlüssels im Importprotokoll, Klassenleitung, Tabelle `Lehrkraefte` –
`SchildAbgleich`, – seit P3 – `Einstellungen`, – seit P6a – `Datenschutz` und – seit P5a, Tabelle `Schuljahre` und
`Benutzer.ZustaendigSchuljahr` – `Schuljahre`). Den Ordner `Data/Migrations` danach ins Repo übernehmen.
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
vergessen“. Seit P6a setzt der Befehl auch den **zweiten Faktor** zurück (Handy des einzigen Admins verloren); im
Echtbetrieb wird er nach dem eigenen Passwort neu eingerichtet.

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
  Kooperationskurs, `#Klasse`-Paar, Status 0/1/3/6/9, seit 5.2a-2 ein Projektkurs ohne Gegenstück im Plan) zeigt das
  Programm nach dem Erzeugen an.
  **`PSEUDODATEN.json`** trägt die SHA-256-Prüfsumme jeder Datei: Nur wenn alle stimmen und keine weitere
  `.dat` im Ordner liegt, gilt er als Pseudodaten. Ein Ordner mit fremden `.dat`-Dateien wird nie überschrieben.
- **Formatprofil:** Standard = amtliche Beschreibung. Weicht ein echter Export ab, mit „Formatprofil als Datei
  anlegen…“ die Vorlage `schild_profil.json` neben die Plandatei legen und dort nur Dateinamen,
  Spaltenüberschriften, Trennzeichen oder Zeichensatz (`auto` | `utf-8` | `windows-1252`) ändern – ohne
  Neukompilieren.
- **Echte Exporte** nur auf dem Verwaltungsrechner ablegen (nicht in OneDrive/Teams) und nach Gebrauch löschen.
  `*.dat` steht in der `.gitignore` (greift beim Hochladen im Browser nicht!).
- **Zuordnung (5.2a-2), Reiter „Zuordnung“:** ordnet dem Plan zu – Oberstufenkurse (`Kurse.dat`, EF/Q1/Q2) ↔
  Plan-Kurs (`MG3_Q2_L11`), Sek-I-Kurse ↔ Sek-I-Gruppe (`PP3_06A06B06D_L85`), Sek I ohne Kurs über Klasse + Fach +
  Fachlehrer (05A + ER + L88 → `ER_05A05B05C05D_L88`; sonst Klassenunterricht), Lehrkräftekürzel und Fächer (ein
  SchILD-Fach kann mehreren Plan-Fächern entsprechen, z. B. `PH` = `P` + `PH`). Automatik je Kurs: Stufe/Jahrgang →
  Fach → Kursart → Lehrkraft (Kursleiter, Zusatzkräfte); passen mehrere, entscheiden nacheinander der **Stundenplan der
  Teilnehmer** (niemand sitzt zur selben Zeit in zwei Kursen), die **Nummer** in der Kursbezeichnung („D-G2“ → 2), bei
  Sek-I-Kursen die **Klassen der Teilnehmer**, zuletzt das übrig gebliebene Paar (nur, wenn es zum Stundenplan der
  Teilnehmer passt – sonst bleibt der Kurs offen). Kürzel/Fächer, die es nur auf einer
  Seite gibt, werden **vorgeschlagen** und gelten erst nach „Vorschläge übernehmen“ (Warnung, solange die Zuordnung
  davon abhängt; sonst Hinweis). Zustände: automatisch · von Hand · keine Zuordnung · Vorschlag · offen · Konflikt ·
  veraltet. Zusätzliche Prüfungen: Teilnehmer passen zur Gruppe/Stufe, **Zeitkonflikte** (ein Schüler in zwei
  zugeordneten Kursen zur selben Stunde – beide Zeilen stehen im Reiter „Zuordnung“ mit „⚠ Zeitkonflikt“ und gelten als
  offen), Plan-Kurse ohne SchILD-Kurs, Klassenleitungen ohne Kürzel im Plan.
  Zuordnungsprobleme sind Warnungen, nie Fehler; Meldungen nennen nur Kurs-/Klassen-/Fachbezeichnungen und Kürzel.
  **Festlegen von Hand:** Zeile wählen → „Festlegen auf“ → „Festlegen“ (wirkt sofort im Prüflauf); erst
  **„Zuordnungen speichern“** schreibt die Datei **`schild_zuordnung.json`** neben die Plandatei (für Pseudodaten
  **`schild_zuordnung_pseudo.json`**, damit Testfestlegungen nie in die echte Zuordnung geraten). Sie enthält nur
  Festlegungen von Hand – Kürzel, Fächer, Kurs- und Klassenbezeichnungen, keine Personendaten; Kurse gelten je Schuljahr +
  Jahrgang + KursBez, `"Plan": null` = bewusst keine Zuordnung. Beim Schließen bzw. neuen Prüflauf fragt das Fenster
  nach ungespeicherten Festlegungen. Ohne lesbare Schülergruppen in der Plandatei (Excel offen) ist keine Zuordnung möglich.
  Der Bericht enthält die Zahlen der Zuordnung und abweichende Kürzel/Fächer.
- **Nicht gelesene Dateien (seit 5.2a-2):** Eine Datei mit fehlerhafter Kopfzeile steht in der Übersicht als „nicht
  gelesen – Kopfzeile fehlerhaft“ (bzw. „Datei leer“, „nicht lesbar“) und zählt für alle Prüfungen als fehlend. Statt
  vieler Folgemeldungen erscheint ein Hinweis, z. B. „Weitere Prüfungen der Schülerdaten entfallen, weil
  SchuelerBasisdaten.dat nicht gelesen werden konnte.“ Klassen in SchILD ohne führende Null („5a“) werden den Klassen im
  Plan („05A“) zugeordnet.
- **Gemeinsam unterrichtete Kurse (5.2a-3, Entscheidung 136):** Ein Plan-Kurs darf mehrere SchILD-Kurse haben (Sport
  oder Projektkurs Q1+Q2, Kooperationskurs). Im Reiter „Zuordnung“ beim weiteren SchILD-Kurs unter „Festlegen auf“
  **„⇄ gemeinsam mit „SP-G3“ (Q1) → SPG3_Q1_L6 …“** wählen – angeboten werden Plan-Kurse aller Stufen, die schon einen
  SchILD-Kurs haben und bei Fach oder Lehrkraft passen; „⇄ ✓“ = Fach, Kursart, Lehrkraft und Stundenplan der Teilnehmer
  passen. Status „von Hand · gemeinsam“, Begründung „gemeinsam mit …“; Teilnehmer aus dem eigenen Jahrgang des SchILD-Kurses
  gelten dann als passend. Passt ein offener SchILD-Kurs nur zu einem schon vergebenen Plan-Kurs, schlägt die Automatik
  „gemeinsam“ vor (Warnung, bestätigen per „⇄ …“ oder „Vorschläge übernehmen“). Zwei SchILD-Kurse **desselben Jahrgangs**
  von Hand auf einem Plan-Kurs **ohne** „gemeinsam“ sind ein **Konflikt** (beide Zeilen; vorher gab es dafür keine
  Meldung). Gemeinsam festgelegte Kurse nehmen dem eigentlichen SchILD-Kurs seinen Plan-Kurs nicht weg. In der Datei:
  `"Gemeinsam": true` an der Kurs-Festlegung. In 5.2b werden die Teilnehmer aller zugeordneten SchILD-Kurse Teilnehmer
  des Plan-Kurses.
- **Muster der Kursbezeichnung (5.2a-3, Entscheidung 137):** Feld oben im Reiter „Zuordnung“, z. B. `{Fach}-{Art}{Nr}`
  („D-G2“), `{Fach} {Art}{Nr}` („D GK1“), `{Jg}-{Fach}-{Art}{Nr}` („Q1-D-G2“). Platzhalter `{Fach}` (Fach laut Kurse.dat,
  sonst Buchstaben), `{Art}` (Buchstaben), `{Nr}` (Ziffern, Pflicht), `{Jg}` (Stufe/Jahrgang), `{*}` (beliebiger Text);
  Leerzeichen = ein oder mehrere Leerzeichen, Groß/klein egal, die ganze Bezeichnung muss passen; mehrere Muster mit `;`.
  Die **Vorschau** zeigt beim Tippen drei Kurse aus dem Export (ein nicht passender zuerst) und „passt bei x von y“.
  „Anwenden“ (oder Enter) → gilt sofort im Prüflauf; gespeichert als `"KursMuster"` mit „Zuordnungen speichern“. Leer =
  Automatik (erste Ziffernfolge nach dem Fach – liest bei „Q1-D-G2“ die 1). Kurse, die nicht passen → Hinweis, dort gilt
  die Automatik. Übersicht/Bericht: Zeile „Muster der Kursbezeichnung“.
- **Pseudo-Export seit 5.2a-3:** Option „Kursbezeichnungen der Oberstufe“ („D-G1“ · „D GK1“ · „Q1-D-G1“) und ein
  zusätzlicher Stolperstein: ein Q1-Kurs (Sport zuerst), der mit einigen Q2-Schülern gemeinsam unterrichtet wird und in
  SchILD einen eigenen Q2-Kurs hat (erwartet: Vorschlag „gemeinsam mit …“). Ein Standard-Pseudo-Export hat damit vor dem
  Übernehmen 4 statt 3 Warnungen.
- Danach **5.2b** Import.

## Gültigkeit der Zugehörigkeiten (Phase 5.2b-1, API)

Vorbereitung des SchILD-Imports (Vorschlag und Entscheidungen 163–171: `claude/5.2b_Vorschlag.md` im Claude-Projekt).
Nur API, keine Desktop- und keine PWA-Änderung.

- **Schema** (Migration `SchildImport` für PostgreSQL; SQLite rüstet es beim Start nach): `Zugehoerigkeiten` +
  `GueltigVon`, `GueltigBis` (jeweils einschließlich, leer = ab Schuljahresbeginn bzw. offen) und `Quelle`
  (`Hand` / `Seed` / `SchILD`); der bisher **eindeutige** Index auf (Schüler, Lerngruppe, Schuljahr) ist jetzt
  nicht eindeutig und enthält `GueltigVon`. `Schueler` + `Status`, `AbgangAm`, `ExtIdSchild`, `ExtIdVornameGeb`,
  `ExtIdNachnameGeb`, `ImportAm` (`ExtId` = Prüfsumme aus Nachname + Vorname + Geburtsdatum, keine SchILD-Id).
  `Lerngruppen` + `Quelle`, `SchildAb`. Neue Tabelle `SchildImportProtokolle` (ohne Namen; geschrieben ab 5.2b-2).
- **Schema-Nachrüstung (SQLite):** ergänzt jetzt auch fehlende, nicht eindeutige Indizes auf vorhandenen Tabellen und
  ersetzt den alten eindeutigen Index der Zugehörigkeiten (Log „Schema nachgerüstet: Index … entfernt (ersetzt)“ /
  „Index … ergänzt“). `vertretung.db` muss nicht gelöscht werden.
- **Gültigkeit in allen Abfragen** (`Services/Gueltigkeit.cs`): Teilnehmer im Stundenblatt, Schülerplan,
  Hausaufgaben der Schüler, Soll/Fehlquote nach dem **Tag der Stunde**; Listen einer Klasse über einen Zeitraum
  (Fehlzeiten, offene Fälle, Einträge, CSV-Export) zeigen, wer im Zeitraum zur Klasse gehörte, **mit den Daten aus der
  Zeit der Zugehörigkeit**; Klasse eines Schülers, Konten (Sammelanlage, Zuständigkeit), Gruppenübersicht und
  Lerngruppen-Listen nach dem **heutigen** Stand. Wer in einer Stunde eine Anwesenheit hat, bleibt in ihrer Liste.
  Ohne Datum (Seed, Gruppenlisten der Lehrkräfte, alles vor 5.2b) ändert sich nichts.
- **Sek-I-Gruppen aus SchILD (81):** Ab `SchildAb` lässt sich die Gruppenliste nicht mehr bearbeiten (nur ansehen;
  Speichern → „Diese Gruppe kommt seit … aus SchILD …“). Die Liste der Fachlehrkraft bleibt ohne Datum; Entfernen
  löscht wie bisher.
- **CSV-Export:** Die Spalte „SchILD-Id“ bleibt leer (die API kennt nur die Prüfsumme; Rückweg nach SchILD: 5.3).
- **Wechsel zum Ausprobieren (nur Entwicklung):** `POST /api/seed/pseudodaten?wechsel=1` (optional
  `&stichtag=2026-11-16`, Standard = Montag der laufenden Woche) legt nach den Pseudodaten an: 2 Klassenwechsel,
  2 Kurswechsel in der Oberstufe, 1 Abgang, 1 Neuaufnahme und 1 Sek-I-Gruppe, die bis zum Vortag eine Liste „von Hand“
  hat und ab dem Stichtag aus SchILD kommt. Die Antwort nennt unter `wechsel.aenderungen` jeden Wechsel mit Namen und
  Id (Pseudodaten); mit `| ConvertTo-Json -Depth 4` anzeigen. Die Testkonten `s05A`/`sQ1` sind nicht betroffen.

## SchILD-Import (Phase 5.2b-2a, API)

Der Desktop liest die SchILD-Dateien auf dem Verwaltungsrechner und schickt nur, was der Server braucht (Entscheidung
166): je Schüler HMAC-Prüfsummen (`extId` aus Nachname + Vorname + Geburtsdatum, `extIdSchild` aus der SchILD-ID, zwei
Teil-Prüfsummen für 5.2c), Name, Jahrgang, SchILD-Status, bei Status 0 das Aufnahmedatum und die **Plan-Schlüssel** der
Klasse und der zugeordneten Kurse/Gruppen. **Nie:** Geburtsdatum, SchILD-ID im Klartext, Noten, Kursart des Schülers,
Status 1 (Warteliste); von den Lehrkräften seit 5.2c-2 nur Klassenleitung (Kürzel) und Kürzel, Nachname, Vorname der
Lehrkräfte des Plans. Den Reiter „Import“ im Desktop liefert Schritt b-2b; bis dahin lässt sich der Weg
mit fertigen Testdateien prüfen. Vorschlag und Entscheidungen: `claude/5.2b_Vorschlag.md` im Claude-Projekt.

| Methode | Pfad | Zweck | Auth |
|---|---|---|---|
| GET | `/api/schild/status?schuljahr=..&abschnitt=..` | Betriebsart, Kalender, Grundpläne, Schülerzahlen, letzter Import, Vorschlag für „Änderungen gelten ab“ | `X-Api-Key` |
| POST | `/api/schild/import` | Probelauf (`probelauf: true`, Standard) bzw. Import (`probelauf: false`, `vorschau`, `abgaenge`) | `X-Api-Key` |
| POST | `/api/schild/seed-entfernen` | nur Entwicklung: alle Pseudo-Schüler samt Klassenbuchdaten entfernen (wie ein neuer Seed) | `X-Api-Key` |
| GET | `/api/schild/protokolle?limit=` | seit 5.2c-2: Importprotokoll für die App (ohne Namen) | Admin (Anmeldung, nicht `X-Api-Key`) |

- **Probelauf = Import ohne Speichern** (78): dieselbe Rechnung; die Antwort enthält Zahlen, je Schüler eine Zeile je
  Kategorie (neu · Name bzw. Geburtsdatum geändert · Klassenwechsel · Kurs-/Gruppenwechsel · Status/Jahrgang geändert ·
  Abgang laut SchILD (vorausgewählt) · fehlt im Export (nicht vorausgewählt)), unbekannte Plan-Schlüssel, neue
  Lerngruppen, Kurse/Gruppen, die ab „gilt ab“ aus SchILD kommen, Sperren, Hinweise, einen **Bericht ohne Namen** und
  die **Prüfsumme der Vorschau** (Datenbankstand + Nutzlast). Der Import rechnet neu und bricht mit 409 und neuer
  Vorschau ab, wenn sich die Prüfsumme geändert hat.
- **Erkennung:** zuerst `extIdSchild`, dann `extId`; eine andere SchILD-ID heißt „andere Person“. Ohne SchILD-ID
  erscheint ein geänderter Name als „neu“ + „fehlt im Export“ mit dem Hinweis „möglicherweise dieselbe Person“
  (Zusammenführen kommt mit 5.2c).
- **Schreiben** (eine Transaktion, 169): Wechsel enden am Vortag von „gilt ab“, neue Zeilen beginnen dann (Neuaufnahme:
  am späteren Aufnahmedatum); beginnt eine alte Zeile erst an „gilt ab“ oder später, wird sie ersetzt; unveränderte
  Zeilen bleiben. Fehlende Lerngruppen werden angelegt (`Quelle = SchILD`, `SchildAb`). **Gruppen ab „gilt ab“ aus
  SchILD (81):** die Liste von Hand endet am Vortag, die Gruppe ist danach im Gruppeneditor nur lesbar; eine Sek-I-Gruppe
  **ohne** Liste von Hand bekommt ihre Liste ab Schuljahresbeginn (genauer: seit dem Eintritt in die jetzige Klasse).
  Nicht zugeordnete Gruppen bleiben bearbeitbar. Unbekannte Plan-Schlüssel werden übersprungen; vorhandene Zeilen dazu
  bleiben. Status 6 (extern) = nur Kurse.
- **Abgänge nur nach Bestätigung** (80, E): `abgaenge` = Ids aus der Vorschau. Alle offenen Zeilen enden am Vortag,
  Status „abgang“, das **Schülerkonto wird gesperrt** (Kontenprotokoll „Abgang laut SchILD-Import“; der Admin kann
  entsperren). Status 8/9 von Schülern, die nicht in der Datenbank sind, werden nicht angelegt.
- **Sperren** (168): Pseudo-Kennzeichen passt nicht zur Umgebung (Entwicklung nur Pseudodaten, Echtbetrieb nur echte),
  echte und Pseudo-Schüler gemischt, Seed-Schüler in der Datenbank (Entwicklung: „Seed-Schüler entfernen“), kein
  Kalender/Grundplan am „gilt ab“ bzw. „gilt ab“ außerhalb des Schuljahrs, „gilt ab“ vor dem Stichtag des letzten
  Imports, mehr als 10 % unbekannte Plan-Schlüssel, doppelte Prüfsummen, Teil-Export (ganze Klasse bzw. ganzer Jahrgang
  fehlt oder mehr als ein Viertel der Schüler).
- **Importprotokoll** (Tabelle `SchildImportProtokolle`): Zeit, „Desktop (Windows-Benutzer)“, Schuljahr, Abschnitt,
  „gilt ab“, Pseudo, Zahlen, Prüfsumme des Datenbankstands – ohne Namen. Das Log nennt nur Zahlen.
- **Keine Schemaänderung** (das Schema kam mit 5.2b-1).

## SchILD-Import im Desktop (Phase 5.2b-2b-1)

Im Vertretungs-Wocheneditor (Button 24) → „SchILD-Daten…“ hat das Fenster (jetzt „SchILD-Daten prüfen und importieren“)
den Reiter **„Import“**. Code: `src/Vertretung/Schild/SchildDialog_Import.cs`, `SchildNutzlast.cs`, `SchildSchluessel.cs`,
`SchildImportClient.cs`; Verbindungsmeldungen `src/Vertretung/OnlineVerbindung.cs`. Keine API- und keine Schemaänderung.

- **Voraussetzungen:** Prüflauf ohne Fehler, Zuordnung möglich und gespeichert, Abschnitt im Schuljahr des Wocheneditors.
  Server-Adresse und API-Key wie beim Veröffentlichen (`vertretung_online.json`, Knopf „Online-Einstellungen…“).
- **„Vorschau berechnen“:** fragt zuerst `GET /api/schild/status` ab. **Passt die Art der Daten nicht zur API
  (Entwicklung nur Pseudodaten, Echtbetrieb nur echte), sendet der Desktop nichts** – echte Namen gehen nie an eine
  Entwicklungs-API; echte Daten nur über HTTPS (außer an `localhost`). „Änderungen gelten ab“ = Vorschlag der API, solange
  nicht von Hand gewählt. Dann Probelauf mit der Nutzlast (Entscheidung 166): je Schüler `extId`, `extIdSchild` (falls
  `SchuelerLernplattformen.dat` die SchILD-ID enthält), die zwei Teil-Prüfsummen, Name, Vorname, Jahrgang, Klasse (Sek I),
  Status, Aufnahmedatum bei Status 0, Plan-Schlüssel der Kurse/Gruppen; dazu `planSchluessel` = alle zugeordneten
  Plan-Kurse/-Gruppen und die Sek-I-Klassen, die es im Plan und in `Klassen.dat` gibt. **Übertragen werden nur feste
  Zuordnungen** (automatisch, ohne Lehrkraft, von Hand) ohne Zeitkonflikt; offene, Vorschläge und Konflikte nennt die
  Vorschau unter „nicht übertragen“ (Punkt H). Status 1 (Warteliste) wird nie übertragen.
- **Vorschau:** links der Bericht (ohne Namen) mit Sperren und „Was tun?“, rechts die Zeilen je Kategorie (Ansicht
  wählbar); „Abgang laut SchILD“ vorausgewählt, „fehlt im Export“ nicht; „Alle in der Ansicht bestätigen“/„Keine in der
  Ansicht“. **„Importieren…“** fragt mit allen Zahlen nach und schickt dieselbe Nutzlast mit der Prüfsumme der Vorschau und
  den bestätigten Ids; bei veralteter Vorschau zeigt das Fenster die neue (Bestätigungen bleiben erhalten).
- **HMAC-Schlüssel** (170, F): 32 Byte Zufall, beim ersten Import gespeichert unter
  `%LOCALAPPDATA%\Stundenplan\schild_schluessel.dat` (Pseudodaten: `schild_schluessel_pseudo.dat`), geschützt mit DPAPI
  für **diesen Windows-Benutzer** (direkt über `crypt32.dll`, kein Zusatzpaket); nie neben der Plandatei (OneDrive), nie
  still überschrieben. **Kennung** (`1A2B-3C4D`) zum Vergleichen. Eingabe der Prüfsummen: getrimmt, Leerzeichen
  zusammengefasst, NFC, Großbuchstaben, Nachname ohne `#Klasse`, Geburtsdatum `JJJJ-MM-TT`, je Art ein Präfix; Ausgabe
  Base64 von HMAC-SHA256. Fehlt der Schlüssel, obwohl schon importierte Schüler in der Datenbank stehen, sendet der
  Desktop nichts („Schlüssel einlesen…“).
- **„Schlüssel sichern…“ / „Schlüssel einlesen…“:** Datei `*.schluessel` (JSON) mit Kennwort (mind. 12 Zeichen;
  PBKDF2-SHA256 mit 600 000 Runden, AES-256-GCM, Kennung und Art als Zusatzdaten). Warnung bei einem Cloud-Ordner
  (OneDrive, SharePoint …). Einlesen ersetzt einen vorhandenen anderen Schlüssel nur nach Rückfrage.
- **„Seed-Schüler entfernen…“** (nur Entwicklung, sichtbar, solange Pseudo-Schüler in der Datenbank stehen): ruft
  `POST /api/schild/seed-entfernen` und berechnet die Vorschau neu.
- **Verbindungsfehler** (P1, gemieteter Server): Proxy (auch mit Windows-Anmeldung), Firewall, DNS, Zertifikat,
  Zeitüberschreitung, 403/407/502–504 werden verständlich erklärt – auch beim Veröffentlichen von Grundplan und
  Vertretungen (`OnlineSync.FehlerText`).
- **Datenschutz:** Die Vorschau (mit Namen) bleibt im Fenster und wird beim Schließen verworfen; „Bericht kopieren“ und das
  Log nennen nur Zahlen, Kurs- und Gruppenbezeichnungen. Der Schlüssel wird nie angezeigt, gesendet oder geloggt.
- **Spalten der Vorschau** (5.2b-2b-2): schmalere feste Spalten mit Umbruch; „Änderung“ und „Hinweis“ teilen sich den Rest.

## Folgestand erzeugen (Phase 5.2b-2b-2, Desktop, nur Pseudodaten)

Knopf **„Folgestand erzeugen…“** unten im Fenster „SchILD-Daten prüfen und importieren“ (Entscheidung 171). Code:
`src/Vertretung/Schild/SchildFolgestand.cs`, `SchildDialog_Folgestand.cs`. Keine API- und keine Schemaänderung.

- **Voraussetzung:** ein Ordner mit gültiger Pseudo-Markierung (`PSEUDODATEN.json`, alle Prüfsummen stimmen), Prüflauf ohne
  Fehler, Zuordnung möglich. Echte Exporte werden weder gelesen noch verändert.
- **Ergebnis:** neuer Ordner neben dem Ausgangsordner, `<Name>_Folge1` (dann `_Folge2` …; aus `…_Folge1` wird `…_Folge2`),
  mit neuer Markierung (Nummer, Ausgangsordner, Änderungen nur als Zahlen) und `FOLGESTAND.txt` (Änderungen mit
  Pseudo-Namen und erwartete Zahlen der Vorschau). Geändert werden nur `SchuelerBasisdaten.dat`,
  `SchuelerLeistungsdaten.dat` und `SchuelerLernplattformen.dat`; alle anderen Dateien und alle übrigen Zeilen bleiben
  byte-gleich.
- **Änderungen** (einzeln abwählbar, Startwert 171 → wiederholbar): 3 Abgänge (Status 9; der erste ist der erste Schüler
  der ersten Klasse in der Datei, in der Entwicklung meist mit dem Testkonto `s05A` verknüpft), 2 Klassenwechsel Sek I
  (Leistungsdaten wie ein Mitschüler der neuen Klasse), 3 Kurswechsel Oberstufe (gleiches Fach, Grundkurs, gleiche Stufe,
  ohne Überschneidung laut Stundenplan), 1 Gruppenwechsel Sek I (parallele Gruppe derselben Klasse), 1 Namensänderung und
  1 korrigiertes Geburtsdatum (beide mit SchILD-ID), 2 Neuaufnahmen (Status 0, Aufnahme in zwei Wochen; Sek I und EF),
  1 Schüler fehlt ohne Abgang; wahlweise **eine ganze Klasse fehlt** (die Vorschau muss mit „Teil-Export“ sperren).
- **Zuordnung bleibt gleich:** benutzt werden nur feste, eindeutige Zuordnungen (bzw. von Hand), jede Liste behält genug
  Teilnehmer, und neue Zeilen der Leistungsdaten sind Kopien der Zeilen eines anderen Schülers.
- **Erwartete Vorschau:** Das Fenster nennt die Zahlen, die die Vorschau zeigen sollte, wenn genau der Ausgangsordner
  importiert ist (alle Abgänge bestätigt) – Kurs-/Gruppenwechsel enthält auch Klassenwechsel, die die Gruppen ändern.
- **Datenschutz:** Pseudo-Namen nur im Fenster und in `FOLGESTAND.txt`; das Log nennt nur Zahlen.
- **Abschnitt 2 (P4a, Entscheidung 207):** eigene Art von Folgestand (oben im Fenster, nur aus Abschnitt 1; die übrigen
  Häkchen gelten dann nicht): dieselben Schüler, Abschnitt 2 in Basis- und Leistungsdaten, `Kurse.dat`, `Klassen.dat` und
  `KurseLehrkraefte.dat`; 2 Klassenwechsel Sek I, 3 Kurswechsel EF, 2 Abwahlen (Grundkurs) in Q1/Q2, 3 Wechsel zwischen
  Religion/PP-Gruppen, 2 zwischen anderen parallelen Sek-I-Gruppen (WP), 1 Abgang (Status 9), 1 Neuaufnahme zum
  2. Halbjahr (Status 2, Aufnahme 1. Februar). `FOLGESTAND.txt` nennt die erwarteten Zahlen.

## SchILD-Abgleich: „dieselbe Person?“ und Neu-Zuordnung (Phase 5.2c-1)

Entscheidungen 185–198 (`claude/5.2c_Vorschlag.md`, Claude-Projekt). API: `Services/SchildImportService.cs`
(`PaareBilden`), `Controllers/SchildController.cs`; Desktop: `src/Vertretung/Schild/SchildDialog_Import.cs`,
`SchildNutzlast.cs`, `SchildSchluessel.cs`, `SchildFolgestand.cs`. **Schemaänderung** (Migration `SchildAbgleich`):
`SchildImportProtokolle` + `SchluesselKennung`, `NeuZuordnung`, `KennungVorher`, `Zusammengefuehrt`, `NeuZugeordnet`;
`Lerngruppen` + `Klassenleitung`; neue Tabelle `Lehrkraefte` (beides genutzt ab 5.2c-2).

- **„Dieselbe Person?“:** Ein Schüler des Exports ohne Treffer und ein Schüler der Datenbank, der im Export fehlt, bilden
  ein Paar, wenn die Teil-Prüfsumme Vorname + Geburtsdatum (Nachname geändert) oder Nachname + Geburtsdatum (Vorname
  korrigiert) gleich ist oder der Name in derselben Klasse bzw. demselben Jahrgang gleich ist (Geburtsdatum korrigiert).
  Verschiedene SchILD-IDs schließen aus. **Vorausgewählt** nur Teil-Prüfsumme + gleiche Klasse/Jahrgang + eindeutig.
  **Zusammenführen** = der bisherige Datensatz bekommt Name und Prüfsummen aus dem Export, danach der normale Abgleich
  (Kategorie „zusammengeführt“, ggf. Klassen- bzw. Kurswechsel); Konto und Klassenbuchdaten bleiben. Die Nutzlast trägt
  die Wahl als `paare: [{ id, extId }]` (`null` = Vorauswahl der API); die angewendeten Paare gehören zur Prüfsumme der
  Vorschau. Im Desktop: Häkchen ändern → „Vorschau berechnen“ → erst dann „Importieren…“.
- **Kennung des Schlüssels** („1A2B-3C4D“, verrät ihn nicht) in jeder Nutzlast (`kennung`) und im Importprotokoll;
  `GET /api/schild/status` nennt `kennungLetzterImport`. Ein anderer Schlüssel als beim letzten Import derselben Art
  sperrt (Desktop schon vor dem Senden, API mit der Sperre `Schluessel`).
- **Neu-Zuordnung** (Schlüssel verloren, Entscheidung 72/190): Knopf „Schlüssel verloren – neu zuordnen…“ (nur sichtbar,
  wenn importierte Schüler da sind und der Schlüssel fehlt, nicht lesbar ist oder nicht passt; bei echten Daten doppelte
  Rückfrage). Nutzlast mit `neuzuordnung: true`; Sperre, wenn ein Schüler über die Prüfsummen erkannt wird oder die
  Kennung zum letzten Import passt. Paare über Name + Klasse (Oberstufe: Jahrgang): eindeutig = vorausgewählt;
  Namensgleiche und „gleicher Name, andere Klasse“ nur von Hand (mit Vergleich der Kurse/Gruppen). Zugeordnete Datensätze
  bekommen alle Prüfsummen des neuen Schlüssels; eine nicht lesbare alte Schlüsseldatei wird beim Import umbenannt
  (`…_ersetzt_<Datum>.dat`), nie gelöscht. Danach „Schlüssel sichern…“.
- **Folgestand** (Testdaten, Standard aus): „Änderungen ohne SchILD-ID“ (die geänderten Schüler fehlen in
  `SchuelerLernplattformen.dat`; ganz ohne SchILD-ID sperrt das „#Klasse“-Paar zu Recht), „Vorname korrigiert“,
  „Nachname geändert + Klassenwechsel“, „Neuaufnahme mit demselben Namen wie ein Mitschüler“ (Namensgleiche).
- **Datenschutz:** Bericht, Importprotokoll und Log ohne Namen; Namen nur in den Zeilen der Vorschau im Fenster.

## Konten und Anzeigen aus SchILD (Phase 5.2c-2)

Entscheidungen 191–197 (`claude/5.2c_Vorschlag.md` §5–§7). API: `Services/SchildImportService.cs`
(`KlassenleitungVormerken`, `LehrkraefteVormerken`, Gruppen der Neu-Zuordnung), `Controllers/KontenController.cs`,
`Controllers/SchildController.cs` (`protokolle`), `Services/KlassenbuchService.cs` (Gruppen „aus SchILD“); Desktop:
`SchildNutzlast.cs`, `SchildZuordnung.cs` (`KuerzelFest`), `SchildDialog_Import.cs`, `SchildFolgestand.cs`; PWA:
`konten.js`, `klassenbuch.js`, `app.js`, `hilfe.js`. **Keine Schemaänderung** (Spalten und Tabelle kamen mit
`SchildAbgleich`).

- **Klassenleitung (191):** Der Desktop schickt je Sek-I-Klasse des Plans `klassenleitungen: [{ klasse, kuerzel }]` laut
  `Klassen.dat` – übersetzt über die feste Kürzeltabelle der Zuordnung (von Hand bzw. gleich, keine Vorschläge); Kürzel
  ohne festes Gegenstück gehen nicht mit (Bericht). Die API speichert sie an der Klasse (`Lerngruppe.Klassenleitung`);
  Klassen ohne Angabe behalten ihren Wert. Änderungen erscheinen als Hinweis („Klassenleitung 07B: L23 → L41“), die Konten
  ändern sich nicht.
- **Vorschläge aus SchILD (192):** PWA → Konten verwalten → Reiter „SchILD“ → „Vorschläge“: Lehrkraft-Konto einer
  Klassenleitung → „Übernehmen“ (Rolle Klassenleitung bzw. Klasse dazu); Klassenleitungs-Konto für eine Klasse, die laut
  SchILD jemand anderes leitet → „Zuständigkeit entfernen“ (nur einzeln – Stellvertretungen von Hand bleiben möglich);
  Verwaltung/Stufenkoordination → nur Hinweis; ohne Konto → Verweis auf die Sammelanlage. Nichts automatisch; jede
  Änderung im Kontenprotokoll, neuer Sicherheitsstempel. „Alle übernehmen“ nimmt nur die Vorschläge „Übernehmen“.
  Konten ohne Kürzel bleiben unberührt. Sammelanlage Lehrkräfte: Häkchen „Klassenleitung laut SchILD gleich eintragen“.
- **Namen der Lehrkräfte (193/194):** `lehrkraefte: [{ kuerzel, nachname, vorname }]` nur für Kürzel des Plans (der
  Desktop filtert, die API noch einmal über den Grundplan); mit `Lehrkraefte.dat` wird die Liste dieser Art ganz ersetzt
  (Tabelle `Lehrkraefte`), ohne Datei bleibt sie. Sichtbar nur für den Admin (Konten: Liste, Suche, Detail, Sammelanlage)
  und die Lehrkraft selbst (Kontomenü „L23 · Anna Schulz“, Feld `name` der Anmeldung). Schüler sehen weiter nur Kürzel.
- **Gruppeneditor (195):** `KbGruppeDto`/`KbGruppeKurzDto` + `ausSchild`, `schildAb`; Text „Diese Liste kommt seit …
  aus SchILD …“, Marke „aus SchILD“ in „Gruppenlisten“/„Meine Gruppen“.
- **SchILD-Importe (196):** `GET /api/schild/protokolle?limit=` – nur Admin mit Anmeldung (JWT, nicht über den
  X-Api-Key), ohne Namen; PWA → Reiter „SchILD“ → „SchILD-Importe“.
- **Konten-Hinweise (197):** Filter `wiederaktiv` (Schülerkonto gesperrt, laut SchILD aktiv – z. B. Wiedereintritt) und
  `namegeaendert` (Benutzername nach dem Muster `vorname.nachname` passt nicht mehr zum Namen laut SchILD); im Detail
  „Umbenennen in …“ (Admin, über `PUT /api/konten/{id}`, das Passwort bleibt) und „Gesperrt durch“.
- **Neu-Zuordnung, Namensgleiche:** Zeilen einer Gruppe stehen untereinander (`gruppe`); je Gruppe ein Vorschlag
  (`vorschlag`: passender Eintritt, dann die meisten gemeinsamen Kurse, dann gleiche Klasse – nur, wenn eindeutig).
  Desktop: Knopf „Vorschlag übernehmen…“ setzt die Häkchen, danach „Vorschau berechnen“.
- **Folgestand** (Standard aus): „Klassenleitung wechselt“ (eine Klasse in `Klassen.dat`, möglichst zu „L2“) und
  „Lehrkraft heißt anders“ (ein Nachname in `Lehrkraefte.dat`, möglichst „L1“).
- **`seed-konten`** setzt Rolle, Kürzel und Zuständigkeit der Standard-Testkonten wieder auf den Standard (Passwort bleibt).

## Schuleinstellungen (P3, E-1)

Entscheidungen 138–141 (`claude/Einrichtung_Vorschlag.md`) und 199–203 (`claude/E-1_Vorschlag.md`). API:
`Model/Einstellungen.cs`, `Services/Schuleinstellungen.cs` (Katalog, Zwischenspeicher, Übernahme),
`Services/Aufraeumdienst.cs`, `Controllers/EinstellungenController.cs`, `Helpers/PwaManifest.cs`; PWA:
`einstellungen.js`, `app.js`, `hilfe.js`. **Migration `Einstellungen`** (Tabellen `Einstellungen`,
`EinstellungsProtokolle`, sieben Spalten in `SchildImportProtokolle`).

- **Zwei Orte (139):** Was vor dem Start feststehen muss (Datenbank, Schlüssel, Adresse, Proxy, HTTPS, CORS,
  `Woche:OhneLogin`), steht weiter in der Einstellungsdatei. Die Schulwerte stehen in der Tabelle `Einstellungen`:
  | Gruppe | Schlüssel | Standard (Startwert aus appsettings) |
  |---|---|---|
  | Schule | `Schule.Name`, `Schule.Kurzname` | leer bzw. „Stundenplan“ |
  | Schuljahr | `Schuljahr.Aktuell` | nach dem Datum, ab 1. August das neue (`DefaultSchuljahr`) |
  | | `Schuljahr.ErfassungAb`, `Schuljahr.ThemaAb`, `Schuljahr.HalbjahrAb` | erster Schultag laut Kalender, wie Erfassung, 1. Februar (`Klassenbuch:…`) |
  | Datenschutz | `Datenschutz.EintraegeLoeschenNachTagen` | 0 = aus (`Klassenbuch:EintraegeLoeschenNachTagen`) |
  | | `Datenschutz.GruendeLoeschenNachTagen` (P4a) | 0 = aus – Grund/Bemerkung veröffentlichter Vertretungen |
  | | `Datenschutz.AbgangKontenLoeschenNachTagen` (P4a) | 0 = aus – gesperrte Konten abgegangener Schüler |
  | | `Datenschutz.ProtokollTage` | 365 – Konten-, Download-, Einstellungs- und (seit P4a) SchILD-Importprotokoll (`Konten:ProtokollTage`) |
  | | `Datenschutz.BerichteNurAdmin` | aus (`Berichte:NurAdmin`) |
  | Anmeldung | `Anmeldung.SitzungStunden`, `Anmeldung.AngemeldetBleibenTage` | 12 Stunden, 30 Tage – 0 = kein „Angemeldet bleiben“ (`Jwt:…`) |
- **Übernahme (140):** Beim ersten Start legt `DatenbankStart` die Zeilen an – mit dem Wert aus appsettings, sonst
  Standard (Eintrag „Startwerte angelegt“ im Protokoll). Danach gilt die Datenbank.
- **Sofort wirksam:** `Schuleinstellungen` hält die Zeilen im Speicher; jede Änderung verwirft ihn. Sitzungsdauer und
  „Angemeldet bleiben“ wirken auf neue Anmeldungen.
- **Endpunkte (nur Admin, JWT):** `GET /api/einstellungen` (alle mit Hilfetext, Standard, „gilt jetzt“, Abstimmung),
  `PUT /api/einstellungen` `{ aenderungen: [{ schluessel, wert, abgestimmtAm?, abgestimmtMit? }], bestaetigt }` (400 mit
  `felder` = Felder prüfen; 409 mit `loeschen` = eine kürzere Frist löscht Daten → mit `bestaetigt: true`),
  `GET /api/einstellungen/protokoll?limit=`. Ohne Anmeldung (201): `GET /api/auth/optionen` liefert zusätzlich
  `schule { name, kurzname, app }` und `angemeldetBleibenTage`; `/manifest.webmanifest` erzeugt die API (Name der App).
- **Datenschutz (141):** Einstellungen mit Datenschutzwirkung zeigen die Frage an den DSB und „abgestimmt am / mit“.
  Ändert sich ihr Wert, entfällt die Abstimmung, wenn sie nicht im selben Schritt neu eingetragen wird. Eine kürzere
  Frist zählt vorher die betroffenen Datensätze (409) und räumt nach der Bestätigung sofort auf.
- **Aufräumen:** beim Start (wie bisher) und täglich um 3 Uhr (`Aufraeumdienst`); das Einstellungsprotokoll hat
  dieselbe Frist wie das Kontenprotokoll (202).
- **Prüfungen:** Schuljahr „2026/27“; Daten im Schuljahr laut Kalender (2. Halbjahr nach dem ersten Schultag); „Thema
  erfasst ab“ nicht vor „Anwesenheit erfasst ab“; Zahlen im erlaubten Bereich; Texte ohne Steuerzeichen.
- **PDF:** Name der Schule klein über dem Titel (Klassenbuch, Kursheft, Jahresarchiv).
- **SchILD-Importe (Kleinigkeit a):** `SchildImportProtokolle` + `KlassenleitungKlassen`, `KlassenleitungNeu`,
  `KlassenleitungGeaendert`, `Lehrkraefte`, `LehrkraefteNeu`, `LehrkraefteGeaendert`, `LehrkraefteEntfernt`; die Liste
  in der App zeigt sie (null = keine Angabe bzw. Import vor P3).
- **Desktop (Kleinigkeit c):** `GET /api/grundplan/veroeffentlicht?schuljahr=` (X-Api-Key) liefert Plan-Spalte und „gilt
  ab“ der Grundplan-Versionen; „Online veröffentlichen“, „Zeitraum veröffentlichen…“ und die SchILD-Vorschau warnen, wenn
  der Plan im Wocheneditor nicht zum veröffentlichten Grundplan passt.

## Jahresablauf, Halbjahr und Fristen (P4a, 5.3a)

Entscheidungen 205–209 und 221 (`claude/5.3_Vorschlag.md`). **Keine Schemaänderung** (Einstellungen sind Schlüssel/Wert).
API: `Helpers/Testdatum.cs`, `Services/Jahresablauf.cs`, `Controllers/JahresablaufController.cs`, `Dtos/JahresablaufDtos.cs`,
`Services/Aufraeumdienst.cs`, `Services/Schuleinstellungen.cs`, `Controllers/EinstellungenController.cs`,
`Controllers/SchildController.cs`, `Services/SchildImportService.cs`; PWA: `jahresablauf.js`, `einstellungen.js`, `app.js`,
`fehlzeiten.js`, `hilfe.js`, `style.css`, `sw.js` (Cache v21); Desktop: `Schild\SchildDialog*.cs`, `SchildFolgestand.cs`,
`SchildImportClient.cs`.

- **Testdatum (221):** `Entwicklung:Heute` (z. B. `2027-01-18`, Einstellungsdatei oder Umgebungsvariable
  `Entwicklung__Heute`) verschiebt das „Heute“ der API (`KlassenbuchService.Heute()`): Schuljahr nach dem Datum,
  Halbjahr, Stundenblatt, Gültigkeit, Fristen nach Datum, Jahresablauf, SchILD-Status. Zeitstempel (Protokolle, Anmeldung,
  Fristen der Protokolle) laufen mit der echten Uhr. Im Echtbetrieb startet die API mit gesetztem Wert nicht
  (Startprüfung). `GET /api/auth/optionen` liefert `testdatum`; die App zeigt ein Band und rechnet mit dem Tag als heute.
  PowerShell (nur Entwicklung): `$env:Entwicklung__Heute = "2027-01-18"` vor `dotnet run`, danach
  `Remove-Item Env:Entwicklung__Heute`.
- **Jahresablauf (205):** `GET /api/jahresablauf` (nur Admin, JWT) – Karten mit Ampel (`gruen`, `gelb`, `rot`, `grau`),
  ohne Namen. Karte „Halbjahreswechsel“ ab 4 Wochen vor „2. Halbjahr ab“ bis zum Ende des Schuljahres: Grundplan ab dem
  2. Halbjahr, SchILD-Import Abschnitt 2, offene Fehlstunden/Verspätungen und nicht erfasste Stunden im 1. Halbjahr,
  Rückweg nach SchILD (seit P4b grün erst mit einer gemeldeten vollständigen Datei, siehe unten), Sek-I-Gruppen des Plans am Stichtag
  ohne Liste, Gruppenlisten mit wegfallendem Schlüssel. Nichts geschieht von selbst; die App springt in die Bereiche.
- **Halbjahr beim Import (206):** `GET /api/schild/status` liefert das wirksame `halbjahrAb` (Einstellung bzw. 1. Februar –
  vorher nur die Einstellung; der Vorschlag für den ersten Import von Abschnitt 2 ist jetzt auch mit „Standard“ der
  Halbjahresbeginn), `heute`, `testdatum` und `abschnittVorschlag`. Vorschau und Import liefern `warnungen` (und
  „WARNUNG:“ im Bericht): Abschnitt 2 mit „gilt ab“ vor dem 2. Halbjahr, Abschnitt 1 mit „gilt ab“ im 2. Halbjahr,
  Abschnitt 2 ab dem 2. Halbjahr ohne eigenen Grundplan (dazu bei unbekannten Plan-Schlüsseln: „SchILD liefert schon die
  Kurse des 2. Halbjahres“). Keine Sperre – der Desktop zeigt sie im Band und fragt vor dem Import nach. Enthält ein
  Export Abschnitt 1 und 2, wählt der Desktop ab dem Stichtag Abschnitt 2 vor.
- **Fristen (208, 209):** „Abwesenheitsgründe der Vertretungen löschen nach … Tagen“ (Grund und Bemerkung, die Vertretung
  bleibt) und „Konten abgegangener Schüler löschen nach … Tagen“ (gesperrte Schülerkonten mit Abgang laut SchILD; je
  Konto ein Eintrag „Gelöscht“ im Kontenprotokoll; Schüler und Klassenbuchdaten bleiben) – Standard 0 = aus, mit
  DSB-Frage. Die Protokolle der SchILD-Importe fallen unter „Aufbewahrung der Protokolle“; behalten wird je Schuljahr +
  Abschnitt + Art das neueste, je Schuljahr + Art das mit dem spätesten Stichtag und je Art das neueste mit
  Schlüsselkennung (der Import braucht sie). Kürzere Frist: zählen, 409, nach Bestätigung sofort aufräumen; jede
  Löschung steht mit Zahlen im Protokoll der Einstellungen (Akteur „System (Löschfrist)“).

## Rückweg nach SchILD (P4b, 5.3a)

Entscheidungen 210–213 (`claude/5.3_Vorschlag.md` §6). **Keine Schemaänderung** (Protokoll über `DownloadProtokoll.Art`,
Abholcodes nur im Arbeitsspeicher). API: `Controllers/RueckwegController.cs`, `Services/SchildRueckweg.cs`,
`Services/RueckwegCodes.cs`, `Dtos/RueckwegDtos.cs`, `Services/Jahresablauf.cs`; PWA: `jahresablauf.js`, `konten.js`,
`hilfe.js`, `index.html`, `style.css`, `sw.js` (Cache v22); Desktop: `Schild\SchildRueckweg.cs`,
`Schild\SchildDialog_Rueckweg.cs`, `SchildDialog.xaml(.cs)`, `SchildImportClient.cs`, `SchildProfil.cs`, `SchildZuordnung.cs`.

- **App (nur Admin, JWT):** `GET /api/schild/rueckweg?von=&bis=` – Zeiträume (1./2. Halbjahr; Standard das laufende, in
  den ersten 6 Wochen des 2. Halbjahres noch das 1.), Zahlen: übertragen (entschuldigt, beurlaubt = J, unentschuldigt) und
  nicht übertragen (offen, Verspätungen, schulische Veranstaltung, ohne Prüfsumme, doppelt erfasst), nicht erfasste
  Stunden, frühere Bereitstellungen, gültiger Code (ohne den Code). `bis` wird auf heute gekürzt.
  `POST /api/schild/rueckweg/code` `{von, bis}` → Abholcode (8 Zeichen ohne 0/O/1/I/L, 15 Minuten, ein Abruf; ein neuer
  ersetzt den alten; 5 Fehlversuche → verfallen). `DELETE /api/schild/rueckweg/code` macht ihn ungültig. Ein Neustart der
  API löscht den Code.
- **Desktop (`X-Api-Key`):** `POST /api/schild/rueckweg/abruf` `{code, pseudo, kennung, desktop}` – prüft zuerst Art
  (Entwicklung = nur Pseudodaten, 83) und Schlüsselkennung des letzten Imports (dann bleibt der Code gültig), danach den
  Code. Antwort: je Fehlstunde nur `extId`, `datum`, `stunde`, `fach` (der Stunde laut Plan; bei Vertretung das Fach des
  Schülers, bei „verlegt hin“ das der verlegten Stunde), `lehrkraft` (laut Grundplan bzw. der verlegten Stunde), `art`
  (`entschuldigt` | `unentschuldigt` | `beurlaubt`) – **keine Namen** –, dazu Zahlen, Kalender, `halbjahrAb` und ein
  `merkzeichen`. Download-Protokoll: Art `SchildRueckweg` (abgeholt), Akteur = wer den Code erzeugt hat.
  `POST /api/schild/rueckweg/datei` `{merkzeichen, von, bis, jahr, abschnitt, probe, klassen, zeilen, …}` – Meldung „Datei
  geschrieben“ (2 Stunden, höchstens 10 je Abruf) → Art `SchildRueckwegDatei` bzw. `SchildRueckwegProbe`.
- **Jahresablauf:** Punkt „Rückweg nach SchILD für das 1. Halbjahr“ ist grün erst mit einer `SchildRueckwegDatei` über das
  ganze 1. Halbjahr; neue Karte „Rückweg nach SchILD“ mit dem Knopf zum Dialog und den bisherigen Einträgen.
- **Desktop:** Reiter „Rückweg“ im Fenster „SchILD-Daten…“: Code → „Abrufen“ (vorher Status: Art, Schlüsselkennung; echte
  Daten nur über HTTPS) → Zuordnung über die Prüfsumme (Schlüssel des Imports, unveränderte Eingabe) zu den Schülern des
  gewählten Abschnitts → Vorschau mit Namen (nur im Fenster) und Bericht ohne Namen → „Datei schreiben…“ in einen neuen
  Ordner `SchILD-Rueckweg_<Datum>` (Pseudodaten: `SchILD-Rueckweg-Pseudo_<Datum>` + `PSEUDODATEN.json`; Warnung bei
  Cloud-Ordnern). Zusammenhängende Stunden (gleicher Tag, gleiches SchILD-Fach, gleiche Bewertung und Lehrkraft) werden
  eine Zeile von–bis. Fach über die Fächertabelle (mehrdeutig → Leistungsdaten des Schülers), Lehrkraft über die feste
  Kürzeltabelle (sonst leer). Stunden außerhalb des Abschnitts (1. bzw. 2. Halbjahr) kommen nicht in die Datei. Probedatei:
  Klassen/Stufen und kürzerer Zeitraum. Übertragene Zeiträume: `schild_rueckweg.json` bzw. `schild_rueckweg_pseudo.json`
  neben der Plandatei (ohne Personendaten) – Warnung bei Überschneidung. Formatprofil: `Rueckweg` → `Datei`, `Kopf`
  (13 Spalten, erste „Nachname“ bzw. „Name“), `Zeichensatz` (leer = wie Profil bzw. Export).

## Schuljahreswechsel: Jahresabschluss und neues Schuljahr (P5a, 5.3b)

Entscheidungen 214–217, 219, 239–242 (`claude/5.3_Vorschlag.md` §7, §11). **Migration `Schuljahre`** (neue Tabelle
`Schuljahre` – mit den Spalten für Archiv-Export und Löschen aus P5c – und Spalte `Benutzer.ZustaendigSchuljahr`). API:
`Model/Schuljahre.cs`, `Services/SchuljahrRechte.cs`, `Services/JahresablaufSchuljahr.cs`, `Controllers/JahresablaufController.cs`;
Prüfungen in den Diensten und Controllern von Klassenbuch, Einträgen, Fehlzeiten, Berichten, Wochenplan, Plan, Grundplan,
Stammdaten und SchILD-Import; PWA: `jahresablauf.js`, `konten.js`, `hilfe.js`, `index.html`, `style.css`, `sw.js` (Cache v25).

- **Karte „Schuljahreswechsel“** (Kontomenü → „Jahresablauf“, nur Admin; sichtbar ab 6 Wochen vor dem letzten Schultag):
  Vorprüfung (letzter Schultag, offene Fehlstunden/Verspätungen, nicht erfasste Stunden, Rückweg 2. Halbjahr – nur
  Warnungen), „Schuljahr … abschließen…“, „Wieder öffnen…“ (Begründung 10–300 Zeichen), „Schuljahr … vorbereiten…“,
  Zuständigkeiten je Schuljahr. Jede Aktion mit Rückfrage (409 `bestaetigen`) und Eintrag im Protokoll der Einstellungen
  („Jahresablauf“). `POST /api/jahresablauf/abschliessen` `{schuljahr, bestaetigt}`, `…/oeffnen` `{schuljahr, begruendung}`,
  `…/neues-schuljahr` `{schuljahr, bestaetigt}` (das neue Jahr).
- **Abgeschlossenes Schuljahr:** nur lesbar – auch für den Admin (409 „… ist abgeschlossen – nur noch lesbar“); nur der Admin
  sieht es (lesen, PDF, CSV, Archiv) – alle anderen 403 bzw. im Plan „Schuljahr … abgeschlossen“. Vertretungen
  (`plan/push`), Grundpläne (`grundplan/push`) und SchILD-Importe (Sperre „Schuljahr“) für das Jahr lehnt die API ab.
  Ausgenommen: Löschfristen und „Vorhandene Gründe entfernen…“.
- **Neues Schuljahr vorbereiten:** „Aktuelles Schuljahr“ → neues Jahr; „Anwesenheit/Thema erfasst ab“ und „2. Halbjahr ab“,
  die nicht im neuen Jahr liegen, → Standard; das bisherige Jahr merkt sich seine Werte (Zeile in `Schuljahre`), „Nicht
  erfasst“ und die CSV-Prüfung rechnen in einem früheren Schuljahr damit. Kalender und Grundplan des neuen Jahres kommen
  vorher aus dem Desktop (fehlen sie: Warnung). Bis zum Abschluss bleibt das alte Jahr für Nachträge bearbeitbar (242).
- **Zuständigkeit je Schuljahr (219, 242):** gilt nur für Daten ihres Schuljahres und – für Schülerkonten – nur im aktuellen
  Schuljahr. Gesetzt beim Anlegen/Ändern (neue bzw. geänderte Zuständigkeit = aktuelles Schuljahr; Haken „… für das
  aktuelle Schuljahr übernehmen“), in der Sammelanlage, bei den Vorschlägen aus SchILD und in `seed-konten`. Konten ohne
  Schuljahr bekommen beim ersten Start mit P5a das aktuelle (Kontenprotokoll, Log „Zuständigkeiten: …“).

## Zugangsschutz (P6a, 5.4)

Entscheidungen 224–231 (`claude/P6_Vorschlag.md` §2–§7). **Migration `Datenschutz`** (alle neuen Spalten und Tabellen
von P6: `Benutzer.ZweiterFaktor*`, `AnmeldeProtokolle`, `LeseProtokolle` – Letztere füllt erst P6b). API:
`Helpers/Totp.cs`, `Helpers/Zugriffsschutz.cs`, `Services/ZweiterFaktor.cs`, `Services/Anmeldedienst.cs`,
`Services/Anmeldeprotokoll.cs`, `Services/Begrenzung.cs`, `Controllers/ZweiterFaktorController.cs`,
`Controllers/ZugriffeController.cs`, `Model/Datenschutz.cs`, `Dtos/ZugriffDtos.cs`; geändert u. a. `Program.cs`,
`AuthController.cs`, `KontenController.cs`, `EinstellungenController.cs`, `LoginBremse.cs`, `Betrieb.cs`,
`ApiSchluessel.cs`; PWA: `zweiterfaktor.js` (mit eigenem QR-Erzeuger), `zugriffe.js`, `app.js`, `konten.js`,
`einstellungen.js`, `hilfe.js`, `index.html`, `style.css`, `sw.js` (Cache v23); Desktop: `OnlineVerbindung.cs`,
`OnlineSync.cs`.

### Zweiter Faktor (TOTP)

- **Verfahren (225):** RFC 6238 – HMAC-SHA1, 6 Ziffern, 30 s, ±1 Zeitschritt, jeder Code nur einmal (der zuletzt benutzte
  Zeitschritt steht am Konto). Geheimnis 160 Bit (Base32) in der Datenbank, nach der Einrichtung nie wieder sichtbar.
  10 Wiederherstellungscodes (`ABCDE-FGHJK`, ohne I/L/O/0/1), nur einmal sichtbar, gespeichert als SHA-256-Prüfsumme.
- **Anmeldung:** `POST /api/auth/login` liefert bei einem Konto mit zweitem Faktor statt des Tokens
  `{ codeNoetig, vorgang, gueltigSekunden }`; dann `POST /api/auth/login/code` `{ vorgang, code }` (Code der App bzw.
  Wiederherstellungscode). Der Vorgang ist mit AES-GCM versiegelt (Schlüssel aus `Jwt:Key`), gilt 5 Minuten und nur,
  solange der Sicherheitsstempel des Kontos gleich bleibt. Reihenfolge: Bremse → Passwort → gesperrt → Adresse (Admin) →
  Code. Falsche Codes zählen in der Login-Bremse (Konto und Adresse); das richtige Passwort setzt den Zähler eines Kontos
  mit zweitem Faktor erst nach dem richtigen Code zurück.
- **Eigenes Konto** (`/api/auth/zweiter-faktor`, nur Personal): `GET` Stand · `POST beginnen` `{passwort, code?}` →
  `{merker, schluessel, uri}` (10 Minuten; bei aktivem Faktor zusätzlich ein Code) · `POST einrichten` `{merker, code}` →
  `{codes, anmeldung}` (andere Geräte abgemeldet, neues Token) · `POST codes` `{passwort, code}` · `POST ausschalten`
  `{passwort, code}` (nur ohne Pflicht). Kontenprotokoll ohne Geheimnis und Codes.
- **Pflicht (226):** Admin im Echtbetrieb immer; Schalter in den Einstellungen (Karte „Anmeldung“): „Zweiter Faktor für
  Admins Pflicht“ (nur Entwicklung), „… für Klassenleitung und Stufenkoordination“, „… für alle Lehrkräfte“ (Standard
  aus). Ohne Faktor ist nach dem Passwort nur die Einrichtung erlaubt (Claim `faktorEinrichten`, 403 mit
  `faktorEinrichten`); erst Passwort festlegen, dann Faktor. Wer den letzten Wiederherstellungscode benutzt, muss neu
  einrichten bzw. neue Codes erzeugen. Admin-Konten bekommen nie „Angemeldet bleiben“.
- **Verlust (227):** Admin → Konten → Konto → „Zweiten Faktor zurücksetzen“
  (`POST /api/konten/{id}/zweiter-faktor/zuruecksetzen`, nicht das eigene Konto). Eigenes Admin-Konto: zweiter Admin oder
  `admin-anlegen` auf dem Server.
- **Testen in der Entwicklung:** eine Authenticator-App auf dem Handy (oder am PC z. B. KeePassXC); für die Pflicht der
  Admins Einstellungen → Anmeldung → „Zweiter Faktor für Admins Pflicht“. Die Uhr des Servers muss stimmen (das Testdatum
  verschiebt nur „heute“, nicht die Uhr).

### Anmeldeprotokoll und Reiter „Zugriffe“ (228)

Erfolglose Anmeldungen aller Konten (Passwort bzw. Code falsch, unbekanntes Konto, gesperrt, gebremst, Adresse nicht
erlaubt), erfolgreiche nur von Admin-Konten, jede Anmeldung mit Wiederherstellungscode – ohne Passwörter, unbekannte
Benutzernamen ohne den eingegebenen Text, Adresse gekürzt (IPv4 /24, IPv6 /48), „Schulnetz“ ja/nein, Gerät grob.
`GET /api/zugriffe/anmeldungen?konto=&ergebnis=&von=&bis=` (nur Admin; Konten verwalten → Reiter „Zugriffe“). Nach der
Anmeldung nennt die Antwort (`fehlversuche`) die erfolglosen Versuche seit der letzten Anmeldung. Frist: „Aufbewahrung
der Protokolle“.

### Schuladressen und Adressbeschränkung (229)

Nur in der Einstellungsdatei (nicht in der App; die Karte „Zugriff“ der Einstellungen zeigt sie zum Lesen):
```json
"Zugriff": {
  "Schuladressen": [ "203.0.113.10/32", "2001:db8:1234::/48" ],
  "DesktopNurSchule": false,
  "AdminNurSchule": false
}
```
- `DesktopNurSchule`: alle Endpunkte mit `X-Api-Key` antworten von anderen Adressen mit **403 „Adresse nicht
  freigegeben“** – vor der Prüfung des Schlüssels und vor dem Lesen des Inhalts (`[DesktopSchnittstelle]`).
- `AdminNurSchule`: Anmeldung eines Admin-Kontos **und jede Anfrage mit einem Admin-Token** von außerhalb → 403
  (`adresseNichtErlaubt`); Anmeldeprotokoll „Adresse nicht erlaubt“. Notfall: Konsole.
- Startprüfung: ungültiger Eintrag bzw. Beschränkung mit leerer Liste → Startfehler; Echtbetrieb mit Beschränkung ohne
  `Proxy:Aktiv` → Hinweis. `localhost` gilt **nicht** von selbst als Schule – zum Testen `127.0.0.1/32` und `::1/128`
  eintragen (z. B. `$env:Zugriff__Schuladressen__0 = "127.0.0.1/32"`, `…__1 = "::1/128"`).
- Auch ohne Beschränkung: Die Login-Bremse sperrt Schuladressen nicht mehr als Ganzes (nur noch je Konto), die
  Anfragebegrenzung ohne Anmeldung gilt dort nicht.

### Anfragebegrenzung (230)

Nur für `/api`; Werte in der Einstellungsdatei (Standard in Klammern, 0 = aus):
`Begrenzung:JeKontoJeMinute` (300), `OhneAnmeldungJeMinute` (120, je Adresse; Schuladressen ausgenommen),
`DesktopJeMinute` (600, je Adresse mit gültigem Schlüssel), `TeuerJeKonto` (2) und `TeuerGesamt` (4) gleichzeitige teure
Abrufe (PDF, CSV, Archiv, SchILD-Import, Rückweg; `[TeurerAbruf]`), `TeuerWartenSekunden` (10),
`OhneAnmeldungMaxKB` (64). Antwort **429** mit `Retry-After` und „Zu viele Anfragen – bitte in n Sekunden erneut
versuchen.“ (App und Desktop zeigen den Text); im Log eine Warnung je Konto bzw. Adresse und 10 Minuten.

### Sicherheits-Header (231)

Dazu: `Permissions-Policy` (Kamera, Mikrofon, Standort, Zahlung, USB u. a. aus), `Cross-Origin-Opener-Policy` und
`Cross-Origin-Resource-Policy: same-origin`, `X-Permitted-Cross-Domain-Policies: none`; Kestrel ohne `Server`-Kopf;
HSTS (Echtbetrieb) 365 Tage mit `includeSubDomains`, ohne `preload`. Die CSP bleibt streng. Prüfen:
`curl.exe -I http://localhost:5080/` bzw. `…/api/auth/optionen`.

## Datenschutzfunktionen (P6b, 5.4)

Entscheidungen 232–238 (`claude/P6_Vorschlag.md` §8–§11). Keine Migration – die Tabelle `LeseProtokolle` kommt mit
`Datenschutz` (P6a).

### Leseprotokoll (232, 233)

- **Schalter** „Leseprotokoll“ (Einstellungen → Datenschutz, **Standard aus**, DSB- und Personalratsfrage). Aus = nichts
  wird aufgezeichnet; gelesen wird er aus dem Zwischenspeicher der Einstellungen (`Services/Leseprotokoll.cs`).
- **Aufgezeichnet** (nur lesende Abrufe): `fehlzeiten/schueler/{id}`, `…/klasse/{k}`, `…/offen/{k}`, `…/unterricht`,
  `…/nicht-erfasst` (nicht „ich“), `klassenbuch/stunde` und `…/stunde/historie`, `klassenbuch/gruppe`,
  `eintraege/stunde`, `…/schueler/{id}`, `…/klasse/{k}`, `…/unterricht`, `…/{id}/historie`, `woche/schueler/{id}`,
  `schueler/{id}`, `lerngruppen/{id}/schueler`, `plan/tag`, `plan/lehrer/{k}` (Admin, andere Lehrkraft) und
  `zugriffe/ansichten` selbst. **Nicht:** eigene Daten, Pläne von Klassen/Lehrkräften/Gruppen, Hausaufgaben, Themen,
  Downloads (eigenes Protokoll).
- **Zusammengefasst:** je Tag (Ortszeit), Konto, Ansicht, Schüler und Gegenstand eine Zeile mit `Anzahl`, `ErsteZeit`,
  `LetzteZeit`. Gegenstand ohne Namen („Schüler-Id 12 (05A)“, „Klasse 05A“, „Stunde 18.11.2026, 3. · M · UNr 20 · L1“);
  `Gruppe` hält bei Listenansichten die Lerngruppen (Bezeichnungen wie im Stundenblatt) für die Auskunft. Geschrieben wird
  in einem eigenen DbContext, nacheinander; ein Fehler bricht die Ansicht nie ab (Warnung höchstens alle 5 Minuten).
- **Einsicht:** `GET /api/zugriffe/ansichten?schueler=&konto=&ansicht=&von=&bis=&limit=` (Admin) – PWA: Konten verwalten
  → „Zugriffe“ → „Ansichten“. Frist „Aufbewahrung der Protokolle“ (nach dem Tag des Eintrags), kein Löschen von Hand.

### Auskunft nach Art. 15 (234, 235)

- `GET /api/auskunft/schueler/{id}` bzw. `GET /api/auskunft/konto/{id}` (Admin, teurer Abruf) → PDF (QuestPDF,
  `Services/Auskunft.cs` + `Services/AuskunftPdf.cs`). Schülerkonto → Schüler-Auskunft.
- **Schüler:** Angaben nach Art. 15 Abs. 1 (Speicherdauer aus den Fristen), Stammdaten, Zugehörigkeiten, Konto mit Konten-
  und Anmeldeprotokoll (handelnde Person als Rolle), Fehlzeiten (jede Stunde, Entschuldigungen, Summen je Halbjahr),
  Schülereinträge, Änderungsverlauf, Leseprotokoll ohne Namen (Einzelansichten; Listenansichten seiner Lerngruppen als
  Zahl je Monat und Rolle), Rückweg nach SchILD. **Konto:** Konto, Name laut SchILD, Protokolle, Abwesenheitsgründe,
  Ansichten, Tätigkeiten im Klassenbuch und Downloads als Zahlen je Monat.
- Download-Protokoll Art `Auskunft`, Was „Schüler-Id n (Klasse)“ bzw. „Konto-Id n (Rolle)“ – ohne Namen.

### Grund der Entschuldigung (236)

- Schalter „Grund der Entschuldigung erfassen“ (Einstellungen → Datenschutz, **Standard an**). Aus → `FehlGrund.OhneAngabe`
  (Wert 4, keine Migration) beim Entschuldigen und Bewerten; `GET /api/fehlzeiten/rechte` meldet `grundErfassen`.
- `GET /api/einstellungen/gruende` zählt, `POST /api/einstellungen/gruende-entfernen` `{ anzahl }` setzt alle Gründe
  (Fehlstunden, Entschuldigungen, Änderungsverlauf) auf „ohne Angabe“ – nur bei ausgeschaltetem Schalter, 409 bei einer
  anderen Zahl; Eintrag im Einstellungsprotokoll.

## Vertretungen aus Untis (U-1b und U-2, Desktop)

Übergangsphase: Die Schule plant die Vertretungen weiter in Untis; App und Klassenbuch zeigen sie trotzdem. Nur
Desktop, **keine API- und keine Schemaänderung** (die Schnittstelle kam mit U-1a). Im Vertretungs-Wocheneditor
(Button 24) öffnet **„Untis-Vertretungen…“** (neben „SchILD-Daten…“) das Fenster „Vertretungen aus Untis“. Es nutzt die
oben unter „Plan“ gewählte Plan-Spalte. Code: `src/Vertretung/Untis/`, Konzept und Entscheidungen 117–135:
`claude/Untis_Vorschlag.md` (Claude-Projekt).

- **Datei:** Untis-Export „Export Vertretungen“ **`GPU014.TXT`** (DIF, 22 Felder je Zeile). Trennzeichen `;` oder `,`
  und Zeichensatz UTF-8 (mit/ohne BOM) oder ANSI werden erkannt; Zeilen mit anderer Feldanzahl, ungültigem Datum oder
  einer Stunde außerhalb des Zeitrasters sind Fehler für die Zeile. **Absenzgrund (Feld 16) und Text zur Vertretung
  (Feld 17) werden nie gelesen**, angezeigt, gespeichert oder gesendet – gezählt wird nur, ob sie gefüllt sind. Eine
  echte Datei enthält Absenzgründe: nur lokal ablegen (z. B. `C:\Vertretung\`), nicht in OneDrive/Teams (das Fenster
  warnt), nach dem Veröffentlichen löschen. `GPU014*.TXT` steht in der `.gitignore` (greift beim Hochladen im Browser nicht!).
- **Prüfen:** Reiter **Übersicht** (Datei, Tage, Untis-Arten, Übernahme, Wechsel, offene Vertretungen), **Probleme**
  (Zeile, Spalte, Grund; gleichartige zusammengefasst), **Vorschau** (je Schultag des Zeitraums: Stunde, Klassen, Fach,
  Lehrkraft, Raum jeweils „alt → neu“, wie es in der App ankommt, Untis-Art, Zeile, Gegenstück; Farben wie im
  Wocheneditor, Klausur pink, ohne Gegenstück grau), **So geht’s**.
- **Zuordnung:** Datum → Wochentag, Stunde, U-Nr (Feld 5) und absente Lehrkraft (Feld 6) → Grundplan-Stunde
  (`grundUNr` + `originalLehrer`). Ohne Gegenstück → Warnung; gesendet wird trotzdem (die App zeigt eine zusätzliche
  Stunde bzw. Entfall/Raumvertretung haben keine Wirkung). Viele Zeilen ohne Gegenstück → Hinweis „GPU001 neu
  importieren (Knopf 3) und Grundplan neu veröffentlichen“. Weitere Prüfungen: unbekannte Kürzel/Klassen (Warnung),
  doppelte Zeilen (Hinweis, nur einmal gesendet), Tage, die laut Kalender keine Schultage sind (Hinweis, nicht gesendet).
- **Übersetzung** (Buchstabe Feld 20 entscheidet, Bits Feld 18 als Rückfall, Bits 21/22 ignoriert): Vertretung,
  Lehrertausch `~`, Teil-Vertretung `P`, Sondereinsatz `A` mit U-Nr → Vertretung; offene Vertretung (kein Vertreter) →
  Lehrkraft `???`; Entfall `C`, Freisetzung `L`, „unterrichtsfrei“ → Entfall; Plus als Vertreter (Bit 5) → EVA (Lehrkraft
  leer, abwesende Lehrkraft gestrichen); Betreuung `S`; Verlegung als Zeilenpaar (`C`+Bit 3 bzw. `T` = alte Stelle
  `VerlegtWeg`, `F` = neue Stelle `VerlegtHin` mit der U-Nr der verlegten Stunde); Raumvertretung `R`; Klausur `E` →
  Status `Klausur` (Kurs aus Feld 10, Stufe aus Feld 19, Aufsicht = Lehrkraft, U-Nr und Lehrkraft des Plan-Kurses
  gleichen Namens); Sondereinsatz ohne U-Nr → zusätzliche Stunde der Klasse. **Nicht übernommen:** Pausenaufsicht `B`,
  Sondereinsatz ohne Klasse bzw. „Klau“ (Klausuraufsicht), „nicht drucken“ (Bit 20). Raum-, Fach- und Klassenwechsel
  setzen die Merker `raumGeaendert` (Feld 13 ≠ 12), `fachGeaendert` (Feld 10 ≠ 8) und `klassenGeaendert` (Feld 19 ≠ 15);
  den Raum sendet ein zugeordneter Eintrag nur bei echtem Wechsel. Zwei Vertretungen derselben Stunde mit
  verschiedenen Klassen = geteilte Stunde (U-1a).
- **Veröffentlichen:** Zeitraum (Vorgabe: erster bis letzter Tag der Datei, änderbar). **Jeder Schultag im Zeitraum wird
  in der App komplett ersetzt** – auch Tage ohne Zeilen (dann leer), damit in Untis gelöschte Vertretungen verschwinden;
  Ferien/Feiertage laut Kalender werden übersprungen. Tag für Tag über `POST /api/plan/push`, mit Fortschritt und
  „Abbrechen“. Die Rückfrage nennt Tage mit Buchungen im Vertretungsmodul, Fehlerzeilen und Zeilen ohne Gegenstück.
- **Eine Quelle je Tag:** „Online-Einstellungen…“ hat den Schalter **„Vertretungen kommen aus: Vertretungsmodul |
  Untis“** (`"Quelle"` in `vertretung_online.json`). Bei „Untis“ fragt „Online veröffentlichen“ im Wocheneditor nach,
  bevor ein Tag aus dem Vertretungsmodul die Untis-Vertretungen ersetzt; steht die Quelle auf „Vertretungsmodul“, bietet
  das Untis-Fenster beim Veröffentlichen an, sie umzustellen.
- **Protokoll `untis_import.json`** neben der Plandatei: zuletzt gewählter Ordner, je Lauf Zeit, Datei, Zeitraum, Zahl der
  Tage und Einträge, je Tag Zeitpunkt und Prüfsumme des zuletzt gesendeten Stands – keine Inhalte (für U-2 „nur geänderte
  Tage senden“).
- **„Demo-Datei erzeugen…“:** schreibt aus dem gewählten Plan eine `GPU014.TXT` für eine Woche (Standardordner
  `Untis-Demo_<Datum>` neben der Plandatei) mit allen Fällen (Liste nach dem Erzeugen und in `UNTIS_DEMO.json`, dort mit
  SHA-256-Prüfsumme – nur dann gilt die Datei als Demo). In Feld 16/17 stehen die Testwerte `GEHEIM-ABSENZGRUND` und
  `GEHEIM-TEXT`, die nirgends erscheinen dürfen. Gleicher Startwert + gleicher Plan = gleiche Datei. Eine GPU014.TXT ohne
  passende Markierung wird nie überschrieben.
- **„Bericht kopieren“:** Zahlen und Probleme als Text (Kürzel, Klassen, Fächer, Zeilennummern – keine Absenzgründe,
  keine Texte).
- **Ordner beobachten (U-2):** Das Fenster ist seit U-2 nicht mehr modal (der Wocheneditor bleibt bedienbar; ein
  zweiter Klick auf „Untis-Vertretungen…“ holt es nach vorn). **„Ordner beobachten…“** (unten im Fenster) beobachtet die
  gewählte Datei bzw. `GPU014.TXT` in einem gewählten Ordner (die Datei muss noch nicht da sein), **solange das Fenster
  offen ist** (auch minimiert). Plan = die beim Einschalten gewählte Plan-Spalte; Zeitraum = immer erster bis letzter Tag
  der Datei. Ein Takt prüft alle 3 Sekunden Größe und Zeitstempel; erst wenn beide einen Takt lang gleich sind und die
  Datei ohne Schreibsperre lesbar ist, wird sie gelesen (gleicher Inhalt wie zuvor → nichts zu tun). Gesendet werden nur
  die **geänderten Schultage** – Prüfsumme der Einträge je Tag gegen den zuletzt aus Untis gesendeten Stand in
  `untis_import.json` –, heute und künftige Tage zuerst; in Untis geleerte Tage werden leer gesendet. **Nichts wird
  automatisch gesendet**, wenn die Datei mehr Fehlerzeilen hat als zuletzt gesendet bzw. bestätigt (anfangs 0, z. B.
  halb geschrieben; nach „trotzdem senden“ werden gleich viele hingenommen – die Demo-Datei hat absichtlich zwei) oder auf einen Schlag mehr als drei
  Tage leer würden, die vorher Einträge hatten; dann nennt das Fenster den Grund (rote Leiste, Fenstertitel „⚠ nicht
  gesendet“) und bietet **„Geänderte Tage trotzdem senden…“** an. Ist der Server nicht erreichbar, folgt nach 60 Sekunden
  ein neuer Versuch. Reiter **„Beobachten“**: Verlauf (Zeit, Datei, Zahl und Datum der geänderten Tage, Ergebnis – keine
  Inhalte). Läufe stehen mit `"Automatisch": true` und `"TageUnveraendert"` im Protokoll. Tage, die aus dem
  Vertretungsmodul veröffentlicht wurden, bleiben, bis sich der Tag in Untis ändert. Schließen des Fensters bzw. des
  Wocheneditors beendet das Beobachten (mit Rückfrage). Code: `src/Vertretung/Untis/UntisDialog_Beobachten.cs`.

## Räume zuweisen – Raumplan (Schritt R1, Desktop)

Nur Desktop, **keine API- und keine Schemaänderung, es wird nichts gesendet.** Knopf **„25. Räume zuweisen
(Raumplan)“** im Hauptfenster öffnet das Fenster „Räume zuweisen“. Code: `src/Raumplan/`, Knopf in
`src/MainWindow_Raumplan.cs`. Konzept und Entscheidungen 95–102: `claude/Raumplan_Vorschlag.md` (Claude-Projekt).

- **Plan wählen** – dieselben Pläne wie im Planeditor: Spalten aus `Lös`, gesicherte Lösungen aus `Gesichert`
  („[Gesichert] A10“) und der manuelle UNr-Plan aus Blatt `Plan` („Plan“); Standard wie bisher die Lös-Spalte
  „Plan“, sonst die letzte Lös-Spalte. Modus **„Behalten und ergänzen“** (Standard, sobald es einen Raumplan
  gibt: vorhandene Zuweisungen bleiben, nur neue/verschobene Stunden bekommen Räume) oder **„Neu verteilen“**.
  Die Vorschau rechnet sofort; Reiter **Übersicht** (Zahlen je Herkunft), **Zuweisungen** (Filter „nur Ausweich
  und Probleme“, Suche), **Räume** (Raumverzeichnis), **Hinweise**. Erst **„Speichern“** schreibt in die Plandatei
  (sie darf nicht in Excel geöffnet sein).
- **Regeln je Unterrichtsteil:** Fachraum laut UV – bei `A~B` bzw. `A,B` werden **alle** Räume gleichzeitig
  gebraucht – sonst anderer Raum derselben Fachgruppe (FGR), Stammraum laut UV, freier Ausweichraum (zuerst
  derselbe Bereich, sonst Reihenfolge im Blatt „Räume“). Ohne Raumangabe bei genau einer Sek-I-Klasse: deren
  Stammraum aus den übrigen UV-Zeilen („abgeleitet“). Reihenfolge der Verteilung je Stunde: Sonderräume, Fachräume
  mit Fachgruppe, übrige Fachräume, Stammräume, Zusatzräume; bei Gleichstand Doppelstunde/mehr Wochenstunden/
  kleinere UNr. Eine Doppelstunde bleibt im Raum der Vorstunde, wenn er nicht der Wunschraum eines anderen
  Unterrichts ist. Team-Teaching (gleiche UNr, gleicher Raum) teilt den Raum; A- und B-Woche dürfen denselben
  Raum belegen. Pseudofächer (UV, VBer, PSF, ufrei, NuHo) bekommen keinen Raum.
- **Schwimmen:** Ein **Sonderraum** (Schwimmbad) wird nie ersetzt und nie als Ausweich genommen. Steht daneben
  ein Fachraum (`RSh1~RSb1`), ist dieser **Zusatzraum** (Halle zum gelegentlichen Ausweichen): er wird nach dem
  übrigen Sportunterricht verteilt – Wunschhalle, sonst jede freie Halle.
- **Blatt „Raumplan“:** `Plan | WTag | Stunde | UNr | Lehrer | Fach | Klasse(n) | Raum | Wunschraum | Herkunft |
  Fix (X) | Hinweis` – eine Zeile je Raumbelegung; Zeilen anderer Pläne bleiben beim Speichern stehen.
  Herkunft: Fachraum, Stammraum, abgeleitet, Fachgruppe, Zusatzraum, Ausweich, fest, ohne Raum.
  **Von Hand ändern:** in Excel „Raum“ ändern und in „Fix (X)“ ein X setzen – solche Zeilen bleiben bei jedem Lauf.
- **Blatt „Räume“** (Raumverzeichnis): `Raum | Art | Fachgruppe | als Ausweich | Bereich | Bemerkung`; Art =
  Klassenraum, Kursraum, Fachraum oder Sonderraum. Fehlt es, wird es beim Speichern (oder mit „Blatt „Räume“
  anlegen“) aus der UV angelegt – danach in Excel prüfen. Räume aus der UV, die im Blatt fehlen, werden unten
  angehängt; vorhandene Zeilen ändert das Programm nie. Zusätzliche Räume (z. B. ein freier Raum ohne Klasse)
  dürfen eingetragen werden und dienen dann als Ausweichraum.
- **App (seit U-1a):** „Grundplan veröffentlichen…“ sendet den Raum aus dem Blatt „Raumplan“ mit – die App zeigt
  ihn statt „Fachraum vor Stammraum“ (siehe „Räume, Klausuren und geteilte Stunden in Vertretungen“).
- **Später (R3-D):** Vertretungsmodul (zugewiesener Raum in Tagesplan/Erfassen/PDF, Raumvertretung mit freien
  Räumen, Raumsperre).

### Räume aus Untis übernehmen (Knopf 3, Desktop)

Code: `src/Raumplan/RaumUntisImport.cs`, `src/MainWindow_PlanImport.cs`, `GpuImportExport.LiesGpu001`. Knopf 3 („Plan
importieren“, GPU001.TXT) fragt – nur wenn die Datei Räume enthält – „Räume aus der GPU001.TXT als Raumplan für den Plan
„Plan“ übernehmen?“ (Standard Ja; Abbrechen bricht den Import ab). Felder laut Untis: 1 Unterr.-Nr., 2 Klasse, 3 Lehrer,
4 Fach, 5 Raum, 6 Tag, 7 Stunde; Trennzeichen `;` oder `,` wird erkannt.

- Je Stunde × UNr × Lehrkraft (Untis hat je Klasse eine Zeile) werden die Untis-Räume den Raumbedarfen laut UV zugeordnet:
  gleicher Raum wie in der UV → dieser Bedarf; sonst der nächste offene Bedarf derselben Lehrkraft; dann Teile ohne
  Raumangabe in der UV; Rest als zusätzlicher Raum. Team-Teaching: fehlt einer Lehrkraft der Raum, gilt der des
  Partners. Räume einer Lehrkraft, die die UV für die UNr nicht kennt, gehen an noch offene Bedarfe der Stunde.
- Herkunft **„aus Untis“**, nicht fest. Bedarfe ohne Untis-Raum: „ohne Raum – in Untis kein Raum“ (rot) – verteilt wird
  nur auf Nachfrage (Planeditor „Räume verteilen“, Knopf 25 „Behalten und ergänzen“).
- Blatt „Raumplan“: Die Zeilen für „Plan“ werden ersetzt; bei „Nein“ bleiben sie unverändert. Räume, die die UV nicht
  kennt, werden im Blatt „Räume“ ergänzt (Art „Fachraum“, nicht als Ausweich – bitte prüfen). Untis-Kombinationen wie
  „RSh2~RSb1“ (mehrere Räume gleichzeitig) bleiben im Raumplan so stehen, im Blatt „Räume“ kommen seit dem
  R3-D-Nachtrag die Einzelräume hinzu (ältere Zeilen mit „~“ lassen sich dort löschen – das Vertretungsmodul übergeht sie).
- Ein Fehler dabei bricht den Plan-Import nicht ab; Ergebnis und Hinweise stehen in der Meldung und im Log.

### Kopfleiste des Planeditors (Desktop)

Code: `src/PlanEditorDialog_Kopfleiste.cs`, `src/PlanEditorDialog.xaml`. Eine feste Zeile, die beim Ändern der
Fensterbreite nicht umbricht und nicht mitzoomt (Mindestbreite des Fensters 1100 px): links **Lösung** ◀ ▾ ▶ und
**Modus** „Block | Einzelstunde“, dann drei Klappmenüs, rechts **Diag-Werte**, **UV anzeigen** und **„?“**.

- **Ansicht ▾:** zusätzliche Pläne (Klassenvergleich, Fachgruppenplan, Fächerplan, Raumplan) und „In den Zellen:
  Räume“. Knopf zeigt „Ansicht (n)“.
- **Farben ▾:** Zellen färben nach aus/Klasse/Fach/Klasse + Fach, „Farben festlegen …“ (früher „Farbcode“),
  Markieren: späte nichtfixierte päd. Einheiten rot, auch fixierte (heller). Knopf zeigt z. B. „Farben: Fach · rot“.
- **Vergleich ▾:** Vergleichsmodus (2 Lösungen) mit Unterschiede farbig, Verschiebungspfeile, Nur Abw; bei
  eingeschaltetem Modus steht „2. Lösung ◀ ▾ ▶“ direkt hinter dem Knopf. Knopf zeigt „Vergleich: an“.
- Knöpfe mit eingeschalteten Optionen sind hellblau hinterlegt. Ein Klappmenü schließt bei einem Klick daneben,
  auf seinen Knopf oder mit Esc. **„?“** blendet die Hinweise zur Bedienung unter der Leiste ein (gemerkt in EdCfg
  `HinweiseZeigen`, Standard aus). Alle übrigen Einstellungen werden wie bisher in EdCfg gemerkt.

### Räume im Planeditor (Schritt R2, Desktop)

Code: `src/PlanEditorDialog_Raeume.cs` (partielle Klasse des Planeditors), Einstellungen in `EdCfg`
(`RaeumeZeigen`, `Raumplan`, `Raum`). Die Raumdaten (UV, FGR, Räume, Raumplan) werden erst gelesen, wenn eine
der beiden Raum-Ansichten eingeschaltet wird. **Ohne Raum-Ansicht verhält sich der Editor wie vorher** (auch beim
Uebernehmen); Verschieben, Tauschvorschläge und die Fachraum-Limits aus FGR (harte Sperre, Fachgruppenplan,
Meldungen) sind unverändert – die Räume sperren und warnen nie.

- **„Ansicht ▾ → Räume“:** Lehrer- und Klassenplan zeigen je Stunde oben rechts ein Raum-Abzeichen (bei mehreren
  Räumen „RSh1 +1“, Tooltip mit allen Räumen). Farbe: **grün** = wie im Blatt „Raumplan“ gespeichert,
  **grau** = verteilt, aber für diese Lösung noch kein Raumplan gespeichert, **blau** = fest, **orange mit „*“** =
  geändert und noch nicht gespeichert, **gelb „?“** = Raum offen, **rot** = kein freier Raum. Nur in den
  bearbeitbaren Plänen, nicht in den Vorher/Nachher-Vergleichen.
- **Räume nur auf Nachfrage:** Grundlage ist der gespeicherte Raumplan der Lösung. Verschieben, Tauschen und
  Entplanen verteilen **nichts** um: Stunden an ihrem Platz behalten ihren Raum, verschobene Stunden sind
  „Raum offen“. Kommt eine Stunde an ihren Ausgangsplatz zurück, hat sie wieder ihren Raum (wenn er frei ist).
  **„Räume verteilen (n offen)“** gibt nur den offenen Stunden einen freien Raum (wie Knopf 25 „Behalten und
  ergänzen“), alle übrigen Räume bleiben. Gibt es für die Lösung noch keinen Raumplan, fragt der Editor beim
  Einschalten einer Raum-Ansicht einmal nach; sonst steht der Knopf „Räume verteilen“ bereit.
- **„Ansicht ▾ → Raumplan“:** eigene Ansicht je Raum (wie Fachgruppen-/Fächerplan), Auswahl mit ◀ ▶. Doppelt
  belegte Stunden haben einen roten Rahmen; „A · B“ = A- und B-Woche teilen sich den Raum. Klick auf einen
  Unterricht stellt Lehrer- und Klassenplan darauf um; Klick im Lehrer-/Klassenplan springt zum Raum.
- **Rechtsklick → „Raum ändern“:** zeigt die in dieser Stunde freien Räume (Wunschraum, gleiche Fachgruppe,
  Stammraum, übrige Räume). Die Wahl gilt als **fest** (Fix X) und ändert nur diese eine Zeile. Außerdem
  „Festlegung aufheben“ (der Raum bleibt) und „Wie gespeichert“. „Zurücksetzen“ setzt auch die Räume zurück.
- **Speichern:** **„Räume speichern“** schreibt den Raumplan der geladenen Lösung – nur solange keine Stunde
  verschoben wurde. Wurde der Plan verändert, schreibt **„Uebernehmen“** die Räume für die neue Lösung `X_manN`
  mit – nur bei eingeschalteter Raum-Ansicht. Bei offenen Stunden fragt es vorher: verteilen, mit „Raum offen“
  speichern oder abbrechen. Beim Schließen warnt der Editor vor nicht gespeicherten Raumänderungen.

## Räume im Vertretungsmodul (Schritt R3-D, Desktop)

Code: `src/Vertretung/PlanRaeume.cs` (Raum je Stunde, Raumverzeichnis, freie Räume, Raumsperren),
`src/Vertretung/TagesEditorDialog_Raeume.cs` (Wocheneditor), dazu `TagesplanBerechnung`, `VertretungsService`,
`VertretungsDialog`, `VertretungsplanPdfExporter` und `OnlineSync`. **Keine API- und keine Schemaänderung**; die
Plandatei wird nur gelesen, gebucht wird wie bisher in die Vertretungsdatei (Spalten `OriginalRaum` und
`VertretungsRaum` im Blatt „Vertretungen“ gab es schon).

- **Raum einer Stunde:** aus dem Blatt „Raumplan“ der im Wocheneditor bzw. in „Vertretung erfassen“ gewählten
  Plan-Spalte (Knopf 25, Planeditor, Knopf 3 „aus Untis“) – dieselbe Zuordnung wie beim „Grundplan
  veröffentlichen…“ (Wochentag, Stunde, UNr, Lehrkraft, Fach). Ohne passende Zeile **Fachraum vor Stammraum**
  laut UV – einheitlich mit der App (vorher zeigte der Desktop Stammraum vor Fachraum). Der Wocheneditor nennt die
  Quelle in der Legendenzeile („Räume: Raumplan „oT_1“ …“) und liest die Räume bei „Aktualisieren“ neu (z. B. nach
  Knopf 25 „Speichern“); „Vertretung erfassen“ liest sie neu, wenn die Plandatei seitdem gespeichert wurde.
- **Wocheneditor:** Jede Stunde zeigt rechts oben ihren Raum; **orange „→ R199“** = durch eine Buchung geändert,
  **rot „⚠“** = Raum gesperrt (Abwesenheit „Raum“ an diesem Tag/in dieser Stunde). Klick → **„Raum ändern
  (jetzt …) …“** zeigt die freien Räume dieser Stunde: Fachraum laut UV, Räume derselben Fachgruppe, Stammraum laut
  UV, allgemeine Räume (Klassen-/Kursräume, „als Ausweich“) aus demselben Bereich, übrige allgemeine Räume, unter
  „weitere Räume“ der Rest (z. B. Fachräume anderer Fächer); Sonderräume (Schwimmbad) nur, wenn die UV sie für den
  Unterricht nennt. Frei = von keiner anderen Stunde belegt (entfallene und weg verlegte Stunden geben ihren Raum
  frei) und nicht gesperrt. Eine Stunde ohne Buchung wird zur **Raumvertretung** (Lehrkraft bleibt, Zähler 0);
  bei Vertretung, Betreuung, EVA, Zusammenlegung und „hierher verlegt“ bekommt die Buchung den neuen Raum.
  „Raum wie im Plan“ nimmt die Änderung zurück (eine reine Raumvertretung verschwindet). Team-Teaching (Teile
  derselben UNr im selben Raum) zieht gemeinsam um. Eine neue Art für eine Stunde mit Raumänderung (z. B.
  Raumvertretung → Vertretung durch …) behält den neuen Raum, außer bei Entfall. Verlegung auf eine freie Stunde:
  Raum der verlegten Stunde; auf eine belegte Stunde: Raum dieser Stunde (wie in der App).
- **„Vertretung erfassen“ (Button 23):** Spalten **„Raum“** (laut Plan) und **„neuer Raum“** (Auswahl der freien
  Räume, „(wie bisher)“ = kein Wechsel; nicht bei Entfall und Verlegung). Abwesenheit **„Raum“ = Raumsperre:** Nach
  „Betroffene Stunden ermitteln“ stehen die Stunden, die laut Raumplan in dem Raum liegen, auf **Raumvertretung**
  mit dem ersten freien Raum – je Tag und Stunde jeder Raum nur einmal. Raumvertretung braucht keine
  Vertretungskraft, aber einen neuen Raum (sonst „übersprungen (kein neuer Raum gewählt)“). Vor dem Buchen fragt das
  Fenster, wenn derselbe neue Raum in einer Stunde für verschiedene Unterrichte gewählt ist. Gebuchte Stunden (V1,
  jetzt kräftig gelb) zeigen den gebuchten Raum in „gebucht“ und „neuer Raum“. Die gebuchte Raumsperre sperrt den
  Raum auch für die Vorschläge im Wocheneditor (löschen lässt sie sich bisher nur im Blatt „Abwesenheiten“ der
  Vertretungsdatei).
- **Nachtrag (R3-D-Nachtrag):** Der Tooltip der ganzen Stunde nennt den Raum („Raum: R07c → R07d (geändert)“, bei
  einer Raumsperre mit „⚠“). Raumkombinationen aus dem Blatt „Räume“ („RSh2~RSb1“, „R08f~RKL“) werden nie als Raum
  angeboten, ihre Einzelräume schon. Ist der Raum geändert, steht der Raum laut Plan nur oben („Raum wie im Plan“),
  nicht noch einmal in der Liste. **Belegte Räume bewusst wählbar:** zuerst wie bisher nur freie Räume; ganz unten im
  Wocheneditor das Untermenü „belegte Räume (n) …“ mit z. B. „R07b (belegt: 07B E L21)“ bzw. „R05a (gesperrt)“ – die
  Wahl fragt nach („… ist in dieser Stunde belegt durch … – trotzdem?“); „Raum wie im Plan“ ist auch dann wählbar,
  wenn der alte Raum inzwischen belegt ist (mit Rückfrage). In „neuer Raum“ von „Vertretung erfassen“ stehen nach den
  freien Räumen die Trennzeilen **„– weitere Räume –“** (Fachräume anderer Fächer, Unbekannte) und **„– belegte
  Räume –“**; beim Buchen kommt eine Rückfrage zusammen mit „Raum doppelt“ – „Ja“ bucht trotzdem (auch zwei Stunden
  in denselben Raum). Wechselt „Abwesend“ (Lehrer / Klasse / Raum), wird das Feld „Namen“ geleert. Ist ein Raum
  **durch eine Buchung doppelt belegt** (mindestens eine der Stunden hat ihren Raum durch eine Buchung geändert), zeigen
  im Wocheneditor beide Stunden den Raum mit **„‼“** (dunkelrot), der Tooltip nennt die andere Stunde („‼ Raum R05d ist
  doppelt belegt – auch 07C M L52“). Doppelbelegungen schon im Raumplan (z. B. zwei Gruppen in einer Halle) und
  Team-Teaching werden nicht markiert.
- **„Online veröffentlichen (Di 24.11.)“:** Der Knopf nennt den Tag, den er sendet – den Tag im Feld „Woche um“ (nicht
  den zuletzt bearbeiteten Tag der Woche).
- **Zeitraum veröffentlichen…** (Wocheneditor, neben „Online veröffentlichen“; Entscheidung 161): alle Schultage
  von–bis laut Kalender (Ferien und freie Tage übersprungen) – Vorgabe vom Tag im Feld „Woche um“ bis zum letzten
  angezeigten Tag. Das Fenster zählt die Schultage und Änderungen, fragt vor dem Senden nach (bei Quelle „Untis“ mit
  Warnung wie beim einen Tag), zeigt den Fortschritt und lässt sich abbrechen; Meldung „n von m Tag(en) veröffentlicht“.
  Jeder Tag wird in der App komplett ersetzt (Tage ohne Buchungen werden leer); es gilt der geladene Plan (vorher
  „Aktualisieren“, wenn im Feld „Plan“ ein anderer gewählt ist – das Fenster weist darauf hin).
- **Tagesübersicht und PDF** (Lehrer-Sicht und Aushang): neue Spalte „Raum“ – Raum der Stunde, bei einer
  Raumänderung „alt → neu“, leer bei Entfall.
- **Online veröffentlichen:** Jeder Eintrag sendet den Raum laut Raumplan bzw. den neuen Raum; ändert eine Buchung
  den Raum, zusätzlich `raumGeaendert = true` – die App zeigt dann bei jeder Art „alter Raum gestrichen → neuer
  Raum“ (Raumvertretung wie bisher als „Raumwechsel“). Ohne Raumänderung bleibt der Raum in der App der des
  Grundplans.

## Dauervertretung (Schritt V3, Desktop)

Code: `src/Vertretung/Dauervertretung.cs` (Vorschau, Buchung, Zurücksetzen) und `TagesEditorDialog_Dauer.cs`
(Fenster im Wocheneditor). **Keine API- und keine Schemaänderung.** In der Vertretungsdatei kommt im Blatt
„Vertretungen“ die Spalte 20 **„SerienId“** hinzu (leer = Einzelbuchung; ältere Dateien laden unverändert).
Entscheidungen 158–162 (`claude/Projektstand_und_Fahrplan.md` §6).

- **Dauervertretung buchen:** Wocheneditor → Klick auf eine Stunde → **„Dauervertretung (jede Woche bis …) …“**.
  Gilt für **diese eine Wochenstunde** (Wochentag, Stunde, Unterricht der abwesenden Lehrkraft). Art Vertretung,
  Betreuung/Aufsicht, EVA oder Entfall; „durch“ bietet dieselben Vorschläge wie das Menü (UV, Vber, Hohlstunde).
  Von (Vorgabe: der angeklickte Tag) – bis (Vorgabe: letzter Schultag vor den nächsten Ferien; Knöpfe „bis
  Ferienbeginn“ / „bis Schuljahresende“). Die **Vorschau** zeigt jeden Tag dieses Wochentags: „wird gebucht“,
  „ersetzt: Entfall“, „schulfrei – Weihnachtsferien“ (Ferien und freie Tage laut Kalender werden übersprungen) und
  **Lücken** (rot): Fehlt die Vertretungskraft (Abwesenheit „Lehrer“ bzw. alle eigenen Stunden entfallen) oder ist
  sie in der Stunde schon belegt (eigener Unterricht, andere Vertretung), wird an diesem Tag **nicht** gebucht – die
  Stunde bleibt offen bzw. wie gebucht. Verlegte Stunden bleiben. „Bestehende Buchungen ersetzen“ (an) ersetzt z. B.
  einen schon gebuchten Entfall; aus, bleiben bestehende Buchungen. Eine **Raumänderung** der angeklickten Stunde
  (orange „→ R…“) wird an allen Tagen übernommen, an denen der Raum frei ist (sonst Raum laut Plan); ein schon
  gebuchter Raumwechsel eines Tages bleibt. **Zähler:** jede Stunde zählt wie eine einzelne Vertretung (UV-Einsatz
  ohne Zähler). Bei Quelle „Untis“ weist das Fenster darauf hin.
- **Anzeige:** Stunden einer Dauervertretung zeigen **„⟳ bis 18.12.“**. Wird ein Tag einzeln umgebucht (z. B.
  „Entfall“), gehört er nicht mehr zur Dauervertretung; „Raum ändern“ behält die Zugehörigkeit.
- **Zurücksetzen** (Entscheidung 162): Knopf **„Künftige zurücksetzen…“** bzw. Klick auf eine Stunde einer
  Dauervertretung → **„Dauervertretung (bis …) ab hier zurücksetzen …“**. Ab einem Datum (einschließlich) werden nur
  die Buchungen **einer Dauervertretung** oder **einer Lehrkraft** entfernt – „für sie gebucht (sie fehlt)“ und/oder
  „von ihr gehalten“; bei der Lehrkraft bleiben Verlegungen und reine Raumwechsel. Vorschau mit allen Buchungen,
  Rückfrage vor dem Entfernen. Wahlweise enden die **Abwesenheiten** der Lehrkraft am Tag davor („früher zurück“;
  beginnt eine erst später, wird sie entfernt).
- **Veröffentlichen:** Nach dem Buchen bzw. Zurücksetzen fragt der Wocheneditor, ob der betroffene Zeitraum
  (erster bis letzter geänderter Tag) veröffentlicht werden soll, und öffnet dann „Zeitraum veröffentlichen“
  (Entscheidung 161; bei Quelle „Untis“ mit der bekannten Rückfrage).

## Nächste Schritte

1. **Schülerkonten aus SchILD** (Phase 5): Import statt Pseudodaten, Zuordnung über `ExtId`.
2. **Auswertungen (Phase 4):** ~~4.1 am Bildschirm~~, ~~4.2 Klassenbuch/Kursheft als PDF~~,
   ~~4.3 CSV-Export, Jahresarchiv, Download-Protokoll~~. Genaues SchILD-Importformat in Phase 5.
3. **Web-Push:** Benachrichtigung der Apps bei Änderung.
4. ~~**Produktiv:** Schlüssel aus Umgebungsvariablen, EF-Migrations, PostgreSQL, CORS auf die
   App-Adresse einschränken, Login-Bremse hinter dem Reverse-Proxy je echter Adresse.~~ Phase 5.1.
5. **Phase 5.2:** SchILD-NRW-3-Schnittstelle (`.dat`-Dateien) – Import per Desktop statt Pseudodaten-Seed
   (5.2a Prüflauf und Zuordnung, 5.2b-1 Gültigkeit, 5.2b-2a Import in der API, 5.2b-2b-1 Reiter „Import“ im Desktop,
   5.2b-2b-2 „Folgestand erzeugen“, 5.2c-1 „dieselbe Person?“ und Neu-Zuordnung, **5.2c-2 Konten und Anzeigen**; als
   Nächstes E-1 Schuleinstellungen in der App und 5.3 Halbjahres-/Schuljahreswechsel).
