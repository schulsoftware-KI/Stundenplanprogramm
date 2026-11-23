"use strict";
// =====================================================================
//  Stundenplan-PWA – zweiter Faktor (P6a, Entscheidungen 225–227)
//
//  - Kontomenü „Zweiter Faktor…“ (alle Personalkonten): Stand, Einrichten bzw. Neu einrichten (neues Handy),
//    neue Wiederherstellungscodes, Ausschalten (nur ohne Pflicht).
//  - Pflicht ohne Faktor (bzw. keine Wiederherstellungscodes mehr): nach dem Passwort zeigt die App nur
//    „Zweiten Faktor einrichten“ (#faktorPflicht) – der Server sperrt bis dahin alle übrigen Abfragen.
//  - Einrichten: Passwort (bei aktivem Faktor zusätzlich ein Code) → QR-Code und Schlüssel zum Abtippen → Code aus
//    der App → 10 Wiederherstellungscodes (nur jetzt sichtbar: kopieren, drucken). Danach sind alle anderen Geräte
//    abgemeldet; die Antwort bringt ein neues Token für dieses Gerät.
//  - QR-Code aus einem eigenen Erzeuger (Modell 2, Byte-Modus, Fehlerkorrektur M) als SVG ohne Inline-Stile – kein
//    CDN, kein Paket, die CSP bleibt streng. Geheimnis, Codes und Passwort liegen nur im Arbeitsspeicher und werden
//    beim Schließen bzw. Abmelden aus der Seite entfernt.
//
//  Wird nach jahresablauf.js und VOR hilfe.js/app.js geladen und nutzt deren Hilfen (h, el, state, senden,
//  HttpFehler, aktionsFehler, toast, dialogAuf, dialogZu, fehlerIn, …) erst zur Laufzeit. Einstieg zfInit() aus
//  start() in app.js. Alle Namen mit Präfix „zf…“ bzw. „qr…“.
// =====================================================================

// ---------------------------------------------------------------------
//  QR-Code (ISO/IEC 18004, Modell 2) – Byte-Modus, Fehlerkorrektur M, Version 1–40, beste von 8 Masken
// ---------------------------------------------------------------------

// Je Version (Index 1–40): Fehlerkorrektur-Codewörter je Block und Zahl der Blöcke für Stufe M.
const QR_ECC_M = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26,
  26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28];
const QR_BLOECKE_M = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
  17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49];
const QR_FORMAT_M = 0;   // Bits der Stufe M im Formatfeld

function qrRohModule(v) {
  let n = (16 * v + 128) * v + 64;
  if (v >= 2) {
    const ausr = Math.floor(v / 7) + 2;
    n -= (25 * ausr - 10) * ausr - 55;
    if (v >= 7) n -= 36;
  }
  return n;
}

const qrDatenCodewoerter = (v) => Math.floor(qrRohModule(v) / 8) - QR_ECC_M[v] * QR_BLOECKE_M[v];

function qrMal(x, y) {   // Multiplikation in GF(2^8) mit x^8 + x^4 + x^3 + x^2 + 1
  let z = 0;
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d);
    z ^= ((y >>> i) & 1) * x;
  }
  return z & 0xff;
}

function qrTeiler(grad) {
  const r = new Array(grad).fill(0);
  r[grad - 1] = 1;
  let wurzel = 1;
  for (let i = 0; i < grad; i++) {
    for (let j = 0; j < r.length; j++) {
      r[j] = qrMal(r[j], wurzel);
      if (j + 1 < r.length) r[j] ^= r[j + 1];
    }
    wurzel = qrMal(wurzel, 0x02);
  }
  return r;
}

function qrRest(daten, teiler) {
  const r = new Array(teiler.length).fill(0);
  for (const b of daten) {
    const f = b ^ r.shift();
    r.push(0);
    teiler.forEach((k, i) => { r[i] ^= qrMal(k, f); });
  }
  return r;
}

function qrUtf8(text) {
  if (typeof TextEncoder !== "undefined") return Array.from(new TextEncoder().encode(text));
  return Array.from(unescape(encodeURIComponent(text)), (c) => c.charCodeAt(0));
}

/** Modulmatrix zu einem Text: { groesse, dunkel: boolean[][] } (ohne Ruhezone). */
function qrMatrix(text) {
  const daten = qrUtf8(text);
  let v = 1;
  for (; v <= 40; v++) {
    const zaehlBits = v <= 9 ? 8 : 16;
    if (4 + zaehlBits + daten.length * 8 <= qrDatenCodewoerter(v) * 8) break;
  }
  if (v > 40) throw new Error("Text zu lang für einen QR-Code");

  // Bitfolge: Modus 0100 (Bytes), Länge, Daten, Ende, Auffüllen
  const bits = [];
  const anhaengen = (wert, n) => { for (let i = n - 1; i >= 0; i--) bits.push((wert >>> i) & 1); };
  anhaengen(4, 4);
  anhaengen(daten.length, v <= 9 ? 8 : 16);
  for (const b of daten) anhaengen(b, 8);
  const kapazitaet = qrDatenCodewoerter(v) * 8;
  anhaengen(0, Math.min(4, kapazitaet - bits.length));
  anhaengen(0, (8 - (bits.length % 8)) % 8);
  for (let fuell = 0xec; bits.length < kapazitaet; fuell ^= 0xec ^ 0x11) anhaengen(fuell, 8);
  const codewoerter = [];
  for (let i = 0; i < bits.length; i += 8) {
    let b = 0;
    for (let j = 0; j < 8; j++) b = (b << 1) | bits[i + j];
    codewoerter.push(b);
  }

  // Fehlerkorrektur je Block, dann verschränken
  const bloecke = QR_BLOECKE_M[v];
  const eccLaenge = QR_ECC_M[v];
  const roh = Math.floor(qrRohModule(v) / 8);
  const kurze = bloecke - (roh % bloecke);
  const kurzLaenge = Math.floor(roh / bloecke);
  const teiler = qrTeiler(eccLaenge);
  const liste = [];
  for (let i = 0, k = 0; i < bloecke; i++) {
    const dat = codewoerter.slice(k, k + kurzLaenge - eccLaenge + (i < kurze ? 0 : 1));
    k += dat.length;
    const ecc = qrRest(dat, teiler);
    if (i < kurze) dat.push(0);
    liste.push(dat.concat(ecc));
  }
  const folge = [];
  for (let i = 0; i < liste[0].length; i++)
    liste.forEach((blk, j) => { if (i !== kurzLaenge - eccLaenge || j >= kurze) folge.push(blk[i]); });

  // Matrix mit Funktionsmustern
  const n = v * 4 + 17;
  const m = Array.from({ length: n }, () => new Array(n).fill(false));
  const fest = Array.from({ length: n }, () => new Array(n).fill(false));
  const setze = (x, y, d) => { m[y][x] = d; fest[y][x] = true; };
  for (let i = 0; i < n; i++) { setze(6, i, i % 2 === 0); setze(i, 6, i % 2 === 0); }
  const finder = (cx, cy) => {
    for (let dy = -4; dy <= 4; dy++)
      for (let dx = -4; dx <= 4; dx++) {
        const d = Math.max(Math.abs(dx), Math.abs(dy));
        const x = cx + dx, y = cy + dy;
        if (x >= 0 && x < n && y >= 0 && y < n) setze(x, y, d !== 2 && d !== 4);
      }
  };
  finder(3, 3); finder(n - 4, 3); finder(3, n - 4);
  const pos = [];
  if (v > 1) {
    const anz = Math.floor(v / 7) + 2;
    const schritt = Math.floor((v * 8 + anz * 3 + 5) / (anz * 4 - 4)) * 2;
    for (let p = n - 7; pos.length < anz - 1; p -= schritt) pos.unshift(p);
    pos.unshift(6);
  }
  for (let i = 0; i < pos.length; i++)
    for (let j = 0; j < pos.length; j++) {
      if ((i === 0 && j === 0) || (i === 0 && j === pos.length - 1) || (i === pos.length - 1 && j === 0)) continue;
      for (let dy = -2; dy <= 2; dy++)
        for (let dx = -2; dx <= 2; dx++) setze(pos[i] + dx, pos[j] + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }
  const format = (maske) => {
    const d = (QR_FORMAT_M << 3) | maske;
    let r = d;
    for (let i = 0; i < 10; i++) r = (r << 1) ^ ((r >>> 9) * 0x537);
    const b = ((d << 10) | r) ^ 0x5412;
    const bit = (i) => ((b >>> i) & 1) !== 0;
    for (let i = 0; i <= 5; i++) setze(8, i, bit(i));
    setze(8, 7, bit(6)); setze(8, 8, bit(7)); setze(7, 8, bit(8));
    for (let i = 9; i < 15; i++) setze(14 - i, 8, bit(i));
    for (let i = 0; i < 8; i++) setze(n - 1 - i, 8, bit(i));
    for (let i = 8; i < 15; i++) setze(8, n - 15 + i, bit(i));
    setze(8, n - 8, true);
  };
  format(0);
  if (v >= 7) {
    let r = v;
    for (let i = 0; i < 12; i++) r = (r << 1) ^ ((r >>> 11) * 0x1f25);
    const b = (v << 12) | r;
    for (let i = 0; i < 18; i++) {
      const d = ((b >>> i) & 1) !== 0;
      const a = n - 11 + (i % 3), c = Math.floor(i / 3);
      setze(a, c, d); setze(c, a, d);
    }
  }

  // Daten im Zickzack von unten rechts
  let i = 0;
  for (let rechts = n - 1; rechts >= 1; rechts -= 2) {
    if (rechts === 6) rechts = 5;
    for (let senk = 0; senk < n; senk++)
      for (let j = 0; j < 2; j++) {
        const x = rechts - j;
        const hoch = ((rechts + 1) & 2) === 0;
        const y = hoch ? n - 1 - senk : senk;
        if (!fest[y][x] && i < folge.length * 8) {
          m[y][x] = ((folge[i >>> 3] >>> (7 - (i & 7))) & 1) !== 0;
          i++;
        }
      }
  }

  // Maske: die mit der kleinsten Strafe
  const maskieren = (k) => {
    for (let y = 0; y < n; y++)
      for (let x = 0; x < n; x++) {
        let u;
        switch (k) {
          case 0: u = (x + y) % 2 === 0; break;
          case 1: u = y % 2 === 0; break;
          case 2: u = x % 3 === 0; break;
          case 3: u = (x + y) % 3 === 0; break;
          case 4: u = (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0; break;
          case 5: u = ((x * y) % 2) + ((x * y) % 3) === 0; break;
          case 6: u = (((x * y) % 2) + ((x * y) % 3)) % 2 === 0; break;
          default: u = (((x + y) % 2) + ((x * y) % 3)) % 2 === 0;
        }
        if (u && !fest[y][x]) m[y][x] = !m[y][x];
      }
  };
  let beste = 0, besteStrafe = Infinity;
  for (let k = 0; k < 8; k++) {
    maskieren(k);
    format(k);
    const s = qrStrafe(m, n);
    if (s < besteStrafe) { besteStrafe = s; beste = k; }
    maskieren(k);   // zurück (XOR)
  }
  maskieren(beste);
  format(beste);
  return { groesse: n, dunkel: m };
}

/** Strafpunkte nach der Norm (Regeln 1–4) – zur Wahl der Maske. */
function qrStrafe(m, n) {
  let s = 0;
  const zelle = (zeile, x, y) => (zeile ? m[y][x] : m[x][y]);
  for (const zeile of [true, false]) {
    for (let a = 0; a < n; a++) {
      let lauf = 1;
      for (let b = 1; b <= n; b++) {
        if (b < n && zelle(zeile, b, a) === zelle(zeile, b - 1, a)) { lauf++; continue; }
        if (lauf >= 5) s += 3 + (lauf - 5);
        lauf = 1;
      }
      // Regel 3: 1011101 mit vier hellen Modulen davor oder danach (außerhalb zählt hell)
      for (let b = -4; b < n; b++) {
        const w = (k) => (k < 0 || k >= n ? false : zelle(zeile, k, a));
        const kern = w(b + 4) && !w(b + 5) && w(b + 6) && w(b + 7) && w(b + 8) && !w(b + 9) && w(b + 10);
        if (!kern) continue;
        const davor = !w(b) && !w(b + 1) && !w(b + 2) && !w(b + 3);
        const danach = !w(b + 11) && !w(b + 12) && !w(b + 13) && !w(b + 14);
        if (davor || danach) s += 40;
      }
    }
  }
  let dunkel = 0;
  for (let y = 0; y < n; y++)
    for (let x = 0; x < n; x++) {
      if (m[y][x]) dunkel++;
      if (x < n - 1 && y < n - 1 && m[y][x] === m[y][x + 1] && m[y][x] === m[y + 1][x] && m[y][x] === m[y + 1][x + 1]) s += 3;
    }
  const gesamt = n * n;
  s += (Math.ceil(Math.abs(dunkel * 20 - gesamt * 10) / gesamt) - 1) * 10;
  return s;
}

/** QR-Code als SVG (schwarz auf weiß, 4 Module Ruhezone) – ohne Inline-Stile. */
function qrSvg(text, beschriftung) {
  const { groesse: n, dunkel } = qrMatrix(text);
  const ns = "http://www.w3.org/2000/svg";
  const r = 4;
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", `0 0 ${n + 2 * r} ${n + 2 * r}`);
  svg.setAttribute("role", "img");
  svg.setAttribute("aria-label", beschriftung || "QR-Code");
  svg.setAttribute("shape-rendering", "crispEdges");
  const grund = document.createElementNS(ns, "rect");
  grund.setAttribute("width", String(n + 2 * r));
  grund.setAttribute("height", String(n + 2 * r));
  grund.setAttribute("fill", "#ffffff");
  svg.append(grund);
  let d = "";
  for (let y = 0; y < n; y++)
    for (let x = 0; x < n; x++)
      if (dunkel[y][x]) d += `M${x + r} ${y + r}h1v1h-1z`;
  const pfad = document.createElementNS(ns, "path");
  pfad.setAttribute("d", d);
  pfad.setAttribute("fill", "#000000");
  svg.append(pfad);
  return svg;
}

// ---------------------------------------------------------------------
//  Zustand
// ---------------------------------------------------------------------

const zf = {
  ort: "dialog",        // "dialog" (Kontomenü) | "pflicht" (nach dem Passwort nur die Einrichtung)
  modus: "einrichten",  // "einrichten" | "codes" | "aus"
  stand: null,          // GET api/auth/zweiter-faktor
  merker: null,         // versiegelter Merker der Einrichtung (enthält das Geheimnis – nur im Arbeitsspeicher)
  schluessel: "",       // Schlüssel zum Abtippen (nur bis zum Bestätigen)
  codes: null,          // Wiederherstellungscodes (nur bis „Fertig“)
  laeuft: false,
  ladeNr: 0,
};

let zfPwMerk = "";      // Passwort der Anmeldung bzw. neues Passwort – nur im Arbeitsspeicher, nur für die Pflicht-Einrichtung

const zfDarf = () => istPersonal();

function zfMenueAnpassen() {
  el("zfKnopf").hidden = !zfDarf();
}

// ---------------------------------------------------------------------
//  Öffnen, schließen, leeren
// ---------------------------------------------------------------------

function zfSchritt(name) {
  el("zfStand").hidden = name !== "stand";
  el("zfPruefen").hidden = name !== "pruefen";
  el("zfQr").hidden = name !== "qr";
  el("zfCodes").hidden = name !== "codes";
  el("zfAbmelden").hidden = zf.ort !== "pflicht" || name === "codes";
  el("zfPflichtText").hidden = name === "codes";   // „Fertig …“ steht dann im Assistenten
}

/** Geheimnis, Codes und Eingaben aus der Seite entfernen. */
function zfLeeren() {
  zf.ladeNr++;
  zf.merker = null;
  zf.schluessel = "";
  zf.codes = null;
  zf.laeuft = false;
  el("zfQrBild").replaceChildren();
  el("zfSchluessel").textContent = "";
  el("zfKonto").textContent = "";
  el("zfCodeListe").replaceChildren();
  for (const id of ["zfPasswort", "zfCode", "zfNeuCode"]) el(id).value = "";
  el("zfGesichert").checked = false;
  el("zfFertig").disabled = true;
  for (const id of ["zfPruefenFehler", "zfQrFehler", "zfStandFehler"]) fehlerIn(id, "");
}

/** true = darf geschlossen werden (keine ungesicherten Wiederherstellungscodes). */
function zfCodesVerwerfen() {
  if (!zf.codes || el("zfGesichert").checked) return true;
  return window.confirm("Die Wiederherstellungscodes werden nur jetzt angezeigt. Hast du sie kopiert oder gedruckt? " +
    "Ohne sie kommst du bei verlorenem Handy nur über den Admin wieder herein. Trotzdem schließen?");
}

function zfDialogOeffnen() {
  if (!zfDarf()) return;
  kontoSchliessen();
  zf.ort = "dialog";
  zfLeeren();
  el("zfDialogPlatz").append(el("zfAssistent"));
  zfSchritt("stand");
  el("zfStandText").textContent = "Lade …";
  dialogAuf(el("zfDialog"));
  zfStandLaden();
}

/** Pflicht: Nach dem Passwort nur noch „Zweiten Faktor einrichten“ (bzw. neu einrichten). */
function zeigeFaktorPflicht() {
  sitzungNr++;
  ladenAbbrechen();
  alleDialogeZu();
  verwaltungLeeren();
  klassenbuchLeeren();
  fehlzeitenLeeren();
  hausaufgabenLeeren();
  einstellungenLeeren();
  for (const id of ["inhalt", "anmeldung", "pflicht", "verwaltung", "klassenbuch", "fehlzeiten", "hausaufgaben",
    "einstellungen"])
    el(id).hidden = true;
  if (state.anmeldung && !state.anmeldung.faktorEinrichten) {
    state.anmeldung.faktorEinrichten = "einrichten";
    anmeldungSpeichern();
  }
  el("faktorPflicht").hidden = false;
  el("aktualisieren").hidden = true;
  el("kontoKnopf").hidden = true;
  el("titel").textContent = appName();
  document.title = `Zweiter Faktor – ${appName()}`;
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  zf.ort = "pflicht";
  zfLeeren();
  el("zfHeimat").append(el("zfAssistent"));
  const neu = state.anmeldung && state.anmeldung.faktorEinrichten === "neu";
  el("zfPflichtTitel").textContent = neu ? "Zweiten Faktor neu einrichten" : "Zweiten Faktor einrichten";
  el("zfPflichtText").textContent = neu
    ? "Du hast deinen letzten Wiederherstellungscode benutzt. Erzeuge neue Codes (mit dem Code aus der App) oder richte " +
      "den zweiten Faktor neu ein – erst danach geht es weiter."
    : "Für dein Konto schreibt die Schule einen zweiten Faktor vor: Nach dem Passwort fragt die App dann nach einem Code " +
      "aus einer Authenticator-App auf deinem Handy. Richte ihn jetzt ein – erst danach geht es weiter.";
  zfSchritt("stand");
  el("zfStandText").textContent = "Lade …";
  window.scrollTo(0, 0);
  zfStandLaden();
}

/** Nach der Einrichtung bzw. „Fertig“: Pflicht erfüllt → zum Plan; im Dialog zurück zum Stand. */
function zfFertig() {
  const codes = zf.codes;
  zf.codes = null;
  el("zfCodeListe").replaceChildren();
  if (zf.ort === "pflicht") {
    zfPwMerk = "";
    zfLeeren();
    el("faktorPflicht").hidden = true;
    appStarten();
    if (codes) toast("Zweiter Faktor eingerichtet. Ab jetzt fragt die App nach dem Passwort nach einem Code.");
    return;
  }
  zfLeeren();
  zfSchritt("stand");
  zfStandLaden();
}

// ---------------------------------------------------------------------
//  Stand
// ---------------------------------------------------------------------

async function zfStandLaden() {
  const nr = ++zf.ladeNr;
  fehlerIn("zfStandFehler", "");
  try {
    const d = await senden("GET", "api/auth/zweiter-faktor");
    if (nr !== zf.ladeNr) return;
    zf.stand = d;
    zfStandZeigen();
    // Pflicht ohne Faktor: gleich zum Passwort (bzw. bei bekanntem Passwort zum QR-Code)
    if (zf.ort === "pflicht" && !d.aktiv) zfPruefenZeigen("einrichten");
  } catch (f) {
    if (nr !== zf.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) { el("zfStandText").textContent = ""; fehlerIn("zfStandFehler", t); }
  }
}

function zfStandZeigen() {
  const d = zf.stand || {};
  const teile = [];
  if (d.aktiv) {
    teile.push(`Der zweite Faktor ist eingerichtet${d.seit ? ` (seit ${datumKurz(d.seit)})` : ""}.`);
    teile.push(d.restCodes === 1 ? "Noch 1 Wiederherstellungscode." : `Noch ${d.restCodes} Wiederherstellungscodes.`);
    if (d.pflicht) teile.push("Für dein Konto ist er Pflicht.");
  } else {
    teile.push("Noch kein zweiter Faktor eingerichtet.");
    teile.push(d.pflicht ? "Für dein Konto ist er Pflicht." : "Er ist freiwillig und schützt dein Konto, auch wenn jemand " +
      "dein Passwort kennt: Nach dem Passwort fragt die App nach einem Code aus einer Authenticator-App.");
  }
  el("zfStandText").textContent = teile.join(" ");
  el("zfEinrichtenKnopf").textContent = d.aktiv ? "Neu einrichten (neues Handy) …" : "Einrichten …";
  el("zfEinrichtenKnopf").className = d.aktiv ? "textknopf" : "hauptknopf";
  el("zfCodesKnopf").hidden = !d.aktiv;
  el("zfCodesKnopf").className = d.aktiv && d.restCodes === 0 ? "hauptknopf" : "textknopf";
  el("zfAusKnopf").hidden = !d.abschaltbar;
  zfSchritt("stand");
}

// ---------------------------------------------------------------------
//  Passwort (und Code) bestätigen
// ---------------------------------------------------------------------

const ZF_PRUEFEN_TEXT = {
  einrichten: "Bestätige zuerst dein Passwort.",
  neu: "Für ein neues Handy: Passwort und ein Code – aus der bisherigen App oder ein Wiederherstellungscode.",
  codes: "Neue Wiederherstellungscodes ersetzen alle bisherigen. Bestätige mit Passwort und einem Code.",
  aus: "Ausschalten: Danach fragt die App nach dem Passwort keinen Code mehr. Bestätige mit Passwort und einem Code.",
};

function zfCodeNoetig(modus) {
  const d = zf.stand || {};
  if (modus === "einrichten") return !!d.codeNoetig;
  return modus === "codes" || modus === "aus";
}

function zfPruefenZeigen(modus) {
  zf.modus = modus;
  const code = zfCodeNoetig(modus);
  const textArt = modus === "einrichten" && code ? "neu" : modus;
  el("zfPruefenText").textContent = ZF_PRUEFEN_TEXT[textArt];
  el("zfName").value = state.anmeldung ? state.anmeldung.benutzername : "";
  const pwBekannt = !!zfPwMerk && zf.ort === "pflicht";
  el("zfPasswortFeld").hidden = pwBekannt;
  el("zfPasswort").value = pwBekannt ? zfPwMerk : "";
  el("zfCodeFeld").hidden = !code;
  el("zfCode").value = "";
  el("zfWeiter").textContent = modus === "aus" ? "Ausschalten" : modus === "codes" ? "Neue Codes erzeugen" : "Weiter";
  el("zfWeiter").className = modus === "aus" ? "hauptknopf gefahr" : "hauptknopf";
  el("zfPruefenZurueck").hidden = zf.ort === "pflicht" && !(zf.stand && zf.stand.aktiv);
  fehlerIn("zfPruefenFehler", "");
  zfSchritt("pruefen");
  // Pflicht mit bekanntem Passwort und ohne Code: gleich weiter zum QR-Code
  if (pwBekannt && !code) { zfWeiter(); return; }
  const feld = pwBekannt ? el("zfCode") : el("zfPasswort");
  setTimeout(() => { try { feld.focus({ preventScroll: true }); } catch { /* egal */ } }, 0);
}

async function zfWeiter(e) {
  if (e) e.preventDefault();
  if (zf.laeuft) return;
  const passwort = el("zfPasswort").value;
  const code = el("zfCode").value.trim();
  const mitCode = !el("zfCodeFeld").hidden;
  if (!passwort) { el("zfPasswortFeld").hidden = false; fehlerIn("zfPruefenFehler", "Bitte dein Passwort eingeben."); el("zfPasswort").focus(); return; }
  if (mitCode && !code) { fehlerIn("zfPruefenFehler", "Bitte einen Code eingeben."); el("zfCode").focus(); return; }

  const knopf = el("zfWeiter");
  const text = knopf.textContent;
  zf.laeuft = true;
  knopf.disabled = true;
  knopf.textContent = "Prüfe …";
  fehlerIn("zfPruefenFehler", "");
  try {
    const body = { passwort, code: mitCode ? code : null };
    if (zf.modus === "einrichten") {
      const d = await senden("POST", "api/auth/zweiter-faktor/beginnen", body);
      zfQrZeigen(d);
    } else if (zf.modus === "codes") {
      const d = await senden("POST", "api/auth/zweiter-faktor/codes", body);
      zf.stand = d.stand || zf.stand;
      if (zf.ort === "pflicht" && state.anmeldung) { state.anmeldung.faktorEinrichten = null; anmeldungSpeichern(); }
      zfCodesZeigen(d.codes);
    } else {
      const d = await senden("POST", "api/auth/zweiter-faktor/ausschalten",
        { ...body, angemeldetBleiben: !!(state.anmeldung && state.anmeldung.dauerhaft) });
      anmeldungUebernehmen(d.anmeldung, !!(state.anmeldung && state.anmeldung.dauerhaft));
      toast("Zweiter Faktor ausgeschaltet. Auf anderen Geräten bist du jetzt abgemeldet.");
      el("zfPasswort").value = "";
      el("zfCode").value = "";
      zfStandLaden();
    }
    zfPwMerk = "";
  } catch (f) {
    if (f instanceof HttpFehler && f.status === 400 && f.daten && f.daten.feld) {
      const feld = f.daten.feld === "code" ? "zfCode" : "zfPasswort";
      if (feld === "zfPasswort") { zfPwMerk = ""; el("zfPasswortFeld").hidden = false; }
      fehlerIn("zfPruefenFehler", f.text);
      el(feld).focus();
      if (el(feld).select) el(feld).select();
    } else {
      const t = aktionsFehler(f);
      if (t) fehlerIn("zfPruefenFehler", t);
    }
  } finally {
    zf.laeuft = false;
    knopf.disabled = false;
    knopf.textContent = text;
  }
}

// ---------------------------------------------------------------------
//  QR-Code und Bestätigung
// ---------------------------------------------------------------------

function zfQrZeigen(d) {
  zf.merker = d.merker;
  zf.schluessel = d.schluessel || "";
  el("zfPasswort").value = "";
  el("zfCode").value = "";
  let bild;
  try {
    bild = qrSvg(d.uri, `QR-Code für die Authenticator-App (${d.aussteller}: ${d.konto})`);
  } catch {
    bild = h("p", { class: "fehler" }, "Der QR-Code ließ sich nicht erzeugen – bitte den Schlüssel abtippen.");
  }
  el("zfQrBild").replaceChildren(bild);
  el("zfSchluessel").textContent = zf.schluessel;
  el("zfKonto").textContent = `Konto in der App: ${d.aussteller} (${d.konto}) · zeitbasiert, 6 Ziffern, 30 Sekunden.`;
  el("zfNeuCode").value = "";
  el("zfSchluesselKopieren").textContent = "Schlüssel kopieren";
  fehlerIn("zfQrFehler", "");
  zfSchritt("qr");
  setTimeout(() => { try { el("zfNeuCode").focus({ preventScroll: true }); } catch { /* egal */ } }, 0);
}

async function zfBestaetigen(e) {
  if (e) e.preventDefault();
  if (zf.laeuft || !zf.merker) return;
  const code = el("zfNeuCode").value.replace(/\s/g, "");
  if (!/^\d{6}$/.test(code)) { fehlerIn("zfQrFehler", "Bitte den 6-stelligen Code aus der App eingeben."); el("zfNeuCode").focus(); return; }
  const knopf = el("zfBestaetigen");
  zf.laeuft = true;
  knopf.disabled = true;
  knopf.textContent = "Prüfe …";
  fehlerIn("zfQrFehler", "");
  try {
    const dauerhaft = !!(state.anmeldung && state.anmeldung.dauerhaft);
    const d = await senden("POST", "api/auth/zweiter-faktor/einrichten", { merker: zf.merker, code, angemeldetBleiben: dauerhaft });
    anmeldungUebernehmen(d.anmeldung, dauerhaft);
    zf.merker = null;
    zf.schluessel = "";
    el("zfQrBild").replaceChildren();
    el("zfSchluessel").textContent = "";
    zfCodesZeigen(d.codes);
  } catch (f) {
    if (f instanceof HttpFehler && f.status === 400 && f.daten && f.daten.neuBeginnen) {
      zfLeeren();
      zfPruefenZeigen("einrichten");
      fehlerIn("zfPruefenFehler", f.text);
    } else {
      const t = aktionsFehler(f);
      if (t) fehlerIn("zfQrFehler", t);
      el("zfNeuCode").select();
    }
  } finally {
    zf.laeuft = false;
    knopf.disabled = false;
    knopf.textContent = "Bestätigen";
  }
}

// ---------------------------------------------------------------------
//  Wiederherstellungscodes (nur jetzt sichtbar)
// ---------------------------------------------------------------------

function zfCodesZeigen(codes) {
  zf.codes = codes || [];
  el("zfCodeListe").replaceChildren(...zf.codes.map((c) => h("li", null, c)));
  el("zfGesichert").checked = false;
  el("zfFertig").disabled = true;
  el("zfCodesKopieren").textContent = "Kopieren";
  zfSchritt("codes");
}

function zfCodesText() {
  const name = state.anmeldung ? state.anmeldung.benutzername : "";
  return [`Wiederherstellungscodes ${appName()} – ${name} (${datumKurz(new Date().toISOString())})`,
    "Jeder Code gilt einmal. Sicher aufbewahren, nicht auf dem Handy mit der Authenticator-App.", "",
    ...(zf.codes || [])].join("\n");
}

function zfCodesDrucken() {
  if (!zf.codes || !zf.codes.length) return;
  const name = state.anmeldung ? state.anmeldung.benutzername : "";
  el("druck").replaceChildren(h("article", { class: "zettel zf-zettel" },
    h("p", { class: "z-titel" }, `Wiederherstellungscodes – ${appName()}`),
    h("p", { class: "z-name" }, name, h("span", { class: "z-gruppe" }, ` · erstellt am ${datumKurz(new Date().toISOString())}`)),
    h("ol", { class: "zf-codeliste" }, zf.codes.map((c) => h("li", null, c))),
    h("p", { class: "z-hinweis" }, "Jeder Code gilt einmal – für die Anmeldung, wenn das Handy mit der Authenticator-App " +
      "fehlt. Sicher aufbewahren (nicht beim Handy). Neue Codes: Kontomenü → „Zweiter Faktor…“.")));
  document.body.classList.add("druckt");
  window.print();
}

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function zfInit() {
  el("zfKnopf").addEventListener("click", zfDialogOeffnen);
  el("zfEinrichtenKnopf").addEventListener("click", () => zfPruefenZeigen("einrichten"));
  el("zfCodesKnopf").addEventListener("click", () => zfPruefenZeigen("codes"));
  el("zfAusKnopf").addEventListener("click", () => zfPruefenZeigen("aus"));
  el("zfPruefen").addEventListener("submit", zfWeiter);
  el("zfPruefenZurueck").addEventListener("click", () => {
    fehlerIn("zfPruefenFehler", "");
    el("zfPasswort").value = "";
    el("zfCode").value = "";
    zfStandZeigen();
  });
  el("zfQr").addEventListener("submit", zfBestaetigen);
  el("zfQrZurueck").addEventListener("click", () => {
    zfLeeren();
    if (zf.ort === "pflicht" && !(zf.stand && zf.stand.aktiv)) zfPruefenZeigen("einrichten");
    else zfStandZeigen();
  });
  el("zfNeuCode").addEventListener("input", () => {
    const v = el("zfNeuCode").value.replace(/\s/g, "");
    if (/^\d{6}$/.test(v) && !zf.laeuft) zfBestaetigen();
  });
  el("zfSchluesselKopieren").addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(zf.schluessel.replace(/\s/g, ""));
      el("zfSchluesselKopieren").textContent = "Kopiert";
    } catch {
      el("zfSchluesselKopieren").textContent = "Kopieren nicht möglich – bitte abtippen";
    }
  });
  el("zfCodesKopieren").addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(zfCodesText());
      el("zfCodesKopieren").textContent = "Kopiert";
    } catch {
      el("zfCodesKopieren").textContent = "Kopieren nicht möglich – bitte drucken oder abschreiben";
    }
  });
  el("zfCodesDrucken").addEventListener("click", zfCodesDrucken);
  el("zfGesichert").addEventListener("change", () => { el("zfFertig").disabled = !el("zfGesichert").checked; });
  el("zfFertig").addEventListener("click", zfFertig);
  el("zfAbmelden").addEventListener("click", () => { zfPwMerk = ""; zfLeeren(); abmelden(); });

  // Dialog: Schließen nur ohne ungesicherte Codes; danach wandert der Assistent zurück und alles wird geleert.
  el("zfZu").addEventListener("click", () => { if (zfCodesVerwerfen()) dialogZu(el("zfDialog")); });
  el("zfDialog").addEventListener("cancel", (ev) => { if (!zfCodesVerwerfen()) ev.preventDefault(); });
  el("zfDialog").addEventListener("close", () => {
    zfLeeren();
    el("zfHeimat").append(el("zfAssistent"));
  });
  hintergrundSchliesst(el("zfDialog"), () => { if (zfCodesVerwerfen()) dialogZu(el("zfDialog")); });
}
