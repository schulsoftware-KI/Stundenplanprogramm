"use strict";
// =====================================================================
//  Stundenplan-PWA (Phase 2, Schritt 3 + 4): Wochenansicht für Lehrkräfte,
//  Klassen, Kurse und Schüler – mit Anmeldung. Daten: /api/woche/...
//  (Grundplan × Kalender × veröffentlichte Vertretungen). Reines JavaScript
//  ohne Build-Schritt.
//
//  Anmeldung (Schritt 4):
//  - Login per /api/auth/login (JWT). „Angemeldet bleiben" = Token im
//    dauerhaften Browser-Speicher (30 Tage), sonst nur bis zum Schließen.
//  - Personal (Admin, Lehrkräfte, Klassenleitung, Stufenkoordination): Lehrkraft,
//    Klasse, Kurs und Schüler (Namenssuche) wählbar; Lehrkräfte starten mit dem
//    eigenen Plan („Mein Plan" im Kontomenü).
//  - Schülerkonten: nur der eigene Plan (/api/woche/ich), keine Auswahl.
//  - Ohne Anmeldung nur, wenn der Server das erlaubt (/api/auth/optionen).
//  - Gespeicherte Wochen und Auswahl liegen je Benutzer getrennt im Browser
//    und werden beim Abmelden gelöscht. Server meldet 401 (abgelaufen,
//    widerrufen) -> zurück zur Anmeldung.
//
//  Wochenansicht (Schritt 3):
//  - Handy hochkant: Tagesreiter + Liste; breit bzw. quer: Wochenraster.
//  - Woche vor/zurück, „Heute", Datum wählen; am Wochenende startet die
//    App mit der kommenden Woche.
//  - Zuletzt geladene Wochen sind offline lesbar (Hinweis „Offline" im Fuß).
//  - Lädt von selbst nach: beim Zurückwechseln ins Fenster bzw. in die App
//    und alle 2 Minuten, solange die Seite sichtbar ist.
//  - Auswahl und Woche stehen in der Adresse (#klasse/05A/2026-11-02).
//
//  Konten (Schritt 5, Code in konten.js): Passwort ändern, Pflicht-Änderung
//  nach Startpasswort, Kontenverwaltung für Admin bzw. Schülerpasswörter für
//  Klassenleitung/Stufenkoordination.
//
//  Klassenbuch (Phase 3.1, Code in klassenbuch.js): Stunden mit Klassenbuch
//  sind für Berechtigte antippbar (Stundenblatt mit Anwesenheit); erfasste
//  Stunden tragen ein Häkchen. Kontomenü „Meine Gruppen".
//  Fehlzeiten (Phase 3.2, Code in fehlzeiten.js): Kontomenü „Fehlzeiten" bzw.
//  „Meine Fehlzeiten".
//  Thema/Hausaufgabe (Phase 3.3): Stundenblatt (klassenbuch.js), Themenliste
//  und „Thema fehlt" (fehlzeiten.js), Schüleransicht „Hausaufgaben" und
//  Lese-Dialog im Plan (hausaufgaben.js). Für Personal landen Thema und
//  Hausaufgabe nie im Browser-Speicher (ohneKlassenbuchInhalt).
//  Klassenbucheinträge (Phase 3.4, Code in eintraege.js): Stundenblatt,
//  Schülerdetail und Listen; das Kontomenü „Fehlzeiten" heißt jetzt
//  „Klassenbuch". Einträge landen nie im Browser-Speicher.
//  Hilfe (Entscheidungen 52–59, Code in hilfe.js): „?" im Kopf, Kontomenü
//  „Hilfe", Kurzkarten beim ersten Besuch; gespeichert wird nur der Merker
//  „Kurzkarte gesehen" (bleibt beim Abmelden).
// =====================================================================

const MODI = {
  lehrer:   { wahl: "Lehrkraft wählen" },
  klasse:   { wahl: "Klasse oder Stufe wählen" },
  gruppe:   { wahl: "Kurs oder Gruppe wählen" },
  schueler: { wahl: "Schülerin oder Schüler suchen" },
  ich:      { wahl: "" }, // Schülerkonto: eigener Plan
};
const AUSWAHL_MODI = ["lehrer", "klasse", "gruppe"]; // Modi mit Auswahlliste

const PERSONAL = ["Admin", "Lehrer", "Klassenlehrer", "Stufenkoordination"];
const ROLLEN_TEXT = {
  Admin: "Verwaltung",
  Lehrer: "Lehrkraft",
  Klassenlehrer: "Klassenleitung",
  Stufenkoordination: "Stufenkoordination",
  Schueler: "Schülerkonto",
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

const SUCH_MAX = 20; // so viele Suchtreffer werden gezeigt

const sortierer = new Intl.Collator("de", { numeric: true, sensitivity: "base" });
const el = (id) => document.getElementById(id);

const leereWerte = () => ({ lehrer: "", klasse: "", gruppe: "", schueler: "", ich: "ich" });

const state = {
  anmeldung: null,       // { token, benutzername, rolle, anzeige, lehrerKuerzel, zustaendig,
                         //   mussPasswortAendern, passwortRegel, dauerhaft, gueltigBis }
  anmeldungNoetig: true, // laut Server (/api/auth/optionen)
  modus: "klasse",
  werte: leereWerte(),   // zuletzt gewählter Wert je Ansicht (schueler: Id)
  montag: "",            // ISO-Datum des Montags der angezeigten Woche
  tagIndex: 0,           // gewählter Tag (Handy-Ansicht)
  zielDatum: null,       // nach dem Laden diesen Tag auswählen
  springeZuHeute: true,  // nach dem Laden „heute" auswählen (Start / Heute-Knopf)
  nurAenderungen: false,
  auswahl: null,
  antwort: null,         // Rohantwort der API (Vergleich beim stillen Nachladen)
  plan: null,
  person: null,          // Schülerdaten bei Schüler- bzw. eigener Schüleransicht
  fehler: null,
  geladenUm: 0,
  offlineStand: null,    // Zeitstempel, wenn der Plan aus dem Speicher stammt
};

// ---------------------------------------------------------------------
//  Hilfen: Elemente bauen (ohne innerHTML), Datum
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
const datumKurz = (t) => new Date(t).toLocaleDateString("de-DE", { day: "2-digit", month: "2-digit", year: "numeric" });
const gleich = (a, b) => (a || "").trim().toUpperCase() === (b || "").trim().toUpperCase();

const alsApp = () => {
  try { return window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true; }
  catch { return false; }
};

// ---------------------------------------------------------------------
//  Browser-Speicher. Kann leer oder gesperrt sein – die App funktioniert
//  dann trotzdem (nur ohne Merken).
//  - "anmeldung": dauerhaft -> localStorage, sonst sessionStorage.
//  - Daten je Benutzer (Präfix „u:<name>|"): Auswahl und offline lesbare
//    Wochen. Ohne „angemeldet bleiben" ebenfalls nur bis zum Schließen.
// ---------------------------------------------------------------------

function ablage(holen) {
  const s = () => { try { return holen(); } catch { return null; } };
  return {
    lesen(k, ersatz) {
      try { const v = s() && s().getItem(k); return v === null || v === undefined ? ersatz : JSON.parse(v); }
      catch { return ersatz; }
    },
    schreiben(k, v) {
      try { if (!s()) return false; s().setItem(k, JSON.stringify(v)); return true; }
      catch { return false; /* voll oder gesperrt */ }
    },
    entfernen(k) {
      try { if (s()) s().removeItem(k); } catch { /* egal */ }
    },
    schluessel() {
      try {
        const x = s();
        const liste = [];
        if (x) for (let i = 0; i < x.length; i++) liste.push(x.key(i));
        return liste;
      } catch { return []; }
    },
  };
}
const lokal = ablage(() => window.localStorage);
const sitzung = ablage(() => window.sessionStorage);

const nutzerAblage = () => (state.anmeldung && !state.anmeldung.dauerhaft ? sitzung : lokal);
const praefix = () => "u:" + (state.anmeldung ? state.anmeldung.benutzername.toLowerCase() : "-") + "|";

const CACHE_MAX = 30;

function cacheSchreiben(url, daten) {
  const a = nutzerAblage();
  const p = praefix();
  if (!a.schreiben(p + "antwort:" + url, { t: Date.now(), d: daten })) return;
  const index = a.lesen(p + "antwort-index", []).filter((u) => u !== url);
  index.push(url);
  while (index.length > CACHE_MAX) a.entfernen(p + "antwort:" + index.shift());
  a.schreiben(p + "antwort-index", index);
}

const cacheLesen = (url) => nutzerAblage().lesen(praefix() + "antwort:" + url, null);

/**
 * Phase 3.3: Thema/Hausaufgabe sind Klassenbuchdaten – für Personal nicht im Browser-Speicher
 * (Entscheidung 5). Schüler dürfen ihre eigenen Hausaufgaben offline lesen (Entscheidung 26).
 */
function ohneKlassenbuchInhalt(daten) {
  if (!istPersonal() || !daten) return daten;
  const plan = Array.isArray(daten.tage) ? daten : daten.plan;
  if (!plan || !Array.isArray(plan.tage)) return daten;
  const kopie = JSON.parse(JSON.stringify(daten));
  const p = Array.isArray(kopie.tage) ? kopie : kopie.plan;
  for (const t of p.tage)
    for (const s of t.stunden || []) { delete s.thema; delete s.hausaufgabe; delete s.hausaufgabeFaellig; }
  return kopie;
}

/** Alles löschen, was für die angemeldete Person gespeichert wurde. */
function nutzerdatenLoeschen() {
  const p = praefix();
  for (const a of [lokal, sitzung])
    for (const k of a.schluessel()) if (k.startsWith(p)) a.entfernen(k);
}

/** Speicher der Version vor der Anmeldung (Schritt 3) aufräumen. */
function altlastenEntfernen() {
  for (const k of lokal.schluessel())
    if (k === "zustand" || k === "kuerzel" || k === "antwort-index" || k.startsWith("antwort:")) lokal.entfernen(k);
}

// ---------------------------------------------------------------------
//  Anmeldung: Zustand, Rollen, Anfragen
// ---------------------------------------------------------------------

function anmeldungLesen() {
  const a = sitzung.lesen("anmeldung", null) || lokal.lesen("anmeldung", null);
  return a && a.token && a.benutzername && a.rolle ? a : null;
}

function anmeldungSpeichern() {
  const a = state.anmeldung;
  (a.dauerhaft ? lokal : sitzung).schreiben("anmeldung", a);
  (a.dauerhaft ? sitzung : lokal).entfernen("anmeldung");
}

const istSchueler = () => !!state.anmeldung && state.anmeldung.rolle === "Schueler";
const istPersonal = () => !!state.anmeldung && PERSONAL.includes(state.anmeldung.rolle);
const eigenesKuerzel = () => (state.anmeldung && state.anmeldung.lehrerKuerzel) || "";

function erlaubteModi() {
  if (istSchueler()) return ["ich"];
  if (istPersonal()) return ["lehrer", "klasse", "gruppe", "schueler"];
  return ["lehrer", "klasse", "gruppe"]; // ohne Anmeldung (nur wenn der Server das erlaubt)
}

const authKopf = () => (state.anmeldung ? { Authorization: "Bearer " + state.anmeldung.token } : {});

class HttpFehler extends Error {
  constructor(status, text, passwortAendern = false) {
    super("HTTP " + status);
    this.status = status;
    this.text = text || "";
    this.passwortAendern = passwortAendern; // 403: erst eigenes Passwort festlegen
    this.daten = null;
  }
}

/**
 * Holt JSON vom Server. Ohne Verbindung (oder bei Serverfehler) die zuletzt
 * gespeicherte Antwort; 401/403/404 werden dagegen als HttpFehler gemeldet.
 */
async function holen(url, { merken = true } = {}) {
  const abbruch = new AbortController();
  const timer = setTimeout(() => abbruch.abort(), 12000);
  try {
    const resp = await fetch(url, { cache: "no-store", signal: abbruch.signal, headers: authKopf() });
    if (resp.status === 401 || resp.status === 403 || resp.status === 404) {
      let text = "";
      let pflicht = false;
      try { const j = await resp.json(); text = (j && j.fehler) || ""; pflicht = !!(j && j.passwortAendern); } catch { /* kein JSON */ }
      throw new HttpFehler(resp.status, text, pflicht);
    }
    if (!resp.ok) throw new Error("Server antwortet mit " + resp.status);
    const daten = await resp.json();
    if (merken) cacheSchreiben(url, ohneKlassenbuchInhalt(daten));
    return { daten, offlineStand: null };
  } catch (fehler) {
    if (fehler instanceof HttpFehler) throw fehler;
    const alt = merken ? cacheLesen(url) : null;
    if (alt) return { daten: alt.d, offlineStand: alt.t };
    throw fehler;
  } finally {
    clearTimeout(timer);
  }
}

let sitzungNr = 0;        // wechselt bei An-/Abmeldung: alte Antworten werden verworfen
let ladeNr = 0;
let laedtGerade = false;  // eine Woche wird gerade geladen (kein stilles Nachladen dazwischen)

/** Laufende Wochen-Abfrage verwerfen (ihr Ergebnis wird ignoriert) und die Ladeanzeige beenden. */
function ladenAbbrechen() {
  ladeNr++;
  laedtGerade = false;
  document.body.classList.remove("laedt");
  el("inhalt").removeAttribute("aria-busy");
}

/**
 * 401 = Anmeldung abgelaufen/widerrufen bzw. Anmeldung nötig; 403 mit passwortAendern =
 * erst ein eigenes Passwort festlegen. true = Anmelde- bzw. Passwortmaske gezeigt.
 */
function zugriffsFehler(fehler) {
  if (fehler instanceof HttpFehler && fehler.status === 403 && fehler.passwortAendern && state.anmeldung) {
    zeigePflicht();
    return true;
  }
  if (!(fehler instanceof HttpFehler) || fehler.status !== 401) return false;
  if (state.anmeldung) {
    abmelden("Deine Anmeldung ist abgelaufen oder wurde beendet. Bitte melde dich neu an.");
  } else {
    state.anmeldungNoetig = true;
    lokal.schreiben("optionen", { anmeldungNoetig: true });
    zeigeAnmeldung("Bitte melde dich an, um die Pläne zu sehen.");
  }
  return true;
}

function fehlerText(f) {
  if (f instanceof HttpFehler && f.status === 403)
    return "Für diese Ansicht fehlt dir die Berechtigung.";
  if (f instanceof HttpFehler && f.status === 404) {
    if (state.modus === "ich")
      return (f.text || "Zu deinem Konto wurde kein Plan gefunden.") + " Bitte wende dich an das Sekretariat.";
    if (state.modus === "schueler")
      return "Diese Schülerin bzw. dieser Schüler wurde nicht gefunden. Bitte neu suchen.";
    return "Nicht gefunden.";
  }
  return "Der Plan konnte nicht geladen werden. Prüfe die Verbindung zum Schulserver und tippe oben rechts auf Aktualisieren.";
}

// ---------------------------------------------------------------------
//  Zustand: Adresse (#modus/wert/datum) und gespeicherte Auswahl
// ---------------------------------------------------------------------

function hashLesen() {
  const teile = location.hash.replace(/^#/, "").split("/").map((t) => {
    try { return decodeURIComponent(t); } catch { return ""; }
  });
  if (!MODI[teile[0]]) return null;
  if (teile[0] === "ich") return { modus: "ich", wert: "ich", datum: teile.slice(1).find(istIso) || null };
  return { modus: teile[0], wert: teile[1] || "", datum: istIso(teile[2]) ? teile[2] : null };
}

function hashSchreiben() {
  const teile = state.modus === "ich" ? ["ich"] : [state.modus, state.werte[state.modus] || ""];
  if (state.montag !== startMontag()) teile.push(state.montag);
  const neu = "#" + teile.map(encodeURIComponent).join("/");
  if (location.hash !== neu) history.replaceState(null, "", neu);
}

function zustandSpeichern() {
  nutzerAblage().schreiben(praefix() + "zustand",
    { modus: state.modus, werte: state.werte, nurAenderungen: state.nurAenderungen });
}

/** Erlaubte Ansicht sicherstellen (Schüler: nur „ich"; Lehrkraft: eigenes Kürzel vorbelegen). */
function modusPruefen() {
  const erlaubt = erlaubteModi();
  if (!erlaubt.includes(state.modus))
    state.modus = eigenesKuerzel() && erlaubt.includes("lehrer") ? "lehrer"
      : erlaubt.includes("klasse") ? "klasse" : erlaubt[0];
  if (state.modus === "lehrer" && !state.werte.lehrer && eigenesKuerzel())
    state.werte.lehrer = eigenesKuerzel();
}

function zustandLaden() {
  state.modus = "klasse";
  state.werte = leereWerte();
  state.nurAenderungen = false;

  const z = nutzerAblage().lesen(praefix() + "zustand", null);
  if (z) {
    if (MODI[z.modus]) state.modus = z.modus;
    Object.assign(state.werte, z.werte || {});
    state.werte.ich = "ich";
    state.nurAenderungen = !!z.nurAenderungen;
  } else if (eigenesKuerzel()) {
    // Erste Anmeldung einer Lehrkraft: mit dem eigenen Plan starten.
    state.modus = "lehrer";
    state.werte.lehrer = eigenesKuerzel();
  }

  const adr = hashLesen();
  if (adr) {
    state.modus = adr.modus;
    if (adr.wert) state.werte[adr.modus] = adr.wert;
  }
  modusPruefen();

  state.montag = adr && adr.datum ? montagVon(adr.datum) : startMontag();
  state.zielDatum = adr && adr.datum ? adr.datum : null;
  state.springeZuHeute = !(adr && adr.datum);
}

// ---------------------------------------------------------------------
//  Laden
// ---------------------------------------------------------------------

function planUrl(montag) {
  if (state.modus === "ich") return `api/woche/ich?datum=${montag}`;
  const wert = state.werte[state.modus];
  if (!wert) return null;
  return `api/woche/${state.modus}/${encodeURIComponent(wert)}?datum=${montag}`;
}

/** /schueler und /ich liefern { art, schueler, plan }, die übrigen direkt den Plan. */
function uebernehmeAntwort(daten) {
  state.antwort = daten;
  state.plan = daten && daten.plan ? daten.plan : daten;
  state.person = daten && daten.schueler ? daten.schueler : null;
}

function verwerfeAntwort() {
  state.antwort = null;
  state.plan = null;
  state.person = null;
}

async function ladeAuswahl() {
  if (istSchueler()) return;
  const sn = sitzungNr;
  try {
    const { daten } = await holen(`api/woche/auswahl?datum=${state.montag}`);
    if (sn !== sitzungNr) return;
    state.auswahl = daten;
  } catch (f) {
    if (sn !== sitzungNr) return;
    if (zugriffsFehler(f)) return;
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
  const url = planUrl(state.montag);
  if (!url) {
    ladenAbbrechen();
    verwerfeAntwort();
    state.fehler = null;
    render();
    return;
  }
  const nr = ++ladeNr;
  laedtGerade = true;
  if (!still) {
    document.body.classList.add("laedt");
    el("inhalt").setAttribute("aria-busy", "true");
  }
  try {
    const { daten, offlineStand } = await holen(url);
    if (nr !== ladeNr) return; // inzwischen etwas anderes angefordert
    const unveraendert = still && !state.fehler && state.antwort &&
      JSON.stringify(daten) === JSON.stringify(state.antwort);
    uebernehmeAntwort(daten);
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
    if (!still) haLeisteLaden(); // Phase 3.3: Schüler – offene Hausaufgaben über dem Plan
  } catch (fehler) {
    if (nr !== ladeNr) return;
    state.geladenUm = Date.now(); // nicht im Sekundentakt erneut versuchen
    if (zugriffsFehler(fehler)) return;
    if (still && state.plan && !(fehler instanceof HttpFehler)) return; // angezeigten Plan stehen lassen
    verwerfeAntwort();
    state.fehler = fehlerText(fehler);
    if (fehler instanceof HttpFehler && fehler.status === 404 && state.modus === "schueler") {
      state.werte.schueler = "";
      zustandSpeichern();
      hashSchreiben();
    }
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
  const url = planUrl(montagIso);
  if (url) holen(url).catch(() => {});
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
//  Auswahlliste (Lehrkraft / Klasse / Kurs)
// ---------------------------------------------------------------------

function fuelleAuswahl() {
  if (!AUSWAHL_MODI.includes(state.modus)) return;
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
      sel.append(...sortiert(a.lehrer).map((l) => option(l, gleich(l, eigenesKuerzel()) ? `${l} (ich)` : l)));
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

// ---------------------------------------------------------------------
//  Schülersuche (nur Personal)
// ---------------------------------------------------------------------

let suchTimer = null;
let suchNr = 0;

const personName = (p) => (p ? `${p.vorname} ${p.nachname}` : "");
function personKlasse(p) {
  if (!p) return "";
  const klasse = (p.lerngruppen || []).find((g) => /^\d{2}[A-Za-z]{1,2}$/.test(g));
  return klasse || p.stufe || "";
}

function sucheStarten(sofort = false) {
  clearTimeout(suchTimer);
  suchNr++;
  const q = el("suchfeld").value.trim();
  if (q.length < 2) {
    trefferZeigen(null, q.length ? "Mindestens zwei Buchstaben eingeben." : "");
    return;
  }
  suchTimer = setTimeout(() => suchen(q), sofort ? 0 : 250);
}

async function suchen(q) {
  const nr = ++suchNr;
  el("suchInfo").textContent = "Suche …";
  try {
    const { daten } = await holen(`api/schueler?q=${encodeURIComponent(q)}&limit=${SUCH_MAX + 1}`, { merken: false });
    if (nr !== suchNr) return;
    trefferZeigen(Array.isArray(daten) ? daten : []);
  } catch (f) {
    if (nr !== suchNr) return;
    if (zugriffsFehler(f)) return;
    trefferZeigen(null, f instanceof HttpFehler && f.status === 403
      ? "Die Schülersuche ist nur für Lehrkräfte und Verwaltung."
      : "Suche nicht möglich – keine Verbindung zum Schulserver?");
  }
}

/** Trefferliste anzeigen (liste = null: Liste leeren, info als Hinweis). */
function trefferZeigen(liste, info = "") {
  const ul = el("treffer");
  if (!liste) {
    ul.replaceChildren();
    ul.hidden = true;
    el("suchInfo").textContent = info;
    return;
  }
  const zeigen = liste.slice(0, SUCH_MAX);
  el("suchInfo").textContent = zeigen.length === 0 ? "Niemand gefunden."
    : liste.length > SUCH_MAX ? `Mehr als ${SUCH_MAX} Treffer – bitte genauer suchen.`
    : zeigen.length === 1 ? "1 Treffer" : `${zeigen.length} Treffer`;
  ul.replaceChildren(...zeigen.map((s) => h("li", null,
    h("button", { type: "button", class: "treffer-knopf", onclick: () => schuelerWaehlen(s) },
      h("span", { class: "t-name" }, `${s.nachname}, ${s.vorname}`),
      h("span", { class: "t-klasse" }, s.klasse || s.stufe || "")))));
  ul.hidden = zeigen.length === 0;
}

function schuelerWaehlen(s) {
  state.werte.schueler = String(s.id);
  el("suchfeld").value = `${s.vorname} ${s.nachname}`;
  trefferZeigen(null, "");
  verwerfeAntwort();
  ladeWoche();
}

/** Suchfeld zeigt den Namen der angezeigten Person (solange niemand darin tippt). */
function suchfeldAbgleichen() {
  const feld = el("suchfeld");
  if (document.activeElement === feld || !el("treffer").hidden) return;
  if (!state.werte.schueler) feld.value = "";
  else if (state.person) feld.value = personName(state.person);
}

// ---------------------------------------------------------------------
//  Darstellung einer Stunde
// ---------------------------------------------------------------------

/** Lesbarer Name des Ziels für die Kopfzeile. */
function zielName() {
  if (state.modus === "ich") {
    const k = personKlasse(state.person);
    return k ? `Mein Plan · ${k}` : "Mein Plan";
  }
  const wert = state.werte[state.modus];
  if (!wert) return "Stundenplan";
  if (state.modus === "schueler") {
    if (!state.person) return "Schülerplan";
    const k = personKlasse(state.person);
    return k ? `${personName(state.person)} (${k})` : personName(state.person);
  }
  if (state.modus === "lehrer")
    return gleich(wert, eigenesKuerzel()) ? `Mein Plan · ${wert}` : `Lehrkraft ${wert}`;
  if (state.modus === "klasse")
    return /^(EF|Q1|Q2)$/i.test(wert) ? `Stufe ${wert.toUpperCase()}` : `Klasse ${wert}`;
  const a = state.auswahl;
  const kurs = a && [...a.kurse, ...a.gruppen].find((k) => gleich(k.schluessel, wert));
  if (kurs && a.kurse.includes(kurs)) return `${kurs.fach} (${kurs.stufe})`;
  if (kurs) return gleich(kurs.fach, kurs.schluessel) ? kurs.schluessel : `${kurs.schluessel} (${kurs.fach})`;
  return wert;
}

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

function eintrag(s, datum) {
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

  // Klassenbuch (Phase 3.1): Stunde antippen öffnet das Stundenblatt.
  // Phase 3.3: Wer kein Stundenblatt öffnen darf, liest Thema/Hausaufgabe in einem Dialog.
  const kbAuf = !!datum && kbDarfOeffnen(s);
  const inhaltAuf = !!datum && !kbAuf && haInhaltZeigbar(s);
  const props = { class: klassen.join(" ") };
  if (kbAuf || inhaltAuf) {
    klassen.push("kb-ja");
    if (kbAuf && s.erfasst) klassen.push("kb-erfasst");
    if (inhaltAuf) klassen.push("inhalt-ja");
    props.class = klassen.join(" ");
    props.role = "button";
    props.tabindex = "0";
    const oeffnen = kbAuf ? () => stundenblattOeffnen(s, datum) : () => haInhaltZeigen(s, datum);
    props.onclick = oeffnen;
    props.onkeydown = (e) => {
      if (e.key === "Enter" || e.key === " ") { e.preventDefault(); oeffnen(); }
    };
    if (inhaltAuf) props["aria-haspopup"] = "dialog";
  }

  // Zweite Zeile: Lehrersicht -> Klassen/Kurs (+ Lehrerwechsel), sonst die Lehrkraft.
  let wer;
  if (lehrerSicht) {
    const wechsel = s.originalLehrer ? korrektur(s.originalLehrer, s.lehrer) : null;
    wer = [gruppenText(s), wechsel ? " " : "", wechsel];
  } else {
    wer = korrektur(s.originalLehrer, s.lehrer);
  }

  return h("div", props,
    h("div", { class: "e-oben" },
      h("span", { class: "fach" }, fachText(s)),
      h("span", { class: "raum" }, korrektur(s.originalRaum, s.raum))),
    h("div", { class: "e-unten" },
      h("span", { class: "wer" }, wer),
      badge || kbAuf || s.hausaufgabe
        ? h("span", { class: "e-rechts" },
            badge ? h("span", { class: "badge" }, badge) : null,
            s.hausaufgabe ? haMarke() : null,
            kbAuf ? kbSymbol(s.erfasst) : null)
        : null));
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

  const erlaubt = erlaubteModi();
  el("auswahl").hidden = state.modus === "ich";
  for (const b of document.querySelectorAll(".modi button[data-modus]")) {
    b.hidden = !erlaubt.includes(b.dataset.modus);
    b.setAttribute("aria-pressed", String(b.dataset.modus === state.modus));
  }
  const suche = state.modus === "schueler";
  el("wahl").hidden = suche;
  el("suche").hidden = !suche;
  if (suche) suchfeldAbgleichen();
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
        h("div", { class: "std-eintraege" }, nachStunde.get(nr).map((s) => eintrag(s, tag.datum))))
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
      zellen.push(h("td", null, hier.map((s) => eintrag(s, t.datum))));
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
  if (state.fehler) {
    text = state.fehler;
  } else if (!state.werte[state.modus]) {
    if (state.modus === "schueler")
      text = "Suche oben nach dem Namen einer Schülerin oder eines Schülers, um den persönlichen Plan zu sehen.";
    else if (state.auswahl && !state.auswahl.grundplan)
      text = "Es ist noch kein Stundenplan veröffentlicht.";
    else
      text = `Wähle oben ${state.modus === "lehrer" ? "eine Lehrkraft" : state.modus === "klasse" ? "eine Klasse" : "einen Kurs"}, um den Plan zu sehen.`;
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
//  Anmeldemaske, App-Start, Abmelden
// ---------------------------------------------------------------------

function anmeldeHinweis(text) {
  const p = el("anmeldeHinweis");
  p.textContent = text || "";
  p.hidden = !text;
}

function anmeldeFehler(text) {
  const p = el("anmeldeFehler");
  p.textContent = text || "";
  p.hidden = !text;
}

function zeigeAnmeldung(hinweis) {
  sitzungNr++;
  ladenAbbrechen();
  alleDialogeZu();
  verwaltungLeeren();
  klassenbuchLeeren();
  fehlzeitenLeeren();
  hausaufgabenLeeren();
  el("inhalt").hidden = true;
  el("verwaltung").hidden = true;
  el("klassenbuch").hidden = true;
  el("fehlzeiten").hidden = true;
  el("hausaufgaben").hidden = true;
  el("pflicht").hidden = true;
  el("anmeldung").hidden = false;
  el("aktualisieren").hidden = true;
  el("kontoKnopf").hidden = true;
  el("titel").textContent = "Stundenplan";
  document.title = "Anmelden – Stundenplan";
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  anmeldeHinweis(hinweis);
  anmeldeFehler("");
  el("ohneAnmeldung").hidden = state.anmeldungNoetig;
  const feld = el("benutzer").value ? el("passwort") : el("benutzer");
  setTimeout(() => { try { feld.focus({ preventScroll: true }); } catch { /* egal */ } }, 0);
}

function appStarten() {
  sitzungNr++;
  ladenAbbrechen();
  alleDialogeZu();
  verwaltungLeeren();
  klassenbuchLeeren();
  fehlzeitenLeeren();
  hausaufgabenLeeren();
  el("anmeldung").hidden = true;
  el("pflicht").hidden = true;
  el("verwaltung").hidden = true;
  el("klassenbuch").hidden = true;
  el("fehlzeiten").hidden = true;
  el("hausaufgaben").hidden = true;
  el("inhalt").hidden = false;
  el("aktualisieren").hidden = false;
  const k = el("kontoKnopf");
  k.hidden = false;
  k.setAttribute("aria-label", state.anmeldung
    ? `Konto: ${state.anmeldung.anzeige || state.anmeldung.benutzername}`
    : "Anmelden");
  k.setAttribute("title", state.anmeldung ? "Konto" : "Anmelden");
  k.classList.toggle("abgemeldet", !state.anmeldung);

  state.auswahl = null;
  verwerfeAntwort();
  state.fehler = null;
  state.offlineStand = null;
  state.geladenUm = 0;
  el("suchfeld").value = "";
  trefferZeigen(null, "");

  zustandLaden();
  fuelleAuswahl();
  haLeisteRendern();
  window.scrollTo(0, 0); // nach der Anmeldemaske (Handy quer: Seite war gescrollt) oben beginnen
  render();
  ladeAuswahl();
  ladeWoche();
}

async function anmelden(e) {
  e.preventDefault();
  const benutzername = el("benutzer").value.trim();
  const passwort = el("passwort").value;
  if (!benutzername || !passwort) {
    anmeldeFehler("Bitte Benutzername und Passwort eingeben.");
    (benutzername ? el("passwort") : el("benutzer")).focus();
    return;
  }
  const dauerhaft = el("bleiben").checked;
  const knopf = el("anmeldeKnopf");
  knopf.disabled = true;
  knopf.textContent = "Anmelden …";
  anmeldeFehler("");
  try {
    const resp = await fetch("api/auth/login", {
      method: "POST",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ benutzername, passwort, angemeldetBleiben: dauerhaft }),
    });
    let d = null;
    try { d = await resp.json(); } catch { /* kein JSON */ }
    if (!resp.ok) {
      anmeldeFehler(resp.status === 401 || resp.status === 403 || resp.status === 429
        ? (d && d.fehler) || "Benutzername oder Passwort falsch."
        : `Anmeldung zurzeit nicht möglich (Fehler ${resp.status}).`);
      el("passwort").select();
      return;
    }
    anmeldungUebernehmen(d, dauerhaft);
    el("passwort").value = "";
    passwortVerbergen();
    history.replaceState(null, "", location.pathname + location.search);
    if (d.mussPasswortAendern) {
      pwMerk = passwort; // nur im Arbeitsspeicher, damit es nicht noch einmal eingetippt werden muss
      zeigePflicht();
    } else {
      appStarten();
    }
  } catch {
    anmeldeFehler("Keine Verbindung zum Schulserver. Prüfe die Internetverbindung und versuche es erneut.");
  } finally {
    knopf.disabled = false;
    knopf.textContent = "Anmelden";
  }
}

/** Antwort von Login bzw. Passwortänderung übernehmen und speichern. */
function anmeldungUebernehmen(d, dauerhaft) {
  state.anmeldung = {
    token: d.token,
    benutzername: d.benutzername,
    rolle: d.rolle,
    anzeige: d.anzeige || d.benutzername,
    lehrerKuerzel: d.lehrerKuerzel || null,
    zustaendig: d.zustaendig || null,
    mussPasswortAendern: !!d.mussPasswortAendern,
    passwortRegel: d.passwortRegel || null,
    dauerhaft,
    gueltigBis: d.gueltigBis || null,
  };
  anmeldungSpeichern();
}

/** Abmelden auf diesem Gerät: Token und alle gespeicherten Daten dieser Person löschen. */
function abmelden(hinweis) {
  const name = state.anmeldung ? state.anmeldung.benutzername : "";
  pwMerk = "";
  nutzerdatenLoeschen();
  lokal.entfernen("anmeldung");
  sitzung.entfernen("anmeldung");
  state.anmeldung = null;
  state.auswahl = null;
  verwerfeAntwort();
  state.fehler = null;
  el("suchfeld").value = "";
  trefferZeigen(null, "");
  history.replaceState(null, "", location.pathname + location.search);
  el("benutzer").value = hinweis ? name : ""; // nach Ablauf: Name schon eintragen
  el("passwort").value = "";
  zeigeAnmeldung(hinweis);
}

async function ladeOptionen() {
  try {
    const resp = await fetch("api/auth/optionen", { cache: "no-store" });
    if (!resp.ok) return;
    const d = await resp.json();
    state.anmeldungNoetig = d.anmeldungNoetig !== false;
    lokal.schreiben("optionen", { anmeldungNoetig: state.anmeldungNoetig });
    if (!el("anmeldung").hidden) el("ohneAnmeldung").hidden = state.anmeldungNoetig;
  } catch { /* offline: der zuletzt bekannte Wert gilt */ }
}

function passwortVerbergen() {
  el("passwort").type = "password";
  el("zeigen").textContent = "Anzeigen";
  el("zeigen").setAttribute("aria-pressed", "false");
}

// ---------------------------------------------------------------------
//  Kontomenü
// ---------------------------------------------------------------------

const konto = el("konto");
let ueberallBestaetigt = false;

function kontoFehler(text) {
  el("kontoFehler").textContent = text || "";
  el("kontoFehler").hidden = !text;
}

function ueberallZuruecksetzen() {
  ueberallBestaetigt = false;
  const b = el("ueberallAbmelden");
  b.textContent = "Auf allen Geräten abmelden";
  b.classList.remove("warnung");
  b.disabled = false;
}

function kontoOeffnen() {
  if (!state.anmeldung) { zeigeAnmeldung(); return; }
  const a = state.anmeldung;
  el("kontoName").textContent = a.anzeige || a.benutzername;
  const info = [ROLLEN_TEXT[a.rolle] || a.rolle];
  if (!gleich(a.anzeige, a.benutzername)) info.push(`Benutzer ${a.benutzername}`);
  info.push(a.dauerhaft && a.gueltigBis
    ? `angemeldet bis ${datumKurz(a.gueltigBis)}`
    : "angemeldet bis zum Schließen");
  el("kontoInfo").textContent = info.join(" · ");
  el("meinPlan").hidden = !(istPersonal() && eigenesKuerzel());
  kontoMenueAnpassen();
  klassenbuchMenueAnpassen();
  fehlzeitenMenueAnpassen();
  hausaufgabenMenueAnpassen();
  ueberallZuruecksetzen();
  kontoFehler("");
  if (typeof konto.showModal === "function") { if (!konto.open) konto.showModal(); }
  else konto.setAttribute("open", "");
}

function kontoSchliessen() {
  if (!konto.open) return;
  if (typeof konto.close === "function") konto.close();
  else konto.removeAttribute("open");
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

for (const b of document.querySelectorAll(".modi button[data-modus]")) {
  b.addEventListener("click", () => {
    const modus = b.dataset.modus;
    if (state.modus === modus || !erlaubteModi().includes(modus)) return;
    state.modus = modus;
    modusPruefen();
    state.fehler = null;
    verwerfeAntwort();
    fuelleAuswahl();
    ladeWoche();
    if (modus === "schueler" && !state.werte.schueler) el("suchfeld").focus();
  });
}

el("wert").addEventListener("change", (e) => {
  state.werte[state.modus] = e.target.value;
  ladeWoche();
});

// Schülersuche
el("suchfeld").addEventListener("input", () => sucheStarten());
el("suchfeld").addEventListener("keydown", (e) => {
  if (e.key === "Enter") { e.preventDefault(); sucheStarten(true); }
  else if (e.key === "ArrowDown") {
    const erster = el("treffer").querySelector("button");
    if (erster) { e.preventDefault(); erster.focus(); }
  } else if (e.key === "Escape") {
    trefferZeigen(null, "");
  }
});
el("suchfeld").addEventListener("focus", () => {
  // Beim Antippen den Namen markieren, damit man gleich neu tippen kann.
  if (state.person && el("suchfeld").value === personName(state.person)) el("suchfeld").select();
});
el("treffer").addEventListener("keydown", (e) => {
  const knoepfe = [...el("treffer").querySelectorAll("button")];
  const i = knoepfe.indexOf(document.activeElement);
  if (i < 0) return;
  if (e.key === "ArrowDown" && i < knoepfe.length - 1) { e.preventDefault(); knoepfe[i + 1].focus(); }
  else if (e.key === "ArrowUp") { e.preventDefault(); (i > 0 ? knoepfe[i - 1] : el("suchfeld")).focus(); }
  else if (e.key === "Escape") { e.preventDefault(); el("suchfeld").focus(); }
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
  if (verwaltungOffen()) { verwaltungAktualisieren(); return; }
  if (klassenbuchOffen()) { kbAktualisieren(); return; }
  if (fehlzeitenOffen()) { fzAktualisieren(); return; }
  if (hausaufgabenOffen()) { haLaden(); return; }
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

// Anmeldung
el("anmeldeForm").addEventListener("submit", anmelden);
el("zeigen").addEventListener("click", () => {
  const feld = el("passwort");
  const zeigen = feld.type === "password";
  feld.type = zeigen ? "text" : "password";
  el("zeigen").textContent = zeigen ? "Verbergen" : "Anzeigen";
  el("zeigen").setAttribute("aria-pressed", String(zeigen));
  feld.focus();
});
el("ohneAnmeldung").addEventListener("click", () => appStarten());

// Kontomenü
el("kontoKnopf").addEventListener("click", kontoOeffnen);
el("kontoZu").addEventListener("click", kontoSchliessen);
konto.addEventListener("click", (e) => {
  // Klick auf den abgedunkelten Hintergrund schließt das Menü.
  if (e.target !== konto) return;
  const r = konto.getBoundingClientRect();
  if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) kontoSchliessen();
});
el("meinPlan").addEventListener("click", () => {
  kontoSchliessen();
  if (verwaltungOffen()) {
    if (!startpasswoerterVerwerfen()) return;
    verwaltungLeeren();
    el("verwaltung").hidden = true;
    el("inhalt").hidden = false;
  }
  if (klassenbuchOffen()) {
    if (!klassenbuchSchliessen()) return;
    el("inhalt").hidden = false;
  }
  if (fehlzeitenOffen()) {
    fehlzeitenSchliessen();
    el("inhalt").hidden = false;
  }
  state.modus = "lehrer";
  state.werte.lehrer = eigenesKuerzel();
  state.montag = startMontag();
  state.springeZuHeute = true;
  state.fehler = null;
  fuelleAuswahl();
  ladeWoche();
});
el("abmelden").addEventListener("click", () => {
  kontoSchliessen();
  abmelden();
});
el("ueberallAbmelden").addEventListener("click", async () => {
  const b = el("ueberallAbmelden");
  if (!ueberallBestaetigt) {
    ueberallBestaetigt = true;
    b.textContent = "Wirklich überall abmelden? Nochmal tippen";
    b.classList.add("warnung");
    return;
  }
  b.disabled = true;
  kontoFehler("");
  try {
    const resp = await fetch("api/auth/ueberall-abmelden", { method: "POST", cache: "no-store", headers: authKopf() });
    if (resp.ok || resp.status === 401) {
      kontoSchliessen();
      abmelden("Du wurdest auf allen Geräten abgemeldet.");
    } else {
      kontoFehler(`Das hat nicht geklappt (Fehler ${resp.status}).`);
      ueberallZuruecksetzen();
    }
  } catch {
    kontoFehler("Keine Verbindung zum Schulserver – bitte später erneut versuchen.");
    ueberallZuruecksetzen();
  }
});

// Automatisch nachladen – damit veröffentlichte Änderungen ohne ↻ erscheinen:
//  - beim Zurückwechseln ins Browserfenster bzw. in die App,
//  - alle 2 Minuten, solange die Seite sichtbar ist.
// Fokus und Sichtbarkeit melden sich oft gleichzeitig; ein Mindestabstand
// verhindert doppelte Abfragen.
const NACHLADEN_MS = 2 * 60 * 1000;
const MIN_ABSTAND_MS = 10 * 1000;

function stillNachladen() {
  if (el("inhalt").hidden || laedtGerade || document.visibilityState !== "visible") return;
  if (!planUrl(state.montag)) return;
  if (Date.now() - state.geladenUm < MIN_ABSTAND_MS) return;
  ladeWoche({ still: true });
}

window.addEventListener("focus", stillNachladen);
document.addEventListener("visibilitychange", stillNachladen);
setInterval(() => {
  if (Date.now() - state.geladenUm >= NACHLADEN_MS) stillNachladen();
}, 15 * 1000);

window.addEventListener("hashchange", () => {
  if (el("inhalt").hidden) return;
  const adr = hashLesen();
  if (!adr || !erlaubteModi().includes(adr.modus)) return;
  const datum = adr.datum ? montagVon(adr.datum) : state.montag;
  if (adr.modus === state.modus && gleich(adr.wert, state.werte[state.modus]) && datum === state.montag) return;
  state.modus = adr.modus;
  if (adr.wert) state.werte[adr.modus] = adr.wert;
  state.montag = datum;
  if (adr.datum) state.zielDatum = adr.datum;
  state.fehler = null;
  verwerfeAntwort();
  fuelleAuswahl();
  ladeWoche();
});

// An- oder Abmeldung in einem anderen Fenster (dauerhafte Anmeldung): neu laden.
window.addEventListener("storage", (e) => {
  if (e.key !== "anmeldung") return;
  try { if (e.storageArea !== window.localStorage) return; } catch { return; }
  location.reload();
});

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

async function start() {
  kontenInit();
  klassenbuchInit();
  fehlzeitenInit();
  hausaufgabenInit();
  eintraegeInit();
  hilfeInit();
  altlastenEntfernen();
  state.anmeldung = anmeldungLesen();
  state.anmeldungNoetig = lokal.lesen("optionen", { anmeldungNoetig: true }).anmeldungNoetig !== false;
  el("bleiben").checked = alsApp(); // installierte App: meist das eigene Handy

  const optionen = ladeOptionen();
  if (!state.anmeldung) await Promise.race([optionen, new Promise((r) => setTimeout(r, 4000))]);

  if (state.anmeldung && state.anmeldung.mussPasswortAendern) zeigePflicht();
  else if (state.anmeldung || !state.anmeldungNoetig) appStarten();
  else zeigeAnmeldung();
}

start();

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
