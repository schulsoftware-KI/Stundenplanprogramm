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
//  P6a: Themen „Zweiter Faktor“ (Personal, auch vor der Anmeldung lesbar – beim Code-Schritt) und „Zugriffe“
//  (Admin); „Anmelden“, „Konten verwalten“ und „Einstellungen der Schule“ ergänzt. nurPersonal: true = nach der
//  Anmeldung nur für Personalkonten.
//  P6b: Thema „Auskunft nach Art. 15“ (Admin) neu; „Zugriffe“ (Teil „Ansichten“ = Leseprotokoll), „Einstellungen der
//  Schule“ (Leseprotokoll, Grund der Entschuldigung), „Offene Fälle …“, „Fehlstunden bewerten“, „Konten verwalten“ und
//  „Downloads“ ergänzt.
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
      // P3: Dauer aus den Einstellungen der Schule ({bleibenTage}); 0 = „Angemeldet bleiben“ gibt es nicht.
      { nur: "bleiben", teile: ["**Angemeldet bleiben** nur auf deinem eigenen Gerät: Dann bleibst du bis zu " +
        "{bleibenTage} angemeldet. Sonst endet die Anmeldung, wenn du den Browser schließt."] },
      { nur: "ohneBleiben", teile: ["Die Anmeldung endet, wenn du den Browser schließt."] },
      "Nach mehreren falschen Versuchen musst du kurz warten, bevor es wieder geht.",
      "Mit einem **Startpasswort** legst du beim ersten Anmelden ein eigenes Passwort fest – erst danach geht es weiter.",
      // P6a (225)
      "Hast du einen **zweiten Faktor** eingerichtet (nur Lehrkräfte und Verwaltung), fragt die App nach dem Passwort nach " +
        "dem 6-stelligen Code aus deiner Authenticator-App – siehe „Zweiter Faktor“.",
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
        ["klausur", "pink – Klausur"],
      ] },
      "Der alte Wert ist rot durchgestrichen, der neue steht daneben. „Nur Änderungen“ blendet alles andere aus.",
      "Ist eine Lehrkraft durchgestrichen und keine neue genannt, übernimmt niemand die Stunde (z. B. EVA). " +
        "„???“ heißt: Die Vertretung ist noch nicht geplant.",
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
      { nur: "personal", teile: ["**„Zweiter Faktor…“** im Kontomenü: einrichten, neu einrichten, neue " +
        "Wiederherstellungscodes, ausschalten – siehe „Zweiter Faktor“."] },
      "Beim Abmelden löscht die App, was sie für dich auf diesem Gerät gespeichert hat – nur der Merker, welche " +
        "Kurzhinweise du schon gesehen hast, bleibt.",
    ],
  },
  {
    // P6a (Entscheidungen 225–227)
    id: "zweiterFaktor", gruppe: "alle", titel: "Zweiter Faktor", ohneAnmeldung: true, nurPersonal: true,
    inhalt: [
      "Der zweite Faktor schützt dein Konto, auch wenn jemand dein Passwort kennt: Nach dem Passwort fragt die App nach " +
        "einem **6-stelligen Code**, den eine **Authenticator-App** auf deinem Handy alle 30 Sekunden neu zeigt (z. B. die " +
        "von Google oder Microsoft, FreeOTP, Aegis, 2FAS). Jeder Code gilt nur einmal. Schülerkonten haben keinen.",
      { zwischen: "Einrichten" },
      { liste: [
        "Kontomenü → **„Zweiter Faktor…“** → „Einrichten…“ und dein Passwort bestätigen.",
        "In der Authenticator-App ein Konto hinzufügen und den **QR-Code scannen** – oder „Schlüssel eingeben“ wählen und " +
          "den Schlüssel abtippen (zeitbasiert).",
        "Den Code eingeben, den die App jetzt zeigt.",
        "Die **10 Wiederherstellungscodes** drucken oder sicher speichern – sie werden nur dieses eine Mal angezeigt. " +
          "Danach bist du auf deinen anderen Geräten abgemeldet.",
      ] },
      "Stimmt der Code nie, prüfe, ob Datum und Uhrzeit des Handys automatisch gestellt werden.",
      { zwischen: "Handy weg oder neu" },
      "Bei der Anmeldung „Wiederherstellungscode verwenden“ – jeder Code gilt einmal. Danach unter „Zweiter Faktor…“ " +
        "**„Neu einrichten (neues Handy)“**; mit dem letzten Code musst du ihn gleich neu einrichten. „Neue " +
        "Wiederherstellungscodes…“ ersetzt alle bisherigen.",
      "Ohne Codes setzt der **Admin** den zweiten Faktor zurück (Konten verwalten → Konto). Admin-Konten setzt ein zweiter " +
        "Admin zurück – oder der Notfallzugang auf dem Server.",
      { zwischen: "Pflicht" },
      "Schreibt die Schule ihn für dein Konto vor (für Admin-Konten immer), kannst du nach dem Passwort zuerst nur den " +
        "zweiten Faktor einrichten – und ihn nicht ausschalten. Sonst ist er freiwillig und lässt sich mit Passwort und " +
        "Code wieder ausschalten.",
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
        ["Klausur", "Bei einer Klausur steht die Aufsicht als Lehrkraft; der Raum ist der Klausurraum."],
        ["???", "Vertretung, die noch nicht geplant ist – wer die Stunde übernimmt, steht noch nicht fest."],
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
      { zwischen: "Gruppen aus SchILD" },
      "Steht bei einer Gruppe **„aus SchILD“**, kommt die Liste vom Sekretariat (SchILD) und wird mit jedem Import " +
        "aktualisiert. Du kannst sie ansehen, aber nicht ändern – fehlt jemand oder ist jemand zu viel, sag bitte dem " +
        "Sekretariat Bescheid.",
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
      "**Nach dem Schuljahreswechsel:** Solange die Verwaltung das alte Schuljahr nicht abgeschlossen hat, kannst du dort " +
        "noch nachtragen – den Zeitraum auf das alte Schuljahr stellen. Nach dem Abschluss ist es nur noch für die " +
        "Verwaltung sichtbar.",
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
      "Als Grund genügt eine Kategorie – erfasst die Schule keinen Grund (Einstellung), entfällt die Auswahl und es steht " +
        "„ohne Angabe“ da. Nichts wird automatisch unentschuldigt.",
    ],
  },
  {
    id: "bewerten", gruppe: "leitung", titel: "Fehlstunden bewerten",
    inhalt: [
      "Im Schülerdetail kannst du eine einzelne Fehlstunde bewerten: entschuldigt (mit Grund, sofern die Schule ihn " +
        "erfasst), offen oder unentschuldigt.",
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
      "Der Filter **„Laut SchILD aktiv, gesperrt“** zeigt Konten, die nach einem Abgang gesperrt wurden, obwohl die " +
        "Schülerin bzw. der Schüler laut SchILD wieder da ist (Wiedereintritt). Passt das, „Entsperren“.",
      "Deine Zuständigkeit gilt für **ein Schuljahr**. Nach dem Wechsel ins neue Schuljahr bleibt die Liste leer, bis die " +
        "Verwaltung sie für das neue Jahr einträgt; die Daten des alten Jahres siehst du bis zu seinem Abschluss weiter.",
    ],
  },

  // ---- Für den Admin ----
  {
    id: "konten", gruppe: "admin", titel: "Konten verwalten",
    inhalt: [
      "**Konten:** anlegen, bearbeiten, Passwort zurücksetzen, sperren, auf allen Geräten abmelden, löschen. Ist das Handy " +
        "einer Lehrkraft weg: **„Zweiten Faktor zurücksetzen“** (meldet das Konto überall ab; bei Pflicht richtet sie ihn " +
        "neu ein). Filter „Mit zweitem Faktor“ und „Zweiter Faktor fehlt (Pflicht)“.",
      "**Zugriffe:** erfolglose Anmeldungen aller Konten und erfolgreiche Anmeldungen der Admin-Konten, dazu – wenn " +
        "eingeschaltet – das Leseprotokoll (siehe „Zugriffe“).",
      "**Auskunft (Art. 15) …** im Konto: PDF mit allen gespeicherten Daten – beim Schülerkonto über die Schülerin bzw. den " +
        "Schüler, bei Personalkonten kompakt (siehe „Auskunft nach Art. 15“).",
      "**Sammelanlage:** Lehrkräfte aus dem Grundplan bzw. Schüler je Klasse oder Stufe. Danach die Zugangszettel " +
        "drucken oder als CSV speichern, bevor du die Liste schließt – die Startpasswörter sind danach weg.",
      "**Protokoll:** wer wann was an welchem Konto geändert hat – ohne Passwörter. Wie lange es aufbewahrt wird, " +
        "steht unter Kontomenü → „Einstellungen“ → Datenschutz (Standard ein Jahr).",
      "**Downloads:** wer wann welche Datei mit Schülerdaten geladen hat.",
      "**SchILD:** Vorschläge für Klassenleitungen laut SchILD und die Liste der SchILD-Importe (siehe eigene Themen).",
      "**Zuständigkeit je Schuljahr:** Klassenleitung und Stufenkoordination sind für ein Schuljahr zuständig (steht im " +
        "Konto). Eine neue bzw. geänderte Zuständigkeit gilt für das aktuelle Schuljahr; eine unveränderte übernimmst du " +
        "beim „Bearbeiten“ mit dem Haken „für das aktuelle Schuljahr“. Siehe „Jahresablauf“.",
      "Bei Lehrkräften steht der **Name laut SchILD** neben dem Kürzel (nur für dich und die Lehrkraft selbst sichtbar); " +
        "die Suche findet auch Namen. Filter **„Laut SchILD aktiv, gesperrt“**: nach einem Wiedereintritt entsperren. " +
        "Filter **„Name laut SchILD geändert“**: im Konto steht ein Vorschlag für den neuen Benutzernamen – " +
        "„Umbenennen“ behält das Passwort; den neuen Namen bitte mitteilen.",
    ],
  },
  {
    id: "schildVorschlaege", gruppe: "admin", titel: "Vorschläge aus SchILD",
    inhalt: [
      "Konten verwalten → Reiter „SchILD“ → „Vorschläge“: Die Klassenleitung jeder Klasse kommt mit dem SchILD-Import " +
        "vom Verwaltungsrechner (Klassen.dat). Hier steht, wo die Konten nicht dazu passen.",
      { liste: [
        "**Übernehmen:** Das Konto der Lehrkraft bekommt die Rolle Klassenleitung bzw. die Klasse dazu.",
        "**Zuständigkeit entfernen:** Das Konto ist für eine Klasse zuständig, die laut SchILD jemand anderes leitet. Ist " +
          "es eine Stellvertretung, die du von Hand eingetragen hast, einfach so lassen.",
        "**Hinweis:** Verwaltungs- und Stufenkoordinations-Konten haben nur eine Rolle – bei Bedarf von Hand ändern.",
        "**Kein Konto:** über die Sammelanlage (Lehrkräfte) anlegen – mit „Klassenleitung laut SchILD gleich eintragen“.",
      ] },
      "Nichts geschieht von selbst. Jede Änderung steht im Protokoll; die Lehrkraft wird auf ihren Geräten abgemeldet " +
        "(neue Rolle). „Alle übernehmen“ übernimmt nur die Vorschläge „Übernehmen“, Entfernen geht einzeln.",
      "Konten ohne Kürzel bleiben unberührt; Stufenkoordinationen gibt es in SchILD nicht – sie bleiben von Hand.",
    ],
  },
  {
    id: "schildImporte", gruppe: "admin", titel: "SchILD-Importe",
    inhalt: [
      "Konten verwalten → Reiter „SchILD“ → „SchILD-Importe“: jeder Import vom Verwaltungsrechner mit Zeit, " +
        "Windows-Benutzer, Schuljahr, Abschnitt, Stichtag („gilt ab“), Pseudodaten bzw. echte Daten und den Zahlen.",
      "**Schlüssel:** die Kennung des Schlüssels, mit dem die Prüfsummen gebildet wurden (verrät den Schlüssel nicht). " +
        "Eine **Neu-Zuordnung** (Schlüssel verloren) ist markiert und nennt die bisherige Kennung.",
      "Die Liste enthält keine Namen und lässt sich nur lesen. Den Import selbst startet das Sekretariat im " +
        "Desktop-Programm.",
    ],
  },
  {
    // P3 (E-1, Entscheidungen 138–141, 199–203)
    id: "einstellungen", gruppe: "admin", titel: "Einstellungen der Schule",
    inhalt: [
      "Kontomenü → „Einstellungen“: die Werte deiner Schule. Sie gelten **sofort**, ohne Neustart. Jede Änderung " +
        "steht im Reiter „Protokoll“ (wer, wann, alter und neuer Wert).",
      "Gespeichert wird je Karte mit „Speichern“; „Verwerfen“ stellt die Karte wieder her. **Standard** leert ein Feld – " +
        "dann gilt wieder das bisherige Verhalten (unter jedem Feld steht, was das ist und was gerade gilt).",
      { zwischen: "Schule" },
      "**Name der Schule** steht auf der Anmeldeseite und im Kopf der PDFs. Der **Kurzname** ist der Name der App im " +
        "Fenstertitel und auf dem Startbildschirm – am besten höchstens 12 Zeichen. Beides ist auch vor der Anmeldung " +
        "sichtbar. Eine schon installierte App behält ihren Namen meist, bis sie neu installiert wird.",
      { zwischen: "Schuljahr" },
      "**Aktuelles Schuljahr**, **Anwesenheit erfasst ab**, **Thema erfasst ab** und **2. Halbjahr ab** gelten für das " +
        "ganze Schuljahr. Die Daten müssen im Schuljahr laut Kalender liegen; der Kalender kommt mit „Grundplan " +
        "veröffentlichen…“ aus dem Desktop.",
      { zwischen: "Datenschutz und Anmeldung" },
      "Diese Werte vor dem Echtbetrieb mit der bzw. dem **Datenschutzbeauftragten** abstimmen und „Abgestimmt am“ und " +
        "„mit“ eintragen. Änderst du später einen dieser Werte, entfällt die Abstimmung – dann neu eintragen.",
      "**Fristen** (Klassenbucheinträge, Abwesenheitsgründe der Vertretungen, Konten abgegangener Schüler, " +
        "Aufbewahrung der Protokolle): Eine kürzere Frist löscht ältere Daten gleich beim Speichern. Vorher steht da, " +
        "wie viele – „Speichern“ musst du dann ein zweites Mal tippen. Danach räumt der Server jede Nacht auf; was " +
        "gelöscht wurde, steht mit Zahlen im Reiter „Protokoll“ („System (Löschfrist)“).",
      { liste: [
        "**Abwesenheitsgründe:** Grund und Bemerkung zur Abwesenheit der Lehrkräfte in veröffentlichten Vertretungen – " +
          "die Vertretung selbst bleibt. Die Vertretungsdatei im Desktop behält ihre Gründe.",
        "**Konten abgegangener Schüler:** das gesperrte Konto nach einem Abgang laut SchILD. Schülerdaten und " +
          "Klassenbuch bleiben; ein wieder entsperrtes Konto bleibt auch.",
        "**Protokolle:** auch die der SchILD-Importe – der jeweils letzte Import je Schuljahr und Abschnitt bleibt, " +
          "weil der nächste Import ihn braucht.",
      ] },
      "Standard ist bei den neuen Fristen „0 (aus)“: Es wird nichts gelöscht, bis du eine Frist einträgst.",
      "**Dauer einer Anmeldung** und **„Angemeldet bleiben“** gelten für neue Anmeldungen; wer schon angemeldet ist, " +
        "bleibt es bis zum Ende der bisherigen Dauer. 0 Tage bei „Angemeldet bleiben“ blendet den Haken auf der " +
        "Anmeldeseite aus.",
      // P6a (226, 229, 230)
      "**Zweiter Faktor Pflicht** (Karte „Anmeldung“): für Klassenleitung und Stufenkoordination bzw. für alle Lehrkräfte. " +
        "Wer noch keinen hat, kann ab dem nächsten Abruf nur noch ihn einrichten – vorher ankündigen. Für Admin-Konten ist " +
        "er im Echtbetrieb immer Pflicht; der Schalter dafür wirkt nur in der Entwicklung.",
      // P6b (233, 236)
      "**Leseprotokoll** (Karte „Datenschutz“, Standard aus): An, zeichnet die App auf, wer wann welche Schülerdaten " +
        "angesehen hat – je Tag, Konto, Ansicht und Gegenstand ein Eintrag. Es zeigt auch, wann welche Lehrkraft gearbeitet " +
        "hat: vorher mit dem bzw. der Datenschutzbeauftragten und dem Personalrat klären (Dienstvereinbarung). Einsicht: " +
        "Konten verwalten → „Zugriffe“ → „Ansichten“.",
      "**Grund der Entschuldigung erfassen** (Standard an): Aus, entfällt die Auswahl beim Entschuldigen und Bewerten; " +
        "gespeichert wird „ohne Angabe“. Schon gespeicherte Gründe bleiben – darunter erscheint dann **„Vorhandene Gründe " +
        "entfernen …“**: zählt, fragt mit der Zahl nach („Nochmal tippen“) und setzt alle Gründe auf „ohne Angabe“ (auch im " +
        "Änderungsverlauf). Die Bewertung „entschuldigt“ bleibt; rückgängig machen lässt sich das nicht.",
      "Was vor dem Start des Servers feststehen muss (Datenbank, Schlüssel, Adresse), steht weiter in der " +
        "Einstellungsdatei des Servers. Die Karte **„Zugriff“** zeigt davon nur zum Lesen: Schuladressen, ob Desktop und " +
        "Admin nur aus dem Schulnetz dürfen, die Anfragebegrenzung und wie die Adresse deines Geräts eingeordnet wird.",
    ],
  },
  {
    // P6a (Entscheidung 228)
    id: "zugriffe", gruppe: "admin", titel: "Zugriffe",
    inhalt: [
      "Konten verwalten → Reiter **„Zugriffe“** → „Anmeldungen“: das Anmeldeprotokoll. Es enthält **erfolglose " +
        "Anmeldungen aller Konten** (Passwort oder Code falsch, Konto gesperrt, zu viele Fehlversuche, Adresse nicht " +
        "erlaubt), **erfolgreiche nur von Admin-Konten** und jede Anmeldung mit Wiederherstellungscode.",
      "Erfolgreiche Anmeldungen von Lehrkräften und Schülern stehen bewusst nicht darin – sonst entstünde ein " +
        "Anwesenheitsprotokoll. Ein unbekannter Benutzername erscheint als „unbekanntes Konto“ (dort steht oft ein " +
        "vertipptes Passwort). Die Adresse steht nur gekürzt da, dazu „Schulnetz“ und das Gerät grob.",
      "Filter: Konto (Teil des Benutzernamens, „-“ für unbekannte), Ergebnis, Zeitraum. Oben steht, wie viele erfolglose " +
        "Anmeldungen es in den letzten 24 Stunden gab. Jede Person sieht nach ihrer Anmeldung einmal, wie viele erfolglose " +
        "Versuche es seit ihrer letzten Anmeldung gab.",
      "Nur lesen, kein Löschen von Hand. Gelöscht wird nach „Aufbewahrung der Protokolle“ (Einstellungen → Datenschutz).",
      // P6b (232, 233)
      { zwischen: "Ansichten (Leseprotokoll)" },
      "„Zugriffe“ → **„Ansichten“**: wer wann welche Schülerdaten angesehen hat – Fehlzeiten, Stundenblatt und sein Verlauf, " +
        "Einträge, Schülerplan, Schülerdaten, Teilnehmer- und Gruppenlisten – und wer die Abwesenheitsgründe der " +
        "Lehrkräfte bzw. „Nicht erfasst“ anderer Lehrkräfte geöffnet hat. Eigene Daten, Pläne von Klassen und Lehrkräften, " +
        "Hausaufgaben und Themen stehen nicht darin, Downloads im eigenen Reiter.",
      "Je Tag, Konto, Ansicht und Gegenstand gibt es **einen** Eintrag mit der Anzahl und der ersten und letzten Uhrzeit. " +
        "Der Gegenstand nennt keine Namen (z. B. „Schüler-Id 12 (05A)“, „Klasse 05A“, „Stunde 18.11.2026, 3.“); den Namen " +
        "des Schülers zeigt die Liste nur dir.",
      "Filter: **Schüler** (Namen eintippen und antippen – dann nur die Einzelansichten dieses Schülers), Konto, Ansicht, " +
        "Zeitraum. Jeder Aufruf dieser Liste steht selbst im Leseprotokoll.",
      "Das Leseprotokoll ist **Standard aus** – einschalten unter Einstellungen → Datenschutz → „Leseprotokoll“ (vorher mit " +
        "DSB und Personalrat klären). Solange es aus ist, steht oben ein Hinweis.",
    ],
  },
  {
    // P6b (Entscheidungen 234, 235)
    id: "auskunft", gruppe: "admin", titel: "Auskunft nach Art. 15",
    inhalt: [
      "Wer wissen möchte, welche Daten die Schule über sie bzw. ihn speichert, hat ein Recht auf Auskunft (Art. 15 DSGVO). " +
        "Die App erstellt dafür ein PDF:",
      { liste: [
        "**Schüler:** Klassenbuch → Klassen → Schüler antippen → „Auskunft (Art. 15) …“ bzw. Konten verwalten → Schülerkonto " +
          "→ „Auskunft (Art. 15) …“. Enthält die Angaben nach Art. 15 Abs. 1 (Zwecke, Empfänger, Speicherdauer nach den " +
          "Fristen der Einstellungen, Rechte), Stammdaten, Klassen und Kurse, Konto mit Protokollen, jede Fehlstunde mit " +
          "Bewertung und Grund, Entschuldigungen, Summen je Halbjahr, Einträge, Änderungsverlauf, das Leseprotokoll und den " +
          "Rückweg nach SchILD – über alle gespeicherten Schuljahre.",
        "**Lehrkräfte und Verwaltung:** Konten verwalten → Konto → „Auskunft (Art. 15) …“: Konto, Zuständigkeit, Name laut " +
          "SchILD, Konten- und Anmeldeprotokoll, Abwesenheitsgründe in veröffentlichten Vertretungen, Ansichten, " +
          "Tätigkeiten im Klassenbuch und Downloads als Zahlen je Monat.",
      ] },
      "Im Leseprotokoll der Schüler-Auskunft stehen Datum, Ansicht und **Rolle – keine Namen** der Beschäftigten. " +
        "Listenansichten (Stundenblatt, Klassen- und Kurslisten) stehen als Zahl je Monat und Rolle.",
      "**Wer bekommt die Auskunft?** Das regelt die Schule: Identität prüfen, bei Minderjährigen ggf. die Eltern. Das PDF " +
        "enthält personenbezogene Daten – nur der berechtigten Person geben. Jeder Abruf steht im Download-Protokoll (Art " +
        "„Auskunft (Art. 15)“, ohne Namen).",
      "Die Texte zu Zwecken, Empfängern und Rechten sind Vorschläge – vor dem Echtbetrieb mit der bzw. dem " +
        "Datenschutzbeauftragten abstimmen. Den Auftragsverarbeiter (Betreiber des Servers) trägt die Einrichtung ein.",
    ],
  },
  {
    // P4a (5.3a, Entscheidungen 205, 206)
    id: "jahresablauf", gruppe: "admin", titel: "Jahresablauf",
    inhalt: [
      "Kontomenü → „Jahresablauf“: was zum Halbjahres- und Schuljahreswechsel zu tun ist. Jede Karte und " +
        "jeder Punkt hat eine **Ampel**: grün erledigt, gelb zu tun, rot überfällig, grau nur ein Hinweis bzw. noch " +
        "nicht dran. Hier geschieht **nichts von selbst** – die Links führen in den passenden Bereich, die Knöpfe fragen " +
        "vorher nach.",
      { zwischen: "Schuljahreswechsel" },
      "Die Karte erscheint **6 Wochen vor** dem letzten Schultag laut Kalender. Reihenfolge:",
      { liste: [
        "**Vorprüfung:** letzter Schultag vorbei? Offene Fehlstunden und Verspätungen, nicht erfasste Stunden, Rückweg nach " +
          "SchILD für das 2. Halbjahr – nur Hinweise, sie sperren nichts.",
        "**„Schuljahr … abschließen…“** (nach dem letzten Schultag): Danach ist das Klassenbuch des Jahres **nur noch lesbar** " +
          "– auch für dich – und **nur für die Verwaltung sichtbar** (lesen, PDF, Archiv). Lehrkräfte, Klassenleitung, " +
          "Stufenkoordination und Schüler sehen das Jahr nicht mehr, auch nicht im Plan. Vertretungen, Grundpläne und " +
          "SchILD-Importe für das Jahr nimmt die App nicht mehr an.",
        "**„Wieder öffnen…“** nur mit Begründung (steht im Protokoll der Einstellungen) – z. B. für Nachträge. Danach erneut " +
          "abschließen.",
        "**„Schuljahr … vorbereiten…“:** vorher im Desktop Kalender und Grundplan des neuen Jahres veröffentlichen. Der Knopf " +
          "stellt „Aktuelles Schuljahr“ um und setzt „Anwesenheit/Thema erfasst ab“ und „2. Halbjahr ab“ des alten Jahres auf " +
          "Standard (danach neu eintragen, wenn nötig). Das geht auch vor dem Abschluss: Bis dahin bleibt das alte Jahr für " +
          "Nachträge offen.",
        "**Zuständigkeiten** gelten je Schuljahr: Die Klassenleitung der 05A von 2026/27 sieht die alte 05A (bis zum " +
          "Abschluss), aber nicht die neuen Fünftklässler, und betreut deren Schülerkonten nicht. Für das neue Jahr: Konten " +
          "verwalten → Konto → „Bearbeiten“ (Haken „für das aktuelle Schuljahr“). Vorschläge aus SchILD kommen mit dem " +
          "Jahreswechsel-Import.",
      ] },
      { zwischen: "Halbjahreswechsel" },
      "Die Karte erscheint **4 Wochen vor** „2. Halbjahr ab“ (Einstellungen → Schuljahr) und bleibt bis zum Ende des " +
        "Schuljahres. Sie prüft:",
      { liste: [
        "**Grundplan ab dem 2. Halbjahr:** Gibt es zum Halbjahr einen neuen Stundenplan, im Desktop „Grundplan " +
          "veröffentlichen…“ mit „gilt ab“ = erster Tag des 2. Halbjahres. Bleibt der Plan, ist der graue Hinweis in Ordnung.",
        "**SchILD-Import Abschnitt 2:** nach dem Halbjahreswechsel in SchILD im Desktop „SchILD-Daten…“ mit dem Export " +
          "des 2. Halbjahres, Abschnitt 2 und „Änderungen gelten ab“ = erster Tag des 2. Halbjahres. Die Vorschau warnt, " +
          "wenn Abschnitt und Datum nicht passen oder zum Halbjahr kein neuer Grundplan veröffentlicht ist.",
        "**Offene und nicht erfasste Stunden** im 1. Halbjahr – offene Fehlstunden kommen nicht in den Rückweg nach SchILD.",
        "**Rückweg nach SchILD** für das 1. Halbjahr – grün erst, wenn der Desktop eine vollständige Datei für das ganze " +
          "Halbjahr gemeldet hat (siehe „Rückweg nach SchILD“).",
        "**Sek-I-Gruppen ohne Liste** im Plan des 2. Halbjahres und **Gruppenlisten mit wegfallendem Schlüssel** – nur " +
          "Hinweise. Neue Gruppen stellen die Fachlehrkräfte unter „Meine Gruppen“ zusammen bzw. der SchILD-Import " +
          "liefert sie; alte Listen bleiben für die Stunden davor erhalten.",
      ] },
      "Die Seite nennt nur Zahlen, Daten und Bezeichnungen – keine Namen.",
    ],
  },
  {
    // P4b (5.3a, Entscheidungen 210–213)
    id: "rueckweg", gruppe: "admin", titel: "Rückweg nach SchILD",
    inhalt: [
      "Jahresablauf → Karte „Rückweg nach SchILD“ → **„Fehlstunden für SchILD bereitstellen…“**: Die Fehlstunden kommen " +
        "einzeln (Tag, Stunde, Fach, Lehrkraft, entschuldigt J/N) in die Datei SchuelerFehlstunden.dat, die SchILD " +
        "importieren kann.",
      { liste: [
        "**Zeitraum** wählen – Standard ist das laufende bzw. gerade beendete Halbjahr. Für eine Probedatei geht auch ein " +
          "kurzer Zeitraum.",
        "Die Zahlen prüfen: **offene** Fehlstunden, **Verspätungen** und **schulische Veranstaltungen** werden nicht " +
          "übertragen, **beurlaubte** Stunden zählen als entschuldigt. Offene Fehlstunden vorher bewerten.",
        "**„Abholcode erzeugen“** – der Code gilt 15 Minuten und für einen Abruf. Ein neuer Code ersetzt den alten.",
        "Im **Desktop**: Wocheneditor → „SchILD-Daten…“ → aktuellen SchILD-Export wählen und prüfen → Reiter **„Rückweg“** → " +
          "Code eingeben → „Abrufen“ → Vorschau prüfen → **„Datei schreiben…“**. Die Datei dann in SchILD von Hand " +
          "importieren.",
      ] },
      "Die App gibt **keine Namen** heraus – nur Prüfsummen. Namen, Geburtsdatum und Klasse ergänzt der Desktop aus dem " +
        "SchILD-Export. Abruf und geschriebene Datei stehen im Download-Protokoll.",
      "**Vor der ersten echten Übertragung:** SchILD-Datenbank sichern, eine Probedatei für eine Klasse und wenige Tage " +
        "importieren, in SchILD mit der Konferenzübersicht vergleichen und prüfen, ob ein zweiter Import desselben Zeitraums " +
        "doppelt zählt. Bis dahin ist die Fehlzeiten-CSV (Klassenbuch → Klassen) der sichere Weg.",
      "Den Zeitraum nicht zweimal übertragen: Die App und der Desktop warnen, wenn er sich mit einer früheren Übertragung " +
        "überschneidet.",
    ],
  },
  {
    id: "gruppenlisten", gruppe: "admin", titel: "Gruppenlisten",
    inhalt: [
      "Kontomenü → „Gruppenlisten“: alle Schülergruppen aus dem Plan an einem Ort.",
      "Normalerweise pflegt sie die Lehrkraft der Gruppe; du kannst jede Gruppe ändern.",
      "Gruppen mit **„aus SchILD“** kommen mit dem SchILD-Import und lassen sich hier nicht ändern – auch nicht von " +
        "dir. Änderungen über SchILD, danach neu importieren.",
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
        "Kursheft-PDFs, Fehlzeiten-CSV und Jahresarchive – mit Zeitraum und ob Schülereinträge enthalten waren. Dazu der " +
        "Rückweg nach SchILD: „abgeholt“ (der Desktop hat die Fehlstunden mit dem Abholcode geholt) und „Datei“ bzw. " +
        "„Probedatei“ (was er daraus geschrieben hat; „durch“ nennt, wer den Code erzeugt hat) und jede Auskunft nach " +
        "Art. 15 (ohne Namen, nur die Id).",
      "Filtern kannst du nach Art und nach Person (Teil des Benutzernamens).",
      "Gespeichert wird nur, was geladen wurde, nicht der Inhalt. Einträge werden nach der Aufbewahrungsfrist " +
        "gelöscht (Einstellungen → Datenschutz, Standard ein Jahr).",
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
  if (t.nurPersonal && !r.personal) return false;   // P6a
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
  if (nur === "bleiben") return state.bleibenTage > 0;       // P3
  if (nur === "ohneBleiben") return !(state.bleibenTage > 0);
  return true;
}

// ---------------------------------------------------------------------
//  Was ist gerade sichtbar? -> Thema, verwandte Themen, Kurzkarte
// ---------------------------------------------------------------------

const hiSichtbar = (id) => { const e = el(id); return !!e && !e.hidden; };

function hiKontext() {
  const r = hiRollen();
  if (hiSichtbar("anmeldung"))
    return { themen: hiSichtbar("codeForm") ? ["zweiterFaktor", "anmelden"] : ["anmelden", "startbildschirm"] };   // P6a
  if (hiSichtbar("pflicht")) return { themen: ["konto", "anmelden"] };
  if (hiSichtbar("faktorPflicht")) return { themen: ["zweiterFaktor", "konto"] };   // P6a
  if (hiSichtbar("einstellungen")) return { themen: ["einstellungen", "zugriffe", "konten", "zweiterFaktor", "downloads"] };   // P3, P6a, P6b
  if (hiSichtbar("jahresablauf")) return { themen: ["jahresablauf", "rueckweg", "einstellungen", "schildImporte"] };   // P4a/P4b
  if (hiSichtbar("verwaltung")) {
    if (r.admin && verw.reiter === "downloads") return { themen: ["downloads", "konten"] };
    if (r.admin && verw.reiter === "zugriffe") return { themen: ["zugriffe", "einstellungen", "auskunft", "konten"] };   // P6a, P6b
    if (r.admin && verw.reiter === "schild")
      return { themen: verw.schildTeil === "importe"
        ? ["schildImporte", "schildVorschlaege", "konten"] : ["schildVorschlaege", "schildImporte", "konten"] };
    return { themen: r.admin ? ["konten", "auskunft", "gruppenlisten", "downloads"] : ["schuelerkonten"] };   // P6b
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
    if (o.art === "schueler")   // P6b: Admin – „Auskunft (Art. 15) …“
      return { themen: ["bewerten", "meineKurse", "offeneFaelle", "eintraegeSchueler", ...(r.admin ? ["auskunft"] : []), "begriffe"],
        karte };
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
  // P3: Platzhalter aus den Einstellungen der Schule
  const tage = state.bleibenTage;
  const text = String(s).replace("{bleibenTage}", `${tage} ${tage === 1 ? "Tag" : "Tage"}`);
  return text.split("**").map((t, i) => (i % 2 ? h("strong", null, t) : t));
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
  // Nachtrag 5.2b-1: ohne verwandte Themen kein Text „null" am Ende.
  el("hiInhalt").replaceChildren(
    ...t.inhalt.map((b) => hiBaustein(b, r)).flat(Infinity).filter(Boolean),
    ...(andere.length ? [h("div", { class: "hi-dazu" },
      h("h3", { class: "hi-zwischen" }, "Passt dazu"),
      andere.map(hiThemaKnopf))] : []),
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
