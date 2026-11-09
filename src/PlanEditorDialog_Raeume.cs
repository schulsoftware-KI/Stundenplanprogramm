// ============================================================================
// Raumplan im Plan-Editor (Schritt R2) – erweitert die partielle Klasse
// PlanEditorDialog. Konzept: claude/Raumplan_Vorschlag.md (§10).
//
//  - „Ansicht ▾ → Räume" (ChkRaeume): Lehrer- und Klassenplan zeigen je Stunde ein Raum-Abzeichen
//    (oben rechts). Farbe = Zustand: gespeichert (grün), verteilt und noch nicht
//    gespeichert (grau bzw. orange „*"), fest (blau), Raum offen (gelb „?"),
//    kein freier Raum (rot).
//  - „Ansicht ▾ → Raumplan" (ChkRaumPlan): zusätzliche Ansicht je Raum (wie Fachgruppen-/Fächerplan).
//  - Räume werden NUR AUF NACHFRAGE verteilt (Nutzer 09.11.2026). Grundlage ist der
//    gespeicherte Raumplan der Lösung (Blatt „Raumplan"). Verschieben, Tauschen und
//    Entplanen verteilen nichts: Stunden an ihrem Platz behalten ihren Raum, verschobene
//    Stunden sind „Raum offen", bis „Räume verteilen" gedrückt wird (wie Knopf 25
//    „Behalten und ergänzen": nur offene Stunden bekommen einen Raum). Kommt eine Stunde
//    an ihren Ausgangsplatz zurück, bekommt sie ihren Ausgangsraum wieder, wenn er frei ist.
//  - Rechtsklick „Raum ändern": ändert genau diese eine Zeile (= fest).
//  - Gespeichert wird mit „Räume speichern" (nur solange keine Stunde verschoben wurde)
//    bzw. beim „Uebernehmen" für die neue Lösung – nur mit eingeschalteter Raum-Ansicht,
//    bei offenen Stunden mit Nachfrage.
//  Die Raumdaten (UV, FGR, Räume, Raumplan) werden erst gelesen, wenn eine der
//  beiden Raum-Ansichten eingeschaltet ist. Ohne Raum-Ansicht verhält sich der Editor
//  wie vor R2.
// ============================================================================

// Wie PlanEditorDialog.xaml.cs ohne Nullable-Anmerkungen (sonst nur Warnungen).
#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Stundenplan_V2.Raumplanung;

namespace Stundenplan_V2
{
    public partial class PlanEditorDialog
    {
        // ---- Raumdaten aus der Plandatei (erst bei Bedarf gelesen) ----
        private RaumEingabe _raumEingabe;
        private RaumVerzeichnis _raumVerzeichnis;
        private RaumVerteilung.ZeilenVorlage _raumVorlage;
        private bool _raumLadeFehler;          // einmal gescheitert -> nicht bei jedem Zeichnen erneut versuchen
        private string _raumStartAuswahl;      // in EdCfg gemerkter Raum der Raumplan-Ansicht

        // ---- Raumstand der Arbeitskopie (wird NIE von selbst neu verteilt) ----
        // Aktuelle Raumzeilen (eigene Kopien); null = für diese Lösung noch keine Räume.
        private List<RaumZeile> _raumStand;
        // Ausgangsstand (gespeicherter Raumplan bzw. erste Verteilung): für „Zurücksetzen"
        // und für Stunden, die an ihren Ausgangsplatz zurückgeschoben werden.
        private List<RaumZeile> _raumStandStart;
        private string _raumStandLabel;             // Lösung, zu der _raumStand gehört (null = nicht eingerichtet)
        private int[,] _raumStandBelegung;          // Belegung beim letzten Abgleich
        // Geänderte Raumstände anderer Lösungen – nur gemerkt, solange deren Belegung unverändert war.
        private readonly Dictionary<string, (List<RaumZeile> stand, List<RaumZeile> start, int aenderungen)> _raumStandGemerkt
            = new(StringComparer.Ordinal);
        private readonly HashSet<string> _raumErstverteilungGefragt = new(StringComparer.Ordinal);

        private bool _raumplanGespeichertVorhanden;
        private Dictionary<string, RaumZeile> _raumGespeichert = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<RaumZeile>> _raumJeTeil = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _raumSlotNr;

        // Reihenfolge = Gewicht (das „schlimmste" bestimmt die Farbe des Abzeichens).
        private enum RaumZustand { Gespeichert = 0, Vorschau = 1, Fest = 2, FestNeu = 3, Geaendert = 4, Offen = 5, OhneRaum = 6 }

        private bool RaeumeInZellen => ChkRaeume?.IsChecked == true;
        private bool RaumplanSichtbar => BrdRaumPlan != null && BrdRaumPlan.Visibility == Visibility.Visible;
        private bool RaumAnsichtAn => ChkRaeume?.IsChecked == true || ChkRaumPlan?.IsChecked == true;
        private bool RaeumeBenoetigt => (RaeumeInZellen || RaumplanSichtbar) && !_raumLadeFehler;
        private string RaumPlanName => _aktLabel ?? "";

        // =====================================================
        //  Laden und Abgleichen (ohne zu verteilen)
        // =====================================================

        private bool LadeRaumdaten(bool still = false)
        {
            if (_raumEingabe != null) return true;
            if (string.IsNullOrWhiteSpace(_excelPfad))
            {
                if (!still) SetStatus("Räume: Die Plandatei ist nicht bekannt.", true);
                return false;
            }

            var cursorVorher = Mouse.OverrideCursor;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var e = RaumplanStore.LiesOhnePlan(_excelPfad);
                _raumVerzeichnis = RaumVerteilung.BaueVerzeichnis(e);
                _raumVorlage = new RaumVerteilung.ZeilenVorlage(e, _raumVerzeichnis);
                _raumEingabe = e;
                _raumLadeFehler = false;
                FuelleRaumDropdown();
                return true;
            }
            catch (Exception ex)
            {
                _raumLadeFehler = true;
                if (!still)
                    SetStatus("Räume konnten nicht gelesen werden (ist die Plandatei in Excel geöffnet?): " + ex.Message, true);
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = cursorVorher;
            }
        }

        /// <summary>
        /// Stellt sicher, dass der Raumstand zur Lösung und zur Arbeitskopie passt – OHNE
        /// zu verteilen (verschobene Stunden werden „Raum offen"). false = keine Raumdaten.
        /// </summary>
        private bool SichereRaumStand(bool erzwingen = false)
        {
            if (_belegung == null || _blocks == null || _slots == null) return false;
            if (!erzwingen && !RaeumeBenoetigt) return false;
            if (!LadeRaumdaten()) return false;

            try
            {
                if (!string.Equals(_raumStandLabel, RaumPlanName, StringComparison.Ordinal))
                    InitialisiereRaumStand();
                if (_raumStand != null && !GleicheBelegung(_raumStandBelegung, _belegung))
                {
                    GleicheRaumStandAb();
                    _raumStandBelegung = (int[,])_belegung.Clone();
                    BaueRaumIndex();
                    AktualisiereRaumKnopf();
                }
                return true;
            }
            catch (Exception ex)
            {
                _raumStand = null;
                _raumStandLabel = null;
                _raumJeTeil = new Dictionary<string, List<RaumZeile>>(StringComparer.OrdinalIgnoreCase);
                SetStatus("Räume konnten nicht zugeordnet werden: " + ex.Message, true);
                return false;
            }
        }

        // Raumstand für die geladene Lösung einrichten: gemerkter Stand, sonst der
        // gespeicherte Raumplan, sonst keiner (dann „Räume verteilen").
        private void InitialisiereRaumStand()
        {
            string plan = RaumPlanName;
            LeseGespeichertenStand(plan);
            _raumStandLabel = plan;
            _raumStandBelegung = null;      // erzwingt den Abgleich mit der Arbeitskopie

            if (_raumStandGemerkt.Remove(plan, out var gemerkt))
            {
                _raumStand = gemerkt.stand;
                _raumStandStart = gemerkt.start;
            }
            else if (_raumplanGespeichertVorhanden)
            {
                _raumStand = GespeicherteZeilen(plan).Select(z => z.Kopie(plan)).ToList();
                _raumStandStart = _raumStand.Select(z => z.Kopie()).ToList();
            }
            else
            {
                _raumStand = null;
                _raumStandStart = null;
            }
            BaueRaumIndex();
            AktualisiereRaumKnopf();
        }

        private List<RaumZeile> GespeicherteZeilen(string plan)
            => _raumEingabe.Raumplan.Where(z => string.Equals(z.Plan, plan, StringComparison.OrdinalIgnoreCase)).ToList();

        private void LeseGespeichertenStand(string plan)
        {
            var gespeichert = GespeicherteZeilen(plan);
            _raumplanGespeichertVorhanden = gespeichert.Count > 0;
            _raumGespeichert = new Dictionary<string, RaumZeile>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in gespeichert) _raumGespeichert.TryAdd(RaumSchluessel(z), z);
        }

        private static bool GleicheBelegung(int[,] a, int[,] b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            int B = a.GetLength(0), S = a.GetLength(1);
            if (b.GetLength(0) != B || b.GetLength(1) != S) return false;
            for (int i = 0; i < B; i++)
                for (int s = 0; s < S; s++)
                    if (a[i, s] != b[i, s]) return false;
            return true;
        }

        private Dictionary<string, int> RaumSlotNummern()
        {
            if (_raumSlotNr == null)
            {
                _raumSlotNr = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int s = 0; s < _slots.Count; s++) _raumSlotNr.TryAdd(_slots[s].WTag + "|" + _slots[s].Stunde, s);
            }
            return _raumSlotNr;
        }

        private int RaumSlotVon(RaumZeile z)
            => RaumSlotNummern().TryGetValue(z.WTag + "|" + z.Stunde, out int s) ? s : -1;

        /// <summary>
        /// Raumstand an die Arbeitskopie anpassen, OHNE zu verteilen: Zeilen von Stunden, die
        /// nicht mehr liegen, entfallen; neu liegende Stunden bekommen ihre Ausgangszeilen
        /// zurück (wenn deren Räume frei sind), sonst „Raum offen".
        /// </summary>
        private void GleicheRaumStandAb()
        {
            string plan = RaumPlanName;
            var paare = new SortedSet<(int slot, int unr)>();
            for (int s = 0; s < _slots.Count; s++)
                for (int b = 0; b < _blocks.Count; b++)
                    if (_belegung[b, s] == 1) paare.Add((s, _blocks[b].UNr));

            var neu = new List<RaumZeile>();
            var vorhanden = new HashSet<(int, int)>();
            foreach (var z in _raumStand)
            {
                int s = RaumSlotVon(z);
                if (s < 0 || !paare.Contains((s, z.UNr))) continue;
                neu.Add(z);
                vorhanden.Add((s, z.UNr));
            }

            var start = new Dictionary<(int, int), List<RaumZeile>>();
            if (_raumStandStart != null)
                foreach (var z in _raumStandStart)
                {
                    int s = RaumSlotVon(z);
                    if (s < 0) continue;
                    if (!start.TryGetValue((s, z.UNr), out var l)) start[(s, z.UNr)] = l = new List<RaumZeile>();
                    l.Add(z);
                }

            foreach (var p in paare)
            {
                if (vorhanden.Contains(p)) continue;
                if (start.TryGetValue(p, out var alt) && alt.All(z => z.Raum.Length == 0 || RaumFreiIn(neu, p.slot, z)))
                    neu.AddRange(alt.Select(z => z.Kopie(plan)));
                else
                    neu.AddRange(_raumVorlage.Offen(plan, _slots[p.slot].WTag, _slots[p.slot].Stunde, p.unr));
            }
            _raumStand = neu;
        }

        // Ist der Raum der Zeile z im Slot frei (A/B-Woche beachtet; dieselbe UNr blockiert nicht)?
        private bool RaumFreiIn(IEnumerable<RaumZeile> zeilen, int slotIdx, RaumZeile z)
        {
            if (z.Raum.Length == 0) return true;
            var slot = _slots[slotIdx];
            string wg = WochenGruppeVon(z.UNr);
            foreach (var x in zeilen)
            {
                if (x.Raum.Length == 0 || x.UNr == z.UNr) continue;
                if (!string.Equals(x.Raum, z.Raum, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(x.WTag, slot.WTag, StringComparison.OrdinalIgnoreCase) || x.Stunde != slot.Stunde) continue;
                string owg = WochenGruppeVon(x.UNr);
                if (wg == "" || owg == "" || wg == owg) return false;
            }
            return true;
        }

        private void BaueRaumIndex()
        {
            _raumJeTeil = new Dictionary<string, List<RaumZeile>>(StringComparer.OrdinalIgnoreCase);
            if (_raumStand == null) return;
            foreach (var z in _raumStand)
            {
                int s = RaumSlotVon(z);
                if (s < 0) continue;
                string k = TeilSchluessel(s, z.UNr, z.Lehrer);
                if (!_raumJeTeil.TryGetValue(k, out var l)) _raumJeTeil[k] = l = new List<RaumZeile>();
                l.Add(z);
            }
        }

        // Belegung der Arbeitskopie als Slots (wie eine Lös-Spalte).
        private List<(string WTag, int Stunde, List<int> UNrn)> RaumSlotsDerArbeitskopie()
        {
            var slots = new List<(string WTag, int Stunde, List<int> UNrn)>();
            for (int s = 0; s < _slots.Count; s++)
            {
                var unrs = new List<int>();
                for (int b = 0; b < _blocks.Count; b++)
                    if (_belegung[b, s] == 1 && !unrs.Contains(_blocks[b].UNr)) unrs.Add(_blocks[b].UNr);
                slots.Add((_slots[s].WTag, _slots[s].Stunde, unrs));
            }
            return slots;
        }

        /// <summary>
        /// „Räume verteilen" (nur auf Nachfrage): wie Knopf 25 „Behalten und ergänzen" – alle
        /// vorhandenen Räume bleiben, nur offene Stunden (und solche ohne freien Raum) bekommen
        /// einen Raum. Ohne bisherigen Stand: Vorschlag für die ganze Lösung.
        /// </summary>
        private RaumErgebnis VerteileRaeume()
        {
            if (!SichereRaumStand(erzwingen: true)) return null;
            string plan = RaumPlanName;
            var cursorVorher = Mouse.OverrideCursor;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var basis = _raumStand?.Where(z => z.Herkunft != RaumHerkunft.Offen).ToList() ?? new List<RaumZeile>();
                var erg = RaumVerteilung.Verteile(_raumEingabe.MitPlan(plan, RaumSlotsDerArbeitskopie(), basis),
                                                  _raumVerzeichnis, behalten: true);
                bool erstmals = _raumStand == null;
                _raumStand = erg.Zeilen.Select(z => z.Kopie(plan)).ToList();
                if (erstmals || _raumStandStart == null)
                    _raumStandStart = _raumStand.Select(z => z.Kopie()).ToList();
                _raumStandLabel = plan;
                _raumStandBelegung = (int[,])_belegung.Clone();
                BaueRaumIndex();
                AktualisiereRaumKnopf();
                return erg;
            }
            catch (Exception ex)
            {
                SetStatus("Räume konnten nicht verteilt werden: " + ex.Message, true);
                return null;
            }
            finally
            {
                Mouse.OverrideCursor = cursorVorher;
            }
        }

        private static string RaumSchluessel(RaumZeile z)
            => (z.WTag + "|" + z.Stunde + "|" + z.UNr + "|" + z.Lehrer + "|" + z.Wunschraum).ToUpperInvariant();

        private static string TeilSchluessel(int slotIdx, int unr, string lehrer)
            => slotIdx + "|" + unr + "|" + (lehrer ?? "").Trim().ToUpperInvariant();

        // Raum-Zeilen eines Blocks in einem Slot. Mit Auswahl nur die Teile der gewählten
        // Lehrkraft bzw. Klasse (passt keiner – z. B. bei Klassengruppen –, dann alle).
        private List<RaumZeile> RaumZeilenFuer(int blockIdx, int slotIdx, string auswahl, bool lehrerAnsicht)
        {
            var alle = new List<RaumZeile>();
            if (_raumStand == null || _blocks == null || blockIdx < 0 || blockIdx >= _blocks.Count) return alle;
            var block = _blocks[blockIdx];
            foreach (var lehrer in block.Teile.Select(t => t.Lehrer).Distinct(StringComparer.OrdinalIgnoreCase))
                if (_raumJeTeil.TryGetValue(TeilSchluessel(slotIdx, block.UNr, lehrer), out var l))
                    foreach (var z in l)
                        if (!alle.Contains(z)) alle.Add(z);

            if (string.IsNullOrWhiteSpace(auswahl)) return alle;
            var passend = alle.Where(z => lehrerAnsicht
                    ? string.Equals(z.Lehrer, auswahl, StringComparison.OrdinalIgnoreCase)
                    : z.Klassen.Split(',').Any(k => string.Equals(k.Trim(), auswahl, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return passend.Count > 0 ? passend : alle;
        }

        private RaumZustand ZustandVon(RaumZeile z)
        {
            if (z.Herkunft == RaumHerkunft.Offen) return RaumZustand.Offen;
            if (z.Raum.Length == 0) return RaumZustand.OhneRaum;
            bool wieGespeichert = _raumGespeichert.TryGetValue(RaumSchluessel(z), out var g)
                                  && string.Equals(g.Raum, z.Raum, StringComparison.OrdinalIgnoreCase) && g.Fix == z.Fix;
            if (z.Fix) return wieGespeichert ? RaumZustand.Fest : RaumZustand.FestNeu;
            if (wieGespeichert) return RaumZustand.Gespeichert;
            return _raumplanGespeichertVorhanden ? RaumZustand.Geaendert : RaumZustand.Vorschau;
        }

        // Anzahl offener Stunden (Stunde × UNr, nicht Zeilen).
        private int AnzahlOffen()
            => _raumStand == null ? 0
             : _raumStand.Where(z => z.Herkunft == RaumHerkunft.Offen)
                         .Select(z => (z.WTag.ToUpperInvariant(), z.Stunde, z.UNr)).Distinct().Count();

        // Noch nicht gespeicherte Änderungen (von Hand oder neu verteilt) gegenüber dem Blatt „Raumplan".
        private int AnzahlAenderungen()
            => _raumStand == null ? 0
             : _raumStand.Count(z => { var s = ZustandVon(z); return s == RaumZustand.FestNeu || s == RaumZustand.Geaendert; });

        private static (Color hg, Color vg) RaumFarben(RaumZustand z) => z switch
        {
            RaumZustand.Gespeichert => (Color.FromRgb(0xE3, 0xF2, 0xE1), Color.FromRgb(0x1B, 0x5E, 0x20)),
            RaumZustand.Vorschau => (Color.FromRgb(0xEE, 0xEE, 0xEE), Color.FromRgb(0x44, 0x44, 0x44)),
            RaumZustand.Fest or RaumZustand.FestNeu => (Color.FromRgb(0xDC, 0xE6, 0xFF), Color.FromRgb(0x10, 0x32, 0x8A)),
            RaumZustand.Geaendert => (Color.FromRgb(0xFF, 0xE8, 0xCC), Color.FromRgb(0x8A, 0x4B, 0x00)),
            RaumZustand.Offen => (Color.FromRgb(0xFF, 0xF3, 0xA6), Color.FromRgb(0x6B, 0x55, 0x00)),
            _ => (Color.FromRgb(0xF8, 0xD7, 0xDA), Color.FromRgb(0x8B, 0x1A, 0x1A))
        };

        private static string ZustandText(RaumZustand z) => z switch
        {
            RaumZustand.Gespeichert => "gespeichert im Blatt „Raumplan“",
            RaumZustand.Vorschau => "verteilt – für diese Lösung ist noch kein Raumplan gespeichert",
            RaumZustand.Fest => "fest (Fix X im Blatt „Raumplan“)",
            RaumZustand.FestNeu => "von Hand festgelegt – noch nicht gespeichert",
            RaumZustand.Geaendert => "geändert bzw. neu verteilt – noch nicht gespeichert",
            RaumZustand.Offen => "Raum offen – Stunde verschoben, noch nicht verteilt („Räume verteilen“)",
            _ => "kein freier Raum gefunden"
        };

        private static string HerkunftKurz(string herkunft) => herkunft switch
        {
            RaumHerkunft.Fachraum => "Fachraum laut UV",
            RaumHerkunft.Stammraum => "Stammraum laut UV",
            RaumHerkunft.Abgeleitet => "Stammraum der Klasse",
            RaumHerkunft.Fachgruppe => "Raum derselben Fachgruppe",
            RaumHerkunft.Zusatzraum => "Zusatzraum (Ausweichhalle)",
            RaumHerkunft.Ausweich => "Ausweichraum",
            RaumHerkunft.Fest => "fest",
            RaumHerkunft.Untis => "Raum laut Untis",
            RaumHerkunft.Offen => "Raum offen",
            _ => herkunft
        };

        private string RaumZeilenText(RaumZeile z)
        {
            string raum = z.Raum.Length > 0 ? z.Raum : z.Herkunft == RaumHerkunft.Offen ? "?" : "kein Raum";
            string wunsch = z.Wunschraum.Length > 0 && !string.Equals(z.Wunschraum, z.Raum, StringComparison.OrdinalIgnoreCase)
                ? " (Wunsch " + z.Wunschraum + ")" : "";
            return raum + " – " + HerkunftKurz(z.Herkunft) + wunsch + " · " + z.Fach + " " + z.Lehrer;
        }

        // =====================================================
        //  Lehrer-/Klassenplan: Raum-Abzeichen
        // =====================================================

        /// <summary>Raum-Abzeichen für einen Teilbereich (null = nichts anzeigen).</summary>
        private Border BaueRaumAbzeichen(int blockIdx, int slotIdx, string auswahl, bool lehrerAnsicht)
        {
            if (_raumStand == null || slotIdx < 0) return null;
            var zeilen = RaumZeilenFuer(blockIdx, slotIdx, auswahl, lehrerAnsicht)
                .Where(z => z.Raum.Length > 0 || z.Wunschraum.Length > 0)   // ohne Raumangabe in der UV: kein Abzeichen
                .ToList();
            if (zeilen.Count == 0) return null;

            var raeume = zeilen.Where(z => z.Raum.Length > 0).Select(z => z.Raum)
                               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool offen = zeilen.Any(z => z.Herkunft == RaumHerkunft.Offen);
            bool ohne = zeilen.Any(z => z.Raum.Length == 0 && z.Herkunft != RaumHerkunft.Offen);
            var zustand = zeilen.Select(ZustandVon).Max();

            string text = raeume.Count == 0 ? (offen ? "?" : "kein Raum")
                        : raeume[0] + (raeume.Count > 1 ? " +" + (raeume.Count - 1) : "") + (offen ? " ?" : ohne ? " !" : "");
            if (zustand == RaumZustand.Geaendert || zustand == RaumZustand.FestNeu) text += "*";

            var (hg, vg) = RaumFarben(zustand);
            var tip = string.Join("\n", zeilen.Select(z => "• " + RaumZeilenText(z) + "\n   " + ZustandText(ZustandVon(z))))
                      + "\n\nRechtsklick: Raum ändern";

            return new Border
            {
                Background = new SolidColorBrush(hg),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, -1, 0, 0),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(vg)
                },
                ToolTip = tip
            };
        }

        /// <summary>
        /// Inhalt eines Teilbereichs mit den Kennzeichen oben rechts: Raum-Abzeichen und „F"
        /// (fixierte UNr). Ohne beides bleibt es beim reinen Text.
        /// </summary>
        private FrameworkElement BaueTeilInhaltMitEcke(TextBlock tb, bool istFixiert, Border raumAbzeichen)
        {
            if (!istFixiert && raumAbzeichen == null) return tb;

            var grid = new Grid();
            grid.Children.Add(tb);
            var ecke = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            if (raumAbzeichen != null) ecke.Children.Add(raumAbzeichen);
            if (istFixiert)
            {
                // Kleines blaues "F": diese Stunde steht im Fix-UNr-Plan.
                ecke.Children.Add(new TextBlock
                {
                    Text = "F",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x4A, 0xE0)),
                    Margin = new Thickness(raumAbzeichen != null ? 2 : 0, -2, 1, 0)
                });
            }
            grid.Children.Add(ecke);
            return grid;
        }

        // Ergänzt die Details (unten rechts) um die Räume des angeklickten Unterrichts.
        private void ErgaenzeRaumDetails(int blockIdx, int slotIdx)
        {
            if (!RaeumeBenoetigt || !SichereRaumStand() || _raumStand == null || slotIdx < 0 || slotIdx >= _slots.Count) return;
            var zeilen = RaumZeilenFuer(blockIdx, slotIdx, null, true);
            if (zeilen.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.Append("\n\nRäume " + _slots[slotIdx].WTag + " " + _slots[slotIdx].Stunde + ". Std:");
            foreach (var z in zeilen)
            {
                sb.Append("\n   " + z.Lehrer + " | " + z.Fach + " -> " + RaumZeilenText(z));
                sb.Append("\n      " + ZustandText(ZustandVon(z)));
                if (z.Hinweis.Length > 0) sb.Append(" · " + z.Hinweis);
            }
            TxtDetails.Text += sb.ToString();
        }

        // =====================================================
        //  Raumplan-Ansicht
        // =====================================================

        private void FuelleRaumDropdown(string behalten = null)
        {
            if (CboRaum == null || _raumVerzeichnis == null) return;
            behalten ??= CboRaum.SelectedItem as string ?? _raumStartAuswahl;

            var namen = _raumVerzeichnis.Raeume.Select(r => r.Name).ToList();
            if (_raumEingabe != null)
                foreach (var r in _raumEingabe.Raumplan.Select(z => z.Raum).Where(r => r.Length > 0))
                    if (!namen.Contains(r, StringComparer.OrdinalIgnoreCase)) namen.Add(r);

            CboRaum.Items.Clear();
            foreach (var n in namen) CboRaum.Items.Add(n);
            if (CboRaum.Items.Count == 0) return;
            int idx = behalten != null
                ? namen.FindIndex(n => string.Equals(n, behalten, StringComparison.OrdinalIgnoreCase))
                : -1;
            CboRaum.SelectedIndex = idx >= 0 ? idx : 0;
        }

        // Verhindert verschachteltes Zeichnen: Das erste Laden der Raumdaten füllt die
        // Raum-Auswahl, deren SelectionChanged sonst mitten in diesem Aufbau neu zeichnen würde.
        private bool _raumplanZeichnetGerade;

        private void ZeichneRaumplanGrid()
        {
            if (RaumplanGrid == null || !RaumplanSichtbar || _raumplanZeichnetGerade) return;
            _raumplanZeichnetGerade = true;
            try
            {
                ZeichneRaumplanGridKern();
            }
            finally
            {
                _raumplanZeichnetGerade = false;
            }
        }

        private void ZeichneRaumplanGridKern()
        {
            // Erst Daten sichern (kann die Raum-Auswahl füllen), dann leeren und zeichnen.
            bool bereit = _belegung != null && _blocks != null && SichereRaumStand();
            if (bereit && CboRaum.Items.Count == 0) FuelleRaumDropdown();

            RaumplanGrid.Children.Clear();
            RaumplanGrid.ColumnDefinitions.Clear();
            RaumplanGrid.RowDefinitions.Clear();
            LblRaumplanKopf.ClearValue(TextBlock.ForegroundProperty);
            if (_belegung == null || _blocks == null) return;

            if (!bereit)
            {
                LblRaumplanKopf.Text = "RAUMPLAN – Räume nicht verfügbar";
                return;
            }
            if (_raumStand == null)
            {
                LblRaumplanKopf.Text = "RAUMPLAN – für diese Lösung gibt es noch keine Räume („Räume verteilen“)";
                return;
            }
            string raum = CboRaum.SelectedItem as string;
            if (raum == null) { LblRaumplanKopf.Text = "RAUMPLAN"; return; }

            var jeSlot = new Dictionary<int, List<RaumZeile>>();
            foreach (var z in _raumStand)
            {
                if (!string.Equals(z.Raum, raum, StringComparison.OrdinalIgnoreCase)) continue;
                int s = RaumSlotVon(z);
                if (s < 0) continue;
                if (!jeSlot.TryGetValue(s, out var l)) jeSlot[s] = l = new List<RaumZeile>();
                l.Add(z);
            }

            RaumplanGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            foreach (var _ in _tage)
                RaumplanGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FgZellBreite) });
            RaumplanGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var _ in _stunden)
                RaumplanGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FgZellHoehe) });

            for (int ti = 0; ti < _tage.Count; ti++)
            {
                var tb = new TextBlock
                {
                    Text = _tage[ti], FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2)
                };
                Grid.SetRow(tb, 0); Grid.SetColumn(tb, ti + 1);
                RaumplanGrid.Children.Add(tb);
            }
            for (int hi = 0; hi < _stunden.Count; hi++)
            {
                var tb = new TextBlock
                {
                    Text = _stunden[hi].ToString(), FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(tb, hi + 1); Grid.SetColumn(tb, 0);
                RaumplanGrid.Children.Add(tb);
            }

            int doppelt = 0;
            for (int ti = 0; ti < _tage.Count; ti++)
                for (int hi = 0; hi < _stunden.Count; hi++)
                {
                    int slotIdx = FindeSlot(_tage[ti], _stunden[hi]);
                    jeSlot.TryGetValue(slotIdx, out var zeilen);
                    var zelle = BaueRaumZelle(slotIdx, zeilen ?? new List<RaumZeile>(), out bool konflikt);
                    if (konflikt) doppelt++;
                    Grid.SetRow(zelle, hi + 1); Grid.SetColumn(zelle, ti + 1);
                    RaumplanGrid.Children.Add(zelle);
                }

            var info = _raumVerzeichnis.Finde(raum);
            string art = info == null ? "" : " (" + info.ArtText
                         + (info.Fachgruppe.Length > 0 ? ", " + info.Fachgruppe : "")
                         + (info.Bereich.Length > 0 ? ", " + info.Bereich : "") + ")";
            int offen = AnzahlOffen();
            LblRaumplanKopf.Text = "RAUMPLAN " + raum + art + " — " + jeSlot.Count + " Std. belegt"
                                   + (doppelt > 0 ? " · " + doppelt + "× doppelt belegt" : "")
                                   + (offen > 0 ? " · " + offen + " Std. (alle Räume) offen" : "");
            if (doppelt > 0) LblRaumplanKopf.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
        }

        private Border BaueRaumZelle(int slotIdx, List<RaumZeile> zeilen, out bool konflikt)
        {
            konflikt = false;
            var border = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(1),
                Tag = slotIdx
            };
            if (slotIdx < 0)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                return border;
            }
            border.Background = Brushes.White;
            if (zeilen.Count == 0) return border;

            // Je UNr ein Eintrag (Team-Teaching = eine UNr mit mehreren Zeilen).
            var gruppen = zeilen.GroupBy(z => z.UNr).ToList();
            var eintraege = new List<(int blockIdx, List<RaumZeile> zeilen, string wg)>();
            foreach (var g in gruppen)
            {
                int b = -1;
                for (int i = 0; i < _blocks.Count; i++)
                    if (_blocks[i].UNr == g.Key && _belegung[i, slotIdx] == 1) { b = i; break; }
                string wg = b >= 0 ? (_blocks[b].WochenGruppe ?? "").Trim() : "";
                eintraege.Add((b, g.ToList(), wg));
            }

            // Doppelt belegt = mehr als ein Unterricht in derselben Woche (A und B dürfen sich teilen).
            int a = eintraege.Count(x => x.wg != "B"), bw = eintraege.Count(x => x.wg != "A");
            konflikt = a > 1 || bw > 1;
            bool abGeteilt = !konflikt && eintraege.Count > 1;
            if (konflikt)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
                border.BorderThickness = new Thickness(2);
            }
            border.ToolTip = "Belegung (" + eintraege.Count + "):\n" +
                             string.Join("\n", zeilen.Select(z => "• UNr " + z.UNr + " · " + z.Klassen + " · " + RaumZeilenText(z)));

            var stapel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 14, 0, 0) };
            foreach (var e in eintraege.Take(FgMaxBloeckeProZelle))
                stapel.Children.Add(BaueRaumEintrag(e.blockIdx, slotIdx, e.zeilen, konflikt));
            if (eintraege.Count > FgMaxBloeckeProZelle)
                stapel.Children.Add(new TextBlock
                {
                    Text = "+" + (eintraege.Count - FgMaxBloeckeProZelle) + " weitere (Tooltip)",
                    FontSize = 10, Margin = new Thickness(2, 0, 0, 0)
                });

            var inhalt = new Grid();
            inhalt.Children.Add(stapel);
            if (konflikt || abGeteilt)
            {
                inhalt.Children.Add(new Border
                {
                    Background = new SolidColorBrush(konflikt ? Color.FromRgb(0xF8, 0xD7, 0xDA) : Color.FromRgb(0xEE, 0xEE, 0xEE)),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(3, 0, 3, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = konflikt ? eintraege.Count + " – doppelt" : "A · B",
                        FontSize = 10, FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(konflikt ? Color.FromRgb(0x8B, 0x1A, 0x1A) : Color.FromRgb(0x55, 0x55, 0x55))
                    }
                });
            }
            border.Child = inhalt;
            return border;
        }

        private Border BaueRaumEintrag(int blockIdx, int slotIdx, List<RaumZeile> zeilen, bool konflikt)
        {
            var z0 = zeilen[0];
            var zustand = zeilen.Select(ZustandVon).Max();
            var (hg, _) = RaumFarben(zustand);
            bool hervorheben = blockIdx >= 0 && _highlightBloecke.Contains(blockIdx);

            string klassen = string.Join(",", zeilen.Select(z => z.Klassen).Distinct());
            string lehrer = string.Join(",", zeilen.Select(z => z.Lehrer).Distinct());
            string faecher = string.Join(",", zeilen.Select(z => z.Fach).Distinct());
            string wg = blockIdx >= 0 ? (_blocks[blockIdx].WochenGruppe ?? "").Trim() : "";
            string stern = zustand == RaumZustand.Geaendert || zustand == RaumZustand.FestNeu ? " *" : "";

            var tb = new TextBlock { TextWrapping = TextWrapping.NoWrap, FontSize = 11 };
            tb.Inlines.Add(new System.Windows.Documents.Run(klassen + " · " + lehrer + (wg == "" ? "" : "  [" + wg + "]"))
            { FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new System.Windows.Documents.Run("\nUNr " + z0.UNr + " · " + faecher + " · " + HerkunftKurz(z0.Herkunft) + stern)
            { FontSize = 10, Foreground = Brushes.DarkSlateGray });

            var rahmen = new Border
            {
                Background = konflikt ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0xC1)) : new SolidColorBrush(hg),
                BorderBrush = hervorheben ? MarkierungBrush() : Brushes.Gray,
                BorderThickness = hervorheben ? new Thickness(_markierungDicke * MarkierungFaktorEinAnsicht) : new Thickness(0.5),
                Margin = new Thickness(1, 0, 1, 1),
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = blockIdx >= 0 ? Cursors.Hand : Cursors.Arrow,
                Child = tb,
                ToolTip = string.Join("\n", zeilen.Select(z => "UNr " + z.UNr + " · " + z.Klassen + " · " + RaumZeilenText(z)
                                                               + "\n   " + ZustandText(ZustandVon(z))
                                                               + (z.Hinweis.Length > 0 ? "\n   " + z.Hinweis : "")))
                          + (blockIdx >= 0 ? "\nKlick: Lehrer- und Klassenplan auf diesen Unterricht umstellen" : "")
            };

            if (blockIdx >= 0)
            {
                int idx = blockIdx, slot = slotIdx;
                rahmen.MouseLeftButtonDown += (s, e) =>
                {
                    ZeigeDetails(idx);
                    ErgaenzeRaumDetails(idx, slot);
                    SynchronisiereBeidePlaeneAufBlock(idx);
                };
            }
            return rahmen;
        }

        // Klick im Lehrer-/Klassenplan: Raumplan-Ansicht auf den Raum dieses Unterrichts stellen.
        private void SpringeZuRaum(int blockIdx, int slotIdx)
        {
            if (!RaumplanSichtbar || !SichereRaumStand() || _raumStand == null) return;
            string raum = RaumZeilenFuer(blockIdx, slotIdx, null, true)
                .Select(z => z.Raum).FirstOrDefault(r => r.Length > 0);
            if (!SetzeComboAuf(CboRaum, raum)) ZeichneRaumplanGrid();
        }

        // ---- Bedienelemente ----

        private void ChkRaeume_Changed(object sender, RoutedEventArgs e)
        {
            AktualisiereRaumKnopf();
            if (!_initialisiert || _belegung == null) return;
            _raumLadeFehler = false;   // neuer Versuch, falls die Datei vorher gesperrt war
            ZeichneNachRaumAenderung();
            if (ChkRaeume.IsChecked == true) FrageErsteVerteilung();
        }

        private void ChkRaumPlan_Changed(object sender, RoutedEventArgs e)
        {
            if (BrdRaumPlan == null) return;
            BrdRaumPlan.Visibility = ChkRaumPlan.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            AktualisiereRaumKnopf();
            if (!_initialisiert || _belegung == null) return;
            if (ChkRaumPlan.IsChecked == true)
            {
                _raumLadeFehler = false;
                ZeichneRaumplanGrid();
                FrageErsteVerteilung();
            }
        }

        // Beim Einschalten einer Raum-Ansicht (durch den Nutzer): Gibt es für die Lösung noch
        // keine Räume, einmal nachfragen. Beim Öffnen des Editors oder beim Lösungswechsel wird
        // nicht gefragt – dann steht der Knopf „Räume verteilen" bereit.
        private void FrageErsteVerteilung()
        {
            if (!RaeumeBenoetigt || !SichereRaumStand() || _raumStand != null) return;
            if (!_raumErstverteilungGefragt.Add(RaumPlanName)) return;
            if (MessageBox.Show(this,
                    "Für die Lösung „" + RaumPlanName + "“ gibt es noch keinen Raumplan.\n\n" +
                    "Räume jetzt verteilen (wie Knopf 25, noch nicht gespeichert)?\n\n" +
                    "Später jederzeit über „Räume verteilen“.",
                    "Räume", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                BtnRaeumeVerteilen_Click(this, new RoutedEventArgs());
        }

        private void CboRaum_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialisiert || _belegung == null) return;
            ZeichneRaumplanGrid();
        }

        private void BtnNaechsterRaum_Click(object sender, RoutedEventArgs e)
        {
            if (CboRaum.Items.Count == 0) return;
            CboRaum.SelectedIndex = (CboRaum.SelectedIndex + 1) % CboRaum.Items.Count;
        }

        private void BtnVorigerRaum_Click(object sender, RoutedEventArgs e)
        {
            if (CboRaum.Items.Count == 0) return;
            int n = CboRaum.Items.Count;
            CboRaum.SelectedIndex = (CboRaum.SelectedIndex - 1 + n) % n;
        }

        private void AktualisiereRaumKnopf()
        {
            if (BtnRaeumeSpeichern == null || BtnRaeumeVerteilen == null) return;
            bool an = RaumAnsichtAn;
            bool geladen = an && _raumEingabe != null && !_raumLadeFehler;
            bool aktuell = geladen && _raumStand != null
                           && string.Equals(_raumStandLabel, RaumPlanName, StringComparison.Ordinal);
            int offen = aktuell ? AnzahlOffen() : 0;

            BtnRaeumeVerteilen.Visibility = geladen && (!aktuell || offen > 0) ? Visibility.Visible : Visibility.Collapsed;
            BtnRaeumeVerteilen.Content = aktuell ? "Räume verteilen (" + offen + " offen)" : "Räume verteilen";

            BtnRaeumeSpeichern.Visibility = aktuell ? Visibility.Visible : Visibility.Collapsed;
            int n = aktuell ? AnzahlAenderungen() : 0;
            BtnRaeumeSpeichern.Content = n > 0 ? "Räume speichern (" + n + " geändert)" : "Räume speichern";
        }

        private void BtnRaeumeVerteilen_Click(object sender, RoutedEventArgs e)
        {
            if (_belegung == null) return;
            bool erstmals = !SichereRaumStand(erzwingen: true) || _raumStand == null;
            var erg = VerteileRaeume();
            if (erg == null) return;
            int ohne = erg.Anzahl(RaumHerkunft.OhneRaum);
            NachRaumAenderung(erstmals
                ? "Räume verteilt (wie Knopf 25, noch nicht gespeichert): " + erg.Belegungen + " Raumbelegungen" +
                  (ohne > 0 ? ", " + ohne + " ohne freien Raum (rot)" : "") + "."
                : "Räume verteilt: " + erg.Neu + " Raumbelegung(en) neu vergeben" +
                  (ohne > 0 ? ", " + ohne + " ohne freien Raum (rot)" : "") +
                  " – alle übrigen Räume sind geblieben (noch nicht gespeichert).");
        }

        // =====================================================
        //  Raum von Hand ändern (Kontextmenü)
        // =====================================================

        /// <summary>Hängt die Einträge „Raum ändern …" an das Kontextmenü eines Teilbereichs an.</summary>
        private void FuegeRaumMenueHinzu(ContextMenu menu, int blockIdx, int slotIdx)
        {
            if (!RaeumeBenoetigt || !SichereRaumStand()) return;
            if (_raumStand == null)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                var mv = new MenuItem { Header = "Räume verteilen (für diese Lösung gibt es noch keine Räume)" };
                mv.Click += (s, e) => BtnRaeumeVerteilen_Click(this, new RoutedEventArgs());
                menu.Items.Add(mv);
                return;
            }
            var zeilen = RaumZeilenFuer(blockIdx, slotIdx, null, true);
            int offen = AnzahlOffen();
            if (zeilen.Count == 0 && offen == 0) return;

            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            foreach (var z in zeilen)
            {
                var zeile = z;
                string jetzt = z.Raum.Length > 0 ? z.Raum : z.Herkunft == RaumHerkunft.Offen ? "Raum offen" : "kein Raum";
                var sub = new MenuItem { Header = "Raum ändern: " + jetzt + "  (" + z.Fach + ", " + z.Lehrer + ")" };
                bool zusatzEintraege = false;

                if (z.Fix)
                {
                    var auf = new MenuItem
                    {
                        Header = z.Raum.Length > 0 ? "Festlegung aufheben (" + z.Raum + " bleibt)" : "Festlegung aufheben"
                    };
                    auf.Click += (s, e) => HebeRaumFestlegungAuf(zeile);
                    sub.Items.Add(auf);
                    zusatzEintraege = true;
                }
                if (_raumGespeichert.TryGetValue(RaumSchluessel(z), out var gespeichert)
                    && (!string.Equals(gespeichert.Raum, z.Raum, StringComparison.OrdinalIgnoreCase) || gespeichert.Fix != z.Fix))
                {
                    var g = gespeichert;
                    bool gFrei = g.Raum.Length == 0 || string.Equals(g.Raum, z.Raum, StringComparison.OrdinalIgnoreCase)
                                 || RaumFreiIn(_raumStand, slotIdx, g);
                    var wie = new MenuItem
                    {
                        Header = "Wie gespeichert: " + (g.Raum.Length > 0 ? g.Raum : "kein Raum") + (g.Fix ? " (fest)" : "")
                                 + (gFrei ? "" : " – belegt"),
                        IsEnabled = gFrei
                    };
                    wie.Click += (s, e) => SetzeWieGespeichert(zeile, g);
                    sub.Items.Add(wie);
                    zusatzEintraege = true;
                }
                if (zusatzEintraege) sub.Items.Add(new Separator());

                var freieListe = FreieRaeume(slotIdx, z);
                if (freieListe.Count == 0)
                    sub.Items.Add(new MenuItem { Header = "(kein freier Raum in dieser Stunde)", IsEnabled = false });
                foreach (var (name, anzeige) in freieListe)
                {
                    string r = name;
                    var mi = new MenuItem { Header = anzeige };
                    mi.Click += (s, e) => SetzeRaumVonHand(zeile, r);
                    sub.Items.Add(mi);
                }
                menu.Items.Add(sub);
            }

            if (offen > 0)
            {
                if (zeilen.Count > 0) menu.Items.Add(new Separator());
                var mv = new MenuItem { Header = "Räume verteilen (" + offen + " Std. offen; alle übrigen Räume bleiben)" };
                mv.Click += (s, e) => BtnRaeumeVerteilen_Click(this, new RoutedEventArgs());
                menu.Items.Add(mv);
            }
        }

        // Freie Räume in diesem Slot für die Zeile (A/B-Woche beachtet, dieselbe UNr blockiert
        // nicht). Reihenfolge: Wunschraum, gleiche Fachgruppe, Stammraum laut UV, übrige Räume
        // des Verzeichnisses (Sonderräume nur, wenn sie der Wunschraum sind).
        private List<(string name, string anzeige)> FreieRaeume(int slotIdx, RaumZeile z)
        {
            var liste = new List<(string, string)>();
            if (_raumStand == null || _raumVerzeichnis == null) return liste;

            string wg = WochenGruppeVon(z.UNr);
            var slot = _slots[slotIdx];
            var belegt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in _raumStand)
            {
                if (x.Raum.Length == 0 || x.UNr == z.UNr) continue;
                if (!string.Equals(x.WTag, slot.WTag, StringComparison.OrdinalIgnoreCase) || x.Stunde != slot.Stunde) continue;
                string owg = WochenGruppeVon(x.UNr);
                if (wg == "" || owg == "" || wg == owg) belegt.Add(x.Raum);
            }

            var reihenfolge = new List<string>();
            void Dazu(string r)
            {
                if (string.IsNullOrWhiteSpace(r)) return;
                if (string.Equals(r, z.Raum, StringComparison.OrdinalIgnoreCase)) return;
                if (belegt.Contains(r) || reihenfolge.Contains(r, StringComparer.OrdinalIgnoreCase)) return;
                reihenfolge.Add(r);
            }

            var wunsch = _raumVerzeichnis.Finde(z.Wunschraum);
            Dazu(z.Wunschraum);
            if (wunsch != null && wunsch.Fachgruppe.Length > 0)
                foreach (var r in _raumVerzeichnis.Raeume.Where(r => r.Art != RaumArt.Sonderraum
                                 && string.Equals(r.Fachgruppe, wunsch.Fachgruppe, StringComparison.OrdinalIgnoreCase)))
                    Dazu(r.Name);
            if (_raumEingabe != null && _raumEingabe.Unterrichte.TryGetValue(z.UNr, out var teile))
                foreach (var t in teile.Where(t => string.Equals(t.Lehrer, z.Lehrer, StringComparison.OrdinalIgnoreCase)))
                    foreach (var r in t.Stammraeume) Dazu(r);
            foreach (var r in _raumVerzeichnis.Raeume.Where(r => r.Art != RaumArt.Sonderraum))
                Dazu(r.Name);

            foreach (var r in reihenfolge)
            {
                var info = _raumVerzeichnis.Finde(r);
                string zusatz = info == null ? ""
                    : "  (" + info.ArtText + (info.Fachgruppe.Length > 0 ? ", " + info.Fachgruppe : "")
                      + (info.Bereich.Length > 0 ? ", " + info.Bereich : "") + ")";
                if (string.Equals(r, z.Wunschraum, StringComparison.OrdinalIgnoreCase)) zusatz += "  – Wunschraum";
                liste.Add((r, r + zusatz));
            }
            return liste;
        }

        private string WochenGruppeVon(int unr)
            => _raumEingabe != null && _raumEingabe.Unterrichte.TryGetValue(unr, out var t) && t.Count > 0
                ? t[0].WochenGruppe : "";

        // Genau diese eine Zeile ändern – kein anderer Raum wird angefasst.
        private void SetzeRaumVonHand(RaumZeile z, string raum)
        {
            z.Raum = raum;
            z.Fix = true;
            z.Herkunft = RaumHerkunft.Fest;
            z.Hinweis = "von Hand im Planeditor";
            NachRaumAenderung("UNr " + z.UNr + " (" + z.Fach + ", " + z.Lehrer + ") " + z.WTag + " " + z.Stunde +
                              ". Std: Raum " + raum + " von Hand festgelegt – noch nicht gespeichert.");
        }

        // Nur das X entfernen – der Raum bleibt (verteilt wird nur auf Nachfrage).
        private void HebeRaumFestlegungAuf(RaumZeile z)
        {
            z.Fix = false;
            if (z.Raum.Length > 0)
                z.Herkunft = _raumVorlage?.HerkunftVon(z) ?? RaumHerkunft.Ausweich;
            else
                z.Herkunft = z.Wunschraum.Length > 0 ? RaumHerkunft.Offen : RaumHerkunft.OhneRaum;
            z.Hinweis = "Festlegung im Planeditor aufgehoben";
            NachRaumAenderung("UNr " + z.UNr + " " + z.WTag + " " + z.Stunde + ". Std: Festlegung aufgehoben" +
                              (z.Raum.Length > 0 ? ", Raum " + z.Raum + " bleibt" : "") + " (noch nicht gespeichert).");
        }

        private void SetzeWieGespeichert(RaumZeile z, RaumZeile gespeichert)
        {
            int i = _raumStand?.IndexOf(z) ?? -1;
            if (i < 0) return;
            _raumStand[i] = gespeichert.Kopie(RaumPlanName);
            BaueRaumIndex();
            NachRaumAenderung("UNr " + z.UNr + " " + z.WTag + " " + z.Stunde + ". Std: Raum wieder wie gespeichert (" +
                              (gespeichert.Raum.Length > 0 ? gespeichert.Raum : "kein Raum") + ").");
        }

        private void ZeichneNachRaumAenderung()
        {
            if (_vergleichsModus)
            {
                ZeichneVergleichsModus();
                ZeichneRaumplanGrid();
            }
            else ZeichneBeideGrids();
        }

        private void NachRaumAenderung(string status)
        {
            AktualisiereRaumKnopf();
            ZeichneNachRaumAenderung();
            SetStatus(status, false);
        }

        // „Zurücksetzen": Räume zurück auf den Ausgangsstand der Lösung.
        private void VerwerfeRaumAenderungen()
        {
            string plan = RaumPlanName;
            _raumStandGemerkt.Remove(plan);
            if (!string.Equals(_raumStandLabel, plan, StringComparison.Ordinal)) return;
            _raumStand = _raumStandStart?.Select(z => z.Kopie(plan)).ToList();
            _raumStandBelegung = null;      // beim nächsten Zeichnen mit der Arbeitskopie abgleichen
            BaueRaumIndex();
            AktualisiereRaumKnopf();
        }

        /// <summary>
        /// Vor dem Laden einer anderen Lösung: geänderte Räume der bisherigen Lösung merken,
        /// solange ihre Stunden nicht verschoben wurden (ein veränderter Plan geht beim
        /// Wechsel ohnehin verloren).
        /// </summary>
        private void MerkeRaumStandVorWechsel(string neuLabel)
        {
            if (_raumStandLabel == null || string.Equals(_raumStandLabel, neuLabel ?? "", StringComparison.Ordinal)) return;
            if (_raumStand != null && string.Equals(_raumStandLabel, RaumPlanName, StringComparison.Ordinal)
                && BelegungUnveraendert() && GleicheBelegung(_raumStandBelegung, _belegung))
            {
                int n = AnzahlAenderungen();
                if (n > 0) _raumStandGemerkt[_raumStandLabel] = (_raumStand, _raumStandStart, n);
            }
            _raumStand = null;
            _raumStandStart = null;
            _raumStandLabel = null;
            _raumStandBelegung = null;
            _raumJeTeil = new Dictionary<string, List<RaumZeile>>(StringComparer.OrdinalIgnoreCase);
        }

        private bool BelegungUnveraendert() => GleicheBelegung(_belegung, _belegungOriginal);

        // =====================================================
        //  Speichern
        // =====================================================

        // Offene Stunden vor dem Speichern: verteilen (Ja), als „Raum offen" speichern (Nein)
        // oder abbrechen. Ohne offene Stunden: null.
        private MessageBoxResult? FrageOffeneVorSpeichern(string titel, string jaText, string abbrechenText)
        {
            int offen = AnzahlOffen();
            if (offen == 0) return null;
            return MessageBox.Show(this,
                offen + " verschobene Stunde(n) haben noch keinen Raum.\n\n" +
                "Jetzt verteilen? Dabei bekommen nur diese Stunden einen Raum; alle übrigen Räume bleiben.\n\n" +
                "Ja = " + jaText + "\nNein = mit „Raum offen“ speichern\nAbbrechen = " + abbrechenText,
                titel, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        }

        private void BtnRaeumeSpeichern_Click(object sender, RoutedEventArgs e)
        {
            if (_belegung == null || string.IsNullOrWhiteSpace(_aktLabel)) return;
            // Alle Lösungen des Editors (Lös, „[Gesichert] …“, „Plan“) kennt auch Knopf 25.
            if (!BelegungUnveraendert())
            {
                MessageBox.Show(this,
                    "Der Plan wurde im Editor verändert. Die Räume werden zusammen mit dem Plan über „Uebernehmen“ " +
                    "gespeichert (für die neue Lösung).\n\n„Räume speichern“ gilt nur, solange keine Stunde verschoben wurde.",
                    "Räume speichern", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!SichereRaumStand(erzwingen: true)) return;
            if (_raumStand == null)
            {
                MessageBox.Show(this, "Für diese Lösung gibt es noch keine Räume – zuerst „Räume verteilen“.",
                    "Räume speichern", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var antwort = FrageOffeneVorSpeichern("Räume speichern", "verteilen und speichern", "nicht speichern");
            if (antwort == MessageBoxResult.Cancel) return;
            if (antwort == MessageBoxResult.Yes && VerteileRaeume() == null) return;
            if (antwort == null)
            {
                int aenderungen = AnzahlAenderungen();
                string frage = "Raumplan für die Lösung „" + _aktLabel + "“ speichern?\n\n" +
                               _raumStand.Count(z => z.Raum.Length > 0) + " Raumbelegungen" +
                               (aenderungen > 0 ? ", davon " + aenderungen + " geändert" : "") + ".\n" +
                               (_raumplanGespeichertVorhanden
                                   ? "Die bisherigen Zeilen dieser Lösung im Blatt „Raumplan“ werden ersetzt."
                                   : "Für diese Lösung gibt es noch keinen Raumplan – er wird angelegt.") +
                               "\n\nDie Plandatei darf dabei nicht in Excel geöffnet sein.";
                if (MessageBox.Show(this, frage, "Räume speichern", MessageBoxButton.YesNo, MessageBoxImage.Question)
                    != MessageBoxResult.Yes) return;
            }

            string plan = RaumPlanName;
            try
            {
                SpeichereRaumplan(plan, _raumStand);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, (ex is System.IO.IOException
                        ? "Speichern fehlgeschlagen – ist die Plandatei gerade in Excel geöffnet?\n\n"
                        : "Speichern fehlgeschlagen:\n\n") + ex.Message,
                    "Räume speichern", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Gespeicherter Stand = neuer Ausgangsstand.
            LeseGespeichertenStand(plan);
            _raumStandStart = _raumStand.Select(z => z.Kopie()).ToList();
            NachRaumAenderung("Raumplan für „" + plan + "“ gespeichert (Blatt „Raumplan“).");
        }

        // Schreibt die Zeilen als Raumplan der Lösung und hält die gelesenen Daten aktuell.
        private void SpeichereRaumplan(string plan, IEnumerable<RaumZeile> zeilen)
        {
            var liste = zeilen.Select(z => z.Kopie(plan)).ToList();
            var cursorVorher = Mouse.OverrideCursor;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                RaumplanStore.Speichern(_excelPfad, plan, liste, _raumVerzeichnis);
            }
            finally
            {
                Mouse.OverrideCursor = cursorVorher;
            }
            _raumEingabe.Raumplan.RemoveAll(z => string.Equals(z.Plan, plan, StringComparison.OrdinalIgnoreCase));
            _raumEingabe.Raumplan.AddRange(liste);
            foreach (var raum in _raumVerzeichnis.Raeume.Where(x => x.Ergaenzt))
            {
                raum.Ergaenzt = false;          // steht jetzt im Blatt „Räume“
                raum.QuelleText = "Blatt „Räume“";
            }
            _raumVerzeichnis.AusBlatt = true;
        }

        /// <summary>
        /// Vor dem „Uebernehmen": welche Raumzeilen die neue Lösung bekommt. null = keinen Raumplan
        /// schreiben – Raum-Ansichten aus (dann wie vor R2: die Datei wird nicht gelesen), Raumdaten
        /// nicht lesbar oder „Nein". Bei offenen Stunden bzw. ohne Räume wird nachgefragt;
        /// „Abbrechen" bricht das ganze Übernehmen ab.
        /// </summary>
        private List<RaumZeile> RaeumeFuerUebernahme(out bool abbrechen)
        {
            abbrechen = false;
            if (!RaumAnsichtAn || _belegung == null) return null;
            if (!SichereRaumStand(erzwingen: true)) return null;

            if (_raumStand == null)
            {
                var a = MessageBox.Show(this,
                    "Für die Lösung „" + RaumPlanName + "“ gibt es noch keine Räume.\n\n" +
                    "Räume für die neue Lösung jetzt verteilen (wie Knopf 25) und mitspeichern?\n\n" +
                    "Ja = verteilen und mitspeichern\nNein = ohne Raumplan übernehmen\nAbbrechen = nicht übernehmen",
                    "Uebernehmen – Räume", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (a == MessageBoxResult.Cancel) { abbrechen = true; return null; }
                if (a == MessageBoxResult.No || VerteileRaeume() == null) return null;
            }
            else
            {
                var a = FrageOffeneVorSpeichern("Uebernehmen – Räume", "verteilen und mitspeichern", "nicht übernehmen");
                if (a == MessageBoxResult.Cancel) { abbrechen = true; return null; }
                if (a == MessageBoxResult.Yes) VerteileRaeume();
            }
            return _raumStand?.Select(z => z.Kopie()).ToList();
        }

        /// <summary>
        /// Nach dem Schreiben der neuen Lösung: deren Raumplan speichern. Liefert einen Satz für
        /// die Statuszeile oder null. Fehler brechen das Übernehmen nie ab.
        /// </summary>
        private string UebernehmeRaeumeFuer(string neuLabel, List<RaumZeile> zeilen)
        {
            if (zeilen == null) return null;
            try
            {
                SpeichereRaumplan(neuLabel, zeilen);
                int offen = zeilen.Where(z => z.Herkunft == RaumHerkunft.Offen)
                                  .Select(z => (z.WTag.ToUpperInvariant(), z.Stunde, z.UNr)).Distinct().Count();
                // Der Stand steckt jetzt in der neuen Lösung – für die alte nichts merken.
                _raumStand = null;
                _raumStandStart = null;
                _raumStandLabel = null;
                _raumStandBelegung = null;
                return "Raumplan übernommen (" + zeilen.Count(z => z.Raum.Length > 0) + " Raumbelegungen" +
                       (offen > 0 ? ", " + offen + " Std. Raum offen" : "") + ").";
            }
            catch (Exception ex)
            {
                return "Raumplan NICHT übernommen: " + ex.Message;
            }
        }

        // Warnung beim Schließen, wenn Räume geändert und nicht gespeichert wurden (nur solange
        // „Räume speichern" möglich ist; ein verschobener Plan geht beim Schließen ohnehin verloren).
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel) return;
            int n = _raumStandGemerkt.Values.Sum(x => x.aenderungen);
            if (_raumStand != null && string.Equals(_raumStandLabel, RaumPlanName, StringComparison.Ordinal)
                && BelegungUnveraendert())
                n += AnzahlAenderungen();
            if (n == 0) return;
            if (MessageBox.Show(this,
                    n + " Raumänderung(en) sind noch nicht gespeichert („Räume speichern“).\n\n" +
                    "Trotzdem schließen?",
                    "Plan-Editor", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
        }
    }
}
