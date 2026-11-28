#!/usr/bin/env bash
# =====================================================================
#  Vertretung – Testserver einrichten (Ubuntu 24.04 LTS, als root) – PB-1b, Entscheidungen 300, 304, 305, 312–317
#
#  Laden und starten im Browser-Terminal von Hostinger (zwei Zeilen, siehe LIESMICH.md):
#      curl -fsSLO https://raw.githubusercontent.com/schulsoftware-KI/Stundenplanprogramm/master/VertretungApi/Betrieb/linux/installieren.sh
#      bash installieren.sh
#  Das Skript zeigt den neuesten Commit von master und fragt, ob dieser Stand installiert wird; danach läuft es mit
#  den Skripten genau dieses Commits weiter. Gefragt wird sonst nur die Adresse (Vorschlag mit Enter übernehmen) und –
#  falls ein SSH-Schlüssel hinterlegt ist – ob die SSH-Passwortanmeldung abgeschaltet wird.
#
#  Wiederholbar: Ein zweiter Lauf ergänzt nur, was fehlt – nie neue Schlüssel, nie eine neue Datenbank, kein neues
#  Admin-Konto, kein Update (dafür: vertretung aktualisieren).
#  Weitere Aufrufe (meist über den Befehl „vertretung“):
#      installieren.sh --adresse NAME       andere Adresse (AllowedHosts, Caddyfile, Zertifikat)
#      installieren.sh --ssh-passwort-aus   SSH-Anmeldung nur noch mit Schlüssel
#
#  Schritte: 1 Prüfen · 2 Stand · 3 Adresse · 4 System (Zeitzone, Updates, Swap, Journal) · 5 Pakete (PostgreSQL 17,
#  Caddy, .NET 10, ufw …) · 6 Benutzer und Ordner · 7 Programm bauen · 8 Datenbank · 9 Einstellungen und Schlüssel ·
#  10 Dienst · 11 Firewall · 12 Caddy (HTTPS) · 13 Sicherung · 14 SSH · 15 Abschluss (Schlüssel einmal, Admin-Konto)
# =====================================================================
set -euo pipefail
umask 022

if [[ ! -f "${BASH_SOURCE[0]:-}" ]]; then
    echo "Bitte das Skript erst herunterladen und dann starten (nicht über eine Pipe) – siehe LIESMICH.md." >&2
    exit 1
fi
SELBST="$(readlink -f "${BASH_SOURCE[0]}")"
HIER="$(dirname "$SELBST")"

# Fingerabdrücke der Paketquellen (geprüft 28.11.2026: postgresql.org/download/linux/ubuntu, cloudsmith.io/~caddy)
PGDG_FP="B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8"
CADDY_FP="65760C51EDEA2017CEA2CA15155B6D79CA56EA34"
PGDG_SCHLUESSEL_URL="https://www.postgresql.org/media/keys/ACCC4CF8.asc"
CADDY_SCHLUESSEL_URL="https://dl.cloudsmith.io/public/caddy/stable/gpg.key"

# ---------------------------------------------------------------------
#  Vorlauf: Allein geladen (ohne gemeinsam.sh daneben) holt das Skript den Stand aus dem Repo, lässt ihn bestätigen
#  und startet sich aus genau diesem Commit neu. Ist schon eine Version installiert, übernimmt deren Skript.
# ---------------------------------------------------------------------
vorlauf() {
    local wurzel="${PRUEF_WURZEL:-}" opt quelle aktuell repo commit antwort ziel
    opt="$wurzel/opt/vertretung"; quelle="$opt/quelle"; aktuell="$opt/aktuell"
    repo="https://github.com/schulsoftware-KI/Stundenplanprogramm.git"
    if [[ -n "$wurzel" && -n "${PRUEF_REPO:-}" ]]; then repo="$PRUEF_REPO"; fi
    [[ $EUID -eq 0 ]] || { echo "FEHLER: Bitte als root ausführen." >&2; exit 1; }

    if [[ -f "$aktuell/FERTIG" && -x "$aktuell/betrieb/installieren.sh" && ! "$aktuell/betrieb/installieren.sh" -ef "$SELBST" ]]; then
        exec "$aktuell/betrieb/installieren.sh" "$@"
    fi

    echo
    echo "== Vertretung – Testserver einrichten =="
    if ! command -v git >/dev/null; then
        echo "   git wird installiert …"
        DEBIAN_FRONTEND=noninteractive apt-get -q -o DPkg::Lock::Timeout=600 update >/dev/null \
            && DEBIAN_FRONTEND=noninteractive apt-get -y -q -o DPkg::Lock::Timeout=600 install --no-install-recommends git ca-certificates >/dev/null \
            || { echo "FEHLER: git lässt sich nicht installieren (apt-get)." >&2; exit 1; }
    fi
    mkdir -p "$opt"
    if [[ -d "$quelle/.git" ]]; then
        git -C "$quelle" fetch --quiet origin "+refs/heads/master:refs/remotes/origin/master" \
            || { echo "FEHLER: Das Repo lässt sich nicht abrufen ($repo)." >&2; exit 1; }
    else
        rm -rf -- "$quelle"
        git clone --quiet --filter=blob:none --no-checkout --branch master "$repo" "$quelle" \
            || { echo "FEHLER: Das Repo lässt sich nicht klonen ($repo) – Internet bzw. GitHub prüfen." >&2; exit 1; }
        git -C "$quelle" sparse-checkout set --cone VertretungApi
    fi
    commit=$(git -C "$quelle" rev-parse --verify "origin/master^{commit}")
    echo "   Neuester Stand von master:"
    git -C "$quelle" log -1 --date=format-local:'%d.%m.%Y %H:%M' --format='      %h · %cd · %s' "$commit"
    printf '   Diesen Stand installieren? [j/N] '
    read -r antwort || antwort=""
    case "${antwort,,}" in
        j|ja|y|yes) ;;
        *) echo "   Abgebrochen – nichts installiert."; exit 0 ;;
    esac
    if [[ -z "$(git -C "$quelle" ls-tree --name-only "$commit" VertretungApi/Betrieb/linux/gemeinsam.sh)" ]]; then
        echo "FEHLER: Dieser Stand enthält keine Linux-Skripte (VertretungApi/Betrieb/linux)." >&2
        exit 1
    fi
    ziel="$opt/einrichtung/${commit:0:12}"
    rm -rf -- "$ziel"
    mkdir -p "$ziel"
    git -C "$quelle" -c advice.detachedHead=false checkout --quiet --force --detach "$commit"
    git -C "$quelle" archive "$commit" VertretungApi/Betrieb | tar -x -C "$ziel"
    exec bash "$ziel/VertretungApi/Betrieb/linux/installieren.sh" --aus-quelle "$commit" "$@"
}

if [[ ! -f "$HIER/gemeinsam.sh" ]]; then
    vorlauf "$@"
fi
# shellcheck source=gemeinsam.sh
. "$HIER/gemeinsam.sh"
if [[ -n "$WURZEL" ]]; then
    PGDG_FP="${PRUEF_PGDG_FP:-$PGDG_FP}"; CADDY_FP="${PRUEF_CADDY_FP:-$CADDY_FP}"
fi
APT_LOG="$OPT/installieren-apt.log"
CODENAME="noble"
ADRESSE=""
DB_PASSWORT=""
DB_PORT="5432"
DB_PASSWORT_NEU=0
NEUE_SCHLUESSEL=0
NEUE_PASSPHRASE=0
HTTPS_OK=0

aufruf() {
    cat <<'EOF'
Aufruf (als root):
  bash installieren.sh                  einrichten bzw. Fehlendes ergänzen
  installieren.sh --adresse NAME        andere Adresse (bequemer: vertretung adresse NAME)
  installieren.sh --ssh-passwort-aus    SSH nur noch mit Schlüssel (bequemer: vertretung ssh-passwort-aus)
EOF
}

apt_laufen() {   # apt-get mit Wartezeit auf die Paketsperre; Ausgabe ins Protokoll, bei Fehler die letzten Zeilen
    if ! apt-get -y -q -o DPkg::Lock::Timeout=600 -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold \
            "$@" >> "$APT_LOG" 2>&1; then
        tail -n 20 "$APT_LOG" | sed 's/^/      /' >&2
        fehler "apt-get $1 ist fehlgeschlagen (Protokoll: $APT_LOG)."
    fi
}

# ---------------------------------------------------------------------
#  1 Prüfen
# ---------------------------------------------------------------------
system_pruefen() {
    local name frei ram
    if [[ -z "$WURZEL" ]]; then
        name=$( . /etc/os-release; printf '%s|%s|%s' "${ID:-}" "${VERSION_ID:-}" "${PRETTY_NAME:-?}" )
        if [[ "${name%%|*}" != ubuntu || "$(cut -d'|' -f2 <<<"$name")" != 24.04 ]]; then
            warnung "Geprüft ist das Skript nur für Ubuntu 24.04 LTS (hier: ${name##*|})."
            frage_jn "Trotzdem weiter?" n || exit 1
        fi
        CODENAME=$( . /etc/os-release; printf '%s' "${VERSION_CODENAME:-noble}" )
    fi
    frei=$(df -Pk "${WURZEL:-/}" | awk 'NR == 2 { print int($4 / 1024 / 1024) }')
    ram=$(awk '/^MemTotal:/ { print int($2 / 1024) }' /proc/meminfo)
    info "Ubuntu ($CODENAME) · Speicher $ram MB · frei $frei GB"
    (( frei >= 6 )) || fehler "Zu wenig freier Platz ($frei GB; nötig sind etwa 6 GB für Pakete, Bau und Versionen)."
    curl -fsS -m 20 -o /dev/null https://github.com || fehler "Keine Verbindung zu github.com – Internet prüfen."
    ok "System geprüft."
}

# ---------------------------------------------------------------------
#  3 Adresse (304): Name des VPS bei Hostinger, sonst <ip>.sslip.io, sonst eine eigene Domain
# ---------------------------------------------------------------------
oeffentliche_ipv4() {
    ip -4 -o addr show scope global 2>/dev/null | awk '{ split($4, a, "/"); print a[1] }' \
        | grep -vE '^(10\.|127\.|169\.254\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.|100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.)' \
        | head -n 1 || true
}

adresse_gueltig() {
    [[ ${#1} -le 253 && "$1" =~ ^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$ ]]
}

adresse_zeigt_hierher() {   # $1 = Name → „ok“, „v6“ (fremde IPv6-Adresse) oder „nein“
    local ip4 a4 a6 eigene6 x
    ip4=$(oeffentliche_ipv4)
    a4=$(getent ahostsv4 "$1" 2>/dev/null | awk '{ print $1 }' | LC_ALL=C sort -u || true)
    if [[ -z "$ip4" ]] || ! grep -qxF "$ip4" <<<"$a4"; then echo nein; return 0; fi
    a6=$(getent ahostsv6 "$1" 2>/dev/null | awk '{ print $1 }' | grep -v '^::ffff:' | LC_ALL=C sort -u || true)
    if [[ -n "$a6" ]]; then
        eigene6=$(ip -6 -o addr show scope global 2>/dev/null | awk '{ split($4, a, "/"); print a[1] }' || true)
        for x in $a6; do
            if ! grep -qxF "$x" <<<"$eigene6"; then echo v6; return 0; fi
        done
    fi
    echo ok
}

# adresse_fragen → ADRESSE
adresse_fragen() {
    local ip4 hn vorschlag="" name versuche=0
    ip4=$(oeffentliche_ipv4)
    if [[ -n "$ip4" ]]; then info "Öffentliche IPv4-Adresse: $ip4"; else warnung "Keine öffentliche IPv4-Adresse gefunden."; fi
    hn=$(hostname -f 2>/dev/null || hostname 2>/dev/null || true)
    hn="${hn,,}"
    if [[ "$hn" == *.* ]] && adresse_gueltig "$hn" && [[ "$(adresse_zeigt_hierher "$hn")" == ok ]]; then
        vorschlag="$hn"
        info "Vorschlag: der Name des Servers bei Hostinger – er zeigt öffentlich auf diese Adresse."
    elif [[ -n "$ip4" ]]; then
        vorschlag="${ip4//./-}.sslip.io"
        info "Der Name des Servers (${hn:-?}) zeigt nicht öffentlich hierher – Vorschlag über sslip.io (kostenlos)."
        info "Hinweis: Alle Nutzer von sslip.io teilen sich bei Let's Encrypt eine Mengengrenze – klappt es nicht, später"
        info "„vertretung adresse <name>“ mit einem anderen Namen."
    fi
    info "Eine andere Adresse (z. B. eine eigene Domain) einfach eintippen; Enter übernimmt den Vorschlag."
    while true; do
        versuche=$((versuche + 1))
        (( versuche <= 5 )) || fehler "Keine gültige Adresse angegeben."
        name=$(eingabe "Adresse:" "$vorschlag")
        name="${name,,}"; name="${name%.}"
        if ! adresse_gueltig "$name"; then
            warnung "„$name“ ist kein gültiger Name (Beispiel: srv123456.hstgr.cloud)."
            continue
        fi
        case "$(adresse_zeigt_hierher "$name")" in
            ok) ok "$name zeigt auf diesen Server."; break ;;
            v6) warnung "$name hat eine IPv6-Adresse, die nicht zu diesem Server gehört – Let's Encrypt prüft dann ggf. dort."
                if frage_jn "Trotzdem nehmen?" n; then break; fi ;;
            *)  warnung "$name zeigt (noch) nicht auf ${ip4:-diesen Server}."
                if frage_jn "Trotzdem nehmen (z. B. weil der DNS-Eintrag gerade erst angelegt wurde)?" n; then break; fi ;;
        esac
    done
    ADRESSE="$name"
}

# ---------------------------------------------------------------------
#  4 System
# ---------------------------------------------------------------------
system_einrichten() {
    local erst="$1"
    if [[ "$(timedatectl show -p Timezone --value 2>/dev/null || true)" != "Europe/Berlin" ]]; then
        timedatectl set-timezone Europe/Berlin
        geaendert "Zeitzone des Servers: Europe/Berlin (die API rechnet ohnehin mit Betrieb:Zeitzone)"
    else
        ok "Zeitzone: Europe/Berlin"
    fi
    if (( erst )); then
        info "Updates des Systems (einige Minuten) …"
        apt_laufen update
        apt_laufen upgrade
        ok "System auf dem neuesten Stand."
    fi
    # Swap 2 GB gegen Speichermangel beim Bauen (F8)
    if [[ -n "$(swapon --show --noheadings 2>/dev/null || true)" ]]; then
        ok "Swap: vorhanden"
    else
        if [[ ! -f "$WURZEL/swapfile" ]]; then
            fallocate -l 2G "$WURZEL/swapfile" 2>/dev/null || dd if=/dev/zero of="$WURZEL/swapfile" bs=1M count=2048 status=none
            chmod 600 "$WURZEL/swapfile"
            mkswap "$WURZEL/swapfile" >/dev/null
        fi
        if swapon "$WURZEL/swapfile" 2>/dev/null; then
            if ! grep -qE '^/swapfile[[:space:]]' "$WURZEL/etc/fstab" 2>/dev/null; then
                echo "/swapfile none swap sw 0 0" >> "$WURZEL/etc/fstab"
            fi
            geaendert "Swap 2 GB (/swapfile)"
        else
            warnung "Swap ließ sich nicht einschalten – der Bau kann bei wenig Speicher scheitern."
        fi
    fi
    # Journal: höchstens 30 Tage bzw. 500 MB (317)
    if datei_setzen "$WURZEL/etc/systemd/journald.conf.d/60-vertretung.conf" 644 root:root <<'EOF'
# Vertretung (PB-1b, Entscheidung 317): Journal höchstens 30 Tage bzw. 500 MB.
[Journal]
SystemMaxUse=500M
MaxRetentionSec=30day
EOF
    then
        systemctl restart systemd-journald
        geaendert "Journal: höchstens 30 Tage bzw. 500 MB"
    else
        ok "Journal: 30 Tage / 500 MB"
    fi
}

# ---------------------------------------------------------------------
#  5 Pakete (314): PostgreSQL 17 und Caddy aus den offiziellen Quellen (Schlüssel mit Fingerabdruck geprüft),
#    .NET 10 aus Ubuntu; Sicherheitsupdates automatisch, Neustart bei Bedarf um 04:30 (F7)
# ---------------------------------------------------------------------
fingerabdruck() {
    local g fp
    g=$(mktemp -d)
    fp=$(GNUPGHOME="$g" gpg --batch --show-keys --with-colons "$1" 2>/dev/null | awk -F: '$1 == "fpr" && !x { print $10; x = 1 }' || true)
    rm -rf -- "$g"
    printf '%s' "$fp"
}

quelle_einrichten() {   # NAME SCHLÜSSEL-URL FINGERABDRUCK URI SUITE
    local name="$1" url="$2" fp="$3" uri="$4" suite="$5" schluessel="" k tmp
    for k in "$WURZEL/etc/apt/keyrings/$name.asc" "$WURZEL/etc/apt/keyrings/$name.gpg"; do
        if [[ -s "$k" && "$(fingerabdruck "$k")" == "$fp" ]]; then schluessel="$k"; fi
    done
    if [[ -z "$schluessel" ]]; then
        tmp=$(mktemp)
        curl -fsSL -m 60 "$url" -o "$tmp" || fehler "Der Schlüssel für $name lässt sich nicht laden ($url)."
        [[ "$(fingerabdruck "$tmp")" == "$fp" ]] \
            || fehler "Der Schlüssel von $url hat nicht den erwarteten Fingerabdruck $fp – abgebrochen."
        if head -c 64 "$tmp" | grep 'BEGIN PGP' >/dev/null; then schluessel="$WURZEL/etc/apt/keyrings/$name.asc"
        else schluessel="$WURZEL/etc/apt/keyrings/$name.gpg"; fi
        install -D -m 644 -o root -g root "$tmp" "$schluessel"
        rm -f "$tmp"
        geaendert "Paketquelle $name: Schlüssel geprüft (${fp: -16}) und abgelegt"
    fi
    if datei_setzen "$WURZEL/etc/apt/sources.list.d/$name.sources" 644 root:root <<EOF
Types: deb
URIs: $uri
Suites: $suite
Components: main
Signed-By: ${schluessel#"$WURZEL"}
EOF
    then geaendert "Paketquelle $name eingetragen"; else ok "Paketquelle $name: vorhanden"; fi
}

pakete_einrichten() {
    local laufzeiten
    apt_laufen install --no-install-recommends ca-certificates curl gnupg git python3
    quelle_einrichten postgresql "$PGDG_SCHLUESSEL_URL" "$PGDG_FP" "https://apt.postgresql.org/pub/repos/apt" "$CODENAME-pgdg"
    quelle_einrichten caddy-stable "$CADDY_SCHLUESSEL_URL" "$CADDY_FP" "https://dl.cloudsmith.io/public/caddy/stable/deb/debian" "any-version"
    info "Pakete installieren (einige Minuten) …"
    apt_laufen update
    apt_laufen install --no-install-recommends "postgresql-$PG_VERSION" "postgresql-client-$PG_VERSION" caddy \
        dotnet-sdk-10.0 aspnetcore-runtime-10.0 ufw unattended-upgrades fontconfig fonts-dejavu-core fonts-liberation2 \
        libicu74 tzdata openssl
    laufzeiten=$(dotnet --list-runtimes 2>/dev/null || true)
    grep -E '^Microsoft\.AspNetCore\.App 10\.' >/dev/null <<<"$laufzeiten" \
        || fehler "Die ASP.NET-Laufzeit 10 fehlt nach der Installation (dotnet --list-runtimes)."
    ok "Pakete: PostgreSQL $PG_VERSION, Caddy $(caddy version 2>/dev/null | awk '{ print $1 }' || true), .NET SDK $(dotnet --version 2>/dev/null || true)"

    if datei_setzen "$WURZEL/etc/apt/apt.conf.d/52vertretung-unattended-upgrades" 644 root:root <<EOF
// Vertretung (PB-1b, 314, F7): Updates automatisch auch aus den Quellen von PostgreSQL und Caddy
// (zusätzlich zu den Sicherheitsupdates von Ubuntu); Neustart bei Bedarf um 04:30.
Unattended-Upgrade::Origins-Pattern {
        "origin=apt.postgresql.org,archive=$CODENAME-pgdg";
        "origin=cloudsmith/caddy/stable,archive=any-version";
};
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "04:30";
EOF
    then geaendert "Automatische Updates: Ubuntu-Sicherheit, PostgreSQL, Caddy; Neustart bei Bedarf 04:30"
    else ok "Automatische Updates: eingerichtet"; fi
    if datei_setzen "$WURZEL/etc/apt/apt.conf.d/21vertretung-auto-upgrades" 644 root:root <<'EOF'
// Vertretung (PB-1b): täglich Paketlisten holen und unattended-upgrades laufen lassen.
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
    then geaendert "unattended-upgrades täglich"; fi
}

# ---------------------------------------------------------------------
#  6 Benutzer und Ordner
# ---------------------------------------------------------------------
benutzer_einrichten() {
    local eintrag name heim
    for eintrag in "$DIENST_BENUTZER:$DIENST_HEIM" "$BAU_BENUTZER:$BAU_HEIM"; do
        name="${eintrag%%:*}"; heim="${eintrag#*:}"
        if id "$name" >/dev/null 2>&1; then
            ok "Benutzer $name: vorhanden"
        else
            useradd --system --user-group --no-create-home --home-dir "$heim" --shell /usr/sbin/nologin "$name"
            geaendert "Benutzer $name angelegt (ohne Anmeldung)"
        fi
        mkdir -p "$heim"
        chown "$name:$name" "$heim"
        chmod 700 "$heim"
    done
    install -d -m 755 -o root -g root "$OPT" "$VERSIONEN"
    install -d -m 750 -o root -g "$DIENST_BENUTZER" "$ETC"
    install -d -m 700 -o "$DIENST_BENUTZER" -g "$DIENST_BENUTZER" "$SICHERUNGEN"
}

# ---------------------------------------------------------------------
#  8 Datenbank: Rolle und Datenbank „vertretung“ (Passwort zufällig, nie angezeigt, nie in der Befehlszeile)
# ---------------------------------------------------------------------
rolle_mit_passwort() {   # $1 = CREATE oder ALTER
    als_postgres_sql postgres <<EOF
SET log_min_error_statement TO 'panic';
$1 ROLE $DB_ROLLE LOGIN CREATEDB PASSWORD '$DB_PASSWORT';
EOF
}

datenbank_einrichten() {
    local zustand rolle db
    zustand=$(pg_lsclusters --no-header 2>/dev/null | awk -v v="$PG_VERSION" '$1 == v && $2 == "main" { print $4 }' || true)
    [[ -n "$zustand" ]] || fehler "PostgreSQL $PG_VERSION: Der Cluster „main“ fehlt (pg_lsclusters)."
    if [[ "$zustand" != online ]]; then
        systemctl start "postgresql@$PG_VERSION-main" || fehler "PostgreSQL $PG_VERSION startet nicht (journalctl -u postgresql@$PG_VERSION-main)."
    fi
    DB_PORT=$(pg_port)
    DB_PASSWORT=$(db_passwort_aus_einstellungen)
    rolle=$(als_postgres_sql postgres -Atc "SELECT 1 FROM pg_roles WHERE rolname = '$DB_ROLLE'")
    if [[ -z "$rolle" ]]; then
        if [[ -z "$DB_PASSWORT" ]]; then DB_PASSWORT=$(zufall 32); fi
        rolle_mit_passwort CREATE
        geaendert "Datenbank-Rolle $DB_ROLLE angelegt (Passwort erzeugt, nicht angezeigt)"
    elif [[ -z "$DB_PASSWORT" ]]; then
        warnung "Die Rolle $DB_ROLLE gibt es schon, aber keine Einstellungsdatei mit ihrem Passwort."
        frage_jn "Neues Passwort für $DB_ROLLE setzen (nötig, damit die API sich anmelden kann)?" j || fehler "Abgebrochen."
        DB_PASSWORT=$(zufall 32)
        rolle_mit_passwort ALTER
        DB_PASSWORT_NEU=1
        geaendert "Neues Passwort für die Rolle $DB_ROLLE (nicht angezeigt)"
    else
        als_postgres_sql postgres -c "ALTER ROLE $DB_ROLLE CREATEDB" >/dev/null
        ok "Datenbank-Rolle $DB_ROLLE: vorhanden"
    fi
    db=$(als_postgres_sql postgres -Atc "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'")
    if [[ -z "$db" ]]; then
        als_postgres_sql postgres -c "CREATE DATABASE $DB_NAME OWNER $DB_ROLLE ENCODING 'UTF8' TEMPLATE template0" >/dev/null
        geaendert "Datenbank $DB_NAME angelegt (PostgreSQL $PG_VERSION, Port $DB_PORT, nur localhost)"
    else
        ok "Datenbank $DB_NAME: vorhanden"
    fi
}

# ---------------------------------------------------------------------
#  9 Einstellungen und Schlüssel (300, 312): alles erzeugt, nichts im Repo
# ---------------------------------------------------------------------
einstellungen_schreiben() {
    local tmp jwt push
    tmp=$(mktemp "$EINSTELLUNGEN.XXXXXX")
    jwt=$(zufall 64); push=$(zufall 48)
    V_DB="Host=localhost;Port=$DB_PORT;Database=$DB_NAME;Username=$DB_ROLLE;Password=$DB_PASSWORT" \
    V_JWT="$jwt" V_PUSH="$push" V_HOSTS="$ADRESSE;localhost" python3 - "$tmp" <<'PY'
import json, os, sys
e = os.environ
d = {
    "Database": {"Provider": "Postgres", "SchemaNachruesten": False},
    "ConnectionStrings": {"Postgres": e["V_DB"]},
    "Api": {"PushKey": e["V_PUSH"]},
    "Jwt": {"Key": e["V_JWT"]},
    "Betrieb": {"HttpsErzwingen": True, "Zeitzone": "Europe/Berlin", "Probebetrieb": True},
    "Proxy": {"Aktiv": True, "BekannteProxys": ["127.0.0.1", "::1"]},
    "Cors": {"Herkuenfte": []},
    "Zugriff": {"Schuladressen": [], "DesktopNurSchule": False, "AdminNurSchule": False},
    "AllowedHosts": e["V_HOSTS"],
    "Logging": {"LogLevel": {"Default": "Information", "Microsoft.AspNetCore": "Warning",
                             "Microsoft.EntityFrameworkCore": "Warning"}},
}
with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump(d, f, indent=2, ensure_ascii=False)
    f.write("\n")
PY
    chown root:"$DIENST_BENUTZER" "$tmp"
    chmod 640 "$tmp"
    mv -f "$tmp" "$EINSTELLUNGEN"
}

einstellungen_einrichten() {
    local r=0
    if [[ -f "$EINSTELLUNGEN" ]]; then
        json_wert "$EINSTELLUNGEN" Api:PushKey >/dev/null 2>&1 || r=$?
        case "$r" in
            0) ok "Einstellungsdatei: vorhanden ($EINSTELLUNGEN)" ;;
            1) warnung "In $EINSTELLUNGEN fehlt Api:PushKey – die API meldet beim Start, was fehlt." ;;
            *) warnung "$EINSTELLUNGEN ist kein reines JSON (Kommentare?) – die API meldet beim Start, was nicht passt." ;;
        esac
        chown root:"$DIENST_BENUTZER" "$EINSTELLUNGEN"
        chmod 640 "$EINSTELLUNGEN"
        if (( DB_PASSWORT_NEU )); then
            JSON_WERT="Host=localhost;Port=$DB_PORT;Database=$DB_NAME;Username=$DB_ROLLE;Password=$DB_PASSWORT" \
                json_setzen "$EINSTELLUNGEN" ConnectionStrings:Postgres \
                || fehler "$EINSTELLUNGEN ist kein reines JSON – das neue Datenbankpasswort lässt sich nicht eintragen."
            geaendert "Neues Datenbankpasswort in die Einstellungsdatei eingetragen"
        fi
    else
        einstellungen_schreiben
        NEUE_SCHLUESSEL=1
        geaendert "Einstellungsdatei mit neuen Schlüsseln (Jwt:Key 64, Api:PushKey 48 Zeichen), Probebetrieb an"
    fi
    if datei_setzen "$ADRESSDATEI" 640 root:"$DIENST_BENUTZER" <<<"$ADRESSE"; then geaendert "Adresse gespeichert: $ADRESSE"; fi
    if datei_setzen "$PGPASS" 600 "$DIENST_BENUTZER:$DIENST_BENUTZER" <<<"localhost:$DB_PORT:*:$DB_ROLLE:$DB_PASSWORT"; then
        geaendert "Passwortdatei für die Sicherung ($PGPASS)"
    fi
    if [[ -s "$PASSPHRASE" ]]; then
        ok "Passphrase der Sicherung: vorhanden"
    else
        if [[ -n "$(neueste_sicherung)" ]]; then
            warnung "Es gibt Sicherungen, aber keine Passphrase – die alten Sicherungen lassen sich ohne die alte Passphrase nicht mehr öffnen."
        fi
        passphrase_erzeugen | datei_setzen "$PASSPHRASE" 640 root:"$DIENST_BENUTZER" || true
        NEUE_PASSPHRASE=1
        geaendert "Passphrase der Sicherung erzeugt"
    fi
    if datei_setzen "$SICHERUNG_CONF" 640 root:"$DIENST_BENUTZER" <<EOF
# Vertretung – Einstellungen für Betrieb/sicherung.sh (angelegt von installieren.sh, PB-1b)
PGHOST=localhost
PGPORT=$DB_PORT
PGUSER=$DB_ROLLE
PGDATABASE=$DB_NAME
PGPASSFILE=$PGPASS
SICHERUNG_ORDNER=$SICHERUNGEN
SICHERUNG_PASSPHRASE_DATEI=$PASSPHRASE
SICHERUNG_TAGE=30
EOF
    then geaendert "Einstellungen der Sicherung ($SICHERUNG_CONF)"; else ok "Einstellungen der Sicherung: vorhanden"; fi
}

# ---------------------------------------------------------------------
#  10 Dienst
# ---------------------------------------------------------------------
dienst_einrichten() {
    local version="$1"
    if [[ "$(version_aktiv)" != "$version" ]]; then
        dienst_umstellen "$version"
        geaendert "Version $(basename "$version") ist die laufende"
    elif einheiten_einspielen "$version/betrieb"; then
        geaendert "systemd-Units erneuert"
    fi
    if ! systemctl is-enabled --quiet "$DIENST" 2>/dev/null; then
        systemctl enable "$DIENST" >/dev/null 2>&1
        geaendert "Dienst startet mit dem Server"
    fi
    if systemctl is-active --quiet "$DIENST" && gesund_lokal; then
        ok "Dienst läuft und antwortet."
    else
        systemctl stop "$DIENST" 2>/dev/null || true
        dienst_starten_pruefen || fehler "Die API startet nicht (Meldung oben). Nach der Korrektur installieren.sh noch einmal starten."
        geaendert "Dienst gestartet"
    fi
    migrationen_pruefen "$version" || fehler "Die Datenbank ist nicht auf dem Stand des Programms (siehe oben)."
}

# ---------------------------------------------------------------------
#  11 Firewall (ufw; die Firewall im hPanel bleibt aus, F6)
# ---------------------------------------------------------------------
ssh_ports() {
    local p
    p=$(sshd -T 2>/dev/null | awk '$1 == "port" { print $2 }' | LC_ALL=C sort -u || true)
    printf '%s' "${p:-22}"
}

firewall_einrichten() {
    local p vorher nachher
    vorher=$(ufw status verbose 2>/dev/null || true)
    for p in $(ssh_ports); do ufw allow "$p/tcp" comment 'SSH (auch Browser-Terminal von Hostinger)' >/dev/null; done
    ufw allow 80/tcp comment 'HTTP (Zertifikat, Umleitung auf HTTPS)' >/dev/null
    ufw allow 443 comment 'HTTPS (TCP und HTTP/3)' >/dev/null
    ufw default deny incoming >/dev/null
    ufw default allow outgoing >/dev/null
    if ! grep -E '^Status: active' >/dev/null <<<"$vorher"; then
        ufw --force enable >/dev/null
    fi
    nachher=$(ufw status verbose 2>/dev/null || true)
    if [[ "$vorher" != "$nachher" ]]; then
        geaendert "Firewall (ufw): nur SSH ($(ssh_ports | paste -sd ' ')), 80, 443 von außen"
    else
        ok "Firewall (ufw): aktiv – SSH, 80, 443"
    fi
}

# ---------------------------------------------------------------------
#  12 Caddy (HTTPS mit Let's Encrypt, ohne Zugriffsprotokoll)
# ---------------------------------------------------------------------
https_pruefen() {   # $1 = Startzeit für das Log von Caddy
    local i seit="${1:-$(date +%s)}"
    info "Warte auf das Zertifikat für $ADRESSE (bis zu 2 Minuten) …"
    for (( i=0; i<24; i++ )); do
        if gesund_aussen "$ADRESSE"; then
            HTTPS_OK=1
            ok "https://$ADRESSE antwortet – Zertifikat ist da."
            return 0
        fi
        sleep 5
    done
    HTTPS_OK=0
    warnung "https://$ADRESSE antwortet nicht. Aus dem Log von Caddy:"
    journalctl -u caddy --since "@$seit" -o cat --no-pager 2>/dev/null \
        | grep -iE 'error|challenge|rate|too many|unauthorized|timeout|dns|refused' | tail -n 8 | cut -c1-240 | sed 's/^/      /' >&2 || true
    info "Häufige Ursachen: Der Name zeigt nicht hierher (DNS) · Port 80/443 zu (Firewall im hPanel – soll aus sein) ·"
    info "Mengengrenze von Let's Encrypt (bei sslip.io häufig; je Name höchstens 5 neue Zertifikate in 7 Tagen)."
    info "Später erneut versuchen: vertretung adresse <name>   (Log: vertretung log caddy)"
    return 0
}

ports_belegt() {   # andere Programme auf 80/443 (z. B. ein Webserver der Vorlage) verhindern das Zertifikat
    local belegt
    command -v ss >/dev/null || return 0
    belegt=$(ss -Hltnp 2>/dev/null | awk '$4 ~ /:(80|443)$/' | grep -v '"caddy"' || true)
    if [[ -n "$belegt" ]]; then
        warnung "Port 80 bzw. 443 ist von einem anderen Programm belegt – Caddy bekommt dann kein Zertifikat:"
        printf '%s\n' "$belegt" | cut -c1-200 | sed 's/^/      /' >&2
        info "Abhilfe: dieses Programm entfernen bzw. abschalten (z. B. systemctl disable --now apache2) und dann"
        info "bash installieren.sh noch einmal starten."
    fi
    return 0
}

caddy_einrichten() {   # $1 = Version (für die Vorlage)
    local vorlage="$1/betrieb/Caddyfile.vorlage" seit
    [[ -f "$vorlage" ]] || fehler "Die Vorlage $vorlage fehlt."
    ports_belegt
    if [[ -f "$CADDYFILE" && ! -f "$CADDYFILE.vor-vertretung" ]] && ! grep 'Vertretung' "$CADDYFILE" >/dev/null; then
        cp -p "$CADDYFILE" "$CADDYFILE.vor-vertretung"
    fi
    seit=$(date +%s)
    if sed "s/@ADRESSE@/$ADRESSE/g" "$vorlage" | datei_setzen "$CADDYFILE" 644 root:root; then
        if ! caddy validate --config "$CADDYFILE" --adapter caddyfile > "$OPT/caddy-pruefung.log" 2>&1; then
            tail -n 10 "$OPT/caddy-pruefung.log" | sed 's/^/      /' >&2
            fehler "Das Caddyfile ist ungültig (Protokoll: $OPT/caddy-pruefung.log)."
        fi
        systemctl enable caddy >/dev/null 2>&1 || true
        systemctl reload-or-restart caddy
        geaendert "Caddy für https://$ADRESSE eingerichtet"
    else
        systemctl enable caddy >/dev/null 2>&1 || true
        if ! systemctl is-active --quiet caddy; then systemctl start caddy; fi
        ok "Caddy: eingerichtet für $ADRESSE"
    fi
    https_pruefen "$seit"
}

# ---------------------------------------------------------------------
#  13 Sicherung (316): Timer 01:30 täglich, monatlich --pruefen, erste Sicherung sofort
# ---------------------------------------------------------------------
sicherung_einrichten() {
    local t
    for t in vertretung-sicherung.timer vertretung-pruefung.timer; do
        if systemctl is-enabled --quiet "$t" 2>/dev/null && systemctl is-active --quiet "$t"; then
            ok "Timer $t: aktiv"
        else
            systemctl enable --now "$t" >/dev/null 2>&1
            geaendert "Timer $t aktiv"
        fi
    done
    if [[ -n "$(neueste_sicherung)" ]]; then
        ok "Sicherungen vorhanden (neueste: $(basename "$(neueste_sicherung)"))"
    elif systemctl start vertretung-sicherung.service; then
        geaendert "Erste Sicherung: $(basename "$(neueste_sicherung)")"
    else
        warnung "Die erste Sicherung ist fehlgeschlagen – journalctl -u vertretung-sicherung (später: vertretung sicherung)"
    fi
}

# ---------------------------------------------------------------------
#  14 SSH (305, F6): Passwortanmeldung nur nach Rückfrage abschalten – und nur, wenn ein Schlüssel hinterlegt ist
# ---------------------------------------------------------------------
ssh_einrichten() {   # $1 = „befehl“ (vertretung ssh-passwort-aus) oder leer (Einrichtung)
    local datei="$WURZEL/etc/ssh/sshd_config.d/01-vertretung.conf" schluessel="$WURZEL/root/.ssh/authorized_keys"
    if [[ -f "$datei" ]]; then
        ok "SSH: Anmeldung nur mit Schlüssel (Passwortanmeldung ist aus; zurück: $datei löschen, systemctl reload ssh)"
        return 0
    fi
    if [[ ! -s "$schluessel" ]] || ! grep -E '^[^#]*(ssh-|ecdsa-|sk-)' "$schluessel" >/dev/null; then
        if [[ "${1:-}" == befehl ]]; then fehler "In $schluessel ist kein SSH-Schlüssel hinterlegt (hPanel → VPS → SSH-Schlüssel)."; fi
        info "SSH: kein Schlüssel hinterlegt – die Passwortanmeldung bleibt (später: vertretung ssh-passwort-aus)."
        return 0
    fi
    info "In $schluessel stehen diese Schlüssel:"
    ssh-keygen -lf "$schluessel" 2>/dev/null | sed 's/^/      /' || true
    info "Darunter kann ein vorübergehender Schlüssel des Browser-Terminals von Hostinger sein (verschwindet nach etwa"
    info "einer Minute). Abschalten nur, wenn du dich mit deinem eigenen Schlüssel schon vom PC aus angemeldet hast."
    if ! frage_jn "SSH-Passwortanmeldung abschalten (nur noch Schlüssel; das Browser-Terminal geht weiter)?" n; then
        info "SSH: Passwortanmeldung bleibt (später: vertretung ssh-passwort-aus)."
        return 0
    fi
    datei_setzen "$datei" 644 root:root <<'EOF' || true
# Vertretung (PB-1b, 305): SSH nur mit Schlüssel. Root bleibt mit Schlüssel erlaubt – das Browser-Terminal von
# Hostinger meldet sich mit einem vorübergehenden Schlüssel an. Rückgängig: Datei löschen, systemctl reload ssh.
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
EOF
    if ! sshd -t 2>/dev/null; then
        rm -f "$datei"
        fehler "sshd lehnt die Einstellung ab – nichts geändert."
    fi
    systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || true
    geaendert "SSH: Passwortanmeldung aus, nur noch Schlüssel"
}

# ---------------------------------------------------------------------
#  --adresse NAME
# ---------------------------------------------------------------------
adresse_aendern() {
    local aktiv name="${1:-}"
    aktiv=$(version_aktiv)
    [[ -n "$aktiv" ]] || fehler "Hier ist noch nichts eingerichtet – erst installieren.sh."
    [[ -n "$name" ]] || fehler "Aufruf: vertretung adresse <name>   (jetzt: $(head -n 1 "$ADRESSDATEI" 2>/dev/null || echo '?'))"
    name="${name,,}"; name="${name%.}"
    adresse_gueltig "$name" || fehler "„$name“ ist kein gültiger Name (Beispiel: srv123456.hstgr.cloud)."
    schritt "Adresse ändern"
    case "$(adresse_zeigt_hierher "$name")" in
        ok) ok "$name zeigt auf diesen Server." ;;
        v6) warnung "$name hat eine IPv6-Adresse, die nicht zu diesem Server gehört."
            frage_jn "Trotzdem nehmen?" n || return 0 ;;
        *)  warnung "$name zeigt (noch) nicht auf diesen Server."
            frage_jn "Trotzdem nehmen?" n || return 0 ;;
    esac
    ADRESSE="$name"
    JSON_WERT="$ADRESSE;localhost" json_setzen "$EINSTELLUNGEN" AllowedHosts \
        || fehler "$EINSTELLUNGEN ist kein reines JSON – AllowedHosts bitte von Hand auf „$ADRESSE;localhost“ setzen."
    datei_setzen "$ADRESSDATEI" 640 root:"$DIENST_BENUTZER" <<<"$ADRESSE" || true
    ok "AllowedHosts: $ADRESSE;localhost"
    systemctl stop "$DIENST" 2>/dev/null || true
    dienst_starten_pruefen || fehler "Die API startet mit der neuen Adresse nicht (Meldung oben)."
    caddy_einrichten "$aktiv"
    if (( HTTPS_OK )); then ok "Fertig: https://$ADRESSE"; fi
}

# ---------------------------------------------------------------------
#  Einrichten
# ---------------------------------------------------------------------
einrichten() {
    local commit="$1" aktiv erst=0 version push="" pass="" konten
    aktiv=$(version_aktiv)
    if [[ -z "$aktiv" ]]; then
        [[ -n "$commit" ]] || vorlauf    # holt den Stand, fragt und startet neu (kehrt nicht zurück)
        erst=1
    else
        commit=$(cat "$aktiv/COMMIT")
    fi

    schritt "1/15 Prüfen"
    system_pruefen

    schritt "2/15 Stand"
    if (( erst )); then info "Erstinstallation"; else info "Eingerichtet – ein zweiter Lauf ergänzt nur, was fehlt (Update: vertretung aktualisieren)."; fi
    info "Stand: $(commit_text "$commit")"

    schritt "3/15 Adresse"
    if [[ -s "$ADRESSDATEI" ]]; then
        ADRESSE=$(head -n 1 "$ADRESSDATEI")
        ok "Adresse: $ADRESSE (ändern: vertretung adresse <name>)"
    else
        adresse_fragen
    fi

    schritt "4/15 System"
    system_einrichten "$erst"

    schritt "5/15 Pakete"
    pakete_einrichten

    schritt "6/15 Benutzer und Ordner"
    benutzer_einrichten

    schritt "7/15 Programm"
    version=$(version_fuer_commit "$commit")
    if [[ -n "$version" ]]; then
        ok "Gebaut: $(version_text "$version")"
    else
        programm_bauen "$commit" || fehler "Der Bau ist fehlgeschlagen (Protokoll: $BAU_LOG). Nach einer Korrektur installieren.sh noch einmal starten."
        version="$NEUE_VERSION"
        geaendert "Programm gebaut"
    fi

    schritt "8/15 Datenbank"
    datenbank_einrichten

    schritt "9/15 Einstellungen und Schlüssel"
    einstellungen_einrichten

    schritt "10/15 Dienst"
    dienst_einrichten "$version"

    schritt "11/15 Firewall"
    firewall_einrichten

    schritt "12/15 Caddy (HTTPS)"
    caddy_einrichten "$version"

    schritt "13/15 Sicherung"
    sicherung_einrichten

    schritt "14/15 SSH"
    ssh_einrichten

    schritt "15/15 Abschluss"
    rm -rf -- "$OPT/einrichtung"
    if (( NEUE_SCHLUESSEL )); then push=$(json_wert "$EINSTELLUNGEN" Api:PushKey); fi
    if (( NEUE_PASSPHRASE )); then pass=$(cat "$PASSPHRASE"); fi
    if [[ -n "$push" || -n "$pass" ]]; then
        info "Diese Werte erscheinen nur jetzt (später: vertretung schluessel):"
        geheimnisse_zeigen "$push" "$pass"
    fi
    konten=$(anzahl_konten)
    if [[ "$konten" == "0" ]]; then
        info "Erstes Admin-Konto (Benutzername admin) – das Startpasswort steht nur hier:"
        programm_ausfuehren admin-anlegen || warnung "admin-anlegen ist fehlgeschlagen – später: vertretung admin-anlegen"
        erst=1
    fi
    echo
    if (( ${#GEAENDERT[@]} == 0 )); then
        ok "Nichts geändert – alles war schon eingerichtet."
    else
        ok "Fertig (${#GEAENDERT[@]} Änderung(en))."
    fi
    if (( HTTPS_OK && erst )); then
        info "Nächste Schritte: Browser → https://$ADRESSE → Band „Probebetrieb – nur Testdaten“ → anmelden als admin"
        info "mit dem Startpasswort → eigenes Passwort → zweiter Faktor (Microsoft Authenticator)."
    elif (( ! HTTPS_OK )); then
        warnung "HTTPS steht noch nicht – siehe oben (vertretung adresse <name>, vertretung log caddy)."
    fi
    info "Befehle: vertretung hilfe · Zustand: vertretung status · Anleitung: Betrieb/linux/LIESMICH.md"
}

main() {
    local modus=einrichten commit="" name=""
    while (( $# > 0 )); do
        case "$1" in
            --aus-quelle)       commit="${2:-}"; shift $(( $# > 1 ? 2 : 1 )) ;;
            --adresse)          modus=adresse; name="${2:-}"; shift $(( $# > 1 ? 2 : 1 )) ;;
            --ssh-passwort-aus) modus=ssh; shift ;;
            -h|--hilfe)         aufruf; return 0 ;;
            *)                  aufruf >&2; fehler "Unbekannte Angabe „$1“." ;;
        esac
    done
    pruefe_root
    sperren
    case "$modus" in
        adresse)    adresse_aendern "$name" ;;
        ssh)        schritt "SSH"; ssh_einrichten befehl ;;
        einrichten) einrichten "$commit" ;;
    esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
