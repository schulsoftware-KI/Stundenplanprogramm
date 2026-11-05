"use strict";
// =====================================================================
//  Stundenplan-PWA – Klassenbucheinträge (Phase 3, Teilschritt 3.4)
//
//  Entscheidungen 31–39 (Nutzer 04.11.2026):
//  - Klasseneintrag (Vorkommnis, Hinweis, Lob) betrifft die Lerngruppe der
//    Stunde; Schülereintrag (Lob, Hinweis, Ermahnung, Hausaufgabe vergessen,
//    Material vergessen) – für eine oder mehrere Personen, je Person ein
//    eigener Datensatz. Text bis 500 Zeichen, Pflicht bei Hinweis,
//    Ermahnung und Vorkommnis.
//  - Stundenblatt: Karte „Einträge" mit Knopf „Eintrag für die Klasse";
//    Schülereinträge über „⋯" beim Namen (Statusdialog, Bereich „Einträge").
//    Ein Eintrag wird sofort gespeichert – nicht über die Speichern-Leiste.
//  - Ändern/Löschen nur Verfasser und Admin (Dialog „Eintrag").
//  - Schülerdetail: Abschnitt „Einträge" (Zähler je Kategorie, Liste);
//    Klassenleitung/Stufenkoordination/Admin auch „Eintrag ohne Stunde".
//  - Reiter „Einträge" (KL/SK/Admin): alle Einträge der Klasse/Stufe mit
//    Filter nach Kategorie. „Meine Kurse": Fehlzeiten | Themen | Einträge.
//  - Schüler sehen keine Einträge. Keine Automatik, nur Zähler.
//
//  Datenschutz: Einträge nur im Arbeitsspeicher – nie im Browser-Speicher;
//  beim Verlassen geleert. Hinweis im Formular: sachlich, keine
//  Gesundheitsdaten, keine Vermutungen.
//
//  Präfix ke… (klassische Skripte teilen sich einen Namensraum).
//  Wird nach hausaufgaben.js und VOR app.js geladen; Einstieg eintraegeInit().
// =====================================================================

const KE_KAT = {
  Lob:                  { text: "Lob",                   kurz: "Lob",                art: "lob",        pflicht: false },
  Hinweis:              { text: "Hinweis",               kurz: "Hinweis",            art: "hinweis",    pflicht: true },
  Ermahnung:            { text: "Ermahnung",             kurz: "Ermahnung",          art: "ermahnung",  pflicht: true },
  HausaufgabeVergessen: { text: "Hausaufgabe vergessen", kurz: "HA vergessen",       art: "vergessen",  pflicht: false },
  MaterialVergessen:    { text: "Material vergessen",    kurz: "Material vergessen", art: "vergessen",  pflicht: false },
  Vorkommnis:           { text: "Vorkommnis",            kurz: "Vorkommnis",         art: "vorkommnis", pflicht: true },
};
const KE_SCHUELER = ["Lob", "Hinweis", "Ermahnung", "HausaufgabeVergessen", "MaterialVergessen"];
const KE_KLASSE = ["Vorkommnis", "Hinweis", "Lob"];
const KE_MAX_TEXT = 500;

const ke = {
  stunde: null,        // Antwort GET api/eintraege/stunde (Stundenblatt)
  stundeFehler: "",
  ladeNr: 0,
  statusForm: null,    // Formular im Statusdialog (Steuerobjekt aus keFormBauen)
  dialog: null,        // Eintragsdialog: { eintrag, opts } bzw. { neu: true }
  dialogForm: null,    // Formular im Eintragsdialog
  schuelerLadeNr: 0,
  liste: null,         // Listenansicht (Klasse/Stufe bzw. Unterricht)
  filter: "",          // Kategorie-Filter der Liste ("" = alle)
};
let keFormNr = 0;

const keKat = (k) => KE_KAT[k] || { text: k, kurz: k, art: "hinweis", pflicht: false };

function keMarke(k, kurz = false) {
  const c = keKat(k);
  return h("span", { class: `marke ke-marke k-${c.art}` }, kurz ? c.kurz : c.text);
}

/** „L1, heute 10:05 · geändert von admin" */
function keVon(e) {
  const teile = [`${e.verfasstVon}, ${kurzZeit(e.verfasstAm)}`];
  if (e.geaendertAm)
    teile.push(e.geaendertVon && !gleich(e.geaendertVon, e.verfasstVon) ? `geändert von ${e.geaendertVon}` : "geändert");
  return teile.join(" · ");
}

/** Text als Absatz (Zeilenumbrüche bleiben, kein innerHTML). */
const keText = (text, klasse) => h("span", { class: klasse }, text);

// ---------------------------------------------------------------------
//  Formular (Stundenblatt, Statusdialog, Eintragsdialog)
// ---------------------------------------------------------------------

/**
 * Baut das Eintragsformular in `box` und liefert ein Steuerobjekt { geaendert(), fokus() }.
 * opts: art ("Schueler" | "Klasse"), eintrag (Ändern), schueler [{ id, name }] (vorausgewählt),
 *       weitere [{ id, name, klasse }] (weitere Schüler der Stunde), datum (true: Datumsfeld),
 *       speichern(werte) -> Promise, fertig(antwort), abbrechen() (optional).
 */
function keFormBauen(box, opts) {
  const nr = ++keFormNr;
  const id = (x) => `ke${nr}-${x}`;
  const e = opts.eintrag || null;
  const kats = opts.art === "Klasse" ? KE_KLASSE : KE_SCHUELER;
  let kategorie = e ? e.kategorie : null;
  const zusatz = new Set();
  let laeuft = false;
  let fertig = false;

  const fehler = h("p", { class: "fehler klein", role: "alert", hidden: true });
  const pflicht = h("span", { class: "ke-pflicht" });
  const zaehler = h("span", { class: "ke-zaehler" });
  const text = h("textarea", {
    id: id("text"), class: "eingabe ke-text", rows: "2", maxlength: String(KE_MAX_TEXT),
    "aria-describedby": id("regel"),
  });
  text.value = e && e.text ? e.text : "";
  const datum = opts.datum ? h("input", { id: id("datum"), class: "eingabe ke-datum", type: "date", max: heuteIso() }) : null;
  if (datum) datum.value = heuteIso();
  const knopf = h("button", { class: "hauptknopf klein", type: "button" });

  const chips = h("div", { class: "ke-kats", role: "group", "aria-labelledby": id("kat") },
    kats.map((k) => h("button", {
      class: `kb-chip ke-chip k-${keKat(k).art}`, type: "button", "data-k": k,
      "aria-pressed": String(k === kategorie),
      onclick: () => {
        kategorie = k;
        fehlerZeigen("");
        aktualisieren();
        if (keKat(k).pflicht && !text.value.trim()) text.focus();
      },
    }, keKat(k).text)));

  // Weitere Schüler derselben Stunde (je Person ein eigener Eintrag).
  let weitere = null;
  if (opts.weitere && opts.weitere.length) {
    const mehrereKlassen = new Set(opts.weitere.map((p) => p.klasse)).size > 1;
    const liste = h("div", { class: "ke-weitere-liste", id: id("weitere"), hidden: true },
      opts.weitere.map((p) => h("label", { class: "kg-name" },
        h("input", {
          type: "checkbox", value: p.id,
          onchange: (ev) => {
            if (ev.target.checked) zusatz.add(p.id); else zusatz.delete(p.id);
            aktualisieren();
          },
        }),
        h("span", null, p.name, mehrereKlassen && p.klasse ? h("span", { class: "fz-klasse" }, p.klasse) : null))));
    const auf = h("button", {
      class: "textknopf klein ke-weitere-auf", type: "button", "aria-expanded": "false", "aria-controls": id("weitere"),
      onclick: () => {
        const zeigen = liste.hidden;
        liste.hidden = !zeigen;
        auf.setAttribute("aria-expanded", String(zeigen));
      },
    }, "Auch für weitere Schüler …");
    weitere = h("div", { class: "ke-weitere" }, auf, liste);
  }

  function fehlerZeigen(t) {
    fehler.textContent = t || "";
    fehler.hidden = !t;
  }

  function hoehe() {
    if (text.offsetParent === null) return;
    text.style.height = "auto";
    text.style.height = Math.min(text.scrollHeight + 2, 260) + "px";
  }

  function aktualisieren() {
    for (const b of chips.querySelectorAll("button")) b.setAttribute("aria-pressed", String(b.dataset.k === kategorie));
    pflicht.textContent = !kategorie ? "" : keKat(kategorie).pflicht ? " (nötig)" : " (freiwillig)";
    const n = text.value.length;
    zaehler.textContent = n > KE_MAX_TEXT - 100 ? `${n}/${KE_MAX_TEXT}` : "";
    const anzahl = (opts.schueler || []).length + zusatz.size;
    knopf.disabled = laeuft;
    knopf.textContent = laeuft ? "Speichern …"
      : e ? "Änderung speichern"
      : opts.art === "Schueler" && anzahl > 1 ? `Eintrag speichern (${anzahl} Schüler)`
      : "Eintrag speichern";
    hoehe();
  }

  async function speichern() {
    if (laeuft) return;
    if (!kategorie) {
      fehlerZeigen("Bitte eine Kategorie wählen.");
      chips.querySelector("button").focus();
      return;
    }
    const t = kbNormText(text.value, true);
    if (!t && keKat(kategorie).pflicht) {
      fehlerZeigen(`Bitte bei „${keKat(kategorie).text}“ kurz sachlich beschreiben, worum es geht.`);
      text.focus();
      return;
    }
    if (t && t.length > KE_MAX_TEXT) {
      fehlerZeigen(`Der Text ist zu lang (höchstens ${KE_MAX_TEXT} Zeichen).`);
      text.focus();
      return;
    }
    if (datum && (!istIso(datum.value) || datum.value > heuteIso())) {
      fehlerZeigen(istIso(datum.value) ? "Das Datum liegt in der Zukunft." : "Bitte ein Datum angeben.");
      datum.focus();
      return;
    }
    laeuft = true;
    fehlerZeigen("");
    aktualisieren();
    try {
      const antwort = await opts.speichern({
        kategorie,
        text: t,
        datum: datum ? datum.value : null,
        schuelerIds: (opts.schueler || []).map((s) => s.id).concat([...zusatz]),
        version: e ? e.version : null,
      });
      laeuft = false;
      fertig = true;
      opts.fertig(antwort);
    } catch (f) {
      laeuft = false;
      aktualisieren();
      const m = aktionsFehler(f);
      if (m !== null) fehlerZeigen(m);
    }
  }

  knopf.addEventListener("click", speichern);
  text.addEventListener("input", () => { fehlerZeigen(""); aktualisieren(); });
  text.addEventListener("keydown", (ev) => {
    if (ev.key === "Enter" && (ev.ctrlKey || ev.metaKey)) { ev.preventDefault(); speichern(); }
  });

  box.replaceChildren(h("div", { class: "ke-form" },
    h("p", { id: id("kat"), class: "feld ke-feld" }, "Kategorie"),
    chips,
    datum ? [h("label", { class: "feld ke-feld", for: id("datum") }, "Datum"), datum] : null,
    h("div", { class: "ke-textkopf" },
      h("label", { class: "feld ke-feld", for: id("text") }, "Text", pflicht),
      zaehler),
    text,
    h("p", { id: id("regel"), class: "regel" }, "Sachlich bleiben: keine Gesundheitsdaten, keine Vermutungen."),
    weitere,
    fehler,
    h("div", { class: "knopfreihe ke-knoepfe" },
      knopf,
      opts.abbrechen ? h("button", { class: "textknopf", type: "button", onclick: () => opts.abbrechen() }, "Abbrechen") : null)));
  box.hidden = false;
  aktualisieren();

  return {
    geaendert: () => !fertig && (
      kategorie !== (e ? e.kategorie : null) ||
      (kbNormText(text.value, true) || null) !== (e && e.text ? e.text : null) ||
      zusatz.size > 0),
    fokus: () => {
      const b = chips.querySelector('[aria-pressed="true"]') || chips.querySelector("button");
      if (b) b.focus();
      hoehe();
    },
  };
}

// ---------------------------------------------------------------------
//  Stundenblatt: Karte „Einträge", Marken in der Liste, Statusdialog
// ---------------------------------------------------------------------

const keHatEintraege = () => !!(ke.stunde && ke.stunde.eintraege.length);

const keStundeSchluessel = () => ({
  datum: kb.schluessel.datum,
  stunde: kb.schluessel.stunde,
  grundUNr: kb.schluessel.unr,
  grundLehrer: kb.schluessel.lehrer,
});

/** Stundenblatt verlassen bzw. neu geöffnet: alles zur Stunde entfernen. */
function keStundeLeeren() {
  ke.ladeNr++;
  ke.stunde = null;
  ke.stundeFehler = "";
  keStatusFormZu();
  el("kbEintraege").hidden = true;
  el("keKarteListe").replaceChildren();
  el("keKarteInfo").textContent = "";
  el("keKarteSofort").textContent = "";
  el("ksEListe").replaceChildren();
  el("ksEintrag").hidden = true;
  if (ke.dialog) dialogZu(el("keDialog"));
}

/** Nach dem Laden des Stundenblatts (klassenbuch.js): Einträge der Stunde holen. */
async function keStundeLaden() {
  if (!kb.schluessel) return;
  const nr = ++ke.ladeNr;
  const s = kb.schluessel;
  try {
    const d = await senden("GET", `api/eintraege/stunde?datum=${encodeURIComponent(s.datum)}&stunde=${s.stunde}` +
      `&unr=${s.unr}&lehrer=${encodeURIComponent(s.lehrer)}`);
    if (nr !== ke.ladeNr) return;
    keStundeUebernehmen(d);
  } catch (f) {
    if (nr !== ke.ladeNr) return;
    const t = aktionsFehler(f);
    if (t === null) return;
    ke.stunde = null;
    ke.stundeFehler = t;
    keKarteRendern();
  }
}

function keStundeUebernehmen(d) {
  ke.stunde = d;
  ke.stundeFehler = "";
  keKarteRendern();
  keListeMarkenAktualisieren();
  if (kb.blatt) renderLeiste();   // „Änderungen" gibt es auch, wenn nur Einträge da sind
  if (el("kbStatusDialog").open && kb.statusFuer) keStatusBereich(kb.statusFuer);
}

function keKarteRendern() {
  const karte = el("kbEintraege");
  const d = ke.stunde;
  if (!kb.blatt || (!d && !ke.stundeFehler)) { karte.hidden = true; return; }
  if (!d) {
    karte.hidden = false;
    el("keKarteTitel").textContent = "Einträge";
    el("keKlasseKnopf").hidden = true;
    el("keKarteListe").replaceChildren();
    el("keKarteInfo").textContent = ke.stundeFehler;
    el("keKarteInfo").hidden = false;
    el("keKarteSofort").hidden = true;
    return;
  }
  const n = d.eintraege.length;
  karte.hidden = !d.darfSchreiben && n === 0;
  el("keKarteTitel").textContent = n ? `Einträge (${n})` : "Einträge";
  el("keKlasseKnopf").hidden = !d.darfSchreiben;
  el("keKarteListe").replaceChildren(...d.eintraege.map(keKartenZeile));
  const info = d.darfSchreiben
    ? (kb.blatt.darfSchreiben && d.schuelerErlaubt.length ? "Für einzelne Schüler: „⋯“ beim Namen." : "")
    : (d.sperrgrund || "");
  el("keKarteInfo").textContent = info;
  el("keKarteInfo").hidden = !info;
  // Nachtrag 3.4: Einträge sind keine Anwesenheit – sie werden sofort gespeichert.
  const sofort = d.darfSchreiben ? keSofortText() : "";
  el("keKarteSofort").textContent = sofort;
  el("keKarteSofort").hidden = !sofort;
}

/** Hinweis, dass Einträge sofort gespeichert werden und wofür „Speichern“ unten gilt. */
function keSofortText() {
  const b = kb.blatt;
  const anw = !!(b && b.darfSchreiben);
  const inh = !!(b && b.inhalt && b.inhalt.darfSchreiben);
  const wofuer = anw && inh ? "Anwesenheit, Thema und Hausaufgabe"
    : anw ? "die Anwesenheit"
    : inh ? "Thema und Hausaufgabe" : "";
  return "Einträge werden sofort gespeichert." + (wofuer ? ` „Speichern“ unten gilt für ${wofuer}.` : "");
}

function keKartenZeile(e) {
  return h("li", null, h("button", {
    class: `ke-kzeile k-${keKat(e.kategorie).art}`, type: "button", "aria-haspopup": "dialog",
    onclick: () => keDetailOeffnen(e, { nachher: keStundeLaden }),
  },
  h("span", { class: "ke-k-oben" },
    keMarke(e.kategorie),
    h("span", { class: "ke-k-wer" }, e.art === "Klasse" ? "ganze Lerngruppe" : e.schueler || "")),
  e.text ? keText(e.text, "ke-k-text") : null,
  h("span", { class: "ke-k-von" }, keVon(e))));
}

/** Kleine Marken unter dem Namen im Stundenblatt (Kategorien der Einträge dieser Stunde). */
function keMarken(id) {
  if (!ke.stunde) return null;
  const kats = [...new Set(ke.stunde.eintraege.filter((e) => e.schuelerId === id).map((e) => e.kategorie))];
  if (!kats.length) return null;
  return h("span", { class: "ke-mini" },
    h("span", { class: "vh" }, "Einträge: "),
    kats.map((k) => h("span", { class: `ke-mini-k k-${keKat(k).art}` }, keKat(k).kurz)));
}

/** Teilnehmerliste mit den Marken neu aufbauen – der Fokus bleibt, wo er war. */
function keListeMarkenAktualisieren() {
  if (!kb.blatt || !klassenbuchOffen() || kb.ansicht !== "stunde") return;
  const liste = el("kbListe");
  const aktiv = document.activeElement;
  const li = aktiv && aktiv.closest ? aktiv.closest("#kbListe li[data-id]") : null;
  const fokusId = li ? li.dataset.id : null;
  const fokusMehr = !!(li && aktiv.classList.contains("kb-mehr"));
  liste.replaceChildren(...(kb.blatt.teilnehmer || []).map(teilnehmerZeile));
  if (fokusId) {
    const neu = liste.querySelector(`li[data-id="${fokusId}"]`);
    const ziel = neu && neu.querySelector(fokusMehr ? ".kb-mehr" : ".kb-person");
    if (ziel) ziel.focus();
  }
}

/** Statusdialog (klassenbuch.js) geöffnet bzw. aktualisiert: Bereich „Einträge" für diese Person. */
function keStatusBereich(t) {
  const d = ke.stunde;
  const eigene = d ? d.eintraege.filter((e) => e.schuelerId === t.id) : [];
  const darf = !!(d && d.darfSchreiben && d.schuelerErlaubt.includes(t.id));
  el("ksEintrag").hidden = !darf && eigene.length === 0;
  el("ksEListe").replaceChildren(...eigene.map((e) => h("li", null, h("button", {
    class: `ke-kzeile k-${keKat(e.kategorie).art}`, type: "button", "aria-haspopup": "dialog",
    onclick: () => {
      if (!keStatusVerwerfenErlaubt()) return;
      keStatusFormZu();
      dialogZu(el("kbStatusDialog"));
      keDetailOeffnen(e, { nachher: keStundeLaden });
    },
  },
  h("span", { class: "ke-k-oben" }, keMarke(e.kategorie)),
  e.text ? keText(e.text, "ke-k-text") : null,
  h("span", { class: "ke-k-von" }, keVon(e))))));
  el("ksENeu").hidden = !darf || !!ke.statusForm;
}

function keStatusFormAuf() {
  const t = kb.statusFuer;
  const d = ke.stunde;
  if (!t || !d || !d.darfSchreiben) return;
  el("ksENeu").hidden = true;
  const andere = (kb.blatt.teilnehmer || [])
    .filter((x) => x.id !== t.id && d.schuelerErlaubt.includes(x.id))
    .map((x) => ({ id: x.id, name: `${x.nachname}, ${x.vorname}`, klasse: x.klasse }));
  ke.statusForm = keFormBauen(el("ksEForm"), {
    art: "Schueler",
    schueler: [{ id: t.id, name: `${t.vorname} ${t.nachname}` }],
    weitere: andere,
    speichern: (w) => senden("POST", "api/eintraege/stunde", {
      ...keStundeSchluessel(), art: "Schueler", schuelerIds: w.schuelerIds, kategorie: w.kategorie, text: w.text,
    }),
    fertig: (antwort) => {
      keStatusFormZu();
      keStundeUebernehmen(antwort);
      toast(antwort.meldung || "Eintrag gespeichert.");
      if (!el("ksENeu").hidden) el("ksENeu").focus();
    },
    abbrechen: () => {
      keStatusFormZu();
      if (kb.statusFuer) keStatusBereich(kb.statusFuer);
      el("ksENeu").focus();
    },
  });
  ke.statusForm.fokus();
}

function keStatusFormZu() {
  ke.statusForm = null;
  const box = el("ksEForm");
  box.replaceChildren();
  box.hidden = true;
}

/** Ist im Statusdialog gerade ein Eintrag offen? Dann schließt die Statuswahl den Dialog nicht. */
const keStatusFormOffen = () => !!ke.statusForm;

function keStatusVerwerfenErlaubt() {
  if (!ke.statusForm || !ke.statusForm.geaendert()) return true;
  return window.confirm("Der Eintrag wurde noch nicht gespeichert. Verwerfen?");
}

/** Knopf „Eintrag für die Klasse". */
function keKlasseNeu() {
  const b = kb.blatt;
  if (!b || !ke.stunde || !ke.stunde.darfSchreiben) return;
  const gruppen = (b.klassen && b.klassen.length ? b.klassen : b.lerngruppen || []).join(", ");
  keNeuOeffnen({
    titel: "Eintrag für die Klasse",
    wann: `${tagLang(kb.schluessel.datum)} · ${kb.schluessel.stunde}. Stunde`,
    info: `${b.fach}${gruppen ? " · " + gruppen : ""} – betrifft die ganze Lerngruppe. Für einzelne Schüler: „⋯“ beim Namen.`,
    form: {
      art: "Klasse",
      speichern: (w) => senden("POST", "api/eintraege/stunde", {
        ...keStundeSchluessel(), art: "Klasse", schuelerIds: [], kategorie: w.kategorie, text: w.text,
      }),
      fertig: (antwort) => {
        keStundeUebernehmen(antwort);
        toast(antwort.meldung || "Klasseneintrag gespeichert.");
      },
    },
    zurueck: el("keKlasseKnopf"),
  });
}

// ---------------------------------------------------------------------
//  Dialog „Eintrag": anzeigen, ändern, löschen, neu
// ---------------------------------------------------------------------

/**
 * Eintrag anzeigen – Verfasser und Admin können ändern und löschen.
 * opts: nachher() lädt die Quelle neu; links: „Zum Schüler"/„Zur Stunde" anbieten.
 */
function keDetailOeffnen(e, opts = {}) {
  ke.dialog = { eintrag: e, opts, zurueck: document.activeElement };
  ke.dialogForm = null;
  keDetailRendern();
  dialogAuf(el("keDialog"));
  const erster = el("keAktionen").querySelector("button");
  (erster || el("keZu")).focus();
}

function keDetailRendern() {
  const { eintrag: e, opts } = ke.dialog;
  el("keWann").textContent = `${tagLang(e.datum)} · ${e.stunde ? `${e.stunde}. Stunde` : "ohne Stunde"}`;
  el("keTitel").textContent = e.art === "Klasse" ? "Klasseneintrag" : e.schueler || "Schülereintrag";
  const info = [];
  if (e.art === "Klasse") info.push([e.fach, (e.klassen || []).join(", ")].filter(Boolean).join(" · "));
  else info.push([e.schuelerKlasse, e.fach].filter(Boolean).join(" · "));
  info.push(`geschrieben von ${e.verfasstVon}, ${kurzZeit(e.verfasstAm)}`);
  if (e.geaendertAm) info.push(`geändert von ${e.geaendertVon || "?"}, ${kurzZeit(e.geaendertAm)}`);
  el("keInfo").replaceChildren(...info.filter(Boolean).map((t) => h("span", { class: "ke-info-zeile" }, t)));
  el("keInhalt").hidden = false;
  el("keInhalt").replaceChildren(
    keMarke(e.kategorie),
    e.text ? h("p", { class: "ke-volltext" }, e.text) : h("p", { class: "kl-leer" }, "ohne Text"));
  el("keForm").replaceChildren();
  el("keForm").hidden = true;
  el("keHistorie").replaceChildren();
  el("keHistorie").hidden = true;
  fehlerIn("keFehler", "");
  el("keZu").hidden = false;

  const knoepfe = [];
  if (e.darfAendern) {
    knoepfe.push(h("button", { class: "hauptknopf klein", type: "button", onclick: keAendernAuf }, "Ändern"));
    knoepfe.push(h("button", {
      class: "textknopf ke-loeschen", type: "button",
      onclick: (ev) => bestaetigen(ev.currentTarget, "Wirklich löschen?", keLoeschen),
    }, "Löschen"));
  }
  if (e.geaendertAm)
    knoepfe.push(h("button", { class: "textknopf", type: "button", "aria-expanded": "false", onclick: keHistorieUmschalten }, "Änderungen"));
  if (opts.links && fehlzeitenOffen()) {
    if (e.schuelerId && !(fzOben() && fzOben().art === "schueler" && fzOben().id === e.schuelerId))
      knoepfe.push(h("button", { class: "textknopf", type: "button", onclick: () => keZumSchueler(e) }, "Zum Schüler"));
    const plan = keStundePlan(e);
    if (plan && kbDarfOeffnen(plan))
      knoepfe.push(h("button", { class: "textknopf", type: "button", onclick: () => keZurStunde(e) }, "Zur Stunde"));
  }
  el("keAktionen").replaceChildren(...knoepfe);
  el("keAktionen").hidden = knoepfe.length === 0;
}

/** Stunde eines Eintrags wie im Plan (für das Stundenblatt). */
function keStundePlan(e) {
  if (!e.stunde || !e.grundLehrer) return null;
  return {
    stunde: e.stunde, grundUNr: e.grundUNr, grundLehrer: e.grundLehrer,
    lehrer: e.lehrer || e.grundLehrer,
    originalLehrer: e.lehrer && !gleich(e.lehrer, e.grundLehrer) ? e.grundLehrer : null,
    klassen: e.klassen || [], klassenbuch: true,
  };
}

function keZumSchueler(e) {
  dialogZu(el("keDialog"));
  fz.stapel.push({ art: "schueler", id: e.schuelerId });
  fzZeigen();
}

function keZurStunde(e) {
  const plan = keStundePlan(e);
  dialogZu(el("keDialog"));
  if (plan) stundenblattOeffnen(plan, e.datum, "fehlzeiten");
}

function keAendernAuf() {
  const { eintrag: e, opts } = ke.dialog;
  el("keInhalt").hidden = true;
  el("keAktionen").hidden = true;
  el("keHistorie").hidden = true;
  el("keZu").hidden = true;
  fehlerIn("keFehler", "");
  ke.dialogForm = keFormBauen(el("keForm"), {
    art: e.art,
    eintrag: e,
    speichern: (w) => senden("PUT", `api/eintraege/${e.id}`, { kategorie: w.kategorie, text: w.text, version: w.version }),
    fertig: (antwort) => {
      ke.dialogForm = null;
      if (antwort && antwort.eintrag) ke.dialog.eintrag = antwort.eintrag;
      keDetailRendern();
      toast((antwort && antwort.meldung) || "Eintrag geändert.");
      const erster = el("keAktionen").querySelector("button");
      (erster || el("keZu")).focus();
      if (opts.nachher) opts.nachher();
    },
    abbrechen: () => {
      ke.dialogForm = null;
      keDetailRendern();
      const erster = el("keAktionen").querySelector("button");
      (erster || el("keZu")).focus();
    },
  });
  ke.dialogForm.fokus();
}

async function keLoeschen() {
  const { eintrag: e, opts } = ke.dialog;
  fehlerIn("keFehler", "");
  try {
    const antwort = await senden("DELETE", `api/eintraege/${e.id}`);
    dialogZu(el("keDialog"));
    toast((antwort && antwort.meldung) || "Eintrag gelöscht.");
    if (opts.nachher) opts.nachher();
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("keFehler", t);
    keDetailRendern();
  }
}

async function keHistorieUmschalten(ev) {
  const knopf = ev.currentTarget;
  const liste = el("keHistorie");
  if (!liste.hidden) {
    liste.hidden = true;
    knopf.setAttribute("aria-expanded", "false");
    return;
  }
  const e = ke.dialog.eintrag;
  knopf.setAttribute("aria-expanded", "true");
  liste.hidden = false;
  liste.replaceChildren(h("li", { class: "p-eintrag" }, "Wird geladen …"));
  try {
    const zeilen = await senden("GET", `api/eintraege/${e.id}/historie`);
    if (!ke.dialog || ke.dialog.eintrag !== e) return;
    liste.replaceChildren(...zeilen.map((p) => h("li", { class: "p-eintrag" },
      h("span", { class: "p-zeit" }, `${datumZeit(p.zeit)} · ${p.akteur}`),
      h("span", { class: "p-text kh-inhalt" }, p.alt ? korrektur(p.alt, p.neu) : p.neu))));
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) liste.replaceChildren(h("li", { class: "p-eintrag" }, t));
  }
}

/** Neuer Eintrag im Dialog (Klasseneintrag aus dem Stundenblatt, Eintrag ohne Stunde). */
function keNeuOeffnen({ titel, wann, info, form, zurueck }) {
  ke.dialog = { neu: true, zurueck: zurueck || document.activeElement };
  el("keWann").textContent = wann || "";
  el("keTitel").textContent = titel;
  el("keInfo").replaceChildren(...(info ? [h("span", { class: "ke-info-zeile" }, info)] : []));
  el("keInhalt").replaceChildren();
  el("keInhalt").hidden = true;
  el("keAktionen").replaceChildren();
  el("keAktionen").hidden = true;
  el("keHistorie").replaceChildren();
  el("keHistorie").hidden = true;
  el("keZu").hidden = true;
  fehlerIn("keFehler", "");
  dialogAuf(el("keDialog"));
  ke.dialogForm = keFormBauen(el("keForm"), {
    ...form,
    fertig: (antwort) => {
      ke.dialogForm = null;
      dialogZu(el("keDialog"));
      form.fertig(antwort);
    },
    abbrechen: () => { ke.dialogForm = null; dialogZu(el("keDialog")); },
  });
  ke.dialogForm.fokus();
}

/** Schließen über ×, Esc oder Hintergrund: bei ungespeichertem Formular nachfragen. */
function keDialogVerwerfenErlaubt() {
  if (!ke.dialogForm || !ke.dialogForm.geaendert()) return true;
  return window.confirm("Der Eintrag wurde noch nicht gespeichert. Verwerfen?");
}

function keDialogSchliessen() {
  if (!keDialogVerwerfenErlaubt()) return;
  dialogZu(el("keDialog"));
}

// ---------------------------------------------------------------------
//  Schülerdetail (fehlzeiten.js): Abschnitt „Einträge"
// ---------------------------------------------------------------------

async function keSchuelerLaden(o) {
  const box = el("fzKe");
  const nr = ++ke.schuelerLadeNr;
  box.hidden = false;
  box.replaceChildren(h("h3", { class: "fz-titel" }, "Einträge"), h("p", { class: "such-info" }, "Wird geladen …"));
  let url = `api/eintraege/schueler/${o.id}`;
  if (o.unr) url += `?unr=${o.unr}&lehrer=${encodeURIComponent(o.lehrer)}`;
  url = fzMitZr(url);   // Phase 4.1: gewählter Zeitraum
  try {
    const d = await senden("GET", url);
    if (nr !== ke.schuelerLadeNr || fzOben() !== o) return;
    keSchuelerRendern(d, o);
  } catch (f) {
    if (nr !== ke.schuelerLadeNr || fzOben() !== o) return;
    const t = aktionsFehler(f);
    if (t === null) return;
    box.replaceChildren(h("h3", { class: "fz-titel" }, "Einträge"), h("p", { class: "fehler klein" }, t));
  }
}

function keSchuelerRendern(d, o) {
  const leer = d.eintraege.length === 0;
  el("fzKe").replaceChildren(
    h("div", { class: "fz-kopfzeile" },
      h("h3", { class: "fz-titel" }, "Einträge"),
      d.darfOhneStunde
        ? h("button", {
            class: "hauptknopf klein", type: "button", "aria-haspopup": "dialog",
            onclick: (ev) => keOhneStundeOeffnen(o, d, ev.currentTarget),
          }, "Eintrag ohne Stunde")
        : null),
    d.zaehler.length
      ? h("p", { class: "ke-zaehlzeile" }, d.zaehler.map((z) => `${z.anzahl}× ${keKat(z.kategorie).text}`).join(" · "))
      : null,
    d.nurEigene ? h("p", { class: "such-info" }, "Hier stehen nur deine eigenen Einträge.") : null,
    leer
      ? h("p", { class: "such-info" }, "Keine Einträge in diesem Zeitraum.")
      : h("ul", { class: "k-liste ke-liste", "aria-label": "Einträge" },
          d.eintraege.map((e) => keZeile(e, { ohneName: true, nachher: () => keSchuelerLaden(o), links: true }))),
    d.gekuerzt ? h("p", { class: "such-info" }, `Es stehen nur die neuesten ${d.eintraege.length} Einträge hier.`) : null);
}

function keOhneStundeOeffnen(o, d, knopf) {
  keNeuOeffnen({
    titel: `Eintrag für ${d.titel}`,
    wann: "Ohne Stunde – z. B. Pause oder Ausflug",
    info: "",
    form: {
      art: "Schueler",
      datum: true,
      speichern: (w) => senden("POST", `api/eintraege/schueler/${o.id}`, { datum: w.datum, kategorie: w.kategorie, text: w.text }),
      fertig: (antwort) => {
        toast((antwort && antwort.meldung) || "Eintrag gespeichert.");
        keSchuelerLaden(o);
      },
    },
    zurueck: knopf,
  });
}

/** Eine Zeile in den Listen (Schülerdetail, Klasse/Stufe, Unterricht). */
function keZeile(e, opts = {}) {
  const c = keKat(e.kategorie);
  const wer = e.art === "Klasse"
    ? `Klasseneintrag · ${(e.klassen || []).join(", ")}`
    : `${e.schueler || "Schüler"}${e.schuelerKlasse ? ` (${e.schuelerKlasse})` : ""}`;
  const was = [`${kbDatumKurz(e.datum)}`, e.stunde ? `${e.stunde}. Std.` : "ohne Stunde", e.fach || null]
    .filter(Boolean).join(" · ");
  return h("li", null, h("button", {
    class: `k-zeile ke-zeile k-${c.art}`, type: "button", "aria-haspopup": "dialog",
    onclick: () => keDetailOeffnen(e, { nachher: opts.nachher, links: opts.links }),
  },
  h("span", { class: "k-oben" }, h("span", { class: "k-name" }, was), keMarke(e.kategorie)),
  opts.ohneName ? null : h("span", { class: "ke-z-wer" }, wer),
  e.text ? keText(e.text, "ke-z-text") : null,
  h("span", { class: "k-unten" }, `von ${keVon(e)}`)));
}

// ---------------------------------------------------------------------
//  Listen: Reiter „Einträge" (Klasse/Stufe) und Unterricht (fehlzeiten.js)
// ---------------------------------------------------------------------

function keListeLeeren(text) {
  ke.liste = null;
  for (const id of ["fzKeWann", "fzKeTitel", "fzKeInfo"]) el(id).textContent = "";
  el("fzKeFilter").replaceChildren();
  el("fzKeFilter").hidden = true;
  el("fzKeListe").replaceChildren();
  el("fzKeWeitere").replaceChildren();
  el("fzKeWeitereTitel").hidden = true;
  el("fzKeWeitereInfo").hidden = true;
  el("fzKeLeer").textContent = text || "";
}

function keRenderListe(d, o) {
  ke.liste = d;
  if (ke.filter && !d.zaehler.some((z) => z.kategorie === ke.filter)) ke.filter = "";
  el("fzKeWann").textContent = fzZeitraumText(d.von, d.bis);
  el("fzKeTitel").textContent = d.titel;
  const n = d.eintraege.length;
  const info = [`${n} ${n === 1 ? "Eintrag" : "Einträge"}`];
  if (d.zaehler.length) info.push(d.zaehler.map((z) => `${z.anzahl}× ${keKat(z.kategorie).text}`).join(" · "));
  el("fzKeInfo").textContent = info.join(" · ");
  keFilterRendern();
  keListeRendern(o);

  const weitere = d.weitere || [];
  const klassen = (d.weitereKlassen || []).join(", ");
  el("fzKeWeitereTitel").hidden = !klassen;
  el("fzKeWeitereTitel").textContent = klassen ? `Klasseneinträge ${klassen} aus anderen Stunden` : "";
  el("fzKeWeitereInfo").hidden = !klassen || weitere.length > 0;
  el("fzKeWeitereInfo").textContent = klassen && !weitere.length ? "Keine." : "";
  el("fzKeWeitere").replaceChildren(...weitere.map((e) => keZeile(e, { nachher: fzLaden, links: true })));
}

function keFilterRendern() {
  const d = ke.liste;
  const box = el("fzKeFilter");
  box.hidden = !d || d.zaehler.length < 2;
  if (box.hidden) { box.replaceChildren(); return; }
  const knopf = (k, text) => h("button", {
    class: "kb-chip ke-filterchip" + (k ? ` k-${keKat(k).art}` : ""), type: "button", "aria-pressed": String(ke.filter === k),
    onclick: () => { ke.filter = k; keFilterRendern(); keListeRendern(fzOben()); },
  }, text);
  box.replaceChildren(
    knopf("", `Alle ${d.eintraege.length}`),
    ...d.zaehler.map((z) => knopf(z.kategorie, `${keKat(z.kategorie).text} ${z.anzahl}`)));
}

function keListeRendern(o) {
  const d = ke.liste;
  if (!d) return;
  const liste = ke.filter ? d.eintraege.filter((e) => e.kategorie === ke.filter) : d.eintraege;
  el("fzKeListe").replaceChildren(...liste.map((e) => keZeile(e, { nachher: fzLaden, links: true })));
  let leer = "";
  if (!d.eintraege.length) {
    leer = o && o.art === "keUnterricht"
      ? "In diesem Unterricht gibt es noch keine Einträge."
      : "In diesem Zeitraum gibt es noch keine Einträge.";
  }
  const hinweis = o && o.art === "keUnterricht" && d.nurEigene
    ? "Schülereinträge siehst du, wenn du sie geschrieben hast; alle sieht die Klassenleitung bzw. Stufenkoordination." : "";
  el("fzKeLeer").textContent = [leer, hinweis].filter(Boolean).join(" ");
  if (d.gekuerzt) el("fzKeLeer").textContent += ` Es stehen nur die neuesten ${d.eintraege.length} Einträge hier.`;
}

/** Fehlzeiten-Ansicht verlassen bzw. neu geöffnet. */
function keFzLeeren() {
  ke.schuelerLadeNr++;
  ke.filter = "";
  el("fzKe").replaceChildren();
  el("fzKe").hidden = true;
  keListeLeeren("");
  if (ke.dialog) dialogZu(el("keDialog"));
}

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function eintraegeInit() {
  el("keKlasseKnopf").addEventListener("click", keKlasseNeu);
  el("ksENeu").addEventListener("click", keStatusFormAuf);

  el("keZu").addEventListener("click", keDialogSchliessen);
  el("keDialog").addEventListener("cancel", (e) => { if (!keDialogVerwerfenErlaubt()) e.preventDefault(); });
  el("keDialog").addEventListener("close", () => {
    const zurueck = ke.dialog && ke.dialog.zurueck;
    ke.dialog = null;
    ke.dialogForm = null;
    for (const id of ["keInhalt", "keForm", "keAktionen", "keHistorie", "keInfo"]) el(id).replaceChildren();
    if (zurueck && zurueck.isConnected && typeof zurueck.focus === "function") zurueck.focus();
  });
  hintergrundSchliesst(el("keDialog"), keDialogSchliessen);

  // Statusdialog: Esc mit ungespeichertem Eintrag fragt nach; beim Schließen das Formular verwerfen.
  el("kbStatusDialog").addEventListener("cancel", (e) => { if (!keStatusVerwerfenErlaubt()) e.preventDefault(); });
  el("kbStatusDialog").addEventListener("close", keStatusFormZu);
}
