"use strict";
// =====================================================================
//  Stundenplan-PWA – Hausaufgaben (Phase 3, Teilschritt 3.3)
//
//  - Schülerkonto: Ansicht „Hausaufgaben" (Kontomenü und Leiste über dem
//    Plan): Aufgaben nach Fälligkeit – heute, morgen, später, dann die der
//    letzten Tage. Abhaken („erledigt") merkt sich nur dieses Gerät
//    (Browser-Speicher je Benutzer, beim Abmelden gelöscht); die Schule
//    sieht das nicht (Entscheidung 26). Offline lesbar wie der Plan.
//  - Plan: Stunden mit Thema/Hausaufgabe lassen sich antippen (nur lesen),
//    wenn kein Stundenblatt geöffnet werden kann (Schüler, Kolleginnen und
//    Kollegen, die die Klasse unterrichten). Hausaufgabe = kleine Marke „HA".
//
//  Wird nach fehlzeiten.js und VOR app.js geladen; Einstieg hausaufgabenInit().
//  Alle Funktionen mit Präfix „ha…" (klassische Skripte teilen einen Namensraum).
// =====================================================================

const HA_URL = "api/hausaufgaben/ich";
const HA_LEISTE_ABSTAND_MS = 60 * 1000;

const ha = {
  ladeNr: 0,
  daten: null,          // Antwort api/hausaufgaben/ich
  offlineStand: null,   // Zeitpunkt, wenn aus dem Speicher
  leisteUm: 0,          // zuletzt für die Leiste geladen
};

const hausaufgabenOffen = () => !el("hausaufgaben").hidden;

// ---------------------------------------------------------------------
//  Abhaken – nur auf diesem Gerät
// ---------------------------------------------------------------------

const haErledigtSchluessel = () => praefix() + "ha-erledigt";

function haErledigt() {
  const liste = nutzerAblage().lesen(haErledigtSchluessel(), []);
  return new Set(Array.isArray(liste) ? liste : []);
}

function haErledigtSpeichern(menge) {
  nutzerAblage().schreiben(haErledigtSchluessel(), [...menge]);
}

/** Häkchen von Aufgaben entfernen, die nicht mehr in der Liste stehen (hält den Speicher klein). */
function haErledigtAufraeumen(liste) {
  const da = new Set(liste.map((a) => a.schluessel));
  const alt = haErledigt();
  const neu = new Set([...alt].filter((k) => da.has(k)));
  if (neu.size !== alt.size) haErledigtSpeichern(neu);
}

// ---------------------------------------------------------------------
//  Öffnen / verlassen
// ---------------------------------------------------------------------

function hausaufgabenMenueAnpassen() {
  el("haKnopf").hidden = !istSchueler();
}

function hausaufgabenOeffnen() {
  kontoSchliessen();
  if (!istSchueler()) return;
  fehlzeitenSchliessen();
  ladenAbbrechen();
  el("inhalt").hidden = true;
  el("hausaufgaben").hidden = false;
  el("aktualisieren").hidden = false;
  el("titel").textContent = "Hausaufgaben";
  document.title = "Hausaufgaben – Stundenplan";
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  window.scrollTo(0, 0);
  haRender();
  haLaden();
}

/** Alles aus der Ansicht entfernen (Abmelden, Neustart). */
function hausaufgabenLeeren() {
  ha.ladeNr++;
  ha.daten = null;
  ha.offlineStand = null;
  ha.leisteUm = 0;
  el("haListe").replaceChildren();
  el("haInfo").textContent = "";
  el("haLeisteZahl").textContent = "";
  el("haLeiste").classList.remove("dringend");
  fehlerIn("haFehler", "");
  dialogZu(el("inhaltDialog"));
}

/** Ansicht schließen, ohne etwas anderes zu zeigen. */
function hausaufgabenSchliessen() {
  if (!hausaufgabenOffen()) return;
  el("hausaufgaben").hidden = true;
  ha.ladeNr++;
}

function hausaufgabenVerlassen() {
  hausaufgabenSchliessen();
  el("inhalt").hidden = false;
  haLeisteRendern();
  render();
  if (state.plan) ladeWoche({ still: true });
  else ladeWoche();
}

// ---------------------------------------------------------------------
//  Laden und anzeigen
// ---------------------------------------------------------------------

async function haLaden() {
  const nr = ++ha.ladeNr;
  fehlerIn("haFehler", "");
  if (!ha.daten) el("haInfo").textContent = "Wird geladen …";
  document.body.classList.add("laedt");
  try {
    const { daten, offlineStand } = await holen(HA_URL);
    if (nr !== ha.ladeNr) return;
    ha.daten = daten;
    ha.offlineStand = offlineStand;
    ha.leisteUm = Date.now();
    if (!offlineStand) haErledigtAufraeumen(daten.aufgaben || []);
    haRender();
    haLeisteRendern();
  } catch (f) {
    if (nr !== ha.ladeNr) return;
    if (zugriffsFehler(f)) return;
    el("haInfo").textContent = "";
    fehlerIn("haFehler", fehlerText(f));
  } finally {
    if (nr === ha.ladeNr) document.body.classList.remove("laedt");
  }
}

/** Gruppen: heute, morgen, je weiteres Datum, zuletzt „schon fällig gewesen". */
function haGruppen(liste) {
  const heute = heuteIso();
  const morgen = plusTage(heute, 1);
  const erledigt = haErledigt();
  const gruppen = [];
  const finde = (titel, art) => {
    let g = gruppen.find((x) => x.titel === titel);
    if (!g) { g = { titel, art, eintraege: [] }; gruppen.push(g); }
    return g;
  };
  const kommend = liste.filter((a) => a.faellig >= heute)
    .sort((a, b) => a.faellig.localeCompare(b.faellig) || a.datum.localeCompare(b.datum) || a.stunde - b.stunde);
  for (const a of kommend) {
    const titel = a.faellig === heute ? "Heute fällig" : a.faellig === morgen ? "Morgen fällig" : `Fällig ${tagLang(a.faellig)}`;
    finde(titel, a.faellig === heute ? "heute" : a.faellig === morgen ? "morgen" : "spaeter").eintraege.push(a);
  }
  const vorbei = liste.filter((a) => a.faellig < heute)
    .sort((a, b) => b.faellig.localeCompare(a.faellig) || b.datum.localeCompare(a.datum));
  if (vorbei.length) finde("Schon fällig gewesen", "vorbei").eintraege.push(...vorbei);
  // Erledigte jeweils ans Ende der Gruppe.
  for (const g of gruppen)
    g.eintraege.sort((a, b) => Number(erledigt.has(a.schluessel)) - Number(erledigt.has(b.schluessel)));
  return { gruppen, erledigt };
}

/** Offene Aufgaben: heute oder später fällig und nicht abgehakt. */
function haOffen(liste) {
  const heute = heuteIso();
  const erledigt = haErledigt();
  return liste.filter((a) => a.faellig >= heute && !erledigt.has(a.schluessel));
}

function haRender() {
  const d = ha.daten;
  if (!d) { el("haListe").replaceChildren(); return; }
  const liste = d.aufgaben || [];
  const offen = haOffen(liste).length;
  const teile = [];
  if (ha.offlineStand) teile.push(`Offline – gespeicherter Stand vom ${zeitpunkt(ha.offlineStand)}.`);
  teile.push(liste.length === 0 ? "Gerade ist nichts auf."
    : offen === 0 ? "Alles erledigt – nichts mehr offen."
    : `${offen} ${offen === 1 ? "Aufgabe" : "Aufgaben"} offen.`);
  el("haInfo").textContent = teile.join(" ");
  el("haInfo").classList.toggle("offline", !!ha.offlineStand);

  const { gruppen, erledigt } = haGruppen(liste);
  el("haListe").replaceChildren(...gruppen.map((g) => h("section", { class: "ha-gruppe ha-" + g.art },
    h("h2", { class: "ha-titel" }, g.titel),
    h("ul", { class: "ha-liste" }, g.eintraege.map((a) => haEintrag(a, erledigt.has(a.schluessel)))))));
}

function haHakenSymbol() {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("width", "22");
  svg.setAttribute("height", "22");
  svg.setAttribute("aria-hidden", "true");
  for (const [d, klasse] of [["M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z", "ring"], ["M7.8 12.4l2.9 2.9l5.6-5.8", "haken"]]) {
    const p = document.createElementNS(ns, "path");
    p.setAttribute("d", d);
    p.setAttribute("fill", "none");
    p.setAttribute("stroke", "currentColor");
    p.setAttribute("stroke-width", "2.2");
    p.setAttribute("stroke-linecap", "round");
    p.setAttribute("stroke-linejoin", "round");
    p.setAttribute("class", klasse);
    svg.append(p);
  }
  return svg;
}

function haEintrag(a, erledigt) {
  return h("li", { class: "ha-eintrag" + (erledigt ? " erledigt" : ""), "data-k": a.schluessel },
    h("button", {
      class: "ha-haken", type: "button", "aria-pressed": String(erledigt),
      "aria-label": `${a.fach}, fällig ${tagLang(a.faellig)}: ${erledigt ? "erledigt – wieder als offen markieren" : "als erledigt abhaken"}`,
      onclick: () => haUmschalten(a.schluessel),
    }, haHakenSymbol()),
    h("div", { class: "ha-text" },
      h("p", { class: "ha-kopf" },
        h("strong", null, a.fach), ` · ${a.lehrer} · aus der Stunde am ${kbDatumKurz(a.datum)}`),
      h("p", { class: "ha-auftrag" }, kbTextMitLinks(a.hausaufgabe)),
      a.thema ? h("p", { class: "ha-thema" }, "Thema: ", kbTextMitLinks(a.thema)) : null));
}

function haUmschalten(schluessel) {
  const menge = haErledigt();
  if (menge.has(schluessel)) menge.delete(schluessel);
  else menge.add(schluessel);
  haErledigtSpeichern(menge);
  haRender();
  haLeisteRendern();
  const knopf = el("haListe").querySelector(`li[data-k="${CSS.escape(schluessel)}"] .ha-haken`);
  if (knopf) knopf.focus();
}

// ---------------------------------------------------------------------
//  Leiste über dem Plan (Schüler)
// ---------------------------------------------------------------------

function haLeisteRendern() {
  const leiste = el("haLeiste");
  leiste.hidden = !istSchueler();
  if (leiste.hidden) return;
  const liste = (ha.daten && ha.daten.aufgaben) || null;
  if (!liste) { el("haLeisteZahl").textContent = ""; leiste.classList.remove("dringend"); return; }
  const offen = haOffen(liste);
  const bald = offen.filter((a) => a.faellig <= plusTage(heuteIso(), 1)).length;
  el("haLeisteZahl").textContent = offen.length === 0 ? "nichts offen"
    : `${offen.length} offen` + (bald ? ` · ${bald} bis morgen` : "");
  leiste.classList.toggle("dringend", bald > 0);
}

/** Für die Leiste still nachladen (höchstens einmal pro Minute). */
async function haLeisteLaden() {
  if (!istSchueler()) return;
  haLeisteRendern();
  if (Date.now() - ha.leisteUm < HA_LEISTE_ABSTAND_MS) return;
  ha.leisteUm = Date.now();
  const sn = sitzungNr;
  try {
    const { daten, offlineStand } = await holen(HA_URL);
    if (sn !== sitzungNr) return;
    ha.daten = daten;
    ha.offlineStand = offlineStand;
    haLeisteRendern();
  } catch { /* still: die Leiste bleibt ohne Zahl */ }
}

// ---------------------------------------------------------------------
//  Plan: Thema/Hausaufgabe einer Stunde lesen
// ---------------------------------------------------------------------

const haInhaltZeigbar = (s) => !!(s && (s.thema || s.hausaufgabe));

function haInhaltZeigen(s, datum) {
  el("idWann").textContent = `${tagLang(datum)} · ${s.stunde}. Stunde · ${s.lehrer}`;
  el("idTitel").textContent = s.fach || "Stunde";
  el("idInhalt").replaceChildren(...kbInhaltLeseTeile(s.thema, s.hausaufgabe, s.hausaufgabeFaellig));
  dialogAuf(el("inhaltDialog"));
  el("idZu").focus();
}

/** Kleine Marke „HA" im Plan. */
const haMarke = () => h("span", { class: "ha-marke", title: "Hausaufgabe" },
  "HA", h("span", { class: "vh" }, " – Hausaufgabe"));

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function hausaufgabenInit() {
  el("haKnopf").addEventListener("click", hausaufgabenOeffnen);
  el("haLeiste").addEventListener("click", hausaufgabenOeffnen);
  el("haZurueck").addEventListener("click", hausaufgabenVerlassen);
  el("idZu").addEventListener("click", () => dialogZu(el("inhaltDialog")));
  el("inhaltDialog").addEventListener("close", () => el("idInhalt").replaceChildren());
  hintergrundSchliesst(el("inhaltDialog"), () => dialogZu(el("inhaltDialog")));
}
