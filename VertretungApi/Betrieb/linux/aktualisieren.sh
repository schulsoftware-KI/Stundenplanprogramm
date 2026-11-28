#!/usr/bin/env bash
# =====================================================================
#  Vertretung – Update des Testservers auf einen Commit aus master (PB-1b, Entscheidungen 301/313, 315, 316)
#
#  Aufruf als root (bequemer über den Befehl „vertretung“):
#    aktualisieren.sh [COMMIT]    neuester Stand von master bzw. der genannte Commit (muss in master liegen)
#    aktualisieren.sh --zurueck   eine Version zurück (mit Rücksicherung, falls die Datenbank sonst nicht passt)
#    aktualisieren.sh --neustart  Dienst neu starten und prüfen (z. B. nach einer Änderung der Einstellungsdatei)
#
#  Ablauf eines Updates:
#    1. Stand holen, Ziel bestimmen        2. neue Commits und Migrationen zeigen, Rückfrage
#    3. bauen (als vertretung-bau) – scheitert der Bau, läuft die alte Version unverändert weiter
#    4. Sicherung (…_vor-update-<commit>.dump.gpg) – ohne Sicherung kein Update
#    5. anhalten → umstellen (Verweis „aktuell“, Units der neuen Version) → starten → prüfen (Lebenszeichen,
#       Migrationen, über Caddy). Fehler: alte Version zurück; hat das Update Migrationen angewendet, Rückfrage,
#       ob die Datenbank aus der Sicherung von Schritt 4 zurückgespielt wird (empfohlen: ja)
#    6. alte Versionen aufräumen (die laufende und die zwei neuesten anderen bleiben)
#
#  Testschalter (PB-E): VERTRETUNG_TEST_UPDATEFEHLER=1 vertretung aktualisieren – das Update gilt nach dem Start als
#  fehlgeschlagen, damit sich der Rückweg (samt Rücksicherung) auf dem Testserver prüfen lässt.
# =====================================================================
set -euo pipefail
umask 022
HIER="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
# shellcheck source=gemeinsam.sh
. "$HIER/gemeinsam.sh"

aufruf() {
    cat <<'EOF'
Aufruf (als root):  aktualisieren.sh [COMMIT] | --zurueck | --neustart
Bequemer:           vertretung aktualisieren [COMMIT] · vertretung zurueck · vertretung neustart
EOF
}

test_updatefehler() {   # Testschalter, siehe Kopf
    if [[ "${VERTRETUNG_TEST_UPDATEFEHLER:-}" == 1 ]]; then
        warnung "Testschalter VERTRETUNG_TEST_UPDATEFEHLER=1: Das Update gilt als fehlgeschlagen – der Rückweg wird geprüft."
        return 0
    fi
    return 1
}

aussen_pruefen() {   # über Caddy – ein Fehler hier ist kein Grund zurückzugehen (die API selbst läuft)
    [[ -s "$ADRESSDATEI" ]] || return 0
    local adresse
    adresse=$(head -n 1 "$ADRESSDATEI")
    if gesund_aussen "$adresse"; then
        ok "Über https://$adresse erreichbar."
    else
        warnung "Über https://$adresse nicht erreichbar (Caddy bzw. Zertifikat?) – die API selbst läuft. Siehe: vertretung status"
    fi
    return 0
}

# rueckweg ALT SICHERUNG – nach einem fehlgeschlagenen Update; setzt RUECKWEG_OK
rueckweg() {
    local alt="$1" sicherung="$2" angewandt
    RUECKWEG_OK=0
    warnung "Zurück auf $(version_text "$alt") …"
    systemctl stop "$DIENST" 2>/dev/null || true
    angewandt=$(LC_ALL=C comm -23 <(migrationen_in_db) "$alt/MIGRATIONEN" | paste -sd ' ' || true)
    if [[ -n "$angewandt" ]]; then
        info "Das Update hat Migrationen angewendet ($angewandt) – das alte Programm kennt dieses Schema nicht."
        if frage_jn "Datenbank aus der Sicherung von eben zurückspielen ($(basename "$sicherung"))? Empfohlen." j; then
            if ! rueckspielen "$sicherung"; then
                warnung "Zurückspielen fehlgeschlagen (Meldung oben) – die Datenbank bleibt auf dem neuen Schema."
            fi
        else
            warnung "Die Datenbank bleibt auf dem neuen Schema – das alte Programm kann damit Fehler zeigen."
        fi
    fi
    dienst_umstellen "$alt"
    if dienst_starten_pruefen; then
        RUECKWEG_OK=1
        ok "Die alte Version läuft wieder."
    else
        warnung "Auch die alte Version startet nicht – bitte: vertretung log 100"
    fi
}

aktualisieren() {
    local angabe="${1:-}" alt alt_commit neu neu_mig weg_mig anzahl sicherung
    alt=$(version_aktiv)
    [[ -n "$alt" ]] || fehler "Hier ist noch nichts installiert – zuerst installieren.sh (siehe LIESMICH)."
    alt_commit=$(cat "$alt/COMMIT")

    schritt "1/6 Stand holen"
    quelle_holen
    commit_aufloesen "$angabe"
    info "Jetzt: $(cat "$alt/STAND")"
    info "Neu:   $(commit_text "$ZIEL_COMMIT")"
    if [[ "$ZIEL_COMMIT" == "$alt_commit" ]]; then
        ok "Dieser Stand ist schon installiert – nichts zu tun."
        return 0
    fi

    schritt "2/6 Änderungen"
    if git -C "$QUELLE" merge-base --is-ancestor "$alt_commit" "$ZIEL_COMMIT"; then
        anzahl=$(git -C "$QUELLE" rev-list --count "$alt_commit..$ZIEL_COMMIT")
        info "$anzahl neue(r) Commit(s):"
        git -C "$QUELLE" log --date=format-local:'%d.%m.' --format='      %h %cd %s' -n 15 "$alt_commit..$ZIEL_COMMIT"
        if (( anzahl > 15 )); then info "      …"; fi
    else
        warnung "Der neue Stand ist älter als der jetzige."
    fi
    weg_mig=$(LC_ALL=C comm -23 <(migrationen_in_db) <(migrationen_im_commit "$ZIEL_COMMIT") | paste -sd ' ' || true)
    [[ -z "$weg_mig" ]] || fehler "Der Stand ${ZIEL_COMMIT:0:7} kennt diese Migration(en) der Datenbank nicht: $weg_mig. Zurück geht es nur mit „vertretung zurueck“ (mit Rücksicherung)."
    neu_mig=$(LC_ALL=C comm -13 "$alt/MIGRATIONEN" <(migrationen_im_commit "$ZIEL_COMMIT") | paste -sd ' ' || true)
    if [[ -n "$neu_mig" ]]; then
        info "Neue Migration(en): $neu_mig"
        info "Vorher wird gesichert; scheitert das Update, kann die Datenbank zurückgespielt werden."
    else
        info "Keine neuen Migrationen."
    fi
    if ! frage_jn "Weiter?" n; then
        info "Abgebrochen – nichts geändert."
        return 0
    fi

    schritt "3/6 Bauen"
    neu=$(version_fuer_commit "$ZIEL_COMMIT")
    if [[ -n "$neu" ]]; then
        ok "Schon gebaut: $(version_text "$neu")"
    else
        programm_bauen "$ZIEL_COMMIT" || fehler "Der Bau ist fehlgeschlagen – die laufende Version bleibt unverändert."
        neu="$NEUE_VERSION"
    fi

    schritt "4/6 Sicherung"
    sicherung_anlegen "vor-update-${ZIEL_COMMIT:0:7}" \
        || fehler "Die Sicherung ist fehlgeschlagen – ohne Sicherung kein Update. Die laufende Version bleibt unverändert."
    sicherung="$SICHERUNG_DATEI"

    schritt "5/6 Umstellen und starten"
    dienst_umstellen "$neu"
    if dienst_starten_pruefen && migrationen_pruefen "$neu" && ! test_updatefehler; then
        aussen_pruefen
    else
        rueckweg "$alt" "$sicherung"
        if (( RUECKWEG_OK )); then
            fehler "Das Update auf ${ZIEL_COMMIT:0:7} ist fehlgeschlagen; es läuft wieder $(basename "$alt")."
        fi
        fehler "Das Update auf ${ZIEL_COMMIT:0:7} ist fehlgeschlagen, und die alte Version startet auch nicht – bitte: vertretung log 100"
    fi

    schritt "6/6 Aufräumen"
    versionen_aufraeumen
    ok "Update fertig: $(version_text "$neu")"
}

zurueck() {
    local aktiv vorg="" v name mig sicherung c7
    aktiv=$(version_aktiv)
    [[ -n "$aktiv" ]] || fehler "Hier ist noch nichts installiert."
    name=$(basename "$aktiv")
    while IFS= read -r v; do
        if [[ "$v" < "$name" ]]; then vorg="$VERSIONEN/$v"; fi
    done < <(versionen_liste)
    [[ -n "$vorg" ]] || fehler "Auf diesem Server gibt es keine ältere Version (es bleiben die letzten $VERSIONEN_BEHALTEN)."

    schritt "Eine Version zurück"
    info "Jetzt:  $(version_text "$aktiv")"
    info "Zurück: $(version_text "$vorg")"
    c7=$(cut -c1-7 "$aktiv/COMMIT")
    mig=$(LC_ALL=C comm -23 <(migrationen_in_db) "$vorg/MIGRATIONEN" | paste -sd ' ' || true)
    if [[ -n "$mig" ]]; then
        sicherung=$(ls -1t "$SICHERUNGEN"/"$DB_NAME"_*_vor-update-"$c7".dump.gpg 2>/dev/null | head -n 1 || true)
        [[ -n "$sicherung" ]] || fehler "Die Datenbank hat Migrationen, die die vorige Version nicht kennt ($mig), und es gibt keine Sicherung von vor dem Update auf $c7 – so geht es nicht zurück."
        info "Die Datenbank hat Migrationen, die die vorige Version nicht kennt: $mig."
        info "Zurück geht es nur zusammen mit der Sicherung von vor dem Update: $(basename "$sicherung")."
        warnung "Alles, was seit dieser Sicherung eingetragen wurde, geht verloren (der jetzige Stand wird vorher noch einmal gesichert)."
        if ! frage_jn "Datenbank zurückspielen und auf die vorige Version gehen?" n; then
            info "Abgebrochen – nichts geändert."
            return 0
        fi
        sicherung_anlegen "vor-zurueck-$c7" || fehler "Die Sicherung des jetzigen Stands ist fehlgeschlagen – nichts geändert."
        systemctl stop "$DIENST" 2>/dev/null || true
        if ! rueckspielen "$sicherung"; then
            warnung "Zurückspielen fehlgeschlagen – die jetzige Version startet wieder."
            dienst_starten_pruefen || true
            fehler "Zurück ist fehlgeschlagen; es läuft weiter $name."
        fi
    else
        info "Die Datenbank passt auch zur vorigen Version (keine Migrationen dazwischen) – sie bleibt, wie sie ist."
        if ! frage_jn "Auf die vorige Version gehen?" n; then
            info "Abgebrochen – nichts geändert."
            return 0
        fi
    fi
    dienst_umstellen "$vorg"
    dienst_starten_pruefen || fehler "Die vorige Version startet nicht – bitte: vertretung log 100"
    migrationen_pruefen "$vorg" || true
    aussen_pruefen
    ok "Zurück auf $(version_text "$vorg")."
}

neustart() {
    [[ -n "$(version_aktiv)" ]] || fehler "Hier ist noch nichts installiert."
    schritt "Neustart"
    systemctl stop "$DIENST" 2>/dev/null || true
    dienst_starten_pruefen || fehler "Die API startet nicht – Meldung oben (oft ein Tippfehler in $EINSTELLUNGEN); danach: vertretung neustart"
    aussen_pruefen
}

main() {
    case "${1:-}" in
        -h|--hilfe|hilfe) aufruf; return 0 ;;
    esac
    pruefe_root
    sperren
    case "${1:-}" in
        --zurueck)  zurueck ;;
        --neustart) neustart ;;
        -*)         aufruf >&2; fehler "Unbekannte Angabe „$1“." ;;
        *)          aktualisieren "${1:-}" ;;
    esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
