"use strict";
// =====================================================================
//  Stundenplan-PWA (Phase 2, Schritt 3): Wochenansicht für Lehrkräfte,
//  Klassen und Kurse. Daten: /api/woche/... (Grundplan × Kalender ×
//  veröffentlichte Vertretungen). Reines JavaScript ohne Build-Schritt.
//
//  - Handy hochkant: Tagesreiter + Liste; breit bzw. quer: Wochenraster.
//  - Woche vor/zurück, „Heute", Datum wählen; am Wochenende startet die
//    App mit der kommenden Woche.
//  - Zuletzt geladene Wochen werden im Browser gespeichert und sind offline
//    lesbar (Hinweis „Offline" im Fuß).
//  - Lädt von selbst nach: beim Zurückwechseln ins Fenster bzw. in die App
//    und alle 2 Minuten, solange die Seite sichtbar ist.
//  - Auswahl und Woche stehen in der Adresse (#klasse/05A/2026-11-02),
//    Links lassen sich also teilen bzw. als Lesezeichen speichern.
// =====================================================================

const MODI = {
  lehrer: { name: "Lehrkraft", wahl: "Lehrkraft wählen" },
  klasse: { name: "Klasse", wahl: "Klasse oder Stufe wählen" },
  gruppe: { name: "Kurs", wahl: "Kurs oder Gruppe wählen" },
};

const WOCHENTAGE = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];
const MONATE = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli",
  "August", "September", "Oktober", "November", "Dezember"];
const MONATE_KURZ = ["Jan.", "Feb.", "März", "Apr.", "Mai", "Juni", "Juli",
  "Aug.", "Sept.", "Okt.", "Nov.", "Dez."];

// Status einer Stunde (aus der API) -> Abzeichen, Farbe, gestrichen?
const STATUS = {
  Vertreten:      { text: "Vertretung",      art: "vertretung" },
  Entfall:        { text: "entfällt",        art: "entfall",   gestrichen: true },
  VerlegtWeg:     { text: "verlegt",         art: "verlegung", gestrichen: true },
  VerlegtHin:     { text: "verlegte Stunde", art: "verlegung" },
  Betreuung:      { text: "Betreuung",       art: "betreuung" },
  EVA:            { text: "EVA",             art: "betreuung" },
  Zusammenlegung: { text: "Zusammenlegung",  art: "betreuung" },
  Raumvertretung: { text: "Raumänderung",    art: "raumwechsel" },
  Frei:           { text: "frei",            art: "frei",      gestrichen: true },
};

const sortierer = new Intl.Collator("de", { numeric: true, sensitivity: "base" });
const el = (id) => document.getElementById(id);

const state = {
  modus: "klasse",
  werte: { lehrer: "", klasse: "", gruppe: "" }, // zuletzt gewählter Wert je Ansicht
  montag: "",            // ISO-Datum des Montags der angezeigten Woche
  tagIndex: 0,           // gewählter Tag (Handy-Ansicht)
  zielDatum: null,       // nach dem Laden diesen Tag auswählen
  springeZuHeute: true,  // nach dem Laden „heute" auswählen (Start / Heute-Knopf)
  nurAenderungen: false,
  auswahl: null,
  plan: null,
  fehler: null,
  geladenUm: 0,
  offlineStand: null,    // Zeitstempel, wenn der Plan aus dem Speicher stammt
};

// ---------------------------------------------------------------------
//  Hilfen: Elemente bauen (ohne innerHTML), Datum, Speicher
// ---------------------------------------------------------------------

function h(tag, props, ...kinder) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === "class") e.className = v;
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v === true ? "" : String(v));
  }
  for (const kind of kinder.flat(Infinity)) {
    if (kind === null || kind === undefined || kind === false || kind === "") continue;
    e.append(kind instanceof Node ? kind : document.createTextNode(String(kind)));
  }
  return e;
}

const pad = (n) => String(n).padStart(2, "0");
const isoAus = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const ausIso = (s) => { const [y, m, d] = s.split("-").map(Number); return new Date(y, m - 1, d); };
const plusTage = (iso, n) => { const d = ausIso(iso); d.setDate(d.getDate() + n); return isoAus(d); };
const montagVon = (iso) => { const d = ausIso(iso); d.setDate(d.getDate() - ((d.getDay() + 6) % 7)); return isoAus(d); };
const heuteIso = () => isoAus(new Date());
const istIso = (s) => /^\d{4}-\d{2}-\d{2}$/.test(s || "");

/** Montag der Woche, die beim Öffnen gezeigt wird – am Wochenende schon die nächste. */
function startMontag() {
  const d = new Date();
  if (d.getDay() === 6) d.setDate(d.getDate() + 2);
  else if (d.getDay() === 0) d.setDate(d.getDate() + 1);
  return montagVon(isoAus(d));
}

function kalenderwoche(iso) {
  const d = ausIso(iso);
  const t = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
  const wt = t.getUTCDay() || 7;
  t.setUTCDate(t.getUTCDate() + 4 - wt);
  const jahrStart = new Date(Date.UTC(t.getUTCFullYear(), 0, 1));
  return Math.ceil(((t - jahrStart) / 86400000 + 1) / 7);
}

/** Mo–Fr der Woche, ohne Jahr (das steht in der KW-Zeile): „2.–6. November". */
function zeitraumText(montagIso) {
  const a = ausIso(montagIso);
  const b = ausIso(plusTage(montagIso, 4));
  if (a.getMonth() === b.getMonth())
    return `${a.getDate()}.–${b.getDate()}. ${MONATE[a.getMonth()]}`;
  return `${a.getDate()}. ${MONATE_KURZ[a.getMonth()]} – ${b.getDate()}. ${MONATE_KURZ[b.getMonth()]}`;
}

/** „KW 45, 2026" – über den Jahreswechsel „KW 53, 2026/27". */
function kwText(montagIso) {
  const a = ausIso(montagIso);
  const b = ausIso(plusTage(montagIso, 4));
  const jahr = a.getFullYear() === b.getFullYear()
    ? String(a.getFullYear())
    : `${a.getFullYear()}/${String(b.getFullYear()).slice(2)}`;
  return `KW ${kalenderwoche(montagIso)}, ${jahr}`;
}

const tagLang = (iso) => { const d = ausIso(iso); return `${WOCHENTAGE[d.getDay()]}, ${d.getDate()}. ${MONATE[d.getMonth()]}`; };
const uhrzeit = (t) => new Date(t).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
const zeitpunkt = (t) => new Date(t).toLocaleString("de-DE",
  { weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
const gleich = (a, b) => (a || "").trim().toUpperCase() === (b || "").trim().toUpperCase();

// Browser-Speicher nur für Bequemlichkeit (Auswahl, offline lesbare Wochen).
// Kann leer oder gesperrt sein – die App funktioniert dann trotzdem.
const speicher = {
  lesen(k, ersatz) {
    try { const v = localStorage.getItem(k); return v === null ? ersatz : JSON.parse(v); }
    catch { return ersatz; }
  },
  schreiben(k, v) {
    try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* voll oder gesperrt */ }
  },
  entfernen(k) {
    try { localStorage.removeItem(k); } catch { /* egal */ }
  },
};

const CACHE_MAX = 30;

function cacheSchreiben(url, daten) {
  speicher.schreiben("antwort:" + url, { t: Date.now(), d: daten });
  const index = speicher.lesen("antwort-index", []).filter((u) => u !== url);
  index.push(url);
  while (index.length > CACHE_MAX) speicher.entfernen("antwort:" + index.shift());
  speicher.schreiben("antwort-index", index);
}

/** Holt JSON vom Server; ohne Verbindung die zuletzt gespeicherte Antwort. */
async function holen(url) {
  const abbruch = new AbortController();
  const timer = setTimeout(() => abbruch.abort(), 12000);
  try {
    const resp = await fetch(url, { cache: "no-store", signal: abbruch.signal });
    if (!resp.ok) throw new Error("Server antwortet mit " + resp.status);
    const daten = await resp.json();
    cacheSchreiben(url, daten);
    return { daten, offlineStand: null };
  } catch (fehler) {
    const alt = speicher.lesen("antwort:" + url, null);
    if (alt) return { daten: alt.d, offlineStand: alt.t };
    throw fehler;
  } finally {
    clearTimeout(timer);
  }
}

// ---------------------------------------------------------------------
//  Zustand: Adresse (#modus/wert/datum) und gespeicherte Auswahl
// ---------------------------------------------------------------------

function hashLesen() {
  const teile = location.hash.replace(/^#/, "").split("/").map((t) => {
    try { return decodeURIComponent(t); } catch { return ""; }
  });
  if (!MODI[teile[0]]) return null;
  return { modus: teile[0], wert: teile[1] || "", datum: istIso(teile[2]) ? teile[2] : null };
}

function hashSchreiben() {
  const teile = [state.modus, state.werte[state.modus] || ""];
  if (state.montag !== startMontag()) teile.push(state.montag);
  const neu = "#" + teile.map(encodeURIComponent).join("/");
  if (location.hash !== neu) history.replaceState(null, "", neu);
}

function zustandSpeichern() {
  speicher.schreiben("zustand", { modus: state.modus, werte: state.werte, nurAenderungen: state.nurAenderungen });
}

function zustandLaden() {
  const z = speicher.lesen("zustand", null);
  if (z) {
    if (MODI[z.modus]) state.modus = z.modus;
    Object.assign(state.werte, z.werte || {});
    state.nurAenderungen = !!z.nurAenderungen;
  } else {
    // Übernahme aus der bisherigen Lehrer-App (dort gespeichertes Kürzel).
    let altKuerzel = null;
    try { altKuerzel = localStorage.getItem("kuerzel"); } catch { /* egal */ }
    if (altKuerzel) { state.modus = "lehrer"; state.werte.lehrer = altKuerzel; }
  }
  const adr = hashLesen();
  if (adr) {
    state.modus = adr.modus;
    if (adr.wert) state.werte[adr.modus] = adr.wert;
  }
  state.montag = adr && adr.datum ? montagVon(adr.datum) : startMontag();
  if (adr && adr.datum) { state.zielDatum = adr.datum; state.springeZuHeute = false; }
}

// ---------------------------------------------------------------------
//  Laden
// ---------------------------------------------------------------------

let ladeNr = 0;
let laedtGerade = false; // eine Woche wird gerade geladen (kein stilles Nachladen dazwischen)

async function ladeAuswahl() {
  try {
    const { daten } = await holen(`api/woche/auswahl?datum=${state.montag}`);
    state.auswahl = daten;
  } catch {
    /* ohne Verbindung und ohne Speicher: Auswahl bleibt leer */
  }
  fuelleAuswahl();
  renderKopf();
}

/**
 * Lädt die angezeigte Woche. still = automatisches Nachladen im Hintergrund:
 * kein Drehsymbol, keine Vorab-Ladung der Folgewoche, und die Anzeige wird nur
 * neu aufgebaut, wenn sich am Plan etwas geändert hat (Fokus und Lage bleiben).
 */
async function ladeWoche({ still = false } = {}) {
  hashSchreiben();
  zustandSpeichern();
  const wert = state.werte[state.modus];
  if (!wert) {
    state.plan = null;
    state.fehler = null;
    render();
    return;
  }
  const url = `api/woche/${state.modus}/${encodeURIComponent(wert)}?datum=${state.montag}`;
  const nr = ++ladeNr;
  laedtGerade = true;
  if (!still) {
    document.body.classList.add("laedt");
    el("inhalt").setAttribute("aria-busy", "true");
  }
  try {
    const { daten, offlineStand } = await holen(url);
    if (nr !== ladeNr) return; // inzwischen etwas anderes angefordert
    const unveraendert = still && !state.fehler && state.plan &&
      JSON.stringify(daten) === JSON.stringify(state.plan);
    state.plan = daten;
    state.fehler = null;
    state.offlineStand = offlineStand;
    state.geladenUm = Date.now();
    if (unveraendert) {
      renderFuss(); // nur „Stand" bzw. „Offline" aktualisieren
    } else {
      waehleTag();
      render();
    }
    if (!offlineStand && !still) vorladen(plusTage(state.montag, 7));
  } catch {
    if (nr !== ladeNr) return;
    state.geladenUm = Date.now(); // nicht im Sekundentakt erneut versuchen
    if (still && state.plan) return; // angezeigten Plan stehen lassen
    state.plan = null;
    state.fehler = "Der Plan konnte nicht geladen werden. Prüfe die Verbindung zum Schulserver und tippe oben rechts auf Aktualisieren.";
    render();
  } finally {
    if (nr === ladeNr) {
      laedtGerade = false;
      document.body.classList.remove("laedt");
      el("inhalt").removeAttribute("aria-busy");
    }
  }
}

/** Nächste Woche still mitladen – schnelleres Blättern und offline verfügbar. */
function vorladen(montagIso) {
  const wert = state.werte[state.modus];
  if (!wert) return;
  holen(`api/woche/${state.modus}/${encodeURIComponent(wert)}?datum=${montagIso}`).catch(() => {});
}

function waehleTag() {
  const tage = (state.plan && state.plan.tage) || [];
  if (tage.length === 0) { state.tagIndex = 0; return; }
  const ziel = state.zielDatum || (state.springeZuHeute ? heuteIso() : null);
  const i = ziel ? tage.findIndex((t) => t.datum === ziel) : -1;
  if (i >= 0) state.tagIndex = i;
  else if (state.springeZuHeute) state.tagIndex = 0;
  state.tagIndex = Math.min(Math.max(state.tagIndex, 0), tage.length - 1);
  state.zielDatum = null;
  state.springeZuHeute = false;
}

// ---------------------------------------------------------------------
//  Auswahlliste
// ---------------------------------------------------------------------

function fuelleAuswahl() {
  const sel = el("wert");
  const a = state.auswahl;
  const wert = state.werte[state.modus] || "";
  el("wahlText").textContent = MODI[state.modus].wahl;
  sel.replaceChildren();

  const platzhalter = a && !a.grundplan
    ? "Noch kein Stundenplan veröffentlicht"
    : MODI[state.modus].wahl;
  sel.append(h("option", { value: "", disabled: true }, platzhalter));

  const werte = new Set();
  const option = (value, text) => { werte.add(value.toUpperCase()); return h("option", { value }, text); };
  const sortiert = (liste, f = (x) => x) => [...liste].sort((x, y) => sortierer.compare(f(x), f(y)));

  if (a && a.grundplan) {
    if (state.modus === "lehrer") {
      sel.append(...sortiert(a.lehrer).map((l) => option(l, l)));
    } else if (state.modus === "klasse") {
      sel.append(h("optgroup", { label: "Klassen" }, sortiert(a.klassen).map((k) => option(k, k))));
      if (a.stufen.length)
        sel.append(h("optgroup", { label: "Oberstufe (ganze Stufe)" }, sortiert(a.stufen).map((s) => option(s, s))));
    } else {
      const nachStufe = new Map();
      for (const k of a.kurse) {
        if (!nachStufe.has(k.stufe)) nachStufe.set(k.stufe, []);
        nachStufe.get(k.stufe).push(k);
      }
      for (const stufe of sortiert(nachStufe.keys())) {
        sel.append(h("optgroup", { label: stufe || "Kurse" },
          sortiert(nachStufe.get(stufe), (k) => k.fach).map((k) =>
            option(k.schluessel, k.lehrer ? `${k.fach} (${k.lehrer})` : k.fach))));
      }
      if (a.gruppen.length)
        sel.append(h("optgroup", { label: "AGs und weitere Gruppen" },
          sortiert(a.gruppen, (g) => g.schluessel).map((g) =>
            option(g.schluessel, gleich(g.fach, g.schluessel) ? g.schluessel : `${g.schluessel} (${g.fach})`))));
    }
  }

  // Gespeicherter Wert, der (noch) nicht in der Liste steht, bleibt wählbar.
  if (wert && !werte.has(wert.toUpperCase()))
    sel.insertBefore(h("option", { value: wert }, wert), sel.children[1] || null);

  // Auswahl setzen; Groß-/Kleinschreibung an die Liste angleichen („05a" -> „05A").
  const passend = [...sel.options].find((o) => o.value && gleich(o.value, wert));
  sel.value = passend ? passend.value : "";
  if (passend && passend.value !== wert) state.werte[state.modus] = passend.value;
}

/** Lesbarer Name des Ziels für die Kopfzeile. */
function zielName() {
  const wert = state.werte[state.modus];
  if (!wert) return "Stundenplan";
  if (state.modus === "lehrer") return `Lehrkraft ${wert}`;
  if (state.modus === "klasse")
    return /^(EF|Q1|Q2)$/i.test(wert) ? `Stufe ${wert.toUpperCase()}` : `Klasse ${wert}`;
  const a = state.auswahl;
  const kurs = a && [...a.kurse, ...a.gruppen].find((k) => gleich(k.schluessel, wert));
  if (kurs && a.kurse.includes(kurs)) return `${kurs.fach} (${kurs.stufe})`;
  if (kurs) return gleich(kurs.fach, kurs.schluessel) ? kurs.schluessel : `${kurs.schluessel} (${kurs.fach})`;
  return wert;
}

// ---------------------------------------------------------------------
//  Darstellung einer Stunde
// ---------------------------------------------------------------------

/** Korrektur im Stil des Rotstifts: alter Wert gestrichen, neuer daneben. */
function korrektur(alt, neu) {
  if (alt && neu && !gleich(alt, neu)) return [h("s", { class: "alt" }, alt), " ", h("span", { class: "neu" }, neu)];
  return neu || alt || "";
}

function statusVon(s) {
  if (!s.geaendert) return null;
  const st = STATUS[s.status] || { text: "geändert", art: "sonst" };
  if (s.status === "Frei" && s.info) return { ...st, text: s.info };
  return st;
}

function fachText(s) {
  if (s.art === "VBer") return "Bereitschaft";
  return s.fach || "–";
}

function gruppenText(s) {
  const klassen = (s.klassen || []).filter(Boolean);
  return klassen.length ? klassen.join(", ") : (s.lerngruppen || []).join(", ");
}

function eintrag(s) {
  const st = statusVon(s);
  const ich = state.werte.lehrer;
  const lehrerSicht = state.modus === "lehrer";
  const dienst = s.art && s.art !== "Unterricht";
  // Lehrersicht: die eigene Stunde hält jemand anderes.
  const fuerMichFrei = lehrerSicht && s.originalLehrer && gleich(s.originalLehrer, ich) && !gleich(s.lehrer, ich);

  let badge = st ? st.text : null;
  if (fuerMichFrei && s.status === "Vertreten") badge = "wird vertreten";

  const klassen = ["eintrag"];
  if (st) klassen.push("geaendert", st.art);
  if (st && st.gestrichen) klassen.push("gestrichen");
  if (dienst) klassen.push("dienst");
  if (fuerMichFrei) klassen.push("fuer-mich-frei");

  // Zweite Zeile: Lehrersicht -> Klassen/Kurs (+ Lehrerwechsel), sonst die Lehrkraft.
  let wer;
  if (lehrerSicht) {
    const wechsel = s.originalLehrer ? korrektur(s.originalLehrer, s.lehrer) : null;
    wer = [gruppenText(s), wechsel ? " " : "", wechsel];
  } else {
    wer = korrektur(s.originalLehrer, s.lehrer);
  }

  return h("div", { class: klassen.join(" ") },
    h("div", { class: "e-oben" },
      h("span", { class: "fach" }, fachText(s)),
      h("span", { class: "raum" }, korrektur(s.originalRaum, s.raum))),
    h("div", { class: "e-unten" },
      h("span", { class: "wer" }, wer),
      badge ? h("span", { class: "badge" }, badge) : null));
}

function sichtbareStunden(tag) {
  const stunden = tag.stunden || [];
  return state.nurAenderungen ? stunden.filter((s) => s.geaendert) : stunden;
}

function tagHinweis(tag) {
  switch (tag.status) {
    case "Frei": return tag.hinweis || "Unterrichtsfrei";
    case "AusserhalbSchuljahr": return "Außerhalb des Schuljahres";
    case "KeinGrundplan": return "Noch kein Stundenplan veröffentlicht";
    default: return tag.hinweis || "";
  }
}

const hatAenderung = (tag) => (tag.stunden || []).some((s) => s.geaendert);

/** „Nur Änderungen" ist an, aber die ganze Woche hat keine. */
const wocheOhneAenderungen = () => state.nurAenderungen && !!state.plan &&
  !state.plan.tage.some((t) => t.status === "Unterricht" && hatAenderung(t));

// ---------------------------------------------------------------------
//  Rendern
// ---------------------------------------------------------------------

function render() {
  renderKopf();
  renderWoche();
  renderTage();
  renderListe();
  renderRaster();
  renderMeldung();
  renderFuss();
}

function renderKopf() {
  const name = zielName();
  el("titel").textContent = name;
  document.title = name === "Stundenplan" ? "Stundenplan" : `${name} – Stundenplan`;
  for (const b of document.querySelectorAll(".modi button"))
    b.setAttribute("aria-pressed", String(b.dataset.modus === state.modus));
  el("nurAenderungen").checked = state.nurAenderungen;
}

function renderWoche() {
  el("zeitraum").textContent = zeitraumText(state.montag);
  el("kw").textContent = kwText(state.montag);
  const tage = (state.plan && state.plan.tage) || [];
  el("datum").value = tage[state.tagIndex] ? tage[state.tagIndex].datum : state.montag;
}

function renderTage() {
  const box = el("tage");
  const tage = (state.plan && state.plan.tage) || [];
  box.replaceChildren(...tage.map((tag, i) => {
    const d = ausIso(tag.datum);
    const klassen = ["tab"];
    if (tag.datum === heuteIso()) klassen.push("heute");
    if (tag.status !== "Unterricht") klassen.push("frei");
    if (hatAenderung(tag)) klassen.push("hat-aenderung");
    const gewaehlt = i === state.tagIndex;
    return h("button", {
      type: "button",
      class: klassen.join(" "),
      role: "tab",
      id: "tab-" + i,
      "aria-selected": String(gewaehlt),
      "aria-controls": "liste",
      "aria-label": tagLang(tag.datum) + (hatAenderung(tag) ? ", mit Änderungen" : ""),
      tabindex: gewaehlt ? "0" : "-1",
      onclick: () => { state.tagIndex = i; render(); },
    },
    h("span", { class: "wt" }, tag.wTag),
    h("span", { class: "dt" }, d.getDate()),
    tag.status === "Frei" ? h("span", { class: "frei-text" }, "frei") : null);
  }));
}

function renderListe() {
  const liste = el("liste");
  const tage = (state.plan && state.plan.tage) || [];
  const tag = tage[state.tagIndex];
  el("tagTitel").textContent = tag ? tagLang(tag.datum) : "";
  if (!tag) { liste.replaceChildren(); return; }

  if (tag.status !== "Unterricht") {
    liste.replaceChildren(h("div", { class: "tagfrei" }, tagHinweis(tag)));
    return;
  }
  const stunden = sichtbareStunden(tag);
  if (wocheOhneAenderungen()) { liste.replaceChildren(); return; } // Hinweis steht oben
  if (stunden.length === 0) {
    liste.replaceChildren(h("div", { class: "leer" },
      state.nurAenderungen ? "Keine Änderungen an diesem Tag." : "Kein Unterricht an diesem Tag."));
    return;
  }
  const nachStunde = new Map();
  for (const s of stunden) {
    if (!nachStunde.has(s.stunde)) nachStunde.set(s.stunde, []);
    nachStunde.get(s.stunde).push(s);
  }
  const nummern = [...nachStunde.keys()].sort((a, b) => a - b);
  // Volle Ansicht: jede Stunde ab der 1. bis zur letzten des Tages, damit freie
  // Stunden (Lücken) sichtbar sind – wie im Wochenraster. „Nur Änderungen": kompakt.
  let reihe = nummern;
  if (!state.nurAenderungen) {
    reihe = [];
    for (let nr = Math.min(1, nummern[0]); nr <= nummern[nummern.length - 1]; nr++) reihe.push(nr);
  }
  liste.replaceChildren(...reihe.map((nr) => nachStunde.has(nr)
    ? h("div", { class: "std-zeile" },
        h("div", { class: "std-nr", "aria-label": `${nr}. Stunde` }, nr),
        h("div", { class: "std-eintraege" }, nachStunde.get(nr).map(eintrag)))
    : h("div", { class: "std-zeile frei-zeile" },
        h("div", { class: "std-nr", "aria-label": `${nr}. Stunde` }, nr),
        h("div", { class: "keine" },
          h("span", { "aria-hidden": "true" }, "–"),
          h("span", { class: "vh" }, "kein Unterricht")))));
}

function renderRaster() {
  const huelle = el("raster");
  const tage = (state.plan && state.plan.tage) || [];
  if (tage.length === 0) { huelle.replaceChildren(); return; }

  let maxStunde = 6;
  for (const t of tage) for (const s of t.stunden || []) maxStunde = Math.max(maxStunde, s.stunde);

  const kopf = h("tr", null, h("th", { class: "ecke", scope: "col" }, h("span", { class: "vh" }, "Stunde")),
    tage.map((t) => {
      const d = ausIso(t.datum);
      const klassen = [];
      if (t.datum === heuteIso()) klassen.push("heute");
      if (hatAenderung(t)) klassen.push("hat-aenderung");
      return h("th", { scope: "col", class: klassen.join(" ") || null },
        h("span", { class: "wt" },
          h("span", { class: "wt-lang" }, WOCHENTAGE[d.getDay()]),
          h("span", { class: "wt-kurz" }, t.wTag)),
        h("span", { class: "dt" }, `${d.getDate()}.${d.getMonth() + 1}.`));
    }));

  const zeilen = [];
  for (let nr = 1; nr <= maxStunde; nr++) {
    const zellen = [h("th", { scope: "row" }, nr)];
    tage.forEach((t) => {
      if (t.status !== "Unterricht") {
        if (nr === 1) zellen.push(h("td", { class: "tagfrei", rowspan: maxStunde }, tagHinweis(t)));
        return;
      }
      const hier = sichtbareStunden(t).filter((s) => s.stunde === nr);
      zellen.push(h("td", null, hier.map(eintrag)));
    });
    zeilen.push(h("tr", null, zellen));
  }

  huelle.replaceChildren(h("table", { class: "raster" },
    h("caption", { class: "vh" }, `${zielName()}, ${zeitraumText(state.montag)}, ${kwText(state.montag)}`),
    h("thead", null, kopf),
    h("tbody", null, zeilen)));
}

function renderMeldung() {
  const m = el("meldung");
  let text = "";
  if (!state.werte[state.modus]) {
    text = state.auswahl && !state.auswahl.grundplan
      ? "Es ist noch kein Stundenplan veröffentlicht."
      : `Wähle oben ${state.modus === "lehrer" ? "eine Lehrkraft" : state.modus === "klasse" ? "eine Klasse" : "einen Kurs"}, um den Plan zu sehen.`;
  } else if (state.fehler) {
    text = state.fehler;
  } else if (wocheOhneAenderungen()) {
    text = "Keine Änderungen in dieser Woche.";
  }
  m.textContent = text;
  m.hidden = !text;
}

function renderFuss() {
  const stand = el("stand");
  if (state.plan && state.offlineStand) {
    stand.textContent = `Offline – gespeicherter Stand vom ${zeitpunkt(state.offlineStand)}`;
    stand.classList.add("offline");
  } else {
    stand.textContent = state.plan ? `Stand ${uhrzeit(state.geladenUm)}` : "";
    stand.classList.remove("offline");
  }
  const tage = (state.plan && state.plan.tage) || [];
  const mitPlan = tage.find((t) => t.grundplan);
  const gp = mitPlan ? mitPlan.grundplan : (state.auswahl && state.auswahl.grundplan);
  el("grundplan").textContent = gp ? `Plan ${gp}` : "";
}

// ---------------------------------------------------------------------
//  Bedienung
// ---------------------------------------------------------------------

function wechsleWoche(tage) {
  state.montag = plusTage(state.montag, tage);
  ladeWoche();
}

function wechsleTag(schritt) {
  const anzahl = ((state.plan && state.plan.tage) || []).length;
  const neu = state.tagIndex + schritt;
  if (anzahl > 0 && neu >= 0 && neu < anzahl) {
    state.tagIndex = neu;
    render();
    const tab = el("tab-" + neu);
    if (tab && document.activeElement && document.activeElement.classList.contains("tab")) tab.focus();
    return;
  }
  // Über den Wochenrand hinaus: Nachbarwoche, erster bzw. letzter Tag.
  state.tagIndex = schritt > 0 ? 0 : 99;
  wechsleWoche(schritt > 0 ? 7 : -7);
}

for (const b of document.querySelectorAll(".modi button")) {
  b.addEventListener("click", () => {
    if (state.modus === b.dataset.modus) return;
    state.modus = b.dataset.modus;
    fuelleAuswahl();
    ladeWoche();
  });
}

el("wert").addEventListener("change", (e) => {
  state.werte[state.modus] = e.target.value;
  ladeWoche();
});

el("zurueck").addEventListener("click", () => wechsleWoche(-7));
el("vor").addEventListener("click", () => wechsleWoche(7));
el("heute").addEventListener("click", () => {
  state.montag = startMontag();
  state.springeZuHeute = true;
  ladeWoche();
});

const datumFeld = el("datum");
datumFeld.addEventListener("click", () => {
  try { if (datumFeld.showPicker) datumFeld.showPicker(); } catch { /* Browser öffnet selbst */ }
});
datumFeld.addEventListener("change", () => {
  if (!istIso(datumFeld.value)) return;
  state.montag = montagVon(datumFeld.value);
  state.zielDatum = datumFeld.value;
  ladeWoche();
});

el("nurAenderungen").addEventListener("change", (e) => {
  state.nurAenderungen = e.target.checked;
  zustandSpeichern();
  render();
});

el("aktualisieren").addEventListener("click", () => {
  ladeAuswahl();
  ladeWoche();
});

// Tagesreiter mit Pfeiltasten (Tab-Muster)
el("tage").addEventListener("keydown", (e) => {
  if (e.key === "ArrowRight") { e.preventDefault(); wechsleTag(1); }
  else if (e.key === "ArrowLeft") { e.preventDefault(); wechsleTag(-1); }
});

// Wischen in der Tagesliste: nächster / vorheriger Tag
let wischStart = null;
el("liste").addEventListener("touchstart", (e) => {
  const t = e.changedTouches[0];
  wischStart = { x: t.clientX, y: t.clientY };
}, { passive: true });
el("liste").addEventListener("touchend", (e) => {
  if (!wischStart) return;
  const t = e.changedTouches[0];
  const dx = t.clientX - wischStart.x;
  const dy = t.clientY - wischStart.y;
  wischStart = null;
  if (Math.abs(dx) > 60 && Math.abs(dy) < 45) wechsleTag(dx < 0 ? 1 : -1);
}, { passive: true });

// Automatisch nachladen – damit veröffentlichte Änderungen ohne ↻ erscheinen:
//  - beim Zurückwechseln ins Browserfenster bzw. in die App,
//  - alle 2 Minuten, solange die Seite sichtbar ist.
// Fokus und Sichtbarkeit melden sich oft gleichzeitig; ein Mindestabstand
// verhindert doppelte Abfragen.
const NACHLADEN_MS = 2 * 60 * 1000;
const MIN_ABSTAND_MS = 10 * 1000;

function stillNachladen() {
  if (laedtGerade || document.visibilityState !== "visible") return;
  if (!state.werte[state.modus]) return;
  if (Date.now() - state.geladenUm < MIN_ABSTAND_MS) return;
  ladeWoche({ still: true });
}

window.addEventListener("focus", stillNachladen);
document.addEventListener("visibilitychange", stillNachladen);
setInterval(() => {
  if (Date.now() - state.geladenUm >= NACHLADEN_MS) stillNachladen();
}, 15 * 1000);

window.addEventListener("hashchange", () => {
  const adr = hashLesen();
  if (!adr) return;
  const datum = adr.datum ? montagVon(adr.datum) : state.montag;
  if (adr.modus === state.modus && gleich(adr.wert, state.werte[state.modus]) && datum === state.montag) return;
  state.modus = adr.modus;
  if (adr.wert) state.werte[adr.modus] = adr.wert;
  state.montag = datum;
  if (adr.datum) state.zielDatum = adr.datum;
  fuelleAuswahl();
  ladeWoche();
});

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

zustandLaden();
fuelleAuswahl();
render();
ladeAuswahl();
ladeWoche();

// Service Worker: App offline startbar; nach einem Update einmal neu laden.
if ("serviceWorker" in navigator) {
  const hatteSteuerung = !!navigator.serviceWorker.controller;
  let neuGeladen = false;
  navigator.serviceWorker.addEventListener("controllerchange", () => {
    if (!hatteSteuerung || neuGeladen) return;
    neuGeladen = true;
    location.reload();
  });
  window.addEventListener("load", () => navigator.serviceWorker.register("sw.js").catch(() => {}));
}
