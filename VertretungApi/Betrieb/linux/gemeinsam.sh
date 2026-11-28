# shellcheck shell=bash
# =====================================================================
#  Vertretung – gemeinsame Teile der Linux-Skripte (Testserver, PB-1b)
#
#  Wird von installieren.sh, aktualisieren.sh und dem Befehl „vertretung“ eingelesen – nicht direkt starten.
#  Aufbau auf dem Server (Entscheidungen 300, 312–317):
#    /opt/vertretung/quelle                Klon des öffentlichen Repos (root; nur VertretungApi, ohne alte Inhalte)
#    /opt/vertretung/versionen/<zeit>_<c>  gebaute Version: app/ (Programm), betrieb/ (Skripte und Units aus
#                                          demselben Commit), COMMIT, MIGRATIONEN, STAND, FERTIG – die letzten 3 bleiben
#    /opt/vertretung/aktuell               Verweis auf die laufende Version
#    /etc/vertretung/                      appsettings.Production.json, sicherung.conf, sicherung-passphrase, adresse
#                                          (root:vertretung, 640), pgpass (vertretung, 600)
#    /var/backups/vertretung               Sicherungen (vertretung, 700)
#    /var/lib/vertretung, …-bau            Heimordner von Dienst- und Bau-Benutzer (700)
#    /usr/local/bin/vertretung             Verweis auf aktuell/betrieb/vertretung
#  Sicherheit: root holt den Quelltext; gebaut wird als „vertretung-bau“ in dessen eigenem Ordner; root übernimmt
#  nur das fertige Programm. Skripte und Units, die root ausführt, kommen nie aus dem Ordner des Bau-Benutzers.
# =====================================================================

# Prüfstand im Chat: PRUEF_WURZEL legt alle Pfade unter einen Testordner. Auf dem Server ist es leer.
WURZEL="${PRUEF_WURZEL:-}"

REPO_URL="https://github.com/schulsoftware-KI/Stundenplanprogramm.git"
REPO_ZWEIG="master"
if [[ -n "$WURZEL" && -n "${PRUEF_REPO:-}" ]]; then REPO_URL="$PRUEF_REPO"; fi

OPT="$WURZEL/opt/vertretung"
QUELLE="$OPT/quelle"
VERSIONEN="$OPT/versionen"
AKTUELL="$OPT/aktuell"
BAU_LOG="$OPT/bau.log"
ETC="$WURZEL/etc/vertretung"
EINSTELLUNGEN="$ETC/appsettings.Production.json"
SICHERUNG_CONF="$ETC/sicherung.conf"
PASSPHRASE="$ETC/sicherung-passphrase"
PGPASS="$ETC/pgpass"
ADRESSDATEI="$ETC/adresse"
SICHERUNGEN="$WURZEL/var/backups/vertretung"
DIENST_HEIM="$WURZEL/var/lib/vertretung"
BAU_HEIM="$WURZEL/var/lib/vertretung-bau"
SYSTEMD_ORDNER="$WURZEL/etc/systemd/system"
BEFEHL="$WURZEL/usr/local/bin/vertretung"
CADDYFILE="$WURZEL/etc/caddy/Caddyfile"
SPERRORDNER="$WURZEL/run/lock"

DIENST="vertretung.service"
DIENST_BENUTZER="vertretung"
BAU_BENUTZER="vertretung-bau"
DB_NAME="vertretung"
DB_ROLLE="vertretung"
PG_VERSION=17
if [[ -n "$WURZEL" ]]; then PG_VERSION="${PRUEF_PG_VERSION:-17}"; fi   # Prüfstand: PostgreSQL 16 im Chat
API_LOKAL="http://127.0.0.1:5080"
VERSIONEN_BEHALTEN=3
EINHEITEN=(vertretung.service vertretung-sicherung.service vertretung-sicherung.timer
           vertretung-pruefung.service vertretung-pruefung.timer)

export LC_ALL=C.UTF-8
export DEBIAN_FRONTEND=noninteractive
export NEEDRESTART_SUSPEND=1   # needrestart soll während der Skripte nicht nachfragen

# ---------------------------------------------------------------------
#  Ausgabe und Fragen
# ---------------------------------------------------------------------
if [[ -t 1 ]]; then
    FETT=$'\e[1m'; ROT=$'\e[31m'; GRUEN=$'\e[32m'; GELB=$'\e[33m'; NORMAL=$'\e[0m'
else
    FETT=""; ROT=""; GRUEN=""; GELB=""; NORMAL=""
fi
schritt() { printf '\n%s== %s ==%s\n' "$FETT" "$*" "$NORMAL"; }
info()    { printf '   %s\n' "$*"; }
ok()      { printf '   %s✓%s %s\n' "$GRUEN" "$NORMAL" "$*"; }
warnung() { printf '   %s!%s %s\n' "$GELB" "$NORMAL" "$*" >&2; }
fehler()  { printf '\n%sFEHLER:%s %s\n' "$ROT" "$NORMAL" "$*" >&2; exit 1; }

# frage_jn "Frage" j|n  → 0 = ja. Ohne Eingabe (Enter bzw. keine Eingabe möglich) gilt der Standard.
frage_jn() {
    local antwort standard="${2:-n}" hinweis="j/N"
    [[ "$standard" == j ]] && hinweis="J/n"
    while true; do
        printf '   %s [%s] ' "$1" "$hinweis"
        if ! read -r antwort; then antwort=""; echo; fi
        case "${antwort,,}" in
            "")             [[ "$standard" == j ]]; return ;;
            j|ja|y|yes)     return 0 ;;
            n|nein|no)      return 1 ;;
            *)              echo "   Bitte j oder n eingeben." ;;
        esac
    done
}

# eingabe "Text" "Vorschlag" → Antwort (bzw. Vorschlag bei leerer Eingabe) auf stdout
eingabe() {
    local antwort
    printf '   %s [%s] ' "$1" "$2" >&2
    if ! read -r antwort; then antwort=""; echo >&2; fi
    printf '%s' "${antwort:-$2}"
}

pruefe_root() {
    [[ $EUID -eq 0 ]] || fehler "Bitte als root ausführen (im Browser-Terminal von Hostinger ist man root; sonst mit sudo)."
}

# Ein Vorgang zur Zeit (Installieren, Aktualisieren, Zurück, Neustart, Zurückspielen).
sperren() {
    mkdir -p "$SPERRORDNER"
    exec 8>"$SPERRORDNER/vertretung-betrieb.sperre"
    flock -n 8 || fehler "Es läuft schon ein anderer Vorgang (Installieren, Aktualisieren, Zurückspielen …) – bitte warten und dann noch einmal."
}

# Befehl als anderer Benutzer – ohne die Sperre (fd 8) weiterzugeben, im Ordner ALS_ORDNER (Standard /, damit z. B.
# psql nicht über /root stolpert).
als_benutzer() {
    local wer="$1"; shift
    ( cd "${ALS_ORDNER:-/}" && runuser -u "$wer" -- "$@" 8>&- 9>&- )
}
als_dienst() { als_benutzer "$DIENST_BENUTZER" "$@"; }

# ---------------------------------------------------------------------
#  Zufall, Dateien, JSON
# ---------------------------------------------------------------------

# zufall N → N zufällige Buchstaben und Ziffern (Python „secrets“, keine Sonderzeichen – passt in jede Datei)
zufall() {
    python3 -c 'import secrets,string,sys
a = string.ascii_letters + string.digits
print("".join(secrets.choice(a) for _ in range(int(sys.argv[1]))))' "$1"
}

# Passphrase der Sicherung: 5 Gruppen zu 6 Zeichen mit Bindestrichen (gut abzuschreiben, 35 Zeichen)
passphrase_erzeugen() {
    python3 -c 'import secrets,string
a = string.ascii_letters + string.digits
print("-".join("".join(secrets.choice(a) for _ in range(6)) for _ in range(5)))'
}

# datei_setzen ZIEL RECHTE EIGENTÜMER < Inhalt → 0 = geschrieben (neu oder geändert), 1 = war schon so.
# Immer in „if“ bzw. mit „||“ aufrufen (set -e).
datei_setzen() {
    local ziel="$1" rechte="$2" eigentuemer="$3" tmp
    mkdir -p "$(dirname "$ziel")"
    tmp=$(mktemp "$ziel.XXXXXX")
    cat > "$tmp"
    if [[ -f "$ziel" ]] && cmp -s "$tmp" "$ziel"; then
        rm -f "$tmp"
        chmod "$rechte" "$ziel"; chown "$eigentuemer" "$ziel"
        return 1
    fi
    chmod "$rechte" "$tmp"; chown "$eigentuemer" "$tmp"
    mv -f "$tmp" "$ziel"
    return 0
}

# GEAENDERT sammelt, was ein Lauf verändert hat (Zusammenfassung am Ende).
GEAENDERT=()
geaendert() { GEAENDERT+=("$*"); ok "$*"; }

# json_wert DATEI Abschnitt:Schlüssel → Wert (true/false klein) bzw. Rückgabe 1 (fehlt) / 2 (kein JSON)
json_wert() {
    python3 - "$1" "$2" <<'PY'
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8-sig") as f:
        d = json.load(f)
except Exception:
    sys.exit(2)
for teil in sys.argv[2].split(":"):
    # ASP.NET unterscheidet bei Schlüsseln nicht zwischen groß und klein
    if not isinstance(d, dict):
        sys.exit(1)
    k = next((x for x in d if x.lower() == teil.lower()), None)
    if k is None:
        sys.exit(1)
    d = d[k]
if isinstance(d, bool):
    print("true" if d else "false")
elif isinstance(d, (dict, list)):
    print(json.dumps(d, ensure_ascii=False))
elif d is None:
    print("")
else:
    print(d)
PY
}

# json_setzen DATEI Abschnitt:Schlüssel – setzt den Wert aus der Umgebungsvariable JSON_WERT (nicht in der
# Befehlszeile: kann ein Passwort sein); die übrigen Einträge bleiben. Rückgabe 1 = Datei ist kein reines JSON.
json_setzen() {
    local tmp
    tmp=$(mktemp "$1.XXXXXX")
    if ! python3 - "$1" "$tmp" "$2" <<'PY2'
import json, os, sys
quelle, ziel, pfad = sys.argv[1:4]
with open(quelle, encoding="utf-8-sig") as f:
    d = json.load(f)
teile = pfad.split(":")
knoten = d
for teil in teile[:-1]:
    k = next((x for x in knoten if x.lower() == teil.lower()), teil)
    if not isinstance(knoten.get(k), dict):
        knoten[k] = {}
    knoten = knoten[k]
k = next((x for x in knoten if x.lower() == teile[-1].lower()), teile[-1])
knoten[k] = os.environ["JSON_WERT"]
with open(ziel, "w", encoding="utf-8") as f:
    json.dump(d, f, indent=2, ensure_ascii=False)
    f.write("\n")
PY2
    then
        rm -f -- "$tmp"
        return 1
    fi
    chown --reference="$1" "$tmp"; chmod --reference="$1" "$tmp"
    mv -f "$tmp" "$1"
}

# Passwort aus ConnectionStrings:Postgres der Einstellungsdatei (leer, wenn nicht vorhanden)
db_passwort_aus_einstellungen() {
    [[ -f "$EINSTELLUNGEN" ]] || return 0
    local conn
    conn=$(json_wert "$EINSTELLUNGEN" ConnectionStrings:Postgres 2>/dev/null) || return 0
    python3 -c 'import sys
for teil in sys.argv[1].split(";"):
    k, _, v = teil.partition("=")
    if k.strip().lower() in ("password", "pwd"):
        print(v.strip()); break' "$conn"
}

# ---------------------------------------------------------------------
#  Quelle (Klon von root), Commits, Migrationen
# ---------------------------------------------------------------------

quelle_holen() {
    if [[ -d "$QUELLE/.git" ]]; then
        git -C "$QUELLE" fetch --quiet origin "+refs/heads/$REPO_ZWEIG:refs/remotes/origin/$REPO_ZWEIG" \
            || fehler "Das Repo lässt sich nicht abrufen ($REPO_URL) – Internet bzw. GitHub prüfen."
    else
        rm -rf -- "$QUELLE"
        mkdir -p "$OPT"
        git clone --quiet --filter=blob:none --no-checkout --branch "$REPO_ZWEIG" "$REPO_URL" "$QUELLE" \
            || fehler "Das Repo lässt sich nicht klonen ($REPO_URL) – Internet bzw. GitHub prüfen."
        # Nur der Ordner der API (ohne runtimes/ und src/ des Desktops); Dateiinhalte kommen erst beim Auschecken.
        git -C "$QUELLE" sparse-checkout set --cone VertretungApi
    fi
}

# commit_aufloesen [Angabe] → ZIEL_COMMIT (voller Hash). Ohne Angabe: neuester Stand von master.
commit_aufloesen() {
    local angabe="${1:-}" ref
    if [[ -z "$angabe" ]]; then
        ref="origin/$REPO_ZWEIG"
    else
        [[ "$angabe" =~ ^[0-9a-fA-F]{7,40}$ ]] || fehler "„$angabe“ ist kein Commit (7 bis 40 Zeichen 0–9, a–f)."
        ref="$angabe"
    fi
    ZIEL_COMMIT=$(git -C "$QUELLE" rev-parse --verify --quiet "$ref^{commit}") \
        || fehler "Den Commit „$angabe“ gibt es im Repo nicht (bzw. die Angabe ist mehrdeutig)."
    git -C "$QUELLE" merge-base --is-ancestor "$ZIEL_COMMIT" "origin/$REPO_ZWEIG" \
        || fehler "Der Commit ${ZIEL_COMMIT:0:7} liegt nicht in $REPO_ZWEIG – installiert wird nur, was in $REPO_ZWEIG liegt."
    [[ -n "$(git -C "$QUELLE" ls-tree --name-only "$ZIEL_COMMIT" VertretungApi/Betrieb/linux/vertretung.service)" ]] \
        || fehler "Der Stand ${ZIEL_COMMIT:0:7} ist älter als PB-1b (ohne Betrieb/linux) und lässt sich hier nicht installieren."
}

commit_text() {   # Kurzform, Datum, Betreff
    git -C "$QUELLE" log -1 --date=format-local:'%d.%m.%Y %H:%M' --format='%h · %cd · %s' "$1"
}

migrationen_im_commit() {   # sortierte Namen wie in __EFMigrationsHistory (ohne .Designer, ohne Snapshot)
    git -C "$QUELLE" ls-tree --name-only "$1" VertretungApi/Data/Migrations/ \
        | sed -n 's#^VertretungApi/Data/Migrations/\([0-9]\{14\}_[A-Za-z0-9_]*\)\.cs$#\1#p' | LC_ALL=C sort
}

pg_port() {   # Port des Clusters <PG_VERSION>/main (Standard 5432)
    local port
    port=$(pg_lsclusters --no-header 2>/dev/null | awk -v v="$PG_VERSION" '$1 == v && $2 == "main" { print $3 }' || true)
    printf '%s' "${port:-5432}"
}

als_postgres_sql() {   # $1 = Datenbank, weitere Angaben für psql (z. B. -Atc "…"); sonst Befehle auf stdin
    als_benutzer postgres psql -X -q -p "$(pg_port)" -d "$1" -v ON_ERROR_STOP=1 "${@:2}"
}

migrationen_in_db() {
    als_benutzer postgres psql -X -q -At -p "$(pg_port)" -d "$DB_NAME" \
        -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory"' 2>/dev/null | LC_ALL=C sort || true
}

anzahl_konten() {   # Anzahl Zeilen in "Benutzer" (leer, wenn die Tabelle fehlt)
    als_benutzer postgres psql -X -q -At -p "$(pg_port)" -d "$DB_NAME" -c 'SELECT count(*) FROM "Benutzer"' 2>/dev/null || true
}

# ---------------------------------------------------------------------
#  Versionen
# ---------------------------------------------------------------------

version_aktiv() {   # Pfad der laufenden Version bzw. leer
    if [[ -L "$AKTUELL" && -f "$AKTUELL/FERTIG" ]]; then readlink -f "$AKTUELL"; fi
}

versionen_liste() {   # fertige Versionen, älteste zuerst (Name beginnt mit Datum und Uhrzeit)
    [[ -d "$VERSIONEN" ]] || return 0
    local v
    for v in "$VERSIONEN"/20*_*; do
        if [[ -f "$v/FERTIG" ]]; then printf '%s\n' "$(basename "$v")"; fi
    done | LC_ALL=C sort
    return 0
}

version_fuer_commit() {   # fertige Version mit diesem Commit (neueste) bzw. leer
    local v treffer=""
    while IFS= read -r v; do
        if [[ "$(cat "$VERSIONEN/$v/COMMIT" 2>/dev/null)" == "$1" ]]; then treffer="$VERSIONEN/$v"; fi
    done < <(versionen_liste)
    printf '%s' "$treffer"
}

version_text() {   # Name · Stand
    printf '%s · %s' "$(basename "$1")" "$(cat "$1/STAND" 2>/dev/null || echo '?')"
}

# programm_bauen COMMIT → NEUE_VERSION (Pfad). Bricht der Bau ab: Rückgabe 1, die laufende Version bleibt.
programm_bauen() {
    local commit="$1" ziel arbeit ausgabe tmp
    ziel="$VERSIONEN/$(date '+%Y-%m-%d_%H%M%S')_${commit:0:7}"
    arbeit="$BAU_HEIM/arbeit"
    ausgabe="$BAU_HEIM/ausgabe"

    git -C "$QUELLE" -c advice.detachedHead=false checkout --quiet --force --detach "$commit" \
        || { warnung "Der Stand ${commit:0:7} lässt sich nicht auschecken."; return 1; }
    pkill -KILL -u "$BAU_BENUTZER" 2>/dev/null || true
    rm -rf -- "$arbeit" "$ausgabe"
    als_benutzer "$BAU_BENUTZER" mkdir -p "$arbeit" "$ausgabe"
    git -C "$QUELLE" archive "$commit" VertretungApi | als_benutzer "$BAU_BENUTZER" tar -x -C "$arbeit" \
        || { warnung "Der Quelltext lässt sich nicht in den Bauordner kopieren."; return 1; }

    info "Baue ${commit:0:7} als $BAU_BENUTZER (dauert einige Minuten; Protokoll: $BAU_LOG) …"
    if ! als_benutzer "$BAU_BENUTZER" env -i PATH="${WURZEL:+$PATH:}/usr/local/bin:/usr/bin:/bin" HOME="$BAU_HEIM" LANG=C.UTF-8 \
            DOTNET_CLI_HOME="$BAU_HEIM" NUGET_PACKAGES="$BAU_HEIM/nuget" \
            DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
            DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1 MSBUILDDISABLENODEREUSE=1 \
            timeout 1800 dotnet publish "$arbeit/VertretungApi/VertretungApi.csproj" -c Release -o "$ausgabe" \
                -p:UseAppHost=false -p:UseSharedCompilation=false --disable-build-servers --nologo -v minimal \
            > "$BAU_LOG" 2>&1; then
        warnung "Der Bau ist fehlgeschlagen – die letzten Zeilen:"
        tail -n 25 "$BAU_LOG" | sed 's/^/      /' >&2
        pkill -KILL -u "$BAU_BENUTZER" 2>/dev/null || true
        return 1
    fi
    # Nach dem Bau läuft nichts mehr als Bau-Benutzer weiter; im Ergebnis nur normale Dateien und Ordner.
    pkill -KILL -u "$BAU_BENUTZER" 2>/dev/null || true
    if [[ -n "$(find "$ausgabe" ! -type f ! -type d -print -quit)" ]]; then
        warnung "Das Ergebnis des Baus enthält Verweise oder besondere Dateien – wird nicht übernommen."
        return 1
    fi
    [[ -f "$ausgabe/VertretungApi.dll" ]] || { warnung "Im Ergebnis fehlt VertretungApi.dll."; return 1; }

    mkdir -p "$ziel/app" "$ziel/betrieb"
    cp -R "$ausgabe/." "$ziel/app/"
    # Skripte und Units aus dem Klon von root – nicht aus dem Bauordner.
    tmp=$(mktemp -d)
    git -C "$QUELLE" archive "$commit" VertretungApi/Betrieb/linux VertretungApi/Betrieb/sicherung.sh | tar -x -C "$tmp"
    cp "$tmp"/VertretungApi/Betrieb/linux/* "$tmp/VertretungApi/Betrieb/sicherung.sh" "$ziel/betrieb/"
    rm -rf -- "$tmp"
    printf '%s\n' "$commit" > "$ziel/COMMIT"
    migrationen_im_commit "$commit" > "$ziel/MIGRATIONEN"
    commit_text "$commit" > "$ziel/STAND"

    chown -R root:"$DIENST_BENUTZER" "$ziel"
    find "$ziel" -type d -exec chmod 750 {} +
    find "$ziel" -type f -exec chmod 640 {} +
    chmod 750 "$ziel"/betrieb/*.sh "$ziel/betrieb/vertretung"
    touch "$ziel/FERTIG"
    rm -rf -- "$arbeit" "$ausgabe"
    NEUE_VERSION="$ziel"
    ok "Gebaut: $(version_text "$ziel")"
}

versionen_aufraeumen() {   # laufende Version + die zwei neuesten anderen bleiben; unfertige Ordner weg
    local aktiv v behalten=0
    aktiv=$(basename "$(version_aktiv)")
    local -a alle=()
    mapfile -t alle < <(versionen_liste)
    local i
    for (( i=${#alle[@]}-1; i>=0; i-- )); do
        v="${alle[i]}"
        if [[ "$v" == "$aktiv" ]]; then continue; fi
        if (( behalten < VERSIONEN_BEHALTEN - 1 )); then behalten=$((behalten + 1)); continue; fi
        rm -rf -- "${VERSIONEN:?}/$v"
        info "Alte Version $v entfernt."
    done
    for v in "$VERSIONEN"/20*_*; do
        if [[ -d "$v" && ! -f "$v/FERTIG" && "$(basename "$v")" != "$aktiv" ]]; then
            rm -rf -- "$v"
            info "Unfertige Version $(basename "$v") entfernt."
        fi
    done
    return 0
}

# ---------------------------------------------------------------------
#  Dienst
# ---------------------------------------------------------------------

einheiten_einspielen() {   # $1 = betrieb-Ordner einer Version; 0 = etwas geändert
    local e geaendert=1
    for e in "${EINHEITEN[@]}"; do
        if datei_setzen "$SYSTEMD_ORDNER/$e" 644 root:root < "$1/$e"; then geaendert=0; fi
    done
    systemctl daemon-reload
    mkdir -p "$(dirname "$BEFEHL")"
    ln -sfn "$OPT/aktuell/betrieb/vertretung" "$BEFEHL"
    return "$geaendert"
}

# dienst_umstellen VERSION – hält den Dienst an, setzt „aktuell“ und spielt die Units dieser Version ein.
dienst_umstellen() {
    systemctl stop "$DIENST" 2>/dev/null || true
    ln -sfn "$1" "$AKTUELL.neu"
    mv -Tf "$AKTUELL.neu" "$AKTUELL"
    einheiten_einspielen "$1/betrieb" || true
}

journal_seit() {   # $1 = Sekunden seit 1970; Meldungen des Dienstes seitdem (ohne Zeitstempel)
    journalctl -u "$DIENST" --since "@$1" -o cat --no-pager 2>/dev/null || true
}

# Gesund = /api/gesundheit antwortet „ok“. Lokal mit Host „localhost“ (AllowedHosts) und X-Forwarded-Proto https
# (sonst leitet die API wegen ASPNETCORE_HTTPS_PORT auf https um); 127.0.0.1 ist bekannter Proxy.
gesund_lokal() {
    local antwort
    antwort=$(curl -fsS -m 5 -H 'Host: localhost' -H 'X-Forwarded-Proto: https' -H 'X-Forwarded-For: 127.0.0.1' \
        "$API_LOKAL/api/gesundheit" 2>/dev/null) || return 1
    [[ "$antwort" == *'"status":"ok"'* ]]
}
gesund_aussen() {   # $1 = Adresse
    local antwort
    antwort=$(curl -fsS -m 10 "https://$1/api/gesundheit" 2>/dev/null) || return 1
    [[ "$antwort" == *'"status":"ok"'* ]]
}
api_option() {   # $1 = Feld aus GET api/auth/optionen (z. B. probebetrieb) → Wert bzw. leer
    curl -fsS -m 5 -H 'Host: localhost' -H 'X-Forwarded-Proto: https' -H 'X-Forwarded-For: 127.0.0.1' \
        "$API_LOKAL/api/auth/optionen" 2>/dev/null | python3 -c 'import json, sys
try:
    v = json.load(sys.stdin).get(sys.argv[1])
except Exception:
    v = None
print("" if v is None else (str(v).lower() if isinstance(v, bool) else v))' "$1" 2>/dev/null || true
}

# dienst_starten_pruefen → 0 = läuft und antwortet. Wartet bis zu 2 Minuten (Migrationen beim ersten Start).
dienst_starten_pruefen() {
    local seit i neustarts0 neustarts
    seit=$(date +%s)
    systemctl start "$DIENST" || true
    neustarts0=$(systemctl show -p NRestarts --value "$DIENST" 2>/dev/null || echo 0)
    info "Warte auf die API (bis zu 2 Minuten) …"
    for (( i=0; i<60; i++ )); do
        if gesund_lokal; then
            ok "Die API läuft und antwortet (lokal)."
            journal_seit "$seit" | grep -E 'Betrieb: |Zeitzone |neuesten Stand' | sed 's/^/      /' || true
            return 0
        fi
        if journal_seit "$seit" | grep 'VertretungApi startet nicht' >/dev/null; then break; fi
        if [[ -n "$(journalctl -u "$DIENST" --since "@$seit" -p crit -o cat --no-pager 2>/dev/null || true)" ]]; then break; fi
        neustarts=$(systemctl show -p NRestarts --value "$DIENST" 2>/dev/null || echo 0)
        if [[ "$neustarts" =~ ^[0-9]+$ && "$neustarts0" =~ ^[0-9]+$ ]] && (( neustarts > neustarts0 + 1 )); then break; fi
        sleep 2
    done
    warnung "Die API startet nicht bzw. antwortet nicht. Aus dem Log:"
    # Meldungen der Startprüfung (ohne Priorität, „ - …“) und alles ab Priorität „err“ (crit, fail); sonst das Ende
    local auszug
    auszug=$( { journal_seit "$seit" | grep -E 'startet nicht|^ - ' || true
                journalctl -u "$DIENST" --since "@$seit" -p err -o cat --no-pager 2>/dev/null || true; } | tail -n 20 )
    if [[ -z "$auszug" ]]; then auszug=$(journal_seit "$seit" | tail -n 15); fi
    printf '%s\n' "$auszug" | cut -c1-300 | sed 's/^/      /' >&2
    info "Ganzes Log: vertretung log 100"
    return 1
}

# migrationen_pruefen VERSION → 0 = alle Migrationen des Programms sind in der Datenbank
migrationen_pruefen() {
    local fehlen
    fehlen=$(LC_ALL=C comm -23 "$1/MIGRATIONEN" <(migrationen_in_db) | paste -sd ' ' || true)
    if [[ -n "$fehlen" ]]; then
        warnung "In der Datenbank fehlen Migrationen: $fehlen"
        return 1
    fi
    ok "Datenbank: alle $(wc -l < "$1/MIGRATIONEN") Migrationen angewendet."
}

# ---------------------------------------------------------------------
#  Sicherung
# ---------------------------------------------------------------------

sicherungsskript() { printf '%s' "$AKTUELL/betrieb/sicherung.sh"; }

# sicherung_anlegen ZUSATZ → SICHERUNG_DATEI (Pfad); Rückgabe 1 bei Fehler
sicherung_anlegen() {
    SICHERUNG_DATEI=""
    als_dienst env SICHERUNG_CONF="$SICHERUNG_CONF" SICHERUNG_ZUSATZ="$1" "$(sicherungsskript)" 2>&1 | sed 's/^/      /' \
        || return 1
    SICHERUNG_DATEI=$(ls -1t "$SICHERUNGEN"/"$DB_NAME"_*_"$1".dump.gpg 2>/dev/null | head -n 1 || true)
    [[ -n "$SICHERUNG_DATEI" ]]
}

# rueckspielen DATEI – Dienst muss angehalten sein (sicherung.sh prüft offene Verbindungen).
rueckspielen() {
    als_dienst env SICHERUNG_CONF="$SICHERUNG_CONF" "$(sicherungsskript)" --zurueckspielen "$1" 2>&1 | sed 's/^/      /'
}

neueste_sicherung() {
    ls -1t "$SICHERUNGEN"/"$DB_NAME"_*.dump.gpg 2>/dev/null | head -n 1 || true
}

# ---------------------------------------------------------------------
#  Programm als Befehl (admin-anlegen) – mit derselben Umgebung wie der Dienst (Environment= aus der Unit)
# ---------------------------------------------------------------------
programm_ausfuehren() {
    local -a umgebung=() pruef=()
    mapfile -t umgebung < <(sed -n 's/^Environment=//p' "$SYSTEMD_ORDNER/$DIENST")
    (( ${#umgebung[@]} > 0 )) || fehler "In $SYSTEMD_ORDNER/$DIENST fehlen die Zeilen Environment=."
    if [[ -n "$WURZEL" ]]; then pruef=("PRUEF_WURZEL=$WURZEL"); fi
    ALS_ORDNER="$AKTUELL/app" als_dienst env -i PATH="${WURZEL:+$PATH:}/usr/bin:/bin" HOME="$DIENST_HEIM" LANG=C.UTF-8 \
        "${umgebung[@]}" "${pruef[@]}" Logging__Console__FormatterName=simple Logging__LogLevel__Default=Warning \
        dotnet "$AKTUELL/app/VertretungApi.dll" "$@"
}

# geheimnisse_zeigen PUSH PASSPHRASE – leere Werte werden ausgelassen
geheimnisse_zeigen() {
    local linie="--------------------------------------------------------------------------"
    printf '\n%s%s%s\n' "$FETT" "$linie" "$NORMAL"
    if [[ -n "${ADRESSE:-}" ]]; then printf '  Adresse:  https://%s\n\n' "$ADRESSE"; fi
    if [[ -n "$1" ]]; then
        printf '  Push-Schlüssel (für den Desktop, „Server verbinden…“ / „Online-Einstellungen…“):\n      %s\n\n' "$1"
    fi
    if [[ -n "$2" ]]; then
        printf '  Passphrase der Sicherung (in den Passwortmanager – ohne sie ist jede Sicherung wertlos):\n      %s\n\n' "$2"
    fi
    printf '  Nicht abfotografieren, nicht in einen Chat kopieren. Danach den Bildschirm mit  clear  leeren.\n'
    printf '  Später noch einmal anzeigen: vertretung schluessel\n'
    printf '%s%s%s\n' "$FETT" "$linie" "$NORMAL"
}
