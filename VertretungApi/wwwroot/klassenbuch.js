"use strict";
// =====================================================================
//  Stundenplan-PWA – Klassenbuch (Phase 3, Teilschritt 3.1)
//
//  - Stundenblatt: Antippen einer Stunde im Plan (nur Personal, nur Stunden
//    mit Klassenbuch und Recht) öffnet die Teilnehmerliste. Alle sind
//    zunächst „anwesend"; Antippen eines Namens schaltet anwesend ⇄ fehlt,
//    „⋯" öffnet die übrigen Status (verspätet mit Minuten, beurlaubt,
//    schulische Veranstaltung). Speichern vermerkt die Stunde als erfasst.
//    Jede Änderung landet serverseitig in der Historie („Änderungen").
//  - Gruppe zusammenstellen: Sek-I-Gruppen mit eigener Schülergruppe
//    (Religion, WP, AG …) stellt die Fachlehrkraft einmal aus den Schülern
//    der genannten Klassen zusammen. Vorher kann für diese Stunden keine
//    Anwesenheit erfasst werden.
//  - Meine Gruppen (Kontomenü): alle eigenen Gruppen, offene zuerst.
//  - Doppelstunde (Nachtrag 3.1): In der 1. Stunde übernimmt der Schalter
//    „Auch für die 2. Stunde" die Erfassung beim Speichern (verspätet zählt
//    dort als anwesend; eine schon erfasste 2. Stunde bleibt unverändert).
//
//  Datenschutz: Namen und Anwesenheiten stehen nur im Arbeitsspeicher –
//  nie im Browser-Speicher – und werden beim Verlassen gelöscht.
//
//  Wird nach konten.js und VOR app.js geladen; nutzt deren Hilfen (h, el,
//  state, senden, aktionsFehler, toast, dialogAuf …) erst zur Laufzeit.
//  Einstieg: klassenbuchInit() aus start() in app.js.
// =====================================================================

const KB_STATUS = {
  Anwesend:           { kurz: "anwesend",      lang: "anwesend",                 art: "da" },
  Fehlt:              { kurz: "fehlt",         lang: "fehlt",                    art: "fehlt" },
  Verspaetet:         { kurz: "verspätet",     lang: "verspätet",                art: "spaet" },
  Beurlaubt:          { kurz: "beurlaubt",     lang: "beurlaubt",                art: "urlaub" },
  Schulveranstaltung: { kurz: "Schulveranst.", lang: "schulische Veranstaltung", art: "veranst" },
};
const KB_REIHENFOLGE = ["Anwesend", "Fehlt", "Verspaetet", "Beurlaubt", "Schulveranstaltung"];
const KB_MAX_MINUTEN = 240;

const kb = {
  ansicht: null,          // "stunde" | "gruppe" | "gruppen"
  herkunft: "plan",       // Stundenblatt geöffnet aus "plan" | "fehlzeiten" (Phase 3.2)
  ladeNr: 0,
  schluessel: null,       // { datum, stunde, unr, lehrer } des Stundenblatts
  blatt: null,            // Antwort GET api/klassenbuch/stunde
  stand: new Map(),       // Schüler-Id -> { status, minuten } (bearbeitet)
  geaendert: false,
  folgeAn: true,          // Doppelstunde: 2. Stunde beim Speichern mit erfassen
  festOk: new Set(),      // festgelegte Zeilen, deren Änderung die Leitung bestätigt hat
  speichert: false,
  konflikt: false,
  statusFuer: null,       // Schüler im Statusdialog
  gruppe: null,           // Antwort GET api/klassenbuch/gruppe
  gruppeWahl: new Set(),  // gewählte Ids
  gruppeGeaendert: false,
  gruppeZurueck: null,    // "stunde" | "gruppen" | null (Plan)
  gruppen: null,          // Antwort GET api/klassenbuch/gruppen
};

// ---------------------------------------------------------------------
//  Wer darf eine Stunde im Plan öffnen? (Der Server prüft dasselbe.)
// ---------------------------------------------------------------------

function kbZustaendigFuer(klassen) {
  const a = state.anmeldung;
  if (!a || !["Klassenlehrer", "Stufenkoordination"].includes(a.rolle) || !a.zustaendig) return false;
  const zust = a.zustaendig.split(/[,; ]+/).map((z) => z.trim().toUpperCase()).filter(Boolean);
  return (klassen || []).some((k) => {
    const kl = (k || "").trim().toUpperCase();
    return zust.some((z) => z === kl || (/^\d\d$/.test(z) && kl.startsWith(z)));
  });
}

/** Stunde im Plan antippbar? Nur Personal, nur mit Klassenbuch und Recht. */
function kbDarfOeffnen(s) {
  if (!s || !s.klassenbuch || !istPersonal()) return false;
  if (state.anmeldung.rolle === "Admin") return true;
  const k = eigenesKuerzel();
  if (k && (gleich(k, s.lehrer) || gleich(k, s.grundLehrer) || gleich(k, s.originalLehrer))) return true;
  return kbZustaendigFuer(s.klassen);
}

const klassenbuchOffen = () => !el("klassenbuch").hidden;

/** Kleines Symbol im Plan: offenes Buch (antippbar) bzw. Häkchen (Anwesenheit erfasst). */
function kbSymbol(erfasst) {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("width", "16");
  svg.setAttribute("height", "16");
  svg.setAttribute("aria-hidden", "true");
  const pfade = erfasst
    ? ["M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z", "M7.8 12.4l2.9 2.9l5.6-5.8"]
    : ["M3.5 5.5c2.8-1 5.8-.9 8.5.8c2.7-1.7 5.7-1.8 8.5-.8v13c-2.8-1-5.8-.9-8.5.8c-2.7-1.7-5.7-1.8-8.5-.8z",
       "M12 6.3v13"];
  for (const d of pfade) {
    const p = document.createElementNS(ns, "path");
    p.setAttribute("d", d);
    p.setAttribute("fill", "none");
    p.setAttribute("stroke", "currentColor");
    p.setAttribute("stroke-width", erfasst ? "2.4" : "2");
    p.setAttribute("stroke-linecap", "round");
    p.setAttribute("stroke-linejoin", "round");
    svg.append(p);
  }
  return h("span", { class: "kb-symbol" + (erfasst ? " ok" : "") },
    svg, h("span", { class: "vh" }, erfasst ? "Anwesenheit erfasst" : "Klassenbuch öffnen"));
}

// ---------------------------------------------------------------------
//  Ansicht öffnen / verlassen
// ---------------------------------------------------------------------

function kbUngespeichert() {
  return (kb.ansicht === "stunde" && kb.geaendert) || (kb.ansicht === "gruppe" && kb.gruppeGeaendert);
}

/** true = darf weiter (nichts Ungespeichertes bzw. bestätigt). */
function kbVerwerfenErlaubt() {
  if (!kbUngespeichert()) return true;
  return window.confirm(kb.ansicht === "gruppe"
    ? "Die Gruppe wurde noch nicht gespeichert. Änderungen verwerfen?"
    : "Die Anwesenheit wurde noch nicht gespeichert. Änderungen verwerfen?");
}

function zeigeKlassenbuch(ansicht) {
  kontoSchliessen();
  ladenAbbrechen();
  kb.ansicht = ansicht;
  el("inhalt").hidden = true;
  el("verwaltung").hidden = true;
  el("fehlzeiten").hidden = true;   // Zustand bleibt erhalten (Rückkehr aus dem Stundenblatt)
  el("klassenbuch").hidden = false;
  el("aktualisieren").hidden = false;
  el("kbStunde").hidden = ansicht !== "stunde";
  el("kbGruppe").hidden = ansicht !== "gruppe";
  el("kbGruppen").hidden = ansicht !== "gruppen";
  const titel = ansicht === "stunde" ? "Klassenbuch"
    : ansicht === "gruppe" ? "Gruppenliste"
    : state.anmeldung && state.anmeldung.rolle === "Admin" ? "Gruppenlisten" : "Meine Gruppen";
  el("titel").textContent = titel;
  document.title = `${titel} – Stundenplan`;
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  const zurueck = ansicht === "gruppe" && kb.gruppeZurueck === "stunde" ? "Zurück zur Stunde"
    : ansicht === "gruppe" && kb.gruppeZurueck === "gruppen" ? "Zurück zu den Gruppen"
    : ansicht === "stunde" && kb.herkunft === "fehlzeiten" ? "Zurück zur Liste"
    : "Zum Plan";
  el("kbZurueckText").textContent = zurueck;
  window.scrollTo(0, 0);
}

/** Alles Personenbezogene des Klassenbuchs aus Seite und Speicher entfernen. */
function klassenbuchLeeren() {
  kb.ladeNr++;
  kb.ansicht = null;
  kb.herkunft = "plan";
  kb.schluessel = null;
  kb.blatt = null;
  kb.stand = new Map();
  kb.geaendert = false;
  kb.folgeAn = true;
  kb.speichert = false;
  kb.konflikt = false;
  kb.statusFuer = null;
  kb.gruppe = null;
  kb.gruppeWahl = new Set();
  kb.gruppeGeaendert = false;
  kb.gruppeZurueck = null;
  kb.gruppen = null;
  for (const id of ["kbListe", "kbOffen", "kbHinweise", "kgKlassen", "kgsListe", "khListe", "ksWahl"])
    el(id).replaceChildren();
  for (const id of ["kbWann", "kbFach", "kbWer", "kbErfasst", "kbZahlen", "kgTitel", "kgInfo", "kgStand",
    "kgsInfo", "khInfo", "ksName", "ksKlasse", "kbFolgeText", "kbFolgeInfo"])
    el(id).textContent = "";
  el("kgFilter").value = "";
  el("ksMinuten").value = "";
  fehlerIn("kbFehler", "");
  fehlerIn("kgFehler", "");
  kbDialogeZu();
}

function kbDialogeZu() {
  dialogZu(el("kbStatusDialog"));
  dialogZu(el("kbHistorieDialog"));
}

/** Klassenbuch schließen, ohne etwas anderes zu zeigen. false = abgebrochen (Ungespeichertes). */
function klassenbuchSchliessen() {
  if (!klassenbuchOffen()) return true;
  if (!kbVerwerfenErlaubt()) return false;
  klassenbuchLeeren();
  el("klassenbuch").hidden = true;
  return true;
}

/** Zurück zum Plan (fragt bei Ungespeichertem nach). false = abgebrochen. */
function klassenbuchVerlassen() {
  if (!klassenbuchOffen()) return true;
  const herkunft = kb.herkunft;
  if (!klassenbuchSchliessen()) return false;
  if (herkunft === "fehlzeiten" && fz.stapel.length) { fehlzeitenWiederZeigen(); return true; }
  el("inhalt").hidden = false;
  render();
  if (state.plan) ladeWoche({ still: true });
  else ladeWoche();
  return true;
}

/** „Zurück"-Knopf: aus der Gruppe ggf. zur Stunde bzw. Gruppenliste, sonst zum Plan. */
function kbZurueck() {
  if (kb.ansicht === "gruppe" && kb.gruppeZurueck) {
    if (!kbVerwerfenErlaubt()) return;
    const ziel = kb.gruppeZurueck;
    kb.gruppe = null;
    kb.gruppeWahl = new Set();
    kb.gruppeGeaendert = false;
    kb.gruppeZurueck = null;
    el("kgKlassen").replaceChildren();
    if (ziel === "stunde" && kb.schluessel) { zeigeKlassenbuch("stunde"); stundeLaden(); }
    else gruppenOeffnen();
    return;
  }
  klassenbuchVerlassen();
}

function kbAktualisieren() {
  if (!kbVerwerfenErlaubt()) return;
  if (kb.ansicht === "stunde") stundeLaden();
  else if (kb.ansicht === "gruppe" && kb.gruppe) gruppeLaden(kb.gruppe.schluessel);
  else if (kb.ansicht === "gruppen") gruppenLaden();
}

// ---------------------------------------------------------------------
//  Stundenblatt
// ---------------------------------------------------------------------

/** Aus dem Plan (bzw. aus „nicht erfasst"): Stunde s am Tag datum öffnen. */
function stundenblattOeffnen(s, datum, herkunft = "plan") {
  if (!kbDarfOeffnen(s)) return;
  kb.herkunft = herkunft;
  kb.schluessel = { datum, stunde: s.stunde, unr: s.grundUNr, lehrer: s.grundLehrer };
  kb.blatt = null;
  kb.stand = new Map();
  kb.geaendert = false;
  kb.gruppeZurueck = null;
  zeigeKlassenbuch("stunde");
  renderStunde();
  stundeLaden();
}

function kbStundeUrl(pfad) {
  const s = kb.schluessel;
  return `api/klassenbuch/${pfad}?datum=${encodeURIComponent(s.datum)}&stunde=${s.stunde}` +
    `&unr=${s.unr}&lehrer=${encodeURIComponent(s.lehrer)}`;
}

async function stundeLaden() {
  if (!kb.schluessel) return;
  const nr = ++kb.ladeNr;
  kb.konflikt = false;
  fehlerIn("kbFehler", "");
  document.body.classList.add("laedt");
  el("kbStunde").setAttribute("aria-busy", "true");
  try {
    const d = await senden("GET", kbStundeUrl("stunde"));
    if (nr !== kb.ladeNr) return;
    blattUebernehmen(d);
  } catch (f) {
    if (nr !== kb.ladeNr) return;
    const text = aktionsFehler(f);
    if (text === null) return;
    kb.blatt = null;
    renderStunde();
    fehlerIn("kbFehler", text);
  } finally {
    if (nr === kb.ladeNr) {
      document.body.classList.remove("laedt");
      el("kbStunde").removeAttribute("aria-busy");
    }
  }
}

function blattUebernehmen(d) {
  kb.blatt = d;
  kb.stand = new Map(d.teilnehmer.map((t) => [t.id, { status: t.status, minuten: t.minuten }]));
  kb.festOk = new Set();
  kb.geaendert = false;
  kb.folgeAn = true;
  renderStunde();
}

/** Doppelstunde: Die folgende Stunde kann beim Speichern mit erfasst werden. */
const kbFolgeMoeglich = () => {
  const d = kb.blatt;
  return !!(d && d.darfSchreiben && d.folge && !d.folge.erfasst);
};
const kbMitFolge = () => kbFolgeMoeglich() && kb.folgeAn;

const kbAbweichungen = () => [...kb.stand.entries()].filter(([, v]) => v.status !== "Anwesend");

/** Teilnehmer, die von Klassenleitung/Stufenkoordination/Admin einzeln festgelegt sind (Nachtrag 3.2). */
const kbFestgelegt = (id) => {
  const t = kb.blatt && (kb.blatt.teilnehmer || []).find((x) => x.id === id);
  return t && t.festgelegt ? t : null;
};

/** Abweichungen, die „Alle anwesend" zurücksetzen darf (ohne festgelegte). */
const kbAenderbareAbweichungen = () => kbAbweichungen().filter(([id]) => !kbFestgelegt(id));

/**
 * Leitung ändert eine festgelegte Fehlstunde: einmal nachfragen. true = darf ändern.
 * (Gesperrte Zeilen – Fach-/Kurslehrkraft – lassen sich gar nicht erst antippen.)
 */
function kbFestgelegtOk(id) {
  const t = kbFestgelegt(id);
  if (!t || kb.festOk.has(id)) return true;
  const was = t.bewertung === "Entschuldigt" ? "entschuldigt" : "unentschuldigt";
  const ok = window.confirm(`Die Fehlstunde von ${t.vorname} ${t.nachname} ist als ${was} festgelegt` +
    (t.festgelegtVon ? ` (von ${t.festgelegtVon})` : "") + ". Wirklich ändern?");
  if (ok) kb.festOk.add(id);
  return ok;
}

function kbGesperrtHinweis(t) {
  toast(`Festgelegt von ${t.festgelegtVon || "der Klassenleitung bzw. Stufenkoordination"} – ` +
    "ändern kann das nur die Klassenleitung bzw. Stufenkoordination.");
}

/** Kleines Schloss für gesperrte Zeilen. */
function kbSchloss() {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("width", "18");
  svg.setAttribute("height", "18");
  svg.setAttribute("aria-hidden", "true");
  for (const d of ["M6.5 10.5h11a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1v-8a1 1 0 0 1 1-1z", "M8.5 10.5V7.8a3.5 3.5 0 0 1 7 0v2.7"]) {
    const p = document.createElementNS(ns, "path");
    p.setAttribute("d", d);
    p.setAttribute("fill", "none");
    p.setAttribute("stroke", "currentColor");
    p.setAttribute("stroke-width", "2");
    p.setAttribute("stroke-linejoin", "round");
    svg.append(p);
  }
  return svg;
}

function kbStatusText(v, lang = false) {
  const st = KB_STATUS[v.status] || KB_STATUS.Anwesend;
  const text = lang ? st.lang : st.kurz;
  return v.status === "Verspaetet" && v.minuten ? `${text} ${v.minuten} Min.` : text;
}

function renderStunde() {
  const d = kb.blatt;
  const s = kb.schluessel;
  el("kbWann").textContent = s ? `${tagLang(s.datum)} · ${s.stunde}. Stunde` : "";
  if (!d) {
    el("kbFach").textContent = "";
    el("kbWer").replaceChildren();
    el("kbErfasst").textContent = "";
    el("kbHinweise").replaceChildren();
    el("kbOffen").replaceChildren();
    el("kbListe").replaceChildren();
    el("kbListeKopf").hidden = true;
    el("kbLeiste").hidden = true;
    return;
  }

  const gruppen = (d.klassen && d.klassen.length ? d.klassen : d.lerngruppen || []).join(", ");
  el("kbFach").textContent = gruppen ? `${d.fach} · ${gruppen}` : d.fach;

  const st = STATUS[d.status];
  const wer = [d.originalLehrer ? korrektur(d.originalLehrer, d.lehrer) : d.lehrer];
  if (st && d.status !== "Normal") wer.push(" ", h("span", { class: `marke kb-art ${st.art}` }, st.text));
  if (d.raum) wer.push(h("span", { class: "kb-raum" }, `Raum ${d.raum}`));
  el("kbWer").replaceChildren(...wer);

  el("kbErfasst").replaceChildren(...erfasstZeile(d));

  el("kbHinweise").replaceChildren(...(d.hinweise || []).map((t) => h("p", { class: "hinweis" }, t)));
  el("kbOffen").replaceChildren(...(d.offeneGruppen || []).map(offeneGruppeKarte));

  const liste = d.teilnehmer || [];
  const keineListe = liste.length === 0;
  el("kbListeKopf").hidden = keineListe;
  el("kbAlleDa").hidden = !d.darfSchreiben || kbAenderbareAbweichungen().length === 0;
  el("kbListe").replaceChildren(...liste.map(teilnehmerZeile));
  renderZahlen();
  renderLeiste();
}

function erfasstZeile(d) {
  if (!d.erfasstAm) {
    return [h("span", { class: "kb-offen" }, d.sperrgrund || "Anwesenheit noch nicht erfasst")];
  }
  const teile = [h("span", { class: "kb-ok" }, "✓ erfasst"), ` von ${d.erfasstVon} ${kurzZeit(d.erfasstAm)}`];
  if (d.geaendertAm && (d.geaendertAm !== d.erfasstAm || d.geaendertVon !== d.erfasstVon))
    teile.push(` · zuletzt geändert von ${d.geaendertVon} ${kurzZeit(d.geaendertAm)}`);
  if (d.sperrgrund) teile.push(h("span", { class: "kb-sperre" }, d.sperrgrund));
  return teile;
}

function offeneGruppeKarte(g) {
  const wer = g.lehrer && g.lehrer.length ? g.lehrer.join(", ") : "der Fachlehrkraft";
  return h("div", { class: "karte kb-gruppe-offen" },
    h("p", { class: "kb-go-titel" }, `Gruppe ${g.fach} (${g.klassen.join(", ")}) noch nicht zusammengestellt`),
    g.darfZusammenstellen
      ? [h("p", { class: "kleingedruckt ohne-einzug" },
          "Wähle einmal aus den Klassen aus, wer zu dieser Gruppe gehört. Danach steht die Liste hier."),
        h("button", {
          class: "hauptknopf klein", type: "button",
          onclick: () => gruppeOeffnen(g.schluessel, "stunde"),
        }, "Gruppe zusammenstellen")]
      : h("p", { class: "kleingedruckt ohne-einzug" },
          `Das macht die Lehrkraft der Gruppe (${wer}). Bis dahin kann hier keine Anwesenheit erfasst werden.`));
}

function teilnehmerZeile(t) {
  const v = kb.stand.get(t.id) || { status: "Anwesend", minuten: null };
  const info = KB_STATUS[v.status] || KB_STATUS.Anwesend;
  const name = `${t.nachname}, ${t.vorname}`;
  const zeigeKlasse = !!t.klasse && ((kb.blatt.klassen || []).length !== 1 || t.nichtInListe);
  // Phase 3.2: Entschuldigung zur (unveränderten) Fehlstunde/Verspätung anzeigen.
  const bewertet = t.bewertung && t.bewertung !== "Offen" && v.status === t.status &&
    (t.status === "Fehlt" || t.status === "Verspaetet");
  const unter = [
    zeigeKlasse ? h("span", { class: "kb-klasse" }, t.klasse) : null,
    t.nichtInListe ? h("span", { class: "kb-ausser" }, "nicht mehr in der Liste") : null,
    bewertet ? h("span", { class: "kb-bew b-" + t.bewertung.toLowerCase() },
      (t.bewertung === "Entschuldigt" ? "entschuldigt" : "unentschuldigt") + (t.festgelegt ? " · festgelegt" : "")) : null,
  ].filter(Boolean);
  const inhalt = [
    h("span", { class: "kb-namezeile" },
      h("span", { class: "kb-name" }, name),
      unter.length ? h("span", { class: "kb-unter" }, unter) : null),
    h("span", { class: "kb-status" }, kbStatusText(v)),
  ];

  const klassen = ["kb-zeile", "s-" + info.art];
  if (!kb.blatt.darfSchreiben) {
    return h("li", { class: klassen.join(" ") },
      h("div", { class: "kb-person nur-lesen" }, inhalt));
  }
  if (t.gesperrt) {
    // Nachtrag 3.2: von KL/SK festgelegt – Fach-/Kurslehrkraft kann nicht ändern.
    klassen.push("gesperrt");
    return h("li", { class: klassen.join(" "), "data-id": t.id },
      h("button", {
        class: "kb-person", type: "button",
        title: `Festgelegt von ${t.festgelegtVon || "der Klassenleitung bzw. Stufenkoordination"}`,
        onclick: () => kbGesperrtHinweis(t),
      }, inhalt, h("span", { class: "vh" }, ", festgelegt – nicht änderbar")),
      h("span", { class: "kb-schloss", title: "festgelegt" }, kbSchloss(), h("span", { class: "vh" }, "festgelegt")));
  }
  return h("li", { class: klassen.join(" "), "data-id": t.id },
    h("button", {
      class: "kb-person", type: "button",
      "aria-pressed": String(v.status !== "Anwesend"),
      title: v.status === "Fehlt" ? "Antippen: anwesend" : "Antippen: fehlt",
      onclick: () => umschalten(t.id),
    }, inhalt),
    h("button", {
      class: "kb-mehr", type: "button",
      "aria-label": `Status für ${t.vorname} ${t.nachname} wählen`,
      "aria-haspopup": "dialog",
      onclick: () => statusDialogOeffnen(t),
    }, h("span", { "aria-hidden": "true" }, "⋯")));
}

/** Antippen des Namens: anwesend ⇄ fehlt (andere Status -> anwesend). */
function umschalten(id) {
  if (!kbFestgelegtOk(id)) return;
  const v = kb.stand.get(id) || { status: "Anwesend", minuten: null };
  statusSetzen(id, v.status === "Anwesend" ? "Fehlt" : "Anwesend", null);
}

function statusSetzen(id, status, minuten) {
  kb.stand.set(id, { status, minuten: status === "Verspaetet" ? minuten : null });
  kb.geaendert = true;
  fehlerIn("kbFehler", "");
  // Nur die eine Zeile neu aufbauen (Fokus bleibt erhalten).
  const t = (kb.blatt.teilnehmer || []).find((x) => x.id === id);
  const alt = el("kbListe").querySelector(`li[data-id="${id}"]`);
  if (t && alt) {
    const fokusMehr = document.activeElement && document.activeElement.classList.contains("kb-mehr") &&
      alt.contains(document.activeElement);
    const fokus = alt.contains(document.activeElement);
    const neu = teilnehmerZeile(t);
    alt.replaceWith(neu);
    if (fokus) neu.querySelector(fokusMehr ? ".kb-mehr" : ".kb-person").focus();
  }
  el("kbAlleDa").hidden = !kb.blatt.darfSchreiben || kbAenderbareAbweichungen().length === 0;
  renderZahlen();
  renderLeiste();
}

function renderZahlen() {
  const d = kb.blatt;
  if (!d) { el("kbZahlen").textContent = ""; return; }
  const n = (d.teilnehmer || []).length;
  const zaehl = {};
  for (const v of kb.stand.values()) zaehl[v.status] = (zaehl[v.status] || 0) + 1;
  const teile = [`${n} Schüler`, `${zaehl.Anwesend || 0} anwesend`];
  if (zaehl.Fehlt) teile.push(`${zaehl.Fehlt} ${zaehl.Fehlt === 1 ? "fehlt" : "fehlen"}`);
  if (zaehl.Verspaetet) teile.push(`${zaehl.Verspaetet} verspätet`);
  if (zaehl.Beurlaubt) teile.push(`${zaehl.Beurlaubt} beurlaubt`);
  if (zaehl.Schulveranstaltung) teile.push(`${zaehl.Schulveranstaltung} Schulveranst.`);
  el("kbZahlen").textContent = teile.join(" · ");
}

function renderLeiste() {
  const d = kb.blatt;
  const knopf = el("kbSpeichern");
  const leiste = el("kbLeiste");
  if (!d) { leiste.hidden = true; return; }
  el("kbHistorieKnopf").hidden = !d.erfasstAm;
  knopf.hidden = !d.darfSchreiben;
  leiste.hidden = knopf.hidden && el("kbHistorieKnopf").hidden;
  leiste.classList.toggle("ungespeichert", kb.geaendert);
  renderFolge();
  knopf.classList.remove("gespeichert");
  if (kb.speichert) {
    knopf.disabled = true;
    knopf.textContent = "Speichern …";
    return;
  }
  if (!d.erfasstAm) {
    knopf.disabled = false;
    knopf.textContent = kbAbweichungen().length === 0 ? "Alle anwesend – speichern" : "Anwesenheit speichern";
  } else if (kb.geaendert) {
    knopf.disabled = false;
    knopf.textContent = "Änderungen speichern";
  } else if (kbMitFolge()) {
    // 1. Stunde schon erfasst, 2. noch nicht: nur übernehmen.
    knopf.disabled = false;
    knopf.textContent = `In die ${d.folge.stunde}. Stunde übernehmen`;
  } else {
    knopf.disabled = true;
    knopf.textContent = "✓ Gespeichert";
    knopf.classList.add("gespeichert");
  }
}

/** Doppelstunde: Schalter bzw. Hinweis über dem Speichern-Knopf. */
function renderFolge() {
  const d = kb.blatt;
  const schalter = el("kbFolgeSchalter");
  const info = el("kbFolgeInfo");
  const f = d && d.darfSchreiben ? d.folge : null;
  schalter.hidden = !kbFolgeMoeglich();
  let text = "";
  if (kbFolgeMoeglich()) {
    el("kbFolge").checked = kb.folgeAn;
    el("kbFolge").disabled = kb.speichert;
    el("kbFolgeText").textContent = `Auch für die ${f.stunde}. Stunde übernehmen (Doppelstunde)`;
    if (kb.folgeAn && kbAbweichungen().some(([, v]) => v.status === "Verspaetet"))
      text = `Verspätete gelten in der ${f.stunde}. Stunde als anwesend.`;
  } else if (f && f.erfasst) {
    text = `Doppelstunde: Die ${f.stunde}. Stunde ist schon erfasst` +
      (f.erfasstVon ? ` (${f.erfasstVon}, ${kurzZeit(f.erfasstAm)}).` : ".");
  }
  info.textContent = text;
  info.hidden = !text;
}

async function stundeSpeichern() {
  const d = kb.blatt;
  if (!d || !d.darfSchreiben || kb.speichert) return;
  if (kb.konflikt) { stundeLaden(); return; }
  kb.speichert = true;
  fehlerIn("kbFehler", "");
  renderLeiste();
  const nr = kb.ladeNr;
  try {
    const antwort = await senden("PUT", "api/klassenbuch/stunde", {
      datum: kb.schluessel.datum,
      stunde: kb.schluessel.stunde,
      grundUNr: kb.schluessel.unr,
      grundLehrer: kb.schluessel.lehrer,
      version: d.version,
      eintraege: kbAbweichungen().map(([id, v]) => ({ schuelerId: id, status: v.status, minuten: v.minuten })),
      folgestundeUebernehmen: kbMitFolge(),
    });
    if (nr !== kb.ladeNr) return;
    kb.speichert = false;
    const folgeAn = kb.folgeAn; // bewusst ausgeschaltet bleibt aus
    blattUebernehmen(antwort);
    kb.folgeAn = folgeAn;
    renderLeiste();
    toast(antwort.meldung || "Anwesenheit gespeichert.");
  } catch (f) {
    kb.speichert = false;
    if (nr !== kb.ladeNr) return;
    const text = aktionsFehler(f);
    if (text === null) return;
    kb.konflikt = f instanceof HttpFehler && f.status === 409 && !!(f.daten && f.daten.konflikt);
    renderLeiste();
    if (kb.konflikt) {
      el("kbSpeichern").disabled = false;
      el("kbSpeichern").textContent = "Neu laden";
      fehlerIn("kbFehler", text + " Deine Eingaben in dieser Ansicht gehen dabei verloren.");
    } else {
      fehlerIn("kbFehler", text);
    }
  }
}

function alleAnwesend() {
  if (!kb.blatt || !kb.blatt.darfSchreiben) return;
  // Festgelegte Fehlstunden bleiben unberührt (nur einzeln änderbar).
  for (const [id] of kbAenderbareAbweichungen()) kb.stand.set(id, { status: "Anwesend", minuten: null });
  kb.geaendert = true;
  el("kbListe").replaceChildren(...(kb.blatt.teilnehmer || []).map(teilnehmerZeile));
  el("kbAlleDa").hidden = true;
  renderZahlen();
  renderLeiste();
}

// ---- Statusdialog ----

function statusDialogOeffnen(t) {
  if (!kbFestgelegtOk(t.id)) return;
  kb.statusFuer = t;
  const v = kb.stand.get(t.id) || { status: "Anwesend", minuten: null };
  el("ksName").textContent = `${t.vorname} ${t.nachname}`;
  el("ksKlasse").textContent = t.klasse || "";
  el("ksWahl").replaceChildren(...KB_REIHENFOLGE.map((status) =>
    h("button", {
      class: `ks-knopf s-${KB_STATUS[status].art}`, type: "button",
      "aria-pressed": String(v.status === status),
      onclick: () => statusGewaehlt(status),
    }, h("span", { class: "ks-punkt", "aria-hidden": "true" }), KB_STATUS[status].lang)));
  el("ksMinuten").value = v.status === "Verspaetet" && v.minuten ? v.minuten : "";
  el("ksMinutenFeld").hidden = v.status !== "Verspaetet";
  fehlerIn("ksFehler", "");
  dialogAuf(el("kbStatusDialog"));
  const aktiv = el("ksWahl").querySelector('[aria-pressed="true"]');
  if (aktiv) aktiv.focus();
}

function statusGewaehlt(status) {
  const t = kb.statusFuer;
  if (!t) return;
  if (status === "Verspaetet") {
    for (const b of el("ksWahl").querySelectorAll("button"))
      b.setAttribute("aria-pressed", String(b.classList.contains("s-spaet")));
    el("ksMinutenFeld").hidden = false;
    el("ksMinuten").focus();
    return;
  }
  statusSetzen(t.id, status, null);
  statusDialogSchliessen();
}

function minutenUebernehmen() {
  const t = kb.statusFuer;
  if (!t) return;
  const m = Number(el("ksMinuten").value);
  if (!Number.isInteger(m) || m < 1 || m > KB_MAX_MINUTEN) {
    fehlerIn("ksFehler", `Bitte die Minuten angeben (1–${KB_MAX_MINUTEN}).`);
    el("ksMinuten").focus();
    return;
  }
  statusSetzen(t.id, "Verspaetet", m);
  statusDialogSchliessen();
}

function statusDialogSchliessen() {
  const t = kb.statusFuer;
  dialogZu(el("kbStatusDialog"));
  if (t) {
    const knopf = el("kbListe").querySelector(`li[data-id="${t.id}"] .kb-mehr`);
    if (knopf) knopf.focus();
  }
}

// ---- Historie ----

async function historieOeffnen() {
  if (!kb.schluessel) return;
  el("khListe").replaceChildren();
  el("khInfo").textContent = "Wird geladen …";
  dialogAuf(el("kbHistorieDialog"));
  const nr = kb.ladeNr;
  try {
    const liste = await senden("GET", kbStundeUrl("stunde/historie"));
    if (nr !== kb.ladeNr) return;
    el("khInfo").textContent = liste.length ? "" : "Noch keine Änderungen.";
    el("khListe").replaceChildren(...liste.map((p) => h("li", { class: "p-eintrag" },
      h("span", { class: "p-zeit" }, `${datumZeit(p.zeit)} · ${p.akteur}`),
      h("span", { class: "p-text" }, p.art === "Erfasst"
        ? `Anwesenheit ${p.neu || "erfasst"}`
        : [h("strong", null, p.schueler || ""), ": ", p.alt ? korrektur(p.alt, p.neu) : p.neu]))));
  } catch (f) {
    if (nr !== kb.ladeNr) return;
    const text = aktionsFehler(f);
    if (text === null) return;
    el("khInfo").textContent = text;
  }
}

// ---------------------------------------------------------------------
//  Gruppe zusammenstellen
// ---------------------------------------------------------------------

function gruppeOeffnen(schluessel, zurueck) {
  if (kb.ansicht === "stunde" && !kbVerwerfenErlaubt()) return;
  kb.geaendert = false;
  kb.gruppeZurueck = zurueck || null;
  kb.gruppe = null;
  kb.gruppeWahl = new Set();
  kb.gruppeGeaendert = false;
  el("kgFilter").value = "";
  zeigeKlassenbuch("gruppe");
  renderGruppe();
  gruppeLaden(schluessel);
}

async function gruppeLaden(schluessel) {
  const nr = ++kb.ladeNr;
  fehlerIn("kgFehler", "");
  document.body.classList.add("laedt");
  try {
    const d = await senden("GET", `api/klassenbuch/gruppe?schluessel=${encodeURIComponent(schluessel)}`);
    if (nr !== kb.ladeNr) return;
    gruppeUebernehmen(d);
  } catch (f) {
    if (nr !== kb.ladeNr) return;
    const text = aktionsFehler(f);
    if (text === null) return;
    kb.gruppe = null;
    renderGruppe();
    fehlerIn("kgFehler", text);
  } finally {
    if (nr === kb.ladeNr) document.body.classList.remove("laedt");
  }
}

function gruppeUebernehmen(d) {
  kb.gruppe = d;
  kb.gruppeWahl = new Set(d.pool.filter((p) => p.mitglied).map((p) => p.id));
  kb.gruppeGeaendert = false;
  renderGruppe();
}

function renderGruppe() {
  const g = kb.gruppe;
  el("kgLeiste").hidden = !g || !g.darfBearbeiten;
  el("kgErklaerung").hidden = !g;
  el("kgWerkzeuge").hidden = !g || g.pool.length === 0;
  if (!g) {
    el("kgTitel").textContent = "";
    el("kgInfo").textContent = "";
    el("kgStand").textContent = "";
    el("kgKlassen").replaceChildren();
    return;
  }
  el("titel").textContent = "Gruppenliste";
  el("kgTitel").textContent = `${g.fach} · ${g.klassen.join(", ")}`;
  el("kgInfo").textContent = `${g.lehrer.join(", ")} · Gruppe ${g.schluessel}`;
  el("kgStand").replaceChildren(...(g.zusammengestellt
    ? [h("span", { class: "kb-ok" }, "✓ zusammengestellt"),
      g.bearbeitetVon ? ` von ${g.bearbeitetVon} ${g.bearbeitetAm ? kurzZeit(g.bearbeitetAm) : ""}` : ""]
    : [h("span", { class: "kb-offen" }, "Noch nicht zusammengestellt")]));
  el("kgErklaerung").textContent = g.darfBearbeiten
    ? "Hake an, wer zu dieser Gruppe gehört. Die Liste gilt für das ganze Schuljahr und lässt sich jederzeit ändern."
    : "Nur ansehen – zusammenstellen dürfen die Lehrkräfte der Gruppe und die Verwaltung.";
  el("kgAlle").hidden = el("kgKeine").hidden = !g.darfBearbeiten;
  renderGruppeKlassen();
  renderGruppeLeiste();
}

function renderGruppeKlassen() {
  const g = kb.gruppe;
  if (!g) return;
  const filter = el("kgFilter").value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const passt = (p) => filter.every((w) => `${p.vorname} ${p.nachname}`.toLowerCase().includes(w));
  const nachKlasse = new Map();
  for (const p of g.pool) {
    const k = p.klasse || "ohne Klasse";
    if (!nachKlasse.has(k)) nachKlasse.set(k, []);
    nachKlasse.get(k).push(p);
  }
  const bloecke = [];
  for (const [klasse, alle] of [...nachKlasse.entries()].sort((a, b) => sortierer.compare(a[0], b[0]))) {
    const sichtbar = alle.filter(passt);
    if (sichtbar.length === 0) continue;
    const gewaehlt = alle.filter((p) => kb.gruppeWahl.has(p.id)).length;
    bloecke.push(h("fieldset", { class: "kg-klasse" },
      h("legend", { class: "kg-legende" },
        h("span", { class: "kg-kname" }, klasse),
        h("span", { class: "kg-kzahl" }, `${gewaehlt} von ${alle.length}`)),
      g.darfBearbeiten
        ? h("div", { class: "knopfreihe klein kg-kknoepfe" },
            h("button", { class: "textknopf klein", type: "button", onclick: () => klasseWaehlen(alle, true) },
              h("span", { "aria-hidden": "true" }, "alle"), h("span", { class: "vh" }, `Alle aus ${klasse} wählen`)),
            h("button", { class: "textknopf klein", type: "button", onclick: () => klasseWaehlen(alle, false) },
              h("span", { "aria-hidden": "true" }, "keine"), h("span", { class: "vh" }, `Niemanden aus ${klasse} wählen`)))
        : null,
      h("div", { class: "kg-namen" }, sichtbar.map((p) =>
        h("label", { class: "kg-name" },
          h("input", {
            type: "checkbox", value: p.id,
            checked: kb.gruppeWahl.has(p.id),
            disabled: !g.darfBearbeiten,
            onchange: (e) => {
              if (e.target.checked) kb.gruppeWahl.add(p.id); else kb.gruppeWahl.delete(p.id);
              kb.gruppeGeaendert = true;
              renderGruppeZahlen();
            },
          }),
          h("span", null, `${p.nachname}, ${p.vorname}`))))));
  }
  if (g.pool.length === 0)
    bloecke.push(h("p", { class: "leer" }, "In den Klassen dieser Gruppe sind keine Schülerinnen und Schüler hinterlegt."));
  else if (bloecke.length === 0)
    bloecke.push(h("p", { class: "leer" }, "Kein Name passt zum Filter."));
  el("kgKlassen").replaceChildren(...bloecke);
}

/** Nur Zähler und Leiste aktualisieren (Häkchen bleiben, Fokus bleibt). */
function renderGruppeZahlen() {
  const g = kb.gruppe;
  if (!g) return;
  for (const fs of el("kgKlassen").querySelectorAll("fieldset.kg-klasse")) {
    const klasse = fs.querySelector(".kg-kname").textContent;
    const alle = g.pool.filter((p) => (p.klasse || "ohne Klasse") === klasse);
    fs.querySelector(".kg-kzahl").textContent = `${alle.filter((p) => kb.gruppeWahl.has(p.id)).length} von ${alle.length}`;
  }
  renderGruppeLeiste();
}

function renderGruppeLeiste() {
  const g = kb.gruppe;
  if (!g) return;
  const n = kb.gruppeWahl.size;
  const knopf = el("kgSpeichern");
  knopf.textContent = `Gruppe speichern (${n} Schüler)`;
  knopf.disabled = !kb.gruppeGeaendert && g.zusammengestellt;
  el("kgLeiste").classList.toggle("ungespeichert", kb.gruppeGeaendert);
}

function klasseWaehlen(liste, an) {
  for (const p of liste) { if (an) kb.gruppeWahl.add(p.id); else kb.gruppeWahl.delete(p.id); }
  kb.gruppeGeaendert = true;
  renderGruppeKlassen();
  renderGruppeLeiste();
}

async function gruppeSpeichern() {
  const g = kb.gruppe;
  if (!g || !g.darfBearbeiten) return;
  if (kb.gruppeWahl.size === 0 &&
      !window.confirm("Niemand ist ausgewählt – die Gruppe hat dann keine Schüler. Trotzdem speichern?")) return;
  const knopf = el("kgSpeichern");
  knopf.disabled = true;
  knopf.textContent = "Speichern …";
  fehlerIn("kgFehler", "");
  const nr = kb.ladeNr;
  try {
    const d = await senden("PUT", `api/klassenbuch/gruppe?schluessel=${encodeURIComponent(g.schluessel)}`,
      { mitglieder: [...kb.gruppeWahl] });
    if (nr !== kb.ladeNr) return;
    toast("Gruppe gespeichert.");
    if (kb.gruppeZurueck) {
      gruppeUebernehmen(d);
      kbZurueck();
    } else {
      gruppeUebernehmen(d);
    }
  } catch (f) {
    if (nr !== kb.ladeNr) return;
    renderGruppeLeiste();
    const text = aktionsFehler(f);
    if (text !== null) fehlerIn("kgFehler", text);
  }
}

// ---------------------------------------------------------------------
//  Meine Gruppen
// ---------------------------------------------------------------------

function gruppenOeffnen() {
  if (klassenbuchOffen() && !kbVerwerfenErlaubt()) return;
  kb.gruppe = null;
  kb.gruppeGeaendert = false;
  kb.geaendert = false;
  kb.gruppeZurueck = null;
  zeigeKlassenbuch("gruppen");
  gruppenLaden();
}

async function gruppenLaden() {
  const nr = ++kb.ladeNr;
  el("kgsInfo").textContent = "Wird geladen …";
  document.body.classList.add("laedt");
  try {
    const liste = await senden("GET", "api/klassenbuch/gruppen");
    if (nr !== kb.ladeNr) return;
    kb.gruppen = liste;
    renderGruppen();
  } catch (f) {
    if (nr !== kb.ladeNr) return;
    const text = aktionsFehler(f);
    if (text !== null) el("kgsInfo").textContent = text;
  } finally {
    if (nr === kb.ladeNr) document.body.classList.remove("laedt");
  }
}

function renderGruppen() {
  const liste = kb.gruppen || [];
  const offen = liste.filter((g) => !g.zusammengestellt).length;
  el("kgsInfo").textContent = liste.length === 0
    ? "Keine Gruppen mit eigener Schülergruppe im aktuellen Stundenplan."
    : `${liste.length} ${liste.length === 1 ? "Gruppe" : "Gruppen"}` +
      (offen ? `, davon ${offen} noch nicht zusammengestellt` : ", alle zusammengestellt");
  el("kgsListe").replaceChildren(...liste.map((g) => h("li", null,
    h("button", {
      class: "k-zeile" + (g.zusammengestellt ? "" : " kg-offen"), type: "button",
      onclick: () => gruppeOeffnen(g.schluessel, "gruppen"),
    },
    h("span", { class: "k-oben" },
      h("span", { class: "k-name" }, `${g.fach} · ${g.klassen.join(", ")}`),
      h("span", { class: "k-rechts" }, g.lehrer.join(", "))),
    h("span", { class: "k-unten" }, g.schluessel),
    h("span", { class: "k-marken" },
      g.zusammengestellt
        ? h("span", { class: "marke" }, `${g.anzahl} Schüler`)
        : h("span", { class: "marke start" }, "noch nicht zusammengestellt"))))));
}

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function klassenbuchMenueAnpassen() {
  const b = el("kbGruppenKnopf");
  const a = state.anmeldung;
  b.hidden = !istPersonal() || !(a.rolle === "Admin" || eigenesKuerzel());
  b.textContent = a && a.rolle === "Admin" ? "Gruppenlisten" : "Meine Gruppen";
}

function klassenbuchInit() {
  el("kbZurueck").addEventListener("click", kbZurueck);
  el("kbSpeichern").addEventListener("click", stundeSpeichern);
  el("kbAlleDa").addEventListener("click", alleAnwesend);
  el("kbHistorieKnopf").addEventListener("click", historieOeffnen);
  el("kbFolge").addEventListener("change", (e) => { kb.folgeAn = e.target.checked; renderLeiste(); });
  el("kbGruppenKnopf").addEventListener("click", () => {
    kontoSchliessen();
    fehlzeitenSchliessen();
    if (verwaltungOffen()) {
      if (!startpasswoerterVerwerfen()) return;
      verwaltungLeeren();
    }
    gruppenOeffnen();
  });

  // Statusdialog
  el("ksZu").addEventListener("click", statusDialogSchliessen);
  el("ksOk").addEventListener("click", minutenUebernehmen);
  el("ksMinuten").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); minutenUebernehmen(); }
  });
  for (const b of document.querySelectorAll("#ksSchnell button"))
    b.addEventListener("click", () => { el("ksMinuten").value = b.dataset.min; minutenUebernehmen(); });
  el("kbStatusDialog").addEventListener("close", () => { kb.statusFuer = null; });
  hintergrundSchliesst(el("kbStatusDialog"), () => dialogZu(el("kbStatusDialog")));

  // Historie
  el("khZu").addEventListener("click", () => dialogZu(el("kbHistorieDialog")));
  el("kbHistorieDialog").addEventListener("close", () => el("khListe").replaceChildren());
  hintergrundSchliesst(el("kbHistorieDialog"), () => dialogZu(el("kbHistorieDialog")));

  // Gruppe
  el("kgSpeichern").addEventListener("click", gruppeSpeichern);
  el("kgAbbrechen").addEventListener("click", kbZurueck);
  el("kgAlle").addEventListener("click", () => kb.gruppe && klasseWaehlen(kb.gruppe.pool, true));
  el("kgKeine").addEventListener("click", () => kb.gruppe && klasseWaehlen(kb.gruppe.pool, false));
  el("kgFilter").addEventListener("input", renderGruppeKlassen);

  // Seite verlassen mit Ungespeichertem: Browser fragt nach.
  window.addEventListener("beforeunload", (e) => {
    if (klassenbuchOffen() && kbUngespeichert()) { e.preventDefault(); e.returnValue = ""; }
  });
}
