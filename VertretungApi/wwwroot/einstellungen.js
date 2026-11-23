"use strict";
// =====================================================================
//  Stundenplan-PWA – Schuleinstellungen (P3, E-1; Entscheidungen 138–141, 199–203)
//
//  Kontomenü „Einstellungen“ (nur Admin): Karten je Gruppe (Schule, Schuljahr, Datenschutz, Anmeldung).
//  Jede Einstellung zeigt Hilfetext, „Gilt jetzt“, den Standard und – bei Datenschutzwirkung – die Frage
//  an den DSB mit „abgestimmt am / mit“. Gespeichert wird je Karte; die Änderung wirkt sofort und steht
//  im Reiter „Protokoll“. Ändert sich ein Wert mit Datenschutzwirkung, leert die Karte die bisherige
//  Abstimmung (sie galt für den alten Wert). Löscht eine kürzere Frist Daten, nennt die API vorher die
//  Zahl; „Speichern“ fragt dann ein zweites Mal („Nochmal tippen“).
//
//  Wird nach eintraege.js und VOR hilfe.js/app.js geladen und nutzt deren Hilfen (h, el, state, senden,
//  aktionsFehler, toast, datumZeit, heuteIso, appName, …) erst zur Laufzeit. Einstieg einstellungenInit()
//  aus start() in app.js. Alle Namen mit Präfix „es…“ bzw. „einstellungen…“.
//
//  P4a: Alle Bereiche schließen beim Öffnen die Einstellungen über einstellungenSchliessen() bzw. leeren sie
//  mit einstellungenLeeren() – beide schließen jetzt auch den Jahresablauf (jahresablauf.js, keine Eingaben).
//  Neue Fristen (Abwesenheitsgründe, Konten abgegangener Schüler) baut der Katalog der API von selbst; die
//  Meldung „Gelöscht: …“ nennt alle Zahlen.
//
//  P6a (Entscheidungen 226, 229, 230): In der Karte „Anmeldung“ die Schalter der Pflicht zum zweiten Faktor (baut der
//  Katalog der API). Dazu am Ende die Karte „Zugriff (Einstellungsdatei)“ nur zum Lesen: Schuladressen,
//  Adressbeschränkung für Desktop und Admin, Anfragebegrenzung und wie die Adresse dieses Geräts eingeordnet wird.
//
//  P6b (Entscheidungen 233, 236): Schalter „Leseprotokoll“ und „Grund der Entschuldigung erfassen“ (Karte „Datenschutz“,
//  baut der Katalog der API). Ist „Grund … erfassen“ gespeichert aus, steht darunter „Vorhandene Gründe entfernen …“:
//  zählt zuerst (api/einstellungen/gruende), fragt mit der Zahl nach („Nochmal tippen“) und setzt dann alle Gründe auf
//  „ohne Angabe“ (api/einstellungen/gruende-entfernen mit der Zahl; der Server zählt neu).
// =====================================================================

const es = {
  reiter: "werte",        // "werte" | "protokoll"
  ladeNr: 0,
  daten: null,            // Antwort von GET api/einstellungen
  felder: new Map(),      // schluessel -> { e, eingabe, am, mit, fehler, dsbHinweis, karte }
  karten: new Map(),      // gruppe -> { el, knopf, verwerfen, fehler, hinweis, schluessel: [] }
  bestaetigen: null,      // Gruppe, deren „Speichern“ auf den zweiten Druck wartet (Löschen bestätigen)
};

/** So lange ist „Speichern und löschen? Nochmal tippen“ nach der Rückfrage gesperrt (Doppelklick-Schutz). */
const ES_BESTAETIGEN_SPERRE_MS = 1500;

const esDarf = () => !!state.anmeldung && state.anmeldung.rolle === "Admin";
const einstellungenOffen = () => !el("einstellungen").hidden;

function einstellungenMenueAnpassen() {
  el("esKnopf").hidden = !esDarf();
}

// ---------------------------------------------------------------------
//  Öffnen, schließen, leeren
// ---------------------------------------------------------------------

function esUngespeichert() {
  for (const f of es.felder.values()) if (esFeldGeaendert(f)) return true;
  return false;
}

/** true = geschlossen bzw. war nicht offen; false = abgebrochen (Ungespeichertes behalten). */
function einstellungenSchliessen() {
  jahresablaufZu();   // P4a: Jahresablauf hat nichts zu speichern
  if (!einstellungenOffen()) return true;
  if (esUngespeichert() && !window.confirm("Die Einstellungen wurden noch nicht gespeichert. Änderungen verwerfen?"))
    return false;
  einstellungenLeeren();
  el("einstellungen").hidden = true;
  return true;
}

/** Alles aus der Ansicht entfernen (Verlassen, Abmelden). */
function einstellungenLeeren() {
  jahresablaufZu();   // P4a
  es.ladeNr++;
  es.daten = null;
  es.felder.clear();
  es.karten.clear();
  es.bestaetigen = null;
  el("esGruppen").replaceChildren();
  el("esHinweise").replaceChildren();
  el("esPListe").replaceChildren();
  el("esInfo").textContent = "";
  el("esPInfo").textContent = "";
}

function einstellungenOeffnen() {
  if (!esDarf()) return;
  kontoSchliessen();
  jahresablaufZu();   // P4a
  if (verwaltungOffen()) {
    if (!startpasswoerterVerwerfen()) return;
    verwaltungLeeren();
    el("verwaltung").hidden = true;
  }
  if (!klassenbuchSchliessen()) return;
  fehlzeitenSchliessen();
  hausaufgabenSchliessen();
  ladenAbbrechen();
  el("inhalt").hidden = true;
  el("einstellungen").hidden = false;
  el("aktualisieren").hidden = false;
  el("titel").textContent = "Einstellungen";
  document.title = `Einstellungen – ${appName()}`;
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  esReiterWaehlen(es.reiter);
  window.scrollTo(0, 0);
}

/** „Zum Plan“. */
function einstellungenVerlassen() {
  if (!einstellungenSchliessen()) return;
  el("inhalt").hidden = false;
  render();
  if (state.plan) ladeWoche({ still: true });
  else ladeWoche();
}

function esReiterWaehlen(reiter) {
  es.reiter = reiter;
  for (const b of document.querySelectorAll("#esReiter [data-reiter]"))
    b.setAttribute("aria-selected", String(b.dataset.reiter === reiter));
  el("esWerte").hidden = reiter !== "werte";
  el("esProtokoll").hidden = reiter !== "protokoll";
  if (reiter === "protokoll") esProtokollLaden();
  else if (!es.daten) esLaden();
}

/** Knopf „Aktualisieren“ oben. */
function esAktualisieren() {
  if (es.reiter === "protokoll") { esProtokollLaden(); return; }
  if (esUngespeichert() && !window.confirm("Nicht gespeicherte Änderungen gehen beim Neuladen verloren. Trotzdem neu laden?"))
    return;
  esLaden();
}

// ---------------------------------------------------------------------
//  Laden und Darstellen
// ---------------------------------------------------------------------

async function esLaden() {
  const nr = ++es.ladeNr;
  el("esInfo").textContent = "Lade …";
  try {
    const d = await senden("GET", "api/einstellungen");
    if (nr !== es.ladeNr) return;
    el("esInfo").textContent = "";
    esRender(d);
  } catch (f) {
    if (nr !== es.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) el("esInfo").textContent = t;
  }
}

function esHinweiseZeigen(liste) {
  el("esHinweise").replaceChildren(...(liste || []).map((t) => h("p", { class: "hinweis es-hinweis" }, t)));
}

function esRender(d) {
  es.daten = d;
  es.felder.clear();
  es.karten.clear();
  es.bestaetigen = null;
  esHinweiseZeigen(d.hinweise);
  el("esGruppen").replaceChildren(...d.gruppen.map((g) => esKarte(g, d.einstellungen.filter((e) => e.gruppe === g.id))));
  if (d.zugriff) el("esGruppen").append(esZugriffKarte(d.zugriff));   // P6a
}

/** P6a (229, 230): Zugriff und Anfragebegrenzung aus der Einstellungsdatei – nur zum Lesen. */
function esZugriffKarte(z) {
  const anAus = (b) => (b ? "an" : "aus");
  const zahl = (n, einheit) => (n === 0 ? "unbegrenzt" : `${n} ${einheit}`);
  const b = z.begrenzung || {};
  const schule = z.dieseAdresseSchule === true ? "Schulnetz" : z.dieseAdresseSchule === false ? "nicht im Schulnetz"
    : "keine Schuladressen eingetragen";
  const zeilen = [
    ["Schuladressen", z.schuladressen && z.schuladressen.length ? z.schuladressen.join(", ") : "keine eingetragen"],
    ["Desktop nur aus dem Schulnetz", anAus(z.desktopNurSchule) + (z.desktopNurSchule
      ? " – Veröffentlichen und SchILD-Import gehen nur von den Schuladressen" : "")],
    ["Admin nur aus dem Schulnetz", anAus(z.adminNurSchule) + (z.adminNurSchule
      ? " – Anmeldung und jede Admin-Anfrage nur von den Schuladressen" : "")],
    ["Dieses Gerät", `${z.dieseAdresse} – ${schule}`],
    ["Reverse Proxy", anAus(z.proxyAktiv) + (z.echtbetrieb && !z.proxyAktiv && (z.desktopNurSchule || z.adminNurSchule)
      ? " – Achtung: hinter einem Proxy kämen alle Anfragen von dessen Adresse" : "")],
    ["Anfragebegrenzung", `je Konto ${zahl(b.jeKonto, "je Minute")} · ohne Anmeldung ${zahl(b.ohneAnmeldung, "je Minute und Adresse")}` +
      ` (Schuladressen ausgenommen) · Desktop ${zahl(b.desktop, "je Minute")} · große Abrufe (PDF, CSV, Archiv) ` +
      `${zahl(b.teuerJeKonto, "je Konto")} und ${zahl(b.teuerGesamt, "insgesamt")} gleichzeitig`],
  ];
  return h("section", { class: "karte es-karte es-zugriff", "aria-labelledby": "esT_zugriff" },
    h("h2", { class: "karte-titel", id: "esT_zugriff" }, "Zugriff (Einstellungsdatei des Servers)"),
    h("p", { class: "kleingedruckt ohne-einzug" }, "Nur zum Lesen: Diese Werte stehen in der Einstellungsdatei des Servers " +
      "(appsettings.Production.json, Abschnitte „Zugriff“ und „Begrenzung“) und gelten nach einem Neustart. In der App " +
      "lassen sie sich nicht ändern – ein falscher Wert könnte sonst genau den Admin aussperren, der ihn ändern müsste."),
    h("dl", { class: "kd-daten es-zugriff-daten" }, zeilen.flatMap(([a, w]) => [h("dt", null, a), h("dd", null, w)])));
}

/** Eine Karte nach dem Speichern neu aufbauen (die anderen behalten ihre Eingaben). */
function esKarteErsetzen(d, gruppe) {
  es.daten = d;
  const alt = es.karten.get(gruppe);
  for (const [k, f] of [...es.felder]) if (f.karte === gruppe) es.felder.delete(k);
  const g = d.gruppen.find((x) => x.id === gruppe);
  if (!g || !alt) { esRender(d); return; }
  const neu = esKarte(g, d.einstellungen.filter((e) => e.gruppe === gruppe));
  alt.el.replaceWith(neu);
  esHinweiseZeigen(d.hinweise);
}

function esKarte(g, liste) {
  const knopf = h("button", { type: "button", class: "hauptknopf klein", disabled: true }, "Speichern");
  const verwerfen = h("button", { type: "button", class: "textknopf", hidden: true }, "Verwerfen");
  const fehler = h("p", { class: "fehler", role: "alert", hidden: true });
  const hinweis = h("p", { class: "hinweis", role: "status", hidden: true });
  const karte = h("section", { class: "karte es-karte", "aria-labelledby": `esT_${g.id}` },
    h("h2", { class: "karte-titel", id: `esT_${g.id}` }, g.titel),
    h("p", { class: "kleingedruckt ohne-einzug" }, g.text),
    liste.map((e) => esFeld(e, g.id)),
    fehler,
    hinweis,
    h("div", { class: "knopfreihe" }, knopf, verwerfen));
  es.karten.set(g.id, { el: karte, knopf, verwerfen, fehler, hinweis, schluessel: liste.map((e) => e.schluessel) });
  knopf.addEventListener("click", () => esSpeichern(g.id));
  verwerfen.addEventListener("click", () => esVerwerfen(g.id));
  return karte;
}

const esId = (schluessel) => "es_" + schluessel.replace(/[^A-Za-z0-9]/g, "_");

function esFeld(e, gruppe) {
  const id = esId(e.schluessel);
  let eingabe;
  let zeile;
  if (e.typ === "schalter") {
    eingabe = h("input", { type: "checkbox", id });
    eingabe.checked = e.wert === "true";
    zeile = h("label", { class: "schalter gross es-schalter", for: id }, eingabe, h("span", null, e.titel));
  } else {
    const p = { id, class: "eingabe", autocomplete: "off" };
    if (e.typ === "datum") p.type = "date";
    else if (e.typ === "zahl") Object.assign(p, { type: "number", inputmode: "numeric", min: e.min, max: e.max, step: 1 });
    else if (e.typ === "schuljahr") Object.assign(p, { type: "text", placeholder: "2026/27", maxlength: 9, spellcheck: "false" });
    else Object.assign(p, { type: "text", maxlength: e.maxLaenge || 120, spellcheck: "false" });
    eingabe = h("input", p);
    eingabe.value = e.wert || "";
    const standard = h("button", { type: "button", class: "textknopf klein es-standard" }, "Standard");
    standard.addEventListener("click", () => {
      eingabe.value = "";
      eingabe.dispatchEvent(new Event("input", { bubbles: true }));
      eingabe.focus();
    });
    zeile = h("div", null,
      h("label", { class: "feld", for: id }, e.titel, e.einheit ? h("span", { class: "es-einheit" }, ` (${e.einheit})`) : null),
      h("div", { class: "es-eingabe" }, eingabe, standard));
    zeile.querySelector(".es-standard").hidden = !e.wert;
  }

  const fehler = h("p", { class: "fehler klein", role: "alert", hidden: true });
  const info = [
    h("p", { class: "es-info" }, h("strong", null, "Gilt jetzt: "), e.wirksam),
    h("p", { class: "es-info" }, e.hilfe + ` Standard: ${e.standard}.`),
  ];
  if (e.geaendertAm && e.geaendertVon)
    info.push(h("p", { class: "es-info leise" }, `Zuletzt geändert ${datumZeit(e.geaendertAm)} von ${e.geaendertVon}`));

  let am = null, mit = null, dsbHinweis = null, dsb = null;
  if (e.dsbFrage) {
    am = h("input", { type: "date", id: id + "_am", class: "eingabe", max: heuteIso() });
    am.value = e.abgestimmtAm || "";
    mit = h("input", { type: "text", id: id + "_mit", class: "eingabe", maxlength: 120, autocomplete: "off",
      placeholder: "z. B. DSB Frau Muster" });
    mit.value = e.abgestimmtMit || "";
    dsbHinweis = h("p", { class: "es-dsb-hinweis", role: "status", hidden: true },
      "Der Wert ändert sich – die bisherige Abstimmung gilt dafür nicht mehr. Bitte neu eintragen oder leer lassen.");
    dsb = h("div", { class: "es-dsb" },
      h("p", { class: "es-dsb-frage" }, h("strong", null, "Frage an den DSB: "), e.dsbFrage),
      h("p", { class: "es-dsb-stand" }, e.abgestimmtAm
        ? `Abgestimmt am ${kurzDatum(e.abgestimmtAm)} mit ${e.abgestimmtMit || "–"}`
        : h("span", { class: "marke start" }, "noch nicht abgestimmt")),
      h("div", { class: "es-dsb-felder" },
        h("div", null, h("label", { class: "feld", for: id + "_am" }, "Abgestimmt am"), am),
        h("div", null, h("label", { class: "feld", for: id + "_mit" }, "mit"), mit)),
      dsbHinweis);
  }

  const f = { e, eingabe, am, mit, fehler, dsbHinweis, karte: gruppe, geleert: false };
  es.felder.set(e.schluessel, f);
  const anpassen = () => esFeldEingabe(f);
  eingabe.addEventListener(e.typ === "schalter" ? "change" : "input", anpassen);
  if (e.typ === "datum") eingabe.addEventListener("change", anpassen);
  if (am) { am.addEventListener("input", anpassen); am.addEventListener("change", anpassen); mit.addEventListener("input", anpassen); }

  return h("div", { class: "es-feld" + (e.dsbFrage ? " mit-dsb" : "") }, zeile, fehler, info, dsb,
    e.schluessel === ES_GRUND && e.wert === "false" ? esGruendeBlock() : null);
}

// ---------------------------------------------------------------------
//  P6b (Entscheidung 236): vorhandene Gründe der Entschuldigungen entfernen
// ---------------------------------------------------------------------

const ES_GRUND = "Datenschutz.GrundErfassen";

function esGruendeBlock() {
  const knopf = h("button", { type: "button", class: "textknopf es-gruende-knopf" }, "Vorhandene Gründe entfernen …");
  const status = h("p", { class: "es-info", role: "status", hidden: true });
  const fehler = h("p", { class: "fehler klein", role: "alert", hidden: true });
  const zustand = { anzahl: null, ab: 0, laeuft: false };
  const zuruecksetzen = () => {
    zustand.anzahl = null;
    knopf.textContent = "Vorhandene Gründe entfernen …";
    knopf.classList.remove("warnung");
  };
  knopf.addEventListener("click", async () => {
    if (zustand.laeuft) return;
    if (zustand.anzahl !== null && Date.now() < zustand.ab) return;   // Doppelklick-Schutz
    zustand.laeuft = true;
    knopf.disabled = true;
    esText(fehler, "");
    const nr = es.ladeNr;
    try {
      if (zustand.anzahl === null) {
        const d = await senden("GET", "api/einstellungen/gruende");
        if (nr !== es.ladeNr) return;
        if (!d.gesamt) { esText(status, "Es sind keine Gründe gespeichert."); return; }
        esText(status, `Gespeichert sind: ${d.text}. Sie werden auf „ohne Angabe“ gesetzt – die Bewertung „entschuldigt“ ` +
          "bleibt. Das lässt sich nicht rückgängig machen.");
        zustand.anzahl = d.gesamt;
        zustand.ab = Date.now() + ES_BESTAETIGEN_SPERRE_MS;
        knopf.textContent = `${d.gesamt} ${d.gesamt === 1 ? "Grund" : "Gründe"} entfernen? Nochmal tippen`;
        knopf.classList.add("warnung");
      } else {
        const d = await senden("POST", "api/einstellungen/gruende-entfernen", { anzahl: zustand.anzahl });
        if (nr !== es.ladeNr) return;
        zuruecksetzen();
        esText(status, d.meldung || "Erledigt.");
        toast(d.meldung || "Gründe entfernt.");
      }
    } catch (f) {
      if (nr !== es.ladeNr) return;
      if (f instanceof HttpFehler && f.status === 409 && f.daten && f.daten.bestaetigen) {
        zustand.anzahl = f.daten.anzahl;
        zustand.ab = Date.now() + ES_BESTAETIGEN_SPERRE_MS;
        knopf.textContent = `${f.daten.anzahl} ${f.daten.anzahl === 1 ? "Grund" : "Gründe"} entfernen? Nochmal tippen`;
        esText(status, `Gespeichert sind: ${f.daten.text}.`);
      } else {
        zuruecksetzen();
        const t = aktionsFehler(f);
        if (t !== null) esText(fehler, t);
      }
    } finally {
      if (nr === es.ladeNr) {
        zustand.laeuft = false;
        knopf.disabled = false;
      }
    }
  });
  return h("div", { class: "es-gruende" }, knopf, status, fehler);
}

// ---------------------------------------------------------------------
//  Eingaben, Änderungen
// ---------------------------------------------------------------------

const esWert = (f) => f.e.typ === "schalter" ? (f.eingabe.checked ? "true" : "false") : f.eingabe.value.trim();
const esWertVorher = (f) => f.e.typ === "schalter" ? (f.e.wert === "true" ? "true" : "false") : (f.e.wert || "");
const esWertGeaendert = (f) => esWert(f) !== esWertVorher(f);
const esDsbGeaendert = (f) => !!f.am && (f.am.value !== (f.e.abgestimmtAm || "") || f.mit.value.trim() !== (f.e.abgestimmtMit || ""));
const esFeldGeaendert = (f) => esWertGeaendert(f) || esDsbGeaendert(f);

function esFeldEingabe(f) {
  // Neuer Wert mit Datenschutzwirkung: die Abstimmung galt für den alten Wert – Felder leeren (einmal).
  if (f.am) {
    const anders = esWertGeaendert(f);
    if (anders && !f.geleert && (f.e.abgestimmtAm || f.e.abgestimmtMit)) {
      f.am.value = "";
      f.mit.value = "";
      f.geleert = true;
    } else if (!anders && f.geleert && !f.am.value && !f.mit.value) {
      f.am.value = f.e.abgestimmtAm || "";
      f.mit.value = f.e.abgestimmtMit || "";
      f.geleert = false;
    }
    f.dsbHinweis.hidden = !(anders && (f.e.abgestimmtAm || f.e.abgestimmtMit));
  }
  const std = f.eingabe.parentElement && f.eingabe.parentElement.querySelector(".es-standard");
  if (std) std.hidden = !f.eingabe.value;
  f.fehler.hidden = true;
  esKarteAnpassen(f.karte);
}

function esKarteAnpassen(gruppe) {
  const k = es.karten.get(gruppe);
  if (!k) return;
  const geaendert = k.schluessel.some((s) => esFeldGeaendert(es.felder.get(s)));
  k.knopf.disabled = !geaendert;
  k.verwerfen.hidden = !geaendert;
  if (es.bestaetigen === gruppe) {   // Eingabe nach der Rückfrage: neu prüfen lassen
    es.bestaetigen = null;
    k.knopf.textContent = "Speichern";
    k.knopf.classList.remove("warnung");
    esText(k.fehler, "");
  }
}

function esText(p, text) {
  p.textContent = text || "";
  p.hidden = !text;
}

function esVerwerfen(gruppe) {
  const k = es.karten.get(gruppe);
  if (!k || !es.daten) return;
  esKarteErsetzen(es.daten, gruppe);
}

// ---------------------------------------------------------------------
//  Speichern
// ---------------------------------------------------------------------

async function esSpeichern(gruppe) {
  const k = es.karten.get(gruppe);
  if (!k) return;
  const aenderungen = [];
  for (const s of k.schluessel) {
    const f = es.felder.get(s);
    if (!esFeldGeaendert(f)) continue;
    const a = { schluessel: s, wert: esWert(f) };
    if (f.am && (esDsbGeaendert(f) || esWertGeaendert(f))) {
      a.abgestimmtAm = f.am.value;
      a.abgestimmtMit = f.mit.value.trim();
    }
    aenderungen.push(a);
  }
  if (aenderungen.length === 0) return;
  const bestaetigt = es.bestaetigen === gruppe;

  for (const s of k.schluessel) esText(es.felder.get(s).fehler, "");
  esText(k.fehler, "");
  esText(k.hinweis, "");
  k.knopf.disabled = true;
  try {
    const d = await senden("PUT", "api/einstellungen", { aenderungen, bestaetigt });
    es.bestaetigen = null;
    esKarteErsetzen(d.stand, gruppe);
    const neu = es.karten.get(gruppe);
    const teile = [];
    const g = d.geloescht;
    if (g && (g.protokolle || g.eintraege || g.gruende || g.konten)) {
      // P4a: auch Abwesenheitsgründe und Konten abgegangener Schüler
      const zahlen = [`${g.protokolle || 0} Protokolleinträge`, `${g.eintraege || 0} Klassenbucheinträge`];
      if (g.gruende) zahlen.push(`Gründe in ${g.gruende} Vertretungen`);
      if (g.konten) zahlen.push(`${g.konten} Konten abgegangener Schüler`);
      teile.push(`Gelöscht: ${zahlen.join(", ")}.`);
    }
    for (const t of d.hinweise || []) teile.push(t);
    if (neu && teile.length) esText(neu.hinweis, teile.join(" "));
    toast(d.geaendert === 1 ? "Gespeichert – gilt ab sofort." : `${d.geaendert} Änderungen gespeichert – gelten ab sofort.`);
    // Name der App, „Angemeldet bleiben“: gleich übernehmen.
    if (gruppe === "schule" || gruppe === "anmeldung") {
      await ladeOptionen();
      if (einstellungenOffen()) document.title = `Einstellungen – ${appName()}`;
    }
  } catch (f) {
    const daten = f instanceof HttpFehler ? f.daten : null;
    if (f instanceof HttpFehler && f.status === 409 && daten && daten.bestaetigen) {
      es.bestaetigen = gruppe;
      k.knopf.textContent = "Speichern und löschen? Nochmal tippen";
      k.knopf.classList.add("warnung");
      esText(k.fehler, daten.fehler || "Dadurch werden Daten gelöscht.");
      // Nachbesserung P3: Der Knopf bleibt kurz gesperrt – ein doppelt gemeldeter Klick darf das Löschen nicht
      // gleich mitbestätigen (die Rückfrage kommt in wenigen Millisekunden).
      const knopf = k.knopf;
      setTimeout(() => {
        if (es.bestaetigen === gruppe && knopf.isConnected) knopf.disabled = false;
      }, ES_BESTAETIGEN_SPERRE_MS);
      return;
    } else if (f instanceof HttpFehler && f.status === 400 && daten && daten.felder) {
      let ausserhalb = false;
      for (const [s, text] of Object.entries(daten.felder)) {
        const feld = es.felder.get(s);
        if (feld && feld.karte === gruppe) esText(feld.fehler, text);
        else ausserhalb = true;
      }
      esText(k.fehler, (daten.fehler || "Bitte die markierten Felder prüfen.") +
        (ausserhalb ? " (Auch andere Einstellungen sind betroffen.)" : ""));
    } else {
      const t = aktionsFehler(f);
      if (t !== null) esText(k.fehler, t);
    }
    k.knopf.disabled = false;
  }
}

// ---------------------------------------------------------------------
//  Protokoll
// ---------------------------------------------------------------------

async function esProtokollLaden() {
  const nr = ++es.ladeNr;
  el("esPInfo").textContent = "Lade …";
  try {
    const d = await senden("GET", "api/einstellungen/protokoll?limit=300");
    if (nr !== es.ladeNr) return;
    const liste = d.eintraege || [];
    el("esPInfo").textContent = liste.length === 0 ? "Noch keine Änderungen."
      : `${d.gesamt} ${d.gesamt === 1 ? "Eintrag" : "Einträge"}` + (d.gesamt > liste.length ? ` – die neuesten ${liste.length}` : "");
    el("esPListe").replaceChildren(...liste.map(esProtokollZeile));
  } catch (f) {
    if (nr !== es.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) { el("esPInfo").textContent = t; el("esPListe").replaceChildren(); }
  }
}

function esProtokollZeile(p) {
  const wert = p.alt && p.neu ? `: ${p.alt} → ${p.neu}` : p.neu ? `: ${p.neu}` : "";
  return h("li", { class: "p-eintrag" },
    h("span", { class: "p-zeit" }, datumZeit(p.zeit)),
    h("span", { class: "p-text" }, h("strong", null, p.titel), wert, h("span", { class: "p-wer" }, ` (${p.akteur})`)),
    p.details ? h("span", { class: "p-details" }, p.details) : null);
}

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function einstellungenInit() {
  el("esKnopf").addEventListener("click", einstellungenOeffnen);
  el("esZurueck").addEventListener("click", einstellungenVerlassen);
  for (const b of document.querySelectorAll("#esReiter [data-reiter]"))
    b.addEventListener("click", () => { if (es.reiter !== b.dataset.reiter) esReiterWaehlen(b.dataset.reiter); });
  // Seite verlassen bzw. neu laden mit Ungespeichertem: der Browser fragt nach.
  window.addEventListener("beforeunload", (ev) => {
    if (einstellungenOffen() && esUngespeichert()) { ev.preventDefault(); ev.returnValue = ""; }
  });
}
