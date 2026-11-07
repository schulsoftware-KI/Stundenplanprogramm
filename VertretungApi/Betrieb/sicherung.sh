#!/usr/bin/env bash
# =====================================================================
#  Verschlüsselte Sicherung der Klassenbuch-Datenbank (PostgreSQL, Linux) – Phase 5.1
#
#  Aufruf:
#    ./sicherung.sh            neue Sicherung anlegen, alte nach Frist löschen
#    ./sicherung.sh --pruefen  neueste Sicherung in eine Testdatenbank zurückspielen,
#                              Datensätze zählen, Testdatenbank wieder löschen
#
#  Die Sicherung ist ein pg_dump (Format custom), mit gpg symmetrisch verschlüsselt
#  (AES-256). Ohne die Passphrase ist sie wertlos – Passphrase getrennt vom Server
#  aufbewahren (z. B. versiegelt im Tresor der Schulleitung).
#
#  Einstellungen: Umgebungsvariablen oder Datei sicherung.conf neben diesem Skript
#  (nicht ins Repo – steht in .gitignore). Beispiel sicherung.conf:
#      PGHOST=localhost
#      PGPORT=5432
#      PGUSER=vertretung
#      PGDATABASE=vertretung
#      SICHERUNG_ORDNER=/var/backups/vertretung
#      SICHERUNG_PASSPHRASE_DATEI=/root/.vertretung-sicherung-passphrase
#      SICHERUNG_TAGE=30
#  Das Datenbankpasswort steht in ~/.pgpass (Rechte 600) oder in PGPASSWORD.
#  Für --pruefen braucht der Datenbankbenutzer das Recht CREATEDB.
#
#  Täglich um 2:30 Uhr (crontab -e des Benutzers, der sichert):
#      30 2 * * * /opt/vertretung/Betrieb/sicherung.sh >> /var/log/vertretung-sicherung.log 2>&1
#  Rücksicherung im Ernstfall: siehe README, Abschnitt „Produktionsmodus (Phase 5.1)“.
# =====================================================================
set -euo pipefail
umask 077

SKRIPTORDNER="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$SKRIPTORDNER/sicherung.conf" ]]; then
    set -a; . "$SKRIPTORDNER/sicherung.conf"; set +a
fi

export PGDATABASE="${PGDATABASE:-vertretung}"
ORDNER="${SICHERUNG_ORDNER:-/var/backups/vertretung}"
PASSPHRASE_DATEI="${SICHERUNG_PASSPHRASE_DATEI:-}"
TAGE="${SICHERUNG_TAGE:-30}"
PRUEFDB="${PGDATABASE}_pruefung"

fehler() { echo "$(date '+%F %T') FEHLER: $*" >&2; exit 1; }
info()   { echo "$(date '+%F %T') $*"; }

for prog in pg_dump pg_restore psql createdb dropdb gpg; do
    command -v "$prog" >/dev/null || fehler "$prog fehlt (Pakete postgresql-client und gnupg installieren)."
done
[[ -n "$PASSPHRASE_DATEI" ]] || fehler "SICHERUNG_PASSPHRASE_DATEI ist nicht gesetzt."
[[ -r "$PASSPHRASE_DATEI" ]] || fehler "Passphrase-Datei $PASSPHRASE_DATEI ist nicht lesbar."
[[ -s "$PASSPHRASE_DATEI" ]] || fehler "Passphrase-Datei $PASSPHRASE_DATEI ist leer."
[[ "$TAGE" =~ ^[0-9]+$ ]] || fehler "SICHERUNG_TAGE muss eine Zahl sein."

GPG=(gpg --batch --yes --quiet --pinentry-mode loopback --passphrase-file "$PASSPHRASE_DATEI")

sichern() {
    mkdir -p "$ORDNER"
    local ziel="$ORDNER/${PGDATABASE}_$(date '+%Y-%m-%d_%H%M%S').dump.gpg"
    SICH_TEIL="$ziel.teil"   # global: die Falle läuft ggf. nach dem Ende der Funktion
    local teil="$SICH_TEIL"
    trap 'rm -f "$SICH_TEIL"' EXIT
    # pipefail: Bricht pg_dump ab, scheitert die ganze Zeile – es bleibt keine halbe Sicherung.
    pg_dump --format=custom --no-owner --no-privileges \
        | "${GPG[@]}" --symmetric --cipher-algo AES256 --output "$teil"
    mv "$teil" "$ziel"
    trap - EXIT
    info "Sicherung angelegt: $ziel ($(du -h "$ziel" | cut -f1))"

    if (( TAGE > 0 )); then
        local alt
        alt=$(find "$ORDNER" -maxdepth 1 -name "${PGDATABASE}_*.dump.gpg" -mtime +"$TAGE" -print -delete | wc -l)
        (( alt > 0 )) && info "$alt Sicherung(en) älter als $TAGE Tage gelöscht."
    fi
    return 0
}

pruefen() {
    local neueste
    neueste=$(ls -1t "$ORDNER"/"${PGDATABASE}"_*.dump.gpg 2>/dev/null | head -n 1 || true)
    [[ -n "$neueste" ]] || fehler "Keine Sicherung in $ORDNER gefunden."
    info "Prüfe $neueste"

    # global (nicht local): Die Aufräum-Falle läuft erst beim Beenden des Skripts.
    PRUEF_TMP=$(mktemp -d)
    trap 'rm -rf "$PRUEF_TMP"; dropdb --if-exists "$PRUEFDB" >/dev/null 2>&1 || true' EXIT

    "${GPG[@]}" --decrypt --output "$PRUEF_TMP/sicherung.dump" "$neueste"
    dropdb --if-exists "$PRUEFDB" 2>/dev/null
    createdb "$PRUEFDB"
    pg_restore --no-owner --no-privileges --exit-on-error --dbname="$PRUEFDB" "$PRUEF_TMP/sicherung.dump"

    info "Zurückgespielt in $PRUEFDB. Datensätze:"
    local tabelle anzahl
    for tabelle in Benutzer Schueler Lerngruppen KlassenbuchLektionen Anwesenheiten KlassenbuchEintraege GrundplanVersionen; do
        if anzahl=$(psql --dbname="$PRUEFDB" -Atqc "SELECT count(*) FROM \"$tabelle\"" 2>/dev/null); then
            printf '  %-22s %s\n' "$tabelle" "$anzahl"
        else
            printf '  %-22s %s\n' "$tabelle" "(Tabelle fehlt)"
        fi
    done
    local benutzer
    benutzer=$(psql --dbname="$PRUEFDB" -Atqc 'SELECT count(*) FROM "Benutzer"' 2>/dev/null || echo 0)
    [[ "$benutzer" =~ ^[0-9]+$ && "$benutzer" -gt 0 ]] || fehler "Die Rücksicherung enthält keine Konten – Sicherung unbrauchbar?"
    info "Rücksicherungstest in Ordnung. Testdatenbank wird gelöscht."
}

case "${1:-}" in
    "")          sichern ;;
    --pruefen)   pruefen ;;
    *)           fehler "Unbekannte Angabe „$1“. Aufruf: sicherung.sh [--pruefen]" ;;
esac
