#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  SchildSchluessel – Phase 5.2b-2b (Entscheidungen 82/170, Punkt F):
    //  der HMAC-Schlüssel für die Prüfsummen der Schüler (ExtId …).
    //
    //  - 32 Byte Zufall. Gespeichert wird er beim ersten Import unter
    //    %LOCALAPPDATA%\Stundenplan\schild_schluessel.dat (Pseudodaten:
    //    schild_schluessel_pseudo.dat) – NICHT neben der Plandatei, die in
    //    OneDrive liegt. Eine vorhandene Datei wird nie still überschrieben.
    //  - Geschützt mit DPAPI für „diesen Windows-Benutzer“ (CryptProtectData
    //    direkt aus crypt32.dll – kein Zusatzpaket). Andere Benutzer und
    //    andere Rechner können die Datei nicht lesen; ein Wechsel geht über
    //    „Schlüssel sichern…“ und „Schlüssel einlesen…“.
    //  - Sicherung: Datei mit Kennwort (AES-256-GCM; der Schlüssel dafür kommt
    //    aus dem Kennwort per PBKDF2-SHA256 mit 600 000 Runden). Ablage auf
    //    USB-Stick bzw. im Tresor, nicht in OneDrive.
    //  - Kennung = 8 Hexzeichen aus SHA-256 über den Schlüssel: zeigt, ob zwei
    //    Schlüssel gleich sind, verrät ihn aber nicht.
    //  Der Schlüssel wird nie angezeigt, nie gesendet und nie geloggt.
    // =====================================================================
    public sealed class SchildSchluessel
    {
        public const int Laenge = 32;
        public const int MinKennwort = 12;
        private const int Runden = 600_000;
        private static readonly byte[] Kopf = { (byte)'S', (byte)'K', (byte)'S', (byte)'1' };
        private static readonly byte[] Entropie = Encoding.UTF8.GetBytes("Stundenplan V150 · SchILD-Schlüssel · DPAPI");
        private const string SicherungArt = "Stundenplan – SchILD-Schlüssel (Sicherung)";

        private readonly byte[] _wert;

        /// <summary>Schlüssel für Pseudodaten (eigene Datei).</summary>
        public bool Pseudo { get; }
        /// <summary>true = noch nicht gespeichert (neu erzeugt oder aus einer Sicherung gelesen).</summary>
        public bool Neu { get; private set; }
        /// <summary>„1A2B-3C4D“ – zum Vergleichen, verrät den Schlüssel nicht.</summary>
        public string Kennung { get; }

        private SchildSchluessel(byte[] wert, bool pseudo, bool neu)
        {
            _wert = wert;
            Pseudo = pseudo;
            Neu = neu;
            Kennung = BildeKennung(wert);
        }

        // =====================================================
        //  Ablage
        // =====================================================

        public static string Ordner =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stundenplan");

        public static string Pfad(bool pseudo) =>
            Path.Combine(Ordner, pseudo ? "schild_schluessel_pseudo.dat" : "schild_schluessel.dat");

        public static bool Vorhanden(bool pseudo) => File.Exists(Pfad(pseudo));

        public static string ArtText(bool pseudo) => pseudo ? "Pseudodaten" : "echte Daten";

        /// <summary>Neuer Zufallsschlüssel (noch nicht gespeichert).</summary>
        public static SchildSchluessel Erzeugen(bool pseudo) => new(RandomNumberGenerator.GetBytes(Laenge), pseudo, true);

        /// <summary>Liest den gespeicherten Schlüssel. null + fehler, wenn er fehlt oder nicht lesbar ist.</summary>
        public static SchildSchluessel? Laden(bool pseudo, out string? fehler)
        {
            fehler = null;
            string pfad = Pfad(pseudo);
            if (!File.Exists(pfad)) { fehler = "Kein Schlüssel gespeichert."; return null; }
            byte[]? klar = null;
            try
            {
                klar = Dpapi(File.ReadAllBytes(pfad), schuetzen: false);
                if (klar.Length != Kopf.Length + 1 + Laenge || !klar.AsSpan(0, Kopf.Length).SequenceEqual(Kopf.AsSpan())
                    || klar[Kopf.Length] != (pseudo ? 1 : 0))
                {
                    fehler = "Die Schlüsseldatei hat ein unbekanntes Format.";
                    return null;
                }
                return new SchildSchluessel(klar.AsSpan(Kopf.Length + 1, Laenge).ToArray(), pseudo, false);
            }
            catch (CryptographicException)
            {
                fehler = "Die Schlüsseldatei lässt sich mit diesem Windows-Benutzer nicht öffnen (DPAPI) – sie wurde " +
                         "vermutlich von einem anderen Benutzer oder auf einem anderen Rechner angelegt. Abhilfe: " +
                         "„Schlüssel einlesen…“ mit der Sicherung.";
                return null;
            }
            catch (Exception ex)
            {
                fehler = "Die Schlüsseldatei ist nicht lesbar: " + ex.Message;
                return null;
            }
            finally
            {
                if (klar != null) CryptographicOperations.ZeroMemory(klar);
            }
        }

        /// <summary>
        /// Speichert den Schlüssel (DPAPI, dieser Windows-Benutzer). ersetzen = false: eine vorhandene Datei wird
        /// nie überschrieben (IOException).
        /// </summary>
        public void Speichern(bool ersetzen = false)
        {
            string pfad = Pfad(Pseudo);
            if (!ersetzen && File.Exists(pfad))
                throw new IOException("Es gibt schon eine Schlüsseldatei – sie wird nicht überschrieben:\n" + pfad);
            Directory.CreateDirectory(Ordner);
            var klar = new byte[Kopf.Length + 1 + Laenge];
            try
            {
                Kopf.CopyTo(klar, 0);
                klar[Kopf.Length] = (byte)(Pseudo ? 1 : 0);
                _wert.CopyTo(klar, Kopf.Length + 1);
                byte[] geschuetzt = Dpapi(klar, schuetzen: true);
                string tmp = pfad + ".neu";
                File.WriteAllBytes(tmp, geschuetzt);
                File.Move(tmp, pfad, overwrite: ersetzen);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(klar);
            }
            Neu = false;
        }

        /// <summary>Base64 von HMAC-SHA256(Schlüssel, Text) – das Format, das die API annimmt (44 Zeichen).</summary>
        public string Hmac(string text) => Convert.ToBase64String(HMACSHA256.HashData(_wert, Encoding.UTF8.GetBytes(text)));

        /// <summary>Schlüssel im Arbeitsspeicher löschen (beim Schließen des Fensters).</summary>
        public void Vergessen() => CryptographicOperations.ZeroMemory(_wert);

        private static string BildeKennung(byte[] wert)
        {
            var daten = new byte[wert.Length + 8];
            Encoding.ASCII.GetBytes("Kennung1").CopyTo(daten, 0);
            wert.CopyTo(daten, 8);
            string hex = Convert.ToHexString(SHA256.HashData(daten), 0, 4);
            CryptographicOperations.ZeroMemory(daten);
            return hex[..4] + "-" + hex[4..];
        }

        // =====================================================
        //  Sicherung mit Kennwort
        // =====================================================

        /// <summary>Inhalt einer Sicherungsdatei (JSON). Der Schlüssel steht nur verschlüsselt darin.</summary>
        public sealed class Sicherung
        {
            public string Art { get; set; } = SicherungArt;
            public int Version { get; set; } = 1;
            public bool Pseudo { get; set; }
            public string Kennung { get; set; } = "";
            public string Erstellt { get; set; } = "";
            public string Verfahren { get; set; } = "PBKDF2-SHA256 + AES-256-GCM";
            public int Runden { get; set; }
            public string Salz { get; set; } = "";
            public string Nonce { get; set; } = "";
            public string Daten { get; set; } = "";
            public string Tag { get; set; } = "";
            public string Hinweis { get; set; } =
                "Sicherung des SchILD-Schlüssels. Nur mit dem Kennwort lesbar. Auf USB-Stick bzw. im Tresor aufbewahren, " +
                "nicht in OneDrive, Teams oder einem anderen Cloud-Ordner; das Kennwort getrennt davon aufbewahren.";
        }

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private static byte[] Zusatzdaten(bool pseudo, string kennung)
            => Encoding.UTF8.GetBytes($"StundenplanSchildSchluessel|1|{(pseudo ? 1 : 0)}|{kennung}");

        /// <summary>Schreibt eine mit dem Kennwort verschlüsselte Sicherung (dauert etwa eine Sekunde).</summary>
        public void Sichern(string pfad, string kennwort)
        {
            if (kennwort.Length < MinKennwort)
                throw new ArgumentException($"Das Kennwort muss mindestens {MinKennwort} Zeichen haben.");
            byte[] salz = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] kw = Encoding.UTF8.GetBytes(kennwort);
            byte[] aesSchluessel = Rfc2898DeriveBytes.Pbkdf2(kw, salz, Runden, HashAlgorithmName.SHA256, 32);
            var geheim = new byte[Laenge];
            var tag = new byte[16];
            try
            {
                using (var aes = new AesGcm(aesSchluessel, 16))
                    aes.Encrypt(nonce, _wert, geheim, tag, Zusatzdaten(Pseudo, Kennung));
                var s = new Sicherung
                {
                    Pseudo = Pseudo,
                    Kennung = Kennung,
                    Erstellt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Runden = Runden,
                    Salz = Convert.ToBase64String(salz),
                    Nonce = Convert.ToBase64String(nonce),
                    Daten = Convert.ToBase64String(geheim),
                    Tag = Convert.ToBase64String(tag)
                };
                File.WriteAllText(pfad, JsonSerializer.Serialize(s, JsonOpt), new UTF8Encoding(false));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kw);
                CryptographicOperations.ZeroMemory(aesSchluessel);
            }
        }

        /// <summary>Liest den Kopf einer Sicherung (ohne Kennwort): Art, Kennung, Datum. null + fehler bei falschem Format.</summary>
        public static Sicherung? LiesKopf(string pfad, out string? fehler)
        {
            fehler = null;
            try
            {
                var info = new FileInfo(pfad);
                if (!info.Exists) { fehler = "Die Datei gibt es nicht."; return null; }
                if (info.Length > 64 * 1024) { fehler = "Das ist keine Sicherung eines SchILD-Schlüssels (Datei zu groß)."; return null; }
                var s = JsonSerializer.Deserialize<Sicherung>(File.ReadAllText(pfad), JsonOpt);
                if (s == null || s.Art != SicherungArt || s.Version != 1)
                {
                    fehler = "Das ist keine Sicherung eines SchILD-Schlüssels (bzw. eine unbekannte Fassung).";
                    return null;
                }
                if (s.Runden < 100_000 || s.Runden > 10_000_000)
                {
                    fehler = "Die Sicherung hat ungültige Angaben (Runden).";
                    return null;
                }
                return s;
            }
            catch (Exception ex)
            {
                fehler = "Die Datei lässt sich nicht als Sicherung lesen: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Entschlüsselt eine Sicherung. Gibt den Schlüssel zurück (Neu = true, noch nicht gespeichert) bzw. null + fehler
        /// (falsches Kennwort, beschädigte Datei).
        /// </summary>
        public static SchildSchluessel? AusSicherung(Sicherung s, string kennwort, out string? fehler)
        {
            fehler = null;
            byte[]? kw = null, aesSchluessel = null, klar = null;
            try
            {
                byte[] salz = Convert.FromBase64String(s.Salz), nonce = Convert.FromBase64String(s.Nonce);
                byte[] geheim = Convert.FromBase64String(s.Daten), tag = Convert.FromBase64String(s.Tag);
                if (salz.Length < 16 || nonce.Length != 12 || geheim.Length != Laenge || tag.Length != 16)
                {
                    fehler = "Die Sicherung ist beschädigt (Längen stimmen nicht).";
                    return null;
                }
                kw = Encoding.UTF8.GetBytes(kennwort);
                aesSchluessel = Rfc2898DeriveBytes.Pbkdf2(kw, salz, s.Runden, HashAlgorithmName.SHA256, 32);
                klar = new byte[Laenge];
                using (var aes = new AesGcm(aesSchluessel, 16))
                    aes.Decrypt(nonce, geheim, tag, klar, Zusatzdaten(s.Pseudo, s.Kennung));
                var schluessel = new SchildSchluessel(klar.ToArray(), s.Pseudo, true);
                if (schluessel.Kennung != s.Kennung)
                {
                    schluessel.Vergessen();
                    fehler = "Die Sicherung ist beschädigt (Kennung passt nicht).";
                    return null;
                }
                return schluessel;
            }
            catch (CryptographicException)
            {
                fehler = "Das Kennwort ist falsch oder die Datei wurde verändert.";
                return null;
            }
            catch (FormatException)
            {
                fehler = "Die Sicherung ist beschädigt (ungültige Zeichen).";
                return null;
            }
            finally
            {
                if (kw != null) CryptographicOperations.ZeroMemory(kw);
                if (aesSchluessel != null) CryptographicOperations.ZeroMemory(aesSchluessel);
                if (klar != null) CryptographicOperations.ZeroMemory(klar);
            }
        }

        /// <summary>Liegt der Pfad in einem OneDrive-, Teams- bzw. SharePoint-Ordner (synchronisiert in die Cloud)?</summary>
        public static bool IstCloudOrdner(string pfad)
        {
            string voll;
            try { voll = Path.GetFullPath(pfad); } catch { return false; }
            foreach (var v in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            {
                string? wurzel = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrWhiteSpace(wurzel) &&
                    voll.StartsWith(wurzel.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return voll.Contains("\\OneDrive", StringComparison.OrdinalIgnoreCase)
                   || voll.Contains("\\SharePoint", StringComparison.OrdinalIgnoreCase)
                   || voll.Contains("\\Dropbox", StringComparison.OrdinalIgnoreCase)
                   || voll.Contains("\\Google Drive", StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================
        //  DPAPI (crypt32.dll), Bereich „dieser Windows-Benutzer“
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
                    ? CryptProtectData(ref ein, "Stundenplan SchILD", ref ent, IntPtr.Zero, IntPtr.Zero, OhneOberflaeche, ref aus)
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
