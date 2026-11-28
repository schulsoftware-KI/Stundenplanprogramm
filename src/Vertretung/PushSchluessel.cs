#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  PushSchluessel – PB-2 (Entscheidungen 302, 142): der Push-Schlüssel
    //  (Header X-Api-Key) je Verbindung, NICHT mehr in vertretung_online.json
    //  neben der Plandatei (OneDrive).
    //
    //  - Ablage: %LOCALAPPDATA%\Stundenplan\push_entwicklung.dat bzw.
    //    push_testserver.dat (Ordner wie beim SchILD-Schlüssel).
    //  - Geschützt mit DPAPI für „diesen Windows-Benutzer“ (crypt32.dll, eigene
    //    Entropie – SchildSchluessel bleibt unverändert). Andere Benutzer und
    //    andere Rechner können die Datei nicht lesen.
    //  - Inhalt (verschlüsselt): Kopf „PSK1“ + JSON mit Adresse, Schlüssel und
    //    Datum. Die Adresse bindet den Schlüssel (V2): Zeigt die Verbindung auf
    //    eine andere Adresse, wird er nicht gesendet.
    //  Der Schlüssel wird nie angezeigt, nie geloggt und erscheint in keiner Meldung.
    // =====================================================================
    public static class PushSchluessel
    {
        private static readonly byte[] Kopf = { (byte)'P', (byte)'S', (byte)'K', (byte)'1' };
        private static readonly byte[] Entropie = Encoding.UTF8.GetBytes("Stundenplan V150 · Push-Schlüssel · DPAPI");

        /// <summary>Gespeicherter Schlüssel einer Verbindung.</summary>
        public sealed class Eintrag
        {
            public string Adresse { get; set; } = "";
            public string Schluessel { get; set; } = "";
            public DateTime Gespeichert { get; set; }
        }

        public static string Ordner => SchildSchluessel.Ordner;

        /// <summary>Datei der Verbindung („Entwicklung“ → push_entwicklung.dat).</summary>
        public static string Pfad(string verbindung)
        {
            var sb = new StringBuilder();
            foreach (char c in (verbindung ?? "").ToLowerInvariant())
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            return Path.Combine(Ordner, "push_" + (sb.Length > 0 ? sb.ToString() : "verbindung") + ".dat");
        }

        public static bool Vorhanden(string verbindung) => File.Exists(Pfad(verbindung));

        /// <summary>Liest den Schlüssel der Verbindung. null + fehler, wenn er fehlt oder nicht lesbar ist.</summary>
        public static Eintrag? Laden(string verbindung, out string? fehler)
        {
            fehler = null;
            string pfad = Pfad(verbindung);
            if (!File.Exists(pfad)) { fehler = $"Für „{verbindung}“ ist kein Schlüssel gespeichert."; return null; }
            byte[]? klar = null;
            try
            {
                klar = Dpapi(File.ReadAllBytes(pfad), schuetzen: false);
                if (klar.Length <= Kopf.Length || !klar.AsSpan(0, Kopf.Length).SequenceEqual(Kopf.AsSpan()))
                {
                    fehler = $"Die Schlüsseldatei von „{verbindung}“ hat ein unbekanntes Format – unter „Server verbinden…“ " +
                             "den Schlüssel neu eingeben.";
                    return null;
                }
                var e = JsonSerializer.Deserialize<Eintrag>(new ReadOnlySpan<byte>(klar, Kopf.Length, klar.Length - Kopf.Length));
                if (e == null || string.IsNullOrEmpty(e.Schluessel))
                {
                    fehler = $"Die Schlüsseldatei von „{verbindung}“ ist leer – unter „Server verbinden…“ den Schlüssel neu eingeben.";
                    return null;
                }
                return e;
            }
            catch (CryptographicException)
            {
                fehler = $"Der Schlüssel von „{verbindung}“ lässt sich mit diesem Windows-Benutzer nicht öffnen (DPAPI) – er " +
                         "wurde vermutlich von einem anderen Benutzer oder auf einem anderen Rechner gespeichert. Abhilfe: unter " +
                         "„Server verbinden…“ neu eingeben.";
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                fehler = $"Die Schlüsseldatei von „{verbindung}“ ist nicht lesbar ({ex.GetType().Name}) – unter „Server " +
                         "verbinden…“ neu eingeben.";
                return null;
            }
            finally
            {
                if (klar != null) CryptographicOperations.ZeroMemory(klar);
            }
        }

        /// <summary>
        /// Speichert Adresse + Schlüssel der Verbindung (ersetzt eine vorhandene Datei). Schreibt zuerst eine
        /// Zwischendatei, liest sie zurück und tauscht erst dann – schlägt etwas fehl, bleibt die alte Datei.
        /// </summary>
        public static void Speichern(string verbindung, string adresse, string schluessel)
        {
            if (string.IsNullOrEmpty(schluessel)) throw new ArgumentException("Leerer Schlüssel.", nameof(schluessel));
            Directory.CreateDirectory(Ordner);
            string pfad = Pfad(verbindung);
            string neu = pfad + ".neu";
            var e = new Eintrag { Adresse = adresse ?? "", Schluessel = schluessel, Gespeichert = DateTime.Now };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(e);
            var klar = new byte[Kopf.Length + json.Length];
            try
            {
                Kopf.CopyTo(klar, 0);
                json.CopyTo(klar, Kopf.Length);
                File.WriteAllBytes(neu, Dpapi(klar, schuetzen: true));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(klar);
                CryptographicOperations.ZeroMemory(json);
            }

            // Gegenprobe: lässt sich die neue Datei lesen und stimmt der Inhalt?
            byte[]? zurueck = null;
            try
            {
                zurueck = Dpapi(File.ReadAllBytes(neu), schuetzen: false);
                var probe = JsonSerializer.Deserialize<Eintrag>(new ReadOnlySpan<byte>(zurueck, Kopf.Length, zurueck.Length - Kopf.Length));
                if (probe == null || probe.Schluessel != schluessel || probe.Adresse != e.Adresse)
                    throw new IOException("Die gespeicherte Schlüsseldatei ließ sich nicht zurücklesen.");
            }
            catch
            {
                try { File.Delete(neu); } catch { /* Rest egal */ }
                throw;
            }
            finally
            {
                if (zurueck != null) CryptographicOperations.ZeroMemory(zurueck);
            }
            File.Move(neu, pfad, overwrite: true);
        }

        /// <summary>Entfernt den gespeicherten Schlüssel der Verbindung (true = es gab einen).</summary>
        public static bool Entfernen(string verbindung)
        {
            string pfad = Pfad(verbindung);
            if (!File.Exists(pfad)) return false;
            File.Delete(pfad);
            return true;
        }

        /// <summary>„gespeichert am 28.11.2026 11:42“ bzw. null.</summary>
        public static string? StandText(Eintrag? e) =>
            e == null ? null : "gespeichert am " + e.Gespeichert.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("de-DE"));

        // =====================================================
        //  DPAPI (crypt32.dll), Bereich „dieser Windows-Benutzer“ – wie SchildSchluessel, eigene Entropie
        // =====================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct DatenBlob
        {
            public int Laenge;
            public IntPtr Daten;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DatenBlob ein, string? beschreibung, ref DatenBlob entropie,
            IntPtr reserviert, IntPtr eingabe, int flags, ref DatenBlob aus);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DatenBlob ein, IntPtr beschreibung, ref DatenBlob entropie,
            IntPtr reserviert, IntPtr eingabe, int flags, ref DatenBlob aus);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr speicher);

        /// <summary>CRYPTPROTECT_UI_FORBIDDEN – nie ein Fenster von Windows. Ohne LOCAL_MACHINE = dieser Benutzer.</summary>
        private const int OhneOberflaeche = 0x1;

        private static byte[] Dpapi(byte[] daten, bool schuetzen)
        {
            var ein = NeuerBlob(daten);
            var ent = NeuerBlob(Entropie);
            var aus = new DatenBlob();
            try
            {
                bool ok = schuetzen
                    ? CryptProtectData(ref ein, "Stundenplan Push", ref ent, IntPtr.Zero, IntPtr.Zero, OhneOberflaeche, ref aus)
                    : CryptUnprotectData(ref ein, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, OhneOberflaeche, ref aus);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                var ergebnis = new byte[aus.Laenge];
                Marshal.Copy(aus.Daten, ergebnis, 0, aus.Laenge);
                return ergebnis;
            }
            finally
            {
                FreiGeben(ein);
                FreiGeben(ent);
                if (aus.Daten != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[aus.Laenge], 0, aus.Daten, aus.Laenge);
                    LocalFree(aus.Daten);
                }
            }
        }

        private static DatenBlob NeuerBlob(byte[] daten)
        {
            IntPtr p = Marshal.AllocHGlobal(Math.Max(1, daten.Length));
            Marshal.Copy(daten, 0, p, daten.Length);
            return new DatenBlob { Laenge = daten.Length, Daten = p };
        }

        private static void FreiGeben(DatenBlob b)
        {
            if (b.Daten == IntPtr.Zero) return;
            Marshal.Copy(new byte[b.Laenge], 0, b.Daten, b.Laenge);
            Marshal.FreeHGlobal(b.Daten);
        }
    }
}
