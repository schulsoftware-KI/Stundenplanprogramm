<#
=====================================================================
 Verschlüsselte Sicherung der Klassenbuch-Datenbank (PostgreSQL, Windows) – Phase 5.1

 Aufruf (PowerShell):
   powershell -ExecutionPolicy Bypass -File sicherung.ps1            neue Sicherung, alte nach Frist löschen
   powershell -ExecutionPolicy Bypass -File sicherung.ps1 -Pruefen   neueste Sicherung in eine Testdatenbank
                                                                     zurückspielen, Datensätze zählen, wieder löschen

 Die Sicherung ist ein pg_dump (Format custom) in einem 7-Zip-Archiv mit AES-256 und
 verschlüsselten Dateinamen. Ohne die Passphrase ist sie wertlos – Passphrase getrennt vom
 Server aufbewahren (z. B. versiegelt im Tresor der Schulleitung).
 Voraussetzungen: PostgreSQL (bringt pg_dump/pg_restore/psql mit) und 7-Zip
 (winget install 7zip.7zip).

 Einstellungen: Datei sicherung.einstellungen.ps1 neben diesem Skript (nicht ins Repo –
 steht in .gitignore). Beispiel:
     $PgBin           = 'C:\Program Files\PostgreSQL\17\bin'
     $DbHost          = 'localhost'
     $DbPort          = 5432
     $DbBenutzer      = 'vertretung'
     $Datenbank       = 'vertretung'
     $Ordner          = 'D:\Sicherungen\Vertretung'
     $PassphraseDatei = 'C:\Vertretung\sicherung-passphrase.txt'
     $Tage            = 30
     $SiebenZip       = 'C:\Program Files\7-Zip\7z.exe'
 Das Datenbankpasswort steht in %APPDATA%\postgresql\pgpass.conf des Benutzers, unter dem
 das Skript läuft (Zeile: localhost:5432:*:vertretung:PASSWORT). Für -Pruefen braucht der
 Datenbankbenutzer das Recht CREATEDB.
 Hinweis: 7-Zip bekommt die Passphrase als Aufrufparameter; sie ist während der Sicherung
 für Administratoren dieses Rechners in der Prozessliste sichtbar.

 Täglich um 2:30 Uhr: Aufgabenplanung → Einfache Aufgabe → Programm „powershell.exe“,
 Argumente „-ExecutionPolicy Bypass -File "C:\Vertretung\Betrieb\sicherung.ps1"“,
 „Unabhängig von der Benutzeranmeldung ausführen“.
 Rücksicherung im Ernstfall: siehe README, Abschnitt „Produktionsmodus (Phase 5.1)“.
=====================================================================
#>
param([switch]$Pruefen)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

# --- Standardwerte (in sicherung.einstellungen.ps1 überschreiben) ---
$PgBin           = $null      # leer: neueste Version unter C:\Program Files\PostgreSQL
$DbHost          = 'localhost'
$DbPort          = 5432
$DbBenutzer      = 'vertretung'
$Datenbank       = 'vertretung'
$Ordner          = 'C:\Sicherungen\Vertretung'
$PassphraseDatei = $null
$Tage            = 30
$SiebenZip       = 'C:\Program Files\7-Zip\7z.exe'

$einstellungen = Join-Path $PSScriptRoot 'sicherung.einstellungen.ps1'
if (Test-Path $einstellungen) { . $einstellungen }

function Info([string]$text) { Write-Host ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $text) }

# Ruft ein Programm auf; Fehlercode ≠ 0 bricht ab (die Argumente – mit Passphrase – stehen nicht in der Meldung).
function Ausfuehren([string]$programm, [string[]]$argumente) {
    & $programm @argumente
    if ($LASTEXITCODE -ne 0) {
        throw ('{0} meldet einen Fehler (Code {1}).' -f [IO.Path]::GetFileName($programm), $LASTEXITCODE)
    }
}

function NeuerArbeitsordner {
    $pfad = Join-Path ([IO.Path]::GetTempPath()) ('vertretung-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $pfad | Out-Null
    return $pfad
}

# --- Voraussetzungen ---
if (-not $PgBin) {
    $kandidat = Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
    if ($kandidat) { $PgBin = Join-Path $kandidat.FullName 'bin' }
}
if (-not $PgBin -or -not (Test-Path (Join-Path $PgBin 'pg_dump.exe'))) {
    throw 'pg_dump.exe nicht gefunden – $PgBin in sicherung.einstellungen.ps1 setzen.'
}
if (-not (Test-Path $SiebenZip)) { throw "7-Zip nicht gefunden ($SiebenZip) – installieren: winget install 7zip.7zip" }
if (-not $PassphraseDatei) { throw '$PassphraseDatei ist nicht gesetzt (sicherung.einstellungen.ps1).' }
if (-not (Test-Path $PassphraseDatei)) { throw "Passphrase-Datei $PassphraseDatei fehlt." }
$Passphrase = (Get-Content -Raw -Path $PassphraseDatei).Trim()
if ($Passphrase.Length -lt 12) { throw 'Die Passphrase ist leer oder kürzer als 12 Zeichen.' }

$pgDump    = Join-Path $PgBin 'pg_dump.exe'
$pgRestore = Join-Path $PgBin 'pg_restore.exe'
$psql      = Join-Path $PgBin 'psql.exe'
$createdb  = Join-Path $PgBin 'createdb.exe'
$dropdb    = Join-Path $PgBin 'dropdb.exe'
$Verbindung = @('-h', $DbHost, '-p', "$DbPort", '-U', $DbBenutzer, '-w')   # -w: nie nach Passwort fragen
$muster = "$($Datenbank)_*.dump.7z"

function Sichern {
    New-Item -ItemType Directory -Force -Path $Ordner | Out-Null
    $ziel = Join-Path $Ordner ('{0}_{1:yyyy-MM-dd_HHmmss}.dump.7z' -f $Datenbank, (Get-Date))
    $arbeit = NeuerArbeitsordner
    try {
        $dump = Join-Path $arbeit 'sicherung.dump'
        Ausfuehren $pgDump ($Verbindung + @('--format=custom', '--no-owner', '--no-privileges', "--file=$dump", $Datenbank))
        Ausfuehren $SiebenZip @('a', '-t7z', '-mhe=on', '-mx=1', '-bso0', '-bsp0', "-p$Passphrase", $ziel, $dump)
    }
    catch {
        Remove-Item -Force $ziel -ErrorAction SilentlyContinue   # keine halbe Sicherung liegen lassen
        throw
    }
    finally {
        Remove-Item -Recurse -Force $arbeit -ErrorAction SilentlyContinue
    }
    Info ('Sicherung angelegt: {0} ({1:N0} KB)' -f $ziel, ((Get-Item $ziel).Length / 1KB))

    if ($Tage -gt 0) {
        $grenze = (Get-Date).AddDays(-$Tage)
        $alt = @(Get-ChildItem -Path $Ordner -Filter $muster | Where-Object { $_.LastWriteTime -lt $grenze })
        $alt | Remove-Item -Force
        if ($alt.Count -gt 0) { Info "$($alt.Count) Sicherung(en) älter als $Tage Tage gelöscht." }
    }
}

function Pruefen {
    $neueste = Get-ChildItem -Path $Ordner -Filter $muster -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $neueste) { throw "Keine Sicherung in $Ordner gefunden." }
    Info "Prüfe $($neueste.FullName)"

    $pruefdb = "$($Datenbank)_pruefung"
    $arbeit = NeuerArbeitsordner
    try {
        Ausfuehren $SiebenZip @('e', '-y', '-bso0', '-bsp0', "-p$Passphrase", "-o$arbeit", $neueste.FullName)
        $dump = Join-Path $arbeit 'sicherung.dump'
        & $dropdb @Verbindung --if-exists $pruefdb
        Ausfuehren $createdb ($Verbindung + @($pruefdb))
        Ausfuehren $pgRestore ($Verbindung + @('--no-owner', '--no-privileges', '--exit-on-error', "--dbname=$pruefdb", $dump))

        # Zählen per SQL-Datei (Anführungszeichen in Aufrufparametern sind in PowerShell unzuverlässig).
        $sql = Join-Path $arbeit 'zaehlen.sql'
        Set-Content -Path $sql -Encoding Ascii -Value @'
SELECT t, CASE WHEN to_regclass(quote_ident(t)) IS NULL THEN -1
       ELSE (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I', t), false, true, '')))[1]::text::bigint END
FROM unnest(ARRAY['Benutzer','Schueler','Lerngruppen','KlassenbuchLektionen','Anwesenheiten','KlassenbuchEintraege','GrundplanVersionen']) AS t;
'@
        $zeilen = @(& $psql @Verbindung -d $pruefdb -A -t -F '|' -f $sql)
        if ($LASTEXITCODE -ne 0) { throw "psql meldet einen Fehler (Code $LASTEXITCODE)." }

        Info "Zurückgespielt in $pruefdb. Datensätze:"
        $konten = 0
        foreach ($zeile in $zeilen) {
            if (-not $zeile) { continue }
            $teile = $zeile -split '\|'
            $anzahl = if ($teile[1] -eq '-1') { '(Tabelle fehlt)' } else { $teile[1] }
            Write-Host ('  {0,-22} {1}' -f $teile[0], $anzahl)
            if ($teile[0] -eq 'Benutzer' -and $teile[1] -match '^\d+$') { $konten = [int64]$teile[1] }
        }
        if ($konten -le 0) { throw 'Die Rücksicherung enthält keine Konten – Sicherung unbrauchbar?' }
        Info 'Rücksicherungstest in Ordnung. Testdatenbank wird gelöscht.'
    }
    finally {
        try { & $dropdb @Verbindung --if-exists $pruefdb } catch { }
        Remove-Item -Recurse -Force $arbeit -ErrorAction SilentlyContinue
    }
}

try {
    if ($Pruefen) { Pruefen } else { Sichern }
    exit 0
}
catch {
    Write-Host ('{0:yyyy-MM-dd HH:mm:ss} FEHLER: {1}' -f (Get-Date), $_.Exception.Message) -ForegroundColor Red
    exit 1
}
