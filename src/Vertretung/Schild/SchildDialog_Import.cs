#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>Eine Zeile der Vorschau im Reiter „Import“ (enthält Namen – bleibt im Fenster).</summary>
    public sealed class SchildImportZeile : INotifyPropertyChanged
    {
        private bool _bestaetigt;

        public SchildImportZeile(SchildVorschauZeileDto d, bool bestaetigt)
        {
            D = d;
            _bestaetigt = bestaetigt && Bestaetigbar;
        }

        public SchildVorschauZeileDto D { get; }

        /// <summary>Abgang laut SchILD bzw. fehlt im Export (80) und – seit 5.2c-1 – Paare lassen sich bestätigen.</summary>
        public bool Bestaetigbar => D.Id != null && (D.Kategorie == SchildDialog.KatAbgang || D.Kategorie == SchildDialog.KatFehlt
                                                     || IstPaar);

        /// <summary>5.2c-1: Paar „dieselbe Person?“ bzw. der Neu-Zuordnung (Häkchen = zusammenführen bzw. zuordnen).</summary>
        public bool IstPaar => D.Id != null && !string.IsNullOrEmpty(D.PartnerExtId)
                               && (D.Kategorie == SchildDialog.KatPaar || D.Kategorie == SchildDialog.KatZuordnung);
        public Visibility BestaetigenSichtbar => Bestaetigbar ? Visibility.Visible : Visibility.Collapsed;

        public bool Bestaetigt
        {
            get => _bestaetigt;
            set
            {
                if (!Bestaetigbar || _bestaetigt == value) return;
                _bestaetigt = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bestaetigt)));
                Geaendert?.Invoke();
            }
        }

        public string KategorieText => SchildDialog.KategorieText(D.Kategorie);
        public string Name => D.Name;
        public string Klasse => D.Klasse ?? "";
        public string Stufe => D.Stufe;
        public string Aenderung => D.Aenderung ?? "";
        public string HinweisText => string.Join(" · ", new[]
        {
            // 5.2c-2: Gruppen Namensgleicher (Zeilen untereinander) und der Vorschlag der API je Gruppe
            IstPaar && D.Gruppe is int g ? $"Gruppe {g}" + (D.Vorschlag ? " – Vorschlag" : "") : null,
            D.KontoWirdGesperrt ? "Konto wird bei Bestätigung gesperrt" : null,
            IstPaar && D.Vorausgewaehlt ? "vorausgewählt" : null,
            D.Hinweis
        }.Where(x => !string.IsNullOrEmpty(x)));

        /// <summary>5.2c-2: Hintergrund der Zeile – Gruppen Namensgleicher abwechselnd „a“/„b“, sonst leer (XAML: RowStyle).</summary>
        public string GruppeFarbe => IstPaar && D.Gruppe is int g ? (g % 2 == 1 ? "a" : "b") : "";

        /// <summary>Wird nach jeder Änderung der Bestätigung aufgerufen (Anzeige der Auswahl).</summary>
        public Action? Geaendert { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Phase 5.2b-2b: Reiter „Import“ im Fenster „SchILD-Daten prüfen und importieren“ (Entscheidungen 78–83, 166–171).
    /// Ablauf: Voraussetzungen (Prüflauf ohne Fehler, Zuordnung gespeichert) → „Vorschau berechnen“ (Status der API, Art der
    /// Daten passt zur API, Schlüssel, Nutzlast, Probelauf) → Abgänge bestätigen → „Importieren…“ (Rückfrage mit Zahlen,
    /// beim ersten Import wird der Schlüssel gespeichert). Dazu „Schlüssel sichern…/einlesen…“ und – nur Entwicklung –
    /// „Seed-Schüler entfernen…“. Bericht, Log und Meldungen nennen keine Namen; die Namen der Vorschau bleiben im Fenster.
    /// </summary>
    public partial class SchildDialog
    {
        internal const string KatAbgang = "abgangLautSchild";
        internal const string KatFehlt = "fehltImExport";
        internal const string KatPaar = "dieselbePerson";        // 5.2c-1
        internal const string KatZuordnung = "zuordnung";        // 5.2c-1 (Neu-Zuordnung)
        private static readonly string[] KatReihenfolge =
        {
            KatAbgang, KatFehlt, KatPaar, KatZuordnung, "neu", "zusammengefuehrt", "nameGeaendert", "klassenwechsel",
            "kurswechsel", "statusGeaendert"
        };

        private SchildStatusAntwort? _imStatus;
        private SchildSchluessel? _imSchluessel;
        private bool? _imSchluesselArt;          // für welche Art (true = Pseudo) _imSchluessel geladen wurde
        private string? _imSchluesselFehler;
        private SchildNutzlastErgebnis? _imBau;
        private SchildImportNutzlast? _imNutzlast;   // Grundlage der aktuellen Vorschau (enthält Namen)
        private SchildVorschauAntwort? _imVorschau;
        private readonly List<SchildImportZeile> _imZeilen = new();
        private string? _imBericht;                  // ohne Namen
        private string? _imLokaleSperre;             // Grund, aus dem der Desktop nichts sendet
        private int? _imAusgefuehrt;                 // Protokoll-Nr. des gerade ausgeführten Imports
        private bool _imDatumSetzen, _imDatumVonHand, _imAnsichtSetzen;
        // 5.2c-1: Paare geändert (neue Vorschau nötig), Neu-Zuordnung (Schlüssel verloren) mit eigenem Schlüssel
        private bool _imPaareGeaendert;
        private bool _imNeuzuordnung;
        private SchildSchluessel? _imNeuSchluessel;

        private sealed class ImAnsicht
        {
            public string Kategorie { get; init; } = "";
            public string Text { get; init; } = "";
            public override string ToString() => Text;
        }

        private static readonly Color FarbeNeutral = Color.FromRgb(0xEE, 0xEE, 0xEE);
        private static readonly Color FarbeGelb = Color.FromRgb(0xFF, 0xF1, 0xCC);
        private static readonly Color FarbeRot = Color.FromRgb(0xFD, 0xE2, 0xE1);
        private static readonly Color FarbeGruen = Color.FromRgb(0xDC, 0xEF, 0xD9);

        private const string ImportHilfe =
            "So geht der Import\n\n" +
            "1. Prüflauf ohne Fehler, Zuordnung geprüft und gespeichert (Reiter „Zuordnung“). Offene Zuordnungen, Vorschläge, " +
            "Konflikte und Zeitkonflikte werden nicht übertragen – die Vorschau nennt sie.\n\n" +
            "2. „Vorschau berechnen“ fragt die API nach ihrem Stand und schickt die Schüler als Probelauf – gespeichert wird noch " +
            "nichts. Gesendet werden Name, Vorname, Jahrgang, Klasse, SchILD-Status, bei Neuaufnahmen das Aufnahmedatum und die " +
            "Plan-Schlüssel der zugeordneten Kurse und Gruppen. Statt Geburtsdatum und SchILD-ID gehen nur Prüfsummen an den " +
            "Server; Noten und Kursarten nie. Von den Lehrkräften gehen nur die Klassenleitung je Klasse (Kürzel, Klassen.dat) " +
            "und Kürzel, Nachname und Vorname der Lehrkräfte des Plans (Lehrkraefte.dat) an den Server – übersetzt über die " +
            "Kürzeltabelle der Zuordnung; die Namen sieht in der App nur die Verwaltung und die Lehrkraft selbst.\n\n" +
            "3. „Änderungen gelten ab“: Ab diesem Tag gelten Klassen-, Kurs- und Gruppenwechsel und Abgänge; Stunden davor " +
            "behalten ihre Listen. Den Vorschlag macht die API (erster Import eines Abschnitts: dessen Beginn, sonst heute – nie " +
            "vor dem letzten Import).\n\n" +
            "4. Vorschau prüfen. „Abgang laut SchILD“ ist vorausgewählt, „fehlt im Export“ nicht. Bestätigte Abgänge beenden alle " +
            "Zugehörigkeiten am Vortag und sperren das Schülerkonto (der Admin kann es entsperren). Nicht bestätigte bleiben " +
            "unverändert.\n\n" +
            "5. „Importieren…“ führt genau diese Vorschau aus. Hat sich inzwischen etwas geändert (Datenbank oder Dateien), kommt " +
            "eine neue Vorschau.\n\n" +
            "Schlüssel: Die Prüfsummen entstehen mit einem Schlüssel, der nur auf diesem Rechner liegt – für diesen " +
            "Windows-Benutzer unter …\\AppData\\Local\\Stundenplan (nicht in OneDrive). Er wird beim ersten Import gespeichert. " +
            "Ohne ihn erkennt der nächste Import die Schüler nicht wieder. Deshalb nach dem ersten echten Import „Schlüssel " +
            "sichern…“: Datei mit Kennwort auf einen USB-Stick bzw. in den Tresor, das Kennwort getrennt davon. Auf einem neuen " +
            "Rechner oder für einen anderen Windows-Benutzer „Schlüssel einlesen…“. Pseudodaten haben einen eigenen Schlüssel.\n\n" +
            "Wohin? Die Entwicklungs-API nimmt nur Pseudodaten, der Echtbetrieb nur echte Daten; echte Daten gehen nur über " +
            "HTTPS. Server-Adresse und API-Key: „Online-Einstellungen…“ (dieselben wie für „Online veröffentlichen“). In der " +
            "Entwicklung sperrt die Vorschau, solange Schüler aus dem Seed in der Datenbank stehen – „Seed-Schüler entfernen…“ " +
            "löscht sie samt Klassenbuchdaten.\n\n" +
            "„Bericht kopieren“ enthält nur Zahlen, Kurs- und Gruppenbezeichnungen – keine Namen. Die Namen der Vorschau bleiben " +
            "in diesem Fenster.\n\n" +
            "„Dieselbe Person?“: Ein Schüler im Export ohne Treffer und ein Schüler der Datenbank, der im Export fehlt, bilden " +
            "ein Paar, wenn Vorname + Geburtsdatum oder Nachname + Geburtsdatum gleich sind (Namensänderung, korrigierter " +
            "Vorname) oder der Name in derselben Klasse gleich ist (korrigiertes Geburtsdatum). Vorausgewählt sind nur sichere " +
            "Paare. Häkchen = zusammenführen: Der bisherige Datensatz bekommt Name und Prüfsummen aus dem Export, Konto und " +
            "Klassenbuchdaten bleiben. Nach einer Änderung der Häkchen rechnet „Vorschau berechnen“ neu. Mit SchILD-ID " +
            "(SchuelerLernplattformen.dat) erkennt der Import Namensänderungen ohne Paare.\n\n" +
            "Schlüssel verloren: Nur wenn es keine Sicherung gibt, „Schlüssel verloren – neu zuordnen…“. Dann ordnet die API die " +
            "bisherigen Schüler über Name + Klasse zu (eindeutige vorausgewählt, Namensgleiche und andere Klassen nur von Hand) " +
            "und gibt ihnen die Prüfsummen des neuen Schlüssels. Namensgleiche stehen als Gruppe untereinander; „Vorschlag " +
            "übernehmen…“ hakt je Gruppe die Paare mit passendem Eintritt bzw. den meisten gemeinsamen Kursen an (nur, wo der " +
            "Vorschlag eindeutig ist) – bitte prüfen, dann „Vorschau berechnen“. Danach sofort „Schlüssel sichern…“.\n\n" +
            "Testen mit Pseudodaten: Nach dem Import eines Pseudo-Exports macht „Folgestand erzeugen…“ (unten) daraus einen " +
            "neuen Ordner mit typischen Änderungen bis zum nächsten Export. Dann wie bei einem neuen Export: Prüflauf, Vorschau, " +
            "Abgänge bestätigen, Importieren. Das Fenster nennt die Zahlen, die die Vorschau zeigen sollte.";

        internal static string KategorieText(string kategorie) => kategorie switch
        {
            KatAbgang => "Abgang laut SchILD",
            KatFehlt => "fehlt im Export",
            "neu" => "neu",
            "nameGeaendert" => "Name/Geburtsdatum geändert",
            "klassenwechsel" => "Klassenwechsel",
            "kurswechsel" => "Kurs-/Gruppenwechsel",
            "statusGeaendert" => "Status/Jahrgang geändert",
            KatPaar => "dieselbe Person?",
            KatZuordnung => "Neu-Zuordnung",
            "zusammengefuehrt" => "zusammengeführt",
            _ => kategorie
        };

        private static string Dat(DateOnly d) => SchildText.DatumText(d);

        /// <summary>Schuljahr wie im Wocheneditor (so heißt der Kalender in der API), sonst aus dem Abschnitt.</summary>
        private string ImSchuljahr()
        {
            if (!string.IsNullOrWhiteSpace(_schuljahr)) return _schuljahr.Trim();
            int jahr = _ergebnis?.Jahr ?? SchildGenerator.Jahr(_schuljahr);
            return $"{jahr}/{(jahr + 1) % 100:00}";
        }

        // =====================================================
        //  Zustand und Anzeige
        // =====================================================

        /// <summary>null = alles bereit; sonst der Grund, warum (noch) nicht importiert werden kann.</summary>
        private string? ImVoraussetzung()
        {
            if (_export == null || _ergebnis == null) return "Zuerst einen Ordner wählen und den Prüflauf starten.";
            int f = _ergebnis.Anzahl(SchildProblemArt.Fehler);
            if (f > 0) return $"Der Prüflauf hat {f} Fehler (Reiter „Probleme“) – erst beheben, dann importieren.";
            if (_planInfo == null)
                return "Kein Plan verfügbar – der Import braucht den Grundplan der im Wocheneditor gewählten Plan-Spalte.";
            if (_planInfo.Hinweis != null) return "Zuordnung nicht möglich: " + _planInfo.Hinweis;
            var zu = _ergebnis.Zuordnung;
            if (zu == null || !zu.Moeglich) return "Ohne Zuordnung SchILD ↔ Plan ist kein Import möglich (Reiter „Zuordnung“).";
            if (_zuordnungGeaendert)
                return "Die Zuordnung hat Festlegungen, die noch nicht gespeichert sind – erst „Zuordnungen speichern“ (Reiter " +
                       "„Zuordnung“).";
            if (!string.IsNullOrWhiteSpace(_schuljahr) && _ergebnis.Jahr != SchildGenerator.Jahr(_schuljahr))
                return $"Der gewählte Abschnitt gehört zum Schuljahr {_ergebnis.Jahr}/{(_ergebnis.Jahr + 1) % 100:00}, der " +
                       $"Wocheneditor arbeitet mit {_schuljahr}.";
            return null;
        }

        private void ImBand(string text, Color farbe)
        {
            TxtImHinweis.Text = text;
            BrdImHinweis.Background = new SolidColorBrush(farbe);
        }

        /// <summary>Band, Knöpfe, Schlüssel und Auswahl neu anzeigen (ohne Netz).</summary>
        private void ImAnzeigen()
        {
            if (TxtImHinweis == null || BtnImVorschau == null) return;
            string? vor = ImVoraussetzung();
            bool an = !_laeuft;

            if (vor != null)
            {
                ImBand(vor, FarbeNeutral);
                TxtImportKopf.Text = "Import";
            }
            else if (_imLokaleSperre != null)
            {
                ImBand("Nicht gesendet: " + _imLokaleSperre, FarbeRot);
                TxtImportKopf.Text = "Import (gesperrt)";
            }
            else if (_imAusgefuehrt is int nr)
            {
                ImBand($"Import ausgeführt (Protokoll Nr. {nr}). Für einen weiteren Import (z. B. nach einem neuen Export) wieder " +
                       "mit „Vorschau berechnen“ beginnen.", FarbeGruen);
                TxtImportKopf.Text = "Import ✓";
            }
            else if (_imVorschau != null && _imVorschau.Gesperrt)
            {
                ImBand("Import gesperrt:\n• " + string.Join("\n• ", _imVorschau.Sperren.Select(s => s.Text)), FarbeRot);
                TxtImportKopf.Text = "Import (gesperrt)";
            }
            else if (_imVorschau != null && _imPaareGeaendert)
            {
                ImBand("Häkchen bei „" + (_imNeuzuordnung ? "Neu-Zuordnung" : "dieselbe Person?") + "“ geändert – die Wahl " +
                       "ändert Wechsel und Zahlen. Bitte „Vorschau berechnen“ (die Häkchen bleiben), erst danach „Importieren…“.",
                       FarbeGelb);
                TxtImportKopf.Text = "Import (Vorschau neu berechnen)";
            }
            else if (_imVorschau != null)
            {
                // P4a (206): Warnungen zum Halbjahr (Abschnitt, Stichtag, Grundplan) – keine Sperre, Rückfrage beim Import
                string warnung = _imVorschau.Warnungen.Count > 0
                    ? "ACHTUNG (Halbjahr):\n• " + string.Join("\n• ", _imVorschau.Warnungen) + "\n\n" : "";
                ImBand(warnung + (_imNeuzuordnung ? "NEU-ZUORDNUNG über Name + Klasse (Schlüssel verloren). " : "") +
                       "Vorschau bereit – nichts ist gespeichert. Zeilen prüfen, Abgänge bestätigen" +
                       (_imZeilen.Any(x => x.IstPaar) ? ", Paare prüfen" : "") + ", dann „Importieren…“.", FarbeGelb);
                TxtImportKopf.Text = _imNeuzuordnung ? "Import (Neu-Zuordnung)" : "Import (Vorschau)";
            }
            else
            {
                var ex = _export!;
                var erg = _ergebnis!;
                int n = SchildNutzlast.Schueler(ex, erg.Jahr, erg.Abschnitt, out _).Count;
                var offen = SchildNutzlast.NichtUebertragbar(erg.Zuordnung!);
                ImBand($"Bereit: {n} Schüler ({SchildSchluessel.ArtText(ex.IstPseudo)}) aus {erg.Jahr}/{(erg.Jahr + 1) % 100:00}, " +
                       $"Abschnitt {erg.Abschnitt}, Plan „{_planInfo?.Bezeichnung}“. " +
                       (offen.Count > 0
                           ? $"{offen.Count} SchILD-Kurse/Gruppen mit offener Zuordnung werden nicht übertragen ({offen.Sum(u => u.Teilnehmer)} " +
                             "Teilnahmen). "
                           : "Alle Zuordnungen stehen fest. ") +
                       "„Vorschau berechnen“ fragt zuerst den Status der API ab und schickt dann einen Probelauf – gespeichert wird " +
                       "dabei nichts.", FarbeNeutral);
                TxtImportKopf.Text = "Import";
            }

            BtnImVorschau.IsEnabled = an && vor == null;
            BtnImImportieren.IsEnabled = an && vor == null && _imLokaleSperre == null && _imAusgefuehrt == null
                                         && _imVorschau != null && !_imVorschau.Gesperrt && _imNutzlast != null
                                         && !_imPaareGeaendert;
            BtnImBericht.IsEnabled = an && _imBericht != null;
            BtnImStatus.IsEnabled = an;
            BtnImEinstellungen.IsEnabled = an;
            BtnImEinlesen.IsEnabled = an;
            bool pseudo = _export?.IstPseudo ?? false;
            BtnImSichern.IsEnabled = an && _export != null && SchildSchluessel.Vorhanden(pseudo);
            DpImGiltAb.IsEnabled = an && vor == null;
            var st = _imStatus;
            BtnImSeed.Visibility = st != null && st.Pseudo && st.Schueler.Seed + st.Schueler.PseudoImportiert > 0
                ? Visibility.Visible : Visibility.Collapsed;
            BtnImSeed.IsEnabled = an;
            bool bestaetigbar = DgImport.ItemsSource is IEnumerable<SchildImportZeile> sichtbar && sichtbar.Any(x => x.Bestaetigbar);
            BtnImAlle.IsEnabled = an && _imAusgefuehrt == null && bestaetigbar;
            BtnImKeine.IsEnabled = an && _imAusgefuehrt == null && bestaetigbar;
            // 5.2c-2: „Vorschlag übernehmen…“, sobald es Gruppen Namensgleicher mit einem Vorschlag der API gibt
            bool vorschlag = _imVorschau != null && _imAusgefuehrt == null
                             && _imZeilen.Any(x => x.IstPaar && x.D.Gruppe != null && x.D.Vorschlag);
            BtnImVorschlag.Visibility = vorschlag ? Visibility.Visible : Visibility.Collapsed;
            BtnImVorschlag.IsEnabled = an && vorschlag;
            // 5.2c-1: „Schlüssel verloren – neu zuordnen…“ nur, wenn importierte Schüler da sind und der Schlüssel fehlt, nicht
            // lesbar ist oder nicht zum letzten Import passt – bzw. während einer Neu-Zuordnung („… abbrechen“).
            if (_export != null && !_imNeuzuordnung) ImSchluesselLaden(_export.IstPseudo);
            bool neuMoeglich = st != null && _export != null && st.Pseudo == _export.IstPseudo && st.Schueler.Importiert > 0
                               && (_imSchluessel == null || _imSchluessel.Neu
                                   || (st.KennungLetzterImport != null && _imSchluessel.Kennung != st.KennungLetzterImport));
            BtnImNeu.Visibility = _imNeuzuordnung || neuMoeglich ? Visibility.Visible : Visibility.Collapsed;
            BtnImNeu.Content = _imNeuzuordnung ? "Neu-Zuordnung abbrechen" : "Schlüssel verloren – neu zuordnen…";
            BtnImNeu.IsEnabled = an && (_imNeuzuordnung || vor == null);
            ImZeigeSchluessel();
            ImZeigeAuswahl();
            FolgeAnzeigen();   // 5.2b-2b-2: „Folgestand erzeugen…“ (gleiche Anlässe: Prüflauf, Zuordnung, laufender Vorgang)
        }

        private void ImZeigeSchluessel()
        {
            if (_export == null) { TxtImSchluessel.Text = ""; return; }
            bool pseudo = _export.IstPseudo;
            ImSchluesselLaden(pseudo);
            string art = SchildSchluessel.ArtText(pseudo);
            var k = _imSchluessel;
            TxtImSchluessel.Foreground = Brushes.Black;
            if (k != null)
                TxtImSchluessel.Text = k.Neu
                    ? $"Schlüssel ({art}): neu, Kennung {k.Kennung} – wird beim ersten Import gespeichert."
                    : $"Schlüssel ({art}): Kennung {k.Kennung}.";
            else if (_imSchluesselFehler != null && SchildSchluessel.Vorhanden(pseudo))
            {
                TxtImSchluessel.Text = $"Schlüssel ({art}): nicht lesbar – „Schlüssel einlesen…“.";
                TxtImSchluessel.Foreground = Brushes.Firebrick;
            }
            else TxtImSchluessel.Text = $"Schlüssel ({art}): noch keiner – entsteht beim ersten Import.";
        }

        private void ImZeigeAuswahl()
        {
            int kandidaten = _imZeilen.Count(x => x.Bestaetigbar && !x.IstPaar);
            int paare = _imZeilen.Count(x => x.IstPaar);
            if (kandidaten + paare == 0 || _imAusgefuehrt != null) { TxtImAuswahl.Text = ""; return; }
            var gewaehlt = _imZeilen.Where(x => x.Bestaetigt && !x.IstPaar).ToList();
            int konten = gewaehlt.Count(x => x.D.KontoWirdGesperrt);
            var t = new StringBuilder();
            if (kandidaten > 0)
                t.Append($"Abgänge bestätigt: {gewaehlt.Count} von {kandidaten} (Schülerkonten, die gesperrt werden: {konten}). " +
                         "Nicht bestätigte bleiben unverändert in ihren Klassen und Kursen.");
            if (paare > 0)
                t.Append((t.Length > 0 ? " " : "") +
                         $"Paare ({(_imNeuzuordnung ? "zuordnen" : "zusammenführen")}): {_imZeilen.Count(x => x.IstPaar && x.Bestaetigt)} " +
                         $"von {paare} angehakt" + (_imPaareGeaendert ? " – geändert, bitte „Vorschau berechnen“." : "."));
            TxtImAuswahl.Text = t.ToString();
        }

        /// <summary>5.2c-1: ein Häkchen bei einem Paar wurde geändert – die Vorschau muss neu berechnet werden.</summary>
        private void ImPaarGeaendert()
        {
            _imPaareGeaendert = true;
            ImAnzeigen();
        }

        /// <summary>5.2c-1: gewählte Paare der aktuellen Vorschau (null = keine Paare in der Vorschau → Vorauswahl der API).</summary>
        private List<SchildPaar>? ImPaarWahl()
        {
            if (_imVorschau == null || !_imZeilen.Any(x => x.IstPaar)) return null;
            return _imZeilen.Where(x => x.IstPaar && x.Bestaetigt)
                            .Select(x => new SchildPaar { Id = x.D.Id!.Value, ExtId = x.D.PartnerExtId! }).ToList();
        }

        /// <summary>5.2c-1: Neu-Zuordnung beenden (neuen, nicht gespeicherten Schlüssel im Speicher löschen).</summary>
        private void ImNeuzuordnungBeenden()
        {
            if (_imNeuSchluessel != null && _imNeuSchluessel.Neu)
            {
                if (ReferenceEquals(_imSchluessel, _imNeuSchluessel)) _imSchluessel = null;
                _imNeuSchluessel.Vergessen();
            }
            _imNeuSchluessel = null;
            _imNeuzuordnung = false;
            _imSchluesselArt = null;
        }

        private void ImZeigeStatus(SchildStatusAntwort st, string adresse)
        {
            var t = new StringBuilder($"Server {adresse}: {st.Betrieb} ({st.Nimmt}) · Schuljahr {st.Schuljahr}");
            t.Append(st.Kalender != null ? $" ({Dat(st.Kalender.Start)} – {Dat(st.Kalender.Ende)})" : " – kein Kalender");
            if (st.Grundplaene.Count > 0)
                t.Append(" · Grundpläne: " + string.Join(", ", st.Grundplaene.Select(g => $"{g.Bezeichnung} ab {Dat(g.GueltigAb)}")));
            else t.Append(" · kein Grundplan");
            t.Append($" · Schüler: echt {st.Schueler.Echt}, Seed {st.Schueler.Seed}, importiert (Pseudo) {st.Schueler.PseudoImportiert}");
            if (st.LetzterImport is SchildLetzterImportDto li)
            {
                DateTime zeit = li.Zeit.Kind == DateTimeKind.Local ? li.Zeit : li.Zeit.ToLocalTime();
                t.Append($" · letzter Import: Nr. {li.Id} vom {zeit:dd.MM.yyyy HH:mm}, gilt ab {Dat(li.GiltAb)}");
            }
            else t.Append(" · noch kein Import");
            if (st.KennungLetzterImport != null) t.Append($" · Schlüssel beim letzten Import: Kennung {st.KennungLetzterImport}");
            TxtImServer.Text = t.ToString();
        }

        private void ImZeigeGiltAb(SchildStatusAntwort st)
        {
            var teile = new List<string>();
            if (st.VorschlagGiltAb is DateOnly v) teile.Add($"Vorschlag der API: {Dat(v)}");
            if (st.FruehestensAb is DateOnly f) teile.Add($"frühestens {Dat(f)} (letzter Import)");
            if (st.Kalender != null) teile.Add($"im Schuljahr {Dat(st.Kalender.Start)} – {Dat(st.Kalender.Ende)}");
            TxtImGiltAb.Text = string.Join(" · ", teile);
        }

        /// <summary>Setzt „Änderungen gelten ab“ auf den Vorschlag der API, solange es nicht von Hand gewählt wurde.</summary>
        private DateOnly ImGiltAbSetzen(SchildStatusAntwort st)
        {
            if ((DpImGiltAb.SelectedDate == null || !_imDatumVonHand) && st.VorschlagGiltAb is DateOnly v)
            {
                _imDatumSetzen = true;
                try { DpImGiltAb.SelectedDate = v.ToDateTime(TimeOnly.MinValue); }
                finally { _imDatumSetzen = false; }
            }
            ImZeigeGiltAb(st);
            return DpImGiltAb.SelectedDate is DateTime d ? DateOnly.FromDateTime(d)
                 : st.VorschlagGiltAb ?? DateOnly.FromDateTime(DateTime.Today);
        }

        /// <summary>Verwirft die Vorschau (nach neuem Prüflauf, neuer Festlegung, anderem Datum, anderem Schlüssel …).</summary>
        private void ImVorschauVerwerfen(string? grund)
        {
            foreach (var alt in _imZeilen) alt.Geaendert = null;
            _imZeilen.Clear();
            if (DgImport != null) DgImport.ItemsSource = null;
            if (CbImAnsicht != null)
            {
                _imAnsichtSetzen = true;
                CbImAnsicht.Items.Clear();
                _imAnsichtSetzen = false;
            }
            if (TxtImAnzahl != null) TxtImAnzahl.Text = "";
            _imVorschau = null;
            _imNutzlast = null;
            _imBau = null;
            _imBericht = null;
            _imLokaleSperre = null;
            _imAusgefuehrt = null;
            _imPaareGeaendert = false;
            if (TxtImBericht != null) TxtImBericht.Text = (grund != null ? grund + "\n\n" : "") + ImportHilfe;
            ImAnzeigen();
        }

        /// <summary>Nach einem neuen Prüflauf: Datum wieder vom Vorschlag der API, Schlüssel passend zur Art der Daten.</summary>
        private void ImNeuerExport()
        {
            _imDatumVonHand = false;
            ImNeuzuordnungBeenden();   // 5.2c-1: eine Neu-Zuordnung gilt nur für den Export, mit dem sie begonnen wurde
            _imSchluesselArt = null;
        }

        /// <summary>Desktop-Sperre: es wird nichts gesendet.</summary>
        private void ImSperre(string text)
        {
            ImVorschauVerwerfen(null);
            _imLokaleSperre = text;
            TxtImBericht.Text = "Nicht gesendet:\n" + text + "\n\n" + ImportHilfe;
            ImAnzeigen();
            MessageBox.Show(this, text, "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ImStart(string text)
        {
            _laeuft = true;
            Mouse.OverrideCursor = Cursors.Wait;
            SetzeKnoepfe(false);
            TxtStatus.Text = text;
        }

        private void ImEnde()
        {
            _laeuft = false;
            Mouse.OverrideCursor = null;
            SetzeKnoepfe(true);
            if (TxtStatus.Text.EndsWith("…", StringComparison.Ordinal)) TxtStatus.Text = "";
        }

        /// <summary>Lädt den gespeicherten Schlüssel der Art (einmal je Art; erzwingen = neu lesen).</summary>
        private void ImSchluesselLaden(bool pseudo, bool erzwingen = false)
        {
            if (!erzwingen && _imSchluesselArt == pseudo) return;
            // 5.2c-1: der (noch nicht gespeicherte) Schlüssel einer Neu-Zuordnung bleibt im Speicher.
            if (!ReferenceEquals(_imSchluessel, _imNeuSchluessel)) _imSchluessel?.Vergessen();
            _imSchluessel = null;
            _imSchluesselFehler = null;
            _imSchluesselArt = pseudo;
            if (SchildSchluessel.Vorhanden(pseudo)) _imSchluessel = SchildSchluessel.Laden(pseudo, out _imSchluesselFehler);
        }

        private SchildImportClient? ImClient()
        {
            var einst = OnlineSync.LadeEinstellungen(_planOrdner);
            if (string.IsNullOrWhiteSpace(einst.BaseUrl))
            {
                var neu = OnlineSync.FrageEinstellungen(this, einst, _planOrdner);
                if (neu == null) return null;
                try { OnlineSync.SpeichereEinstellungen(_planOrdner, neu); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Die Online-Einstellungen konnten nicht gespeichert werden:\n\n" + ex.Message,
                        "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                einst = neu;
            }
            var client = new SchildImportClient(einst, _planOrdner);
            if (client.AdresseUri == null)
            {
                MessageBox.Show(this, $"Die Server-Adresse „{client.Adresse}“ ist ungültig (Beispiel: https://klassenbuch.schule.de " +
                                      "bzw. in der Entwicklung http://localhost:5080).\n\nEinstellungen: " +
                                      OnlineSync.EinstellungsPfad(_planOrdner),
                    "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return client;
        }

        // =====================================================
        //  Knöpfe: Status, Einstellungen, Datum, Ansicht
        // =====================================================

        private async void BtnImStatus_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft) return;
            var client = ImClient();
            if (client == null) return;
            ImStart("Frage den Status der API ab …");
            try
            {
                var st = await client.StatusAsync(ImSchuljahr(), _ergebnis?.Abschnitt ?? 1);
                _imStatus = st;
                ImZeigeStatus(st, client.Adresse);
                ImGiltAbSetzen(st);
            }
            catch (SchildServerFehler ex)
            {
                TxtImServer.Text = $"Server {client.Adresse}: keine Antwort bzw. Fehler.";
                MessageBox.Show(this, ex.Message, "SchILD-Import – Status", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { ImEnde(); }
        }

        private void BtnImEinstellungen_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft) return;
            var neu = OnlineSync.FrageEinstellungen(this, OnlineSync.LadeEinstellungen(_planOrdner), _planOrdner);
            if (neu == null) return;
            try { OnlineSync.SpeichereEinstellungen(_planOrdner, neu); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Speichern fehlgeschlagen:\n\n" + ex.Message, "Online-Einstellungen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _imStatus = null;
            TxtImServer.Text = "Server: noch nicht abgefragt.";
            ImVorschauVerwerfen("Online-Einstellungen geändert – bitte die Vorschau neu berechnen.");
        }

        private void DpImGiltAb_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_imDatumSetzen) return;
            _imDatumVonHand = DpImGiltAb.SelectedDate != null;
            if (_imNutzlast != null && (DpImGiltAb.SelectedDate is not DateTime d || DateOnly.FromDateTime(d) != _imNutzlast.GiltAb))
                ImVorschauVerwerfen("„Änderungen gelten ab“ geändert – bitte die Vorschau neu berechnen.");
            else ImAnzeigen();
        }

        private void CbImAnsicht_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_imAnsichtSetzen) return;
            ImFuelleTabelle();
        }

        private void ImFuelleTabelle()
        {
            string kat = (CbImAnsicht.SelectedItem as ImAnsicht)?.Kategorie ?? "";
            var liste = _imZeilen.Where(x => kat.Length == 0 || x.D.Kategorie == kat).ToList();
            DgImport.ItemsSource = liste;
            TxtImAnzahl.Text = liste.Count == 0 ? "keine Zeilen" : $"{liste.Count} Zeile(n)";
            ImAnzeigen();
        }

        private void BtnImAlle_Click(object sender, RoutedEventArgs e) => ImBestaetigeSichtbare(true);

        private void BtnImKeine_Click(object sender, RoutedEventArgs e) => ImBestaetigeSichtbare(false);

        private void ImBestaetigeSichtbare(bool wert)
        {
            if (DgImport.ItemsSource is not IEnumerable<SchildImportZeile> sichtbar) return;
            foreach (var zeile in sichtbar.Where(y => y.Bestaetigbar)) zeile.Bestaetigt = wert;
            ImZeigeAuswahl();
        }

        private void BtnImBericht_Click(object sender, RoutedEventArgs e)
        {
            if (_imBericht == null) return;
            try
            {
                Clipboard.SetText(_imBericht);
                TxtStatus.Text = "Bericht in die Zwischenablage kopiert (ohne Namen).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Zwischenablage nicht verfügbar:\n\n" + ex.Message, "Bericht",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // =====================================================
        //  Vorschau
        // =====================================================

        private async void BtnImVorschau_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft) return;
            string? vor = ImVoraussetzung();
            if (vor != null)
            {
                MessageBox.Show(this, vor, "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var client = ImClient();
            if (client == null) return;
            // 5.2c-1: Häkchen der Paare und Bestätigungen der Abgänge aus der bisherigen Vorschau mitnehmen.
            var paare = ImPaarWahl();
            var behalten = ImBestaetigungen();
            ImStart("Frage den Status der API ab …");
            string? sperre = null;
            try
            {
                sperre = await ImVorschauAsync(client, paare, behalten);
            }
            catch (SchildServerFehler ex)
            {
                MessageBox.Show(this, ex.Message, "SchILD-Import – Vorschau", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Die Vorschau ist fehlgeschlagen:\n\n" + ex.Message, "SchILD-Import",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { ImEnde(); }
            if (sperre != null) ImSperre(sperre);
        }

        /// <summary>Bestätigungen der Abgänge (Kategorie|Id → an/aus) der aktuellen Vorschau – ohne Paare.</summary>
        private Dictionary<string, bool>? ImBestaetigungen()
        {
            if (_imVorschau == null) return null;
            var d = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var zeile in _imZeilen.Where(x => x.Bestaetigbar && !x.IstPaar && x.D.Id != null))
                d[$"{zeile.D.Kategorie}|{zeile.D.Id}"] = zeile.Bestaetigt;
            return d;
        }

        /// <param name="paare">gewählte Paare (null = Vorauswahl der API)</param>
        /// <param name="behalten">Bestätigungen der Abgänge aus der bisherigen Vorschau</param>
        /// <returns>null = Vorschau angezeigt (auch mit Sperren der API); sonst der Grund, aus dem der Desktop nichts sendet.</returns>
        private async Task<string?> ImVorschauAsync(SchildImportClient client, List<SchildPaar>? paare = null,
            Dictionary<string, bool>? behalten = null)
        {
            var ex = _export!;
            var erg = _ergebnis!;
            var plan = _planInfo!;
            bool pseudo = ex.IstPseudo;
            ImVorschauVerwerfen(null);

            // Echte Schülerdaten nur über HTTPS (außer an diesen Rechner).
            var uri = client.AdresseUri!;
            if (!pseudo && uri.Scheme != Uri.UriSchemeHttps && !OnlineVerbindung.IstLokal(uri))
                return $"Echte Schülerdaten werden nur über HTTPS gesendet – die Server-Adresse „{client.Adresse}“ beginnt mit " +
                       "http://. Bitte die https://-Adresse des Servers in die Online-Einstellungen eintragen.";

            // 1. Status
            var st = await client.StatusAsync(ImSchuljahr(), erg.Abschnitt);
            _imStatus = st;
            ImZeigeStatus(st, client.Adresse);

            // 2. Art der Daten passt zur API (83) – sonst gehen keine Namen hinaus.
            if (st.Pseudo && !pseudo)
                return "Die API läuft in der Entwicklung und nimmt nur Pseudodaten an (Entscheidung 83). Echte SchILD-Daten " +
                       "werden deshalb nicht gesendet – nur an den Server im Echtbetrieb.";
            if (!st.Pseudo && pseudo)
                return "Die API läuft im Echtbetrieb und nimmt keine Pseudodaten an (Entscheidung 83). Pseudodaten nur an die " +
                       "Entwicklungs-API senden.";
            if (st.Kalender == null)
                return $"Für das Schuljahr „{ImSchuljahr()}“ gibt es in der API keinen Kalender" +
                       (st.Schuljahre.Count > 0 ? $" (vorhanden: {string.Join(", ", st.Schuljahre)})" : "") +
                       " – zuerst im Wocheneditor „Grundplan veröffentlichen…“ (ab Schuljahresbeginn).";

            // 3. „Änderungen gelten ab“
            DateOnly giltAb = ImGiltAbSetzen(st);

            // 3a. P3 (Kleinigkeit c, Entscheidung 199): Passt die im Wocheneditor gewählte Plan-Spalte zum Grundplan, der am
            //     Stichtag in der App gilt? Sonst rechnen Zuordnung und Vorschau mit den Kursen eines anderen Plans.
            string? planWarnung = ImPlanWarnung(st, plan.Bezeichnung, giltAb);
            if (planWarnung != null &&
                MessageBox.Show(this, planWarnung + "\n\nTrotzdem berechnen?", "SchILD-Import – Plan", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                ImVorschauVerwerfen("Nicht berechnet: " + planWarnung);
                return null;
            }

            // 4. Schlüssel (170): vorhandenen nehmen; neu nur, wenn noch niemand importiert ist.
            //    5.2c-1: bei der Neu-Zuordnung der Schlüssel aus „Schlüssel verloren – neu zuordnen…“.
            string art = SchildSchluessel.ArtText(pseudo);
            SchildSchluessel schluessel;
            if (_imNeuzuordnung)
            {
                if (_imNeuSchluessel == null || _imNeuSchluessel.Pseudo != pseudo)
                    return "Die Neu-Zuordnung hat keinen Schlüssel (mehr) – bitte „Schlüssel verloren – neu zuordnen…“ noch einmal.";
                if (st.Schueler.Importiert == 0)
                    return "In der Datenbank stehen keine importierten Schüler – eine Neu-Zuordnung ist nicht nötig " +
                           "(„Neu-Zuordnung abbrechen“, dann normal „Vorschau berechnen“).";
                schluessel = _imNeuSchluessel;
                _imSchluessel = schluessel;
            }
            else
            {
                ImSchluesselLaden(pseudo, erzwingen: true);
                string neuZu = " Gibt es keine Sicherung mehr: „Schlüssel verloren – neu zuordnen…“ (ordnet die Schüler über Name + " +
                               "Klasse neu zu).";
                if (_imSchluessel == null && SchildSchluessel.Vorhanden(pseudo))
                    return (_imSchluesselFehler ?? "Die Schlüsseldatei ist nicht lesbar.") +
                           (st.Schueler.Importiert > 0 ? neuZu : "");
                if (_imSchluessel == null && st.Schueler.Importiert > 0)
                    return $"Für diesen Windows-Benutzer gibt es auf diesem Rechner keinen Schlüssel ({art}), in der Datenbank " +
                           $"stehen aber schon {st.Schueler.Importiert} importierte Schüler. Ohne den bisherigen Schlüssel erkennt " +
                           "der Import sie nicht wieder (alle wären „neu“). Abhilfe: „Schlüssel einlesen…“ mit der Sicherung vom " +
                           "bisherigen Rechner bzw. Benutzer." + neuZu +
                           (st.Pseudo ? " In der Entwicklung geht auch „Seed-Schüler entfernen…“ – das entfernt auch die früher " +
                                        "importierten Pseudo-Schüler." : "");
                // 5.2c-1 (189): ein anderer Schlüssel als beim letzten Import würde niemanden wiedererkennen.
                if (_imSchluessel != null && st.Schueler.Importiert > 0 && st.KennungLetzterImport != null
                    && !string.Equals(_imSchluessel.Kennung, st.KennungLetzterImport, StringComparison.OrdinalIgnoreCase))
                    return $"Der Schlüssel dieses Rechners ({art}, Kennung {_imSchluessel.Kennung}) ist ein anderer als beim " +
                           $"letzten Import (Kennung {st.KennungLetzterImport}). Mit diesem Schlüssel würde der Import die " +
                           $"{st.Schueler.Importiert} Schüler nicht wiedererkennen. Abhilfe: „Schlüssel einlesen…“ mit der Sicherung " +
                           $"des Schlüssels {st.KennungLetzterImport}." + neuZu;
                schluessel = _imSchluessel ?? SchildSchluessel.Erzeugen(pseudo);
                _imSchluessel = schluessel;
            }

            // 5. Nutzlast
            TxtStatus.Text = "Bilde die Prüfsummen …";
            var bau = SchildNutzlast.Baue(ex, erg, plan, schluessel, ImSchuljahr(), giltAb);
            if (bau.Nutzlast == null) return string.Join("\n", bau.Sperren);
            bau.Nutzlast.Neuzuordnung = _imNeuzuordnung;
            bau.Nutzlast.Paare = paare;

            // 6. Probelauf
            TxtStatus.Text = $"Schicke {bau.Schueler} Schüler als Probelauf …";
            var v = await client.VorschauAsync(bau.Nutzlast);
            _imBau = bau;
            _imNutzlast = bau.Nutzlast;
            ImZeigeVorschau(v, behalten);
            _log?.Invoke($"SchILD-Import: Vorschau ({art}, Abschnitt {erg.Abschnitt}, gilt ab {Dat(giltAb)}) – {v.Zahlen.ImExport} " +
                         $"Schüler, neu {v.Zahlen.Neu}, Klassenwechsel {v.Zahlen.Klassenwechsel}, Kurswechsel {v.Zahlen.Kurswechsel}, " +
                         $"Abgang laut SchILD {v.Zahlen.AbgangLautSchild}, fehlt {v.Zahlen.FehltImExport}, " +
                         $"Paare {v.Zahlen.DieselbePerson + v.Zahlen.ZuordnungPaare} (angewendet " +
                         $"{v.Zahlen.Zusammengefuehrt + v.Zahlen.NeuZugeordnet})" + (_imNeuzuordnung ? ", Neu-Zuordnung" : "") +
                         (v.Gesperrt ? $", gesperrt ({string.Join(", ", v.Sperren.Select(s => s.Art))})" : "") + ".");
            return null;
        }

        /// <summary>
        /// P3 (Kleinigkeit c): Warntext, wenn am Stichtag in der App ein Grundplan mit einer anderen Plan-Spalte gilt; null =
        /// passt bzw. noch kein Grundplan (das meldet die API selbst).
        /// </summary>
        private static string? ImPlanWarnung(SchildStatusAntwort st, string planSpalte, DateOnly giltAb)
        {
            if (st.Grundplaene.Count == 0 || string.IsNullOrWhiteSpace(planSpalte)) return null;
            var versionen = st.Grundplaene
                .Select(g => new GrundplanAbgleich.PlanVersion { Bezeichnung = g.Bezeichnung, GueltigAb = g.GueltigAb }).ToList();
            var erg = GrundplanAbgleich.Pruefen(versionen, planSpalte, giltAb, giltAb);
            if (erg.KeinGrundplan || erg.Andere.Count == 0) return null;
            return $"Im Wocheneditor ist der Plan „{planSpalte}“ gewählt, in der App gilt am {Dat(giltAb)} der Grundplan " +
                   $"{GrundplanAbgleich.Liste(erg.Andere)}. Zuordnung und Vorschau rechnen dann mit den Kursen eines anderen " +
                   "Plans – das gibt viele Kurswechsel bzw. unbekannte Kurse.\n\nAbhilfe: dieses Fenster schließen, im " +
                   "Wocheneditor im Feld „Plan“ den Grundplan wählen und „SchILD-Daten…“ neu öffnen.";
        }

        /// <param name="behalten">Bestätigungen aus einer früheren Vorschau (Kategorie|Id → an/aus), sonst null.</param>
        private void ImZeigeVorschau(SchildVorschauAntwort v, Dictionary<string, bool>? behalten)
        {
            foreach (var alt in _imZeilen) alt.Geaendert = null;
            _imZeilen.Clear();
            _imVorschau = v;
            _imAusgefuehrt = null;
            _imLokaleSperre = null;
            _imPaareGeaendert = false;
            foreach (var d in v.Schueler)
            {
                var zeile = new SchildImportZeile(d, false);
                // Paare (5.2c-1): Häkchen = in dieser Vorschau angewendet; Abgänge: Vorauswahl bzw. bisherige Bestätigung.
                bool an = zeile.IstPaar ? d.Gewaehlt : d.Vorausgewaehlt;
                if (!zeile.IstPaar && behalten != null && d.Id is int id && behalten.TryGetValue($"{d.Kategorie}|{id}", out bool b))
                    an = b;
                zeile = new SchildImportZeile(d, an);
                zeile.Geaendert = zeile.IstPaar ? ImPaarGeaendert : ImZeigeAuswahl;
                _imZeilen.Add(zeile);
            }

            _imAnsichtSetzen = true;
            CbImAnsicht.Items.Clear();
            CbImAnsicht.Items.Add(new ImAnsicht { Kategorie = "", Text = $"alle Zeilen ({_imZeilen.Count})" });
            var kategorien = KatReihenfolge.Concat(_imZeilen.Select(x => x.D.Kategorie).Where(k => !KatReihenfolge.Contains(k)).Distinct());
            foreach (var kat in kategorien)
            {
                int n = _imZeilen.Count(x => x.D.Kategorie == kat);
                if (n > 0) CbImAnsicht.Items.Add(new ImAnsicht { Kategorie = kat, Text = $"{KategorieText(kat)} ({n})" });
            }
            CbImAnsicht.SelectedIndex = 0;
            _imAnsichtSetzen = false;

            _imBericht = ImBerichtText(v.Bericht);
            var text = new StringBuilder(_imBericht);
            if (v.Gesperrt)
            {
                text.Append("\n\nWas tun?");
                foreach (var s in v.Sperren)
                {
                    if (s.Art == "SeedSchueler" && v.SeedEntfernenMoeglich)
                        text.Append("\n• Seed-Schüler: Knopf „Seed-Schüler entfernen…“ (nur Entwicklung), danach die Vorschau neu berechnen.");
                    else if (s.Art == "TeilExport")
                        text.Append("\n• Teil-Export: Fehlen fast alle Schüler, passt vermutlich der Schlüssel nicht (anderer Rechner " +
                                    "oder Windows-Benutzer) – „Schlüssel einlesen…“ mit der Sicherung. Sonst den Export vollständig " +
                                    "wiederholen (alle Klassen und Jahrgänge).");
                    else if (s.Art == "GiltAb")
                        text.Append("\n• „Änderungen gelten ab“ später wählen (frühestens der Stichtag des letzten Imports).");
                    else if (s.Art == "UnbekannteSchluessel")
                        text.Append("\n• Plan-Schlüssel unbekannt: Im Wocheneditor die richtige Plan-Spalte wählen bzw. den Grundplan " +
                                    "veröffentlichen, dann Prüflauf und Vorschau wiederholen.");
                    else if (s.Art == "Schluessel")
                        text.Append("\n• Anderer Schlüssel: „Schlüssel einlesen…“ mit der Sicherung des bisherigen Schlüssels. Nur wenn " +
                                    "es keine Sicherung gibt: „Schlüssel verloren – neu zuordnen…“.");
                    else if (s.Art == "Neuzuordnung")
                        text.Append("\n• Neu-Zuordnung nicht möglich bzw. nicht nötig: „Neu-Zuordnung abbrechen“, dann normal " +
                                    "„Vorschau berechnen“.");
                }
            }
            TxtImBericht.Text = text.ToString();
            ImFuelleTabelle();
        }

        /// <summary>Bericht der API (ohne Namen) und was der Desktop übertragen bzw. nicht übertragen hat.</summary>
        private string ImBerichtText(string apiBericht)
        {
            var b = new StringBuilder((apiBericht ?? "").TrimEnd());
            var bau = _imBau;
            if (bau != null)
            {
                b.Append("\n\nDesktop (Nutzlast):\n");
                b.Append($"  übertragen: {bau.Schueler} Schüler (davon {bau.Abgaenge} mit Abgang laut SchILD, {bau.MitSchildId} mit " +
                         $"SchILD-ID) · {bau.ZugeordneteKurse} zugeordnete Kurse/Gruppen · {bau.Kursplaetze} Kursplätze\n");
                if (bau.Warteliste > 0) b.Append($"  nicht übertragen: {bau.Warteliste} Schüler auf der Warteliste (Status 1)\n");
                // 5.2c-2: Klassenleitungen und Namen der Lehrkräfte (nur Kürzel bzw. Zahlen)
                b.Append($"  Klassenleitung (Klassen.dat): {bau.Klassenleitungen} Klassen übertragen" +
                         (bau.KlassenleitungOhneKuerzel.Count > 0
                             ? $" · ohne festes Plan-Kürzel, nicht übertragen: {string.Join(", ", bau.KlassenleitungOhneKuerzel.Take(20))}" +
                               (bau.KlassenleitungOhneKuerzel.Count > 20 ? " …" : "")
                             : "") + "\n");
                b.Append(bau.Lehrkraefte is int lk
                    ? $"  Namen der Lehrkräfte (Lehrkraefte.dat): {lk} Lehrkräfte des Plans übertragen" +
                      (bau.LehrkraefteOhneKuerzel > 0 ? $" · {bau.LehrkraefteOhneKuerzel} ohne festes Plan-Kürzel nicht" : "") + "\n"
                    : "  Namen der Lehrkräfte: Lehrkraefte.dat fehlt – nichts übertragen, die gespeicherten Namen bleiben\n");
                if (bau.NichtUebertragenZeilen > 0)
                {
                    b.Append($"  Zuordnung offen – nicht übertragen ({bau.NichtUebertragenZeilen} SchILD-Kurse/Gruppen, " +
                             $"{bau.NichtUebertragenTeilnehmer} Teilnahmen):\n");
                    foreach (var t in bau.NichtUebertragen.Take(40)) b.Append("    " + t + "\n");
                    if (bau.NichtUebertragen.Count > 40) b.Append($"    … und {bau.NichtUebertragen.Count - 40} weitere\n");
                }
            }
            if (_imSchluessel != null)
                b.Append($"  Schlüssel ({SchildSchluessel.ArtText(_imSchluessel.Pseudo)}): Kennung {_imSchluessel.Kennung}" +
                         (_imSchluessel.Neu ? " – neu, wird beim Import gespeichert" : "") +
                         (_imNeuzuordnung ? " – NEU-ZUORDNUNG über Name + Klasse" : "") + "\n");
            return b.ToString().TrimEnd();
        }

        // =====================================================
        //  Importieren
        // =====================================================

        private async void BtnImImportieren_Click(object sender, RoutedEventArgs e)
        {
            var v = _imVorschau;
            var nutzlast = _imNutzlast;
            var schluessel = _imSchluessel;
            if (_laeuft || v == null || nutzlast == null || schluessel == null || v.Gesperrt || _imAusgefuehrt != null) return;
            if (_imPaareGeaendert)
            {
                MessageBox.Show(this, "Die Häkchen bei den Paaren wurden geändert – bitte zuerst „Vorschau berechnen“.",
                    "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string? vor = ImVoraussetzung();
            if (vor != null)
            {
                MessageBox.Show(this, vor, "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (DpImGiltAb.SelectedDate is not DateTime datum || DateOnly.FromDateTime(datum) != nutzlast.GiltAb)
            {
                MessageBox.Show(this, "„Änderungen gelten ab“ wurde geändert – bitte zuerst die Vorschau neu berechnen.",
                    "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (schluessel.Pseudo != nutzlast.Pseudo) return;
            var client = ImClient();
            if (client == null) return;

            var bestaetigt = _imZeilen.Where(x => x.Bestaetigt && x.D.Id != null && !x.IstPaar).ToList();
            int kandidaten = _imZeilen.Count(x => x.Bestaetigbar && !x.IstPaar);
            // 5.2c-1: genau die Paare, die die Vorschau angewendet hat (die Häkchen sind seitdem unverändert).
            var paare = v.Schueler.Where(d => d.Id != null && !string.IsNullOrEmpty(d.PartnerExtId) && d.Gewaehlt
                                              && (d.Kategorie == KatPaar || d.Kategorie == KatZuordnung))
                                  .Select(d => new SchildPaar { Id = d.Id!.Value, ExtId = d.PartnerExtId! }).ToList();
            bool neuzuordnung = nutzlast.Neuzuordnung;
            int konten = bestaetigt.Count(x => x.D.KontoWirdGesperrt);
            var zz = v.Zahlen;
            bool pseudo = nutzlast.Pseudo;
            string art = SchildSchluessel.ArtText(pseudo);
            string frage =
                $"Import ausführen?\n\nSchuljahr {nutzlast.Schuljahr} · Abschnitt {nutzlast.Abschnitt} · Änderungen gelten ab " +
                $"{Dat(nutzlast.GiltAb)} · {art}\n" +
                (v.Grundplan != null ? $"Grundplan „{v.Grundplan}“" + (v.GrundplanAb is DateOnly ga ? $" (ab {Dat(ga)})" : "") + "\n" : "") +
                // P3 (Kleinigkeit c): anderer Plan als der Grundplan – die Warnung vor der Vorschau wurde mit „Ja“ übergangen
                (v.Grundplan != null && _planInfo != null &&
                 !string.Equals(v.Grundplan.Trim(), _planInfo.Bezeichnung.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? $"ACHTUNG: Im Wocheneditor ist der Plan „{_planInfo.Bezeichnung}“ gewählt – nicht der Grundplan.\n" : "") +
                // P4a (206): Warnungen zum Halbjahr – mit „Ja“ wird trotzdem importiert
                string.Concat(v.Warnungen.Select(w => "ACHTUNG: " + w + "\n")) +
                $"\nneu: {zz.Neu} · Name/Geburtsdatum geändert: {zz.NameGeaendert} · Klassenwechsel: {zz.Klassenwechsel} · " +
                $"Kurs-/Gruppenwechsel: {zz.Kurswechsel} · Status/Jahrgang: {zz.StatusGeaendert}\n" +
                $"Abgänge bestätigt: {bestaetigt.Count} von {kandidaten} (Schülerkonten, die gesperrt werden: {konten})\n" +
                (zz.DieselbePerson > 0 ? $"„Dieselbe Person?“: {zz.Zusammengefuehrt} von {zz.DieselbePerson} Paaren werden zusammengeführt\n" : "") +
                (neuzuordnung ? $"NEU-ZUORDNUNG: {zz.NeuZugeordnet} Schüler über Name + Klasse zugeordnet (mögliche Paare: " +
                                $"{zz.ZuordnungPaare}) – sie bekommen die Prüfsummen des neuen Schlüssels\n" : "") +
                $"neue Lerngruppen: {zz.NeueLerngruppen} · Kurse/Gruppen ab jetzt aus SchILD: {zz.GruppenAusSchild}\n" +
                // 5.2c-2
                (nutzlast.Klassenleitungen != null && zz.KlassenleitungNeu + zz.KlassenleitungGeaendert > 0
                    ? $"Klassenleitung laut SchILD: erstmals {zz.KlassenleitungNeu}, geändert {zz.KlassenleitungGeaendert} (die Konten " +
                      "ändern sich nicht – Vorschläge in der App)\n" : "") +
                (nutzlast.Lehrkraefte is { Count: > 0 }
                    ? $"Namen der Lehrkräfte: {zz.Lehrkraefte} – neu {zz.LehrkraefteNeu}, geändert {zz.LehrkraefteGeaendert}, " +
                      $"entfernt {zz.LehrkraefteEntfernt}\n" : "") +
                (schluessel.Neu
                    ? $"\nDer Schlüssel ({art}, Kennung {schluessel.Kennung}) wird jetzt für diesen Windows-Benutzer gespeichert" +
                      (pseudo ? "." : " – danach bitte „Schlüssel sichern…“.") + "\n"
                    : "") +
                (pseudo ? "" : "\nACHTUNG: echte Schülerdaten. Die Klassen- und Kurslisten ändern sich ab dem Stichtag; " +
                               "rückgängig machen lässt sich der Import nur über eine Rücksicherung der Datenbank.\n");
            if (MessageBox.Show(this, frage, "SchILD-Import", MessageBoxButton.YesNo,
                    pseudo && v.Warnungen.Count == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            // 5.2c-1 (Frage E): Neu-Zuordnung mit echten Daten – zweite Rückfrage.
            if (neuzuordnung && !pseudo &&
                MessageBox.Show(this, "Wirklich neu zuordnen? Wurde die Sicherung des bisherigen Schlüssels gesucht (USB-Stick, Tresor, " +
                                      "zweiter Rechner)? Mit der Sicherung („Schlüssel einlesen…“) ist keine Neu-Zuordnung nötig.\n\n" +
                                      "Die Neu-Zuordnung wird im Importprotokoll vermerkt.",
                    "SchILD-Import – Neu-Zuordnung", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                != MessageBoxResult.Yes)
                return;

            bool schluesselNeu = schluessel.Neu;
            var behalten = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var zeile in _imZeilen.Where(x => x.Bestaetigbar && !x.IstPaar && x.D.Id != null))
                behalten[$"{zeile.D.Kategorie}|{zeile.D.Id}"] = zeile.Bestaetigt;
            SchildImportErgebnisDto? fertig = null;
            ImStart("Importiere …");
            try
            {
                if (schluesselNeu)
                {
                    try
                    {
                        // 5.2c-1: eine nicht lesbare alte Schlüsseldatei wird bei der Neu-Zuordnung beiseitegelegt (umbenannt).
                        if (neuzuordnung && SchildSchluessel.Vorhanden(pseudo))
                        {
                            string? alt = SchildSchluessel.Beiseite(pseudo);
                            _log?.Invoke($"SchILD: alte Schlüsseldatei ({art}) beiseitegelegt: {alt}");
                        }
                        schluessel.Speichern();
                    }
                    catch (Exception fx)
                    {
                        MessageBox.Show(this, "Der Schlüssel konnte nicht gespeichert werden – es wurde nichts importiert.\n\n" +
                                              fx.Message, "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    _log?.Invoke($"SchILD: Schlüssel ({art}, Kennung {schluessel.Kennung}) für diesen Windows-Benutzer gespeichert.");
                }

                var ausgang = await client.ImportierenAsync(
                    nutzlast.AlsImport(v.Vorschau, bestaetigt.Select(x => x.D.Id!.Value).ToList(), Environment.UserName, paare));
                if (ausgang.Ergebnis is SchildImportErgebnisDto ergebnis)
                {
                    fertig = ergebnis;
                    foreach (var alt in _imZeilen) alt.Geaendert = null;
                    _imZeilen.Clear();
                    DgImport.ItemsSource = null;
                    _imAnsichtSetzen = true;
                    CbImAnsicht.Items.Clear();
                    _imAnsichtSetzen = false;
                    TxtImAnzahl.Text = "";
                    _imVorschau = null;
                    _imNutzlast = null;
                    _imAusgefuehrt = ergebnis.ProtokollId;
                    if (neuzuordnung)
                    {
                        // Der neue Schlüssel ist gespeichert – ab jetzt gilt er; die Neu-Zuordnung ist beendet.
                        _imNeuSchluessel = null;
                        _imNeuzuordnung = false;
                    }
                    _imBericht = ImBerichtText(ergebnis.Bericht);
                    TxtImBericht.Text = _imBericht;
                    var ez = ergebnis.Zahlen;
                    _log?.Invoke($"SchILD-Import ausgeführt (Protokoll Nr. {ergebnis.ProtokollId}, {art}, gilt ab " +
                                 $"{Dat(ergebnis.GiltAb)}): neu {ez.Neu}, Name geändert {ez.NameGeaendert}, Klassenwechsel " +
                                 $"{ez.Klassenwechsel}, Kurswechsel {ez.Kurswechsel}, Abgänge bestätigt {ergebnis.AbgaengeBestaetigt}, " +
                                 $"übersprungen {ergebnis.Uebersprungen}, zusammengeführt {ez.Zusammengefuehrt}" +
                                 (neuzuordnung ? $", Neu-Zuordnung {ez.NeuZugeordnet} (Schlüssel {schluessel.Kennung})" : "") + ".");
                    try
                    {
                        var st = await client.StatusAsync(ImSchuljahr(), nutzlast.Abschnitt);
                        _imStatus = st;
                        ImZeigeStatus(st, client.Adresse);
                        ImZeigeGiltAb(st);
                    }
                    catch (SchildServerFehler) { /* der Import ist trotzdem ausgeführt */ }
                }
                else if (ausgang.Vorschau != null)
                {
                    ImZeigeVorschau(ausgang.Vorschau, behalten);
                    MessageBox.Show(this, ausgang.Veraltet
                            ? "Die Vorschau war nicht mehr aktuell (die Datenbank hat sich inzwischen geändert) – es wurde nichts " +
                              "importiert. Bitte die neue Vorschau prüfen und dann noch einmal „Importieren…“."
                            : "Der Import ist gesperrt – es wurde nichts importiert:\n\n• " +
                              string.Join("\n• ", ausgang.Vorschau.Sperren.Select(s => s.Text)),
                        "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (SchildServerFehler sx)
            {
                MessageBox.Show(this, sx.Message, "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception sx)
            {
                MessageBox.Show(this, "Der Import ist fehlgeschlagen:\n\n" + sx.Message, "SchILD-Import",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { ImEnde(); }

            if (fertig == null) return;
            MessageBox.Show(this, $"Import ausgeführt (Protokoll Nr. {fertig.ProtokollId}).\n\nneu: {fertig.Zahlen.Neu} · " +
                                  $"Klassenwechsel: {fertig.Zahlen.Klassenwechsel} · Kurs-/Gruppenwechsel: {fertig.Zahlen.Kurswechsel}\n" +
                                  $"Abgänge bestätigt: {fertig.AbgaengeBestaetigt} · übersprungen: {fertig.Uebersprungen}" +
                                  (fertig.Zahlen.Zusammengefuehrt > 0 ? $"\nzusammengeführt: {fertig.Zahlen.Zusammengefuehrt}" : "") +
                                  (neuzuordnung ? $"\nneu zugeordnet: {fertig.Zahlen.NeuZugeordnet} (neuer Schlüssel, Kennung " +
                                                  $"{schluessel.Kennung})" : ""),
                "SchILD-Import", MessageBoxButton.OK, MessageBoxImage.Information);
            if (schluesselNeu && (!pseudo || neuzuordnung) &&
                MessageBox.Show(this, "Das war der erste Import mit diesem Schlüssel. Ohne ihn erkennt der nächste Import die " +
                                      "Schüler nicht wieder (z. B. nach einem Rechnerwechsel).\n\nJetzt „Schlüssel sichern…“ " +
                                      "(Datei mit Kennwort für USB-Stick bzw. Tresor)?",
                    "Schlüssel sichern", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await ImSichernAsync(pseudo);
        }

        // =====================================================
        //  5.2c-2: Neu-Zuordnung – Vorschlag je Gruppe Namensgleicher übernehmen
        // =====================================================

        /// <summary>
        /// Hakt je Gruppe Namensgleicher genau die Paare an, die die API vorschlägt (passender Eintritt, dann die meisten
        /// gemeinsamen Kurse) – nur Gruppen mit eindeutigem Vorschlag, nach Rückfrage. Danach „Vorschau berechnen“.
        /// </summary>
        private void BtnImVorschlag_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft || _imVorschau == null || _imAusgefuehrt != null) return;
            var gruppen = _imZeilen.Where(x => x.IstPaar && x.D.Gruppe != null).GroupBy(x => x.D.Gruppe!.Value).ToList();
            var mitVorschlag = gruppen.Where(g => g.Any(x => x.D.Vorschlag)).ToList();
            if (mitVorschlag.Count == 0)
            {
                MessageBox.Show(this, "Für keine Gruppe gibt es einen eindeutigen Vorschlag – bitte je Schüler von Hand wählen " +
                                      "(in SchILD nachsehen).", "Vorschlag übernehmen", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int aendern = mitVorschlag.Sum(g => g.Count(x => x.Bestaetigt != x.D.Vorschlag));
            if (aendern == 0)
            {
                MessageBox.Show(this, "Die Häkchen entsprechen schon dem Vorschlag.", "Vorschlag übernehmen",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int ohne = gruppen.Count - mitVorschlag.Count;
            int paare = mitVorschlag.Sum(g => g.Count(x => x.D.Vorschlag));
            string frage =
                $"Vorschlag übernehmen?\n\nFür {mitVorschlag.Count} von {gruppen.Count} Gruppen Namensgleicher gibt es einen " +
                $"eindeutigen Vorschlag ({paare} Paare): zuerst passender Eintritt (Neuaufnahme am selben Tag bzw. beide seit " +
                "Schuljahresbeginn), dann die meisten gemeinsamen Kurse und Gruppen. In diesen Gruppen werden genau die " +
                $"vorgeschlagenen Paare angehakt, die übrigen abgehakt ({aendern} Häkchen ändern sich).\n\n" +
                (ohne > 0 ? $"{ohne} Gruppen sind gleich gut – dort bitte selbst wählen (in SchILD nachsehen).\n\n" : "") +
                "Danach „Vorschau berechnen“ und die Zeilen prüfen; gespeichert wird erst mit „Importieren…“.";
            if (MessageBox.Show(this, frage, "Vorschlag übernehmen", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            foreach (var gruppe in mitVorschlag)
                foreach (var zeile in gruppe) zeile.Bestaetigt = zeile.D.Vorschlag;
            ImZeigeAuswahl();
        }

        // =====================================================
        //  5.2c-1: Schlüssel verloren – neu zuordnen (Entscheidung 190, Frage E)
        // =====================================================

        private async void BtnImNeu_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft || _export == null) return;
            if (_imNeuzuordnung)
            {
                ImNeuzuordnungBeenden();
                ImVorschauVerwerfen("Neu-Zuordnung abgebrochen – es wurde nichts gespeichert.");
                return;
            }
            string? vor = ImVoraussetzung();
            if (vor != null)
            {
                MessageBox.Show(this, vor, "Neu zuordnen", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var st = _imStatus;
            bool pseudo = _export.IstPseudo;
            string art = SchildSchluessel.ArtText(pseudo);
            if (st == null || st.Pseudo != pseudo || st.Schueler.Importiert == 0)
            {
                MessageBox.Show(this, "Zuerst „Status abfragen“ bzw. „Vorschau berechnen“ – eine Neu-Zuordnung gibt es nur, wenn in der " +
                                      $"Datenbank schon importierte Schüler ({art}) stehen.", "Neu zuordnen",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ImSchluesselLaden(pseudo, erzwingen: true);
            var vorhanden = _imSchluessel;
            if (vorhanden != null && !vorhanden.Neu && st.KennungLetzterImport != null
                && string.Equals(vorhanden.Kennung, st.KennungLetzterImport, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, $"Der Schlüssel dieses Rechners (Kennung {vorhanden.Kennung}) ist derselbe wie beim letzten Import – " +
                                      "eine Neu-Zuordnung ist nicht nötig. Einfach „Vorschau berechnen“.", "Neu zuordnen",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            bool lesbar = vorhanden != null && !vorhanden.Neu;
            string text =
                $"Schlüssel verloren – die {st.Schueler.Importiert} importierten Schüler ({art}) neu zuordnen?\n\n" +
                "Zuerst bitte die Sicherung suchen (USB-Stick, Tresor, alter Rechner) und „Schlüssel einlesen…“ – dann ist keine " +
                "Neu-Zuordnung nötig.\n\n" +
                "Die Neu-Zuordnung ordnet die bisherigen Schüler über Name + Klasse (Oberstufe: Jahrgang) den Schülern des Exports zu. " +
                "Eindeutige Paare sind vorausgewählt; Namensgleiche und Schüler, die gleichzeitig die Klasse gewechselt haben, " +
                "ordnest du in der Vorschau von Hand zu. Zugeordnete Schüler behalten Konto und Klassenbuchdaten und bekommen die " +
                "Prüfsummen des " + (lesbar ? $"Schlüssels dieses Rechners (Kennung {vorhanden!.Kennung})" : "neuen Schlüssels") + ". " +
                "Gespeichert wird erst mit „Importieren…“; danach sofort „Schlüssel sichern…“.\n\n" +
                (SchildSchluessel.Vorhanden(pseudo) && !lesbar
                    ? "Die vorhandene, nicht lesbare Schlüsseldatei wird beim Import umbenannt (nicht gelöscht).\n\n" : "") +
                "Vorschau der Neu-Zuordnung berechnen?";
            if (MessageBox.Show(this, text, "Schlüssel verloren – neu zuordnen", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            if (!pseudo &&
                MessageBox.Show(this, "Echte Schülerdaten: Ist die Sicherung des bisherigen Schlüssels wirklich nicht mehr zu finden?\n\n" +
                                      "Die Neu-Zuordnung wird im Importprotokoll vermerkt.", "Neu zuordnen – echte Daten",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            ImNeuzuordnungBeenden();
            _imNeuSchluessel = lesbar ? vorhanden : SchildSchluessel.Erzeugen(pseudo);
            _imSchluessel = _imNeuSchluessel;
            _imSchluesselArt = pseudo;
            _imNeuzuordnung = true;
            ImVorschauVerwerfen(null);   // keine Paare aus einer früheren (normalen) Vorschau mitnehmen
            _log?.Invoke($"SchILD: Neu-Zuordnung begonnen ({art}, Schlüssel Kennung {_imNeuSchluessel!.Kennung}" +
                         (lesbar ? ", vorhandener Schlüssel" : ", neu erzeugt") + ").");
            await Task.Yield();
            BtnImVorschau_Click(sender, e);
        }

        // =====================================================
        //  Seed-Schüler entfernen (nur Entwicklung, Punkt A)
        // =====================================================

        private async void BtnImSeed_Click(object sender, RoutedEventArgs e)
        {
            var st = _imStatus;
            if (_laeuft || st == null || !st.Pseudo) return;
            if (MessageBox.Show(this,
                    "Nur Entwicklung: In der Entwicklungsdatenbank alle Pseudo-Schüler entfernen – " +
                    $"{st.Schueler.Seed} aus dem Seed und {st.Schueler.PseudoImportiert} früher importierte – samt ihren " +
                    "Klassenbuchdaten (Anwesenheit, Einträge), Pseudo-Lerngruppen und Gruppenlisten, wie ein neuer Seed.\n\n" +
                    "Die Testkonten s05A/sQ1 werden gelöst (nach dem Import mit POST /api/auth/seed-konten neu verknüpfen). " +
                    "Importprotokolle bleiben.\n\nFortfahren?",
                    "Seed-Schüler entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                != MessageBoxResult.Yes)
                return;
            var client = ImClient();
            if (client == null) return;
            bool neuBerechnen = false;
            ImStart("Entferne die Pseudo-Schüler …");
            try
            {
                var r = await client.SeedEntfernenAsync();
                _log?.Invoke($"SchILD: Pseudo-Schüler entfernt ({r.SeedSchueler} aus dem Seed, {r.ImportierteSchueler} importiert).");
                var stNeu = await client.StatusAsync(ImSchuljahr(), _ergebnis?.Abschnitt ?? 1);
                _imStatus = stNeu;
                ImZeigeStatus(stNeu, client.Adresse);
                _imDatumVonHand = false;
                ImGiltAbSetzen(stNeu);
                ImVorschauVerwerfen(null);
                MessageBox.Show(this, $"Entfernt: {r.SeedSchueler} Schüler aus dem Seed, {r.ImportierteSchueler} früher importierte." +
                                      (string.IsNullOrWhiteSpace(r.Hinweis) ? "" : "\n\n" + r.Hinweis),
                    "Seed-Schüler entfernen", MessageBoxButton.OK, MessageBoxImage.Information);
                neuBerechnen = ImVoraussetzung() == null;
            }
            catch (SchildServerFehler sx)
            {
                MessageBox.Show(this, sx.Message, "Seed-Schüler entfernen", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { ImEnde(); }
            if (neuBerechnen) BtnImVorschau_Click(sender, e);
        }

        // =====================================================
        //  Schlüssel sichern / einlesen (170, F)
        // =====================================================

        private async void BtnImSichern_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft || _export == null) return;
            await ImSichernAsync(_export.IstPseudo);
        }

        private async Task ImSichernAsync(bool pseudo)
        {
            ImSchluesselLaden(pseudo, erzwingen: true);
            var k = _imSchluessel;
            string art = SchildSchluessel.ArtText(pseudo);
            if (k == null || k.Neu)
            {
                MessageBox.Show(this, k != null || !SchildSchluessel.Vorhanden(pseudo)
                        ? $"Es gibt noch keinen gespeicherten Schlüssel ({art}) – er entsteht beim ersten Import. Danach sichern."
                        : _imSchluesselFehler ?? "Der Schlüssel ist nicht lesbar.",
                    "Schlüssel sichern", MessageBoxButton.OK, MessageBoxImage.Information);
                ImAnzeigen();
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Schlüssel sichern ({art})",
                FileName = $"SchILD-Schluessel_{(pseudo ? "Pseudo_" : "")}{k.Kennung}_{DateTime.Today:yyyy-MM-dd}.schluessel",
                DefaultExt = ".schluessel",
                AddExtension = true,
                OverwritePrompt = true,
                Filter = "Sicherung des SchILD-Schlüssels (*.schluessel)|*.schluessel|Alle Dateien (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            string pfad = dlg.FileName;
            if (!pseudo && SchildSchluessel.IstCloudOrdner(pfad) &&
                MessageBox.Show(this, "Der gewählte Ordner wird in die Cloud synchronisiert (OneDrive, Teams/SharePoint …).\n\n" +
                                      "Die Sicherung gehört auf einen USB-Stick bzw. in den Tresor – nicht in die Cloud. Trotzdem " +
                                      "hier speichern?", "Schlüssel sichern", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            string? kennwort = FrageKennwort(true, "Schlüssel sichern",
                $"Kennwort für die Sicherung ({art}, Kennung {k.Kennung}) – mindestens {SchildSchluessel.MinKennwort} Zeichen. " +
                "Ohne dieses Kennwort lässt sich die Sicherung nicht einlesen; es wird nirgends gespeichert.");
            if (kennwort == null) return;
            ImStart("Sichere den Schlüssel …");
            try
            {
                await Task.Run(() => k.Sichern(pfad, kennwort));
            }
            catch (Exception sx)
            {
                MessageBox.Show(this, "Sichern fehlgeschlagen:\n\n" + sx.Message, "Schlüssel sichern",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { ImEnde(); }
            _log?.Invoke($"SchILD: Schlüssel ({art}, Kennung {k.Kennung}) gesichert.");
            MessageBox.Show(this, $"Gesichert:\n{pfad}\n\nKennung {k.Kennung}. Datei und Kennwort getrennt aufbewahren (die Datei z. B. " +
                                  "auf einem USB-Stick im Tresor, das Kennwort an einem anderen Ort). Auf einem neuen Rechner bzw. " +
                                  "für einen anderen Windows-Benutzer: „Schlüssel einlesen…“.",
                "Schlüssel sichern", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnImEinlesen_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Sicherung des SchILD-Schlüssels einlesen",
                Filter = "Sicherung des SchILD-Schlüssels (*.schluessel)|*.schluessel|Alle Dateien (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;
            var kopf = SchildSchluessel.LiesKopf(dlg.FileName, out string? kopfFehler);
            if (kopf == null)
            {
                MessageBox.Show(this, kopfFehler ?? "Die Datei ist keine Sicherung.", "Schlüssel einlesen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string art = SchildSchluessel.ArtText(kopf.Pseudo);
            string? kennwort = FrageKennwort(false, "Schlüssel einlesen",
                $"Sicherung für {art}, Kennung {kopf.Kennung}, erstellt {kopf.Erstellt}.\n\nKennwort der Sicherung:");
            if (kennwort == null) return;

            SchildSchluessel? neu = null;
            string? fehler = null;
            ImStart("Lese die Sicherung …");
            try
            {
                (neu, fehler) = await Task.Run(() =>
                {
                    var gelesen = SchildSchluessel.AusSicherung(kopf, kennwort, out string? f);
                    return (gelesen, f);
                });
            }
            finally { ImEnde(); }
            if (neu == null)
            {
                MessageBox.Show(this, fehler ?? "Die Sicherung ließ sich nicht lesen.", "Schlüssel einlesen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string kennung = neu.Kennung;
            if (SchildSchluessel.Vorhanden(kopf.Pseudo))
            {
                var alt = SchildSchluessel.Laden(kopf.Pseudo, out _);
                string? altKennung = alt?.Kennung;
                alt?.Vergessen();
                if (altKennung == kennung)
                {
                    neu.Vergessen();
                    MessageBox.Show(this, $"Dieser Schlüssel ({art}, Kennung {kennung}) ist hier schon eingerichtet.",
                        "Schlüssel einlesen", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show(this,
                        $"Für {art} gibt es auf diesem Rechner schon einen Schlüssel " +
                        (altKennung != null ? $"(Kennung {altKennung})" : "(nicht lesbar)") + ".\n\n" +
                        $"Durch den Schlüssel aus der Sicherung (Kennung {kennung}) ersetzen? Wurde mit dem bisherigen Schlüssel " +
                        "schon importiert, erkennt der Import diese Schüler danach nicht mehr – nur ersetzen, wenn die Sicherung " +
                        "von dem Rechner bzw. Benutzer stammt, mit dem bisher importiert wurde.",
                        "Schlüssel einlesen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    != MessageBoxResult.Yes)
                {
                    neu.Vergessen();
                    return;
                }
            }
            try
            {
                neu.Speichern(ersetzen: true);
            }
            catch (Exception sx)
            {
                MessageBox.Show(this, "Speichern fehlgeschlagen:\n\n" + sx.Message, "Schlüssel einlesen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { neu.Vergessen(); }
            _log?.Invoke($"SchILD: Schlüssel ({art}, Kennung {kennung}) aus einer Sicherung eingelesen.");
            ImNeuzuordnungBeenden();   // 5.2c-1: mit der Sicherung ist keine Neu-Zuordnung mehr nötig
            _imSchluesselArt = null;
            ImVorschauVerwerfen("Schlüssel eingelesen – bitte die Vorschau neu berechnen.");
            MessageBox.Show(this, $"Eingelesen: Schlüssel für {art}, Kennung {kennung}.\n\nGespeichert für diesen Windows-Benutzer " +
                                  $"unter:\n{SchildSchluessel.Pfad(kopf.Pseudo)}",
                "Schlüssel einlesen", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Kleines Fenster für ein Kennwort (neu = zweimal eingeben, Mindestlänge). null = Abbruch.</summary>
        private string? FrageKennwort(bool neu, string titel, string text)
        {
            var win = new Window
            {
                Title = titel,
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            panel.Children.Add(new TextBlock { Text = "Kennwort:", Margin = new Thickness(0, 0, 0, 2) });
            var pb1 = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(pb1);
            PasswordBox? pb2 = null;
            if (neu)
            {
                panel.Children.Add(new TextBlock { Text = "Kennwort wiederholen:", Margin = new Thickness(0, 0, 0, 2) });
                pb2 = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
                panel.Children.Add(pb2);
            }
            var meldung = new TextBlock
            {
                Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 6)
            };
            panel.Children.Add(meldung);
            var knoepfe = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var abbr = new Button { Content = "Abbrechen", Width = 100, IsCancel = true };
            knoepfe.Children.Add(ok);
            knoepfe.Children.Add(abbr);
            panel.Children.Add(knoepfe);
            win.Content = panel;

            string? ergebnis = null;
            ok.Click += (_, __) =>
            {
                string eingabe = pb1.Password;
                string? problem = null;
                if (neu && eingabe.Length < SchildSchluessel.MinKennwort)
                    problem = $"Mindestens {SchildSchluessel.MinKennwort} Zeichen.";
                else if (neu && pb2 != null && eingabe != pb2.Password)
                    problem = "Die beiden Eingaben stimmen nicht überein.";
                else if (eingabe.Length == 0)
                    problem = "Bitte das Kennwort eingeben.";
                if (problem != null)
                {
                    meldung.Text = problem;
                    meldung.Visibility = Visibility.Visible;
                    return;
                }
                ergebnis = eingabe;
                win.DialogResult = true;
            };
            win.Loaded += (_, __) => pb1.Focus();
            return win.ShowDialog() == true ? ergebnis : null;
        }

        /// <summary>Beim Schließen: Vorschau (mit Namen) und Schlüssel aus dem Arbeitsspeicher entfernen.</summary>
        private void ImSchliessen()
        {
            foreach (var alt in _imZeilen) alt.Geaendert = null;
            _imZeilen.Clear();
            DgImport.ItemsSource = null;
            _imVorschau = null;
            _imNutzlast = null;
            _imBau = null;
            _imSchluessel?.Vergessen();
            _imSchluessel = null;
            if (_imNeuSchluessel != null && _imNeuSchluessel.Neu) _imNeuSchluessel.Vergessen();
            _imNeuSchluessel = null;
            _imNeuzuordnung = false;
        }
    }
}
