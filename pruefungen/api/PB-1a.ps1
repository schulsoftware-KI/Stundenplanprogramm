# =====================================================================
#  PB-1a: API-Pruefungen ohne Anmeldung (Vorgriff auf die Testsammlung TS, F14 Variante B)
#  Laeuft mit Windows PowerShell 5.1 und PowerShell 7 (HttpClient - 4xx werfen keine Ausnahme).
#  Prueft: Lebenszeichen, Betriebsart in api/auth/optionen, lesbare 400 (318) fuer App und Desktop,
#  im Echt-/Probebetrieb: Seeds 404; mit -PushKey zusaetzlich 400 der Desktop-Schnittstelle und die Betriebsart
#  im SchILD-Status.
#  Schreibt nichts in die Datenbank (alle 400 kommen vor dem Controller, Seeds sind im Echtbetrieb 404).
#
#  Aufruf (Terminal 2, Ordner z63, API laeuft in Terminal 1):
#    powershell -ExecutionPolicy Bypass -File .\pruefungen\api\PB-1a.ps1 -Erwartet Entwicklung
#    powershell -ExecutionPolicy Bypass -File .\pruefungen\api\PB-1a.ps1 -Erwartet Probebetrieb
#  -Erwartet  Entwicklung | Echtbetrieb | Probebetrieb  (Betriebsart der laufenden API)
#  -Adresse   Standard http://localhost:5080
#  -PushKey   optional: Push-Schluessel der laufenden API (dann auch GET api/schild/status)
# =====================================================================
param(
  [ValidateSet("Entwicklung", "Echtbetrieb", "Probebetrieb")][string]$Erwartet = "Entwicklung",
  [string]$Adresse = "http://localhost:5080",
  [string]$PushKey = ""
)

Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(30)
$script:ok = 0
$script:fehl = 0

function Pruefe([string]$name, [bool]$bedingung, [string]$info = "") {
  if ($bedingung) { $script:ok++; Write-Host ("OK    " + $name) -ForegroundColor Green }
  else { $script:fehl++; Write-Host ("FEHL  " + $name + $(if ($info) { " - " + $info } else { "" })) -ForegroundColor Red }
}

function Anfrage([string]$methode, [string]$pfad, $body = $null, [hashtable]$kopf = @{}) {
  # $body bewusst ohne [string]: sonst wird $null zu "" und GET bekaeme einen Koerper
  $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($methode), ($Adresse.TrimEnd("/") + "/" + $pfad))
  foreach ($k in $kopf.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, $kopf[$k]) }
  if ($null -ne $body) { $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json") }
  $antwort = $client.SendAsync($req).Result
  $text = $antwort.Content.ReadAsStringAsync().Result
  $json = $null
  try { if ($text) { $json = $text | ConvertFrom-Json } } catch { $json = $null }
  return [pscustomobject]@{ Status = [int]$antwort.StatusCode; Text = $text; Json = $json }
}

Write-Host ("PB-1a: API " + $Adresse + ", erwartet: " + $Erwartet)
$produktiv = $Erwartet -ne "Entwicklung"

# 1. Lebenszeichen
$r = Anfrage "GET" "api/gesundheit"
Pruefe "1 api/gesundheit -> ok" ($r.Status -eq 200 -and $r.Json.status -eq "ok") ("HTTP " + $r.Status + " " + $r.Text)

# 2. Betriebsart fuer die App (Band)
$r = Anfrage "GET" "api/auth/optionen"
Pruefe "2 api/auth/optionen erreichbar" ($r.Status -eq 200) ("HTTP " + $r.Status)
Pruefe ("2 probebetrieb = " + ($Erwartet -eq "Probebetrieb")) ($r.Json.probebetrieb -eq ($Erwartet -eq "Probebetrieb")) ("geliefert: " + $r.Json.probebetrieb)
if ($produktiv) { Pruefe "2 kein Testdatum im Echt-/Probebetrieb" ($null -eq $r.Json.testdatum) ("geliefert: " + $r.Json.testdatum) }

# 3. Lesbare 400 (318) - App: Anmeldung ohne Passwort, unlesbares JSON, leerer Koerper
$r = Anfrage "POST" "api/auth/login" '{"benutzername":"pb1a-pruefung"}'
Pruefe "3a Login ohne Passwort -> 400" ($r.Status -eq 400) ("HTTP " + $r.Status)
Pruefe "3a Text nennt das Feld (passwort)" ($r.Json.fehler -like "*(passwort)*") $r.Text
Pruefe "3a Feldliste enthaelt passwort" (@($r.Json.felder) -contains "passwort") $r.Text
Pruefe "3a eingegebener Wert steht nicht in der Antwort" (-not ($r.Text -like "*pb1a-pruefung*")) $r.Text
$r = Anfrage "POST" "api/auth/login" '{"benutzername": '
Pruefe "3b unlesbares JSON -> 400 'konnten nicht gelesen werden'" ($r.Status -eq 400 -and $r.Json.fehler -like "*nicht gelesen*") $r.Text
$r = Anfrage "POST" "api/auth/login" 'null'
Pruefe "3c leerer Koerper (null) -> 400 'Die Angaben fehlen'" ($r.Status -eq 400 -and $r.Json.fehler -like "Die Angaben fehlen*") $r.Text

# 4. Desktop-Schnittstelle: ohne Schluessel 401 vor dem Lesen des Koerpers (P6a); mit Schluessel lesbare 400
#    (die Pruefung der Angaben kommt vor der Aktion - es wird nichts geschrieben)
$r = Anfrage "POST" "api/plan/push" '{"datum":"2026-11-30"}'
Pruefe "4 Desktop ohne Schluessel -> 401 (keine Auskunft ueber die Angaben)" ($r.Status -eq 401) ("HTTP " + $r.Status)
if ($PushKey) {
  $r = Anfrage "POST" "api/plan/push" '{"datum":"2026-11-30"}' @{ "X-Api-Key" = $PushKey }
  Pruefe "4 Desktop mit Schluessel: api/plan/push unvollstaendig -> 400" ($r.Status -eq 400) ("HTTP " + $r.Status)
  Pruefe "4 Desktop-Text (Desktop-Programm und Server)" ($r.Json.fehler -like "*Desktop-Programm*") $r.Text
  Pruefe "4 Felder schuljahr und eintraege" ((@($r.Json.felder) -contains "schuljahr") -and (@($r.Json.felder) -contains "eintraege")) $r.Text
}

# 5. Seeds im Echt-/Probebetrieb gesperrt (in der Entwicklung NICHT aufrufen - sie wuerden Daten anlegen)
if ($produktiv) {
  $r = Anfrage "POST" "api/seed/pseudodaten" $null @{ "X-Api-Key" = "beliebig" }
  Pruefe "5 api/seed/pseudodaten -> 404" ($r.Status -eq 404) ("HTTP " + $r.Status)
  $r = Anfrage "POST" "api/auth/seed-konten" $null @{ "X-Api-Key" = "beliebig" }
  Pruefe "5 api/auth/seed-konten -> 404" ($r.Status -eq 404) ("HTTP " + $r.Status)
}

# 6. Betriebsart im SchILD-Status (nur mit -PushKey)
if ($PushKey) {
  $r = Anfrage "GET" "api/schild/status" $null @{ "X-Api-Key" = $PushKey }
  Pruefe ("6 SchILD-Status: betrieb = " + $Erwartet) ($r.Status -eq 200 -and $r.Json.betrieb -eq $Erwartet) ("HTTP " + $r.Status + " betrieb " + $r.Json.betrieb)
  Pruefe "6 SchILD-Status: pseudo passt" ($r.Json.pseudo -eq ($Erwartet -ne "Echtbetrieb")) ("pseudo " + $r.Json.pseudo)
}

Write-Host ""
Write-Host ("Ergebnis: " + $script:ok + " OK, " + $script:fehl + " FEHL") -ForegroundColor $(if ($script:fehl -eq 0) { "Green" } else { "Red" })
$client.Dispose()
if ($script:fehl -gt 0) { exit 1 }
