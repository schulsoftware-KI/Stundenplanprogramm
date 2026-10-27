// =====================================================================
//  Vertretungsplan – Lehrer-PWA (M5). Fragt die API (gleicher Origin) ab
//  und zeigt den Tagesplan einer Lehrkraft. Kein Build-Schritt, reines JS.
// =====================================================================

const el = (id) => document.getElementById(id);
const inpKuerzel = el("kuerzel");
const inpDatum = el("datum");
const liste = el("liste");
const status = el("status");
const stand = el("stand");

// --- Hilfen ---

function heuteIso() {
  const d = new Date();
  d.setMinutes(d.getMinutes() - d.getTimezoneOffset());
  return d.toISOString().slice(0, 10);
}

function datumVerschieben(tage) {
  const d = new Date(inpDatum.value || heuteIso());
  d.setDate(d.getDate() + tage);
  d.setMinutes(d.getMinutes() - d.getTimezoneOffset());
  inpDatum.value = d.toISOString().slice(0, 10);
}

function anzeigeDatum(iso) {
  const d = new Date(iso + "T00:00:00");
  return d.toLocaleDateString("de-DE", { weekday: "long", day: "2-digit", month: "2-digit", year: "numeric" });
}

const ART_KLASSE = {
  Vertretung: "vertretung",
  Entfall: "entfall",
  Verlegung: "verlegung",
  Betreuung: "betreuung",
  EVA: "eva",
  Raumvertretung: "raumvertretung",
};
const cssArt = (art) => ART_KLASSE[art] || "normal";

// --- Laden & Rendern ---

async function laden() {
  const kuerzel = inpKuerzel.value.trim();
  const datum = inpDatum.value || heuteIso();
  if (!kuerzel) { status.textContent = "Bitte ein Kürzel eingeben."; return; }

  localStorage.setItem("kuerzel", kuerzel);
  status.textContent = "Lädt …";
  liste.innerHTML = "";

  try {
    const url = `api/plan/lehrer/${encodeURIComponent(kuerzel)}?datum=${encodeURIComponent(datum)}`;
    const resp = await fetch(url, { cache: "no-store" });
    if (!resp.ok) throw new Error("Server " + resp.status);
    const eintraege = await resp.json();

    status.textContent = `${kuerzel} · ${anzeigeDatum(datum)}`;
    render(eintraege);
    stand.textContent = "Stand: " + new Date().toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
  } catch (e) {
    status.textContent = "Fehler beim Laden – ist der Server erreichbar?";
    liste.innerHTML = `<div class="leer">${e.message}</div>`;
  }
}

function render(eintraege) {
  if (!Array.isArray(eintraege) || eintraege.length === 0) {
    liste.innerHTML = `<div class="leer">Keine Änderungen an diesem Tag.</div>`;
    return;
  }
  eintraege.sort((a, b) => a.stunde - b.stunde);

  liste.innerHTML = "";
  for (const e of eintraege) {
    const art = cssArt(e.art);
    const karte = document.createElement("div");
    karte.className = "karte " + art;

    const klassen = e.klassen || "";
    const fach = e.fach || "";
    const lehrerWechsel =
      e.originalLehrer && e.lehrer && e.originalLehrer !== e.lehrer
        ? `${e.originalLehrer} → <strong>${e.lehrer}</strong>`
        : (e.lehrer || e.originalLehrer || "");

    const istEntfall = e.art === "Entfall";
    const kopfText = `${fach ? fach : "—"} · ${klassen}`;

    const detailTeile = [];
    if (lehrerWechsel) detailTeile.push(lehrerWechsel);
    if (e.raum) detailTeile.push("Raum " + e.raum);
    if (e.bemerkung) detailTeile.push(e.bemerkung);
    if (e.grund) detailTeile.push("(" + e.grund + ")");

    karte.innerHTML = `
      <div class="stunde">${e.stunde}</div>
      <div class="kopf ${istEntfall ? "" : ""}">
        <span class="${istEntfall ? "entfalltext" : ""}">${kopfText}</span>
      </div>
      <div class="detail">${detailTeile.join(" · ")}</div>
      <div class="badge ${art}">${e.art}</div>
    `;
    liste.appendChild(karte);
  }
}

// --- Events ---

el("laden").addEventListener("click", laden);
el("tagZurueck").addEventListener("click", () => { datumVerschieben(-1); laden(); });
el("tagVor").addEventListener("click", () => { datumVerschieben(1); laden(); });
el("heute").addEventListener("click", () => { inpDatum.value = heuteIso(); laden(); });
inpKuerzel.addEventListener("keydown", (e) => { if (e.key === "Enter") laden(); });

// --- Start ---

inpDatum.value = heuteIso();
inpKuerzel.value = localStorage.getItem("kuerzel") || "";
if (inpKuerzel.value) laden();

// Service Worker (Installierbarkeit / App-Shell-Cache).
if ("serviceWorker" in navigator) {
  window.addEventListener("load", () => navigator.serviceWorker.register("sw.js").catch(() => {}));
}
