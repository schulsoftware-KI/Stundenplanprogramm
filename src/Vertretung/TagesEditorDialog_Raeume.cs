#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stundenplan_V2.Vertretung
{
    /// <summary>
    /// R3-D: Räume im Wocheneditor (Teil der partiellen Klasse TagesEditorDialog).
    ///  - Jede Stunde zeigt rechts oben ihren Raum (Raumplan der Plan-Spalte, sonst Fachraum vor
    ///    Stammraum laut UV); geänderte Räume orange mit „→“, gesperrte Räume (Abwesenheit „Raum“) rot mit „⚠“.
    ///  - Klick → „Raum ändern …“: freie Räume dieser Stunde. Bei einer Stunde ohne Buchung entsteht eine
    ///    Raumvertretung; bei Vertretung, Betreuung, EVA, Zusammenlegung und „hierher verlegt“ bekommt die
    ///    Buchung den neuen Raum (die Lehrkraft bleibt). „Raum wie im Plan“ nimmt die Änderung zurück.
    ///  - Team-Teaching (Teile derselben UNr im selben Raum) zieht gemeinsam um.
    ///  - R3-D-Nachtrag: Doppelbelegung durch eine Buchung („‼“, beide Stunden).
    ///  - R3-D-Nachtrag: Tooltip der ganzen Stunde nennt den Raum („Raum: alt → neu“); der Raum laut Plan steht
    ///    nur einmal im Menü („Raum wie im Plan“); belegte und gesperrte Räume bewusst wählbar (Untermenü
    ///    „belegte Räume“, mit Rückfrage); Raumkombinationen wie „RSh2~RSb1“ werden nicht angeboten.
    /// Die Plandatei wird nicht verändert; gebucht wird in die Vertretungsdatei.
    /// </summary>
    public partial class TagesEditorDialog
    {
        private const int MaxRaeumeImMenu = 14;
        private static readonly SolidColorBrush RaumGeaendertBrush = new(Color.FromRgb(0xE6, 0x51, 0x00));
        /// <summary>R3-D-Nachtrag: Raum durch eine Buchung doppelt belegt („‼“).</summary>
        private static readonly SolidColorBrush RaumDoppeltBrush = new(Color.FromRgb(0xAD, 0x14, 0x57));

        /// <summary>Räume der geladenen Plan-Spalte lesen (Raumplan, Raumverzeichnis) und anzeigen.</summary>
        private void LadeRaeume()
        {
            if (_plan == null) return;
            _plan.Raeume = PlanRaeume.Lade(_plan);
            TxtRaumInfo.Text = _plan.Raeume.Info;
            TxtRaumInfo.ToolTip = _plan.Raeume.Info;
            TxtRaumInfo.Foreground = _plan.Raeume.Fehler ? Brushes.DarkRed : Brushes.DimGray;
            _tpCache.Clear();
        }

        private HashSet<string> GesperrteRaeume(DateTime datum, int stunde)
            => PlanRaeume.Gesperrt(_daten.Abwesenheiten, datum, stunde);

        /// <summary>
        /// Raum-Text oben rechts in der Zelle (null = nichts anzeigen). <paramref name="tooltip"/> = Raumzeile für
        /// den Tooltip der ganzen Stunde (R3-D-Nachtrag: am kleinen Raum-Text allein erschien er nicht).
        /// </summary>
        private TextBlock? RaumAnzeige(TagesZelle z, DateTime datum, out string? tooltip)
        {
            tooltip = null;
            if (z.IstFrei || string.IsNullOrWhiteSpace(z.Raum)) return null;

            var tb = new TextBlock
            {
                Text = z.Raum,
                FontSize = 10,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            tooltip = "Raum: " + z.Raum;
            if (z.RaumGeaendert)
            {
                tb.Text = "→ " + z.Raum;
                tb.Foreground = RaumGeaendertBrush;
                tb.FontWeight = FontWeights.SemiBold;
                tooltip = $"Raum: {(string.IsNullOrWhiteSpace(z.GrundRaum) ? "–" : z.GrundRaum)} → {z.Raum} (geändert)";
            }

            // R3-D-Nachtrag: durch eine Buchung doppelt belegt (z. B. bewusst in einen belegten Raum gelegt) –
            // beide Stunden zeigen „‼“ und nennen im Tooltip die andere Stunde.
            var doppelt = PlanRaeume.DoppeltDurchBuchung(Tagesplan(datum), z);
            if (doppelt.Count > 0)
            {
                tb.Text += " ‼";
                tb.Foreground = RaumDoppeltBrush;
                tb.FontWeight = FontWeights.SemiBold;
                foreach (var kv in doppelt)
                    tooltip += $"\n‼ Raum {kv.Key} ist doppelt belegt – auch {kv.Value}.";
            }

            var gesperrt = GesperrteRaeume(datum, z.Stunde);
            var hier = PlanRaeume.Zerlege(z.Raum).Where(x => gesperrt.Contains(x)).ToList();
            if (hier.Count > 0)
            {
                tb.Text += " ⚠";
                tb.Foreground = Brushes.Red;
                tb.FontWeight = FontWeights.SemiBold;
                tooltip += $"\n⚠ Raum {string.Join(", ", hier)} ist gesperrt (Abwesenheit „Raum“) – Klick → „Raum ändern …“.";
            }
            return tb;
        }

        /// <summary>
        /// Menüpunkt „Raum ändern …“ für eine Stunde (null, wenn es dort keinen Raum zu ändern gibt).
        /// Die freien Räume werden erst beim Öffnen des Untermenüs bestimmt.
        /// </summary>
        private MenuItem? BaueRaumMenu(TagesZelle z, DateTime datum)
        {
            if (_plan == null || z.IstFrei || string.IsNullOrWhiteSpace(z.OriginalLehrer)) return null;
            if (z.Eintrag?.Art == Vertretungsart.Verlegung && !z.Eintrag.VerlegtVonStunde.HasValue) return null;
            // Pseudofächer (VBer, UV, PSF…, ufrei, NuHo) brauchen keinen Raum – wie im Raumplan (Knopf 25).
            if (Stundenplan_V2.Raumplanung.RaumplanStore.IstOhneRaumbedarf(z.Fach)) return null;

            string jetzt = string.IsNullOrWhiteSpace(z.Raum) ? "kein Raum" : z.Raum!;
            var mi = new MenuItem { Header = $"Raum ändern (jetzt {jetzt}) …" };
            mi.Items.Add(new MenuItem { Header = "…", IsEnabled = false });   // Platzhalter, damit das Untermenü aufgeht
            bool gefuellt = false;
            mi.SubmenuOpened += (_, e) =>
            {
                if (gefuellt || !ReferenceEquals(e.OriginalSource, mi)) return;
                gefuellt = true;
                mi.Items.Clear();
                FuelleRaumMenu(mi, z, datum);
            };
            return mi;
        }

        private void FuelleRaumMenu(MenuItem mi, TagesZelle z, DateTime datum)
        {
            var raeume = _plan?.Raeume;
            if (_plan == null || raeume == null) return;

            var tp = Tagesplan(datum);
            var gesperrt = GesperrteRaeume(datum, z.Stunde);

            if (z.RaumGeaendert)
            {
                // R3-D-Nachtrag: auch belegt wählbar – dann mit Rückfrage.
                var belegtVon = PlanRaeume.BelegtVon(tp, z);
                var grund = PlanRaeume.Zerlege(z.GrundRaum);
                string? warum = grund.Any(gesperrt.Contains) ? "gesperrt"
                              : grund.Where(belegtVon.ContainsKey).Select(x => belegtVon[x]).FirstOrDefault() is string wer
                                  ? "belegt: " + wer : null;
                var zurueck = new MenuItem
                {
                    Header = "Raum wie im Plan: " + (grund.Count > 0 ? z.GrundRaum : "kein Raum")
                             + (warum == null ? "" : "  (" + warum + ")")
                };
                zurueck.Click += (_, __) =>
                {
                    if (warum != null && !BestaetigeBelegtenRaum(z, datum, z.GrundRaum ?? "", warum)) return;
                    SetzeRaum(z, datum, null);
                };
                mi.Items.Add(zurueck);
                mi.Items.Add(new Separator());
            }

            // R3-D-Nachtrag: Der Raum laut Plan steht oben („Raum wie im Plan“) – in der Liste nicht noch einmal.
            var ohne = PlanRaeume.Zerlege(z.Raum);
            if (z.RaumGeaendert) ohne.AddRange(PlanRaeume.Zerlege(z.GrundRaum));
            var alle = raeume.Vorschlaege(_plan, tp, z, gesperrt, ohne);
            var freie = alle.Where(x => x.IstFrei).ToList();
            var belegte = alle.Where(x => !x.IstFrei).ToList();

            if (freie.Count == 0)
                mi.Items.Add(new MenuItem { Header = "(kein freier Raum in dieser Stunde)", IsEnabled = false });

            var oben = freie.Where(x => !x.Weitere).Take(MaxRaeumeImMenu).ToList();
            var rest = freie.Where(x => !oben.Contains(x)).ToList();
            foreach (var v in oben) mi.Items.Add(RaumEintrag(z, datum, v));
            if (rest.Count > 0)
            {
                var weitere = new MenuItem { Header = $"weitere Räume ({rest.Count})" };
                foreach (var v in rest) weitere.Items.Add(RaumEintrag(z, datum, v));
                mi.Items.Add(weitere);
            }
            // R3-D-Nachtrag: belegte und gesperrte Räume ganz unten, nur mit Rückfrage.
            if (belegte.Count > 0)
            {
                mi.Items.Add(new Separator());
                var bel = new MenuItem { Header = $"belegte Räume ({belegte.Count}) …" };
                foreach (var v in belegte) bel.Items.Add(RaumEintrag(z, datum, v));
                mi.Items.Add(bel);
            }
        }

        private MenuItem RaumEintrag(TagesZelle z, DateTime datum, RaumVorschlag v)
        {
            var m = new MenuItem { Header = v.Anzeige };
            string raum = v.Name;
            string? belegt = v.Belegt;
            m.Click += (_, __) =>
            {
                if (belegt != null && !BestaetigeBelegtenRaum(z, datum, raum, belegt)) return;
                SetzeRaum(z, datum, raum);
            };
            return m;
        }

        /// <summary>R3-D-Nachtrag: Rückfrage vor der Wahl eines belegten bzw. gesperrten Raums.</summary>
        private bool BestaetigeBelegtenRaum(TagesZelle z, DateTime datum, string raum, string belegt)
        {
            bool istBelegt = belegt.StartsWith("belegt: ", StringComparison.Ordinal);
            string grund = istBelegt
                ? "ist in dieser Stunde belegt durch " + belegt.Substring("belegt: ".Length)
                : "ist in dieser Stunde gesperrt (Abwesenheit „Raum“)";
            string frage = $"{raum} {grund}.\n\n{z.Fach} ({string.Join(", ", z.Klassen)}) am {datum:dd.MM.yyyy}, " +
                           $"{z.Stunde}. Std trotzdem in {raum} legen?" +
                           (istBelegt ? "\n\nDie andere Stunde behält ihren Raum – der Raum ist dann doppelt belegt." : "");
            bool ja = MessageBox.Show(this, frage, "Raum belegt", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                          MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!ja) SetzeInfo($"Raum unverändert ({raum} nicht frei).");
            return ja;
        }

        /// <summary>
        /// Raum einer Stunde ändern (<paramref name="raum"/> = null: zurück zum Raum laut Plan). Teile derselben
        /// UNr im selben Raum (Team-Teaching) ziehen mit.
        /// </summary>
        private void SetzeRaum(TagesZelle alt, DateTime datum, string? raum)
        {
            var tp = Tagesplan(datum);
            // Die Zelle aus dem aktuellen Tagesplan nehmen (nach „NeuLaden“ wäre die Menü-Zelle veraltet).
            var z = tp.Zellen.FirstOrDefault(x => ReferenceEquals(x, alt))
                    ?? tp.InStunde(alt.Stunde).FirstOrDefault(x =>
                           x.UNr == alt.UNr && x.Status == alt.Status &&
                           string.Equals(x.OriginalLehrer, alt.OriginalLehrer, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(x.Fach, alt.Fach, StringComparison.OrdinalIgnoreCase));
            if (z == null) { SetzeInfo("Die Stunde wurde inzwischen geändert – bitte erneut anklicken."); return; }

            var gruppe = tp.InStunde(z.Stunde)
                .Where(x => ReferenceEquals(x, z)
                            || (!x.IstFrei && x.UNr == z.UNr && !string.IsNullOrWhiteSpace(x.OriginalLehrer)
                                && !string.IsNullOrWhiteSpace(z.Raum) && PlanRaeume.GleicherRaum(x.Raum, z.Raum)))
                .ToList();
            if (!gruppe.Any(x => ReferenceEquals(x, z))) gruppe.Add(z);

            int n = 0;
            foreach (var zelle in gruppe)
                if (SetzeRaumIntern(zelle, datum, raum)) n++;
            if (n == 0) { SetzeInfo("Raum unverändert."); return; }

            Speichere();
            Zeichne();
            string wer = $"{z.Fach} ({string.Join(", ", z.Klassen)})";
            SetzeInfo(raum == null
                ? $"{wer} am {datum:dd.MM.} Std {z.Stunde}: wieder Raum laut Plan ({(string.IsNullOrWhiteSpace(z.GrundRaum) ? "kein Raum" : z.GrundRaum)})."
                : $"{wer} am {datum:dd.MM.} Std {z.Stunde}: Raum {raum}" + (n > 1 ? $" (mit {n - 1} Team-Partner)" : "") + ".");
            _log?.Invoke($"Wocheneditor: Raum {datum:dd.MM.yyyy} Std {z.Stunde} UNr {z.UNr} → {raum ?? "wie im Plan"}.");
        }

        private bool SetzeRaumIntern(TagesZelle c, DateTime datum, string? raum)
        {
            bool zurueck = raum == null || PlanRaeume.GleicherRaum(raum, c.GrundRaum);
            var e = c.Eintrag;
            if (e != null)
            {
                if (e.Art == Vertretungsart.Raumvertretung && zurueck)
                {
                    EntferneEintraege(datum, c.Stunde, e.UNr, c.OriginalLehrer);   // nur Raum geändert → Buchung weg
                    return true;
                }
                e.OriginalRaum = c.GrundRaum;
                e.VertretungsRaum = zurueck ? null : raum!.Trim();
                return true;
            }
            if (zurueck) return false;

            _daten.Vertretungen.Add(new VertretungsEintrag
            {
                Datum = datum.Date, Stunde = c.Stunde, UNr = c.UNr,
                Art = Vertretungsart.Raumvertretung, Grund = VertretungsGrund.Sonstiges,
                OriginalLehrer = c.OriginalLehrer,
                Klassen = new List<string>(c.Klassen), Fach = c.Fach,
                OriginalRaum = c.GrundRaum, VertretungsRaum = raum!.Trim(),
                ZaehlwertSnapshot = 0
            });
            return true;
        }
    }
}
