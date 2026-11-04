"use strict";
// =====================================================================
//  Stundenplan-PWA – Fehlzeiten (Phase 3, Teilschritt 3.2)
//
//  Kontomenü „Fehlzeiten" (Personal) bzw. „Meine Fehlzeiten" (Schüler):
//  - Nicht erfasst: Stunden ohne Anwesenheit (ab dem Folgetag); Lehrkraft:
//    eigene Stunden, Klassenleitung/Stufenkoordination: Klasse/Stufe,
//    Admin: alle mit Filter. Antippen öffnet das Stundenblatt.
//  - Meine Kurse: je Unterricht die Schüler mit ihren Fehlstunden.
//  - Klasse/Stufe (KL/SK, Admin): alle Schüler mit Summen.
//  - Schüler: Summen, Entschuldigungen (Zeitraum eintragen/löschen),
//    Einträge – bewerten (entschuldigt/offen/unentschuldigt) dürfen KL/SK/
//    Admin sowie in der Oberstufe die Kurslehrkraft in eigenen Kursstunden
//    (solange die Stufenkoordination nicht festgelegt hat).
//
//  Phase 3.3: „Nicht erfasst" zeigt auch Stunden ohne Thema (Marke „Thema
//  fehlt", Filter Alles/Anwesenheit/Thema). Unter „Meine Kurse" je Unterricht
//  umschaltbar: Fehlzeiten | Themen (Themenliste mit Hausaufgaben und dem
//  Schalter „Hausaufgaben für Schüler sichtbar").
//
//  Phase 3.4: Kontomenü heißt jetzt „Klassenbuch" (Schüler weiterhin „Meine
//  Fehlzeiten"). Neuer Reiter „Einträge" (KL/SK/Admin), Unterricht umschaltbar
//  Fehlzeiten | Themen | Einträge, Schülerdetail mit Abschnitt „Einträge" –
//  Code dafür in eintraege.js (ke…).
//
//  Datenschutz: nichts davon im Browser-Speicher; beim Verlassen geleert.
//  Wird nach klassenbuch.js und VOR app.js geladen; Einstieg fehlzeitenInit().
// =====================================================================

const FZ_GRUND = { Krankheit: "Krankheit", Arzttermin: "Arzttermin", Familiaer: "familiär", Sonstiges: "sonstiges" };
const FZ_BEWERTUNG = { Offen: "offen", Entschuldigt: "entschuldigt", Unentschuldigt: "unentschuldigt" };
const FZ_STATUS = { Fehlt: "fehlt", Verspaetet: "verspätet", Beurlaubt: "beurlaubt", Schulveranstaltung: "Schulveranst." };
const FZ_WT = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];

const fz = {
  rechte: null,      // Antwort api/fehlzeiten/rechte
  stapel: [],        // Ansichten, oben = sichtbar: { art: "reiter", reiter } | { art: "unterricht", unr, lehrer }
                     //   | { art: "themen", unr, lehrer } | { art: "keUnterricht", unr, lehrer } (Phase 3.4)
                     //   | { art: "schueler", id, unr?, lehrer? } | { art: "ich" }
  ladeNr: 0,
  daten: null,       // Antwort der sichtbaren Ansicht
  neAnsicht: "ich",  // nicht erfasst: ich | zustaendig | alle
  neArt: "alle",     // Phase 3.3: was fehlt? alle | anwesenheit | thema
  neLehrer: "",
  neKlasse: "",
  neLehrerListe: [], // Admin: Lehrkräfte mit offenen Stunden (für den Filter)
  klasse: "",        // Reiter Klasse/Stufe
  eintrag: null,     // im Bewertungsdialog
};

const fehlzeitenOffen = () => !el("fehlzeiten").hidden;
const fzOben = () => fz.stapel[fz.stapel.length - 1] || null;
const fzDatum = (iso) => { const d = ausIso(iso); return `${FZ_WT[d.getDay()]} ${pad(d.getDate())}.${pad(d.getMonth() + 1)}.`; };
const fzDatumLang = (iso) => { const d = ausIso(iso); return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()}`; };
const fzBis = (iso) => (iso > heuteIso() ? heuteIso() : iso);

function fzTitel(text) {
  el("titel").textContent = text;
  document.title = `${text} – Stundenplan`;
}

// ---------------------------------------------------------------------
//  Öffnen / verlassen
// ---------------------------------------------------------------------

function fehlzeitenMenueAnpassen() {
  const b = el("fzKnopf");
  const a = state.anmeldung;
  if (!a) { b.hidden = true; return; }
  if (istSchueler()) { b.hidden = false; b.textContent = "Meine Fehlzeiten"; return; }
  const kl = ["Klassenlehrer", "Stufenkoordination"].includes(a.rolle) && !!a.zustaendig;
  b.hidden = !istPersonal() || !(a.rolle === "Admin" || eigenesKuerzel() || kl);
  b.textContent = "Klassenbuch";   // Phase 3.4 (Entscheidung 37)
}

async function fehlzeitenOeffnen() {
  kontoSchliessen();
  if (verwaltungOffen()) {
    if (!startpasswoerterVerwerfen()) return;
    verwaltungLeeren();
    el("verwaltung").hidden = true;
  }
  if (!klassenbuchSchliessen()) return;
  hausaufgabenSchliessen();
  ladenAbbrechen();
  fehlzeitenLeeren();
  el("inhalt").hidden = true;
  el("fehlzeiten").hidden = false;
  el("aktualisieren").hidden = false;
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  window.scrollTo(0, 0);

  if (istSchueler()) {
    fz.stapel = [{ art: "ich" }];
    fzZeigen();
    return;
  }

  fzTitel("Klassenbuch");
  const nr = ++fz.ladeNr;
  document.body.classList.add("laedt");
  try {
    const r = await senden("GET", "api/fehlzeiten/rechte");
    if (nr !== fz.ladeNr) return;
    fz.rechte = r;
    const reiter = fzReiterListe();
    if (!reiter.length) { fehlerIn("fzFehler", "Hier gibt es für dich nichts zu sehen."); return; }
    fz.neAnsicht = fzNeAnsichten()[0].id;
    fz.klasse = r.klassen[0] || "";
    fz.stapel = [{ art: "reiter", reiter: reiter[0].id }];
    fzZeigen();
  } catch (f) {
    if (nr !== fz.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("fzFehler", t);
  } finally {
    if (nr === fz.ladeNr) document.body.classList.remove("laedt");
  }
}

/** Alles Personenbezogene der Fehlzeiten aus der Seite entfernen. */
function fehlzeitenLeeren() {
  fz.ladeNr++;
  fz.rechte = null;
  fz.stapel = [];
  fz.daten = null;
  fz.neLehrer = "";
  fz.neKlasse = "";
  fz.neLehrerListe = [];
  fz.klasse = "";
  fz.eintrag = null;
  fz.neArt = "alle";
  for (const id of ["fzReiter", "fzNeWahl", "fzNeArt", "fzNeLehrer", "fzNeKlasse", "fzNeListe", "fzKurseListe", "fzKlasseWahl",
    "fzListeZeilen", "fzSumme", "fzEntsch", "fzFaecher", "fzEintraege", "fzSInfo", "fzTListe"])
    el(id).replaceChildren();
  for (const id of ["fzNeInfo", "fzKurseInfo", "fzListeWann", "fzListeTitel", "fzListeInfo", "fzSWann", "fzSName",
    "fzEintraegeInfo", "fbWas", "feFuer", "fzTWann", "fzTTitel", "fzTInfo", "fzTLeer", "fzTSichtbarInfo"])
    el(id).textContent = "";
  el("fzUWahl").hidden = true;
  keFzLeeren();   // Phase 3.4
  fehlerIn("fzFehler", "");
  dialogZu(el("fzBewertDialog"));
  dialogZu(el("fzEntschDialog"));
}

/** Fehlzeiten schließen, ohne etwas anderes zu zeigen. */
function fehlzeitenSchliessen() {
  if (!fehlzeitenOffen()) return true;
  fehlzeitenLeeren();
  el("fehlzeiten").hidden = true;
  return true;
}

function fehlzeitenVerlassen() {
  fehlzeitenSchliessen();
  el("inhalt").hidden = false;
  render();
  if (state.plan) ladeWoche({ still: true });
  else ladeWoche();
}

/** Zurück aus dem Stundenblatt (geöffnet aus „nicht erfasst"): Liste neu laden. */
function fehlzeitenWiederZeigen() {
  el("inhalt").hidden = true;
  el("fehlzeiten").hidden = false;
  el("aktualisieren").hidden = false;
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  fzZeigen();
}

function fzZurueck() {
  if (fz.stapel.length > 1) {
    fz.stapel.pop();
    fzZeigen();
    return;
  }
  fehlzeitenVerlassen();
}

const fzAktualisieren = () => fzLaden();

// ---------------------------------------------------------------------
//  Reiter und Ansichten
// ---------------------------------------------------------------------

function fzReiterListe() {
  const r = fz.rechte;
  if (!r) return [];
  const l = [];
  if (r.lehrkraft || r.admin || r.klassen.length) l.push({ id: "ne", text: "Nicht erfasst" });
  if (r.lehrkraft) l.push({ id: "kurse", text: "Meine Kurse" });
  if (r.klassen.length) {
    const k = r.klassen;
    l.push({ id: "klasse", text: r.admin ? "Klassen" : k.length === 1 ? fzGruppenName(k[0]) : "Meine Klassen" });
    l.push({ id: "eintraege", text: "Einträge" });   // Phase 3.4
  }
  return l;
}

const fzGruppenName = (k) => (/^(EF|Q1|Q2)$/i.test(k) ? `Stufe ${k.toUpperCase()}` : /^\d\d$/.test(k) ? `Jahrgang ${k}` : `Klasse ${k}`);

function fzNeAnsichten() {
  const r = fz.rechte;
  const l = [];
  if (!r) return l;
  if (r.lehrkraft) l.push({ id: "ich", text: "Meine Stunden" });
  if (!r.admin && r.klassen.length)
    l.push({ id: "zustaendig", text: r.klassen.length === 1 ? fzGruppenName(r.klassen[0]) : "Meine Klassen" });
  if (r.admin) l.push({ id: "alle", text: "Alle" });
  return l;
}

function fzRenderReiter() {
  const liste = fzReiterListe();
  const o = fzOben();
  el("fzReiter").replaceChildren(...liste.map((r) => h("button", {
    type: "button", role: "tab", "aria-selected": String(o && o.reiter === r.id),
    onclick: () => { if (fzOben().reiter !== r.id) { fz.stapel = [{ art: "reiter", reiter: r.id }]; fzZeigen(); } },
  }, r.text)));
}

/** Sichtbare Ansicht aufbauen und ihre Daten laden. */
function fzZeigen() {
  const o = fzOben();
  fehlerIn("fzFehler", "");
  fz.daten = null;
  for (const id of ["fzNe", "fzKurse", "fzKlasseBox", "fzListe", "fzSchueler", "fzThemen", "fzKeAnsicht"]) el(id).hidden = true;
  el("fzZurueckText").textContent = fz.stapel.length > 1 ? "Zurück" : "Zum Plan";
  // Phase 3.3: Unterricht – Fehlzeiten | Themen (Phase 3.4: | Einträge)
  const uSicht = !!o && (o.art === "unterricht" || o.art === "themen" || o.art === "keUnterricht");
  el("fzUWahl").hidden = !uSicht;
  if (uSicht)
    for (const b of el("fzUWahl").querySelectorAll("button")) b.setAttribute("aria-pressed", String(b.dataset.u === o.art));
  const reiterSicht = !!o && o.art === "reiter";
  el("fzReiter").hidden = !reiterSicht || fzReiterListe().length < 2;
  if (reiterSicht) fzRenderReiter();
  if (!o) return;

  fzTitel(o.art === "ich" ? "Meine Fehlzeiten" : o.art === "themen" ? "Themen"
    : o.art === "keUnterricht" || (o.art === "reiter" && o.reiter === "eintraege") ? "Einträge" : "Klassenbuch");
  if (o.art === "reiter" && o.reiter === "ne") {
    el("fzNe").hidden = false;
    fzRenderNeWahl();
    el("fzNeListe").replaceChildren();
    el("fzNeInfo").textContent = "Wird geladen …";
  } else if (o.art === "reiter" && o.reiter === "kurse") {
    el("fzKurse").hidden = false;
    el("fzKurseListe").replaceChildren();
    el("fzKurseInfo").textContent = "Wird geladen …";
  } else if (o.art === "themen") {
    el("fzThemen").hidden = false;
    el("fzTListe").replaceChildren();
    for (const id of ["fzTWann", "fzTTitel", "fzTInfo"]) el(id).textContent = "";
    el("fzTSichtbarSchalter").hidden = true;
    el("fzTSichtbarInfo").hidden = true;
    el("fzTLeer").textContent = "Wird geladen …";
  } else if ((o.art === "reiter" && o.reiter === "eintraege") || o.art === "keUnterricht") {
    // Phase 3.4: Einträge einer Klasse/Stufe bzw. eines Unterrichts
    if (o.art === "reiter") {
      el("fzKlasseBox").hidden = fz.rechte.klassen.length < 2;
      el("fzKlasseWahl").replaceChildren(...fz.rechte.klassen.map((k) =>
        h("option", { value: k, selected: k === fz.klasse }, fzGruppenName(k))));
    }
    el("fzKeAnsicht").hidden = false;
    keListeLeeren("Wird geladen …");
  } else if (o.art === "reiter" || o.art === "unterricht") {
    if (o.art === "reiter") {
      el("fzKlasseBox").hidden = fz.rechte.klassen.length < 2;
      el("fzKlasseWahl").replaceChildren(...fz.rechte.klassen.map((k) =>
        h("option", { value: k, selected: k === fz.klasse }, fzGruppenName(k))));
    }
    el("fzListe").hidden = false;
    el("fzListeZeilen").replaceChildren();
    el("fzListeTitel").textContent = "";
    el("fzListeWann").textContent = "";
    el("fzListeInfo").textContent = "Wird geladen …";
  } else {
    el("fzSchueler").hidden = false;
    for (const id of ["fzSumme", "fzEntsch", "fzFaecher", "fzEintraege", "fzSInfo", "fzKe"]) el(id).replaceChildren();
    el("fzKe").hidden = true;
    el("fzSName").textContent = "";
    el("fzSWann").textContent = "";
    el("fzEintraegeInfo").textContent = "Wird geladen …";
  }
  window.scrollTo(0, 0);
  fzLaden();
}

function fzUrl(o) {
  const enc = encodeURIComponent;
  if (o.art === "reiter" && o.reiter === "ne") {
    let url = `api/fehlzeiten/nicht-erfasst?ansicht=${fz.neAnsicht}&art=${fz.neArt}`;
    if (fz.neAnsicht === "alle" && fz.neLehrer) url += `&lehrer=${enc(fz.neLehrer)}`;
    if (fz.neAnsicht === "alle" && fz.neKlasse) url += `&klasse=${enc(fz.neKlasse)}`;
    return url;
  }
  if (o.art === "reiter" && o.reiter === "kurse") return "api/fehlzeiten/unterrichte";
  if (o.art === "reiter" && o.reiter === "eintraege") return fz.klasse ? `api/eintraege/klasse/${enc(fz.klasse)}` : null;
  if (o.art === "keUnterricht") return `api/eintraege/unterricht?unr=${o.unr}&lehrer=${enc(o.lehrer)}`;
  if (o.art === "reiter") return fz.klasse ? `api/fehlzeiten/klasse/${enc(fz.klasse)}` : null;
  if (o.art === "unterricht") return `api/fehlzeiten/unterricht?unr=${o.unr}&lehrer=${enc(o.lehrer)}`;
  if (o.art === "themen") return `api/klassenbuch/themen?unr=${o.unr}&lehrer=${enc(o.lehrer)}`;
  if (o.art === "schueler")
    return `api/fehlzeiten/schueler/${o.id}` + (o.unr ? `?unr=${o.unr}&lehrer=${enc(o.lehrer)}` : "");
  return "api/fehlzeiten/ich";
}

async function fzLaden() {
  const o = fzOben();
  if (!o) return;
  const url = fzUrl(o);
  if (!url) { el("fzListeInfo").textContent = "Keine Klasse gewählt."; el("fzKeLeer").textContent = "Keine Klasse gewählt."; return; }
  const nr = ++fz.ladeNr;
  document.body.classList.add("laedt");
  try {
    const d = await senden("GET", url);
    if (nr !== fz.ladeNr) return;
    fz.daten = d;
    if (o.art === "reiter" && o.reiter === "ne") fzRenderNe(d);
    else if (o.art === "reiter" && o.reiter === "kurse") fzRenderKurse(d);
    else if (o.art === "themen") fzRenderThemen(d);
    else if ((o.art === "reiter" && o.reiter === "eintraege") || o.art === "keUnterricht") keRenderListe(d, o);
    else if (o.art === "reiter" || o.art === "unterricht") fzRenderListe(d);
    else fzRenderSchueler(d);
  } catch (f) {
    if (nr !== fz.ladeNr) return;
    const t = aktionsFehler(f);
    if (t === null) return;
    for (const id of ["fzNeInfo", "fzKurseInfo", "fzListeInfo", "fzEintraegeInfo", "fzTLeer", "fzKeLeer"]) el(id).textContent = "";
    fehlerIn("fzFehler", t);
  } finally {
    if (nr === fz.ladeNr) document.body.classList.remove("laedt");
  }
}

// ---------------------------------------------------------------------
//  Nicht erfasste Stunden
// ---------------------------------------------------------------------

function fzRenderNeWahl() {
  const ansichten = fzNeAnsichten();
  el("fzNeWahl").hidden = ansichten.length < 2;
  el("fzNeWahl").replaceChildren(...ansichten.map((a) => h("button", {
    type: "button", "aria-pressed": String(a.id === fz.neAnsicht),
    onclick: () => { if (fz.neAnsicht !== a.id) { fz.neAnsicht = a.id; fzZeigen(); } },
  }, a.text)));

  // Phase 3.3 (Entscheidung 20): Was fehlt?
  const arten = [{ id: "alle", text: "Alles" }, { id: "anwesenheit", text: "Anwesenheit" }, { id: "thema", text: "Thema" }];
  el("fzNeArt").replaceChildren(...arten.map((a) => h("button", {
    type: "button", "aria-pressed": String(a.id === fz.neArt),
    onclick: () => { if (fz.neArt !== a.id) { fz.neArt = a.id; fzZeigen(); } },
  }, a.text)));

  const admin = fz.neAnsicht === "alle";
  el("fzNeFilter").hidden = !admin;
  if (admin) {
    const lehrer = [...new Set([...fz.neLehrerListe, fz.neLehrer].filter(Boolean))].sort(sortierer.compare);
    el("fzNeLehrer").replaceChildren(h("option", { value: "" }, "Alle Lehrkräfte"),
      ...lehrer.map((l) => h("option", { value: l, selected: l === fz.neLehrer }, l)));
    el("fzNeKlasse").replaceChildren(h("option", { value: "" }, "Alle Klassen"),
      ...fz.rechte.klassen.map((k) => h("option", { value: k, selected: k === fz.neKlasse }, k)));
  }
}

function fzRenderNe(d) {
  if (fz.neAnsicht === "alle" && !fz.neLehrer) {
    fz.neLehrerListe = d.proLehrer.map((x) => x.lehrer);
    fzRenderNeWahl();
  }
  const seit = fzDatumLang(d.von);
  const st1 = (n) => `${n} ${n === 1 ? "Stunde" : "Stunden"}`;
  let info;
  if (d.bis < d.von) info = `Offene Stunden erscheinen hier ab dem ${seit} – jeweils ab dem Folgetag.`;
  else if (d.anzahl === 0) {
    info = fz.neArt === "thema" ? `Alle Stunden seit dem ${seit} haben ein Thema.`
      : fz.neArt === "anwesenheit" ? `Alle Stunden seit dem ${seit} sind erfasst.`
      : `Seit dem ${seit} ist alles eingetragen.`;
  } else {
    info = fz.neArt === "thema" ? `${st1(d.anzahl)} ohne Thema seit dem ${seit}`
      : fz.neArt === "anwesenheit" ? `${st1(d.anzahl)} ohne Anwesenheit seit dem ${seit}`
      : `${st1(d.anzahl)} offen seit dem ${seit}: ${d.ohneAnwesenheit} ohne Anwesenheit, ${d.ohneThema} ohne Thema`;
    info += d.gekuerzt ? ` – die neuesten ${d.stunden.length} stehen hier.` : ".";
  }
  if (fz.neAnsicht !== "ich" && d.proLehrer.length > 1)
    info += " " + d.proLehrer.slice(0, 8).map((x) => `${x.lehrer}: ${x.anzahl}`).join(" · ");
  el("fzNeInfo").textContent = info;

  const mitLehrer = fz.neAnsicht !== "ich";
  el("fzNeListe").replaceChildren(...d.stunden.map((s) => {
    const st = s.status !== "Normal" ? STATUS[s.status] : null;
    const plan = { stunde: s.stunde, grundUNr: s.grundUNr, grundLehrer: s.grundLehrer, lehrer: s.lehrer,
      originalLehrer: s.originalLehrer, klassen: s.klassen, klassenbuch: true };
    return h("li", null, h("button", {
      class: "k-zeile", type: "button", disabled: !kbDarfOeffnen(plan),
      onclick: () => stundenblattOeffnen(plan, s.datum, "fehlzeiten"),
    },
    h("span", { class: "k-oben" },
      h("span", { class: "k-name" }, `${fzDatum(s.datum)} · ${s.stunde}. Stunde`),
      mitLehrer ? h("span", { class: "k-rechts" }, s.originalLehrer ? korrektur(s.originalLehrer, s.lehrer) : s.lehrer) : null),
    h("span", { class: "k-unten" }, `${s.fach} · ${s.klassen.join(", ")}`),
    h("span", { class: "k-marken" },
      s.anwesenheitFehlt ? h("span", { class: "marke start" }, "Anwesenheit fehlt") : null,
      s.themaFehlt ? h("span", { class: "marke thema" }, "Thema fehlt") : null,
      st ? h("span", { class: "marke" }, st.text) : null,
      s.gruppeFehlt ? h("span", { class: "marke start" }, "Gruppe fehlt") : null)));
  }));
}

// ---------------------------------------------------------------------
//  Meine Kurse
// ---------------------------------------------------------------------

function fzRenderKurse(liste) {
  el("fzKurseInfo").textContent = liste.length
    ? "Tippe auf einen Kurs bzw. eine Klasse: Fehlzeiten deiner Schülerinnen und Schüler, Themen und Hausaufgaben sowie Einträge."
    : "Im aktuellen Stundenplan hast du keinen Unterricht mit Klassenbuch.";
  el("fzKurseListe").replaceChildren(...liste.map((u) => h("li", null, h("button", {
    class: "k-zeile", type: "button",
    onclick: () => { fz.stapel.push({ art: "unterricht", unr: u.uNr, lehrer: u.lehrer }); fzZeigen(); },
  },
  h("span", { class: "k-oben" },
    h("span", { class: "k-name" }, u.fach),
    h("span", { class: "k-rechts" }, u.klassen.join(", "))),
  u.oberstufe ? h("span", { class: "k-unten" }, "Oberstufe – du kannst hier Fehlstunden entschuldigen") : null))));
}

// ---------------------------------------------------------------------
//  Liste (Klasse/Stufe oder Unterricht)
// ---------------------------------------------------------------------

function fzRenderListe(d) {
  const o = fzOben();
  el("fzListeWann").textContent = `${fzDatumLang(d.von)} – ${fzDatumLang(fzBis(d.bis))}`;
  el("fzListeTitel").textContent = d.titel;
  const n = d.schueler.length;
  el("fzListeInfo").textContent = (d.erfassteStunden !== null && d.erfassteStunden !== undefined
    ? `${d.erfassteStunden} ${d.erfassteStunden === 1 ? "Stunde" : "Stunden"} erfasst · ` : "") + `${n} Schüler`;
  const zelle = (wert, art) => h("td", { class: "zahl" + (wert > 0 && art ? " " + art : "") }, wert || "–");
  // Klasse unter dem Namen nur, wenn die Liste mehrere Klassen mischt (z. B. Religion 05A–05D).
  const mehrereKlassen = new Set(d.schueler.map((x) => x.klasse)).size > 1;
  el("fzListeZeilen").replaceChildren(...(n === 0
    ? [h("tr", null, h("td", { colspan: "5", class: "leer" }, "Keine Schülerinnen und Schüler hinterlegt."))]
    : d.schueler.map((s) => h("tr", null,
      h("th", { scope: "row" }, h("button", {
        class: "fz-name", type: "button",
        onclick: () => {
          const ziel = { art: "schueler", id: s.id };
          if (o.art === "unterricht") { ziel.unr = o.unr; ziel.lehrer = o.lehrer; }
          fz.stapel.push(ziel);
          fzZeigen();
        },
      }, `${s.nachname}, ${s.vorname}`,
      o.art === "unterricht" && mehrereKlassen && s.klasse ? h("span", { class: "fz-klasse" }, s.klasse) : null)),
      zelle(s.summe.fehlstunden),
      zelle(s.summe.offen + s.summe.verspaetungenOffen, "offen"),
      zelle(s.summe.unentschuldigt + s.summe.verspaetungenUnentschuldigt, "unent"),
      zelle(s.summe.verspaetungen)))));
}

// ---------------------------------------------------------------------
//  Themen und Hausaufgaben eines Unterrichts (Phase 3.3)
// ---------------------------------------------------------------------

function fzRenderThemen(d) {
  el("fzTWann").textContent = `${fzDatumLang(d.von)} – ${fzDatumLang(fzBis(d.bis))}`;
  el("fzTTitel").textContent = d.titel;
  const n = d.stunden.length;
  const mitThema = d.stunden.filter((x) => x.thema).length;
  const mitHa = d.stunden.filter((x) => x.hausaufgabe).length;
  el("fzTInfo").textContent = n
    ? `${n} ${n === 1 ? "Stunde" : "Stunden"} · ${mitThema} mit Thema · ${mitHa} mit Hausaufgabe` : "";
  el("fzTSichtbarSchalter").hidden = !d.darfSichtbarkeit;
  el("fzTSichtbar").checked = !!d.hausaufgabenSichtbar;
  el("fzTSichtbar").disabled = false;
  const info = d.hausaufgabenSichtbar ? "" : "Die Hausaufgaben dieses Kurses sind für Schüler ausgeblendet.";
  el("fzTSichtbarInfo").textContent = d.darfSichtbarkeit ? "" : info;
  el("fzTSichtbarInfo").hidden = d.darfSichtbarkeit || !info;
  el("fzTLeer").textContent = n ? "" : "In diesem Zeitraum gibt es noch keine Stunde mit Klassenbuch.";
  el("fzTListe").replaceChildren(...d.stunden.map(fzThemaZeile));
}

function fzThemaZeile(s) {
  const plan = { stunde: s.stunde, grundUNr: s.grundUNr, grundLehrer: s.grundLehrer, lehrer: s.lehrer,
    originalLehrer: null, klassen: s.klassen, klassenbuch: true };
  const vertretung = s.lehrer && !gleich(s.lehrer, s.grundLehrer);
  // „Vertreten" steht schon rechts als „Vertretung L9".
  const st = s.status && s.status !== "Normal" && !(vertretung && s.status === "Vertreten") ? STATUS[s.status] : null;
  return h("li", null, h("button", {
    class: "k-zeile fz-thema", type: "button", disabled: !kbDarfOeffnen(plan),
    onclick: () => stundenblattOeffnen(plan, s.datum, "fehlzeiten"),
  },
  h("span", { class: "k-oben" },
    h("span", { class: "k-name" }, `${fzDatum(s.datum)} · ${s.stunde}. Stunde`),
    vertretung ? h("span", { class: "k-rechts" }, `Vertretung ${s.lehrer}`) : null),
  h("span", { class: "fz-t-thema" + (s.thema ? "" : " leer") }, s.thema || "kein Thema eingetragen"),
  s.hausaufgabe
    ? h("span", { class: "fz-t-ha" },
        h("span", { class: "fz-t-etikett" }, s.faellig ? `Hausaufgabe bis ${fzDatum(s.faellig)}: ` : "Hausaufgabe: "),
        s.hausaufgabe)
    : null,
  !s.erfasst || st
    ? h("span", { class: "k-marken" },
        !s.erfasst ? h("span", { class: "marke start" }, "Anwesenheit fehlt") : null,
        st ? h("span", { class: "marke" }, st.text) : null)
    : null));
}

/** Schalter „Hausaufgaben für Schüler sichtbar" (gilt für den ganzen Unterricht). */
async function fzSichtbarSetzen() {
  const d = fz.daten;
  const o = fzOben();
  if (!d || !o || o.art !== "themen") return;
  const an = el("fzTSichtbar").checked;
  el("fzTSichtbar").disabled = true;
  fehlerIn("fzFehler", "");
  const nr = fz.ladeNr;
  try {
    const a = await senden("PUT", "api/klassenbuch/sichtbarkeit", { uNr: d.uNr, lehrer: d.lehrer, sichtbar: an, datum: null });
    if (nr !== fz.ladeNr) return;
    d.hausaufgabenSichtbar = !!a.sichtbar;
    toast(a.meldung || "Gespeichert.");
  } catch (f) {
    if (nr !== fz.ladeNr) return;
    el("fzTSichtbar").checked = !an;
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("fzFehler", t);
  } finally {
    if (nr === fz.ladeNr) el("fzTSichtbar").disabled = false;
  }
}

// ---------------------------------------------------------------------
//  Schüler
// ---------------------------------------------------------------------

function fzRenderSchueler(d) {
  el("fzSWann").textContent = `${fzDatumLang(d.von)} – ${fzDatumLang(fzBis(d.bis))}`;
  el("fzSName").textContent = `${d.vorname} ${d.nachname}`;
  const info = [d.klasse || ""];
  if (d.unterricht) info.push(`nur ${d.unterricht}`);
  else if (d.nurEigeneStunden) info.push("nur deine Stunden");
  el("fzSInfo").replaceChildren(info.filter(Boolean).join(" · "));

  const s = d.summe;
  const karte = (zahl, titel, details, art) => h("div", { class: "fz-karte" + (art ? " " + art : "") },
    h("span", { class: "fz-zahl" }, zahl),
    h("span", { class: "fz-was" }, titel),
    details ? h("span", { class: "fz-details" }, details) : null);
  const teile = (paare) => paare.filter(([w]) => w > 0).map(([w, t]) => `${w} ${t}`).join(" · ");
  el("fzSumme").replaceChildren(...[
    karte(s.fehlstunden, s.fehlstunden === 1 ? "Fehlstunde" : "Fehlstunden",
      teile([[s.entschuldigt, "entschuldigt"], [s.offen, "offen"], [s.unentschuldigt, "unentschuldigt"]]),
      s.unentschuldigt > 0 ? "unent" : s.offen > 0 ? "offen" : null),
    karte(s.verspaetungen, s.verspaetungen === 1 ? "Verspätung" : "Verspätungen",
      teile([[s.verspaetungMinuten, "Min."], [s.verspaetungenOffen, "offen"], [s.verspaetungenUnentschuldigt, "unentschuldigt"]]),
      s.verspaetungenUnentschuldigt > 0 ? "unent" : s.verspaetungenOffen > 0 ? "offen" : null),
    s.schulveranstaltungen > 0 ? karte(s.schulveranstaltungen, "schulische Veranst.", "keine Fehlzeit") : null,
  ].filter(Boolean));

  fzRenderEntschuldigungen(d);
  fzRenderFaecher(d);

  const n = d.eintraege.length;
  el("fzEintraegeInfo").textContent = n ? "" : "Keine Fehlzeiten in diesem Zeitraum.";
  el("fzEintraege").replaceChildren(...d.eintraege.map((e) => fzEintragZeile(e)));

  // Phase 3.4: Klassenbucheinträge (nur Personal; Schüler sehen keine Einträge).
  const o = fzOben();
  if (o && o.art === "schueler" && istPersonal()) keSchuelerLaden(o);
}

function fzRenderEntschuldigungen(d) {
  const box = el("fzEntsch");
  if (!d.darfZeitraumEntschuldigen && !d.entschuldigungen.length) { box.replaceChildren(); return; }
  box.replaceChildren(
    h("div", { class: "fz-kopfzeile" },
      h("h3", { class: "fz-titel" }, "Entschuldigungen"),
      d.darfZeitraumEntschuldigen
        ? h("button", { class: "hauptknopf klein", type: "button", onclick: () => fzEntschuldigungOeffnen(d) }, "Entschuldigung eintragen")
        : null),
    d.entschuldigungen.length
      ? h("ul", { class: "k-liste" }, d.entschuldigungen.map((e) => h("li", { class: "fz-ent" },
          h("span", { class: "fz-ent-text" },
            h("strong", null, e.von === e.bis ? fzDatumLang(e.von) : `${fzDatumLang(e.von)} – ${fzDatumLang(e.bis)}`),
            ` · ${FZ_GRUND[e.grund] || e.grund} · ${e.stunden} ${e.stunden === 1 ? "Stunde" : "Stunden"}`,
            h("span", { class: "fz-ent-wer" }, `eingetragen von ${e.erfasstVon}, ${kurzZeit(e.erfasstAm)}`)),
          d.darfZeitraumEntschuldigen
            ? h("button", {
                class: "textknopf klein", type: "button",
                onclick: (ev) => bestaetigen(ev.currentTarget, "Wirklich löschen?", () => fzEntschuldigungLoeschen(e.id)),
              }, "Löschen")
            : null)))
      : h("p", { class: "such-info" }, "Noch keine Entschuldigung eingetragen."));
}

function fzRenderFaecher(d) {
  const box = el("fzFaecher");
  if (d.unterricht || d.faecher.length < 2) { box.replaceChildren(); return; }
  box.replaceChildren(
    h("h3", { class: "fz-titel" }, "Nach Fächern"),
    h("div", { class: "tabelle-huelle fz-huelle" }, h("table", { class: "tabelle fz-tabelle" },
      h("thead", null, h("tr", null,
        h("th", { scope: "col" }, "Fach"),
        h("th", { scope: "col", class: "zahl" }, "Fehlst."),
        h("th", { scope: "col", class: "zahl" }, "offen"),
        h("th", { scope: "col", class: "zahl" }, "unent."),
        h("th", { scope: "col", class: "zahl" }, "Versp."))),
      h("tbody", null, d.faecher.map((f) => h("tr", null,
        h("th", { scope: "row" }, f.fach, h("span", { class: "fz-klasse" }, f.lehrer)),
        h("td", { class: "zahl" }, f.summe.fehlstunden || "–"),
        h("td", { class: "zahl" + (f.summe.offen ? " offen" : "") }, f.summe.offen || "–"),
        h("td", { class: "zahl" + (f.summe.unentschuldigt ? " unent" : "") }, f.summe.unentschuldigt || "–"),
        h("td", { class: "zahl" }, f.summe.verspaetungen || "–")))))));
}

function fzEintragZeile(e) {
  const status = e.status === "Verspaetet" && e.minuten ? `verspätet ${e.minuten} Min.` : FZ_STATUS[e.status] || e.status;
  let bew = null;
  if (e.bewertung) {
    bew = FZ_BEWERTUNG[e.bewertung] || e.bewertung;
    if (e.status === "Beurlaubt") bew = "entschuldigt";
    else if (e.bewertung === "Entschuldigt" && e.grund) bew = `entschuldigt · ${FZ_GRUND[e.grund] || e.grund}`;
  }
  const art = e.bewertung ? "b-" + e.bewertung.toLowerCase() : "b-keine";
  const wer = e.lehrer && !gleich(e.lehrer, e.grundLehrer) ? `${e.grundLehrer}, Vertretung ${e.lehrer}` : e.grundLehrer;
  const hinweis = [];
  if (e.entschuldigungId) hinweis.push("über Entschuldigung");
  if (e.sperrgrund) hinweis.push(e.sperrgrund);
  else if (e.bewertetVon && e.bewertung && e.status !== "Beurlaubt" && e.bewertung !== "Offen")
    hinweis.push(`von ${e.bewertetVon}`);

  const inhalt = [
    h("span", { class: "k-oben" },
      h("span", { class: "k-name" }, `${fzDatum(e.datum)} · ${e.stunde}. Stunde`),
      h("span", { class: "marke fz-status s-" + (KB_STATUS[e.status] ? KB_STATUS[e.status].art : "") }, status)),
    h("span", { class: "k-unten fz-unten" },
      h("span", null, `${e.fach} · ${wer}`),
      bew ? h("span", { class: "marke fz-bew " + art }, bew) : null),
    hinweis.length ? h("span", { class: "fz-hinweis" }, hinweis.join(" · ")) : null,
  ];
  if (!e.darfBewerten) return h("li", null, h("div", { class: "k-zeile fz-eintrag " + art }, inhalt));
  return h("li", null, h("button", {
    class: "k-zeile fz-eintrag " + art, type: "button",
    "aria-haspopup": "dialog",
    onclick: () => fzBewertenOeffnen(e),
  }, inhalt, h("span", { class: "fz-aktion" }, "Bewerten")));
}

// ---------------------------------------------------------------------
//  Bewerten
// ---------------------------------------------------------------------

function fzBewertenOeffnen(e) {
  fz.eintrag = e;
  const status = e.status === "Verspaetet" ? `Verspätung${e.minuten ? ` (${e.minuten} Min.)` : ""}` : "Fehlstunde";
  el("fbTitel").textContent = `${status} bewerten`;
  el("fbWas").textContent = `${fzDatum(e.datum)} · ${e.stunde}. Stunde · ${e.fach} · ${e.grundLehrer}`;
  for (const r of document.querySelectorAll('input[name="fbBew"]')) r.checked = r.value === e.bewertung;
  el("fbGrund").value = e.grund || "Krankheit";
  el("fbHinweis").hidden = !e.entschuldigungId;
  el("fbHinweis").textContent = e.entschuldigungId
    ? "Diese Stunde ist über eine Entschuldigung (Zeitraum) entschuldigt. Eine Änderung hier gilt nur für diese Stunde." : "";
  fehlerIn("fbFehler", "");
  fbGrundAnpassen();
  dialogAuf(el("fzBewertDialog"));
  const gewaehlt = document.querySelector('input[name="fbBew"]:checked');
  (gewaehlt || document.querySelector('input[name="fbBew"]')).focus();
}

function fbGrundAnpassen() {
  const r = document.querySelector('input[name="fbBew"]:checked');
  el("fbGrundBox").hidden = !r || r.value !== "Entschuldigt";
}

async function fzBewertungSpeichern() {
  const e = fz.eintrag;
  const r = document.querySelector('input[name="fbBew"]:checked');
  if (!e || !r) { fehlerIn("fbFehler", "Bitte eine Bewertung wählen."); return; }
  const knopf = el("fbSpeichern");
  knopf.disabled = true;
  fehlerIn("fbFehler", "");
  try {
    const antwort = await senden("PUT", `api/fehlzeiten/eintrag/${e.id}`,
      { bewertung: r.value, grund: r.value === "Entschuldigt" ? el("fbGrund").value : null });
    dialogZu(el("fzBewertDialog"));
    if (antwort && antwort.meldung) toast(antwort.meldung);
    fzLaden();
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("fbFehler", t);
  } finally {
    knopf.disabled = false;
  }
}

// ---------------------------------------------------------------------
//  Entschuldigung (Zeitraum)
// ---------------------------------------------------------------------

function fzEntschuldigungOeffnen(d) {
  const offen = d.eintraege.filter((e) => e.bewertung === "Offen").map((e) => e.datum).sort();
  el("feFuer").textContent = `Für ${d.vorname} ${d.nachname}${d.klasse ? ` (${d.klasse})` : ""}`;
  el("feVon").value = offen.length ? offen[0] : heuteIso();
  el("feBis").value = offen.length ? offen[offen.length - 1] : heuteIso();
  el("feGrund").value = "Krankheit";
  fehlerIn("feFehler", "");
  dialogAuf(el("fzEntschDialog"));
  el("feVon").focus();
}

async function fzEntschuldigungSpeichern() {
  const o = fzOben();
  const von = el("feVon").value;
  const bis = el("feBis").value;
  if (!istIso(von) || !istIso(bis)) { fehlerIn("feFehler", "Bitte „von“ und „bis“ angeben."); return; }
  if (bis < von) { fehlerIn("feFehler", "„Bis“ liegt vor „von“."); return; }
  const knopf = el("feSpeichern");
  knopf.disabled = true;
  fehlerIn("feFehler", "");
  try {
    const antwort = await senden("POST", `api/fehlzeiten/schueler/${o.id}/entschuldigung`,
      { von, bis, grund: el("feGrund").value });
    dialogZu(el("fzEntschDialog"));
    if (antwort && antwort.meldung) toast(antwort.meldung);
    fzLaden();
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("feFehler", t);
  } finally {
    knopf.disabled = false;
  }
}

async function fzEntschuldigungLoeschen(id) {
  try {
    const antwort = await senden("DELETE", `api/fehlzeiten/entschuldigung/${id}`);
    if (antwort && antwort.meldung) toast(antwort.meldung);
    fzLaden();
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("fzFehler", t);
  }
}

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function fehlzeitenInit() {
  el("fzKnopf").addEventListener("click", fehlzeitenOeffnen);
  el("fzZurueck").addEventListener("click", fzZurueck);
  el("fzKlasseWahl").addEventListener("change", (e) => { fz.klasse = e.target.value; fzZeigen(); });
  el("fzNeLehrer").addEventListener("change", (e) => { fz.neLehrer = e.target.value; fzLaden(); });
  el("fzNeKlasse").addEventListener("change", (e) => { fz.neKlasse = e.target.value; fzLaden(); });
  // Phase 3.3: Unterricht – Fehlzeiten | Themen (ersetzt die oberste Ansicht)
  for (const b of el("fzUWahl").querySelectorAll("button")) {
    b.addEventListener("click", () => {
      const o = fzOben();
      if (!o || o.art === b.dataset.u || !["unterricht", "themen", "keUnterricht"].includes(o.art)) return;
      fz.stapel[fz.stapel.length - 1] = { art: b.dataset.u, unr: o.unr, lehrer: o.lehrer };
      fzZeigen();
    });
  }
  el("fzTSichtbar").addEventListener("change", fzSichtbarSetzen);
  el("fzReiter").addEventListener("keydown", (e) => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
    const liste = fzReiterListe().map((r) => r.id);
    const i = (liste.indexOf(fzOben().reiter) + (e.key === "ArrowRight" ? 1 : liste.length - 1)) % liste.length;
    fz.stapel = [{ art: "reiter", reiter: liste[i] }];
    fzZeigen();
    const b = el("fzReiter").querySelectorAll("button")[i];
    if (b) b.focus();
  });

  for (const r of document.querySelectorAll('input[name="fbBew"]')) r.addEventListener("change", fbGrundAnpassen);
  el("fbSpeichern").addEventListener("click", fzBewertungSpeichern);
  el("fbAbbrechen").addEventListener("click", () => dialogZu(el("fzBewertDialog")));
  el("fzBewertDialog").addEventListener("close", () => { fz.eintrag = null; });
  hintergrundSchliesst(el("fzBewertDialog"), () => dialogZu(el("fzBewertDialog")));

  el("feSpeichern").addEventListener("click", fzEntschuldigungSpeichern);
  el("feAbbrechen").addEventListener("click", () => dialogZu(el("fzEntschDialog")));
  hintergrundSchliesst(el("fzEntschDialog"), () => dialogZu(el("fzEntschDialog")));
}
