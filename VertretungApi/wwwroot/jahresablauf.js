"use strict";
// =====================================================================
//  Stundenplan-PWA – Jahresablauf (P4a, 5.3a; Entscheidungen 205, 206)
//
//  Kontomenü „Jahresablauf“ (nur Admin): Karten mit Ampel – grün erledigt, gelb zu tun, rot überfällig,
//  grau nur ein Hinweis bzw. noch nicht dran. P4a: Karte „Halbjahreswechsel“ (ab 4 Wochen vor „2. Halbjahr
//  ab“) mit Grundplan, SchILD-Import Abschnitt 2, offenen und nicht erfassten Stunden, Rückweg nach SchILD und
//  den Sek-I-Gruppen. Hier geschieht nichts von selbst; die Knöpfe springen in den passenden Bereich
//  (Klassenbuch, Konten → SchILD/Downloads, Gruppenlisten). P5 ergänzt Schuljahreswechsel und Archiv.
//
//  P4b (210–213): Karte „Rückweg nach SchILD“ und Dialog „Fehlstunden für SchILD bereitstellen“ (rwDialog): Zeitraum,
//  Zahlen (übertragen / nicht übertragen, nicht erfasst, frühere Bereitstellungen) und Abholcode für den Desktop. Der
//  Code steht nur in diesem Dialog (nicht gespeichert); beim Schließen bleibt er gültig, bis er abläuft oder abgeholt ist.
//
//  P5a (215–217, 239, 242): Karte „Schuljahreswechsel“ mit Aktionen (Knöpfe in den Punkten): „Schuljahr … abschließen…“,
//  „Wieder öffnen…“ (mit Begründung) und „Schuljahr … vorbereiten…“. Jede Aktion fragt zuerst die API (409 mit Vorprüfung
//  und Änderungen) und zeigt das im Dialog jaDialog; erst „bestätigen“ führt sie aus. Danach kommt die neue Übersicht mit.
//
//  P5b-2 (219, 240, 249–251): Punkt „Zuständigkeiten“ mit Knopf „Zuständigkeiten für … übernehmen…“ → Dialog jzDialog: eine
//  Zeile je Konto, das nur für das Vorjahr zuständig ist (laut SchILD bzw. Folgeklasse vorausgewählt, „entfällt“ nicht,
//  Hinweise ohne Häkchen). „Übernehmen“ schickt die angehakten Schlüssel; die API übernimmt nur, was sie noch genauso
//  vorschlägt. Ohne Namen von Schülern.
//
//  Keine Eingaben – beim Verlassen geht nichts verloren. Wird nach einstellungen.js und VOR hilfe.js/app.js
//  geladen und nutzt deren Hilfen (h, el, state, senden, aktionsFehler, kurzDatum, appName, …) erst zur Laufzeit.
//  Einstieg jahresablaufInit() aus start() in app.js. Alle Namen mit Präfix „ja…“ bzw. „jahresablauf…“.
// =====================================================================

const ja = {
  ladeNr: 0,
  daten: null,            // Antwort von GET api/jahresablauf
};

const JA_AMPEL = { gruen: "erledigt", gelb: "zu tun", rot: "überfällig", grau: "Hinweis" };

// P4b: Dialog „Fehlstunden für SchILD bereitstellen“
const jaRw = {
  nr: 0,
  daten: null,            // Antwort von GET api/schild/rueckweg
  laeuft: false,
  timer: 0,
  ohneNeuladen: false,    // Schließen beim Verlassen des Bereichs: danach nicht neu laden
};

const JA_RW_ART = { SchildRueckweg: "abgeholt", SchildRueckwegDatei: "Datei", SchildRueckwegProbe: "Probedatei" };

// P5a: Dialog der Aktionen (abschließen, wieder öffnen, vorbereiten)
const jaAk = {
  aktion: null,           // { id, text, schuljahr, gefahr }
  laeuft: false,
};

const JA_AKTION_URL = { abschliessen: "api/jahresablauf/abschliessen", oeffnen: "api/jahresablauf/oeffnen",
  vorbereiten: "api/jahresablauf/neues-schuljahr" };
const JA_BEGRUENDUNG_MIN = 10;

// P5b-2: Dialog „Zuständigkeiten übernehmen“
const jaZk = {
  nr: 0,
  daten: null,            // Antwort von GET api/jahresablauf/zustaendigkeiten
  laeuft: false,
};

const JA_ZK_ART = { schild: "laut SchILD", folge: "Folgeklasse", entfaellt: "entfällt", hinweis: "Hinweis" };

const jaDarf = () => !!state.anmeldung && state.anmeldung.rolle === "Admin";
const jahresablaufOffen = () => !el("jahresablauf").hidden;

function jahresablaufMenueAnpassen() {
  el("jaKnopf").hidden = !jaDarf();
}

/** Alles aus der Ansicht entfernen (Verlassen, Abmelden). */
function jahresablaufLeeren() {
  ja.ladeNr++;
  ja.daten = null;
  if (el("rwDialog").open) { jaRw.ohneNeuladen = true; jaRwZu(); }
  if (el("jaDialog").open) jaAkZu();
  if (el("jzDialog").open) jaZkZu();
  el("jaKopf").replaceChildren();
  el("jaKarten").replaceChildren();
  el("jaInfo").textContent = "";
}

/** Bereich schließen, wenn er offen ist (es gibt nichts zu speichern). */
function jahresablaufZu() {
  if (!jahresablaufOffen()) return;
  jahresablaufLeeren();
  el("jahresablauf").hidden = true;
}

function jahresablaufOeffnen() {
  if (!jaDarf()) return;
  kontoSchliessen();
  if (!einstellungenSchliessen()) return;   // schließt auch einen offenen Jahresablauf
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
  el("jahresablauf").hidden = false;
  el("aktualisieren").hidden = false;
  el("titel").textContent = "Jahresablauf";
  document.title = `Jahresablauf – ${appName()}`;
  el("stand").textContent = "";
  el("grundplan").textContent = "";
  window.scrollTo(0, 0);
  jaLaden();
}

/** „Zum Plan“. */
function jahresablaufVerlassen() {
  jahresablaufZu();
  el("inhalt").hidden = false;
  render();
  if (state.plan) ladeWoche({ still: true });
  else ladeWoche();
}

// ---------------------------------------------------------------------
//  Laden und Darstellen
// ---------------------------------------------------------------------

async function jaLaden() {
  const nr = ++ja.ladeNr;
  el("jaInfo").textContent = "Lade … (die Zahlen zum 1. Halbjahr brauchen einen Moment)";
  try {
    const d = await senden("GET", "api/jahresablauf");
    if (nr !== ja.ladeNr) return;
    el("jaInfo").textContent = "";
    jaRender(d);
  } catch (f) {
    if (nr !== ja.ladeNr) return;
    const t = aktionsFehler(f);
    if (t !== null) el("jaInfo").textContent = t;
  }
}

function jaRender(d) {
  ja.daten = d;
  const teile = [`Schuljahr ${d.schuljahr}`];
  if (d.kalenderStart && d.kalenderEnde) teile.push(`${kurzDatum(d.kalenderStart)} – ${kurzDatum(d.kalenderEnde)}`);
  teile.push(`heute ${kurzDatum(d.heute)}` + (d.testdatum ? " (Testdatum)" : ""));
  el("jaKopf").replaceChildren(h("p", { class: "ja-stand" }, teile.join(" · ")));
  el("jaKarten").replaceChildren(...(d.karten || []).map((k) => jaKarte(k, d)));
}

/** Ampel als Punkt und Wort – nie nur über die Farbe. */
function jaAmpel(ampel, text) {
  const a = JA_AMPEL[ampel] ? ampel : "grau";
  return h("span", { class: `ja-ampel a-${a}` }, h("span", { class: "ja-punktfarbe", "aria-hidden": "true" }),
    text || JA_AMPEL[a]);
}

function jaKarte(k, d) {
  let marke;
  if (k.aktiv) marke = jaAmpel(k.ampel);
  else marke = jaAmpel("grau", k.sichtbarAb && d.heute < k.sichtbarAb ? "später" : "Hinweis");
  const punkte = k.punkte || [];
  return h("section", { class: `karte ja-karte a-${k.aktiv ? k.ampel : "grau"}`, "aria-labelledby": `jaT_${k.id}` },
    h("div", { class: "ja-kartenkopf" },
      h("h2", { class: "karte-titel", id: `jaT_${k.id}` }, k.titel),
      marke),
    h("p", { class: "ja-text" }, k.text),
    punkte.length ? h("ol", { class: "ja-punkte" }, punkte.map(jaPunkt)) : null);
}

function jaPunkt(p) {
  const liste = p.liste && p.liste.length
    ? h("ul", { class: "ja-liste" },
      p.liste.map((x) => h("li", null, x)),
      p.weitere ? h("li", { class: "ja-weitere" }, `… und ${p.weitere} weitere`) : null)
    : null;
  const ziele = (p.ziele || []).map((z) => h("button", {
    type: "button", class: "textknopf klein ja-ziel", onclick: () => jaZiel(z.id),
  }, `${z.text} →`));
  // P5a: Aktionen mit Rückfrage
  const aktionen = (p.aktionen || []).filter((a) => JA_AKTION_URL[a.id] || a.id === "zustaendigkeiten").map((a) => h("button", {
    type: "button", class: `hauptknopf klein ja-aktion${a.gefahr ? " gefahr" : ""}`, "aria-haspopup": "dialog",
    onclick: () => (a.id === "zustaendigkeiten" ? jaZkOeffnen(a) : jaAktion(a)),
  }, a.text));
  return h("li", { class: `ja-punkt a-${JA_AMPEL[p.ampel] ? p.ampel : "grau"}` },
    h("div", { class: "ja-punkt-kopf" }, h("strong", { class: "ja-punkt-titel" }, p.titel), jaAmpel(p.ampel)),
    h("p", { class: "ja-punkt-text" }, p.text),
    liste,
    ziele.length ? h("div", { class: "ja-ziele" }, ziele) : null,
    aktionen.length ? h("div", { class: "ja-aktionen" }, aktionen) : null);
}

/** Sprung in den passenden Bereich (dort gelten die üblichen Rechte). */
function jaZiel(id) {
  if (id === "nichtErfasst") { fehlzeitenOeffnen("ne"); return; }
  if (id === "offeneFaelle") { fehlzeitenOeffnen("offen"); return; }
  if (id === "schildImporte") { verw.reiter = "schild"; verw.schildTeil = "importe"; zeigeVerwaltung(); return; }
  if (id === "downloads") { verw.reiter = "downloads"; zeigeVerwaltung(); return; }
  if (id === "gruppen") { el("kbGruppenKnopf").click(); return; }
  if (id === "rueckweg") { jaRwOeffnen(); return; }
  if (id === "konten") { verw.reiter = "konten"; zeigeVerwaltung(); return; }   // P5a
  if (id === "einstellungen") einstellungenOeffnen();
}

// ---------------------------------------------------------------------
//  P5a: Aktionen der Karte „Schuljahreswechsel“ (Rückfrage im Dialog)
// ---------------------------------------------------------------------

function jaAkListe(id, eintraege) {
  const box = el(id);
  box.replaceChildren(...(eintraege || []).map((t) => h("li", null, t)));
  return (eintraege || []).length > 0;
}

/** Dialog füllen: r = Rückfrage der API ({ titel, warnungen, aenderungen, knopf }) bzw. für „Wieder öffnen“ selbst gebaut. */
function jaAkZeigen(a, r, mitGrund) {
  jaAk.aktion = a;
  el("jaDTitel").textContent = r.titel;
  el("jaDText").textContent = r.text || "";
  el("jaDText").hidden = !r.text;
  el("jaDWarnBox").hidden = !jaAkListe("jaDWarnungen", r.warnungen);
  el("jaDAendBox").hidden = !jaAkListe("jaDAenderungen", r.aenderungen);
  el("jaDGrundBox").hidden = !mitGrund;
  el("jaDGrund").value = "";
  jaAkZeichen();
  el("jaDOk").textContent = r.knopf;
  el("jaDOk").className = a.gefahr ? "hauptknopf gefahr" : "hauptknopf";
  el("jaDOk").disabled = false;
  fehlerIn("jaDFehler", "");
  dialogAuf(el("jaDialog"));
  setTimeout(() => { try { (mitGrund ? el("jaDGrund") : el("jaDAbbrechen")).focus(); } catch { /* egal */ } }, 0);
}

function jaAkZu() {
  jaAk.aktion = null;
  el("jaDGrund").value = "";
  dialogZu(el("jaDialog"));
}

function jaAkZeichen() {
  const n = el("jaDGrund").value.trim().length;
  el("jaDZeichen").textContent = n < JA_BEGRUENDUNG_MIN
    ? `Mindestens ${JA_BEGRUENDUNG_MIN} Zeichen (noch ${JA_BEGRUENDUNG_MIN - n}).` : `${n} von 300 Zeichen.`;
}

/** Knopf in einer Karte: zuerst die Rückfrage der API holen (bzw. bei „Wieder öffnen“ gleich die Begründung erfragen). */
async function jaAktion(a) {
  if (!jaDarf() || jaAk.laeuft) return;
  if (a.id === "oeffnen") {
    jaAkZeigen(a, {
      titel: `Schuljahr ${a.schuljahr} wieder öffnen?`,
      text: "Danach sehen Lehrkräfte, Klassenleitung, Stufenkoordination und Schüler das Schuljahr wieder und können " +
        "nachtragen; Vertretungen, Grundpläne und SchILD-Importe gehen wieder. Die Begründung steht im Protokoll der " +
        "Einstellungen. Nach den Nachträgen das Schuljahr erneut abschließen.",
      knopf: `Schuljahr ${a.schuljahr} wieder öffnen`,
    }, true);
    return;
  }
  jaAk.laeuft = true;
  el("jaInfo").textContent = "Prüfe …";
  try {
    const d = await senden("POST", JA_AKTION_URL[a.id], { schuljahr: a.schuljahr, bestaetigt: false });
    // 200 ohne Rückfrage: es gab nichts zu tun
    el("jaInfo").textContent = "";
    if (d && d.meldung) toast(d.meldung);
    if (d && d.stand) jaRender(d.stand);
  } catch (f) {
    el("jaInfo").textContent = "";
    if (f instanceof HttpFehler && f.status === 409 && f.daten && f.daten.bestaetigen) {
      jaAkZeigen(a, f.daten, false);
    } else {
      const t = aktionsFehler(f);
      if (t !== null) el("jaInfo").textContent = t;
    }
  } finally {
    jaAk.laeuft = false;
  }
}

/** „bestätigen“ im Dialog. */
async function jaAkAusfuehren() {
  const a = jaAk.aktion;
  if (!a || jaAk.laeuft) return;
  let body;
  if (a.id === "oeffnen") {
    const grund = el("jaDGrund").value.trim();
    if (grund.length < JA_BEGRUENDUNG_MIN) {
      fehlerIn("jaDFehler", `Bitte begründen, warum das Schuljahr wieder geöffnet wird (mindestens ${JA_BEGRUENDUNG_MIN} Zeichen).`);
      el("jaDGrund").focus();
      return;
    }
    body = { schuljahr: a.schuljahr, begruendung: grund };
  } else {
    body = { schuljahr: a.schuljahr, bestaetigt: true };
  }
  jaAk.laeuft = true;
  el("jaDOk").disabled = true;
  fehlerIn("jaDFehler", "");
  try {
    const d = await senden("POST", JA_AKTION_URL[a.id], body);
    jaAkZu();
    if (d && d.meldung) toast(d.meldung);
    if (d && d.stand && jahresablaufOffen()) jaRender(d.stand);
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("jaDFehler", t);
  } finally {
    jaAk.laeuft = false;
    el("jaDOk").disabled = false;
  }
}

// ---------------------------------------------------------------------
//  P5b-2: Zuständigkeiten ins neue Schuljahr (Liste mit Häkchen)
// ---------------------------------------------------------------------

function jaZkOeffnen(a) {
  if (!jaDarf() || jaZk.laeuft) return;
  jaZk.daten = null;
  el("jzTitel").textContent = `Zuständigkeiten für ${a.schuljahr} übernehmen`;
  el("jzInfo").textContent = "Lade die Vorschläge …";
  el("jzListe").replaceChildren();
  el("jzAuswahl").hidden = true;
  el("jzOk").disabled = true;
  el("jzOk").textContent = "Übernehmen";
  fehlerIn("jzFehler", "");
  dialogAuf(el("jzDialog"));
  setTimeout(() => { try { el("jzAbbrechen").focus(); } catch { /* egal */ } }, 0);
  jaZkLaden();
}

function jaZkZu() {
  jaZk.nr++;
  jaZk.daten = null;
  dialogZu(el("jzDialog"));
}

async function jaZkLaden() {
  const nr = ++jaZk.nr;
  try {
    const d = await senden("GET", "api/jahresablauf/zustaendigkeiten");
    if (nr !== jaZk.nr) return;
    jaZkZeigen(d);
  } catch (f) {
    if (nr !== jaZk.nr) return;
    el("jzInfo").textContent = "";
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("jzFehler", t);
  }
}

function jaZkZeigen(d) {
  jaZk.daten = d;
  el("jzTitel").textContent = `Zuständigkeiten für ${d.schuljahr} übernehmen`;
  const zeilen = d.zeilen || [];
  if (!d.moeglich) {
    el("jzInfo").textContent = d.grund || "Im Moment nicht möglich.";
    el("jzListe").replaceChildren();
    el("jzAuswahl").hidden = true;
    jaZkZaehlen();
    return;
  }
  const teile = [`Konten, die nur für ${d.vorjahr} zuständig sind: ${zeilen.length}`,
    `Klassen ${d.schuljahr}: ${d.klassenNeu}` + (d.klassenMitLeitung ? `, davon ${d.klassenMitLeitung} mit Klassenleitung laut SchILD` :
      " (noch keine Klassenleitung laut SchILD)")];
  if (d.uebernommenAm) teile.push(`zuletzt übernommen ${datumZeit(d.uebernommenAm)}` + (d.uebernommenVon ? ` von ${d.uebernommenVon}` : ""));
  el("jzInfo").textContent = zeilen.length === 0
    ? `Kein Konto ist mehr nur für ${d.vorjahr} zuständig – es gibt nichts zu übernehmen.`
    : teile.join(" · ") + ". Ohne Häkchen bleibt die Zuständigkeit bei " + d.vorjahr +
      " (z. B. für Nachträge). Angehakte Konten werden auf ihren Geräten abgemeldet.";
  el("jzListe").replaceChildren(...zeilen.map(jaZkZeile));
  el("jzAuswahl").hidden = !zeilen.some((z) => z.schluessel);
  jaZkZaehlen();
}

function jaZkZeile(z) {
  const id = `jzK_${z.kontoId}`;
  const box = z.schluessel
    ? h("input", { type: "checkbox", id, class: "jz-haken", "data-schluessel": z.schluessel, checked: z.vorausgewaehlt,
      onchange: jaZkZaehlen })
    : h("span", { class: "jz-ohne", "aria-hidden": "true" });
  return h("li", { class: `jz-zeile jz-${z.art}` },
    box,
    h("label", { class: "jz-inhalt", for: z.schluessel ? id : null },
      h("span", { class: "jz-kopf" },
        h("strong", null, z.konto),
        ` · ${z.rolle}` + (z.kuerzel ? ` (${z.kuerzel})` : ""),
        h("span", { class: `marke jz-art jz-art-${z.art}` },
          z.art === "folge" && z.rolle === "Stufenkoordination" ? "Folgestufe" : JA_ZK_ART[z.art] || z.art),
        z.gesperrt ? h("span", { class: "marke gesperrt" }, "gesperrt") : null),
      h("span", { class: "jz-text" }, z.text),
      h("span", { class: "jz-aenderung" }, "Änderung: " + z.aenderung)));
}

function jaZkHaken() {
  return Array.from(el("jzListe").querySelectorAll("input.jz-haken"));
}

function jaZkZaehlen() {
  const haken = jaZkHaken();
  const n = haken.filter((x) => x.checked).length;
  el("jzOk").textContent = n ? `Übernehmen (${n})` : "Übernehmen";
  el("jzOk").disabled = jaZk.laeuft || n === 0;
}

/** „Vorschläge anhaken“: wie beim Öffnen (laut SchILD und Folgeklasse, ohne gesperrte); „Keine“: alle aus. */
function jaZkAuswahl(alle) {
  const d = jaZk.daten;
  if (!d) return;
  const vor = new Set((d.zeilen || []).filter((z) => z.vorausgewaehlt).map((z) => z.schluessel));
  for (const x of jaZkHaken()) x.checked = alle && vor.has(x.dataset.schluessel);
  jaZkZaehlen();
}

async function jaZkUebernehmen() {
  const d = jaZk.daten;
  if (!d || jaZk.laeuft) return;
  const schluessel = jaZkHaken().filter((x) => x.checked).map((x) => x.dataset.schluessel);
  if (schluessel.length === 0) return;
  jaZk.laeuft = true;
  el("jzOk").disabled = true;
  fehlerIn("jzFehler", "");
  try {
    const r = await senden("POST", "api/jahresablauf/zustaendigkeiten", { schuljahr: d.schuljahr, schluessel });
    jaZk.laeuft = false;
    jaZkZu();
    if (r && r.meldung) toast(r.meldung);
    if (r && r.stand && jahresablaufOffen()) jaRender(r.stand);
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("jzFehler", t);
  } finally {
    jaZk.laeuft = false;
    if (el("jzDialog").open) jaZkZaehlen();
  }
}

// ---------------------------------------------------------------------
//  P4b: Fehlstunden für SchILD bereitstellen (Abholcode)
// ---------------------------------------------------------------------

const jaUhr = (t) => new Date(t).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });

function jaRwOeffnen() {
  if (!jaDarf()) return;
  jaRw.daten = null;
  el("rwZeitraeume").replaceChildren();
  el("rwZahlen").replaceChildren();
  el("rwWarnung").replaceChildren();
  el("rwWarnung").hidden = true;
  el("rwCodeBox").hidden = true;
  el("rwCode").textContent = "";
  el("rwCodeInfo").textContent = "";
  el("rwAktiv").hidden = true;
  el("rwVerwerfen").hidden = true;
  el("rwEigen").hidden = true;
  el("rwErzeugen").disabled = true;
  el("rwErzeugen").textContent = "Abholcode erzeugen";
  fehlerIn("rwFehler", "");
  dialogAuf(el("rwDialog"));
  jaRwLaden(null);
}

/** Dialog schließen und den angezeigten Code vergessen (er bleibt in der API gültig, bis er abläuft). */
function jaRwZu() {
  jaRw.nr++;
  clearTimeout(jaRw.timer);
  el("rwCode").textContent = "";
  dialogZu(el("rwDialog"));
}

/** Gewählter Zeitraum {von, bis} (ISO) bzw. null, solange „anderer Zeitraum“ unvollständig ist. */
function jaRwZeitraum() {
  const d = jaRw.daten;
  const r = document.querySelector('input[name="rwZr"]:checked');
  if (!d || !r) return null;
  if (r.value === "eigen") {
    const von = el("rwVon").value, bis = el("rwBis").value;
    return von && bis ? { von, bis } : null;
  }
  const z = (d.zeitraeume || []).find((x) => x.id === r.value);
  return z ? { von: z.von, bis: z.bis } : null;
}

async function jaRwLaden(zr) {
  const nr = ++jaRw.nr;
  el("rwInfo").textContent = "Zähle die Fehlstunden …";
  el("rwErzeugen").disabled = true;
  fehlerIn("rwFehler", "");
  try {
    let url = "api/schild/rueckweg";
    if (zr) url += `?von=${encodeURIComponent(zr.von)}&bis=${encodeURIComponent(zr.bis)}`;
    const d = await senden("GET", url);
    if (nr !== jaRw.nr) return;
    const erst = !jaRw.daten;
    jaRw.daten = d;
    if (erst) jaRwZeitraeumeBauen(d);
    jaRwZeigen(d);
  } catch (f) {
    if (nr !== jaRw.nr) return;
    el("rwInfo").textContent = "";
    el("rwZahlen").replaceChildren();
    el("rwWarnung").hidden = true;
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("rwFehler", t);
  }
}

function jaRwZeitraeumeBauen(d) {
  const knoepfe = (d.zeitraeume || []).map((z) => h("label", { class: "schalter gross" },
    h("input", { type: "radio", name: "rwZr", value: z.id }),
    h("span", null, `${z.text} (${kurzDatum(z.von)} – ${kurzDatum(z.bis)})`)));
  knoepfe.push(h("label", { class: "schalter gross" },
    h("input", { type: "radio", name: "rwZr", value: "eigen" }),
    h("span", null, "anderer Zeitraum (z. B. wenige Tage für eine Probedatei)")));
  el("rwZeitraeume").replaceChildren(...knoepfe);
  for (const r of el("rwZeitraeume").querySelectorAll("input")) {
    if (r.value === d.standard) r.checked = true;
    r.addEventListener("change", jaRwZeitraumGeaendert);
  }
  el("rwVon").min = d.kalenderStart || "";
  el("rwVon").max = d.kalenderEnde || "";
  el("rwBis").min = d.kalenderStart || "";
  el("rwBis").max = d.kalenderEnde || "";
  el("rwVon").value = d.von;
  el("rwBis").value = d.bis;
}

function jaRwZeitraumGeaendert() {
  const r = document.querySelector('input[name="rwZr"]:checked');
  el("rwEigen").hidden = !r || r.value !== "eigen";
  el("rwCodeBox").hidden = true;          // ein Code gehört zu seinem Zeitraum
  el("rwCode").textContent = "";
  clearTimeout(jaRw.timer);
  jaRw.timer = setTimeout(() => {
    const zr = jaRwZeitraum();
    if (!zr) { el("rwInfo").textContent = "Bitte „von“ und „bis“ wählen."; el("rwErzeugen").disabled = true; return; }
    jaRwLaden(zr);
  }, 250);
}

function jaRwZeigen(d) {
  const z = d.zahlen;
  const ueber = z.entschuldigt + z.unentschuldigt + z.beurlaubt;
  const gewaehlt = jaRwZeitraum();
  el("rwInfo").textContent = `Zeitraum ${kurzDatum(d.von)} – ${kurzDatum(d.bis)}` +
    (gewaehlt && gewaehlt.bis > d.bis ? ` (bis heute${d.testdatum ? ", Testdatum" : ""} – spätere Stunden gibt es noch nicht)` : "") +
    (d.pseudo ? " · Pseudodaten (Entwicklung)" : "");
  el("rwZahlen").replaceChildren(
    h("p", { class: "rw-zeile" }, h("strong", null, "Wird übertragen: "),
      `${ueber} Fehlstunden von ${z.schueler} Schülern – entschuldigt ${z.entschuldigt}, beurlaubt ${z.beurlaubt} ` +
      `(zählt als entschuldigt), unentschuldigt ${z.unentschuldigt}.`),
    h("p", { class: "rw-zeile" }, h("strong", null, "Nicht übertragen: "),
      `offen ${z.offen}, Verspätungen ${z.verspaetungen}, schulische Veranstaltung ${z.schulveranstaltung}` +
      (z.ohnePruefsumme ? `, ohne Prüfsumme ${z.ohnePruefsumme} (${z.schuelerOhnePruefsumme} Schüler, nicht aus SchILD importiert)` : "") +
      (z.doppelt ? `, doppelt erfasst ${z.doppelt} (eine zählt)` : "") + "."),
    h("p", { class: "rw-zeile" }, h("strong", null, "Nicht erfasst: "),
      `${d.nichtErfasst} Stunden ohne Anwesenheit (bis gestern).`));

  const warn = [];
  if (z.offen) warn.push(`${z.offen} offene Fehlstunden – erst bewerten (Klassenbuch → Offene Fälle), sonst fehlen sie in SchILD.`);
  if (d.nichtErfasst) warn.push(`${d.nichtErfasst} Stunden ohne Anwesenheit (Klassenbuch → Nicht erfasst).`);
  if (d.ueberHalbjahr && d.halbjahrAb)
    warn.push(`Der Zeitraum reicht über den Beginn des 2. Halbjahres (${kurzDatum(d.halbjahrAb)}). SchILD ordnet Fehlstunden ` +
      "einem Abschnitt zu – der Desktop schreibt nur die Stunden des gewählten Abschnitts. Besser je Halbjahr einen Code.");
  if (z.ohnePruefsumme)
    warn.push(`${z.schuelerOhnePruefsumme} Schüler ohne Prüfsumme (z. B. Seed-Schüler oder von Hand angelegt) – ihre Fehlstunden ` +
      "kann der Desktop keinem SchILD-Schüler zuordnen.");
  if ((d.frueher || []).length)
    warn.push(h("span", null, "Für diesen Zeitraum schon bereitgestellt – ein zweiter Import in SchILD kann doppelt zählen:",
      h("ul", { class: "rw-frueher" }, d.frueher.map((p) => h("li", null,
        `${datumZeit(p.zeit)}: ${JA_RW_ART[p.art] || p.art} ${kurzDatum(p.von)} – ${kurzDatum(p.bis)} (${p.akteur})`)))));
  el("rwWarnung").replaceChildren(...warn.map((w) => h("p", null, w)));
  el("rwWarnung").hidden = warn.length === 0;

  if (d.code) {
    el("rwAktiv").textContent = `Ein Abholcode ist noch gültig bis ${jaUhr(d.code.gueltigBis)} Uhr (Zeitraum ` +
      `${kurzDatum(d.code.von)} – ${kurzDatum(d.code.bis)}, erzeugt von ${d.code.akteur}). Ein neuer Code ersetzt ihn.`;
    el("rwAktiv").hidden = false;
    el("rwVerwerfen").hidden = false;
  } else {
    el("rwAktiv").hidden = true;
    el("rwVerwerfen").hidden = el("rwCodeBox").hidden;
  }
  el("rwErzeugen").disabled = jaRw.laeuft || ueber === 0;
  el("rwErzeugen").title = ueber === 0 ? "Im Zeitraum gibt es keine übertragbaren Fehlstunden." : "";
}

async function jaRwErzeugen() {
  const zr = jaRwZeitraum();
  if (!zr || jaRw.laeuft) return;
  jaRw.laeuft = true;
  el("rwErzeugen").disabled = true;
  fehlerIn("rwFehler", "");
  try {
    const c = await senden("POST", "api/schild/rueckweg/code", { von: zr.von, bis: zr.bis });
    el("rwCode").textContent = c.code;
    el("rwCodeInfo").textContent = `Gültig bis ${jaUhr(c.gueltigBis)} Uhr (${c.minuten} Minuten), nur ein Abruf · Zeitraum ` +
      `${kurzDatum(c.von)} – ${kurzDatum(c.bis)}. Im Desktop: Wocheneditor → „SchILD-Daten…“ → aktuellen SchILD-Export ` +
      "prüfen → Reiter „Rückweg“ → Code eingeben → „Abrufen“.";
    el("rwCodeBox").hidden = false;
    el("rwVerwerfen").hidden = false;
    el("rwAktiv").hidden = true;
    el("rwErzeugen").textContent = "Neuen Code erzeugen";
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("rwFehler", t);
  } finally {
    jaRw.laeuft = false;
    el("rwErzeugen").disabled = false;
  }
}

async function jaRwVerwerfen() {
  if (jaRw.laeuft) return;
  jaRw.laeuft = true;
  fehlerIn("rwFehler", "");
  try {
    const r = await senden("DELETE", "api/schild/rueckweg/code");
    el("rwCodeBox").hidden = true;
    el("rwCode").textContent = "";
    el("rwAktiv").hidden = true;
    el("rwVerwerfen").hidden = true;
    el("rwErzeugen").textContent = "Abholcode erzeugen";
    toast(r && r.verworfen ? "Der Abholcode ist ungültig." : "Es gab keinen gültigen Abholcode mehr.");
  } catch (f) {
    const t = aktionsFehler(f);
    if (t !== null) fehlerIn("rwFehler", t);
  } finally {
    jaRw.laeuft = false;
  }
}

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function jahresablaufInit() {
  el("jaKnopf").addEventListener("click", jahresablaufOeffnen);
  el("jaZurueck").addEventListener("click", jahresablaufVerlassen);
  // P4b
  el("rwErzeugen").addEventListener("click", jaRwErzeugen);
  el("rwVerwerfen").addEventListener("click", jaRwVerwerfen);
  el("rwSchliessen").addEventListener("click", jaRwZu);
  el("rwVon").addEventListener("change", jaRwZeitraumGeaendert);
  el("rwBis").addEventListener("change", jaRwZeitraumGeaendert);
  el("rwDialog").addEventListener("close", () => {
    const neu = !jaRw.ohneNeuladen;
    jaRw.ohneNeuladen = false;
    jaRw.nr++;
    clearTimeout(jaRw.timer);
    el("rwCode").textContent = "";
    if (neu && jahresablaufOffen()) jaLaden();   // Karten mit dem neuen Stand (Abruf, Datei)
  });
  hintergrundSchliesst(el("rwDialog"), () => { if (!jaRw.laeuft) jaRwZu(); });
  // P5a
  el("jaDOk").addEventListener("click", jaAkAusfuehren);
  el("jaDAbbrechen").addEventListener("click", () => { if (!jaAk.laeuft) jaAkZu(); });
  el("jaDGrund").addEventListener("input", jaAkZeichen);
  el("jaDialog").addEventListener("close", () => { jaAk.aktion = null; });
  hintergrundSchliesst(el("jaDialog"), () => { if (!jaAk.laeuft) jaAkZu(); });
  // P5b-2
  el("jzOk").addEventListener("click", jaZkUebernehmen);
  el("jzAbbrechen").addEventListener("click", () => { if (!jaZk.laeuft) jaZkZu(); });
  el("jzAlle").addEventListener("click", () => jaZkAuswahl(true));
  el("jzKeine").addEventListener("click", () => jaZkAuswahl(false));
  el("jzDialog").addEventListener("close", () => { jaZk.nr++; jaZk.daten = null; });
  hintergrundSchliesst(el("jzDialog"), () => { if (!jaZk.laeuft) jaZkZu(); });
}
