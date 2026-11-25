using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace VertretungApi.Services;

// =====================================================================
//  P5c-1 (Entscheidungen 261, 273): Zip-Datei mit WinZip-AES-256 (AE-2) – ohne Zusatzpaket.
//
//  System.IO.Compression kann nicht verschlüsseln (239). Das Format (WinZip „AES Encryption Information“) je Datei:
//    - erst Deflate (Methode 8), dann Verschlüsselung; im Kopf steht Methode 99 und das Extra-Feld 0x9901
//      (Länge 7: Version 2 = AE-2, „AE“, Stärke 3 = 256 Bit, eigentliche Methode 8)
//    - Daten: Salz (16 Byte, zufällig je Datei) · Prüfwert (2 Byte) · verschlüsselte Daten · 10 Byte HMAC-SHA1
//    - Schlüssel: PBKDF2-HMAC-SHA1(Kennwort, Salz, 1000 Runden – vom Format so festgelegt) → 66 Byte:
//      32 Byte AES-Schlüssel, 32 Byte HMAC-Schlüssel, 2 Byte Prüfwert
//    - AES-256 im CTR-Verfahren: 16-Byte-Zähler little-endian, beginnt bei 1, je Block um 1 erhöht
//    - HMAC-SHA1 über die verschlüsselten Daten, die ersten 10 Byte
//    - AE-2: CRC-32 im Kopf ist 0 (die Prüfung übernimmt der HMAC)
//  Dateinamen in UTF-8 (Bit 11) – sie bleiben im Zip sichtbar (Klasse, Fach, Kürzel, Zeitraum; keine Schülernamen).
//  Kein Zip64: über 4 GB bzw. 65 534 Dateien bricht das Schreiben mit ArchivZuGrossException ab.
//
//  Kennwort (Kennwort.Pruefen): 12 bis 128 Zeichen, nur Zeichen der Tastatur ohne Umlaute (ASCII 32–126), ohne
//  Leerzeichen am Anfang oder Ende – so lesen 7-Zip, WinZip und WinRAR es gleich (die Kodierung von Umlauten im
//  Kennwort ist zwischen den Programmen nicht einheitlich). Es liegt nur im Arbeitsspeicher (Vergessen() löscht die
//  Bytes) und erscheint nie in Protokoll oder Log.
//  Nebenbei: SHA-256 je Datei (für Inhalt.txt) und der ganzen Zip-Datei (für die Bestätigung, 264).
// =====================================================================

/// <summary>Das Archiv würde die Grenzen des Zip-Formats ohne Zip64 überschreiten (4 GB bzw. 65 534 Dateien).</summary>
public sealed class ArchivZuGrossException : Exception
{
    public ArchivZuGrossException(string text) : base(text) { }
}

/// <summary>Regeln für das Kennwort eines verschlüsselten Archivs (261).</summary>
public static class Kennwort
{
    public const int MinLaenge = 12, MaxLaenge = 128;

    /// <summary>Fehlertext oder null (gültig).</summary>
    public static string? Pruefen(string? kennwort)
    {
        var k = kennwort ?? "";
        if (k.Length < MinLaenge)
            return $"Das Kennwort braucht mindestens {MinLaenge} Zeichen – am besten ein Satz aus mehreren Wörtern.";
        if (k.Length > MaxLaenge) return $"Das Kennwort darf höchstens {MaxLaenge} Zeichen haben.";
        if (k.Any(c => c < ' ' || c > '~'))
            return "Bitte im Kennwort keine Umlaute, kein ß und keine Sonderzeichen außerhalb der Tastatur (ä → ae) – " +
                   "sonst öffnen manche Programme das Archiv nicht.";
        if (k[0] == ' ' || k[^1] == ' ') return "Bitte kein Leerzeichen am Anfang oder Ende des Kennworts.";
        return null;
    }
}

/// <summary>Schreibt eine Zip-Datei mit WinZip-AES-256 (AE-2) in einen Stream (der Stream bleibt offen).</summary>
public sealed class AesZipSchreiber
{
    public const int SalzLaenge = 16, PruefwertLaenge = 2, MacLaenge = 10, Runden = 1000, SchluesselLaenge = 32;
    public const int MaxDateien = 65534;
    public const long MaxBytes = uint.MaxValue;

    private const ushort VersionNoetig = 51, VersionErstellt = 63;
    private const ushort Flags = 0x0001 | 0x0800;         // verschlüsselt, Namen in UTF-8
    private const ushort MethodeAes = 99, MethodeDeflate = 8;
    private const int ExtraLaenge = 11;

    /// <summary>Eine Datei im Archiv: Name, Größe (unverschlüsselt) und SHA-256 (Großbuchstaben, wie Get-FileHash).</summary>
    public sealed record Datei(string Name, long Groesse, string Sha256);

    private sealed record Eintrag(byte[] Name, ushort Zeit, ushort Datum, uint Komprimiert, uint Groesse, uint Offset);

    private readonly Stream _ziel;
    private readonly byte[] _kennwort;
    private readonly IncrementalHash _gesamt = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<Eintrag> _eintraege = new();
    private readonly List<Datei> _dateien = new();
    private readonly HashSet<string> _namen = new(StringComparer.Ordinal);
    private long _pos;
    private bool _fertig;

    public AesZipSchreiber(Stream ziel, string kennwort)
    {
        if (Kennwort.Pruefen(kennwort) is string fehler) throw new ArgumentException(fehler, nameof(kennwort));
        _ziel = ziel;
        _kennwort = Encoding.ASCII.GetBytes(kennwort);
    }

    /// <summary>Bisher geschriebene Dateien (ohne die noch kommenden).</summary>
    public IReadOnlyList<Datei> Dateien => _dateien;

    /// <summary>Bisher geschriebene Bytes.</summary>
    public long Geschrieben => _pos;

    /// <summary>Kennwort aus dem Arbeitsspeicher löschen (nach dem Abschluss bzw. bei einem Fehler).</summary>
    public void Vergessen() => Array.Clear(_kennwort);

    /// <summary>Eine Datei anhängen: Deflate, dann AES-256 mit eigenem Salz.</summary>
    public void Hinzufuegen(string name, byte[] daten, DateTime? zeit = null)
    {
        if (_fertig) throw new InvalidOperationException("Das Archiv ist schon abgeschlossen.");
        if (!_namen.Add(name)) throw new InvalidOperationException("Dateiname doppelt im Archiv.");
        if (_eintraege.Count >= MaxDateien)
            throw new ArchivZuGrossException($"Mehr als {MaxDateien} Dateien – das passt nicht in eine Zip-Datei ohne Zip64.");

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var (dosZeit, dosDatum) = Dos(zeit ?? DateTime.Now);
        string sha = Convert.ToHexString(SHA256.HashData(daten));

        byte[] komprimiert;
        using (var ms = new MemoryStream())
        {
            using (var d = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                d.Write(daten, 0, daten.Length);
            komprimiert = ms.ToArray();
        }

        var salz = RandomNumberGenerator.GetBytes(SalzLaenge);
        var abgeleitet = Rfc2898DeriveBytes.Pbkdf2(_kennwort, salz, Runden, HashAlgorithmName.SHA1,
            2 * SchluesselLaenge + PruefwertLaenge);
        var aesSchluessel = abgeleitet.AsSpan(0, SchluesselLaenge).ToArray();
        var macSchluessel = abgeleitet.AsSpan(SchluesselLaenge, SchluesselLaenge).ToArray();
        var pruefwert = abgeleitet.AsSpan(2 * SchluesselLaenge, PruefwertLaenge).ToArray();
        CtrVerschluesseln(aesSchluessel, komprimiert);   // jetzt verschlüsselt (an Ort und Stelle)
        var mac = HMACSHA1.HashData(macSchluessel, komprimiert).AsSpan(0, MacLaenge).ToArray();
        Array.Clear(abgeleitet);
        Array.Clear(aesSchluessel);
        Array.Clear(macSchluessel);

        long datenLaenge = (long)SalzLaenge + PruefwertLaenge + komprimiert.Length + MacLaenge;
        long kopfLaenge = 30L + nameBytes.Length + ExtraLaenge;
        // Platz für den Rest: Zentralverzeichnis (je Datei 46 + Name + Extra) und Abschluss (22)
        long danach = _pos + kopfLaenge + datenLaenge;
        long verzeichnis = _eintraege.Sum(e => 46L + e.Name.Length + ExtraLaenge) + 46L + nameBytes.Length + ExtraLaenge + 22;
        if (danach + verzeichnis > MaxBytes || daten.LongLength > MaxBytes)
            throw new ArchivZuGrossException("Das Archiv würde größer als 4 GB – das passt nicht in eine Zip-Datei ohne Zip64.");

        var eintrag = new Eintrag(nameBytes, dosZeit, dosDatum, (uint)datenLaenge, (uint)daten.LongLength, (uint)_pos);

        var kopf = new byte[kopfLaenge];
        var k = kopf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(k[0..], 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(k[4..], VersionNoetig);
        BinaryPrimitives.WriteUInt16LittleEndian(k[6..], Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(k[8..], MethodeAes);
        BinaryPrimitives.WriteUInt16LittleEndian(k[10..], dosZeit);
        BinaryPrimitives.WriteUInt16LittleEndian(k[12..], dosDatum);
        BinaryPrimitives.WriteUInt32LittleEndian(k[14..], 0);                 // CRC-32: bei AE-2 nicht benutzt
        BinaryPrimitives.WriteUInt32LittleEndian(k[18..], eintrag.Komprimiert);
        BinaryPrimitives.WriteUInt32LittleEndian(k[22..], eintrag.Groesse);
        BinaryPrimitives.WriteUInt16LittleEndian(k[26..], (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(k[28..], ExtraLaenge);
        nameBytes.CopyTo(k[30..]);
        Extra(k[(30 + nameBytes.Length)..]);

        Schreiben(kopf);
        Schreiben(salz);
        Schreiben(pruefwert);
        Schreiben(komprimiert);
        Schreiben(mac);

        _eintraege.Add(eintrag);
        _dateien.Add(new Datei(name, daten.LongLength, sha));
    }

    /// <summary>Zentralverzeichnis und Abschluss schreiben. Liefert Größe und SHA-256 (Großbuchstaben) der ganzen Datei.</summary>
    public (long Groesse, string Sha256) Abschliessen()
    {
        if (_fertig) throw new InvalidOperationException("Das Archiv ist schon abgeschlossen.");
        _fertig = true;
        long start = _pos;
        foreach (var e in _eintraege)
        {
            var z = new byte[46 + e.Name.Length + ExtraLaenge];
            var s = z.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s[0..], 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], VersionErstellt);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], VersionNoetig);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(s[10..], MethodeAes);
            BinaryPrimitives.WriteUInt16LittleEndian(s[12..], e.Zeit);
            BinaryPrimitives.WriteUInt16LittleEndian(s[14..], e.Datum);
            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);              // CRC-32 (AE-2)
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], e.Komprimiert);
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], e.Groesse);
            BinaryPrimitives.WriteUInt16LittleEndian(s[28..], (ushort)e.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(s[30..], ExtraLaenge);
            BinaryPrimitives.WriteUInt16LittleEndian(s[32..], 0);              // Kommentar
            BinaryPrimitives.WriteUInt16LittleEndian(s[34..], 0);              // Datenträger
            BinaryPrimitives.WriteUInt16LittleEndian(s[36..], 0);              // interne Attribute
            BinaryPrimitives.WriteUInt32LittleEndian(s[38..], 0);              // externe Attribute
            BinaryPrimitives.WriteUInt32LittleEndian(s[42..], e.Offset);
            e.Name.CopyTo(s[46..]);
            Extra(s[(46 + e.Name.Length)..]);
            Schreiben(z);
        }
        long laenge = _pos - start;
        var ende = new byte[22];
        var en = ende.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(en[0..], 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(en[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(en[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(en[8..], (ushort)_eintraege.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(en[10..], (ushort)_eintraege.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(en[12..], (uint)laenge);
        BinaryPrimitives.WriteUInt32LittleEndian(en[16..], (uint)start);
        BinaryPrimitives.WriteUInt16LittleEndian(en[20..], 0);
        Schreiben(ende);
        _ziel.Flush();
        Vergessen();
        return (_pos, Convert.ToHexString(_gesamt.GetHashAndReset()));
    }

    // -----------------------------------------------------------------

    private void Schreiben(byte[] b)
    {
        _ziel.Write(b, 0, b.Length);
        _gesamt.AppendData(b);
        _pos += b.Length;
    }

    /// <summary>Extra-Feld 0x9901 (AES): Länge 7, Version 2 (AE-2), „AE“, Stärke 3 (256 Bit), Methode 8 (Deflate).</summary>
    private static void Extra(Span<byte> s)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 0x9901);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 7);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 2);
        s[6] = (byte)'A';
        s[7] = (byte)'E';
        s[8] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(s[9..], MethodeDeflate);
    }

    /// <summary>AES-256 im CTR-Verfahren (Zähler little-endian ab 1) – blockweise per ECB über einen Zählerpuffer.</summary>
    internal static void CtrVerschluesseln(byte[] schluessel, byte[] daten)
    {
        using var aes = Aes.Create();
        aes.Key = schluessel;
        const int Bloecke = 4096;                       // 64 KB je Durchgang
        var zaehler = new byte[16];
        var puffer = new byte[Bloecke * 16];
        var strom = new byte[Bloecke * 16];
        for (int pos = 0; pos < daten.Length; pos += puffer.Length)
        {
            int n = Math.Min(puffer.Length, daten.Length - pos);
            int bloecke = (n + 15) / 16;
            for (int b = 0; b < bloecke; b++)
            {
                for (int i = 0; i < 16; i++)
                    if (++zaehler[i] != 0) break;
                Buffer.BlockCopy(zaehler, 0, puffer, b * 16, 16);
            }
            aes.EncryptEcb(puffer.AsSpan(0, bloecke * 16), strom.AsSpan(0, bloecke * 16), PaddingMode.None);
            for (int i = 0; i < n; i++) daten[pos + i] ^= strom[i];
        }
        Array.Clear(strom);
    }

    private static (ushort Zeit, ushort Datum) Dos(DateTime t)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        if (t.Year > 2107) t = new DateTime(2107, 12, 31);
        return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)),
                (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }
}
