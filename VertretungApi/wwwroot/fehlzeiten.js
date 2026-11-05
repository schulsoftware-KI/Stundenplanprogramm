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
//  Phase 4.1 (Auswertungen, Entscheidungen 40–43): Zeitraum überall wählbar
//  (1. Halbjahr · 2. Halbjahr · Schuljahr · eigener Zeitraum; Standard: das
//  laufende Halbjahr, auch für „Meine Fehlzeiten"). Klasse/Stufe bzw.
//  Unterricht als Konferenzübersicht: Fehlstunden, davon entschuldigt/offen/
//  unentschuldigt, Fehlquote, Verspätungen + Minuten; sortierbar, Gesamtzeile,
//  „nur mit offenen". Klasse/Stufe zusätzlich „Offene Fälle" (ganzes
//  Schuljahr, älteste zuerst, direkt „Entschuldigung eintragen"). Fehlquote
//  = Fehlstunden ÷ erfasste Stunden, in denen der Schüler dabei sein sollte –
//  nur Anzeige, keine Schwellen.
//
//  Phase 4.2 (Entscheidungen 44–47, 50): „Klassenbuch als PDF …" (Klasse/
//  Stufe; wahlweise ein Kursheft der Klasse) bzw. „Kursheft als PDF …"
//  (Unterricht) – im gewählten Zeitraum, Schülereinträge nur auf Wunsch (KL/SK/
//  Admin). Hinweis „enthält Schülerdaten" im Dialog; der Server protokolliert.
//  Das PDF wird nur im Arbeitsspeicher gehalten und als Datei angeboten.
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
  // Phase 4.1
  zeitraeume: null,  // Antwort api/fehlzeiten/zeitraeume
  zr: null,          // gewählter Zeitraum { id, von, bis } (id "frei" = eigener Zeitraum)
  zrFreiOffen: false,
  kAnsicht: "uebersicht",               // Klasse/Stufe: uebersicht | offen
  sort: { feld: "name", ab: false },    // Konferenzübersicht (ab = absteigend)
  nurOffen: false,
  entschFuer: null,  // Schüler-Id im Dialog „Entschuldigung eintragen"
  // Phase 4.2
  pdf: null,         // im PDF-Dialog: { art: "klasse", klasse } | { art: "kurs", unr, lehrer, titel }
  pdfLaeuft: false,
  pdfNr: 0,
};

const fehlzeitenOffen = () => !el("fehlzeiten").hidden;
const fzOben = () => fz.stapel[fz.stapel.length - 1] || null;
const fzDatum = (iso) => { const d = ausIso(iso); return `${FZ_WT[d.getDay()]} ${pad(d.getDate())}.${pad(d.getMonth() + 1)}.`; };
const fzDatumLang = (iso) => { const d = ausIso(iso); return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()}`; };
const fzBis = (iso) => (iso > heuteIso() ? heuteIso() : iso);
/** „01.09.2026 – 05.11.2026": ein laufender Zeitraum endet heute; ein künftiger (2. Halbjahr) bleibt ganz. */
const fzZeitraumText = (von, bis) =>
  `${fzDatumLang(von)} – ${fzDatumLang(von > heuteIso() ? bis : fzBis(bis))}`;

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

  fzTitel(istSchueler() ? "Meine Fehlzeiten" : "Klassenbuch");
  const nr = ++fz.ladeNr;
  document.body.classList.add("laedt");
  try {
    // Phase 4.1: Zeiträume (Standard: laufendes Halbjahr) gleich mit laden.
    const [z, r] = await Promise.all([
      senden("GET", "api/fehlzeiten/zeitraeume"),
      istSchueler() ? Promise.resolve(null) : senden("GET", "api/fehlzeiten/rechte"),
    ]);
    if (nr !== fz.ladeNr) return;
    fzZeitraeumeUebernehmen(z);
    if (istSchueler()) {
      fz.stapel = [{ art: "ich" }];
      fzZeigen();
      return;
    }
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
  fz.zeitraeume = null;
  fz.zr = null;
  fz.zrFreiOffen = false;
  fz.kAnsicht = "uebersicht";
  fz.sort = { feld: "name", ab: false };
  fz.nurOffen = false;
  fz.entschFuer = null;
  fz.pdf = null;
  fz.pdfLaeuft = false;
  fz.pdfNr++;
  el("fzPdfLeiste").hidden = true;
  el("pdUmfang").replaceChildren();
  for (const id of ["pdWas", "pdZeitraum", "pdStatus"]) el(id).textContent = "";
  dialogZu(el("fzPdfDialog"));
  for (const id of ["fzReiter", "fzNeWahl", "fzNeArt", "fzNeLehrer", "fzNeKlasse", "fzNeListe", "fzKurseListe", "fzKlasseWahl",
    "fzListeZeilen", "fzSumme", "fzEntsch", "fzFaecher", "fzEintraege", "fzSInfo", "fzTListe",
    "fzListeKopf", "fzListeFuss", "fzListeOffen", "fzOListe", "fzZrWahl"])
    el(id).replaceChildren();
  for (const id of ["fzNeInfo", "fzKurseInfo", "fzListeWann", "fzListeTitel", "fzListeInfo", "fzSWann", "fzSName",
    "fzEintraegeInfo", "fbWas", "feFuer", "fzTWann", "fzTTitel", "fzTInfo", "fzTLeer", "fzTSichtbarInfo",
    "fzOWann", "fzOTitel", "fzOInfo"])
    el(id).textContent = "";
  el("fzZeitraum").hidden = true;
  el("fzKWahl").hidden = true;
  el("fzNurOffen").checked = false;
  fehlerIn("fzZrFehler", "");
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
const fzAlleStundenText = (k) => (/^\d\d$/.test(k) ? `Alle Stunden des Jahrgangs ${k}` : `Alle Stunden der ${k.toUpperCase()}`);

function fzNeAnsichten() {
  const r = fz.rechte;
  const l = [];
  if (!r) return l;
  if (r.lehrkraft) l.push({ id: "ich", text: "Meine Stunden" });
  // Entscheidung 57: nicht „Klasse 05A" (wie der Reiter), sondern „Alle Stunden der 05A".
  if (!r.admin && r.klassen.length)
    l.push({ id: "zustaendig", text: r.klassen.length === 1 ? fzAlleStundenText(r.klassen[0]) : "Alle Stunden meiner Klassen" });
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
  for (const id of ["fzNe", "fzKurse", "fzKlasseBox", "fzListe", "fzSchueler", "fzThemen", "fzKeAnsicht", "fzOffen"]) el(id).hidden = true;
  el("fzZurueckText").textContent = fz.stapel.length > 1 ? "Zurück" : "Zum Plan";
  // Phase 3.3: Unterricht – Fehlzeiten | Themen (Phase 3.4: | Einträge)
  const uSicht = !!o && (o.art === "unterricht" || o.art === "themen" || o.art === "keUnterricht");
  el("fzUWahl").hidden = !uSicht;
  if (uSicht)
    for (const b of el("fzUWahl").querySelectorAll("button")) b.setAttribute("aria-pressed", String(b.dataset.u === o.art));
  const reiterSicht = !!o && o.art === "reiter";
  el("fzReiter").hidden = !reiterSicht || fzReiterListe().length < 2;
  if (reiterSicht) fzRenderReiter();
  // Phase 4.1: Klasse/Stufe – Übersicht | Offene Fälle; Zeitraum (nicht bei „nicht erfasst", „Meine Kurse", offene Fälle)
  const klasseSicht = reiterSicht && o.reiter === "klasse";
  el("fzKWahl").hidden = !klasseSicht;
  if (klasseSicht)
    for (const b of el("fzKWahl").querySelectorAll("button")) b.setAttribute("aria-pressed", String(b.dataset.k === fz.kAnsicht));
  el("fzZeitraum").hidden = !fzMitZeitraum(o);
  if (!el("fzZeitraum").hidden) fzRenderZeitraum();
  fzPdfLeisteRendern(o);   // Phase 4.2
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
  } else if (o.art === "reiter" && o.reiter === "klasse" && fz.kAnsicht === "offen") {
    // Phase 4.1: offene Fälle der Klasse/Stufe
    el("fzKlasseBox").hidden = fz.rechte.klassen.length < 2;
    el("fzKlasseWahl").replaceChildren(...fz.rechte.klassen.map((k) =>
      h("option", { value: k, selected: k === fz.klasse }, fzGruppenName(k))));
    el("fzOffen").hidden = false;
    el("fzOListe").replaceChildren();
    el("fzOWann").textContent = "";
    el("fzOTitel").textContent = "";
    el("fzOInfo").textContent = "Wird geladen …";
  } else if (o.art === "reiter" || o.art === "unterricht") {
    if (o.art === "reiter") {
      el("fzKlasseBox").hidden = fz.rechte.klassen.length < 2;
      el("fzKlasseWahl").replaceChildren(...fz.rechte.klassen.map((k) =>
        h("option", { value: k, selected: k === fz.klasse }, fzGruppenName(k))));
    }
    el("fzListe").hidden = false;
    for (const id of ["fzListeKopf", "fzListeZeilen", "fzListeFuss", "fzListeOffen"]) el(id).replaceChildren();
    el("fzListeOffen").hidden = true;
    el("fzNurOffenSchalter").hidden = true;
    el("fzListeTitel").textContent = "";
    el("fzListeWann").textContent = "";
    el("fzListeInfo").textContent = "Wird geladen …";
  } else {
    el("fzSchueler").hidden = false;
    el("fzIchHinweis").hidden = o.art !== "ich";   // Hilfe: was „offen" heißt (nur Schülerkonto)
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
  const zr = fzMitZr;
  if (o.art === "reiter" && o.reiter === "ne") {
    let url = `api/fehlzeiten/nicht-erfasst?ansicht=${fz.neAnsicht}&art=${fz.neArt}`;
    if (fz.neAnsicht === "alle" && fz.neLehrer) url += `&lehrer=${enc(fz.neLehrer)}`;
    if (fz.neAnsicht === "alle" && fz.neKlasse) url += `&klasse=${enc(fz.neKlasse)}`;
    return url;
  }
  if (o.art === "reiter" && o.reiter === "kurse") return "api/fehlzeiten/unterrichte";
  if (o.art === "reiter" && o.reiter === "eintraege") return fz.klasse ? zr(`api/eintraege/klasse/${enc(fz.klasse)}`) : null;
  if (o.art === "keUnterricht") return zr(`api/eintraege/unterricht?unr=${o.unr}&lehrer=${enc(o.lehrer)}`);
  if (o.art === "reiter" && fz.kAnsicht === "offen") return fz.klasse ? `api/fehlzeiten/offen/${enc(fz.klasse)}` : null;
  if (o.art === "reiter") return fz.klasse ? zr(`api/fehlzeiten/klasse/${enc(fz.klasse)}`) : null;
  if (o.art === "unterricht") return zr(`api/fehlzeiten/unterricht?unr=${o.unr}&lehrer=${enc(o.lehrer)}`);
  if (o.art === "themen") return zr(`api/klassenbuch/themen?unr=${o.unr}&lehrer=${enc(o.lehrer)}`);
  if (o.art === "schueler")
    return zr(`api/fehlzeiten/schueler/${o.id}` + (o.unr ? `?unr=${o.unr}&lehrer=${enc(o.lehrer)}` : ""));
  return zr("api/fehlzeiten/ich");
}

async function fzLaden() {
  const o = fzOben();
  if (!o) return;
  const url = fzUrl(o);
  if (!url) {
    for (const id of ["fzListeInfo", "fzKeLeer", "fzOInfo"]) el(id).textContent = "Keine Klasse gewählt.";
    return;
  }
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
    else if (o.art === "reiter" && fz.kAnsicht === "offen") fzRenderOffen(d);
    else if (o.art === "reiter" || o.art === "unterricht") fzRenderListe(d);
    else fzRenderSchueler(d);
  } catch (f) {
    if (nr !== fz.ladeNr) return;
    const t = aktionsFehler(f);
    if (t === null) return;
    for (const id of ["fzNeInfo", "fzKurseInfo", "fzListeInfo", "fzEintraegeInfo", "fzTLeer", "fzKeLeer", "fzOInfo"]) el(id).textContent = "";
    fehlerIn("fzFehler", t);
  } finally {
    if (nr === fz.ladeNr) document.body.classList.remove("laedt");
  }
}

// ---------------------------------------------------------------------
//  Zeitraum (Phase 4.1, Entscheidung 40)
// ---------------------------------------------------------------------

/** Antwort api/fehlzeiten/zeitraeume übernehmen; gewählt wird der Standard (laufendes Halbjahr). */
function fzZeitraeumeUebernehmen(z) {
  fz.zeitraeume = z && Array.isArray(z.zeitraeume) ? z : null;
  const liste = fz.zeitraeume ? fz.zeitraeume.zeitraeume : [];
  const std = liste.find((x) => x.id === fz.zeitraeume.standard) || liste[0];
  fz.zr = std ? { id: std.id, von: std.von, bis: std.bis } : null;
  fz.zrFreiOffen = false;
}

/** Adresse mit dem gewählten Zeitraum (ohne Auswahl: Standard des Servers = Schuljahr). */
function fzMitZr(url) {
  if (!fz.zr) return url;
  return `${url}${url.includes("?") ? "&" : "?"}von=${fz.zr.von}&bis=${fz.zr.bis}`;
}

/** Ansichten mit Zeitraum: Listen, Themen, Einträge, Schüler – nicht „nicht erfasst", „Meine Kurse", offene Fälle. */
function fzMitZeitraum(o) {
  if (!o || !fz.zeitraeume) return false;
  if (o.art === "reiter") return o.reiter === "eintraege" || (o.reiter === "klasse" && fz.kAnsicht === "uebersicht");
  return ["unterricht", "themen", "keUnterricht", "schueler", "ich"].includes(o.art);
}

const FZ_ZR_KURZ = { hj1: "1. Hj.", hj2: "2. Hj.", sj: "Schuljahr", frei: "Eigener" };

function fzRenderZeitraum() {
  const knopf = (id, lang, gewaehlt, onclick) => h("button", {
    type: "button", "aria-pressed": String(gewaehlt), "aria-label": lang, "data-zr": id, onclick,
  },
  h("span", { class: "zr-lang", "aria-hidden": "true" }, lang),
  h("span", { class: "zr-kurz", "aria-hidden": "true" }, lang.startsWith("Letzte") ? "12 Monate" : FZ_ZR_KURZ[id] || lang));
  const frei = !!fz.zr && fz.zr.id === "frei";
  el("fzZrWahl").replaceChildren(
    ...fz.zeitraeume.zeitraeume.map((x) => knopf(x.id, x.text, !!fz.zr && fz.zr.id === x.id && !fz.zrFreiOffen,
      () => fzZrWaehlen({ id: x.id, von: x.von, bis: x.bis }))),
    knopf("frei", "Eigener Zeitraum", frei || fz.zrFreiOffen, () => {
      fz.zrFreiOffen = !fz.zrFreiOffen || !frei;
      fzRenderZeitraum();
      if (fz.zrFreiOffen) el("fzZrVon").focus();
    }));
  el("fzZrFrei").hidden = !fz.zrFreiOffen;
  if (fz.zrFreiOffen && fz.zr) {
    el("fzZrVon").value = fz.zr.von;
    el("fzZrBis").value = fz.zr.bis > heuteIso() && fz.zr.von <= heuteIso() ? heuteIso() : fz.zr.bis;
  }
}

function fzZrWaehlen(zr) {
  fehlerIn("fzZrFehler", "");
  fz.zrFreiOffen = false;
  if (fz.zr && fz.zr.id === zr.id && fz.zr.von === zr.von && fz.zr.bis === zr.bis) { fzRenderZeitraum(); return; }
  fz.zr = zr;
  fzZeigen();
}

/** „Anzeigen" beim eigenen Zeitraum. */
function fzZrFreiAnwenden() {
  const von = el("fzZrVon").value;
  const bis = el("fzZrBis").value;
  if (!istIso(von) || !istIso(bis)) { fehlerIn("fzZrFehler", "Bitte „von“ und „bis“ angeben."); return; }
  if (bis < von) { fehlerIn("fzZrFehler", "„Bis“ liegt vor „von“."); return; }
  fzZrWaehlen({ id: "frei", von, bis });
}

/** Fehlquote: Fehlstunden ÷ Soll-Stunden; null, wenn keine Stunde erfasst ist. */
const fzQuote = (fehl, soll) => (soll > 0 ? fehl / soll : null);
const fzProzent = (q) => (q === null ? "–"
  : `${(q * 100).toLocaleString("de-DE", { maximumFractionDigits: 1 })}\u202f%`);

// ---------------------------------------------------------------------
//  Klassenbuch / Kursheft als PDF (Phase 4.2)
// ---------------------------------------------------------------------

/** Wer was drucken darf (Entscheidung 46) – der Server prüft dasselbe. */
function fzPdfZiel(o) {
  const r = fz.rechte;
  if (!o || !r || r.berichte === false) return null;
  if (o.art === "reiter" && o.reiter === "klasse" && fz.kAnsicht === "uebersicht" && fz.klasse)
    return { art: "klasse", klasse: fz.klasse };
  if (["unterricht", "themen", "keUnterricht"].includes(o.art) && (r.admin || gleich(eigenesKuerzel(), o.lehrer)))
    return { art: "kurs", unr: o.unr, lehrer: o.lehrer };
  return null;
}

function fzPdfLeisteRendern(o) {
  const ziel = fzPdfZiel(o);
  el("fzPdfLeiste").hidden = !ziel;
  if (ziel) el("fzPdfKnopfText").textContent = ziel.art === "klasse" ? "Klassenbuch als PDF …" : "Kursheft als PDF …";
}

const fzDarfSchuelerEintraege = () =>
  !!state.anmeldung && ["Admin", "Klassenlehrer", "Stufenkoordination"].includes(state.anmeldung.rolle);

async function fzPdfOeffnen() {
  const ziel = fzPdfZiel(fzOben());
  if (!ziel) return;
  fz.pdf = ziel;
  const nr = ++fz.pdfNr;
  const klasse = ziel.art === "klasse";
  el("pdTitel").textContent = klasse ? "Klassenbuch als PDF" : "Kursheft als PDF";
  el("pdWas").textContent = klasse ? fzGruppenName(ziel.klasse) : (el("fzListeTitel").textContent || el("fzTTitel").textContent
    || el("fzKeTitel").textContent || "Unterricht");
  el("pdUmfangBox").hidden = !klasse;
  el("pdUmfang").replaceChildren(h("option", { value: "klasse" }, "Ganzes Klassenbuch"));
  el("pdSchuelerSchalter").hidden = !fzDarfSchuelerEintraege();
  el("pdSchueler").checked = false;   // Entscheidung 45: Standard aus
  fehlerIn("pdFehler", "");
  el("pdStatus").textContent = "";

  // Zeitraum (wie oben gewählt; endet spätestens heute)
  const zr = fz.zr;
  const name = !zr ? "Schuljahr" : zr.id === "frei" ? "Eigener Zeitraum"
    : ((fz.zeitraeume && fz.zeitraeume.zeitraeume.find((x) => x.id === zr.id)) || { text: "" }).text;
  const zukunft = !!zr && zr.von > heuteIso();
  el("pdZeitraum").textContent = zukunft
    ? `${name} (${fzZeitraumText(zr.von, zr.bis)}) liegt noch in der Zukunft – es gibt noch nichts zu drucken. Wähle oben einen anderen Zeitraum.`
    : `Zeitraum: ${name}${zr ? ` · ${fzZeitraumText(zr.von, zr.bis)}` : ""} – ändern über die Zeitraumleiste.`;
  el("pdErstellen").disabled = zukunft;
  dialogAuf(el("fzPdfDialog"));
  (klasse ? el("pdUmfang") : el("pdErstellen")).focus();

  if (klasse) {
    // Kurshefte der Klasse/Stufe zur Auswahl
    try {
      const liste = await senden("GET", `api/berichte/unterrichte?klasse=${encodeURIComponent(ziel.klasse)}`);
      if (nr !== fz.pdfNr) return;
      if (liste.length) {
        el("pdUmfang").append(h("optgroup", { label: "Kursheft eines Unterrichts" },
          liste.map((u) => h("option", { value: `${u.uNr}|${u.lehrer}` }, `${u.fach} · ${u.lehrer}` +
            (u.klassen.length > 1 ? ` (${u.klassen.join(", ")})` : "")))));
      }
    } catch (f) {
      if (nr !== fz.pdfNr) return;
      const t = aktionsFehler(f);
      if (t !== null) fehlerIn("pdFehler", t);
    }
  }
}

/** Dateiname aus Content-Disposition (filename* bevorzugt). */
function fzPdfDateiname(kopf, ersatz) {
  const k = kopf || "";
  const stern = k.match(/filename\*=UTF-8''([^;]+)/i);
  if (stern) { try { return decodeURIComponent(stern[1].trim()); } catch { /* weiter */ } }
  const einfach = k.match(/filename="?([^";]+)"?/i);
  return einfach ? einfach[1].trim() : ersatz;
}

async function fzPdfErstellen() {
  const p = fz.pdf;
  if (!p || fz.pdfLaeuft) return;
  const enc = encodeURIComponent;
  let url;
  const wahl = p.art === "klasse" ? el("pdUmfang").value : "";
  if (p.art === "kurs") url = `api/berichte/kursheft?unr=${p.unr}&lehrer=${enc(p.lehrer)}`;
  else if (wahl && wahl !== "klasse") {
    const [unr, lehrer] = wahl.split("|");
    url = `api/berichte/kursheft?unr=${enc(unr)}&lehrer=${enc(lehrer)}`;
  } else url = `api/berichte/klassenbuch?klasse=${enc(p.klasse)}`;
  const mitEintraegen = !el("pdSchuelerSchalter").hidden && el("pdSchueler").checked;
  url = fzMitZr(url) + `&schuelerEintraege=${mitEintraegen}`;

  const nr = fz.pdfNr;
  fz.pdfLaeuft = true;
  el("pdErstellen").disabled = true;
  el("pdErstellen").textContent = "PDF wird erstellt …";
  el("pdStatus").textContent = "Das kann bei einem ganzen Halbjahr einige Sekunden dauern.";
  fehlerIn("pdFehler", "");
  const abbruch = new AbortController();
  const timer = setTimeout(() => abbruch.abort(), 180000);
  try {
    let resp;
    try {
      resp = await fetch(url, { cache: "no-store", headers: authKopf(), signal: abbruch.signal });
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
    if (nr !== fz.pdfNr) return;
    const name = fzPdfDateiname(resp.headers.get("Content-Disposition"), "Klassenbuch.pdf");
    const adresse = URL.createObjectURL(blob);
    const a = h("a", { href: adresse, download: name, hidden: true });
    document.body.append(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(adresse), 60000);
    dialogZu(el("fzPdfDialog"));
    toast(`PDF erstellt: ${name}`);
  } catch (f) {
    if (nr !== fz.pdfNr) return;
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("pdFehler", t);
  } finally {
    clearTimeout(timer);
    if (nr === fz.pdfNr) {
      fz.pdfLaeuft = false;
      el("pdErstellen").disabled = !!fz.zr && fz.zr.von > heuteIso();
      el("pdErstellen").textContent = "PDF erstellen";
      el("pdStatus").textContent = "";
    }
  }
}

// ---------------------------------------------------------------------
//  Nicht erfasste Stunden
// ---------------------------------------------------------------------

function fzRenderNeWahl() {
  const ansichten = fzNeAnsichten();
  el("fzNeWahl").hidden = ansichten.length < 2;
  el("fzNeWahlTitel").hidden = ansichten.length < 2;   // Entscheidung 57: „Welche Stunden?" sichtbar
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

// Phase 4.1: Konferenzübersicht – Spalten (Fehlstunden, davon entschuldigt/offen/unentschuldigt,
// Fehlquote, Verspätungen, Minuten). „schmal": auf kleinen Handys ausgeblendet.
const FZ_SPALTEN = [
  { id: "name", text: "Name" },
  { id: "fehl", text: "Fehlst.", lang: "Fehlstunden", wert: (s) => s.summe.fehlstunden },
  { id: "ent", text: "entsch.", lang: "davon entschuldigt", wert: (s) => s.summe.entschuldigt, schmal: true },
  { id: "offen", text: "offen", lang: "davon offen", wert: (s) => s.summe.offen, art: "offen" },
  { id: "unent", text: "unent.", lang: "davon unentschuldigt", wert: (s) => s.summe.unentschuldigt, art: "unent" },
  { id: "quote", text: "Quote", lang: "Fehlquote", wert: (s) => fzQuote(s.summe.fehlstunden, s.soll) },
  { id: "versp", text: "Versp.", lang: "Verspätungen", wert: (s) => s.summe.verspaetungen },
  { id: "min", text: "Min.", lang: "Minuten Verspätung", wert: (s) => s.summe.verspaetungMinuten, schmal: true },
];

const fzHatOffene = (s) => s.summe.offen + s.summe.verspaetungenOffen > 0;

function fzSortiert(liste) {
  const { feld, ab } = fz.sort;
  const sp = FZ_SPALTEN.find((x) => x.id === feld);
  const nachName = (a, b) => sortierer.compare(a.nachname, b.nachname) || sortierer.compare(a.vorname, b.vorname) || a.id - b.id;
  return [...liste].sort((a, b) => {
    if (!sp || !sp.wert) return ab ? nachName(b, a) : nachName(a, b);
    const wa = sp.wert(a);
    const wb = sp.wert(b);
    if (wa === null && wb === null) return nachName(a, b);
    if (wa === null) return 1;   // ohne Quote immer unten
    if (wb === null) return -1;
    return (ab ? wb - wa : wa - wb) || nachName(a, b);
  });
}

function fzSortieren(feld) {
  fz.sort = fz.sort.feld === feld ? { feld, ab: !fz.sort.ab } : { feld, ab: feld !== "name" };
  if (fz.daten) fzRenderListe(fz.daten);
  const b = el("fzListeKopf").querySelector(`button[data-feld="${feld}"]`);
  if (b) b.focus();
}

function fzRenderListe(d) {
  const o = fzOben();
  el("fzListeWann").textContent = fzZeitraumText(d.von, d.bis);
  el("fzListeTitel").textContent = d.titel;
  const n = d.schueler.length;
  el("fzListeInfo").textContent = (d.erfassteStunden !== null && d.erfassteStunden !== undefined
    ? `${d.erfassteStunden} ${d.erfassteStunden === 1 ? "Stunde" : "Stunden"} erfasst · ` : "") + `${n} Schüler`;

  // „x Stunden noch offen" (Entscheidung 41) – bei Klasse/Stufe mit Sprung zu den offenen Fällen.
  const g = d.gesamt;
  const of = g.offen;
  const ov = g.verspaetungenOffen;
  const offenText = [of ? `${of} ${of === 1 ? "Fehlstunde" : "Fehlstunden"}` : "",
    ov ? `${ov} ${ov === 1 ? "Verspätung" : "Verspätungen"}` : ""].filter(Boolean).join(" und ");
  el("fzListeOffen").hidden = !offenText;
  el("fzListeOffen").replaceChildren(...(offenText ? [
    h("span", null, `Noch offen: ${offenText}.`),
    o.art === "reiter" ? h("button", {
      class: "textknopf klein", type: "button",
      onclick: () => { fz.kAnsicht = "offen"; fzZeigen(); },
    }, "Offene Fälle ansehen") : null,
  ] : []));
  el("fzNurOffenSchalter").hidden = !offenText && !fz.nurOffen;
  el("fzNurOffen").checked = fz.nurOffen;

  // Kopf mit Sortierknöpfen
  el("fzListeKopf").replaceChildren(h("tr", null, FZ_SPALTEN.map((sp) => {
    const aktiv = fz.sort.feld === sp.id;
    return h("th", {
      scope: "col",
      class: (sp.id === "name" ? "" : "zahl") + (sp.schmal ? " fz-schmal" : "") + (sp.id === "quote" ? " fz-quote" : ""),
      "aria-sort": aktiv ? (fz.sort.ab ? "descending" : "ascending") : null,
    }, h("button", {
      class: "fz-sort", type: "button", "data-feld": sp.id, title: `Sortieren nach ${sp.lang || "Name"}`,
      onclick: () => fzSortieren(sp.id),
    },
    sp.lang ? h("abbr", { title: sp.lang }, sp.text) : sp.text,
    h("span", { class: "fz-pfeil", "aria-hidden": "true" }, aktiv ? (fz.sort.ab ? "▼" : "▲") : "")));
  })));

  const zahl = (wert, art, extra) => h("td", { class: "zahl" + (wert > 0 && art ? " " + art : "") + (extra || "") }, wert || "–");
  const zellen = (summe, soll) => [
    zahl(summe.fehlstunden),
    zahl(summe.entschuldigt, null, " fz-schmal"),
    zahl(summe.offen, "offen"),
    zahl(summe.unentschuldigt, "unent"),
    h("td", { class: "zahl fz-quote" }, fzProzent(fzQuote(summe.fehlstunden, soll))),
    h("td", { class: "zahl" + (summe.verspaetungenUnentschuldigt ? " unent" : "") },
      summe.verspaetungen || "–",
      summe.verspaetungMinuten ? h("span", { class: "fz-unter fz-min-kurz" }, `${summe.verspaetungMinuten} Min.`) : null,
      summe.verspaetungenOffen ? h("span", { class: "fz-unter offen" }, `${summe.verspaetungenOffen} offen`) : null),
    zahl(summe.verspaetungMinuten, null, " fz-schmal"),
  ];

  // Klasse unter dem Namen nur, wenn die Liste mehrere Klassen mischt (z. B. Religion 05A–05D).
  const mehrereKlassen = new Set(d.schueler.map((x) => x.klasse)).size > 1;
  const sichtbar = fzSortiert(fz.nurOffen ? d.schueler.filter(fzHatOffene) : d.schueler);
  const leerText = n === 0 ? "Keine Schülerinnen und Schüler hinterlegt." : "Niemand hat offene Stunden.";
  el("fzListeZeilen").replaceChildren(...(sichtbar.length === 0
    ? [h("tr", null, h("td", { colspan: String(FZ_SPALTEN.length), class: "leer" }, leerText))]
    : sichtbar.map((s) => h("tr", null,
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
      ...zellen(s.summe, s.soll)))));

  // Gesamtzeile (immer über alle, auch wenn gefiltert)
  el("fzListeFuss").replaceChildren(...(n === 0 ? [] : [h("tr", null,
    h("th", { scope: "row" }, "Gesamt", h("span", { class: "fz-klasse" }, fz.nurOffen ? `alle ${n} Schüler` : `${n} Schüler`)),
    ...zellen(g, d.soll))]));
}

// ---------------------------------------------------------------------
//  Offene Fälle einer Klasse/Stufe (Phase 4.1, Entscheidung 43)
// ---------------------------------------------------------------------

function fzRenderOffen(d) {
  el("fzOWann").textContent = `Schuljahr bis heute · ${fzDatumLang(d.von)} – ${fzDatumLang(d.bis)}`;
  el("fzOTitel").textContent = `Offene Fälle · ${d.titel}`;
  const n = d.schueler.length;
  el("fzOInfo").textContent = n
    ? `${n} ${n === 1 ? "Schülerin bzw. Schüler" : "Schülerinnen und Schüler"} mit offenen Fehlstunden oder Verspätungen – die ältesten zuerst.`
    : "Keine offenen Fehlstunden oder Verspätungen.";
  const mehrereKlassen = new Set(d.schueler.map((x) => x.klasse)).size > 1;
  el("fzOListe").replaceChildren(...d.schueler.map((s) => {
    const was = [s.fehlstunden ? `${s.fehlstunden} ${s.fehlstunden === 1 ? "Fehlstunde" : "Fehlstunden"}` : "",
      s.verspaetungen ? `${s.verspaetungen} ${s.verspaetungen === 1 ? "Verspätung" : "Verspätungen"}` : ""]
      .filter(Boolean).join(" · ");
    const seit = s.tage <= 0 ? "offen seit heute"
      : `offen seit ${s.tage} ${s.tage === 1 ? "Tag" : "Tagen"} (${fzDatum(s.aelteste)})`;
    return h("li", { class: "fz-of-zeile" },
      h("button", {
        class: "k-zeile fz-of-person" + (s.tage >= 14 ? " alt" : ""), type: "button",
        onclick: () => fzOffenZumSchueler(s),
      },
      h("span", { class: "k-oben" },
        h("span", { class: "k-name" }, `${s.nachname}, ${s.vorname}`),
        mehrereKlassen && s.klasse ? h("span", { class: "k-rechts" }, s.klasse) : null),
      h("span", { class: "k-unten" }, `${was} offen`),
      h("span", { class: "fz-of-seit" }, seit)),
      h("button", {
        class: "textknopf klein fz-of-ent", type: "button", "aria-haspopup": "dialog",
        "aria-label": `Entschuldigung eintragen für ${s.vorname} ${s.nachname}`,
        onclick: () => fzEntschuldigungOeffnen(s, s.aelteste, s.neueste),
      }, "Entschuldigung eintragen"));
  }));
}

/** Aus den offenen Fällen ins Schülerdetail – Zeitraum so, dass die älteste offene Stunde dabei ist. */
function fzOffenZumSchueler(s) {
  if (fz.zr && s.aelteste < fz.zr.von && fz.zeitraeume) {
    const sj = fz.zeitraeume.zeitraeume.find((x) => x.id === "sj");
    if (sj) fz.zr = { id: sj.id, von: sj.von, bis: sj.bis };
  }
  fz.stapel.push({ art: "schueler", id: s.id });
  fzZeigen();
}

// ---------------------------------------------------------------------
//  Themen und Hausaufgaben eines Unterrichts (Phase 3.3)
// ---------------------------------------------------------------------

function fzRenderThemen(d) {
  el("fzTWann").textContent = fzZeitraumText(d.von, d.bis);
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
  el("fzSWann").textContent = fzZeitraumText(d.von, d.bis);
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
    // Phase 4.1: Fehlquote (nur Anzeige, keine Schwelle)
    d.soll > 0 ? karte(fzProzent(fzQuote(s.fehlstunden, d.soll)), "Fehlquote",
      `${s.fehlstunden} von ${d.soll} ${d.soll === 1 ? "Stunde" : "Stunden"}`) : null,
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
        ? h("button", {
            class: "hauptknopf klein", type: "button", "aria-haspopup": "dialog",
            onclick: () => {
              const offen = d.eintraege.filter((e) => e.bewertung === "Offen").map((e) => e.datum).sort();
              fzEntschuldigungOeffnen(d, offen[0], offen[offen.length - 1]);
            },
          }, "Entschuldigung eintragen")
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
        h("th", { scope: "col", class: "zahl fz-quote" }, h("abbr", { title: "Fehlquote" }, "Quote")),
        h("th", { scope: "col", class: "zahl" }, "Versp."))),
      h("tbody", null, d.faecher.map((f) => h("tr", null,
        h("th", { scope: "row" }, f.fach, h("span", { class: "fz-klasse" }, f.lehrer)),
        h("td", { class: "zahl" }, f.summe.fehlstunden || "–"),
        h("td", { class: "zahl" + (f.summe.offen ? " offen" : "") }, f.summe.offen || "–"),
        h("td", { class: "zahl" + (f.summe.unentschuldigt ? " unent" : "") }, f.summe.unentschuldigt || "–"),
        h("td", { class: "zahl fz-quote" }, fzProzent(fzQuote(f.summe.fehlstunden, f.soll))),
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

/** p = Schüler { id, vorname, nachname, klasse }; von/bis = Vorschlag (z. B. älteste/neueste offene Stunde). */
function fzEntschuldigungOeffnen(p, von, bis) {
  fz.entschFuer = p.id;
  el("feFuer").textContent = `Für ${p.vorname} ${p.nachname}${p.klasse ? ` (${p.klasse})` : ""}`;
  el("feVon").value = von || heuteIso();
  el("feBis").value = bis || von || heuteIso();
  el("feGrund").value = "Krankheit";
  fehlerIn("feFehler", "");
  dialogAuf(el("fzEntschDialog"));
  el("feVon").focus();
}

async function fzEntschuldigungSpeichern() {
  const id = fz.entschFuer;
  const von = el("feVon").value;
  const bis = el("feBis").value;
  if (!istIso(von) || !istIso(bis)) { fehlerIn("feFehler", "Bitte „von“ und „bis“ angeben."); return; }
  if (bis < von) { fehlerIn("feFehler", "„Bis“ liegt vor „von“."); return; }
  if (!id) { dialogZu(el("fzEntschDialog")); return; }
  const knopf = el("feSpeichern");
  knopf.disabled = true;
  fehlerIn("feFehler", "");
  try {
    const antwort = await senden("POST", `api/fehlzeiten/schueler/${id}/entschuldigung`,
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
  // Phase 4.1: Klasse/Stufe – Übersicht | Offene Fälle; Zeitraum; Filter „nur mit offenen"
  for (const b of el("fzKWahl").querySelectorAll("button")) {
    b.addEventListener("click", () => {
      if (fz.kAnsicht === b.dataset.k) return;
      fz.kAnsicht = b.dataset.k;
      fzZeigen();
    });
  }
  el("fzZrAnwenden").addEventListener("click", fzZrFreiAnwenden);
  for (const id of ["fzZrVon", "fzZrBis"])
    el(id).addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); fzZrFreiAnwenden(); } });
  el("fzNurOffen").addEventListener("change", (e) => {
    fz.nurOffen = e.target.checked;
    if (fz.daten) fzRenderListe(fz.daten);
  });
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
  el("fzEntschDialog").addEventListener("close", () => { fz.entschFuer = null; });
  // Phase 4.2: PDF
  el("fzPdfKnopf").addEventListener("click", fzPdfOeffnen);
  el("pdErstellen").addEventListener("click", fzPdfErstellen);
  el("pdAbbrechen").addEventListener("click", () => dialogZu(el("fzPdfDialog")));
  el("fzPdfDialog").addEventListener("close", () => { fz.pdfNr++; fz.pdfLaeuft = false; fz.pdf = null;
    el("pdErstellen").textContent = "PDF erstellen"; el("pdStatus").textContent = ""; });
  hintergrundSchliesst(el("fzPdfDialog"), () => { if (!fz.pdfLaeuft) dialogZu(el("fzPdfDialog")); });
  hintergrundSchliesst(el("fzEntschDialog"), () => dialogZu(el("fzEntschDialog")));
}
