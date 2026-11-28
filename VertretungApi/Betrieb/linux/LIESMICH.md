# Testserver einrichten (Linux, Probebetrieb) – Kurzanleitung

Stand: PB-1b · Für einen **Testserver mit Pseudodaten** (Betriebsart „Probebetrieb“): Ubuntu 24.04 LTS, z. B. ein gemieteter
VPS bei Hostinger. Echte Schülerdaten gehören nicht auf diesen Server. Der Echtserver einer Schule wird später eigens
eingerichtet (Installationspaket, P9).

## Voraussetzungen

- VPS mit **Ubuntu 24.04 LTS ohne Control Panel** (kein cPanel, Plesk, Coolify …), Standort Deutschland, mindestens
  4 GB RAM und 50 GB Platte (bei Hostinger: KVM 1).
- Ein Name, der auf den Server zeigt. Ohne gekaufte Domain reicht meist der Name des Servers bei Hostinger
  (`srv…hstgr.cloud`); sonst schlägt das Skript `<ip-mit-bindestrichen>.sslip.io` vor.
- Die Firewall im hPanel bleibt **aus** – es gilt nur `ufw` auf dem Server (SSH, 80, 443).
- Zugang als root: Browser-Terminal im hPanel (VPS → Verwalten → Terminal) oder SSH.

## Einrichten (zwei Zeilen im Browser-Terminal)

```bash
curl -fsSLO https://raw.githubusercontent.com/schulsoftware-KI/Stundenplanprogramm/master/VertretungApi/Betrieb/linux/installieren.sh
bash installieren.sh
```

Das Skript fragt nur dreimal:

1. **„Diesen Stand installieren?“** – zeigt den neuesten Commit von master (Kurzform, Datum, Betreff). `j` = weiter; danach
   läuft alles mit den Skripten genau dieses Commits.
2. **Adresse** – Vorschlag mit Enter übernehmen oder einen anderen Namen eintippen. Das Skript prüft, ob der Name auf
   diesen Server zeigt.
3. **SSH-Passwortanmeldung abschalten?** – nur, wenn ein SSH-Schlüssel hinterlegt ist; Standard ist „nein“ (siehe unten).

Dann richtet es ein (dauert etwa 10–20 Minuten): Zeitzone Europe/Berlin, Updates, Swap 2 GB, Journal 30 Tage/500 MB,
PostgreSQL 17 und Caddy aus den offiziellen Quellen (Schlüssel mit Fingerabdruck geprüft), .NET 10 aus Ubuntu, automatische
Sicherheitsupdates (Neustart bei Bedarf um 04:30), die Benutzer `vertretung` (Dienst) und `vertretung-bau` (Bau), das
Programm (gebaut aus dem Repo), die Datenbank, die Einstellungsdatei mit neuen Schlüsseln, den Dienst, die Firewall, HTTPS
mit Let's-Encrypt-Zertifikat und die Sicherung (täglich 01:30, monatlich ein Rücksicherungstest).

**Einmal angezeigt** werden am Ende: die Adresse, der **Push-Schlüssel** (für den Desktop) und die **Passphrase der
Sicherung** (in den Passwortmanager – ohne sie ist jede Sicherung wertlos), dazu das **Startpasswort** des Kontos `admin`.
Nicht abfotografieren, nicht in einen Chat kopieren; danach `clear` eingeben. Schlüssel und Passphrase zeigt
`vertretung schluessel` später noch einmal.

**Wiederholbar:** `bash installieren.sh` noch einmal ergänzt nur, was fehlt – keine neuen Schlüssel, keine neue Datenbank,
kein neues Admin-Konto, kein Update.

## Nach der Einrichtung

1. Browser: `https://<adresse>` → Band **„Probebetrieb – nur Testdaten“** → anmelden als `admin` mit dem Startpasswort →
   eigenes Passwort → **zweiter Faktor** (Pflicht für Admins, z. B. Microsoft Authenticator) → Einstellungen der Schule.
2. Desktop verbinden: Wocheneditor → „Server verbinden…“ → Verbindung „Testserver“: `https://<adresse>` und den
   Push-Schlüssel (`vertretung schluessel`) eintragen → „Verbindung testen“ (zeigt „Probebetrieb“) → OK; danach
   Grundplan veröffentlichen, SchILD-Pseudo-Import.
3. Kolleg:innen nur mit Pseudo-Konten (z. B. ein Kürzel aus dem Pseudo-SchILD), nie mit echten Namen.

## Befehle (als root)

| Befehl | Wirkung |
|---|---|
| `vertretung status` | Dienst, Version, Betriebsart, Adresse, Zertifikat, Datenbank, Sicherungen, Platz |
| `vertretung log [n] [-f] [caddy\|sicherung\|pruefung]` | Log der API (Standard 50 Zeilen; `-f` laufend, Ende mit Strg+C) |
| `vertretung admin-anlegen [name]` | Admin-Konto anlegen bzw. neues Startpasswort (setzt auch den zweiten Faktor zurück) |
| `vertretung neustart` | API neu starten und prüfen |
| `vertretung sicherung` · `vertretung pruefen [datei]` | jetzt sichern · Rücksicherungstest |
| `vertretung zurueckspielen [datei]` | Datenbank auf den Stand einer Sicherung setzen (ohne Datei: Liste) |
| `vertretung aktualisieren [commit]` | Update auf den neuesten Stand von master bzw. einen Commit daraus |
| `vertretung zurueck` | eine Version zurück |
| `vertretung schluessel` | Push-Schlüssel und Passphrase noch einmal zeigen |
| `vertretung adresse <name>` | andere Adresse (AllowedHosts, Caddy, Zertifikat) |
| `vertretung ssh-passwort-aus` | SSH nur noch mit Schlüssel |

## Update und Rückweg

`vertretung aktualisieren` zeigt alten und neuen Commit und die **neuen Migrationen** und fragt „Weiter?“. Dann: bauen (als
`vertretung-bau`; scheitert der Bau, läuft die alte Version unverändert weiter) → **Sicherung** (`…_vor-update-<commit>`;
ohne Sicherung kein Update) → anhalten → umstellen → starten → prüfen (Lebenszeichen, alle Migrationen in der Datenbank,
HTTPS). **Scheitert der Start**, geht das Skript auf die alte Version zurück; hat das Update schon Migrationen angewendet,
fragt es, ob die Datenbank aus der Sicherung von eben zurückgespielt wird (empfohlen: ja – das alte Programm kennt das
neue Schema nicht). Es bleiben die laufende und die zwei neuesten anderen Versionen.

`vertretung zurueck` geht eine Version zurück. Kennt die vorige Version eine Migration der Datenbank nicht, geht das nur mit
der Sicherung von vor dem Update (Rückfrage; alles seitdem Eingetragene geht verloren, der jetzige Stand wird vorher
gesichert). Ein älterer Commit über `vertretung aktualisieren <commit>` wird in diesem Fall abgelehnt.

## Sicherung und Rücksicherung

Sicherungen liegen verschlüsselt (gpg, AES-256) in `/var/backups/vertretung` (30 Tage). `vertretung zurueckspielen <datei>`
sichert erst den jetzigen Stand, hält die API an, spielt die Sicherung in eine neue Datenbank, prüft sie und tauscht dann
(scheitert etwas vorher, bleibt die Datenbank, wie sie war). Eine Sicherung auf den eigenen Rechner holen: per `scp` mit
SSH-Schlüssel; entschlüsseln mit `gpg --decrypt datei.dump.gpg > sicherung.dump` (fragt nach der Passphrase), dann wie
in der README („Sicherung und Rücksicherung“).

## Aufbau auf dem Server

| Ort | Inhalt |
|---|---|
| `/opt/vertretung/quelle` | Klon des Repos (root; nur `VertretungApi/`) |
| `/opt/vertretung/versionen/<zeit>_<commit>/` | gebaute Version: `app/` (Programm), `betrieb/` (Skripte und Units desselben Commits) |
| `/opt/vertretung/aktuell` | Verweis auf die laufende Version |
| `/etc/vertretung/appsettings.Production.json` | Einstellungen und Schlüssel (root:vertretung, 640) – per `VERTRETUNG_EINSTELLUNGEN` |
| `/etc/vertretung/sicherung.conf`, `sicherung-passphrase`, `pgpass`, `adresse` | Sicherung, Datenbankpasswort, Adresse |
| `/var/backups/vertretung` | Sicherungen (nur `vertretung`) |
| `/etc/caddy/Caddyfile` | aus `Caddyfile.vorlage` (ohne Zugriffsprotokoll) |

**Einstellungen ändern** (z. B. Schuladressen für die Tests der Adressbeschränkung): `nano /etc/vertretung/appsettings.Production.json`
→ speichern → `vertretung neustart` (meldet Tippfehler aus der Startprüfung). Schulwerte (Name, Fristen …) stehen in der
App unter „Einstellungen“, nicht in der Datei.

Sicherheit: Der Dienst läuft ohne Rechte nur auf `127.0.0.1:5080`; nach außen geht es nur über Caddy (HTTPS). PostgreSQL hört
nur auf localhost. Gebaut wird als `vertretung-bau` in dessen eigenem Ordner; root übernimmt nur das fertige Programm, und
die Skripte, die root ausführt, kommen nur aus dem Klon von root. Logs: Journal 30 Tage, Caddy ohne Zugriffsprotokoll.

## SSH-Schlüssel

Schlüssel im hPanel hinterlegen, die Anmeldung vom PC aus testen (`ssh root@<adresse>`), erst dann
`vertretung ssh-passwort-aus`. Das Browser-Terminal von Hostinger meldet sich mit einem eigenen, vorübergehenden Schlüssel
an und geht danach weiter (root bleibt mit Schlüssel erlaubt). Rückgängig: `/etc/ssh/sshd_config.d/01-vertretung.conf`
löschen, `systemctl reload ssh`.

## Neu aufbauen

hPanel → VPS → „Betriebssystem neu installieren“ (Ubuntu 24.04 ohne Panel) → die zwei Zeilen oben. Alles entsteht neu
(neue Schlüssel, neue Datenbank, neue Passphrase – alte Sicherungen lassen sich nur mit der alten Passphrase öffnen).
**Let's Encrypt** stellt für **denselben Namen höchstens 5 Zertifikate in 7 Tagen** aus – also höchstens etwa fünf
Neuaufbauten je Woche mit derselben Adresse.

## Typische Meldungen

- **`fail: … Failed executing DbCommand` (`__EFMigrationsHistory`) beim allerersten Start** – erwartet (leere Datenbank),
  kein Fehler. Danach steht „Datenbank auf den neuesten Stand gebracht: …“ mit allen Migrationen.
- **„https://… antwortet nicht“** – der Name zeigt nicht auf den Server (DNS), Port 80/443 ist zu (Firewall im hPanel an?),
  oder Let's Encrypt hat eine Mengengrenze erreicht (bei sslip.io häufig, weil sich alle Nutzer den Namen teilen). Log:
  `vertretung log caddy`; anderen Namen: `vertretung adresse <name>`.
- **„Die API startet nicht“** – die Zeilen darunter stammen aus der Startprüfung bzw. dem Log (oft ein Tippfehler in der
  Einstellungsdatei); nach der Korrektur `vertretung neustart`.
- **„Es läuft schon ein anderer Vorgang“** – Installieren, Update, Zurück und Zurückspielen laufen nie gleichzeitig.
- **Neustart nötig** (`vertretung status`) – nach Kernel-Updates; geschieht automatisch um 04:30.
