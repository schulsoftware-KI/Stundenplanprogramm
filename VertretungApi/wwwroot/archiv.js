"use strict";
// =====================================================================
//  Stundenplan-PWA – Archive mit Kennwort (P5c-1; Entscheidungen 260–265, 273)
//
//  axDialog: ein Dialog für beide Archive, die im Hintergrund entstehen (API: ArchivAuftraege):
//    - Archiv-Export eines abgeschlossenen Schuljahres (Jahresablauf → Karte „Archiv und Fristen“), POST api/jahresablauf/archiv
//    - Jahresarchiv aus 4.3 (Klassenbuch → Klassen → „Jahresarchiv …“), POST api/berichte/archiv
//  Kennwort zweimal (mindestens 12 Zeichen, Zeichen der Tastatur ohne Umlaute), „Anzeigen“, Zähler. Das Kennwort geht
//  einmal im Körper der POST-Anfrage an den Server und wird danach aus den Feldern gelöscht; die App speichert es nie.
//  Danach zeigt der Dialog den Fortschritt (GET api/jahresablauf/archiv/auftrag, alle 1,5 s) – das Fenster darf zu, der
//  Auftrag läuft weiter. „Herunterladen“ holt die Datei EINMAL (GET …/archiv/datei); danach ist sie auf dem Server weg.
//
//  abDialog: „Aufbewahrung bestätigen“ (264) – Datei wählen (die SHA-256 rechnet der Browser auf dem Gerät, nichts wird
//  hochgeladen) oder Prüfsumme eintragen (PowerShell Get-FileHash), Haken „mit 7-Zip und dem Kennwort geöffnet“, Ort
//  (nie das Kennwort) und „aufzubewahren bis“ → POST api/jahresablauf/archiv/bestaetigen. P5c-2 (Befund C): Ist das
//  Bestätigen gespeichert, kommen Meldung und neue Karte immer – auch wenn der Dialog inzwischen zu ist; solange die Anfrage
//  läuft, schließt Esc ihn nicht. Befund E: Kennwortfelder mit autocomplete="new-password" (index.html).
//
//  ljDialog (P5c-2, 266–270): „Schuljahr … löschen…“ – POST api/jahresablauf/loeschen ohne Prüfsumme liefert 409 mit den
//  Zahlen je Art; der Dialog zeigt sie samt Archiv-Export und Hinweisen. Löschen erst, wenn das Schuljahr eingetippt und
//  das Passwort eingetragen ist; der zweite POST schickt beides mit der Prüfsumme der Zahlen. Andere Zahlen → 409 mit den
//  neuen (der Dialog zeigt sie, Passwort neu); falsches Passwort → 400. Kommt keine Antwort (Zeitüberschreitung, Proxy),
//  lädt die Karte neu – sie zeigt, ob gelöscht wurde. Das Passwort steht nach jeder Anfrage nicht mehr im Feld.
//
//  Wird nach jahresablauf.js und VOR hilfe.js/app.js geladen und nutzt deren Hilfen (h, el, senden, aktionsFehler,
//  dialogAuf, …) erst zur Laufzeit. Einstieg archivInit() aus start() in app.js. Namen mit Präfix „ax…“, „ab…“ bzw. „lj…“.
// =====================================================================

const AX_KW_MIN = 12, AX_KW_MAX = 128;
const AX_TAKT_MS = 1500;

const ax = {
  o: null,                // { art, schuljahr, von, bis, titel, was, text, gesperrt, nurStand, beimSchliessen }
  auftrag: null,          // letzter Stand (JaArchivAuftragDto)
  laeuft: false,          // eine Anfrage läuft (Start, Download, Verwerfen)
  timer: 0,
  nr: 0,
  gespeichert: null,      // Dateiname nach dem Download
};

const ab = {
  jahr: null,             // JaArchivJahrDto
  nr: 0,
  laeuft: false,
};

// P5c-2: Schuljahr löschen
const lj = {
  a: null,                // Aktion aus der Karte { schuljahr, text }
  r: null,                // Rückfrage der API (JaLoeschRueckfrageDto)
  laeuft: false,
  nr: 0,
};
const LJ_ZEIT_MS = 15 * 60 * 1000;   // länger als das Befehls-Zeitlimit der API (10 Minuten)

/** Wie Kennwort.Pruefen in der API (AesZip.cs). */
function axKennwortFehler(k) {
  if (k.length < AX_KW_MIN) return `Das Kennwort braucht mindestens ${AX_KW_MIN} Zeichen – am besten ein Satz aus mehreren Wörtern.`;
  if (k.length > AX_KW_MAX) return `Das Kennwort darf höchstens ${AX_KW_MAX} Zeichen haben.`;
  if (/[^\x20-\x7e]/.test(k))
    return "Bitte im Kennwort keine Umlaute, kein ß und keine Sonderzeichen außerhalb der Tastatur (ä → ae) – sonst öffnen " +
      "manche Programme das Archiv nicht.";
  if (k[0] === " " || k[k.length - 1] === " ") return "Bitte kein Leerzeichen am Anfang oder Ende des Kennworts.";
  return null;
}

const axMb = (b) => { const mb = b / 1048576; return mb >= 10 ? `${Math.round(mb)} MB` : `${mb.toFixed(1).replace(".", ",")} MB`; };
const axUhr = (t) => new Date(t).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
const axKurz = (sha) => (sha && sha.length >= 8 ? `${sha.slice(0, 4)}…${sha.slice(-4)}` : sha || "");

// ---------------------------------------------------------------------
//  Archiv erstellen
// ---------------------------------------------------------------------

/**
 * Dialog öffnen. o.art „export“ (o.schuljahr) bzw. „jahresarchiv“ (o.von/o.bis, null = Schuljahr); o.nurStand = gleich den
 * Stand des laufenden bzw. fertigen Auftrags zeigen; o.beimSchliessen = Rückruf nach dem Schließen.
 */
function axOeffnen(o) {
  if (!state.anmeldung || state.anmeldung.rolle !== "Admin") return;
  ax.o = o;
  ax.auftrag = null;
  ax.gespeichert = null;
  ax.nr++;
  el("axTitel").textContent = o.titel || "Archiv erstellen";
  el("axWas").textContent = o.was || "";
  el("axText").textContent = o.text || "";
  el("axSchueler").checked = false;      // Standard aus (wie beim PDF)
  axKennwortLeeren();
  el("axEingabe").hidden = !!o.nurStand;
  el("axStand").hidden = true;
  fehlerIn("axFehler", "");
  axKnoepfe();
  dialogAuf(el("axDialog"));
  setTimeout(() => { try { (o.nurStand || o.gesperrt ? el("axZu") : el("axKw1")).focus(); } catch { /* egal */ } }, 0);
  axAbfragen(ax.nr);   // gibt es schon einen Auftrag?
}

function axZu() {
  ax.nr++;
  clearTimeout(ax.timer);
  axKennwortLeeren();
  dialogZu(el("axDialog"));
}

function axKennwortLeeren() {
  el("axKw1").value = "";
  el("axKw2").value = "";
  el("axKw1").type = "password";
  el("axKw2").type = "password";
  el("axKwZeigen").textContent = "Anzeigen";
  el("axKwZeigen").setAttribute("aria-pressed", "false");
  axKennwortZaehlen();
}

function axKennwortZeigen() {
  const zeigen = el("axKw1").type === "password";
  el("axKw1").type = zeigen ? "text" : "password";
  el("axKw2").type = zeigen ? "text" : "password";
  el("axKwZeigen").textContent = zeigen ? "Verbergen" : "Anzeigen";
  el("axKwZeigen").setAttribute("aria-pressed", String(zeigen));
}

function axKennwortZaehlen() {
  const k = el("axKw1").value, w = el("axKw2").value;
  let t;
  if (!k) t = `Mindestens ${AX_KW_MIN} Zeichen, z. B. ein Satz aus mehreren Wörtern. Ohne Umlaute und ß (ä → ae). Das Kennwort ` +
    "speichert die App nicht – vergessen heißt: das Archiv ist wertlos.";
  else if (k.length < AX_KW_MIN) t = `${k.length} Zeichen – noch ${AX_KW_MIN - k.length}.`;
  else t = `${k.length} Zeichen.` + (axKennwortFehler(k) ? ` ${axKennwortFehler(k)}` : "") +
    (w && w !== k ? " Die Wiederholung stimmt noch nicht überein." : w === k ? " Wiederholung stimmt." : "");
  el("axKwInfo").textContent = t;
  axKnoepfe();
}

/** Knöpfe nach dem Stand: Eingabe → „Archiv erstellen“; läuft → „Abbrechen“; fertig → „Herunterladen“ / „Verwerfen“. */
function axKnoepfe() {
  const a = ax.auftrag && ax.auftrag.eigener ? ax.auftrag : null;
  const ok = el("axOk"), weg = el("axAbbrechen");
  if (ax.gespeichert) {
    ok.hidden = true;
    weg.hidden = true;
  } else if (a && a.status === "laeuft") {
    ok.hidden = true;
    weg.hidden = false;
    weg.textContent = "Erstellung abbrechen";
  } else if (a && a.status === "fertig") {
    ok.hidden = false;
    ok.textContent = "Herunterladen";
    ok.disabled = ax.laeuft;
    weg.hidden = false;
    weg.textContent = "Verwerfen";
  } else {
    ok.hidden = el("axEingabe").hidden;
    ok.textContent = "Archiv erstellen";
    const k = el("axKw1").value;
    ok.disabled = ax.laeuft || !!(ax.o && ax.o.gesperrt) || !!axKennwortFehler(k) || el("axKw2").value !== k;
    weg.hidden = true;
  }
  weg.disabled = ax.laeuft;
}

/** Passt der Auftrag zu dem, was der Dialog zeigen soll? (fremde Art → nur Hinweis) */
function axPasst(a) {
  if (!a || !ax.o) return false;
  if (a.art !== ax.o.art) return false;
  return a.art !== "export" || a.schuljahr === ax.o.schuljahr;
}

async function axAbfragen(nr) {
  try {
    const r = await senden("GET", "api/jahresablauf/archiv/auftrag");
    if (nr !== ax.nr) return;
    axStandZeigen(r && r.auftrag);
  } catch (f) {
    if (nr !== ax.nr) return;
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("axFehler", t);
  }
}

function axStandZeigen(a) {
  ax.auftrag = a || null;
  clearTimeout(ax.timer);
  if (ax.gespeichert) { axKnoepfe(); return; }
  if (!a) {
    el("axStand").hidden = true;
    if (ax.o && ax.o.nurStand) {
      el("axEingabe").hidden = false;     // nichts mehr da (abgelaufen bzw. schon geholt) → neu erstellen
      el("axSchritt").textContent = "";
      fehlerIn("axFehler", "Es gibt kein Archiv (mehr) in Arbeit – es lässt sich nur einmal herunterladen und liegt höchstens " +
        "30 Minuten bereit. Hier neu erstellen.");
    }
    axKnoepfe();
    return;
  }
  const eigen = a.eigener;
  if (!eigen || !axPasst(a)) {
    // Ein anderer Auftrag belegt den Server (es geht immer nur einer) – nur Hinweis, Eingabe bleibt.
    el("axStand").hidden = false;
    el("axBalken").hidden = true;
    const was = a.art === "export" ? `ein Archiv-Export ${a.schuljahr}` : "ein Jahresarchiv";
    el("axSchritt").textContent = eigen
      ? (a.status === "laeuft" ? `Gerade entsteht ${was} von dir (${a.schritt}).`
        : a.status === "fertig" ? `Dein ${a.art === "export" ? `Archiv-Export ${a.schuljahr}` : "Jahresarchiv"} wartet noch auf den ` +
          `Download (bis ${axUhr(a.gueltigBis)} Uhr) – ein neues ersetzt es.` : "")
      : a.status === "laeuft" ? `Gerade erstellt ${a.akteur} ${was} (seit ${axUhr(a.start)} Uhr) – es geht immer nur eines gleichzeitig.`
        : a.status === "fertig" ? `Das Archiv von ${a.akteur} wartet noch auf den Download (bis ${axUhr(a.gueltigBis)} Uhr).` : "";
    el("axStandInfo").textContent = "";
    if (!el("axSchritt").textContent) el("axStand").hidden = true;
    if (eigen && a.status === "laeuft") ax.timer = setTimeout(() => axAbfragen(ax.nr), AX_TAKT_MS);
    if (ax.o && ax.o.nurStand) el("axEingabe").hidden = false;
    ax.auftrag = null;        // die Knöpfe gelten der Eingabe
    axKnoepfe();
    return;
  }
  el("axEingabe").hidden = a.status === "laeuft" || a.status === "fertig";
  el("axStand").hidden = false;
  el("axBalken").hidden = false;
  el("axBalken").max = Math.max(1, a.gesamt || 1);
  el("axBalken").value = a.status === "fertig" ? el("axBalken").max : Math.min(a.erledigt || 0, el("axBalken").max);
  if (a.status === "laeuft") {
    el("axSchritt").textContent = `Wird erstellt … ${a.schritt}`;
    el("axStandInfo").textContent = `Gestartet ${axUhr(a.start)} Uhr${a.mitSchuelerEintraegen ? ", mit Schülereinträgen" : ""}. ` +
      "Das Fenster darf zu – das Archiv entsteht auf dem Server weiter" +
      (a.art === "export" ? "; die Karte zeigt den Stand." : "; „Jahresarchiv …“ öffnet diesen Stand wieder.");
    ax.timer = setTimeout(() => axAbfragen(ax.nr), AX_TAKT_MS);
  } else if (a.status === "fertig") {
    el("axSchritt").textContent = `Fertig: ${a.dateiname || "Archiv"} (${axMb(a.groesse || 0)}).`;
    el("axStandInfo").textContent = `Liegt bis ${axUhr(a.gueltigBis)} Uhr bereit und lässt sich nur EINMAL herunterladen. ` +
      `Prüfsumme (SHA-256): ${a.pruefsumme || ""}` +
      (a.nichtErstellt ? ` · ${a.nichtErstellt} Datei(en) nicht erstellt – siehe Inhalt.txt.` : "");
  } else {
    el("axBalken").hidden = true;
    el("axSchritt").textContent = a.status === "abgebrochen" ? "Abgebrochen." : "Nicht fertig geworden.";
    el("axStandInfo").textContent = a.fehler || "";
    el("axEingabe").hidden = false;
  }
  axKnoepfe();
}

async function axOk() {
  if (ax.laeuft) return;
  const a = ax.auftrag;
  if (a && a.eigener && a.status === "fertig") { await axHerunterladen(); return; }
  await axStarten();
}

async function axStarten() {
  const o = ax.o;
  if (!o || o.gesperrt) return;
  const k = el("axKw1").value, w = el("axKw2").value;
  const kf = axKennwortFehler(k);
  if (kf) { fehlerIn("axFehler", kf); el("axKw1").focus(); return; }
  if (w !== k) { fehlerIn("axFehler", "Die beiden Kennwörter stimmen nicht überein."); el("axKw2").focus(); return; }
  const body = { schuelerEintraege: el("axSchueler").checked, kennwort: k, kennwortWiederholung: w };
  let url;
  if (o.art === "export") { url = "api/jahresablauf/archiv"; body.schuljahr = o.schuljahr; }
  else { url = "api/berichte/archiv"; body.von = o.von || null; body.bis = o.bis || null; }
  ax.laeuft = true;
  axKnoepfe();
  fehlerIn("axFehler", "");
  const nr = ax.nr;
  try {
    const r = await senden("POST", url, body);
    axKennwortLeeren();          // das Kennwort bleibt nirgends stehen
    if (nr !== ax.nr) return;
    ax.laeuft = false;
    axStandZeigen(r && r.auftrag);
  } catch (f) {
    if (nr !== ax.nr) return;
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("axFehler", t);
    if (f instanceof HttpFehler && f.daten && f.daten.feld === "kennwort2") el("axKw2").focus();
    else if (f instanceof HttpFehler && f.daten && f.daten.feld === "kennwort") el("axKw1").focus();
  } finally {
    if (nr === ax.nr) { ax.laeuft = false; axKnoepfe(); }
  }
}

async function axHerunterladen() {
  const nr = ax.nr;
  ax.laeuft = true;
  axKnoepfe();
  fehlerIn("axFehler", "");
  el("axSchritt").textContent = "Wird heruntergeladen …";
  let resp;
  try {
    try {
      resp = await fetch("api/jahresablauf/archiv/datei", { cache: "no-store", headers: authKopf() });
    } catch {
      throw new Error("Keine Verbindung zum Schulserver – bitte später erneut versuchen.");
    }
    if (!resp.ok) {
      let daten = null;
      try { daten = await resp.json(); } catch { /* kein JSON */ }
      const f = new HttpFehler(resp.status, (daten && daten.fehler) || "");
      f.daten = daten;
      throw f;
    }
    const blob = await resp.blob();
    const name = fzPdfDateiname(resp.headers.get("Content-Disposition"), (ax.auftrag && ax.auftrag.dateiname) || "Archiv.zip");
    const adresse = URL.createObjectURL(blob);
    const link = h("a", { href: adresse, download: name, hidden: true });
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(adresse), 60000);
    if (nr !== ax.nr) return;
    ax.gespeichert = name;
    const export_ = ax.o && ax.o.art === "export";
    el("axBalken").hidden = true;
    el("axSchritt").textContent = `Gespeichert: ${name}`;
    el("axStandInfo").textContent = "Auf dem Server ist die Datei jetzt gelöscht – sie liegt nur noch auf diesem Gerät. " +
      "Nächster Schritt: mit 7-Zip und dem Kennwort öffnen, Inhalt.txt lesen und die Datei sicher aufbewahren (nicht in " +
      "einem Cloud-Ordner)" + (export_ ? ", danach in der Karte „Aufbewahrung bestätigen…“." : ".");
    if (export_ && typeof jaMeldung === "function" && jahresablaufOffen())
      jaMeldung(`Archiv-Export ${ax.o.schuljahr} gespeichert: ${name}. Jetzt mit 7-Zip und dem Kennwort prüfen, aufbewahren und ` +
        "danach „Aufbewahrung bestätigen…“.");
    else if (!export_) toast(`Archiv gespeichert: ${name}`);
  } catch (f) {
    if (nr !== ax.nr) return;
    el("axSchritt").textContent = "";
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("axFehler", t);
    axAbfragen(nr);      // Stand neu (z. B. schon geholt bzw. abgelaufen)
  } finally {
    if (nr === ax.nr) { ax.laeuft = false; axKnoepfe(); }
  }
}

/** Laufenden eigenen Auftrag abbrechen bzw. die fertige Datei verwerfen (mit Rückfrage am Knopf). */
async function axVerwerfen(e) {
  if (ax.laeuft) return;
  const knopf = e.currentTarget;
  if (knopf.dataset.bestaetigt !== "1") {
    knopf.dataset.bestaetigt = "1";
    knopf.dataset.text = knopf.textContent;
    knopf.textContent = ax.auftrag && ax.auftrag.status === "fertig" ? "Wirklich verwerfen? Nochmal tippen" : "Wirklich abbrechen? Nochmal tippen";
    knopf.classList.add("warnung");
    return;
  }
  knopf.dataset.bestaetigt = "";
  knopf.classList.remove("warnung");
  const nr = ax.nr;
  ax.laeuft = true;
  axKnoepfe();
  try {
    await senden("DELETE", "api/jahresablauf/archiv/auftrag");
    if (nr !== ax.nr) return;
    ax.laeuft = false;
    el("axEingabe").hidden = false;
    axStandZeigen(null);
    el("axStand").hidden = false;
    el("axBalken").hidden = true;
    el("axSchritt").textContent = "Abgebrochen bzw. verworfen – die Datei ist gelöscht.";
    el("axStandInfo").textContent = "";
  } catch (f) {
    if (nr !== ax.nr) return;
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("axFehler", t);
  } finally {
    if (nr === ax.nr) { ax.laeuft = false; axKnoepfe(); }
  }
}

// ---------------------------------------------------------------------
//  Aufbewahrung bestätigen (264)
// ---------------------------------------------------------------------

const abNorm = (s) => (s || "").replace(/\s+/g, "").toUpperCase();
const abHex = (puffer) => Array.from(new Uint8Array(puffer), (b) => b.toString(16).padStart(2, "0")).join("").toUpperCase();

/** j = JaArchivJahrDto (aus der Übersicht des Jahresablaufs). */
function abOeffnen(j) {
  if (!j || !j.archivPruefsumme) return;
  ab.jahr = j;
  ab.nr++;
  el("abTitel").textContent = `Aufbewahrung bestätigen – ${j.schuljahr}`;
  el("abWas").textContent = `Archiv-Export ${j.schuljahr}: erstellt ${datumZeit(j.archivAm)}${j.archivVon ? ` von ${j.archivVon}` : ""}` +
    (j.archivGroesse ? `, ${axMb(j.archivGroesse)}` : "") + `, Prüfsumme ${axKurz(j.archivPruefsumme)}.` +
    (j.bestaetigt ? " Schon bestätigt – mit derselben Datei lassen sich Ort und Datum korrigieren." : "");
  el("abDatei").value = "";
  el("abSumme").value = "";
  el("abGeoeffnet").checked = false;
  el("abOrt").value = j.bestaetigt && j.aufbewahrtOrt ? j.aufbewahrtOrt : "";
  el("abBis").value = j.bestaetigt && j.aufbewahrenBis ? j.aufbewahrenBis : "";
  el("abBis").min = plusTage(heuteIso(), 1);
  el("abVergleich").textContent = "";
  el("abVergleich").className = "ab-vergleich";
  fehlerIn("abFehler", "");
  abPruefen();
  dialogAuf(el("abDialog"));
  setTimeout(() => { try { el("abDatei").focus(); } catch { /* egal */ } }, 0);
}

function abZu() {
  ab.nr++;
  ab.jahr = null;
  dialogZu(el("abDialog"));
}

async function abDateiGewaehlt() {
  const datei = el("abDatei").files && el("abDatei").files[0];
  if (!datei || !ab.jahr) return;
  const nr = ++ab.nr;
  el("abVergleich").className = "ab-vergleich";
  if (!window.crypto || !crypto.subtle) {
    el("abVergleich").textContent = "Dieser Browser kann hier keine Prüfsumme rechnen – bitte mit PowerShell Get-FileHash " +
      "rechnen und eintragen.";
    return;
  }
  el("abVergleich").textContent = `Rechne die Prüfsumme von ${datei.name} (${axMb(datei.size)}) …`;
  try {
    const summe = abHex(await crypto.subtle.digest("SHA-256", await datei.arrayBuffer()));
    if (nr !== ab.nr) return;
    el("abSumme").value = summe;
    abVergleichen();
  } catch {
    if (nr !== ab.nr) return;
    el("abVergleich").textContent = "Die Datei ließ sich im Browser nicht lesen (zu groß?) – bitte mit PowerShell Get-FileHash " +
      "rechnen und eintragen.";
  }
}

/** Prüfsumme gegen den Archiv-Export vergleichen – rot, wenn sie abweicht (dann kein Bestätigen). */
function abVergleichen() {
  const s = abNorm(el("abSumme").value);
  const soll = ab.jahr ? ab.jahr.archivPruefsumme : "";
  const v = el("abVergleich");
  if (!s) { v.textContent = ""; v.className = "ab-vergleich"; }
  else if (!/^[0-9A-F]{64}$/.test(s)) {
    v.textContent = "Keine SHA-256-Prüfsumme (64 Zeichen 0–9 und A–F).";
    v.className = "ab-vergleich falsch";
  } else if (s === soll) {
    v.textContent = "✓ Die Prüfsumme stimmt mit dem Archiv-Export überein.";
    v.className = "ab-vergleich richtig";
  } else {
    v.textContent = `✗ Die Prüfsumme passt nicht (erwartet ${axKurz(soll)}) – eine andere oder unvollständige Datei.`;
    v.className = "ab-vergleich falsch";
  }
  abPruefen();
}

function abPruefen() {
  const j = ab.jahr;
  const ok = !!j && abNorm(el("abSumme").value) === j.archivPruefsumme && el("abGeoeffnet").checked
    && el("abOrt").value.trim().length >= 3 && !!el("abBis").value;
  el("abOk").disabled = ab.laeuft || !ok;
}

async function abBestaetigen() {
  const j = ab.jahr;
  if (!j || ab.laeuft) return;
  ab.laeuft = true;
  abPruefen();
  fehlerIn("abFehler", "");
  const nr = ab.nr;
  let r;
  try {
    r = await senden("POST", "api/jahresablauf/archiv/bestaetigen", {
      schuljahr: j.schuljahr,
      pruefsumme: abNorm(el("abSumme").value),
      geoeffnet: el("abGeoeffnet").checked,
      ort: el("abOrt").value.trim(),
      aufbewahrenBis: el("abBis").value || null,
    });
  } catch (f) {
    ab.laeuft = false;
    if (nr === ab.nr && el("abDialog").open) {
      const t = aktionsFehler(f);
      if (t !== null) fehlerIn("abFehler", t);
      abPruefen();
    }
    return;
  }
  // Befund C: gespeichert ist gespeichert – Meldung und Karte immer, auch wenn der Dialog inzwischen zu ist.
  ab.laeuft = false;
  if (el("abDialog").open) abZu();
  jaNachAktion(r, `Bestätigt: Der Archiv-Export ${j.schuljahr} ist geprüft und aufbewahrt.`);
}

// ---------------------------------------------------------------------
//  P5c-2: Schuljahr aus der Datenbank löschen (266–270)
// ---------------------------------------------------------------------

/** Knopf „Schuljahr … löschen…“: zuerst die Zahlen der API holen (409), dann der Dialog. */
async function ljOeffnen(a) {
  if (!state.anmeldung || state.anmeldung.rolle !== "Admin" || lj.laeuft) return;
  jaMeldungZu();
  const nr = ++lj.nr;
  el("jaInfo").textContent = "Zähle, was gelöscht würde …";
  try {
    const d = await senden("POST", "api/jahresablauf/loeschen", { schuljahr: a.schuljahr });
    // Ohne Rückfrage antwortet die API nicht mit 200 – sicherheitshalber den Stand zeigen.
    if (nr !== lj.nr) return;
    el("jaInfo").textContent = "";
    if (d && d.stand && jahresablaufOffen()) jaRender(d.stand);
  } catch (f) {
    if (nr !== lj.nr) return;
    el("jaInfo").textContent = "";
    if (f instanceof HttpFehler && f.status === 409 && f.daten && f.daten.loeschen) {
      ljZeigen(a, f.daten);
      el("ljJahr").value = "";
      el("ljPasswort").value = "";
      ljPruefen();
      dialogAuf(el("ljDialog"));
      setTimeout(() => { try { el("ljJahr").focus(); } catch { /* egal */ } }, 0);
    } else {
      const t = aktionsFehler(f);
      if (t !== null) jaMeldung(t, "hinweis");
    }
  }
}

/** Rückfrage r (JaLoeschRueckfrageDto) im Dialog zeigen – auch nach „andere Zahlen“ (r.neu). */
function ljZeigen(a, r) {
  lj.a = a;
  lj.r = r;
  el("ljTitel").textContent = r.titel || `Schuljahr ${r.schuljahr} löschen?`;
  el("ljArchiv").textContent = r.archiv || "";
  el("ljNeu").textContent = r.neu ? r.fehler || "Inzwischen sind es andere Zahlen." : "";
  el("ljNeu").hidden = !r.neu;
  const zahlen = r.zahlen || [];
  el("ljZahlen").replaceChildren(...(zahlen.length
    ? zahlen.map((z) => h("li", null, z.text))
    : [h("li", null, "Keine Daten mehr – es bleibt nur der Vermerk „gelöscht“.")]));
  el("ljHinweise").replaceChildren(...(r.hinweise || []).map((t) => h("li", null, t)));
  el("ljJahrTitel").textContent = `Zum Bestätigen „${r.schuljahr}“ eintippen`;
  el("ljJahr").placeholder = r.schuljahr;
  el("ljOk").textContent = r.knopf || `Schuljahr ${r.schuljahr} endgültig löschen`;
  el("ljStand").textContent = "";
  fehlerIn("ljFehler", "");
}

function ljPruefen() {
  const r = lj.r;
  const ok = !!r && el("ljJahr").value.trim() === r.schuljahr && el("ljPasswort").value.length > 0;
  el("ljOk").disabled = lj.laeuft || !ok;
  el("ljAbbrechen").disabled = lj.laeuft;
}

/** Dialog schließen (ein laufendes Löschen geht auf dem Server weiter; die Antwort lädt dann die Karte). */
function ljZu() {
  lj.nr++;
  el("ljPasswort").value = "";
  dialogZu(el("ljDialog"));
}

async function ljLoeschen() {
  const r = lj.r, a = lj.a;
  if (!r || !a || lj.laeuft) return;
  const jahr = el("ljJahr").value.trim();
  const pw = el("ljPasswort").value;
  if (jahr !== r.schuljahr || !pw) { ljPruefen(); return; }
  lj.laeuft = true;
  ljPruefen();
  fehlerIn("ljFehler", "");
  el("ljNeu").hidden = true;
  el("ljStand").textContent = "Wird gelöscht … Das kann einige Minuten dauern – bitte warten.";
  const nr = lj.nr;
  let d;
  try {
    d = await senden("POST", "api/jahresablauf/loeschen",
      { schuljahr: r.schuljahr, bestaetigung: jahr, passwort: pw, pruefsumme: r.pruefsumme }, LJ_ZEIT_MS);
  } catch (f) {
    lj.laeuft = false;
    el("ljPasswort").value = "";
    const offen = nr === lj.nr && el("ljDialog").open;
    const status = f instanceof HttpFehler ? f.status : 0;
    if (status === 409 && f.daten && f.daten.loeschen) {
      // Andere Zahlen: zeigen, das eingetippte Schuljahr bleibt, das Passwort neu.
      if (offen) { ljZeigen(a, f.daten); ljPruefen(); el("ljPasswort").focus(); }
      else if (jahresablaufOffen()) jaLaden();
      return;
    }
    if (status === 400 || status === 409 || status === 429 || status === 500) {
      // Der Server hat geantwortet – gelöscht ist nichts (500: Transaktion zurückgesetzt).
      if (offen) {
        el("ljStand").textContent = "";
        const t = aktionsFehler(f);
        if (t !== null) fehlerIn("ljFehler", t);
        ljPruefen();
        try { (f.daten && f.daten.feld === "bestaetigung" ? el("ljJahr") : el("ljPasswort")).focus(); } catch { /* egal */ }
      } else if (jahresablaufOffen()) jaLaden();
      return;
    }
    // Keine (vollständige) Antwort – ob gelöscht wurde, zeigt der neue Stand der Karte.
    if (offen) ljZu();
    const t = aktionsFehler(f);
    if (t !== null && jahresablaufOffen())
      jaMeldung(`${t} Ob ${r.schuljahr} gelöscht wurde, zeigt die Karte „Archiv und Fristen“ (wird neu geladen).`, "hinweis");
    if (jahresablaufOffen()) jaLaden();
    return;
  }
  lj.laeuft = false;
  el("ljPasswort").value = "";
  if (el("ljDialog").open) ljZu();
  jaNachAktion(d, `Schuljahr ${r.schuljahr} gelöscht.`);   // nur, solange der Jahresablauf offen ist
}

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function archivInit() {
  el("axOk").addEventListener("click", axOk);
  el("axAbbrechen").addEventListener("click", axVerwerfen);
  el("axZu").addEventListener("click", axZu);
  el("axKwZeigen").addEventListener("click", axKennwortZeigen);
  el("axKw1").addEventListener("input", axKennwortZaehlen);
  el("axKw2").addEventListener("input", axKennwortZaehlen);
  el("axDialog").addEventListener("close", () => {
    ax.nr++;
    clearTimeout(ax.timer);
    axKennwortLeeren();
    const danach = ax.o && ax.o.beimSchliessen;
    ax.o = null;
    ax.auftrag = null;
    if (typeof danach === "function") danach();
  });
  hintergrundSchliesst(el("axDialog"), () => { if (!ax.laeuft) axZu(); });

  el("abDatei").addEventListener("change", abDateiGewaehlt);
  el("abSumme").addEventListener("input", abVergleichen);
  el("abGeoeffnet").addEventListener("change", abPruefen);
  el("abOrt").addEventListener("input", abPruefen);
  el("abBis").addEventListener("input", abPruefen);
  el("abBis").addEventListener("change", abPruefen);
  el("abOk").addEventListener("click", abBestaetigen);
  el("abAbbrechen").addEventListener("click", () => { if (!ab.laeuft) abZu(); });
  el("abDialog").addEventListener("close", () => { ab.nr++; ab.jahr = null; el("abDatei").value = ""; });
  el("abDialog").addEventListener("cancel", (e) => { if (ab.laeuft) e.preventDefault(); });   // P5c-2 (Befund C)
  hintergrundSchliesst(el("abDialog"), () => { if (!ab.laeuft) abZu(); });

  // P5c-2: Schuljahr löschen
  el("ljJahr").addEventListener("input", ljPruefen);
  el("ljPasswort").addEventListener("input", ljPruefen);
  el("ljOk").addEventListener("click", ljLoeschen);
  el("ljAbbrechen").addEventListener("click", () => { if (!lj.laeuft) ljZu(); });
  el("ljDialog").addEventListener("cancel", (e) => { if (lj.laeuft) e.preventDefault(); });
  el("ljDialog").addEventListener("close", () => {
    lj.nr++;
    el("ljPasswort").value = "";
    el("ljJahr").value = "";
    lj.r = null;
    lj.a = null;
  });
  hintergrundSchliesst(el("ljDialog"), () => { if (!lj.laeuft) ljZu(); });
}
