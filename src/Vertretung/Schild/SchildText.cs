#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildText – Hilfen für die SchILD-Textdateien (Phase 5.2a):
    //  Zeichensatz (UTF-8 mit/ohne BOM, Windows-1252 „ANSI“), Zeilen,
    //  Header-Normalisierung, Datum TT.MM.JJJJ, Personenschlüssel.
    //
    //  Windows-1252 ohne Zusatzpaket: Das Projekt referenziert
    //  System.Text.Encoding.CodePages bewusst nicht (vgl. GpuImportExport.cs).
    //  Windows-1252 = ISO-8859-1 bis auf 0x80–0x9F → kleine eigene Tabelle.
    // =====================================================================

    public static class SchildText
    {
        // ---- Windows-1252 ----

        private static readonly char[] Cp1252Sonder =
        {
            '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡',  // 80–87
            'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',  // 88–8F
            '\u0090', '‘', '’', '“', '”', '•', '–', '—',  // 90–97
            '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ'   // 98–9F
        };

        private static readonly Dictionary<char, byte> Cp1252Rueck = BaueRueck();

        private static Dictionary<char, byte> BaueRueck()
        {
            var d = new Dictionary<char, byte>();
            for (int i = 0; i < Cp1252Sonder.Length; i++) d[Cp1252Sonder[i]] = (byte)(0x80 + i);
            return d;
        }

        public static string DekodiereCp1252(byte[] bytes, int start)
        {
            var sb = new StringBuilder(bytes.Length - start);
            for (int i = start; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                sb.Append(b >= 0x80 && b <= 0x9F ? Cp1252Sonder[b - 0x80] : (char)b);
            }
            return sb.ToString();
        }

        /// <summary>Text → Windows-1252; nicht darstellbare Zeichen werden „?“.</summary>
        public static byte[] KodiereCp1252(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0x80 || (c >= 0xA0 && c <= 0xFF)) bytes[i] = (byte)c;
                else if (Cp1252Rueck.TryGetValue(c, out byte b)) bytes[i] = b;
                else bytes[i] = (byte)'?';
            }
            return bytes;
        }

        /// <summary>
        /// Dekodiert eine Datei. modus: "auto" | "utf-8" | "windows-1252".
        /// auto: UTF-8-BOM → UTF-8; sonst gültiges UTF-8 mit Nicht-ASCII-Zeichen → UTF-8 (hinweis gesetzt);
        /// sonst Windows-1252. Gibt den erkannten Zeichensatz als Text zurück.
        /// </summary>
        public static string Dekodiere(byte[] bytes, string modus, out string zeichensatz, out string? hinweis)
        {
            hinweis = null;
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string m = (modus ?? "auto").Trim().ToLowerInvariant();

            if (m == "windows-1252" || m == "ansi" || m == "cp1252")
            {
                zeichensatz = "Windows-1252 (ANSI, laut Profil)";
                return DekodiereCp1252(bytes, 0);
            }
            if (m == "utf-8" || m == "utf8")
            {
                zeichensatz = bom ? "UTF-8 mit BOM" : "UTF-8 ohne BOM (laut Profil)";
                return new UTF8Encoding(false, false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            }

            if (bom)
            {
                zeichensatz = "UTF-8 mit BOM";
                return new UTF8Encoding(false, false).GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                zeichensatz = "UTF-16";
                hinweis = "Datei ist UTF-16 kodiert (erwartet: ANSI oder UTF-8 mit BOM) – wurde trotzdem gelesen.";
                var enc = bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
                return enc.GetString(bytes, 2, bytes.Length - 2);
            }

            bool nurAscii = true;
            foreach (byte b in bytes) if (b >= 0x80) { nurAscii = false; break; }
            if (nurAscii)
            {
                zeichensatz = "ASCII (ohne Umlaute)";
                return DekodiereCp1252(bytes, 0);
            }
            try
            {
                string text = new UTF8Encoding(false, true).GetString(bytes);
                zeichensatz = "UTF-8 ohne BOM";
                hinweis = "UTF-8 ohne BOM erkannt (erwartet: ANSI oder UTF-8 mit BOM) – Umlaute bitte in der Übersicht prüfen.";
                return text;
            }
            catch (ArgumentException)
            {
                zeichensatz = "Windows-1252 (ANSI)";
                return DekodiereCp1252(bytes, 0);
            }
        }

        /// <summary>Zerlegt in Zeilen (CRLF, LF, CR); entfernt leere Zeilen am Ende.</summary>
        public static List<string> Zeilen(string text)
        {
            var zeilen = new List<string>(text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None));
            while (zeilen.Count > 0 && zeilen[^1].Trim().Length == 0) zeilen.RemoveAt(zeilen.Count - 1);
            if (zeilen.Count > 0 && zeilen[0].Length > 0 && zeilen[0][0] == '﻿') zeilen[0] = zeilen[0][1..];
            return zeilen;
        }

        private static readonly Regex Leerraum = new(@"\s+", RegexOptions.Compiled);

        /// <summary>Header-Vergleich: Leerzeichen zusammenfassen, Groß/klein egal, Punkt am Ende egal.</summary>
        public static string NormHeader(string h)
        {
            string s = Leerraum.Replace((h ?? "").Replace('﻿', ' ').Trim(), " ");
            while (s.EndsWith(".")) s = s[..^1].TrimEnd();
            return s.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        /// <summary>Wert säubern: trimmen, Unicode NFC.</summary>
        public static string Wert(string? s) => (s ?? "").Trim().Normalize(NormalizationForm.FormC);

        private static readonly Regex DatumDe = new(@"^(\d{1,2})\.(\d{1,2})\.(\d{4})$", RegexOptions.Compiled);
        private static readonly Regex DatumIso = new(@"^(\d{4})-(\d{2})-(\d{2})$", RegexOptions.Compiled);

        /// <summary>TT.MM.JJJJ (tolerant: 1.4.2015) oder JJJJ-MM-TT.</summary>
        public static bool TryDatum(string s, out DateOnly datum)
        {
            datum = default;
            s = (s ?? "").Trim();
            int t, m, j;
            var md = DatumDe.Match(s);
            if (md.Success)
            {
                t = int.Parse(md.Groups[1].Value, CultureInfo.InvariantCulture);
                m = int.Parse(md.Groups[2].Value, CultureInfo.InvariantCulture);
                j = int.Parse(md.Groups[3].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                var mi = DatumIso.Match(s);
                if (!mi.Success) return false;
                j = int.Parse(mi.Groups[1].Value, CultureInfo.InvariantCulture);
                m = int.Parse(mi.Groups[2].Value, CultureInfo.InvariantCulture);
                t = int.Parse(mi.Groups[3].Value, CultureInfo.InvariantCulture);
            }
            if (j < 1900 || j > 2100 || m < 1 || m > 12 || t < 1 || t > DateTime.DaysInMonth(j, m)) return false;
            datum = new DateOnly(j, m, t);
            return true;
        }

        public static string DatumText(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        public static bool TryGanzzahl(string s, out int wert)
            => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out wert);

        public static bool TryLang(string s, out long wert)
            => long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out wert);

        /// <summary>Nachname mit optionalem „#Klasse“-Zusatz zerlegen.</summary>
        public static (string Name, string Zusatz) TrenneZusatz(string nachname)
        {
            string n = Wert(nachname);
            int p = n.IndexOf('#');
            return p < 0 ? (n, "") : (n[..p].Trim(), n[(p + 1)..].Trim());
        }

        /// <summary>Schlüssel zum Verknüpfen (Groß/klein und Mehrfach-Leerzeichen egal). Nie anzeigen.</summary>
        public static string PersonSchluessel(string nachname, string zusatz, string vorname, DateOnly geb)
        {
            static string N(string s) => Leerraum.Replace(s.Trim(), " ").ToUpperInvariant();
            return $"{N(nachname)}#{N(zusatz)}|{N(vorname)}|{geb:yyyy-MM-dd}";
        }

        /// <summary>Kürzel-/Fach-/Klassen-Vergleich: Groß/klein egal, ohne Leerzeichen am Rand.</summary>
        public static bool Gleich(string? a, string? b)
            => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static readonly Regex KlasseOhneNull = new(@"^\d[A-Z]{1,2}$", RegexOptions.Compiled);

        /// <summary>Klasse für den Vergleich mit dem Plan (5.2a-2): Großbuchstaben, führende Null ergänzt („5a“ → „05A“).</summary>
        public static string KlasseNorm(string? klasse)
        {
            string t = (klasse ?? "").Trim().ToUpperInvariant();
            return KlasseOhneNull.IsMatch(t) ? "0" + t : t;
        }

        /// <summary>Zelle für eine SchILD-Datei: Trennzeichen und Zeilenumbrüche entfernen.</summary>
        public static string Zelle(string? s, string trenn)
            => (s ?? "").Replace(trenn, " ").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
