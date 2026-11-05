"use strict";
// =====================================================================
//  Stundenplan-PWA – Hilfe zur Bedienung (Entscheidungen 52–59, 05.11.2026)
//
//  - „?"-Knopf oben im Kopf: erklärt den gerade sichtbaren Bereich (Thema +
//    „Passt dazu"), von dort „Alle Hilfethemen".
//  - Kontomenü „Hilfe": Themenliste – nur Themen, die zur eigenen Rolle passen.
//  - Kurzkarte „Kurz erklärt" beim ersten Besuch von Plan, Stundenblatt,
//    Klassenbuch (Personal) und Hausaufgaben (Schüler); „Verstanden" blendet
//    sie auf diesem Gerät dauerhaft aus.
//  - Browser-Speicher: nur der Merker, welche Kurzkarten gesehen wurden
//    (localStorage, Schlüssel „hilfe:<Prüfsumme des Benutzernamens>"). Er
//    bleibt beim Abmelden erhalten (Entscheidung 54) – app.js löscht beim
//    Abmelden nur Schlüssel mit „u:<name>|".
//  - Anrede „du" (Entscheidung 55). Alle Texte stehen unten gesammelt
//    (HI_KARTEN, HI_THEMEN) und lassen sich ohne Neukompilieren ändern.
//
//  Wird nach eintraege.js und VOR app.js geladen; Einstieg hilfeInit().
//  Alle Namen mit Präfix „hi…" (klassische Skripte teilen sich einen
//  Namensraum).
// =====================================================================

// ---------------------------------------------------------------------
//  Texte
// ---------------------------------------------------------------------

/** Kurzkarten beim ersten Besuch eines Bereichs (2–3 Sätze). */
const HI_KARTEN = {
  plan: {
    text: "Hier siehst du deinen Stundenplan mit allen Änderungen. Rot durchgestrichen heißt: fällt weg oder ist " +
      "geändert – daneben steht, was stattdessen gilt. Über das „?“ oben bekommst du jederzeit Hilfe.",
    thema: "plan",
  },
  stundenblatt: {
    text: "Tippe die Namen an, die fehlen, und dann auf „Speichern“. Sind alle da, reicht „Alle anwesend“ und " +
      "„Speichern“. Thema und Hausaufgabe trägst du oben ein – sie werden mit gespeichert.",
    thema: "stundenblatt",
  },
  klassenbuch: {
    text: "Hier findest du Stunden, die noch nicht erfasst sind, deine Kurse mit Fehlzeiten und Themen und – als " +
      "Klassenleitung oder Stufenkoordination – die Übersicht deiner Klasse bzw. Stufe.",
    thema: null,   // je nach Reiter (hiKontext)
  },
  hausaufgaben: {
    text: "Hier stehen deine Hausaufgaben, die nächsten oben. Mit dem Haken markierst du Erledigtes – das sieht " +
      "nur dieses Gerät, nicht die Schule.",
    thema: "hausaufgaben",
  },
};

const HI_GRUPPEN = [
  { id: "alle", titel: "Für alle" },
  { id: "schueler", titel: "Für Schülerinnen und Schüler" },
  { id: "lehrkraft", titel: "Für Lehrkräfte" },
  { id: "leitung", titel: "Für Klassenleitung und Stufenkoordination" },
  { id: "admin", titel: "Für den Admin" },
];

/*
 * Ein Thema besteht aus Bausteinen:
 *   "Text"                       Absatz; **fett** wird hervorgehoben
 *   { liste: [...] }             Aufzählung
 *   { zwischen: "…" }            Zwischenüberschrift
 *   { farben: [[art, text]] }    Aufzählung mit Farbfeld (Farben wie im Plan)
 *   { begriffe: [[wort, text]] } Begriffsliste
 *   { nur: "personal" | "schueler" | "admin" | "app" | "keineApp", teile: [...] }  nur für diese Fälle
 * ohneAnmeldung: true = auch vor der Anmeldung lesbar.
 */
const HI_THEMEN = [
  // ---- Für alle ----
  {
    id: "anmelden", gruppe: "alle", titel: "Anmelden", ohneAnmeldung: true,
    inhalt: [
      "Melde dich mit deinem Benutzernamen an – Groß- und Kleinschreibung ist egal.",
      "**Angemeldet bleiben** nur auf deinem eigenen Gerät: Dann bleibst du 30 Tage angemeldet. Sonst endet die " +
        "Anmeldung, wenn du den Browser schließt.",
      "Nach mehreren falschen Versuchen musst du kurz warten, bevor es wieder geht.",
      "Mit einem **Startpasswort** legst du beim ersten Anmelden ein eigenes Passwort fest – erst danach geht es weiter.",
      "**Passwort vergessen?** Ein neues Startpasswort bekommst du von deiner Klassenleitung bzw. " +
        "Stufenkoordination, Lehrkräfte vom Admin. Per E-Mail geht das nicht.",
    ],
  },
  {
    id: "plan", gruppe: "alle", titel: "Plan lesen", ohneAnmeldung: true,
    inhalt: [
      "Mit den Pfeilen blätterst du wochenweise, „Heute“ springt zurück. Tippst du auf das Datum, wählst du eine " +
        "beliebige Woche.",
      "Am Handy hochkant siehst du einen Tag – wische nach links oder rechts für den nächsten. Quer oder am PC " +
        "siehst du die ganze Woche.",
      { zwischen: "Farben" },
      { farben: [
        ["vertretung", "grün – Vertretung"],
        ["entfall", "rot – Entfall"],
        ["verlegung", "orange – Verlegung"],
        ["betreuung", "blau – Betreuung oder EVA"],
        ["raum", "gelb – Raumänderung"],
      ] },
      "Der alte Wert ist rot durchgestrichen, der neue steht daneben. „Nur Änderungen“ blendet alles andere aus.",
      "Der Plan lädt sich von selbst neu. Ohne Netz zeigt die App den zuletzt geladenen Stand und unten den " +
        "Hinweis „Offline“.",
      { nur: "schueler", teile: [
        "Steht „HA“ an einer Stunde, tippe sie an: Dann siehst du Thema und Hausaufgabe.",
      ] },
      { nur: "personal", teile: [
        { zwischen: "Für das Kollegium" },
        "Oben wählst du Lehrkraft, Klasse, Kurs oder Schüler (Suche nach dem Namen).",
        "Stunden mit **Buch-Symbol** kannst du antippen – dann öffnet sich das Stundenblatt. Ein Häkchen heißt: " +
          "schon erfasst.",
        "„wird vertreten“ steht bei deinen eigenen Stunden, die jemand anderes übernimmt.",
      ] },
    ],
  },
  {
    id: "startbildschirm", gruppe: "alle", titel: "App auf den Startbildschirm", ohneAnmeldung: true,
    inhalt: [
      { nur: "app", teile: ["Du nutzt die App schon vom Startbildschirm aus."] },
      { nur: "keineApp", teile: ["Dann startet der Plan wie eine eigene App – ohne Adresszeile."] },
      { liste: [
        "**iPhone/iPad:** in Safari auf „Teilen“ tippen, dann „Zum Home-Bildschirm“.",
        "**Android:** im Browsermenü (⋮) „App installieren“ bzw. „Zum Startbildschirm hinzufügen“.",
        "**PC mit Chrome oder Edge:** das Installieren-Symbol rechts in der Adresszeile.",
        "**PC mit Firefox:** am besten ein Lesezeichen anlegen.",
      ] },
    ],
  },
  {
    id: "konto", gruppe: "alle", titel: "Konto und Passwort",
    inhalt: [
      "Über den Kontoknopf oben rechts erreichst du „Passwort ändern“. Danach bist du auf allen anderen Geräten " +
        "abgemeldet.",
      "Personal braucht mindestens 10 Zeichen, Schüler mindestens 8. Ein langer Satz aus mehreren Wörtern ist " +
        "sicherer als Sonderzeichen.",
      "„Auf allen Geräten abmelden“ hilft, wenn du ein Handy verloren hast oder dich an einem fremden Gerät nicht " +
        "abgemeldet hast.",
      "Beim Abmelden löscht die App, was sie für dich auf diesem Gerät gespeichert hat – nur der Merker, welche " +
        "Kurzhinweise du schon gesehen hast, bleibt.",
    ],
  },
  {
    id: "begriffe", gruppe: "alle", titel: "Begriffe",
    inhalt: [
      { begriffe: [
        ["entschuldigt / offen / unentschuldigt", "„Offen“ heißt: Es liegt noch keine Entscheidung vor. " +
          "„Unentschuldigt“ setzen Klassenleitung bzw. Stufenkoordination – in der Oberstufe auch die Kurslehrkraft " +
          "für ihre eigenen Kursstunden."],
        ["festgelegt", "von Klassenleitung, Stufenkoordination oder Admin entschieden. Die Fach- bzw. " +
          "Kurslehrkraft kann es dann nicht mehr ändern."],
        ["Fehlquote", "Fehlstunden geteilt durch die erfassten Stunden, in denen die Person dabei sein sollte."],
        ["schulische Veranstaltung", "z. B. Ausflug oder Wettbewerb – zählt nicht als Fehlzeit."],
        ["Doppelstunde", "zwei direkt aufeinanderfolgende Stunden desselben Unterrichts."],
        ["EVA", "eigenverantwortliches Arbeiten."],
        ["Betreuung", "Aufsicht ohne Fachunterricht."],
        ["Verlegung", "Die Stunde wurde auf einen anderen Termin geschoben."],
      ] },
    ],
  },

  // ---- Für Schülerinnen und Schüler ----
  {
    id: "hausaufgaben", gruppe: "schueler", titel: "Hausaufgaben",
    inhalt: [
      "Hier stehen deine Hausaufgaben, sortiert nach heute, morgen und später. Die Leiste über dem Plan zeigt, wie " +
        "viele noch offen sind.",
      "Tippst du im Plan auf eine Stunde mit „HA“, siehst du Thema und Aufgabe.",
      "**Abhaken** gilt nur auf diesem Gerät – die Schule sieht das nicht.",
      "Fehlt eine Hausaufgabe, hat die Lehrkraft sie vielleicht nicht für Schüler sichtbar gestellt. Frag im " +
        "Zweifel nach.",
      "Ohne Netz siehst du den zuletzt geladenen Stand.",
    ],
  },
  {
    id: "meineFehlzeiten", gruppe: "schueler", titel: "Meine Fehlzeiten",
    inhalt: [
      "Hier stehen deine Fehlstunden und Verspätungen mit Status. Oben wählst du den Zeitraum: 1. oder 2. Halbjahr, " +
        "das ganze Schuljahr oder einen eigenen.",
      "Entschuldigungen gibst du wie bisher **schriftlich** ab. Sobald sie eingetragen sind, steht hier „entschuldigt“.",
      "„Offen“ heißt: Die Entschuldigung ist noch nicht eingetragen. „Unentschuldigt“ setzt nur die Schule.",
    ],
  },

  // ---- Für Lehrkräfte ----
  {
    id: "stundenblatt", gruppe: "lehrkraft", titel: "Stundenblatt: Anwesenheit",
    inhalt: [
      "**Name antippen** = fehlt, nochmal tippen = wieder anwesend.",
      "**⋯** neben dem Namen öffnet: verspätet (mit Minuten, Schnellwahl 5–20), beurlaubt oder schulische " +
        "Veranstaltung (zählt nicht als Fehlzeit). Dort schreibst du auch Einträge.",
      "Sind alle da: „Alle anwesend“, dann „Speichern“. Erst nach dem Speichern gilt die Stunde als erfasst – bis " +
        "dahin steht unten „Noch nicht gespeichert“.",
      "**Doppelstunde:** Der Schalter in der Speichern-Leiste übernimmt die Anwesenheit in die 2. Stunde. Aus " +
        "„verspätet“ wird dort „anwesend“; eine schon erfasste 2. Stunde wird nie überschrieben.",
      "Ein **Schloss** am Namen heißt: Diese Fehlstunde hat die Klassenleitung bzw. Stufenkoordination festgelegt.",
      "Künftige Stunden kannst du nur lesen. Haben zwei Personen gleichzeitig gespeichert, erscheint „Neu laden“. " +
        "Unter „Änderungen“ siehst du, wer wann was geändert hat.",
    ],
  },
  {
    id: "themaHa", gruppe: "lehrkraft", titel: "Thema und Hausaufgabe",
    inhalt: [
      "**Thema** (bis 300 Zeichen): erscheint im Klassenbuch und bei den Schülern. Das Thema der vorigen Stunde " +
        "steht als Hinweis darüber.",
      "**Hausaufgabe** (bis 2000 Zeichen): „Fällig am“ steht schon auf der nächsten Stunde dieses Unterrichts; " +
        "Schnellwahl „in 1 Woche“ oder ein Datum.",
      "Gespeichert wird beides mit „Speichern“, zusammen mit der Anwesenheit. Bitte keine Schülernamen eintragen – " +
        "dafür gibt es Einträge.",
      "Der Schalter „Hausaufgaben für Schüler sichtbar“ gilt für den ganzen Unterricht, nicht für eine einzelne " +
        "Stunde. Umlegen kann ihn die Lehrkraft laut Plan.",
    ],
  },
  {
    id: "eintraege", gruppe: "lehrkraft", titel: "Einträge",
    inhalt: [
      "**Schülereintrag:** über ⋯ beim Namen – Lob, Hinweis, Ermahnung, Hausaufgabe vergessen, Material vergessen.",
      "**Klasseneintrag:** über „+ Eintrag für die Klasse“ – Vorkommnis, Hinweis, Lob.",
      "Einträge werden **sofort** gespeichert, nicht über „Speichern“.",
      "Schreib sachlich: keine Gesundheitsdaten, keine Vermutungen.",
      "Schülereinträge sehen nur du, die Klassenleitung bzw. Stufenkoordination und der Admin. Schüler sehen keine " +
        "Einträge.",
      "Ändern und löschen kann nur, wer den Eintrag geschrieben hat (und der Admin). Gelöscht ist wirklich weg.",
    ],
  },
  {
    id: "gruppen", gruppe: "lehrkraft", titel: "Gruppen zusammenstellen",
    inhalt: [
      "Bei Religion, Wahlpflicht oder klassenübergreifenden Kursen stellst du die Gruppe einmal aus den beteiligten " +
        "Klassen zusammen.",
      "Danach zeigt das Stundenblatt nur diese Schülerinnen und Schüler, und sie sehen nur ihre eigene Gruppe im Plan.",
      "Ändern kannst du die Gruppe jederzeit unter „Meine Gruppen“ im Kontomenü.",
    ],
  },
  {
    id: "nichtErfasst", gruppe: "lehrkraft", titel: "Nicht erfasst",
    inhalt: [
      "Eine Stunde erscheint hier ab dem Tag nach dem Unterricht, wenn die Anwesenheit oder das Thema fehlt.",
      "„Meine Stunden“ zeigt deine eigenen. Klassenleitung bzw. Stufenkoordination sehen mit „Alle Stunden der …“ " +
        "alle Stunden ihrer Klasse bzw. Stufe.",
      "Mit „Alles | Anwesenheit | Thema“ filterst du, was fehlt.",
      "Antippen öffnet das Stundenblatt, „Zurück zur Liste“ führt zurück. „Gruppe fehlt“ heißt: Die Gruppe ist noch " +
        "nicht zusammengestellt.",
    ],
  },
  {
    id: "meineKurse", gruppe: "lehrkraft", titel: "Meine Kurse",
    inhalt: [
      "Je Unterricht siehst du Fehlzeiten, Themen oder Einträge – umschaltbar oben. Den Zeitraum wählst du darüber.",
      "Ein Name öffnet die Fehlzeiten der Person in diesem Unterricht.",
      "**Oberstufe:** In deinen eigenen Kursstunden kannst du Fehlstunden selbst bewerten (entschuldigt, offen, " +
        "unentschuldigt), solange die Stufenkoordination nichts festgelegt hat.",
    ],
  },
  {
    id: "kursheft", gruppe: "lehrkraft", titel: "Kursheft als PDF",
    inhalt: [
      "„Kursheft als PDF …“ erstellt das Kursheft des Unterrichts für den gewählten Zeitraum.",
      "Die Datei enthält Schülerdaten: nur auf dienstlich freigegebenen Geräten speichern und nicht weitergeben.",
      "Jeder Download wird protokolliert (wer, wann, was – ohne Inhalt).",
    ],
  },

  // ---- Für Klassenleitung und Stufenkoordination ----
  {
    id: "uebersicht", gruppe: "leitung", titel: "Übersicht und Fehlquote",
    inhalt: [
      "Pro Person: Fehlstunden, davon entschuldigt, offen und unentschuldigt, die Fehlquote, Verspätungen und Minuten.",
      "Ein Tipp auf eine Spaltenüberschrift sortiert. „Nur Schüler mit offenen Stunden“ filtert.",
      "Die Fehlquote ist reine Information – es gibt keine Grenzwerte und keine Automatik.",
      "Am Handy sind „entsch.“ und „Min.“ ausgeblendet; die Minuten stehen dann unter den Verspätungen.",
    ],
  },
  {
    id: "offeneFaelle", gruppe: "leitung", titel: "Offene Fälle und Entschuldigungen",
    inhalt: [
      "„Offene Fälle“ zeigt alle Personen mit offenen Stunden im Schuljahr, die ältesten zuerst. Ab 14 Tagen " +
        "bekommen sie einen orangenen Rand – nur zur Orientierung, keine Frist.",
      "Die schriftliche Entschuldigung bleibt auf Papier. Hier trägst du nur ein, dass sie vorliegt.",
      "„Entschuldigung eintragen“ gilt für einen **Zeitraum** und deckt alle Fehlstunden und Verspätungen darin ab – " +
        "auch später nachgetragene.",
      "Als Grund genügt eine Kategorie. Nichts wird automatisch unentschuldigt.",
    ],
  },
  {
    id: "bewerten", gruppe: "leitung", titel: "Fehlstunden bewerten",
    inhalt: [
      "Im Schülerdetail kannst du eine einzelne Fehlstunde bewerten: entschuldigt (mit Grund), offen oder " +
        "unentschuldigt.",
      "Was du festlegst, kann die Fach- bzw. Kurslehrkraft nicht mehr ändern.",
      "Eine Entschuldigung für einen Zeitraum trägst du ebenfalls im Schülerdetail ein – dort kannst du sie auch " +
        "wieder löschen.",
    ],
  },
  {
    id: "eintraegeSchueler", gruppe: "leitung", titel: "Einträge und Schülerdetail",
    inhalt: [
      "Der Reiter „Einträge“ zeigt alle Einträge deiner Klasse bzw. Stufe, filterbar nach Kategorie.",
      "Im Schülerdetail stehen Zähler und Liste der Einträge. Dort kannst du auch einen „Eintrag ohne Stunde“ " +
        "schreiben (z. B. Pause, Ausflug).",
    ],
  },
  {
    id: "klassenbuchPdf", gruppe: "leitung", titel: "Klassenbuch als PDF",
    inhalt: [
      "„Klassenbuch als PDF …“ in der Übersicht: Wähle das ganze Klassenbuch oder das Kursheft eines Unterrichts " +
        "der Klasse. Es gilt der Zeitraum, den du oben gewählt hast.",
      "„Schülereinträge aufnehmen“ ist absichtlich aus – schalte es nur ein, wenn du sie wirklich brauchst.",
      "Die Datei enthält Schülerdaten: nur auf dienstlich freigegebenen Geräten speichern. Jeder Download wird " +
        "protokolliert.",
    ],
  },
  {
    id: "fehlzeitenCsv", gruppe: "leitung", titel: "Fehlzeiten als CSV",
    inhalt: [
      "„Fehlzeiten als CSV …“ in der Übersicht deiner Klasse bzw. Stufe speichert die Summen für die Verwaltung " +
        "als Tabelle – für Excel, Semikolon-getrennt.",
      "Eine Zeile je Schüler; in der Oberstufe zusätzlich je Kurs eine Zeile (Spalte „Kurs“, leer = Summe).",
      "Vorher zeigt der Dialog, wie viele Stunden noch offen oder nicht erfasst sind – die Summen können sich dann " +
        "noch ändern. Speichern ist trotzdem möglich. Es gilt der Zeitraum, den du oben gewählt hast.",
      { nur: "admin", teile: ["Als Admin kannst du auch **alle Klassen und Stufen** in einer Datei speichern."] },
      "Die Datei enthält Schülerdaten: nur auf dienstlich freigegebenen Geräten speichern. Jeder Download wird " +
        "protokolliert.",
    ],
  },
  {
    id: "schuelerkonten", gruppe: "leitung", titel: "Schülerkonten", nichtAdmin: true,
    inhalt: [
      "Kontomenü → „Schülerpasswörter“: Hier setzt du das Passwort eines Schülerkontos deiner Klasse bzw. Stufe zurück.",
      "Das **Startpasswort erscheint nur einmal** – gleich notieren oder den Zugangszettel drucken.",
      "Außerdem kannst du Konten sperren und wieder entsperren.",
    ],
  },

  // ---- Für den Admin ----
  {
    id: "konten", gruppe: "admin", titel: "Konten verwalten",
    inhalt: [
      "**Konten:** anlegen, bearbeiten, Passwort zurücksetzen, sperren, auf allen Geräten abmelden, löschen.",
      "**Sammelanlage:** Lehrkräfte aus dem Grundplan bzw. Schüler je Klasse oder Stufe. Danach die Zugangszettel " +
        "drucken oder als CSV speichern, bevor du die Liste schließt – die Startpasswörter sind danach weg.",
      "**Protokoll:** wer wann was an welchem Konto geändert hat – ohne Passwörter, ein Jahr lang.",
      "**Downloads:** wer wann welche Datei mit Schülerdaten geladen hat.",
    ],
  },
  {
    id: "gruppenlisten", gruppe: "admin", titel: "Gruppenlisten",
    inhalt: [
      "Kontomenü → „Gruppenlisten“: alle Schülergruppen aus dem Plan an einem Ort.",
      "Normalerweise pflegt sie die Lehrkraft der Gruppe; du kannst jede Gruppe ändern.",
    ],
  },
  {
    id: "jahresarchiv", gruppe: "admin", titel: "Jahresarchiv",
    inhalt: [
      "„Jahresarchiv …“ (Klassenbuch → Klassen, Übersicht) erstellt für den gewählten Zeitraum je Klasse der Sek I " +
        "ein Klassenbuch und je Oberstufenkurs ein Kursheft – als PDF-Dateien in einer Zip-Datei mit Inhaltsverzeichnis.",
      "Das dauert einige Minuten. Lass das Fenster so lange offen; „Abbrechen“ beendet die Erstellung. Am " +
        "zuverlässigsten am PC im Schulnetz.",
      "„Schülereinträge aufnehmen“ ist absichtlich aus – schalte es nur ein, wenn du sie wirklich brauchst.",
      "Die Datei enthält Schülerdaten aller Klassen: nur auf dienstlich freigegebenen Geräten speichern. Der " +
        "Download wird protokolliert.",
    ],
  },
  {
    id: "downloads", gruppe: "admin", titel: "Downloads",
    inhalt: [
      "Konten verwalten → „Downloads“ zeigt, wer wann welche Datei mit Schülerdaten geladen hat: Klassenbuch- und " +
        "Kursheft-PDFs, Fehlzeiten-CSV und Jahresarchive – mit Zeitraum und ob Schülereinträge enthalten waren.",
      "Filtern kannst du nach Art und nach Person (Teil des Benutzernamens).",
      "Gespeichert wird nur, was geladen wurde, nicht der Inhalt. Einträge werden nach einem Jahr gelöscht.",
    ],
  },
];

// ---------------------------------------------------------------------
//  Rollen: wer sieht welches Thema?
// ---------------------------------------------------------------------

const hi = {
  karte: null,       // Id der gerade gezeigten Kurzkarte
  karteThema: null,  // Thema hinter „Mehr dazu"
  geplant: false,    // Neuberechnung angemeldet (MutationObserver)
};

function hiRollen() {
  const a = state.anmeldung;
  const r = { angemeldet: !!a, schueler: false, personal: false, lehrkraft: false, leitung: false, admin: false };
  if (!a) return r;
  r.schueler = a.rolle === "Schueler";
  r.personal = istPersonal();
  r.admin = a.rolle === "Admin";
  r.lehrkraft = r.personal && (!!eigenesKuerzel() || r.admin);
  r.leitung = r.admin || (["Klassenlehrer", "Stufenkoordination"].includes(a.rolle) && !!a.zustaendig);
  return r;
}

function hiErlaubt(t, r = hiRollen()) {
  if (!t) return false;
  if (!r.angemeldet) return !!t.ohneAnmeldung;
  if (t.nichtAdmin && r.admin) return false;
  switch (t.gruppe) {
    case "alle": return true;
    case "schueler": return r.schueler;
    case "lehrkraft": return r.lehrkraft;
    case "leitung": return r.leitung;
    case "admin": return r.admin;
    default: return false;
  }
}

const hiThema = (id) => HI_THEMEN.find((t) => t.id === id) || null;

function hiNur(nur, r) {
  if (nur === "personal") return r.personal;
  if (nur === "schueler") return r.schueler;
  if (nur === "admin") return r.admin;
  if (nur === "app") return alsApp();
  if (nur === "keineApp") return !alsApp();
  return true;
}

// ---------------------------------------------------------------------
//  Was ist gerade sichtbar? -> Thema, verwandte Themen, Kurzkarte
// ---------------------------------------------------------------------

const hiSichtbar = (id) => { const e = el(id); return !!e && !e.hidden; };

function hiKontext() {
  const r = hiRollen();
  if (hiSichtbar("anmeldung")) return { themen: ["anmelden", "startbildschirm"] };
  if (hiSichtbar("pflicht")) return { themen: ["konto", "anmelden"] };
  if (hiSichtbar("verwaltung")) {
    if (r.admin && verw.reiter === "downloads") return { themen: ["downloads", "konten"] };
    return { themen: r.admin ? ["konten", "gruppenlisten", "downloads"] : ["schuelerkonten"] };
  }
  if (hiSichtbar("klassenbuch")) {
    if (kb.ansicht === "stunde")
      return { themen: ["stundenblatt", "themaHa", "eintraege", "begriffe"],
        karte: hiSichtbar("kbTipp") ? "stundenblatt" : null };
    return { themen: r.admin ? ["gruppenlisten", "gruppen"] : ["gruppen"] };
  }
  if (hiSichtbar("fehlzeiten")) {
    const o = fzOben();
    const karte = r.personal ? "klassenbuch" : null;
    if (!o) return { themen: r.schueler ? ["meineFehlzeiten"] : [], karte };
    if (o.art === "ich") return { themen: ["meineFehlzeiten", "begriffe"] };
    if (o.art === "schueler") return { themen: ["bewerten", "meineKurse", "offeneFaelle", "eintraegeSchueler", "begriffe"], karte };
    if (o.art === "themen") return { themen: ["meineKurse", "themaHa", "kursheft"], karte };
    if (o.art === "unterricht" || o.art === "keUnterricht")
      return { themen: ["meineKurse", "kursheft", "eintraege", "begriffe"], karte };
    if (o.art === "reiter") {
      if (o.reiter === "ne") return { themen: ["nichtErfasst", "stundenblatt"], karte };
      if (o.reiter === "kurse") return { themen: ["meineKurse", "kursheft"], karte };
      if (o.reiter === "eintraege") return { themen: ["eintraegeSchueler", "eintraege"], karte };
      if (o.reiter === "klasse")
        return fz.kAnsicht === "offen"
          ? { themen: ["offeneFaelle", "bewerten"], karte }
          : { themen: ["uebersicht", "offeneFaelle", "klassenbuchPdf", "fehlzeitenCsv", "jahresarchiv", "begriffe"], karte };
    }
    return { themen: [], karte };
  }
  if (hiSichtbar("hausaufgaben")) return { themen: ["hausaufgaben"], karte: r.schueler ? "hausaufgaben" : null };
  if (hiSichtbar("inhalt")) {
    const verwandt = r.schueler ? ["hausaufgaben", "meineFehlzeiten", "startbildschirm"]
      : r.personal ? ["stundenblatt", "startbildschirm", "begriffe"]
      : ["startbildschirm", "anmelden"];
    return { themen: ["plan", ...verwandt], karte: r.angemeldet ? "plan" : null };
  }
  return { themen: [] };
}

/** Erlaubte Themen des Kontexts: [Hauptthema, ...verwandte]. */
function hiKontextThemen() {
  const r = hiRollen();
  return hiKontext().themen.map(hiThema).filter((t) => hiErlaubt(t, r));
}

// ---------------------------------------------------------------------
//  Dialog: ein Thema bzw. die Themenliste
// ---------------------------------------------------------------------

/** „**fett**" -> <strong>; sonst reiner Text (kein innerHTML). */
function hiText(s) {
  return String(s).split("**").map((t, i) => (i % 2 ? h("strong", null, t) : t));
}

function hiBaustein(b, r) {
  if (typeof b === "string") return h("p", null, hiText(b));
  if (!b) return null;
  if (b.nur) return hiNur(b.nur, r) ? (b.teile || []).map((x) => hiBaustein(x, r)) : null;
  if (b.zwischen) return h("h3", { class: "hi-zwischen" }, b.zwischen);
  if (b.liste) return h("ul", { class: "hi-liste" }, b.liste.map((x) => h("li", null, hiText(x))));
  if (b.farben)
    return h("ul", { class: "hi-farben" }, b.farben.map(([art, text]) =>
      h("li", null, h("span", { class: `hi-farbe hi-f-${art}`, "aria-hidden": "true" }), hiText(text))));
  if (b.begriffe)
    return h("dl", { class: "hi-begriffe" }, b.begriffe.map(([wort, text]) =>
      [h("dt", null, wort), h("dd", null, hiText(text))]));
  return null;
}

function hiThemaKnopf(t) {
  return h("button", { type: "button", class: "menuepunkt hi-themaknopf", onclick: () => hiZeigeThema(t.id) }, t.titel);
}

function hiZeigeThema(id, verwandt = null) {
  const r = hiRollen();
  const t = hiThema(id);
  if (!hiErlaubt(t, r)) { hiZeigeListe(); return; }
  el("hiTitel").textContent = t.titel;
  el("hiListeKnopf").hidden = false;
  const andere = (verwandt || []).filter((v) => v.id !== t.id);
  el("hiInhalt").replaceChildren(
    ...t.inhalt.map((b) => hiBaustein(b, r)).flat(Infinity).filter(Boolean),
    andere.length ? h("div", { class: "hi-dazu" },
      h("h3", { class: "hi-zwischen" }, "Passt dazu"),
      andere.map(hiThemaKnopf)) : null,
  );
  hiNachOben();
}

function hiZeigeListe() {
  const r = hiRollen();
  el("hiTitel").textContent = "Hilfe";
  el("hiListeKnopf").hidden = true;
  const teile = [];
  for (const g of HI_GRUPPEN) {
    const themen = HI_THEMEN.filter((t) => t.gruppe === g.id && hiErlaubt(t, r));
    if (!themen.length) continue;
    teile.push(h("h3", { class: "hi-zwischen" }, g.titel), h("div", { class: "hi-themen" }, themen.map(hiThemaKnopf)));
  }
  el("hiInhalt").replaceChildren(...teile);
  hiNachOben();
}

function hiNachOben() {
  const d = el("hiDialog");
  d.scrollTop = 0;
  try { el("hiTitel").focus({ preventScroll: true }); } catch { /* egal */ }
}

/** Hilfe öffnen: ohne Thema die Hilfe zum sichtbaren Bereich (bzw. die Liste), mit liste=true die Themenliste. */
function hilfeOeffnen(themaId = null, liste = false) {
  dialogAuf(el("hiDialog"));
  if (liste) { hiZeigeListe(); return; }
  const kontext = hiKontextThemen();
  const id = themaId || (kontext[0] && kontext[0].id);
  if (!id) { hiZeigeListe(); return; }
  hiZeigeThema(id, kontext);
}

function hilfeSchliessen() {
  dialogZu(el("hiDialog"));
}

// ---------------------------------------------------------------------
//  Kurzkarte beim ersten Besuch (Merker nur auf diesem Gerät)
// ---------------------------------------------------------------------

/** Schlüssel ohne Klarnamen: kurze Prüfsumme (FNV-1a) des Benutzernamens. */
function hiSchluessel() {
  const name = state.anmeldung ? state.anmeldung.benutzername.toLowerCase() : "";
  let x = 2166136261;
  for (const z of name) { x ^= z.codePointAt(0); x = Math.imul(x, 16777619) >>> 0; }
  return "hilfe:" + x.toString(36);
}

function hiGesehen() {
  const v = lokal.lesen(hiSchluessel(), []);
  return Array.isArray(v) ? v : [];
}

function hiAlsGesehen(id) {
  if (!state.anmeldung || !id) return;
  const g = hiGesehen();
  if (!g.includes(id)) { g.push(id); lokal.schreiben(hiSchluessel(), g); }
}

/** Kurzkarte an den Anfang des sichtbaren Bereichs setzen (nach „Zum Plan"). */
function hiKarteEinsetzen(karte) {
  const main = [...document.querySelectorAll("body > main")].find((m) => !m.hidden);
  if (!main) return;
  const kopf = main.querySelector(":scope > .verw-kopf");
  if (kopf) { if (kopf.nextElementSibling !== karte) kopf.after(karte); }
  else if (main.firstElementChild !== karte) main.prepend(karte);
}

function hiAktualisieren() {
  hi.geplant = false;
  const karte = el("hiKarte");
  const k = state.anmeldung ? hiKontext() : null;
  const id = k && k.karte && !hiGesehen().includes(k.karte) ? k.karte : null;
  if (!id) {
    hi.karte = null;
    if (!karte.hidden) karte.hidden = true;
    return;
  }
  const kontext = hiKontextThemen();
  hi.karteThema = HI_KARTEN[id].thema || (kontext[0] && kontext[0].id) || null;
  if (hi.karte !== id) {
    hi.karte = id;
    el("hiKarteText").textContent = HI_KARTEN[id].text;
  }
  // Nur ändern, was sich ändert: jedes Setzen von hidden meldet der Beobachter wieder.
  if (el("hiKarteMehr").hidden !== !hi.karteThema) el("hiKarteMehr").hidden = !hi.karteThema;
  hiKarteEinsetzen(karte);
  if (karte.hidden) karte.hidden = false;
}

function hiPlanen() {
  if (hi.geplant) return;
  hi.geplant = true;
  setTimeout(hiAktualisieren, 0);
}

// ---------------------------------------------------------------------
//  Start
// ---------------------------------------------------------------------

function hilfeInit() {
  el("hilfeKnopf").addEventListener("click", () => hilfeOeffnen());
  el("hilfeMenue").addEventListener("click", () => { kontoSchliessen(); hilfeOeffnen(null, true); });
  el("hiZu").addEventListener("click", hilfeSchliessen);
  el("hiListeKnopf").addEventListener("click", hiZeigeListe);
  hintergrundSchliesst(el("hiDialog"), hilfeSchliessen);

  el("hiKarteOk").addEventListener("click", () => {
    hiAlsGesehen(hi.karte);
    hiAktualisieren();
  });
  el("hiKarteMehr").addEventListener("click", () => {
    const thema = hi.karteThema;
    hiAlsGesehen(hi.karte);
    hiAktualisieren();
    hilfeOeffnen(thema);
  });

  // Anmeldung: „Passwort vergessen?" klappt den Hinweis auf (kein E-Mail-Reset).
  el("pwVergessen").addEventListener("click", () => {
    const p = el("pwVergessenText");
    p.hidden = !p.hidden;
    el("pwVergessen").setAttribute("aria-expanded", String(!p.hidden));
  });

  // Bereichswechsel erkennt die Hilfe an ein- und ausgeblendeten Elementen.
  try {
    new MutationObserver(hiPlanen).observe(document.body, { attributes: true, attributeFilter: ["hidden"], subtree: true });
  } catch { /* sehr alter Browser: keine Kurzkarten */ }
  hiPlanen();
}
