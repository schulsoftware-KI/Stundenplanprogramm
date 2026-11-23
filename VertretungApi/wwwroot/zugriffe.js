"use strict";
// =====================================================================
//  Stundenplan-PWA – Reiter „Zugriffe“ der Kontenverwaltung (P6a, Entscheidung 228; P6b, 232/233; nur Admin)
//
//  Teil „Anmeldungen“: das Anmeldeprotokoll – erfolglose Anmeldungen aller Konten, erfolgreiche nur von Admin-Konten
//  und Anmeldungen mit Wiederherstellungscode. Filter Konto (Teil des Benutzernamens, „-“ = unbekannte Konten),
//  Ergebnis und Zeitraum; oben „Letzte 24 Stunden: n erfolglos“. Nur lesen, kein Löschen von Hand.
//  P6b – Teil „Ansichten“: das Leseprotokoll – je Tag, Konto, Ansicht und Gegenstand ein Eintrag mit Anzahl und Uhrzeit.
//  Filter Schüler (Suche über api/schueler, gewählt wird die Id), Konto, Ansicht, Zeitraum. Ist der Schalter
//  „Leseprotokoll“ aus, steht oben ein Hinweis. Jeder Abruf steht selbst im Leseprotokoll (Server).
//
//  P6b – „Auskunft (Art. 15) …“ (Entscheidungen 234, 235): Dialog auDialog, aufgerufen aus dem Schülerdetail des
//  Klassenbuchs (fehlzeiten.js) und aus dem Konto (konten.js). Das PDF wird nur im Arbeitsspeicher gehalten und als
//  Datei angeboten (fzDateiLaden aus fehlzeiten.js); der Server protokolliert den Download.
//
//  Wird nach zweiterfaktor.js und VOR hilfe.js/app.js geladen und nutzt die Hilfen aus app.js/konten.js (h, el,
//  senden, aktionsFehler, datumZeit, verw …) erst zur Laufzeit. Einstieg zugriffeInit() aus start() in app.js;
//  geladen wird über reiterWaehlen("zugriffe") in konten.js. Alle Namen mit Präfix „zg…“/„za…“ bzw. „zugriffe…“.
// =====================================================================

const zg = {
  teil: "anmeldungen",   // P6b: "anmeldungen" | "ansichten"
  ladeNr: 0,
  timer: null,
  ergebnisse: false,   // Auswahl um die einzelnen Ergebnisse ergänzt?
  // P6b: Ansichten
  aTimer: null,
  aAnsichten: false,   // Auswahl der Ansichten gefüllt?
  schueler: null,      // { id, name } – Filter
  suchNr: 0,
  suchTimer: null,
};

function zugriffeLeeren() {
  zg.ladeNr++;
  zg.suchNr++;
  clearTimeout(zg.timer);
  clearTimeout(zg.aTimer);
  clearTimeout(zg.suchTimer);
  el("zgListe").replaceChildren();
  el("zgInfo").textContent = "";
  el("zgZusammenfassung").textContent = "";
  el("zgZusammenfassung").hidden = true;
  for (const id of ["zgKonto", "zgVon", "zgBis"]) el(id).value = "";
  el("zgErgebnis").value = "";
  // P6b
  el("zaListe").replaceChildren();
  el("zaTreffer").replaceChildren();
  el("zaTreffer").hidden = true;
  el("zaInfo").textContent = "";
  el("zaSchalter").hidden = true;
  for (const id of ["zaSchueler", "zaKonto", "zaVon", "zaBis"]) el(id).value = "";
  el("zaAnsicht").value = "";
  zaSchuelerSetzen(null);
}

/** Einstieg aus konten.js (reiterWaehlen bzw. Aktualisieren): den gewählten Teil laden. */
function zugriffeLaden() {
  for (const b of document.querySelectorAll("#zgTeile [data-teil]"))
    b.setAttribute("aria-selected", String(b.dataset.teil === zg.teil));
  el("zgAnmeldungen").hidden = zg.teil !== "anmeldungen";
  el("zgAnsichten").hidden = zg.teil !== "ansichten";
  if (zg.teil === "ansichten") ansichtenLaden();
  else anmeldungenLaden();
}

// ---------------------------------------------------------------------
//  Anmeldungen (P6a)
// ---------------------------------------------------------------------

async function anmeldungenLaden() {
  const nr = ++zg.ladeNr;
  const p = new URLSearchParams({ limit: "300" });
  const konto = el("zgKonto").value.trim();
  if (konto) p.set("konto", konto === "–" ? "-" : konto);
  if (el("zgErgebnis").value) p.set("ergebnis", el("zgErgebnis").value);
  if (el("zgVon").value) p.set("von", el("zgVon").value);
  if (el("zgBis").value) p.set("bis", el("zgBis").value);
  el("zgInfo").textContent = "Lade …";
  try {
    const d = await senden("GET", "api/zugriffe/anmeldungen?" + p.toString());
    if (nr !== zg.ladeNr) return;
    zgRender(d);
  } catch (f) {
    if (nr !== zg.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) { el("zgInfo").textContent = t; el("zgListe").replaceChildren(); }
  }
}

function zgRender(d) {
  if (!zg.ergebnisse && Array.isArray(d.ergebnisse)) {
    zg.ergebnisse = true;
    el("zgErgebnis").append(...d.ergebnisse.map((e) => h("option", { value: e }, e)));
  }
  const z = el("zgZusammenfassung");
  z.hidden = false;
  z.classList.toggle("warnung-leise", d.erfolglos24h > 0);
  z.textContent = d.erfolglos24h === 0
    ? "Letzte 24 Stunden: keine erfolglosen Anmeldungen."
    : `Letzte 24 Stunden: ${d.erfolglos24h} erfolglos` +
      (d.konten24h > 1 ? ` (${d.konten24h} Konten)` : d.konten24h === 1 ? " (1 Konto)" : "") + ".";

  const liste = d.eintraege || [];
  const gefiltert = el("zgKonto").value.trim() || el("zgErgebnis").value || el("zgVon").value || el("zgBis").value;
  el("zgInfo").textContent = liste.length === 0
    ? (gefiltert ? "Keine Anmeldungen zu diesem Filter." : "Noch keine Einträge.")
    : `${d.gesamt === 1 ? "1 Eintrag" : `${d.gesamt} Einträge`}` + (d.gesamt > liste.length ? ` – die neuesten ${liste.length}` : "") +
      (d.schuladressen ? "" : " · Schulnetz: keine Schuladressen eingetragen");
  el("zgListe").replaceChildren(...liste.map(zgZeile));
}

function zgZeile(p) {
  // „Unbekanntes Konto“ steht schon im Ergebnis – nicht doppelt
  const wer = p.konto ? p.konto : p.ergebnis === "Unbekanntes Konto" ? "" : "unbekanntes Konto";
  const details = [];
  if (p.adresse) details.push(`Adresse ${p.adresse}`);
  if (p.schulnetz === true) details.push("Schulnetz");
  else if (p.schulnetz === false) details.push("nicht im Schulnetz");
  if (p.geraet) details.push(p.geraet);
  return h("li", { class: "p-eintrag zg-eintrag" + (p.erfolgreich ? " zg-ok" : " zg-fehl") },
    h("span", { class: "p-zeit" }, datumZeit(p.zeit)),
    h("span", { class: "p-text" },
      h("strong", null, p.ergebnis), wer ? ` · ${wer}` : "",
      p.rollenText ? h("span", { class: "p-wer" }, ` (${p.rollenText})`) : null),
    details.length ? h("span", { class: "p-details" }, details.join(" · ")) : null);
}

// ---------------------------------------------------------------------
//  P6b (Entscheidungen 232, 233): Ansichten – das Leseprotokoll
// ---------------------------------------------------------------------

async function ansichtenLaden() {
  const nr = ++zg.ladeNr;
  const p = new URLSearchParams({ limit: "300" });
  if (zg.schueler) p.set("schueler", String(zg.schueler.id));
  const konto = el("zaKonto").value.trim();
  if (konto) p.set("konto", konto);
  if (el("zaAnsicht").value) p.set("ansicht", el("zaAnsicht").value);
  if (el("zaVon").value) p.set("von", el("zaVon").value);
  if (el("zaBis").value) p.set("bis", el("zaBis").value);
  el("zaInfo").textContent = "Lade …";
  try {
    const d = await senden("GET", "api/zugriffe/ansichten?" + p.toString());
    if (nr !== zg.ladeNr) return;
    zaRender(d);
  } catch (f) {
    if (nr !== zg.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) { el("zaInfo").textContent = t; el("zaListe").replaceChildren(); }
  }
}

function zaRender(d) {
  if (!zg.aAnsichten && Array.isArray(d.ansichten)) {
    zg.aAnsichten = true;
    el("zaAnsicht").append(...d.ansichten.map((a) => h("option", { value: a.id }, a.text)));
  }
  const s = el("zaSchalter");
  s.hidden = !!d.an;
  s.textContent = d.an ? "" : "Das Leseprotokoll ist ausgeschaltet – es werden keine neuen Ansichten aufgezeichnet " +
    "(Kontomenü → „Einstellungen“ → Datenschutz → „Leseprotokoll“). Ältere Einträge bleiben bis zum Ende der Frist.";

  const liste = d.eintraege || [];
  const gefiltert = zg.schueler || el("zaKonto").value.trim() || el("zaAnsicht").value || el("zaVon").value || el("zaBis").value;
  el("zaInfo").textContent = liste.length === 0
    ? (gefiltert ? "Keine Ansichten zu diesem Filter." : "Noch keine Einträge.")
    : `${d.gesamt === 1 ? "1 Eintrag" : `${d.gesamt} Einträge`} · ${d.aufrufe === 1 ? "1 Aufruf" : `${d.aufrufe} Aufrufe`}` +
      (d.gesamt > liste.length ? ` – die neuesten ${liste.length}` : "");
  el("zaListe").replaceChildren(...liste.map(zaZeile));
}

function zaZeile(p) {
  const uhr = (t) => new Date(t).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
  const zeit = p.anzahl > 1 ? `${kurzDatum(p.tag)} ${uhr(p.ersteZeit)}–${uhr(p.letzteZeit)}` : `${kurzDatum(p.tag)} ${uhr(p.ersteZeit)}`;
  const details = [p.gegenstand];
  if (p.schueler) details.push(p.schueler);
  if (p.istPseudo) details.push("Pseudodaten");
  return h("li", { class: "p-eintrag za-eintrag" },
    h("span", { class: "p-zeit" }, zeit),
    h("span", { class: "p-text" },
      h("strong", null, p.ansichtText),
      ` · ${p.akteur}`, h("span", { class: "p-wer" }, ` (${p.rollenText})`),
      p.anzahl > 1 ? h("span", { class: "marke" }, `${p.anzahl}×`) : null),
    h("span", { class: "p-details" }, details.join(" · ")));
}

/** Schüler als Filter wählen bzw. aufheben (null). */
function zaSchuelerSetzen(s) {
  zg.schueler = s;
  el("zaGewaehlt").hidden = !s;
  el("zaGewaehltName").textContent = s ? `Schüler: ${s.name}` : "";
  el("zaSchueler").hidden = !!s;
}

function zaSuche(sofort) {
  clearTimeout(zg.suchTimer);
  zg.suchNr++;
  const q = el("zaSchueler").value.trim();
  if (q.length < 2) {
    el("zaTreffer").replaceChildren();
    el("zaTreffer").hidden = true;
    return;
  }
  zg.suchTimer = setTimeout(async () => {
    const nr = ++zg.suchNr;
    try {
      const liste = await senden("GET", `api/schueler?q=${encodeURIComponent(q)}&limit=11`);
      if (nr !== zg.suchNr) return;
      const zeigen = (liste || []).slice(0, 10);
      el("zaTreffer").replaceChildren(...(zeigen.length === 0
        ? [h("li", { class: "such-info" }, "Niemand gefunden.")]
        : zeigen.map((s) => h("li", null,
            h("button", {
              type: "button", class: "treffer-knopf",
              onclick: () => {
                el("zaSchueler").value = "";
                el("zaTreffer").replaceChildren();
                el("zaTreffer").hidden = true;
                zaSchuelerSetzen({ id: s.id, name: `${s.vorname} ${s.nachname}` + (s.klasse || s.stufe ? ` (${s.klasse || s.stufe})` : "") });
                ansichtenLaden();
                el("zaSchuelerWeg").focus();
              },
            },
            h("span", { class: "t-name" }, `${s.nachname}, ${s.vorname}`),
            h("span", { class: "t-klasse" }, s.klasse || s.stufe || ""))))));
      el("zaTreffer").hidden = false;
    } catch (f) {
      if (nr !== zg.suchNr) return;
      const t = aktionsFehler(f);
      if (t) el("zaInfo").textContent = t;
    }
  }, sofort ? 0 : 250);
}

// ---------------------------------------------------------------------
//  P6b (Entscheidungen 234, 235): Auskunft nach Art. 15 – Dialog
// ---------------------------------------------------------------------

const au = { ziel: null, laeuft: false, nr: 0, abbruch: null };

/** ziel = { url, name, schueler: true|false } */
function auskunftOeffnen(ziel) {
  au.ziel = ziel;
  au.nr++;
  el("auWas").textContent = ziel.name;
  el("auText").textContent = ziel.schueler
    ? "Das PDF enthält alle gespeicherten Daten über alle Schuljahre: Angaben nach Art. 15 Abs. 1 (Zwecke, Empfänger, " +
      "Speicherdauer nach den Fristen der Einstellungen, Rechte), Stammdaten, Klassen und Kurse, Konto, jede Fehlstunde mit " +
      "Bewertung und Grund, Entschuldigungen, Einträge, Änderungsverlauf und das Leseprotokoll (ohne Namen, nur die Rolle)."
    : "Das PDF enthält das Konto, die Zuständigkeit, den Namen laut SchILD, Konten- und Anmeldeprotokoll, die " +
      "Abwesenheitsgründe in veröffentlichten Vertretungen sowie Ansichten, Tätigkeiten im Klassenbuch und Downloads als " +
      "Zahlen je Monat.";
  el("auStatus").textContent = "";
  fehlerIn("auFehler", "");
  el("auErstellen").disabled = false;
  el("auErstellen").textContent = "PDF erstellen";
  dialogAuf(el("auDialog"));
  el("auErstellen").focus();
}

async function auskunftErstellen() {
  const z = au.ziel;
  if (!z || au.laeuft) return;
  const nr = au.nr;
  au.laeuft = true;
  au.abbruch = new AbortController();
  const timer = setTimeout(() => au.abbruch && au.abbruch.abort(), 180000);
  el("auErstellen").disabled = true;
  el("auErstellen").textContent = "PDF wird erstellt …";
  el("auStatus").textContent = "Das kann bei mehreren Schuljahren einige Sekunden dauern.";
  fehlerIn("auFehler", "");
  try {
    const name = await fzDateiLaden(z.url, "Auskunft.pdf", au.abbruch);
    if (nr !== au.nr) return;
    dialogZu(el("auDialog"));
    toast(`PDF erstellt: ${name}`);
  } catch (f) {
    if (nr !== au.nr) return;
    if (au.abbruch && au.abbruch.signal.aborted) fehlerIn("auFehler", "Abgebrochen – das hat zu lange gedauert.");
    else {
      const t = aktionsFehler(f);
      if (t !== null) fehlerIn("auFehler", t);
    }
  } finally {
    clearTimeout(timer);
    if (nr === au.nr) {
      au.laeuft = false;
      el("auErstellen").disabled = false;
      el("auErstellen").textContent = "PDF erstellen";
      el("auStatus").textContent = "";
    }
  }
}

function zugriffeInit() {
  // P6b: Auskunft
  el("auErstellen").addEventListener("click", auskunftErstellen);
  el("auAbbrechen").addEventListener("click", () => dialogZu(el("auDialog")));
  el("auDialog").addEventListener("close", () => {
    au.nr++;
    au.ziel = null;
    au.laeuft = false;
    if (au.abbruch) { au.abbruch.abort(); au.abbruch = null; }
    el("auWas").textContent = "";
  });
  hintergrundSchliesst(el("auDialog"), () => dialogZu(el("auDialog")));

  const spaeter = () => { clearTimeout(zg.timer); zg.timer = setTimeout(anmeldungenLaden, 250); };
  el("zgKonto").addEventListener("input", spaeter);
  el("zgKonto").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); clearTimeout(zg.timer); anmeldungenLaden(); }
  });
  for (const id of ["zgErgebnis", "zgVon", "zgBis"]) el(id).addEventListener("change", anmeldungenLaden);

  // P6b: Teile und Ansichten
  for (const b of document.querySelectorAll("#zgTeile [data-teil]"))
    b.addEventListener("click", () => { if (zg.teil !== b.dataset.teil) { zg.teil = b.dataset.teil; zugriffeLaden(); } });
  const aSpaeter = () => { clearTimeout(zg.aTimer); zg.aTimer = setTimeout(ansichtenLaden, 250); };
  el("zaKonto").addEventListener("input", aSpaeter);
  el("zaKonto").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); clearTimeout(zg.aTimer); ansichtenLaden(); }
  });
  for (const id of ["zaAnsicht", "zaVon", "zaBis"]) el(id).addEventListener("change", ansichtenLaden);
  el("zaSchueler").addEventListener("input", () => zaSuche(false));
  el("zaSchueler").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); zaSuche(true); }
    else if (e.key === "ArrowDown") {
      const erster = el("zaTreffer").querySelector("button");
      if (erster) { e.preventDefault(); erster.focus(); }
    }
  });
  el("zaTreffer").addEventListener("keydown", (e) => {
    const knoepfe = [...el("zaTreffer").querySelectorAll("button")];
    const i = knoepfe.indexOf(document.activeElement);
    if (i < 0) return;
    if (e.key === "ArrowDown" && i < knoepfe.length - 1) { e.preventDefault(); knoepfe[i + 1].focus(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); (i > 0 ? knoepfe[i - 1] : el("zaSchueler")).focus(); }
  });
  el("zaSchuelerWeg").addEventListener("click", () => {
    zaSchuelerSetzen(null);
    ansichtenLaden();
    el("zaSchueler").focus();
  });
}
