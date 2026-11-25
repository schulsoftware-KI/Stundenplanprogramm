using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P5c-1 (Entscheidungen 262, 263, 273): Archive entstehen im Hintergrund – der Archiv-Export eines abgeschlossenen
//  Schuljahres (Jahresablauf) und das Jahresarchiv aus 4.3 (Klassenbuch → Klassen), beide mit WinZip-AES-256.
//
//  Grund (262): Eine einzige lange Anfrage bricht beim Hoster bzw. Proxy oder beim Wegklicken auf dem Handy ab.
//  Ablauf: Starten (POST, Kennwort im Körper) → Stand abfragen (Fortschritt) → Datei einmal abholen.
//    - Höchstens EIN Auftrag je Server gleichzeitig (der Speicher liegt nur hier, nicht in der Datenbank).
//    - Die Temp-Datei (Path.GetTempPath(), „vertretung-archiv-<id>.zip“) ist schon verschlüsselt. Sie wird nach dem
//      Abholen gelöscht (DeleteOnClose), spätestens 30 Minuten nach dem Fertigwerden, beim Verwerfen und beim nächsten
//      Start der API (Reste). Ein Download, der abbricht, ist damit weg – dann das Archiv neu erstellen.
//    - Das Kennwort liegt nur im Arbeitsspeicher, solange die Datei entsteht (AesZipSchreiber.Vergessen), und erscheint
//      nie in Protokoll, Log oder Antwort – auch nicht bei Fehlern.
//    - Abholen und Verwerfen darf nur, wer den Auftrag gestartet hat (sein Kennwort); andere Admins sehen nur, dass
//      gerade ein Archiv entsteht.
//    - Ein Auftrag, der länger als 3 Stunden läuft, wird abgebrochen (Schutz vor einem hängenden Auftrag).
//    - P5c-2 (270): Während ein Schuljahr gelöscht wird, startet kein Archiv; gelöscht wird nur, solange kein Archiv entsteht
//      und kein fertiger Archiv-Export dieses Jahres auf den Download wartet (LoeschenPruefen/LoeschenBeenden, unter
//      derselben Sperre wie Starten).
//  Was der Server sich nach dem Abholen merkt (Zeile „Schuljahre“, Protokolle), schreibt JahresablaufArchiv.cs.
// =====================================================================

/// <summary>Art eines Archivs im Hintergrund.</summary>
public static class ArchivArt
{
    public const string Export = "export";              // Archiv-Export eines abgeschlossenen Schuljahres
    public const string Jahresarchiv = "jahresarchiv";  // Jahresarchiv aus 4.3 (frei gewählter Zeitraum)
}

/// <summary>Was ein Auftrag erstellen soll (vorher vom Aufrufer geprüft).</summary>
public sealed record ArchivPlan(
    string Art,
    string? Schuljahr,
    DateOnly Von,
    DateOnly Bis,
    bool MitSchuelerEintraegen,
    string Dateiname,
    string? Schule,
    IReadOnlyList<string>? Kopf,
    IReadOnlyList<ArchivCsvTeil>? Csv);

/// <summary>Stand eines Auftrags (Schnappschuss) – für die Antworten und das Protokoll nach dem Abholen.</summary>
public sealed record ArchivAuftragStand(
    Guid Id, ArchivPlan Plan, string Akteur, int KontoId, string Status, string Schritt, int Erledigt, int Gesamt,
    DateTime Start, DateTime? Fertig, long Groesse, string? Pruefsumme, string? Fehler, ArchivErgebnis? Ergebnis,
    int Dateien);

public sealed class ArchivAuftraege : BackgroundService
{
    public const int GueltigMinuten = 30;
    public const int HoechstensStunden = 3;
    public const string Praefix = "vertretung-archiv-";

    public const string Laeuft = "laeuft", Fertig = "fertig", Fehlgeschlagen = "fehler", Abgebrochen = "abgebrochen";

    private sealed class Auftrag
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required ArchivPlan Plan { get; init; }
        public required string Akteur { get; init; }
        public required int KontoId { get; init; }
        public DateTime Start { get; } = DateTime.UtcNow;
        public DateTime? FertigAm { get; set; }
        public string Status { get; set; } = Laeuft;
        public string Schritt { get; set; } = "wird vorbereitet";
        public int Erledigt { get; set; }
        public int Gesamt { get; set; }
        public string? Pfad { get; set; }
        public long Groesse { get; set; }
        public string? Pruefsumme { get; set; }
        public string? Fehler { get; set; }
        public ArchivErgebnis? Ergebnis { get; set; }
        public int Dateien { get; set; }
        public CancellationTokenSource Abbruch { get; } = new();
    }

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ArchivAuftraege> _log;
    private readonly object _sperre = new();
    private Auftrag? _auftrag;
    private bool _loeschen;      // P5c-2: gerade wird ein Schuljahr gelöscht

    public ArchivAuftraege(IServiceScopeFactory scopes, ILogger<ArchivAuftraege> log)
    {
        _scopes = scopes;
        _log = log;
    }

    private static string Uhr(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm");

    private static ArchivAuftragStand Schnappschuss(Auftrag a)
        => new(a.Id, a.Plan, a.Akteur, a.KontoId, a.Status, a.Schritt, a.Erledigt, a.Gesamt, a.Start, a.FertigAm, a.Groesse,
            a.Pruefsumme, a.Fehler, a.Ergebnis, a.Dateien);

    /// <summary>Stand für die App: eigener Auftrag mit allen Angaben, fremder nur mit Art, Person und Status.</summary>
    public JaArchivAuftragDto? Stand(int kontoId)
    {
        lock (_sperre)
        {
            var a = _auftrag;
            if (a == null) return null;
            bool eigen = a.KontoId == kontoId;
            DateTime? gueltigBis = a.Status == Fertig && a.FertigAm is DateTime f ? f.AddMinutes(GueltigMinuten) : null;
            return new JaArchivAuftragDto(a.Plan.Art, a.Plan.Schuljahr, a.Plan.Von, a.Plan.Bis, a.Plan.MitSchuelerEintraegen,
                a.Akteur, eigen, a.Status, a.Schritt, a.Erledigt, a.Gesamt, a.Start, a.FertigAm, gueltigBis,
                a.Status == Fertig ? a.Groesse : null,
                eigen && a.Status == Fertig ? a.Pruefsumme : null,
                eigen ? a.Plan.Dateiname : null,
                a.Fehler, a.Ergebnis?.Fehler.Count ?? 0);
        }
    }

    /// <summary>
    /// Startet einen Auftrag. Fehlertext, wenn schon einer läuft bzw. das fertige Archiv eines anderen Admins noch abzuholen
    /// ist; ein eigener (fertig, fehlgeschlagen) wird ersetzt. Das Kennwort ist vorher geprüft (Kennwort.Pruefen).
    /// </summary>
    public string? Starten(ArchivPlan plan, string akteur, int kontoId, string kennwort)
    {
        Auftrag neu;
        lock (_sperre)
        {
            if (_loeschen)   // P5c-2 (270)
                return "Gerade wird ein Schuljahr aus der Datenbank gelöscht – bitte danach erneut versuchen.";
            var alt = _auftrag;
            if (alt != null)
            {
                if (alt.Status == Laeuft)
                    return alt.KontoId == kontoId
                        ? "Gerade entsteht schon ein Archiv von dir – bitte warten oder es vorher abbrechen."
                        : $"Gerade erstellt {alt.Akteur} ein Archiv (seit {Uhr(alt.Start)} Uhr) – es geht immer nur eines " +
                          "gleichzeitig. Bitte später erneut versuchen.";
                if (alt.Status == Fertig && alt.KontoId != kontoId && alt.FertigAm is DateTime f)
                    return $"Das Archiv von {alt.Akteur} wartet noch auf den Download (bis {Uhr(f.AddMinutes(GueltigMinuten))} " +
                           "Uhr) – es geht immer nur eines gleichzeitig. Bitte danach erneut versuchen.";
                Entfernen(alt);
            }
            neu = new Auftrag { Plan = plan, Akteur = akteur, KontoId = kontoId };
            _auftrag = neu;
        }
        _ = Task.Run(() => LaufAsync(neu, kennwort));
        return null;
    }

    /// <summary>
    /// P5c-2 (270): Darf <paramref name="schuljahr"/> jetzt gelöscht werden? null = ja; mit <paramref name="sperren"/> startet
    /// danach kein Archiv mehr, bis <see cref="LoeschenBeenden"/>. Sonst der Grund (ein Archiv entsteht, ein fertiger
    /// Archiv-Export dieses Jahres wartet auf den Download, ein anderes Löschen läuft).
    /// </summary>
    public string? LoeschenPruefen(string schuljahr, bool sperren)
    {
        lock (_sperre)
        {
            if (_loeschen) return "Gerade wird schon ein Schuljahr gelöscht – bitte warten, bis es fertig ist.";
            var a = _auftrag;
            if (a != null && a.Status == Laeuft)
                return $"Gerade erstellt {a.Akteur} ein Archiv (seit {Uhr(a.Start)} Uhr) – erst danach löschen.";
            if (a != null && a.Status == Fertig && a.Plan.Art == ArchivArt.Export && a.Plan.Schuljahr == schuljahr)
                return $"Ein neuer Archiv-Export {schuljahr} von {a.Akteur} wartet noch auf den Download – erst herunterladen und " +
                       "seine Aufbewahrung bestätigen oder ihn verwerfen, dann löschen.";
            if (sperren) _loeschen = true;
            return null;
        }
    }

    /// <summary>P5c-2: nach dem Löschen (auch bei Fehlern) – Archive gehen wieder.</summary>
    public void LoeschenBeenden()
    {
        lock (_sperre) _loeschen = false;
    }

    /// <summary>Eigenen Auftrag abbrechen bzw. die fertige Datei verwerfen. false = es gab keinen eigenen.</summary>
    public bool Verwerfen(int kontoId)
    {
        lock (_sperre)
        {
            var a = _auftrag;
            if (a == null || a.KontoId != kontoId) return false;
            Entfernen(a);
            _auftrag = null;
            return true;
        }
    }

    /// <summary>
    /// Die fertige Datei übernehmen (einmal): Stream, der die Datei beim Schließen löscht, und der Stand. Danach gibt es den
    /// Auftrag nicht mehr. Sonst Fehlertext (Status 404 bzw. 409).
    /// </summary>
    public (FileStream? Datei, ArchivAuftragStand? Stand, int Status, string? Fehler) Abholen(int kontoId)
    {
        lock (_sperre)
        {
            var a = _auftrag;
            if (a == null)
                return (null, null, 404, "Es gibt kein Archiv zum Herunterladen (mehr) – jede Datei lässt sich nur einmal " +
                                         $"herunterladen und liegt höchstens {GueltigMinuten} Minuten bereit. Bitte neu erstellen.");
            if (a.KontoId != kontoId)
                return (null, null, 403, $"Dieses Archiv hat {a.Akteur} erstellt – nur {a.Akteur} kann es herunterladen.");
            if (a.Status == Laeuft) return (null, null, 409, "Das Archiv ist noch nicht fertig.");
            if (a.Status != Fertig || a.Pfad == null)
                return (null, null, 409, a.Fehler ?? "Das Archiv wurde nicht fertig – bitte neu erstellen.");
            FileStream datei;
            try
            {
                datei = new FileStream(a.Pfad, FileMode.Open, FileAccess.Read, FileShare.Delete, 81920,
                    FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Archiv: Die Temp-Datei ist nicht mehr lesbar.");
                Entfernen(a);
                _auftrag = null;
                return (null, null, 404, "Die Datei ist nicht mehr da – bitte das Archiv neu erstellen.");
            }
            var stand = Schnappschuss(a);
            a.Pfad = null;              // gehört jetzt dem Download (wird beim Schließen gelöscht)
            _auftrag = null;
            return (datei, stand, 200, null);
        }
    }

    // -----------------------------------------------------------------

    /// <summary>Auftrag beenden: abbrechen (läuft) bzw. Datei löschen. Aufruf nur unter _sperre.</summary>
    private void Entfernen(Auftrag a)
    {
        try { a.Abbruch.Cancel(); } catch (ObjectDisposedException) { }
        if (a.Status != Laeuft) Loeschen(a.Pfad);
        a.Pfad = null;
    }

    private void Loeschen(string? pfad)
    {
        if (pfad == null) return;
        try { File.Delete(pfad); }
        catch (Exception ex) { _log.LogWarning(ex, "Archiv: Temp-Datei konnte nicht gelöscht werden."); }
    }

    private void Fortschritt(Auftrag a, ArchivFortschritt f)
    {
        lock (_sperre)
        {
            a.Schritt = f.Schritt;
            a.Erledigt = f.Erledigt;
            a.Gesamt = f.Gesamt;
        }
    }

    private async Task LaufAsync(Auftrag a, string kennwort)
    {
        string pfad = Path.Combine(Path.GetTempPath(), $"{Praefix}{a.Id:N}.zip");
        var ct = a.Abbruch.Token;
        AesZipSchreiber? zip = null;
        string art = a.Plan.Art == ArchivArt.Export ? $"Archiv-Export {a.Plan.Schuljahr}" : "Jahresarchiv";
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var u = new KbNutzer(a.Akteur, BenutzerRolle.Admin, null, Array.Empty<string>());
            ArchivErgebnis erg;
            (long Groesse, string Sha256) ende;
            using (var fs = new FileStream(pfad, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                lock (_sperre)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    a.Pfad = pfad;     // ab jetzt räumt Entfernen() bzw. der Abbruch die Datei weg
                }
                zip = new AesZipSchreiber(fs, kennwort);
                kennwort = "";
                erg = await ExportService.ArchivSchreibenAsync(db, u, a.Plan.Von, a.Plan.Bis, a.Plan.MitSchuelerEintraegen, zip,
                    _log, ct, a.Plan.Schule, a.Plan.Kopf, a.Plan.Csv, a.Plan.Schuljahr, f => Fortschritt(a, f));
                ct.ThrowIfCancellationRequested();
                ende = zip.Abschliessen();
            }
            bool behalten;
            lock (_sperre)
            {
                behalten = ReferenceEquals(_auftrag, a) && !ct.IsCancellationRequested;
                if (behalten)
                {
                    a.Status = Fertig;
                    a.FertigAm = DateTime.UtcNow;
                    a.Groesse = ende.Groesse;
                    a.Pruefsumme = ende.Sha256;
                    a.Ergebnis = erg;
                    a.Dateien = zip.Dateien.Count;
                    a.Schritt = "fertig";
                    a.Erledigt = a.Gesamt;
                }
            }
            if (!behalten) Loeschen(pfad);
            else
                _log.LogInformation("{Art}: fertig für {Akteur} ({Dateien} Dateien, {Groesse} Bytes, {Fehler} nicht erstellt) – " +
                                    "wartet auf den Download.", art, a.Akteur, zip.Dateien.Count, ende.Groesse, erg.Fehler.Count);
        }
        catch (Exception ex)
        {
            bool abgebrochen = ex is OperationCanceledException && ct.IsCancellationRequested;
            if (abgebrochen) _log.LogInformation("{Art}: abgebrochen ({Akteur}).", art, a.Akteur);
            else if (ex is ArchivZuGrossException) _log.LogWarning("{Art}: zu groß – {Text}", art, ex.Message);
            else _log.LogError(ex, "{Art} konnte nicht erstellt werden.", art);
            lock (_sperre)
            {
                a.Status = abgebrochen ? Abgebrochen : Fehlgeschlagen;
                a.FertigAm = DateTime.UtcNow;
                a.Fehler = abgebrochen ? "Abgebrochen."
                    : ex is ArchivZuGrossException ? ex.Message
                    : "Das Archiv konnte nicht erstellt werden. Details stehen im Protokoll der API.";
                a.Pfad = null;
            }
            Loeschen(pfad);
        }
        finally
        {
            zip?.Vergessen();
        }
    }

    /// <summary>Beim Start: Reste löschen. Danach jede halbe Minute: abgelaufene Dateien weg, hängende Aufträge abbrechen.</summary>
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            foreach (var rest in Directory.GetFiles(Path.GetTempPath(), Praefix + "*.zip"))
                Loeschen(rest);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Archiv: Reste im Temp-Ordner konnten nicht geprüft werden.");
        }
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stop);
                lock (_sperre)
                {
                    var a = _auftrag;
                    if (a == null) continue;
                    var jetzt = DateTime.UtcNow;
                    if (a.Status != Laeuft && (a.FertigAm ?? a.Start).AddMinutes(GueltigMinuten) < jetzt)
                    {
                        if (a.Status == Fertig)
                            _log.LogInformation("Archiv: nicht abgeholt – nach {Minuten} Minuten gelöscht.", GueltigMinuten);
                        Entfernen(a);
                        _auftrag = null;
                    }
                    else if (a.Status == Laeuft && a.Start.AddHours(HoechstensStunden) < jetzt)
                    {
                        _log.LogWarning("Archiv: Auftrag läuft länger als {Stunden} Stunden – abgebrochen.", HoechstensStunden);
                        try { a.Abbruch.Cancel(); } catch (ObjectDisposedException) { }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            lock (_sperre)
            {
                if (_auftrag is Auftrag a) Entfernen(a);
                _auftrag = null;
            }
        }
    }
}
