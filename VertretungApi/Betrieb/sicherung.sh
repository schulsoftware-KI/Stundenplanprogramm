#!/usr/bin/env bash
# =====================================================================
#  Verschlüsselte Sicherung der Klassenbuch-Datenbank (PostgreSQL, Linux) – Phase 5.1, ergänzt in PB-1b
#
#  Aufruf:
#    ./sicherung.sh                         neue Sicherung anlegen, alte nach Frist löschen
#    ./sicherung.sh --pruefen [DATEI]       neueste (bzw. die genannte) Sicherung in eine Testdatenbank
#                                           zurückspielen, Datensätze zählen, Testdatenbank wieder löschen
#    ./sicherung.sh --zurueckspielen DATEI  (PB-1b) die Datenbank durch den Stand der Sicherung ersetzen –
#                                           nur bei angehaltener API (Testserver: „vertretung zurueckspielen“)
#
#  Die Sicherung ist ein pg_dump (Format custom), mit gpg symmetrisch verschlüsselt
#  (AES-256). Ohne die Passphrase ist sie wertlos – Passphrase getrennt vom Server
#  aufbewahren (z. B. versiegelt im Tresor der Schulleitung).
#
#  Einstellungen: Umgebungsvariablen oder eine Datei sicherung.conf (nicht ins Repo – steht in .gitignore).
#  Gesucht wird (PB-1b) in dieser Reihenfolge: Datei aus SICHERUNG_CONF, sicherung.conf neben diesem Skript,
#  /etc/vertretung/sicherung.conf (Testserver, von Betrieb/linux/installieren.sh angelegt). Beispiel:
#      PGHOST=localhost
#      PGPORT=5432
#      PGUSER=vertretung
#      PGDATABASE=vertretung
#      PGPASSFILE=/etc/vertretung/pgpass
#      SICHERUNG_ORDNER=/var/backups/vertretung
#      SICHERUNG_PASSPHRASE_DATEI=/etc/vertretung/sicherung-passphrase
#      SICHERUNG_TAGE=30
#  Das Datenbankpasswort steht in ~/.pgpass bzw. in der Datei aus PGPASSFILE (Rechte 600) oder in PGPASSWORD.
#  Für --pruefen und --zurueckspielen braucht der Datenbankbenutzer das Recht CREATEDB und muss Eigentümer
#  der Datenbank sein. SICHERUNG_ZUSATZ (Buchstaben, Ziffern, Bindestriche) hängt einen Zusatz an den
#  Dateinamen an, z. B. „vor-update-3c6118d“ (Betrieb/linux/aktualisieren.sh).
#  Ausführen als Eigentümer des Sicherungsordners (Testserver: Benutzer „vertretung“ – „vertretung sicherung“).
#
#  Zeitplan: auf dem Testserver systemd-Timer (Betrieb/linux/vertretung-sicherung.timer, täglich 01:30;
#  vertretung-pruefung.timer, monatlich --pruefen). Ohne systemd z. B. per crontab des sichernden Benutzers:
#      30 1 * * * /pfad/zu/sicherung.sh >> /var/log/vertretung-sicherung.log 2>&1
#  Rücksicherung im Ernstfall: --zurueckspielen bzw. README, Abschnitt „Sicherung und Rücksicherung“.
# =====================================================================
set -euo pipefail
umask 077

SKRIPTORDNER="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -n "${SICHERUNG_CONF:-}" ]]; then
    [[ -r "$SICHERUNG_CONF" ]] || { echo "FEHLER: Einstellungsdatei $SICHERUNG_CONF (SICHERUNG_CONF) ist nicht lesbar." >&2; exit 1; }
    EINSTELLUNGSDATEI="$SICHERUNG_CONF"
elif [[ -f "$SKRIPTORDNER/sicherung.conf" ]]; then
    EINSTELLUNGSDATEI="$SKRIPTORDNER/sicherung.conf"
elif [[ -f /etc/vertretung/sicherung.conf ]]; then
    EINSTELLUNGSDATEI=/etc/vertretung/sicherung.conf
else
    EINSTELLUNGSDATEI=""
fi
if [[ -n "$EINSTELLUNGSDATEI" ]]; then
    set -a; . "$EINSTELLUNGSDATEI"; set +a
fi

export PGDATABASE="${PGDATABASE:-vertretung}"
ORDNER="${SICHERUNG_ORDNER:-/var/backups/vertretung}"
PASSPHRASE_DATEI="${SICHERUNG_PASSPHRASE_DATEI:-}"
TAGE="${SICHERUNG_TAGE:-30}"
ZUSATZ="${SICHERUNG_ZUSATZ:-}"
PRUEFDB="${PGDATABASE}_pruefung"

fehler() { echo "$(date '+%F %T') FEHLER: $*" >&2; exit 1; }
info()   { echo "$(date '+%F %T') $*"; }

# PB-1b: eine Aufräum-Falle für alle Fälle (halbe Sicherung, entpackte Kopie, Testdatenbank, gpg-Ordner).
AUFRAEUMEN=()
DB_LOESCHEN=""
GPG_TMP=""
aufraeumen() {
    local rc=$?
    if [[ -n "$DB_LOESCHEN" ]]; then dropdb --if-exists "$DB_LOESCHEN" >/dev/null 2>&1 || true; fi
    if [[ -n "$GPG_TMP" ]] && command -v gpgconf >/dev/null; then
        gpgconf --homedir "$GPG_TMP" --kill gpg-agent >/dev/null 2>&1 || true
    fi
    local p
    for p in "${AUFRAEUMEN[@]}"; do rm -rf -- "$p"; done
    exit "$rc"
}
trap aufraeumen EXIT

for prog in pg_dump pg_restore psql createdb dropdb gpg flock; do
    command -v "$prog" >/dev/null || fehler "$prog fehlt (Pakete postgresql-client, gnupg und util-linux installieren)."
done
[[ -n "$PASSPHRASE_DATEI" ]] || fehler "SICHERUNG_PASSPHRASE_DATEI ist nicht gesetzt."
[[ -r "$PASSPHRASE_DATEI" ]] || fehler "Passphrase-Datei $PASSPHRASE_DATEI ist nicht lesbar."
[[ -s "$PASSPHRASE_DATEI" ]] || fehler "Passphrase-Datei $PASSPHRASE_DATEI ist leer."
[[ "$TAGE" =~ ^[0-9]+$ ]] || fehler "SICHERUNG_TAGE muss eine Zahl sein."
# Der Name steht in SQL-Befehlen (Umbenennen beim Zurückspielen) – nur einfache Zeichen.
[[ "$PGDATABASE" =~ ^[A-Za-z0-9_]{1,50}$ ]] || fehler "PGDATABASE darf nur Buchstaben, Ziffern und _ enthalten."
[[ "$ZUSATZ" =~ ^[A-Za-z0-9-]{0,40}$ ]] || fehler "SICHERUNG_ZUSATZ darf nur Buchstaben, Ziffern und Bindestriche enthalten (höchstens 40)."

# PB-1b: eigener, leerer gpg-Ordner je Lauf (der Dienstbenutzer braucht kein ~/.gnupg).
GPG_TMP=$(mktemp -d)
AUFRAEUMEN+=("$GPG_TMP")
export GNUPGHOME="$GPG_TMP"
GPG=(gpg --batch --yes --quiet --pinentry-mode loopback --passphrase-file "$PASSPHRASE_DATEI")

# PB-1b: Sicherungsordner gehört dem, der sichert; nie zwei Läufe gleichzeitig (Timer und Update).
mkdir -p "$ORDNER"
[[ -O "$ORDNER" ]] || fehler "Der Ordner $ORDNER gehört nicht $(id -un) – bitte als dessen Eigentümer ausführen (Testserver: „vertretung sicherung“)."
exec 9>>"$ORDNER/.sicherung.sperre"
if ! flock -n 9; then
    info "Eine andere Sicherung bzw. Prüfung läuft gerade – warte (höchstens 30 Minuten) …"
    flock -w 1800 9 || fehler "Die andere Sicherung läuft seit über 30 Minuten – bitte nachsehen."
fi

entschluesseln() {   # $1 = Sicherung, $2 = Ziel
    "${GPG[@]}" --decrypt --output "$2" "$1" \
        || fehler "Entschlüsseln fehlgeschlagen – falsche Passphrase oder beschädigte Datei ($1)."
}

neueste_sicherung() {
    ls -1t "$ORDNER"/"${PGDATABASE}"_*.dump.gpg 2>/dev/null | head -n 1 || true
}

zaehlen() {   # $1 = Datenbank; Rückgabe: Anzahl Konten auf stdout (letzte Zeile)
    local tabelle anzahl
    for tabelle in Benutzer Schueler Lerngruppen KlassenbuchLektionen Anwesenheiten KlassenbuchEintraege GrundplanVersionen; do
        if anzahl=$(psql --dbname="$1" -Atqc "SELECT count(*) FROM \"$tabelle\"" 2>/dev/null); then
            printf '  %-22s %s\n' "$tabelle" "$anzahl" >&2
        else
            printf '  %-22s %s\n' "$tabelle" "(Tabelle fehlt)" >&2
        fi
    done
    psql --dbname="$1" -Atqc 'SELECT count(*) FROM "Benutzer"' 2>/dev/null || echo 0
}

sichern() {
    local ziel teil
    ziel="$ORDNER/${PGDATABASE}_$(date '+%Y-%m-%d_%H%M%S')${ZUSATZ:+_$ZUSATZ}.dump.gpg"
    [[ ! -e "$ziel" ]] || fehler "$ziel gibt es schon – bitte in einer Sekunde noch einmal."
    teil="$ziel.teil"
    AUFRAEUMEN+=("$teil")
    # pipefail: Bricht pg_dump ab, scheitert die ganze Zeile – es bleibt keine halbe Sicherung.
    pg_dump --format=custom --no-owner --no-privileges \
        | "${GPG[@]}" --symmetric --cipher-algo AES256 --output "$teil"
    mv "$teil" "$ziel"
    info "Sicherung angelegt: $ziel ($(du -h "$ziel" | cut -f1))"

    if (( TAGE > 0 )); then
        local alt
        alt=$(find "$ORDNER" -maxdepth 1 -name "${PGDATABASE}_*.dump.gpg" -mtime +"$TAGE" -print -delete | wc -l)
        if (( alt > 0 )); then info "$alt Sicherung(en) älter als $TAGE Tage gelöscht."; fi
    fi
    return 0
}

pruefen() {
    local datei="${1:-}"
    [[ -n "$datei" ]] || datei=$(neueste_sicherung)
    [[ -n "$datei" ]] || fehler "Keine Sicherung in $ORDNER gefunden."
    [[ -f "$datei" ]] || fehler "Die Sicherung $datei gibt es nicht."
    info "Prüfe $datei"

    local tmp
    tmp=$(mktemp -d)
    AUFRAEUMEN+=("$tmp")
    entschluesseln "$datei" "$tmp/sicherung.dump"
    DB_LOESCHEN="$PRUEFDB"
    dropdb --if-exists "$PRUEFDB" 2>/dev/null || fehler "Die alte Testdatenbank $PRUEFDB lässt sich nicht löschen."
    createdb "$PRUEFDB" || fehler "createdb meldet einen Fehler – hat $PGUSER das Recht CREATEDB?"
    pg_restore --no-owner --no-privileges --exit-on-error --dbname="$PRUEFDB" "$tmp/sicherung.dump" \
        || fehler "pg_restore meldet einen Fehler – die Sicherung ist unvollständig oder passt nicht."

    info "Zurückgespielt in $PRUEFDB. Datensätze:"
    local benutzer
    benutzer=$(zaehlen "$PRUEFDB")
    [[ "$benutzer" =~ ^[0-9]+$ && "$benutzer" -gt 0 ]] || fehler "Die Rücksicherung enthält keine Konten – Sicherung unbrauchbar?"
    info "Rücksicherungstest in Ordnung. Testdatenbank wird gelöscht."
}

# PB-1b: Datenbank durch den Stand einer Sicherung ersetzen. Erst in eine neue Datenbank zurückspielen und prüfen,
# dann tauschen (beide Umbenennungen in einem Schritt) – scheitert etwas vorher, bleibt die Datenbank, wie sie war.
zurueckspielen() {
    local datei="${1:-}"
    [[ -n "$datei" ]] || fehler "Welche Sicherung? Aufruf: sicherung.sh --zurueckspielen DATEI"
    [[ -f "$datei" ]] || fehler "Die Sicherung $datei gibt es nicht."
    local neu="${PGDATABASE}_rueck" alt="${PGDATABASE}_vor_rueck"
    info "Spiele $datei nach $PGDATABASE zurück"

    local tmp
    tmp=$(mktemp -d)
    AUFRAEUMEN+=("$tmp")
    entschluesseln "$datei" "$tmp/sicherung.dump"
    DB_LOESCHEN="$neu"
    dropdb --if-exists "$neu" 2>/dev/null || fehler "Die alte Datenbank $neu lässt sich nicht löschen."
    createdb "$neu" || fehler "createdb meldet einen Fehler – hat $PGUSER das Recht CREATEDB?"
    pg_restore --no-owner --no-privileges --exit-on-error --dbname="$neu" "$tmp/sicherung.dump" \
        || fehler "pg_restore meldet einen Fehler – die Datenbank $PGDATABASE bleibt unverändert."
    info "Zurückgespielt in $neu. Datensätze:"
    local benutzer
    benutzer=$(zaehlen "$neu")
    [[ "$benutzer" =~ ^[0-9]+$ && "$benutzer" -gt 0 ]] \
        || fehler "Die Sicherung enthält keine Konten – die Datenbank $PGDATABASE bleibt unverändert."

    local verbunden
    verbunden=$(psql --dbname=postgres -Atqc "SELECT count(*) FROM pg_stat_activity WHERE datname = '$PGDATABASE'") \
        || fehler "Die Verbindungen zu $PGDATABASE lassen sich nicht zählen."
    [[ "$verbunden" == "0" ]] || fehler "Mit $PGDATABASE sind noch $verbunden Verbindung(en) offen – erst die API anhalten (systemctl stop vertretung). Die Datenbank bleibt unverändert."

    dropdb --if-exists "$alt" 2>/dev/null || fehler "Die alte Datenbank $alt lässt sich nicht löschen."
    psql --dbname=postgres -v ON_ERROR_STOP=1 -qc \
        "ALTER DATABASE \"$PGDATABASE\" RENAME TO \"$alt\"; ALTER DATABASE \"$neu\" RENAME TO \"$PGDATABASE\";" \
        || fehler "Tauschen der Datenbanken fehlgeschlagen – $PGDATABASE ist unverändert, der zurückgespielte Stand liegt in $neu."
    DB_LOESCHEN="$alt"   # der ersetzte Stand wird am Ende gelöscht
    info "Datenbank $PGDATABASE ist jetzt auf dem Stand der Sicherung ($(basename "$datei"))."
}

case "${1:-}" in
    "")               sichern ;;
    --pruefen)        pruefen "${2:-}" ;;
    --zurueckspielen) zurueckspielen "${2:-}" ;;
    *)                fehler "Unbekannte Angabe „$1“. Aufruf: sicherung.sh [--pruefen [DATEI] | --zurueckspielen DATEI]" ;;
esac
