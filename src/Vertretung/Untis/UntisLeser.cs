#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  UntisLeser – U-1b, Entscheidung 128: liest die Untis-Exportdatei
    //  GPU014.TXT („Export Vertretungen“). Eine Vertretung je Zeile, 22 Felder,
    //  Trennzeichen „;“ oder „,“ (wird erkannt), Text in "…", Zeichensatz
    //  UTF-8 (mit/ohne BOM) oder ANSI (wird erkannt, SchildText.Dekodiere).
    //  Zeilen mit anderer Feldanzahl sind Fehler für die Zeile.
    //
    //  Datenschutz (Entscheidung 123): Der Inhalt von Feld 16 (Absenzgrund) und
    //  Feld 17 (Text zur Vertretung) wird beim Zerlegen übersprungen – er landet
    //  in keinem Feld, wird nicht angezeigt, nicht gespeichert und nicht gesendet
    //  (nur die Rohzeilen der Datei liegen beim Lesen kurz im Arbeitsspeicher).
    //  Gezählt wird nur, OB die Felder gefüllt sind (für den Hinweis „echte Datei
    //  nicht in OneDrive ablegen“). Fehlermeldungen nennen nie den Zeileninhalt.
    // =====================================================================

    public static class UntisLeser
    {
        public const int Feldanzahl = 22;
        private const int FeldAbsenzgrund = 15;   // 0-basiert: Feld 16
        private const int FeldText = 16;          // 0-basiert: Feld 17

        public static UntisDatei Lies(string pfad)
        {
            byte[] bytes = File.ReadAllBytes(pfad);
            string text = SchildText.Dekodiere(bytes, "auto", out string zeichensatz, out _);
            var d = LiesText(text);
            d.Pfad = pfad;
            d.Zeichensatz = zeichensatz;
            d.IstDemo = UntisDemo.Pruefe(pfad, bytes, out string demoText);
            d.DemoText = demoText;
            return d;
        }

        /// <summary>Zerlegt den Text der Datei (für Tests auch ohne Datei nutzbar).</summary>
        public static UntisDatei LiesText(string text)
        {
            var d = new UntisDatei();
            char trenn = '\0';
            string[] zeilen = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            DateTime? letzte = null;

            for (int i = 0; i < zeilen.Length; i++)
            {
                string roh = zeilen[i];
                int nr = i + 1;
                if (string.IsNullOrWhiteSpace(roh)) continue;
                d.ZeilenGesamt++;
                if (trenn == '\0') trenn = ErkenneTrennzeichen(roh);

                var f = Teile(roh, trenn, out bool mitGrund, out bool mitText);
                if (mitGrund) d.MitAbsenzgrund++;
                if (mitText) d.MitText++;

                if (f.Count != Feldanzahl)
                {
                    Fehler(d, nr, null, $"Zeile hat {f.Count} statt {Feldanzahl} Felder – nicht übernommen.");
                    continue;
                }

                string datumText = f[1].Trim();
                if (!DateTime.TryParseExact(datumText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var datum))
                {
                    Fehler(d, nr, "Datum (Feld 2)", $"Datum „{datumText}“ ist kein gültiges JJJJMMTT – nicht übernommen.");
                    continue;
                }
                if (!int.TryParse(f[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int stunde) || stunde <= 0)
                {
                    Fehler(d, nr, "Stunde (Feld 3)", $"Stunde „{f[2].Trim()}“ ist keine Zahl – nicht übernommen.");
                    continue;
                }
                int unr = 0;
                string unrText = f[4].Trim();
                if (unrText.Length > 0 && !int.TryParse(unrText, NumberStyles.Integer, CultureInfo.InvariantCulture, out unr))
                {
                    Fehler(d, nr, "U-Nr (Feld 5)", $"U-Nr „{unrText}“ ist keine Zahl – nicht übernommen.");
                    continue;
                }

                long bits = 0;
                string artText = f[17].Trim();
                if (artText.Length > 0 && !long.TryParse(artText, NumberStyles.Integer, CultureInfo.InvariantCulture, out bits))
                {
                    bits = 0;
                    d.Probleme.Add(new UntisProblem
                    {
                        Art = UntisProblemArt.Warnung, Zeile = nr, Spalte = "Art (Feld 18)",
                        Grund = $"Art „{artText}“ ist keine Zahl – als 0 gelesen."
                    });
                }

                int.TryParse(f[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int vnr);
                var z = new UntisZeile
                {
                    Nr = nr,
                    Vertretungsnummer = vnr,
                    Datum = datum.Date,
                    Stunde = stunde,
                    UNr = unr,
                    Absent = f[5].Trim(),
                    Vertreter = f[6].Trim(),
                    Fach = Norm(f[7]),
                    VFach = Norm(f[9]),
                    Raum = f[11].Trim(),
                    VRaum = f[12].Trim(),
                    Klassen = Liste(f[14]),
                    Bits = bits,
                    VKlassen = Liste(f[18]),
                    Buchstabe = f[19].Trim().ToUpperInvariant(),
                    Geaendert = f[20].Trim()
                };
                d.Zeilen.Add(z);

                if (DateTime.TryParseExact(z.Geaendert, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var g)
                    && (letzte == null || g > letzte))
                    letzte = g;
            }
            d.Trennzeichen = trenn == '\0' ? ';' : trenn;
            d.LetzteAenderung = letzte;
            return d;
        }

        private static void Fehler(UntisDatei d, int nr, string? spalte, string grund)
            => d.Probleme.Add(new UntisProblem { Art = UntisProblemArt.Fehler, Zeile = nr, Spalte = spalte, Grund = grund });

        /// <summary>„;“ oder „,“ – das Zeichen, das außerhalb von Anführungszeichen häufiger vorkommt.</summary>
        internal static char ErkenneTrennzeichen(string zeile)
        {
            int semi = 0, komma = 0;
            bool inText = false;
            foreach (char c in zeile)
            {
                if (c == '"') inText = !inText;
                else if (!inText && c == ';') semi++;
                else if (!inText && c == ',') komma++;
            }
            return komma > semi ? ',' : ';';
        }

        /// <summary>
        /// Zerlegt eine DIF-Zeile (Felder in "…" dürfen das Trennzeichen enthalten, "" = Anführungszeichen).
        /// Felder 16 und 17 bleiben leer: ihr Inhalt wird übersprungen, gemerkt wird nur, ob er gefüllt war.
        /// </summary>
        internal static List<string> Teile(string zeile, char trenn, out bool mitGrund, out bool mitText)
        {
            mitGrund = false;
            mitText = false;
            var felder = new List<string>(Feldanzahl);
            var sb = new StringBuilder();
            bool inText = false;
            int idx = 0;
            for (int i = 0; i < zeile.Length; i++)
            {
                char c = zeile[i];
                bool auslassen = idx == FeldAbsenzgrund || idx == FeldText;
                if (c == '"')
                {
                    if (inText && i + 1 < zeile.Length && zeile[i + 1] == '"')
                    {
                        if (auslassen) Gefuellt(idx, ref mitGrund, ref mitText);
                        else sb.Append('"');
                        i++;
                    }
                    else inText = !inText;
                }
                else if (c == trenn && !inText)
                {
                    felder.Add(sb.ToString());
                    sb.Clear();
                    idx++;
                }
                else if (auslassen)
                {
                    if (!char.IsWhiteSpace(c)) Gefuellt(idx, ref mitGrund, ref mitText);
                }
                else sb.Append(c);
            }
            felder.Add(sb.ToString());
            return felder;
        }

        private static void Gefuellt(int idx, ref bool mitGrund, ref bool mitText)
        {
            if (idx == FeldAbsenzgrund) mitGrund = true;
            else mitText = true;
        }

        /// <summary>Fach ohne doppelte Leerzeichen („D  G1“ → „D G1“, wie im Grundplan).</summary>
        internal static string Norm(string? s) => Regex.Replace((s ?? "").Trim(), @"\s+", " ");

        /// <summary>Klassen bzw. Räume, mit „~“ (Untis) oder „,“ getrennt.</summary>
        internal static List<string> Liste(string? s)
            => string.IsNullOrWhiteSpace(s)
                ? new List<string>()
                : s.Split(new[] { '~', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(x => x.Length > 0).ToList();
    }
}
