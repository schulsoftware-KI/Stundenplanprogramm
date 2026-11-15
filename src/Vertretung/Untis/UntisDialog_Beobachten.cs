#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// U-2 (claude/Untis_Vorschlag.md §3): „Ordner beobachten“. Solange das Fenster „Vertretungen aus Untis“ offen ist
    /// (auch minimiert), prüft ein Takt alle drei Sekunden, ob Untis die beobachtete GPU014.TXT neu geschrieben hat. Erst
    /// wenn die Datei fertig geschrieben ist (Größe und Zeitstempel zwei Takte lang gleich, Datei ohne Schreibsperre
    /// lesbar), wird sie gelesen, geprüft und übersetzt wie mit „Prüfen“. Gesendet werden nur die Schultage der Datei,
    /// deren Einträge sich gegenüber dem zuletzt aus Untis gesendeten Stand geändert haben (Prüfsumme je Tag in
    /// untis_import.json) – heute und künftige Tage zuerst. Nichts wird automatisch gesendet, wenn die Datei MEHR
    /// Fehlerzeilen hat als zuletzt gesendet bzw. bestätigt (anfangs 0; eine dauerhaft fehlerhafte Zeile wird so nur
    /// einmal je Beobachtung bestätigt – die Demo-Datei enthält absichtlich zwei) oder wenn auf einen Schlag mehr als drei Tage leer würden, die vorher Vertretungen hatten (Schutz vor einer
    /// unvollständigen Datei); dann zeigt das Fenster den Grund und „Geänderte Tage trotzdem senden…“. Scheitert das
    /// Senden (Server nicht erreichbar), versucht es das Fenster nach einer Minute erneut.
    /// </summary>
    public partial class UntisDialog
    {
        private const int TaktSekunden = 3;
        private const int WiederholenSekunden = 60;
        private const int MaxNeuLeereTage = 3;
        private const int MaxProtokollZeilen = 300;

        private const string HilfeBeobachten =
            "\n\nOrdner beobachten (U-2)\n\n" +
            "„Ordner beobachten…“ (unten) beobachtet die gewählte Datei – bzw. GPU014.TXT im gewählten Ordner –, solange " +
            "dieses Fenster offen ist (es darf minimiert sein; der Wocheneditor bleibt bedienbar). Schreibt Untis die Datei " +
            "neu, prüft das Fenster sie nach wenigen Sekunden und veröffentlicht nur die Schultage, die sich gegenüber dem " +
            "zuletzt aus Untis gesendeten Stand geändert haben – heute und künftige Tage zuerst. Der Zeitraum ist dabei " +
            "immer der erste bis letzte Tag der Datei; der Plan ist der beim Einschalten gewählte.\n" +
            "Am einfachsten richtet man in Untis den Export so ein, dass er nach jeder Änderungsrunde in denselben Ordner " +
            "schreibt (z. B. C:\\Vertretung\\GPU014.TXT).\n\n" +
            "Sicherheit: Hat die Datei mehr Zeilen mit Fehlern als zuletzt gesendet bzw. bestätigt (z. B. eine halb " +
            "geschriebene Zeile) oder würden auf einen Schlag mehr als drei Tage leer, die vorher Vertretungen hatten, wird " +
            "nichts gesendet. Das Fenster nennt den Grund; mit „Geänderte Tage trotzdem senden…“ geht es nach Prüfung weiter " +
            "– gleich viele Fehlerzeilen werden danach hingenommen (die Zeilen selbst fehlen in der App). Ist der Server nicht erreichbar, versucht es das " +
            "Fenster nach einer Minute erneut. Was geschehen ist, steht im Reiter „Beobachten“.\n\n" +
            "Tage, die im Wocheneditor aus dem Vertretungsmodul veröffentlicht wurden, bleiben so, bis sich der Tag in " +
            "Untis ändert – dann gilt wieder Untis (eine Quelle je Tag).\n\n" +
            "Testen: Ordner der Demo-Datei beobachten, dann „Demo-Datei erzeugen…“ mit einem anderen Startwert in denselben " +
            "Ordner – oder in der Demo-Datei mit dem Editor eine Zeile ändern (die Datei gilt danach als „echte Datei“) und " +
            "speichern. Gesendet werden nur die Tage mit Änderungen.";

        private readonly ObservableCollection<string> _beobProtokoll = new();
        private DispatcherTimer? _takt;
        private string? _beobPfad;
        private PlanDaten? _beobPlan;
        private (long Laenge, DateTime Zeit)? _kandidat;
        private (long Laenge, DateTime Zeit)? _verarbeitet;
        private string? _verarbeiteterHash;
        private DateTime? _wiederholenAb;
        private bool _durchgang;
        private int _pause;
        private int _akzeptierteFehler;   // Fehlerzeilen, die zuletzt gesendet bzw. bestätigt wurden (je Beobachtung)
        private bool _dateiFehltGemeldet;
        private bool _ohneRueckfrageSchliessen;
        private Gehalten? _gehalten;
        private DateTime? _zuletztGeprueft;
        private string? _zuletztGesendet;

        /// <summary>Angehaltener Stand: geändert, aber aus Sicherheitsgründen nicht automatisch gesendet.</summary>
        private sealed class Gehalten
        {
            public UntisDatei Datei { get; init; } = new();
            public UntisErgebnis Ergebnis { get; init; } = new();
            public string Grund { get; init; } = "";
        }

        /// <summary>Wird der Ordner gerade beobachtet? (für den Wocheneditor)</summary>
        public bool Beobachtet => _takt != null;

        /// <summary>Wird gerade gelesen oder gesendet? (für den Wocheneditor)</summary>
        public bool Beschaeftigt => _laeuft || _durchgang;

        /// <summary>Schließen, nachdem der Wocheneditor schon gefragt hat.</summary>
        public void SchliessenOhneRueckfrage()
        {
            _ohneRueckfrageSchliessen = true;
            Close();
        }

        /// <summary>Hält das Beobachten an, solange das Ergebnis nicht freigegeben ist (Rückfragen, manuelles Senden).</summary>
        private IDisposable PauseBeobachten() => new PauseToken(this);

        private sealed class PauseToken : IDisposable
        {
            private UntisDialog? _d;
            public PauseToken(UntisDialog d) { _d = d; d._pause++; }
            public void Dispose() { if (_d != null) { _d._pause--; _d = null; } }
        }

        // =====================================================
        //  Ein- und Ausschalten
        // =====================================================

        private async void BtnBeobachten_Click(object sender, RoutedEventArgs e)
        {
            if (Beobachtet) { BeendeBeobachten("von Hand beendet"); return; }
            if (_laeuft) return;
            using (PauseBeobachten())
            {
                if (!StarteBeobachten()) return;
            }
            await TaktSicherAsync(sofort: true);
        }

        /// <summary>Takt ohne Absturz: Unerwartete Fehler landen im Reiter „Beobachten“, das Beobachten läuft weiter.</summary>
        private async Task TaktSicherAsync(bool sofort)
        {
            try { await TaktAsync(sofort); }
            catch (Exception ex)
            {
                _durchgang = false;
                Protokoll("Unerwarteter Fehler beim Beobachten: " + EineZeile(ex.Message));
                ZeigeBeobachtenStatus();
            }
        }

        /// <summary>Fragt Datei bzw. Ordner, Plan und Server ab und schaltet den Takt ein. false = nicht gestartet.</summary>
        private bool StarteBeobachten()
        {
            if (string.IsNullOrWhiteSpace(_schuljahr))
            {
                MessageBox.Show(this, "Im Schuljahreskalender ist kein Schuljahr eingetragen (Button 22).", "Ordner beobachten",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            // Datei: die gewählte – sonst GPU014.TXT in einem Ordner (die Datei muss noch nicht da sein).
            string pfad = TxtDatei.Text.Trim();
            if (pfad.Length == 0)
            {
                var prot = UntisImportProtokoll.Laden(_planOrdner);
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Ordner, in den Untis die GPU014.TXT schreibt"
                };
                if (!string.IsNullOrEmpty(prot.Ordner) && Directory.Exists(prot.Ordner)) dlg.InitialDirectory = prot.Ordner;
                if (dlg.ShowDialog(this) != true) return false;
                pfad = Path.Combine(dlg.FolderName, UntisGenerator.DateiName);
                try
                {
                    prot.Ordner = dlg.FolderName;
                    prot.Speichern(_planOrdner);
                }
                catch { /* nur Komfort */ }
            }
            string? ordner = Path.GetDirectoryName(pfad);
            if (string.IsNullOrEmpty(ordner) || !Directory.Exists(ordner))
            {
                MessageBox.Show(this, "Der Ordner der Datei ist nicht vorhanden:\n" + (ordner ?? pfad), "Ordner beobachten",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Plan: der jetzt im Wocheneditor gewählte (gilt für die ganze Beobachtung).
            PlanDaten? plan;
            string? fehler;
            try { (plan, fehler) = _planHolen(); }
            catch (Exception ex) { plan = null; fehler = ex.Message; }
            if (plan == null)
            {
                MessageBox.Show(this, "Der Plan konnte nicht gelesen werden:\n\n" + (fehler ?? "unbekannter Fehler"),
                    "Ordner beobachten", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var einst = HoleEinstellungenMitQuelle("beobachten");
            if (einst == null) return false;

            bool cloud = IstCloudOrdner(pfad);
            string frage =
                "Ordner beobachten?\n\n" +
                $"Datei: {pfad}\n" +
                $"Plan: „{plan.GewaehltePlanSpalte}“ – der veröffentlichte Grundplan muss zu diesem Plan passen.\n" +
                $"Server: {einst.BaseUrl}\n\n" +
                "Solange dieses Fenster offen ist (auch minimiert), prüft das Programm alle drei Sekunden, ob Untis die Datei neu " +
                "geschrieben hat. Dann werden nur die Schultage veröffentlicht, die sich gegenüber dem zuletzt aus Untis " +
                "gesendeten Stand geändert haben (Zeitraum: erster bis letzter Tag der Datei). Gleich nach dem Einschalten wird " +
                "die vorhandene Datei so geprüft.\n\n" +
                "Ein geänderter Tag ersetzt in der App auch Buchungen des Vertretungsmoduls. Hat die Datei Fehler, wird nichts " +
                "gesendet." +
                (cloud
                    ? "\n\n⚠ Der Ordner liegt in einem Cloud-Ordner (OneDrive, Teams …). Echte GPU014-Dateien enthalten Absenzgründe " +
                      "und gehören nur auf diesen Rechner (z. B. C:\\Vertretung\\) – für Demo-Dateien ist es in Ordnung."
                    : "");
            if (MessageBox.Show(this, frage, "Ordner beobachten", MessageBoxButton.OKCancel,
                    cloud ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
                return false;

            _beobPfad = pfad;
            _beobPlan = plan;
            _kandidat = null;
            _verarbeitet = null;
            _verarbeiteterHash = null;
            _wiederholenAb = null;
            _dateiFehltGemeldet = false;
            _akzeptierteFehler = 0;
            _gehalten = null;
            _zuletztGeprueft = null;
            _zuletztGesendet = null;
            TxtDatei.Text = pfad;
            TxtPlan.Text = $"Plan: „{plan.GewaehltePlanSpalte}“";

            _takt = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(TaktSekunden) };
            _takt.Tick += async (_, __) => await TaktSicherAsync(sofort: false);
            _takt.Start();

            Protokoll($"Beobachten eingeschaltet: {pfad} (Plan „{plan.GewaehltePlanSpalte}“, Server {einst.BaseUrl}).");
            _log?.Invoke($"Untis: Ordner beobachten eingeschaltet ({pfad}, Plan „{plan.GewaehltePlanSpalte}“).");
            BtnBeobachten.Content = "Beobachten beenden";
            SetzeKnoepfe(!_laeuft);
            ZeigeBeobachtenStatus();
            return true;
        }

        /// <summary>Schaltet das Beobachten aus (Knopf, Schließen des Fensters oder des Wocheneditors).</summary>
        public void BeendeBeobachten(string grund)
        {
            if (_takt == null) return;
            _takt.Stop();
            _takt = null;
            _beobPlan = null;
            _gehalten = null;
            _wiederholenAb = null;
            Protokoll($"Beobachten beendet ({grund}).");
            _log?.Invoke($"Untis: Ordner beobachten beendet ({grund}).");
            if (!IsLoaded) return;
            BtnBeobachten.Content = "Ordner beobachten…";
            BtnJetztSenden.Visibility = Visibility.Collapsed;
            SetzeKnoepfe(!_laeuft);
            ZeigeBeobachtenStatus();
        }

        // =====================================================
        //  Takt: Datei fertig geschrieben? Dann prüfen und senden.
        // =====================================================

        private async Task TaktAsync(bool sofort)
        {
            if (_takt == null || _beobPfad == null || _beobPlan == null) return;
            if (_laeuft || _durchgang || _pause > 0) return;
            PlanDaten plan = _beobPlan;
            if (_wiederholenAb is DateTime w && DateTime.Now < w) return;

            string pfad = _beobPfad;
            (long, DateTime)? sig = Signatur(pfad);
            if (sig == null)
            {
                if (!_dateiFehltGemeldet)
                {
                    Protokoll($"Warte auf die Datei {Path.GetFileName(pfad)} im Ordner {Path.GetDirectoryName(pfad)} …");
                    _dateiFehltGemeldet = true;
                    ZeigeBeobachtenStatus();
                }
                _kandidat = null;
                return;
            }
            _dateiFehltGemeldet = false;

            bool faellig = _wiederholenAb != null || !Equals(sig, _verarbeitet);
            if (!faellig) return;

            // Fertig geschrieben? Größe und Zeitstempel müssen einen Takt lang gleich bleiben (beim Einschalten nicht).
            if (!Equals(sig, _kandidat))
            {
                _kandidat = sig;
                if (!sofort) return;
            }

            byte[]? gelesen = LiesOhneSchreibsperre(pfad);
            if (gelesen == null) return;   // Untis schreibt noch – nächster Takt
            byte[] bytes = gelesen;

            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (hash == _verarbeiteterHash && _wiederholenAb == null)
            {
                _verarbeitet = sig;   // nur neu gespeichert, Inhalt gleich
                return;
            }

            _durchgang = true;
            UntisDatei? datei = null;
            UntisErgebnis? erg = null;
            string? lesefehler = null;
            try
            {
                var kal = _kal;
                var tage = _buchungsTageHolen();
                (datei, erg) = await Task.Run(() =>
                {
                    var d = UntisLeser.Lies(pfad, bytes);
                    return (d, UntisUebersetzung.Uebersetze(d, plan, kal, tage, DateTime.Today));
                });
            }
            catch (Exception ex) { lesefehler = ex.Message; }
            finally { _durchgang = false; }

            if (_takt == null || !IsLoaded) return;   // inzwischen beendet
            _verarbeitet = sig;
            _verarbeiteterHash = hash;
            _wiederholenAb = null;
            _zuletztGeprueft = DateTime.Now;

            if (datei == null || erg == null)
            {
                Protokoll($"{Path.GetFileName(pfad)}: Lesen fehlgeschlagen ({lesefehler}) – nichts gesendet.");
                ZeigeBeobachtenStatus();
                return;
            }

            ZeigeErgebnis(datei, erg, pfad, waehleTag: false);
            await PruefeUndSendeAsync(datei, erg, trotzdem: false);
        }

        /// <summary>
        /// Bestimmt die geänderten Schultage der Datei und sendet sie – außer bei Fehlern bzw. zu vielen neu leeren Tagen
        /// (dann angehalten, „trotzdem senden“ möglich). trotzdem = true: ohne diese beiden Sperren.
        /// </summary>
        private async Task PruefeUndSendeAsync(UntisDatei datei, UntisErgebnis erg, bool trotzdem)
        {
            string name = Path.GetFileName(datei.Pfad);
            string art = datei.IstDemo ? "Demo-Datei" : "echte Datei";
            _gehalten = null;
            BtnJetztSenden.Visibility = Visibility.Collapsed;

            if (erg.ErsterTag is not DateTime von || erg.LetzterTag is not DateTime bis)
            {
                Protokoll($"{name} ({art}): keine Vertretungen in der Datei – nichts gesendet.");
                ZeigeBeobachtenStatus();
                return;
            }

            var alle = UntisUebersetzung.Schultage(_kal, von, bis);
            var prot = UntisImportProtokoll.Laden(_planOrdner);
            var geaendert = new List<DateTime>();
            var neuLeer = new List<DateTime>();
            foreach (var t in alle)
            {
                var dto = erg.PushFuer(_schuljahr, t);
                if (prot.TagUnveraendert(dto)) continue;
                geaendert.Add(t);
                if (dto.Eintraege.Count == 0 && prot.EintraegeZuletzt(t) is int vorher && vorher > 0) neuLeer.Add(t);
            }
            int unveraendert = alle.Count - geaendert.Count;
            int fehler = erg.Anzahl(UntisProblemArt.Fehler);
            string kopf = $"{name} ({art}, {datei.Zeilen.Count} Zeilen, {von:dd.MM.}–{bis:dd.MM.yyyy}): ";

            if (geaendert.Count == 0)
            {
                Protokoll(kopf + $"keine Änderung – {alle.Count} Schultag(e) wie zuletzt gesendet." +
                          (fehler > 0 ? $" ({fehler} Zeile(n) mit Fehlern, siehe „Probleme“.)" : ""));
                ZeigeBeobachtenStatus();
                return;
            }

            string tageText = TageText(geaendert);
            if (!trotzdem && (fehler > _akzeptierteFehler || neuLeer.Count > MaxNeuLeereTage))
            {
                string grund = fehler > _akzeptierteFehler
                    ? $"{fehler} Zeile(n) mit Fehlern (Reiter „Probleme“{(_akzeptierteFehler > 0 ? $", zuletzt {_akzeptierteFehler}" : "")}) – " +
                      "vielleicht war die Datei noch nicht fertig geschrieben"
                    : $"{neuLeer.Count} Tage würden leer, die vorher Vertretungen hatten ({TageText(neuLeer)}) – ist die Datei vollständig?";
                _gehalten = new Gehalten { Datei = datei, Ergebnis = erg, Grund = grund };
                Protokoll(kopf + $"{geaendert.Count} Tag(e) geändert ({tageText}), NICHT gesendet: {grund}.");
                _log?.Invoke($"Untis (Ordner beobachten): {geaendert.Count} geänderte Tag(e) nicht gesendet – {grund}.");
                BtnJetztSenden.Visibility = Visibility.Visible;
                ZeigeBeobachtenStatus();
                return;
            }

            var einst = OnlineSync.LadeEinstellungen(_planOrdner);
            if (string.IsNullOrWhiteSpace(einst.BaseUrl))
            {
                _gehalten = new Gehalten { Datei = datei, Ergebnis = erg, Grund = "keine Serveradresse in den Online-Einstellungen" };
                Protokoll(kopf + $"{geaendert.Count} Tag(e) geändert, NICHT gesendet: keine Serveradresse in den Online-Einstellungen.");
                BtnJetztSenden.Visibility = Visibility.Visible;
                ZeigeBeobachtenStatus();
                return;
            }

            // Heute und künftige Tage zuerst (morgens zählt der heutige Tag), danach vergangene.
            DateTime heute = DateTime.Today;
            var reihe = geaendert.Where(t => t >= heute).OrderBy(t => t)
                                 .Concat(geaendert.Where(t => t < heute).OrderByDescending(t => t)).ToList();
            var buchungsTage = _buchungsTageHolen();
            var mitBuchung = reihe.Where(t => buchungsTage.Contains(t)).ToList();

            Protokoll(kopf + $"{geaendert.Count} Tag(e) geändert ({tageText}), {unveraendert} unverändert – sende …" +
                      (mitBuchung.Count > 0 ? $" ⚠ ersetzt Buchungen des Vertretungsmoduls am {TageText(mitBuchung)}." : "") +
                      (fehler > 0 ? $" ⚠ {fehler} Zeile(n) mit Fehlern fehlen." : ""));
            var r = await SendeTageAsync(einst, datei, erg, reihe, von, bis, automatisch: true, unveraendert: unveraendert);

            if (r.Fehler != null)
            {
                _wiederholenAb = DateTime.Now.AddSeconds(WiederholenSekunden);
                Protokoll($"  {r.Zahlen} Fehler bei {r.FehlerTag:dd.MM.yyyy}: {EineZeile(r.Fehler)} – neuer Versuch um {_wiederholenAb:HH:mm:ss}.");
            }
            else if (r.Abgebrochen)
            {
                _gehalten = new Gehalten { Datei = datei, Ergebnis = erg, Grund = "Senden abgebrochen" };
                BtnJetztSenden.Visibility = Visibility.Visible;
                Protokoll($"  Abgebrochen – {r.Zahlen} Die übrigen geänderten Tage: „Geänderte Tage trotzdem senden…“.");
            }
            else
            {
                _akzeptierteFehler = fehler;   // gleich viele Fehlerzeilen künftig hinnehmen
                _zuletztGesendet = $"{DateTime.Now:HH:mm} ({r.Gesendet} Tag(e): {TageText(r.Tage)})";
                Protokoll($"  {r.Zahlen}");
            }
            if (r.ProtokollFehler != null)
                Protokoll($"  ⚠ untis_import.json konnte nicht gespeichert werden: {r.ProtokollFehler}");
            ZeigeBeobachtenStatus();
        }

        private async void BtnJetztSenden_Click(object sender, RoutedEventArgs e)
        {
            if (_laeuft || _durchgang || _gehalten == null) return;
            var g = _gehalten;
            using (PauseBeobachten())
            {
                if (MessageBox.Show(this,
                        $"Die geänderten Tage wurden nicht automatisch gesendet:\n{g.Grund}.\n\n" +
                        "Bitte im Reiter „Vorschau“ bzw. „Probleme“ prüfen. Trotzdem alle geänderten Tage der Datei senden? " +
                        "Jeder dieser Tage wird in der App komplett ersetzt.",
                        "Geänderte Tage senden", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                    return;
                try { await PruefeUndSendeAsync(g.Datei, g.Ergebnis, trotzdem: true); }
                catch (Exception ex)
                {
                    Protokoll("Unerwarteter Fehler beim Senden: " + EineZeile(ex.Message));
                    ZeigeBeobachtenStatus();
                }
            }
        }

        // =====================================================
        //  Hilfen
        // =====================================================

        /// <summary>Größe und Zeitstempel der Datei; null = (noch) nicht vorhanden bzw. nicht lesbar.</summary>
        private static (long, DateTime)? Signatur(string pfad)
        {
            try
            {
                var fi = new FileInfo(pfad);
                if (!fi.Exists) return null;
                return (fi.Length, fi.LastWriteTimeUtc);
            }
            catch { return null; }
        }

        /// <summary>Liest die Datei nur, wenn kein anderes Programm sie gerade zum Schreiben offen hat (sonst null).</summary>
        private static byte[]? LiesOhneSchreibsperre(string pfad)
        {
            try
            {
                using var fs = new FileStream(pfad, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string TageText(List<DateTime> tage, int max = 6)
        {
            var s = string.Join(", ", tage.OrderBy(t => t).Take(max).Select(UntisText.TagDatum));
            return tage.Count > max ? s + $" … (+{tage.Count - max})" : s;
        }

        private static string EineZeile(string text)
            => string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

        private void Protokoll(string text)
        {
            _beobProtokoll.Insert(0, $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {text}");
            while (_beobProtokoll.Count > MaxProtokollZeilen) _beobProtokoll.RemoveAt(_beobProtokoll.Count - 1);
            if (IsLoaded) TxtBeobachtenKopf.Text = $"Beobachten ({_beobProtokoll.Count})";
        }

        private void ZeigeBeobachtenStatus()
        {
            if (!IsLoaded) return;
            if (!Beobachtet)
            {
                TxtBeobachten.Text = "Ordner wird nicht beobachtet.";
                TxtBeobachten.Foreground = Brushes.DimGray;
                BrdBeobachten.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4));
                Title = "Vertretungen aus Untis";
                return;
            }
            string text = $"Beobachtet: {_beobPfad}";
            if (_dateiFehltGemeldet) text += " · Datei noch nicht vorhanden";
            if (_zuletztGeprueft != null) text += $" · zuletzt geprüft {_zuletztGeprueft:HH:mm:ss}";
            if (_zuletztGesendet != null) text += $" · zuletzt gesendet {_zuletztGesendet}";
            bool problem = _gehalten != null || _wiederholenAb != null;
            if (_gehalten != null) text += $"\n⚠ Nicht gesendet: {_gehalten.Grund}.";
            else if (_wiederholenAb != null) text += $"\n⚠ Senden fehlgeschlagen – neuer Versuch um {_wiederholenAb:HH:mm:ss} (Reiter „Beobachten“).";
            TxtBeobachten.Text = text;
            TxtBeobachten.Foreground = problem ? Brushes.DarkRed : Brushes.DarkGreen;
            BrdBeobachten.Background = new SolidColorBrush(problem ? Color.FromRgb(0xFF, 0xE0, 0xE0) : Color.FromRgb(0xDC, 0xEF, 0xD9));
            Title = problem ? "Vertretungen aus Untis – beobachtet ⚠ nicht gesendet" : "Vertretungen aus Untis – beobachtet";
        }
    }
}
