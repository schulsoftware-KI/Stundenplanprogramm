"use strict";
// =====================================================================
//  Stundenplan-PWA – Konten (Phase 2, Schritt 5)
//
//  - Passwort ändern (Kontomenü) und Pflicht-Änderung nach Startpasswort:
//    Nach der Anmeldung mit einem Startpasswort bzw. zurückgesetzten Passwort
//    zeigt die App nur „Eigenes Passwort festlegen" (der Server sperrt bis
//    dahin alle übrigen Abfragen).
//  - Kontenverwaltung (Kontomenü „Konten verwalten"):
//      Admin: Konten suchen/filtern, anlegen, bearbeiten, Passwort zurücksetzen,
//             sperren, überall abmelden, löschen; Sammelanlage für Lehrkräfte
//             (aus dem Grundplan) und Schüler (je Klasse/Stufe); Protokoll.
//      Klassenleitung / Stufenkoordination („Schülerpasswörter"): Schülerkonten
//             der eigenen Klasse/Stufe sehen, Passwort zurücksetzen, sperren/entsperren.
//  - Startpasswörter erscheinen nur einmal: als Zugangszettel zum Drucken bzw.
//    als CSV-Datei. Sie werden nirgends gespeichert und beim Verlassen gelöscht.
//
//  Wird VOR app.js geladen und nutzt deren Hilfen (h, el, state, HttpFehler,
//  authKopf, zugriffsFehler, appStarten, abmelden, …) erst zur Laufzeit.
//  Einstieg: kontenInit() aus start() in app.js.
// =====================================================================

const KONTEN_ROLLEN = ["Admin", "Klassenlehrer", "Stufenkoordination"];

const darfKonten = () => !!state.anmeldung && KONTEN_ROLLEN.includes(state.anmeldung.rolle);
const darfVerwalten = () => !!state.anmeldung && state.anmeldung.rolle === "Admin";

const verw = {
  reiter: "konten",
  ladeNr: 0,
  suchTimer: null,
  konto: null,            // im Detaildialog gezeigtes Konto
  formModus: "neu",       // "neu" | "bearbeiten"
  formKonto: null,
  formSchueler: null,     // { id, name, klasse }
  formSuchNr: 0,
  formSuchTimer: null,
  protokollKonto: null,   // { id, name }: Protokoll nur dieses Kontos
  sammel: {
    uebersicht: null,
    vorschau: null,       // Antwort der Vorschau
    anfrage: null,        // { art, gruppen }
    ergebnis: null,       // Antwort des Anlegens (enthält Startpasswörter!)
    gesichert: false,     // gedruckt oder gespeichert
  },
  einzelZettel: null,     // Startpasswort-Dialog: Daten für Zettel/Kopie
};

// ---------------------------------------------------------------------
//  Anfragen und Rückmeldungen
// ---------------------------------------------------------------------

/** Schreibende/lesende API-Anfrage mit Anmeldung. Fehler -> HttpFehler (mit .daten). */
async function senden(methode, url, body) {
  const abbruch = new AbortController();
  const timer = setTimeout(() => abbruch.abort(), 90000); // Sammelanlage kann etwas dauern
  let resp;
  try {
    const kopf = { ...authKopf() };
    if (body !== undefined) kopf["Content-Type"] = "application/json";
    resp = await fetch(url, {
      method: methode,
      cache: "no-store",
      signal: abbruch.signal,
      headers: kopf,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw new Error("Keine Verbindung zum Schulserver – bitte später erneut versuchen.");
  } finally {
    clearTimeout(timer);
  }
  let daten = null;
  if (resp.status !== 204) {
    try { daten = await resp.json(); } catch { /* kein JSON */ }
  }
  if (resp.ok) return daten;
  const f = new HttpFehler(resp.status, (daten && daten.fehler) || "");
  f.daten = daten;
  f.passwortAendern = !!(daten && daten.passwortAendern);
  throw f;
}

/**
 * Text für einen Fehler. null = schon erledigt (abgelaufene Anmeldung bzw.
 * Passwort muss geändert werden – die App hat die passende Maske gezeigt).
 */
function aktionsFehler(f) {
  if (zugriffsFehler(f)) return null;
  if (f instanceof HttpFehler) {
    if (f.text) return f.text;
    if (f.status === 403) return "Dafür fehlt dir die Berechtigung.";
    if (f.status === 404) return "Nicht gefunden – vielleicht wurde das Konto inzwischen gelöscht.";
    return `Das hat nicht geklappt (Fehler ${f.status}).`;
  }
  return (f && f.message) || "Das hat nicht geklappt.";
}

let toastTimer = null;
function toast(text) {
  const t = el("toast");
  t.textContent = text;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, 5000);
}

function dialogAuf(d) {
  if (typeof d.showModal === "function") { if (!d.open) d.showModal(); }
  else d.setAttribute("open", "");
}

function dialogZu(d) {
  if (!d.open) return;
  if (typeof d.close === "function") d.close();
  else { d.removeAttribute("open"); d.dispatchEvent(new Event("close")); }
}

/** Klick auf den abgedunkelten Hintergrund schließt den Dialog. */
function hintergrundSchliesst(d, schliessen) {
  d.addEventListener("click", (e) => {
    if (e.target !== d) return;
    const r = d.getBoundingClientRect();
    if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) schliessen();
  });
}

function fehlerIn(id, text) {
  const p = el(id);
  p.textContent = text || "";
  p.hidden = !text;
}

/** Zweistufige Bestätigung: erster Druck fragt nach, zweiter führt aus. */
function bestaetigen(knopf, frage, aktion) {
  if (knopf.dataset.bestaetigt !== "1") {
    knopf.dataset.bestaetigt = "1";
    knopf.dataset.text = knopf.textContent;
    knopf.textContent = frage;
    knopf.classList.add("warnung");
    return;
  }
  aktion();
}

const datumZeit = (t) => new Date(t).toLocaleString("de-DE",
  { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });

/** „heute 14:05", „gestern 08:10" oder „31.10.2026". */
function kurzZeit(t) {
  const d = new Date(t);
  const tage = Math.round((new Date(heuteIso()) - new Date(isoAus(d))) / 86400000);
  const uhr = d.toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
  if (tage === 0) return `heute ${uhr}`;
  if (tage === 1) return `gestern ${uhr}`;
  return d.toLocaleDateString("de-DE", { day: "2-digit", month: "2-digit", year: "numeric" });
}

/** „Mia Müller" -> „Müller, Mia". */
function nachnameZuerst(name) {
  if (!name) return "";
  const i = name.lastIndexOf(" ");
  return i < 0 ? name : `${name.slice(i + 1)}, ${name.slice(0, i)}`;
}

// ---------------------------------------------------------------------
//  Passwort ändern / festlegen
// ---------------------------------------------------------------------

let pwMerk = "";        // Startpasswort aus der Anmeldung – nur im Arbeitsspeicher
let pwModus = "pflicht"; // "pflicht" | "dialog"

function pwRegel() {
  const r = state.anmeldung && state.anmeldung.passwortRegel;
  if (r && r.minLaenge) return r;
  return { minLaenge: istSchueler() ? 8 : 10, text: "" };
}

function pwRegelZeigen() {
  const r = pwRegel();
  const n = el("pwNeu").value.length;
  const basis = r.text || `Mindestens ${r.minLaenge} Zeichen.`;
  el("pwRegel").textContent = n > 0 && n < r.minLaenge ? `Noch ${r.minLaenge - n} Zeichen. ${basis}` : basis;
}

function pwSichtbar(zeigen) {
  for (const id of ["pwNeu", "pwWdh"]) el(id).type = zeigen ? "text" : "password";
  el("pwZeigen").textContent = zeigen ? "Verbergen" : "Anzeigen";
  el("pwZeigen").setAttribute("aria-pressed", String(zeigen));
}

function pwFormLeeren() {
  for (const id of ["pwAlt", "pwNeu", "pwWdh"]) el(id).value = "";
  pwSichtbar(false);
  fehlerIn("pwFehler", "");
}

function pwFormVorbereiten(modus) {
  pwModus = modus;
  const pflicht = modus === "pflicht";
  el("pwName").value = state.anmeldung ? state.anmeldung.benutzername : "";
  el("pwAltLabel").textContent = pflicht ? "Startpasswort" : "Bisheriges Passwort";
  const altBekannt = pflicht && !!pwMerk;
  el("pwAltFeld").hidden = altBekannt;
  el("pwAlt").value = altBekannt ? pwMerk : "";
  el("pwAbbrechen").textContent = pflicht ? "Abmelden" : "Abbrechen";
  el("pwSpeichern").textContent = pflicht ? "Passwort festlegen" : "Passwort speichern";
  pwRegelZeigen();
}

function pwFokus() {
  const feld = el("pwAltFeld").hidden ? el("pwNeu") : el("pwAlt");
  setTimeout(() => { try { feld.focus({ preventScroll: true }); } catch { /* egal */ } }, 0);
}

/** Pflicht: Nach Startpasswort nur noch „Eigenes Passwort festlegen". */
function zeigePflicht() {
  sitzungNr++;
  ladenAbbrechen();
  alleDialogeZu();
  verwaltungLeeren();
  klassenbuchLeeren();
  el("klassenbuch").hidden = true;
  fehlzeitenLeeren();
  el("fehlzeiten").hidden = true;
  if (state.anmeldung && !state.anmeldung.mussPasswortAendern) {
    state.anmeldung.mussPasswortAendern = true;
    anmeldungSpeichern();
  }
  el("inhalt").hidden = true;
  el("anmeldung").hidden = true;
  el("verwaltung").hidden = true;
  el("pflicht").hidden = false;
  el("aktualisieren").hidden = true;
  el("kontoKnopf").hidden = true;
  el("titel").textContent = "Stundenplan";
  document.title = "Passwort festlegen – Stundenplan";
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  el("pwHeimat").append(el("pwForm"));
  pwFormLeeren();
  pwFormVorbereiten("pflicht");
  pwFokus();
}

function pwDialogOeffnen() {
  kontoSchliessen();
  el("pwDialogPlatz").append(el("pwForm"));
  pwFormLeeren();
  pwFormVorbereiten("dialog");
  dialogAuf(el("pwDialog"));
  pwFokus();
}

function pwFehlerZeigen(text, feldId) {
  fehlerIn("pwFehler", text);
  if (feldId) {
    const feld = el(feldId);
    feld.focus();
    if (feld.select) feld.select();
  }
}

async function pwSpeichern(e) {
  e.preventDefault();
  const alt = el("pwAlt").value;
  const neu = el("pwNeu").value;
  const wdh = el("pwWdh").value;
  const r = pwRegel();
  if (!alt) {
    el("pwAltFeld").hidden = false;
    return pwFehlerZeigen(pwModus === "pflicht" ? "Bitte das Startpasswort eingeben." : "Bitte das bisherige Passwort eingeben.", "pwAlt");
  }
  if (neu.length < r.minLaenge)
    return pwFehlerZeigen(`Das neue Passwort muss mindestens ${r.minLaenge} Zeichen lang sein.`, "pwNeu");
  if (neu !== wdh)
    return pwFehlerZeigen("Die beiden neuen Passwörter stimmen nicht überein.", "pwWdh");

  const knopf = el("pwSpeichern");
  const knopfText = knopf.textContent;
  knopf.disabled = true;
  knopf.textContent = "Speichern …";
  fehlerIn("pwFehler", "");
  try {
    const d = await senden("POST", "api/auth/passwort", {
      altesPasswort: alt,
      neuesPasswort: neu,
      angemeldetBleiben: !!(state.anmeldung && state.anmeldung.dauerhaft),
    });
    anmeldungUebernehmen(d, !!(state.anmeldung && state.anmeldung.dauerhaft));
    pwMerk = "";
    pwFormLeeren();
    if (pwModus === "pflicht") {
      appStarten();
      toast("Dein Passwort ist gespeichert.");
    } else {
      dialogZu(el("pwDialog"));
      toast("Passwort geändert. Auf anderen Geräten bist du jetzt abgemeldet.");
    }
  } catch (f) {
    if (f instanceof HttpFehler && f.status === 400) {
      if (f.daten && f.daten.feld === "alt") {
        pwMerk = "";
        el("pwAltFeld").hidden = false;
        pwFehlerZeigen(f.text, "pwAlt");
      } else {
        pwFehlerZeigen(f.text, "pwNeu");
      }
    } else {
      const t = aktionsFehler(f);
      if (t) pwFehlerZeigen(t);
    }
  } finally {
    knopf.disabled = false;
    knopf.textContent = knopfText;
  }
}

// ---------------------------------------------------------------------
//  Kontenverwaltung: Ansicht öffnen / verlassen
// ---------------------------------------------------------------------

function kontoMenueAnpassen() {
  const k = el("kontenKnopf");
  k.hidden = !darfKonten();
  k.textContent = darfVerwalten() ? "Konten verwalten" : "Schülerpasswörter";
}

const verwaltungOffen = () => !el("verwaltung").hidden;

function zeigeVerwaltung() {
  if (!darfKonten()) return;
  kontoSchliessen();
  if (!klassenbuchSchliessen()) return; // Klassenbuch offen mit Ungespeichertem: abgebrochen
  fehlzeitenSchliessen();
  ladenAbbrechen();
  const admin = darfVerwalten();
  el("inhalt").hidden = true;
  el("verwaltung").hidden = false;
  el("aktualisieren").hidden = false;

  const zust = (state.anmeldung && state.anmeldung.zustaendig) || "";
  const titel = admin ? "Konten verwalten" : zust ? `Schülerkonten ${zust}` : "Schülerkonten";
  el("titel").textContent = titel;
  document.title = `${titel} – Stundenplan`;
  el("stand").textContent = "";
  el("grundplan").textContent = "";

  el("verwReiter").hidden = !admin;
  el("kNeu").hidden = !admin;
  el("kRolle").hidden = !admin;
  el("kSuche").placeholder = admin ? "Suchen: Name, Kürzel, Klasse …" : "Suchen: Name …";
  reiterWaehlen(admin ? verw.reiter : "konten");
  window.scrollTo(0, 0);
}

/** Zurück zum Plan. Ungesicherte Startpasswörter: vorher nachfragen. */
function zurueckZumPlan() {
  if (!startpasswoerterVerwerfen()) return;
  verwaltungLeeren();
  el("verwaltung").hidden = true;
  el("inhalt").hidden = false;
  render();
  if (state.plan) ladeWoche({ still: true });
  else ladeWoche();
}

/** true = darf verworfen werden. */
function startpasswoerterVerwerfen() {
  const s = verw.sammel;
  if (!s.ergebnis || s.gesichert || !s.ergebnis.angelegt) return true;
  return window.confirm("Die Startpasswörter wurden noch nicht gedruckt oder gespeichert. " +
    "Sie sind danach nicht mehr abrufbar (nur durch Zurücksetzen). Trotzdem schließen?");
}

/** Alles Personenbezogene der Verwaltung aus der Seite entfernen (beim Verlassen/Abmelden). */
function verwaltungLeeren() {
  verw.ladeNr++;
  verw.konto = null;
  verw.formKonto = null;
  verw.formSchueler = null;
  verw.protokollKonto = null;
  verw.einzelZettel = null;
  verw.sammel = { uebersicht: null, vorschau: null, anfrage: null, ergebnis: null, gesichert: false };
  for (const id of ["kListe", "pListe", "sZeilen", "sErgebnisZeilen", "sGruppen", "druck", "kfTreffer"])
    el(id).replaceChildren();
  for (const id of ["kInfo", "pInfo", "spName", "spPasswort", "spFuer", "sErgebnis", "sZusammenfassung"])
    el(id).textContent = "";
  el("kSuche").value = "";
  el("kRolle").value = "";
  el("kFilter").value = "";
  el("sSchritt1").hidden = false;
  el("sSchritt2").hidden = true;
  el("sSchritt3").hidden = true;
}

function alleDialogeZu() {
  for (const id of ["kDialog", "kfDialog", "spDialog", "pwDialog", "hiDialog"]) dialogZu(el(id));
  kontoSchliessen();
}

function reiterWaehlen(reiter) {
  verw.reiter = reiter;
  for (const b of document.querySelectorAll("#verwReiter [data-reiter]"))
    b.setAttribute("aria-selected", String(b.dataset.reiter === reiter));
  el("vKonten").hidden = reiter !== "konten";
  el("vSammel").hidden = reiter !== "sammel";
  el("vProtokoll").hidden = reiter !== "protokoll";
  verwaltungAktualisieren();
}

function verwaltungAktualisieren() {
  if (verw.reiter === "konten") kontenLaden();
  else if (verw.reiter === "sammel") sammelStart();
  else protokollLaden();
}

// ---------------------------------------------------------------------
//  Kontenliste
// ---------------------------------------------------------------------

async function kontenLaden() {
  const nr = ++verw.ladeNr;
  const p = new URLSearchParams({ limit: "200" });
  const q = el("kSuche").value.trim();
  if (q) p.set("q", q);
  if (el("kRolle").value && darfVerwalten()) p.set("rolle", el("kRolle").value);
  if (el("kFilter").value) p.set("filter", el("kFilter").value);
  el("kInfo").textContent = "Lade …";
  try {
    const d = await senden("GET", "api/konten?" + p.toString());
    if (nr !== verw.ladeNr) return;
    renderKonten(d);
  } catch (f) {
    if (nr !== verw.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) {
      el("kInfo").textContent = t;
      el("kListe").replaceChildren();
    }
  }
}

function renderKonten(d) {
  const liste = d.konten || [];
  let info;
  if (!darfVerwalten() && !(state.anmeldung && state.anmeldung.zustaendig))
    info = "Deinem Konto ist noch keine Klasse bzw. Stufe zugeordnet. Bitte wende dich an die Verwaltung.";
  else if (liste.length === 0)
    info = el("kSuche").value.trim() || el("kFilter").value || el("kRolle").value
      ? "Keine passenden Konten gefunden." : "Noch keine Konten.";
  else if (d.mehr)
    info = `${d.gesamt} Konten – die ersten ${liste.length} werden gezeigt. Bitte genauer suchen.`;
  else
    info = liste.length === 1 ? "1 Konto" : `${liste.length} Konten`;
  el("kInfo").textContent = info;
  el("kListe").replaceChildren(...liste.map(kontoZeile));
}

function kontoTitel(k) {
  if (k.rolle !== "Schueler") return k.benutzername;
  return k.schueler ? nachnameZuerst(k.schueler) : `${k.benutzername} (Schüler fehlt)`;
}

function kontoMarken(k) {
  const m = [];
  if (k.ich) m.push({ text: "ich", art: "ich" });
  if (k.gesperrt) m.push({ text: "gesperrt", art: "gesperrt" });
  if (k.mussPasswortAendern) m.push({ text: "Startpasswort", art: "start" });
  if (k.verwaist) m.push({ text: "Schüler fehlt", art: "gesperrt" });
  return m;
}

function kontoZeile(k) {
  const schueler = k.rolle === "Schueler";
  const rechts = schueler ? (k.klasse || "")
    : k.lehrerKuerzel && !gleich(k.lehrerKuerzel, k.benutzername) ? k.lehrerKuerzel : "";
  const unten = [];
  if (schueler) unten.push(k.benutzername);
  else {
    unten.push(k.rollenText);
    if (k.zustaendig) unten.push(k.zustaendig);
  }
  unten.push(k.letzteAnmeldung ? `zuletzt ${kurzZeit(k.letzteAnmeldung)}` : "nie angemeldet");
  const marken = kontoMarken(k);
  return h("li", null,
    h("button", { type: "button", class: "k-zeile" + (k.gesperrt ? " gesperrt" : ""), onclick: () => kontoDetail(k) },
      h("span", { class: "k-oben" },
        h("span", { class: "k-name" }, kontoTitel(k)),
        rechts ? h("span", { class: "k-rechts" }, rechts) : null),
      h("span", { class: "k-unten" }, unten.join(" · ")),
      marken.length ? h("span", { class: "k-marken" }, marken.map((m) => h("span", { class: "marke " + m.art }, m.text))) : null));
}

// ---------------------------------------------------------------------
//  Konto: Details und Aktionen
// ---------------------------------------------------------------------

function kontoDetail(k) {
  verw.konto = k;
  const admin = darfVerwalten();
  el("kdName").textContent = kontoTitel(k);

  const zeilen = [["Benutzername", k.benutzername], ["Rolle", k.rollenText]];
  if (k.lehrerKuerzel) zeilen.push(["Kürzel", k.lehrerKuerzel]);
  if (k.zustaendig) zeilen.push(["Zuständig", k.zustaendig]);
  if (k.rolle === "Schueler") zeilen.push(["Klasse/Stufe", k.verwaist ? "– (Schülerdatensatz fehlt)" : (k.klasse || "–")]);
  zeilen.push(["Angelegt", k.angelegtAm ? datumZeit(k.angelegtAm) : "unbekannt"]);
  zeilen.push(["Zuletzt angemeldet", k.letzteAnmeldung ? datumZeit(k.letzteAnmeldung) : "nie"]);
  zeilen.push(["Passwort", k.mussPasswortAendern
    ? "Startpasswort – noch kein eigenes festgelegt"
    : k.passwortGeaendertAm ? `geändert ${datumZeit(k.passwortGeaendertAm)}` : "eigenes Passwort"]);
  if (k.gesperrt) zeilen.push(["Status", "gesperrt – Anmeldung nicht möglich"]);
  el("kdDaten").replaceChildren(...zeilen.flatMap(([a, b]) => [h("dt", null, a), h("dd", null, b)]));

  const aktionen = [];
  const knopf = (text, klasse, onclick) => h("button", { type: "button", class: "menuepunkt" + (klasse ? " " + klasse : ""), onclick }, text);
  if (k.ich) {
    aktionen.push(h("p", { class: "kd-hinweis" }, "Dein eigenes Passwort änderst du über „Passwort ändern“ im Kontomenü."));
  } else {
    aktionen.push(knopf("Passwort zurücksetzen", null, (e) =>
      bestaetigen(e.currentTarget, "Neues Startpasswort erzeugen? Nochmal tippen", () =>
        kontoAktion(e.currentTarget, "POST", `api/konten/${k.id}/passwort`, (d) => {
          dialogZu(el("kDialog"));
          startpasswortZeigen(d.konto, d.startPasswort);
          kontenLaden();
        }))));
  }
  if (admin) aktionen.push(knopf("Bearbeiten", null, () => formular("bearbeiten", k)));
  if (admin && !k.ich) {
    aktionen.push(knopf("Auf allen Geräten abmelden", null, (e) =>
      bestaetigen(e.currentTarget, "Wirklich überall abmelden? Nochmal tippen", () =>
        kontoAktion(e.currentTarget, "POST", `api/konten/${k.id}/abmelden`, (d) => {
          toast(`${d.benutzername} ist auf allen Geräten abgemeldet.`);
          kontoDetail(d);
        }))));
  }
  // Sperren/Entsperren: Admin; Klassenleitung/Stufenkoordination für ihre Schülerkonten.
  if (!k.ich) {
    if (k.gesperrt) {
      aktionen.push(knopf("Entsperren", null, (e) =>
        kontoAktion(e.currentTarget, "POST", `api/konten/${k.id}/entsperren`, (d) => {
          toast(`${d.benutzername} ist entsperrt.`);
          kontoDetail(d);
          kontenLaden();
        })));
    } else {
      aktionen.push(knopf("Sperren", null, (e) =>
        bestaetigen(e.currentTarget, "Wirklich sperren? Nochmal tippen", () =>
          kontoAktion(e.currentTarget, "POST", `api/konten/${k.id}/sperren`, (d) => {
            toast(`${d.benutzername} ist gesperrt.`);
            kontoDetail(d);
            kontenLaden();
          }))));
    }
  }
  if (admin) {
    if (!k.ich) {
      aktionen.push(knopf("Konto löschen", "leise", (e) =>
        bestaetigen(e.currentTarget, "Endgültig löschen? Nochmal tippen", () =>
          kontoAktion(e.currentTarget, "DELETE", `api/konten/${k.id}`, () => {
            dialogZu(el("kDialog"));
            toast(`Konto ${k.benutzername} gelöscht.`);
            kontenLaden();
          }))));
    }
    aktionen.push(knopf("Protokoll dieses Kontos", "leise", () => {
      dialogZu(el("kDialog"));
      verw.protokollKonto = { id: k.id, name: k.benutzername };
      reiterWaehlen("protokoll");
    }));
  }
  el("kdAktionen").replaceChildren(...aktionen);
  fehlerIn("kdFehler", "");
  dialogAuf(el("kDialog"));
}

async function kontoAktion(knopf, methode, url, erfolg) {
  knopf.disabled = true;
  fehlerIn("kdFehler", "");
  try {
    const d = await senden(methode, url);
    erfolg(d);
  } catch (f) {
    const t = aktionsFehler(f);
    if (t) fehlerIn("kdFehler", t);
    if (knopf.dataset.text) {
      knopf.textContent = knopf.dataset.text;
      knopf.classList.remove("warnung");
      knopf.dataset.bestaetigt = "";
    }
  } finally {
    knopf.disabled = false;
  }
}

// ---------------------------------------------------------------------
//  Startpasswort (ein Konto)
// ---------------------------------------------------------------------

function zettelDaten(konto, passwort) {
  return {
    name: konto.rolle === "Schueler" ? (konto.schueler || konto.benutzername) : konto.benutzername,
    gruppe: konto.rolle === "Schueler" ? (konto.klasse || "") : konto.rollenText,
    benutzername: konto.benutzername,
    passwort,
  };
}

function startpasswortZeigen(konto, passwort) {
  const z = zettelDaten(konto, passwort);
  verw.einzelZettel = z;
  el("spFuer").textContent = z.gruppe ? `${z.name} · ${z.gruppe}` : z.name;
  el("spName").textContent = z.benutzername;
  el("spPasswort").textContent = passwort;
  el("spKopieren").textContent = "Kopieren";
  dialogAuf(el("spDialog"));
}

// ---------------------------------------------------------------------
//  Konto anlegen / bearbeiten
// ---------------------------------------------------------------------

async function auswahlListen() {
  // Lehrerkürzel und Klassen/Stufen als Vorschläge (aus dem gültigen Grundplan).
  try {
    if (!state.auswahl) {
      const { daten } = await holen(`api/woche/auswahl?datum=${state.montag || heuteIso()}`);
      state.auswahl = daten;
    }
  } catch { return; }
  const a = state.auswahl;
  if (!a) return;
  el("kfLehrerListe").replaceChildren(...(a.lehrer || []).map((l) => h("option", { value: l })));
  el("kfGruppenListe").replaceChildren(...[...(a.klassen || []), ...(a.stufen || [])].map((g) => h("option", { value: g })));
}

function formular(modus, k) {
  dialogZu(el("kDialog"));
  verw.formModus = modus;
  verw.formKonto = k || null;
  verw.formSchueler = k && k.rolle === "Schueler" && k.schuelerId
    ? { id: k.schuelerId, name: k.schueler || "", klasse: k.klasse || "" } : null;

  const neu = modus === "neu";
  el("kfTitel").textContent = neu ? "Neues Konto" : `Konto ${k.benutzername} bearbeiten`;
  el("kfSpeichern").textContent = neu ? "Anlegen" : "Speichern";
  el("kfNameHinweis").hidden = !neu;
  el("kfName").placeholder = neu ? "automatisch" : "";

  const rolle = el("kfRolle");
  for (const o of rolle.options) {
    o.disabled = !neu && ((o.value === "Schueler") !== (k.rolle === "Schueler"));
  }
  rolle.value = k ? k.rolle : "Lehrer";
  rolle.disabled = !neu && k.ich; // eigene Admin-Rolle nicht entziehen

  el("kfKuerzel").value = (k && k.lehrerKuerzel) || "";
  el("kfZustaendig").value = (k && k.zustaendig) || "";
  el("kfName").value = k ? k.benutzername : "";
  el("kfSchuelerSuche").value = "";
  el("kfTreffer").replaceChildren();
  el("kfTreffer").hidden = true;
  el("kfSchuelerInfo").textContent = "";
  fehlerIn("kfFehler", "");
  felderAnpassen();
  auswahlListen();
  dialogAuf(el("kfDialog"));
  setTimeout(() => { try { (neu ? rolle : el("kfName")).focus(); } catch { /* egal */ } }, 0);
}

function felderAnpassen() {
  const r = el("kfRolle").value;
  el("kfSchuelerFeld").hidden = r !== "Schueler";
  el("kfKuerzelFeld").hidden = r === "Schueler";
  el("kfZustaendigFeld").hidden = !(r === "Klassenlehrer" || r === "Stufenkoordination");
  el("kfZustaendigLabel").textContent = r === "Stufenkoordination" ? "Zuständig für Stufe" : "Zuständig für Klasse";
  el("kfZustaendig").placeholder = r === "Stufenkoordination" ? "z. B. Q1 (mehrere mit Komma)" : "z. B. 05A (mehrere mit Komma)";
  el("kfKuerzel").previousElementSibling.textContent = r === "Admin" ? "Lehrerkürzel (falls Lehrkraft)" : "Lehrerkürzel";
  const g = verw.formSchueler;
  const gew = el("kfSchuelerGewaehlt");
  gew.hidden = !g;
  gew.replaceChildren();
  if (g) gew.append(h("span", { class: "g-name" }, nachnameZuerst(g.name) || `Schüler ${g.id}`), g.klasse ? h("span", { class: "g-klasse" }, g.klasse) : null);
}

function formSuche(sofort) {
  clearTimeout(verw.formSuchTimer);
  verw.formSuchNr++;
  const q = el("kfSchuelerSuche").value.trim();
  if (q.length < 2) {
    el("kfTreffer").replaceChildren();
    el("kfTreffer").hidden = true;
    el("kfSchuelerInfo").textContent = q.length ? "Mindestens zwei Buchstaben eingeben." : "";
    return;
  }
  verw.formSuchTimer = setTimeout(async () => {
    const nr = ++verw.formSuchNr;
    el("kfSchuelerInfo").textContent = "Suche …";
    try {
      const liste = await senden("GET", `api/schueler?q=${encodeURIComponent(q)}&limit=11`);
      if (nr !== verw.formSuchNr) return;
      const zeigen = (liste || []).slice(0, 10);
      el("kfSchuelerInfo").textContent = zeigen.length === 0 ? "Niemand gefunden."
        : liste.length > 10 ? "Mehr als 10 Treffer – bitte genauer suchen." : "";
      el("kfTreffer").replaceChildren(...zeigen.map((s) => h("li", null,
        h("button", {
          type: "button", class: "treffer-knopf",
          onclick: () => {
            verw.formSchueler = { id: s.id, name: `${s.vorname} ${s.nachname}`, klasse: s.klasse || s.stufe || "" };
            el("kfSchuelerSuche").value = "";
            el("kfTreffer").replaceChildren();
            el("kfTreffer").hidden = true;
            el("kfSchuelerInfo").textContent = "";
            felderAnpassen();
            el("kfName").focus();
          },
        },
        h("span", { class: "t-name" }, `${s.nachname}, ${s.vorname}`),
        h("span", { class: "t-klasse" }, s.klasse || s.stufe || "")))));
      el("kfTreffer").hidden = zeigen.length === 0;
    } catch (f) {
      if (nr !== verw.formSuchNr) return;
      const t = aktionsFehler(f);
      if (t) el("kfSchuelerInfo").textContent = t;
    }
  }, sofort ? 0 : 250);
}

async function formSpeichern(e) {
  e.preventDefault();
  const rolle = el("kfRolle").value;
  const neu = verw.formModus === "neu";
  if (rolle === "Schueler" && !verw.formSchueler) {
    fehlerIn("kfFehler", "Bitte zuerst die Schülerin bzw. den Schüler suchen und auswählen.");
    el("kfSchuelerSuche").focus();
    return;
  }
  const body = {
    rolle,
    benutzername: el("kfName").value.trim(),
    lehrerKuerzel: rolle === "Schueler" ? null : el("kfKuerzel").value.trim() || null,
    schuelerId: rolle === "Schueler" ? verw.formSchueler.id : null,
    zustaendig: rolle === "Klassenlehrer" || rolle === "Stufenkoordination" ? el("kfZustaendig").value.trim() : null,
  };
  const knopf = el("kfSpeichern");
  knopf.disabled = true;
  fehlerIn("kfFehler", "");
  try {
    if (neu) {
      const d = await senden("POST", "api/konten", body);
      dialogZu(el("kfDialog"));
      startpasswortZeigen(d.konto, d.startPasswort);
    } else {
      const d = await senden("PUT", `api/konten/${verw.formKonto.id}`, body);
      dialogZu(el("kfDialog"));
      if (d.ich && (d.benutzername !== verw.formKonto.benutzername || d.lehrerKuerzel !== verw.formKonto.lehrerKuerzel)) {
        abmelden("Dein Konto wurde geändert. Bitte melde dich neu an.");
        return;
      }
      toast("Gespeichert.");
    }
    kontenLaden();
  } catch (f) {
    const t = aktionsFehler(f);
    if (t) {
      fehlerIn("kfFehler", t);
      const feld = f.daten && f.daten.feld;
      const ziel = { benutzername: "kfName", kuerzel: "kfKuerzel", zustaendig: "kfZustaendig", schueler: "kfSchuelerSuche", rolle: "kfRolle" }[feld];
      if (ziel && !el(ziel).closest("[hidden]")) el(ziel).focus();
    }
  } finally {
    knopf.disabled = false;
  }
}

// ---------------------------------------------------------------------
//  Sammelanlage
// ---------------------------------------------------------------------

const sammelArt = () => (document.querySelector('input[name="sArt"]:checked') || {}).value || "Lehrer";

function sammelSchritt(n) {
  el("sSchritt1").hidden = n !== 1;
  el("sSchritt2").hidden = n !== 2;
  el("sSchritt3").hidden = n !== 3;
}

async function sammelStart() {
  const s = verw.sammel;
  if (s.ergebnis) { sammelSchritt(3); return; }
  if (s.vorschau) { sammelSchritt(2); return; }
  sammelSchritt(1);
  fehlerIn("sFehler", "");
  try {
    const d = await senden("GET", "api/konten/sammel/uebersicht");
    s.uebersicht = d;
    renderSammelUebersicht();
  } catch (f) {
    const t = aktionsFehler(f);
    if (t) fehlerIn("sFehler", t);
  }
}

function renderSammelUebersicht() {
  const u = verw.sammel.uebersicht;
  if (!u) return;
  el("sLehrerText").textContent = u.grundplan
    ? `Lehrkräfte aus dem Grundplan (${u.lehrkraefte}, davon ${u.lehrkraefteMitKonto} mit Konto)`
    : "Lehrkräfte (noch kein Grundplan veröffentlicht)";
  const gewaehlt = new Set([...document.querySelectorAll("#sGruppen input:checked")].map((i) => i.value));
  const box = el("sGruppen");
  if (u.gruppen.length === 0) {
    box.replaceChildren(h("p", { class: "such-info" }, "Noch keine Schülerdaten vorhanden."));
  } else {
    const teil = (art, titel) => {
      const liste = u.gruppen.filter((g) => g.art === art);
      if (liste.length === 0) return null;
      return h("div", { class: "gruppen-teil" },
        h("p", { class: "gruppen-titel" }, titel),
        h("div", { class: "gruppen-raster" }, liste.map((g) =>
          h("label", { class: "gruppe-wahl" + (g.mitKonto >= g.schueler ? " fertig" : "") },
            h("input", { type: "checkbox", value: g.gruppe, checked: gewaehlt.has(g.gruppe) }),
            h("span", { class: "g-name" }, g.gruppe),
            h("span", { class: "g-zahl" }, g.mitKonto >= g.schueler ? "alle mit Konto" : `${g.schueler - g.mitKonto} ohne Konto`)))));
    };
    box.replaceChildren(
      h("div", { class: "knopfreihe klein" },
        h("button", { type: "button", class: "textknopf klein", onclick: () => gruppenAlle(true) }, "Alle ohne Konto"),
        h("button", { type: "button", class: "textknopf klein", onclick: () => gruppenAlle(false) }, "Keine")),
      teil("Klasse", "Klassen"),
      teil("Stufe", "Oberstufe"));
  }
  el("sGruppenBox").hidden = sammelArt() !== "Schueler";
}

function gruppenAlle(an) {
  for (const i of document.querySelectorAll("#sGruppen input[type=checkbox]"))
    i.checked = an && !i.closest(".gruppe-wahl").classList.contains("fertig");
}

async function sammelVorschau() {
  const art = sammelArt();
  const gruppen = [...document.querySelectorAll("#sGruppen input:checked")].map((i) => i.value);
  if (art === "Schueler" && gruppen.length === 0) {
    fehlerIn("sFehler", "Bitte mindestens eine Klasse oder Stufe auswählen.");
    return;
  }
  const knopf = el("sVorschau");
  knopf.disabled = true;
  fehlerIn("sFehler", "");
  try {
    const d = await senden("POST", "api/konten/sammel", { art, gruppen, nur: null, anlegen: false });
    verw.sammel.vorschau = d;
    verw.sammel.anfrage = { art, gruppen };
    renderSammelVorschau();
    sammelSchritt(2);
    el("sTitel").focus();
  } catch (f) {
    const t = aktionsFehler(f);
    if (t) fehlerIn("sFehler", t);
  } finally {
    knopf.disabled = false;
  }
}

const STATUS_SAMMEL = { neu: "wird angelegt", vorhanden: "hat schon ein Konto", konflikt: "nicht möglich", angelegt: "angelegt" };

function renderSammelVorschau() {
  const d = verw.sammel.vorschau;
  const lehrer = d.art === "Lehrer";
  el("sTitel").textContent = lehrer ? "Vorschau: Lehrkräfte" : `Vorschau: ${verw.sammel.anfrage.gruppen.join(", ")}`;
  const teile = [`${d.neu} neu`, `${d.vorhanden} schon vorhanden`];
  if (d.konflikte) teile.push(`${d.konflikte} nicht möglich`);
  el("sZusammenfassung").textContent = teile.join(" · ") + (d.grundplan ? ` · Grundplan ${d.grundplan}` : "");
  el("sZeilen").replaceChildren(...d.zeilen.map((z) => h("tr", { class: "z-" + z.status },
    h("td", { class: "spalte-wahl" }, z.status === "neu"
      ? h("input", { type: "checkbox", checked: true, value: z.schluessel, "aria-label": `${z.anzeige} anlegen`, onchange: sammelZaehlen })
      : null),
    h("td", null, z.anzeige, z.gruppe && !lehrer ? h("span", { class: "t-klasse" }, " " + z.gruppe) : null),
    h("td", { class: "mono" }, z.benutzername),
    h("td", null, STATUS_SAMMEL[z.status] || z.status, z.hinweis ? h("span", { class: "z-hinweis" }, z.hinweis) : null))));
  sammelZaehlen();
}

function sammelZaehlen() {
  const n = document.querySelectorAll("#sZeilen input:checked").length;
  const k = el("sAnlegen");
  k.disabled = n === 0;
  k.textContent = n === 1 ? "1 Konto anlegen" : `${n} Konten anlegen`;
}

async function sammelAnlegen() {
  const a = verw.sammel.anfrage;
  const nur = [...document.querySelectorAll("#sZeilen input:checked")].map((i) => i.value);
  if (!a || nur.length === 0) return;
  const knopf = el("sAnlegen");
  knopf.disabled = true;
  knopf.textContent = nur.length > 50 ? `Lege ${nur.length} Konten an … (dauert etwas)` : "Lege an …";
  fehlerIn("sFehler2", "");
  try {
    const d = await senden("POST", "api/konten/sammel", { art: a.art, gruppen: a.gruppen, nur, anlegen: true });
    verw.sammel.ergebnis = d;
    verw.sammel.gesichert = false;
    verw.sammel.vorschau = null;
    renderSammelErgebnis();
    sammelSchritt(3);
  } catch (f) {
    const t = aktionsFehler(f);
    if (t) fehlerIn("sFehler2", t);
    sammelZaehlen();
  }
}

function sammelZettel() {
  const d = verw.sammel.ergebnis;
  if (!d) return [];
  return d.zeilen.filter((z) => z.status === "angelegt").map((z) => ({
    name: d.art === "Lehrer" ? z.anzeige : z.anzeige.split(", ").reverse().join(" "),
    gruppe: d.art === "Lehrer" ? "Lehrkraft" : z.gruppe || "",
    benutzername: z.benutzername,
    passwort: z.startPasswort,
  }));
}

function renderSammelErgebnis() {
  const d = verw.sammel.ergebnis;
  const teile = [`${d.angelegt} ${d.angelegt === 1 ? "Konto" : "Konten"} angelegt`];
  if (d.vorhanden) teile.push(`${d.vorhanden} hatten schon ein Konto`);
  if (d.konflikte) teile.push(`${d.konflikte} nicht möglich`);
  el("sErgebnis").textContent = teile.join(" · ") + ".";
  el("sErgebnisZeilen").replaceChildren(...d.zeilen.filter((z) => z.status === "angelegt").map((z) =>
    h("tr", null,
      h("td", null, z.anzeige, z.gruppe ? h("span", { class: "t-klasse" }, " " + z.gruppe) : null),
      h("td", { class: "mono" }, z.benutzername),
      h("td", { class: "mono passwort-text" }, z.startPasswort))));
}

function sammelFertig() {
  if (!startpasswoerterVerwerfen()) return;
  verw.sammel = { uebersicht: null, vorschau: null, anfrage: null, ergebnis: null, gesichert: false };
  el("sErgebnisZeilen").replaceChildren();
  el("sZeilen").replaceChildren();
  sammelStart();
}

// ---------------------------------------------------------------------
//  Protokoll
// ---------------------------------------------------------------------

async function protokollLaden() {
  const nr = ++verw.ladeNr;
  const k = verw.protokollKonto;
  el("pInfo").replaceChildren("Lade …");
  try {
    const liste = await senden("GET", `api/konten/protokoll?limit=300${k ? "&konto=" + k.id : ""}`);
    if (nr !== verw.ladeNr) return;
    const info = [];
    info.push(k ? `Nur Konto ${k.name}: ` : "");
    info.push(liste.length === 0 ? "Keine Einträge." : liste.length === 1 ? "1 Eintrag" : `${liste.length} Einträge`);
    el("pInfo").replaceChildren(info.join(""), k ? " " : "", k
      ? h("button", { type: "button", class: "textknopf klein", onclick: () => { verw.protokollKonto = null; protokollLaden(); } }, "Alle zeigen")
      : "");
    el("pListe").replaceChildren(...liste.map((p) => h("li", { class: "p-eintrag" },
      h("span", { class: "p-zeit" }, datumZeit(p.zeit)),
      h("span", { class: "p-text" },
        h("strong", null, p.aktion), " · ", p.konto,
        h("span", { class: "p-wer" }, ` (durch ${p.akteur})`)),
      p.details ? h("span", { class: "p-details" }, p.details) : null)));
  } catch (f) {
    if (nr !== verw.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) { el("pInfo").textContent = t; el("pListe").replaceChildren(); }
  }
}

// ---------------------------------------------------------------------
//  Zugangszettel drucken, CSV speichern
// ---------------------------------------------------------------------

function zettelDrucken(liste) {
  if (!liste.length) return;
  const adresse = location.origin + location.pathname.replace(/index\.html$/, "");
  el("druck").replaceChildren(...liste.map((z) => h("article", { class: "zettel" },
    h("p", { class: "z-titel" }, "Zugang zum Stundenplan"),
    h("p", { class: "z-name" }, z.name, z.gruppe ? h("span", { class: "z-gruppe" }, ` · ${z.gruppe}`) : null),
    h("dl", null,
      h("dt", null, "Adresse"), h("dd", null, adresse),
      h("dt", null, "Benutzername"), h("dd", { class: "z-wert" }, z.benutzername),
      h("dt", null, "Startpasswort"), h("dd", { class: "z-wert" }, z.passwort)),
    h("p", { class: "z-hinweis" },
      "Beim ersten Anmelden legst du ein eigenes Passwort fest. Danach diesen Zettel vernichten."))));
  document.body.classList.add("druckt");
  window.print();
  // Manche Browser kehren erst nach dem Druckdialog zurück, andere sofort – aufgeräumt wird bei „afterprint".
}

function druckAufraeumen() {
  document.body.classList.remove("druckt");
  el("druck").replaceChildren();
}

function csvSpeichern(liste, dateiname) {
  if (!liste.length) return;
  const zelle = (v) => {
    let s = String(v === null || v === undefined ? "" : v);
    if (/^[=+\-@\t\r]/.test(s)) s = "'" + s; // keine Formeln in Excel
    return /[;"\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const zeilen = [["Name", "Klasse/Stufe", "Benutzername", "Startpasswort"],
    ...liste.map((z) => [z.name, z.gruppe, z.benutzername, z.passwort])];
  const text = "﻿" + zeilen.map((r) => r.map(zelle).join(";")).join("\r\n") + "\r\n";
  const url = URL.createObjectURL(new Blob([text], { type: "text/csv;charset=utf-8" }));
  const a = h("a", { href: url, download: dateiname, hidden: true });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 15000);
}

// ---------------------------------------------------------------------
//  Bedienung verdrahten (aus start() in app.js)
// ---------------------------------------------------------------------

function kontenInit() {
  // Passwort
  el("pwForm").addEventListener("submit", pwSpeichern);
  el("pwNeu").addEventListener("input", pwRegelZeigen);
  el("pwZeigen").addEventListener("click", () => {
    pwSichtbar(el("pwNeu").type === "password");
    el("pwNeu").focus();
  });
  el("pwAbbrechen").addEventListener("click", () => {
    if (pwModus === "pflicht") { pwMerk = ""; abmelden(); }
    else dialogZu(el("pwDialog"));
  });
  el("pwDialog").addEventListener("close", () => {
    pwFormLeeren();
    el("pwHeimat").append(el("pwForm"));
  });
  hintergrundSchliesst(el("pwDialog"), () => dialogZu(el("pwDialog")));
  el("pwAendernKnopf").addEventListener("click", pwDialogOeffnen);

  // Verwaltung
  el("kontenKnopf").addEventListener("click", zeigeVerwaltung);
  el("verwZurueck").addEventListener("click", zurueckZumPlan);
  for (const b of document.querySelectorAll("#verwReiter [data-reiter]"))
    b.addEventListener("click", () => { if (verw.reiter !== b.dataset.reiter) reiterWaehlen(b.dataset.reiter); });
  el("verwReiter").addEventListener("keydown", (e) => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
    const liste = ["konten", "sammel", "protokoll"];
    const i = (liste.indexOf(verw.reiter) + (e.key === "ArrowRight" ? 1 : liste.length - 1)) % liste.length;
    reiterWaehlen(liste[i]);
    document.querySelector(`#verwReiter [data-reiter="${liste[i]}"]`).focus();
  });

  el("kSuche").addEventListener("input", () => {
    clearTimeout(verw.suchTimer);
    verw.suchTimer = setTimeout(kontenLaden, 250);
  });
  el("kSuche").addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); clearTimeout(verw.suchTimer); kontenLaden(); } });
  el("kRolle").addEventListener("change", kontenLaden);
  el("kFilter").addEventListener("change", kontenLaden);
  el("kNeu").addEventListener("click", () => formular("neu"));

  el("kdZu").addEventListener("click", () => dialogZu(el("kDialog")));
  hintergrundSchliesst(el("kDialog"), () => dialogZu(el("kDialog")));

  el("kfForm").addEventListener("submit", formSpeichern);
  el("kfRolle").addEventListener("change", () => { fehlerIn("kfFehler", ""); felderAnpassen(); });
  el("kfAbbrechen").addEventListener("click", () => dialogZu(el("kfDialog")));
  el("kfSchuelerSuche").addEventListener("input", () => formSuche(false));
  el("kfSchuelerSuche").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); formSuche(true); }
    else if (e.key === "ArrowDown") {
      const erster = el("kfTreffer").querySelector("button");
      if (erster) { e.preventDefault(); erster.focus(); }
    }
  });
  el("kfTreffer").addEventListener("keydown", (e) => {
    const knoepfe = [...el("kfTreffer").querySelectorAll("button")];
    const i = knoepfe.indexOf(document.activeElement);
    if (i < 0) return;
    if (e.key === "ArrowDown" && i < knoepfe.length - 1) { e.preventDefault(); knoepfe[i + 1].focus(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); (i > 0 ? knoepfe[i - 1] : el("kfSchuelerSuche")).focus(); }
  });

  // Startpasswort-Dialog
  el("spDrucken").addEventListener("click", () => { if (verw.einzelZettel) zettelDrucken([verw.einzelZettel]); });
  el("spKopieren").addEventListener("click", async () => {
    const z = verw.einzelZettel;
    if (!z) return;
    try {
      await navigator.clipboard.writeText(`Benutzername: ${z.benutzername}\nStartpasswort: ${z.passwort}`);
      el("spKopieren").textContent = "Kopiert";
    } catch {
      el("spKopieren").textContent = "Kopieren nicht möglich";
    }
  });
  const spLeeren = () => {
    verw.einzelZettel = null;
    el("spName").textContent = "";
    el("spPasswort").textContent = "";
    el("spFuer").textContent = "";
  };
  el("spFertig").addEventListener("click", () => { spLeeren(); dialogZu(el("spDialog")); });
  el("spDialog").addEventListener("close", spLeeren); // auch bei Esc

  // Sammelanlage
  for (const r of document.querySelectorAll('input[name="sArt"]'))
    r.addEventListener("change", () => { el("sGruppenBox").hidden = sammelArt() !== "Schueler"; fehlerIn("sFehler", ""); });
  el("sVorschau").addEventListener("click", sammelVorschau);
  el("sZurueck").addEventListener("click", () => {
    verw.sammel.vorschau = null;
    el("sZeilen").replaceChildren();
    sammelSchritt(1);
    renderSammelUebersicht();
  });
  el("sAnlegen").addEventListener("click", sammelAnlegen);
  el("sDrucken").addEventListener("click", () => {
    const liste = sammelZettel();
    if (!liste.length) return;
    verw.sammel.gesichert = true;
    zettelDrucken(liste);
  });
  el("sCsv").addEventListener("click", () => {
    const liste = sammelZettel();
    if (!liste.length) return;
    verw.sammel.gesichert = true;
    const art = verw.sammel.ergebnis.art === "Lehrer" ? "Lehrkraefte" : "Schueler";
    csvSpeichern(liste, `Zugangsdaten_${art}_${heuteIso()}.csv`);
  });
  el("sFertig").addEventListener("click", sammelFertig);

  window.addEventListener("afterprint", druckAufraeumen);
  // Seite verlassen mit ungesicherten Startpasswörtern: Browser fragt nach.
  window.addEventListener("beforeunload", (e) => {
    const s = verw.sammel;
    if (s.ergebnis && s.ergebnis.angelegt && !s.gesichert) { e.preventDefault(); e.returnValue = ""; }
  });
}
