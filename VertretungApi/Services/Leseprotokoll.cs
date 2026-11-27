using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Helpers;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P6b – 5.4 Leseprotokoll (Entscheidungen 232, 233, 238).
//
//  Aufgezeichnet werden Lesezugriffe auf Schülerdaten (Einzel- und Listenansichten) und auf Daten über Lehrkräfte
//  („Nicht erfasst“ anderer Lehrkräfte, Abwesenheitsgründe der Vertretungen) – nicht eigene Daten, Pläne von Klassen
//  und Lehrkräften, Hausaufgaben und Themen, Downloads (eigenes Protokoll). Die Ansichten stehen im Katalog unten;
//  P7 ergänzt Ansichten mit der Rolle „Eltern“ (238).
//
//  Zusammengefasst (232): je Tag, Konto, Ansicht, Schüler und Gegenstand EIN Eintrag mit Anzahl, erster und letzter
//  Uhrzeit. Der Gegenstand ist lesbar und ohne Namen („Schüler-Id 12 (05A)“, „Klasse 05A“, „Stunde 18.11.2026, 3. ·
//  UNr 20 · L1“). „Gruppe“ hält bei Listenansichten die Lerngruppen (Bezeichnungen wie im Stundenblatt, mit Komma) –
//  die Auskunft nach Art. 15 findet darüber die Listenansichten der Lerngruppen eines Schülers (234).
//
//  Schalter „Leseprotokoll“ (Einstellungen → Datenschutz, Standard aus, 233) – gelesen aus dem Zwischenspeicher der
//  Einstellungen. Geschrieben wird in einem eigenen DbContext (berührt die Anfrage nicht) und nacheinander (eine
//  Sperre – sonst entstünden bei gleichzeitigen Aufrufen doppelte Zeilen). Kann ein Eintrag nicht geschrieben werden,
//  geht die Ansicht trotzdem (Warnung im Log, höchstens alle 5 Minuten). Tag = Zeitzone des Programms (PB-1a; echte Uhr, nicht
//  das Testdatum – wie die übrigen Zeitstempel). Frist: „Aufbewahrung der Protokolle“ (Aufraeumdienst).
// =====================================================================

/// <summary>Eine Ansicht im Katalog. Liste = Listenansicht (in der Schüler-Auskunft als Zahl je Monat und Rolle).</summary>
public sealed record LeseAnsichtDef(string Id, string Text, bool Liste);

public static class LeseAnsichten
{
    public const string FehlzeitenSchueler = "FehlzeitenSchueler";
    public const string FehlzeitenKlasse = "FehlzeitenKlasse";
    public const string OffeneFaelle = "OffeneFaelle";
    public const string FehlzeitenUnterricht = "FehlzeitenUnterricht";
    public const string NichtErfasst = "NichtErfasst";
    public const string Stundenblatt = "Stundenblatt";
    public const string StundenblattVerlauf = "StundenblattVerlauf";
    public const string EintraegeSchueler = "EintraegeSchueler";
    public const string EintraegeKlasse = "EintraegeKlasse";
    public const string EintraegeStunde = "EintraegeStunde";
    public const string EintraegeUnterricht = "EintraegeUnterricht";
    public const string EintragVerlauf = "EintragVerlauf";
    public const string PlanSchueler = "PlanSchueler";
    public const string Schuelerdaten = "Schuelerdaten";
    public const string Teilnehmer = "Teilnehmer";
    public const string Gruppenliste = "Gruppenliste";
    public const string VertretungenTag = "VertretungenTag";
    public const string VertretungenLehrkraft = "VertretungenLehrkraft";
    public const string Leseprotokoll = "Leseprotokoll";

    public static readonly IReadOnlyList<LeseAnsichtDef> Alle = new[]
    {
        new LeseAnsichtDef(FehlzeitenSchueler, "Fehlzeiten eines Schülers", false),
        new LeseAnsichtDef(EintraegeSchueler, "Einträge eines Schülers", false),
        new LeseAnsichtDef(PlanSchueler, "Plan eines Schülers", false),
        new LeseAnsichtDef(Schuelerdaten, "Schülerdaten", false),
        new LeseAnsichtDef(EintragVerlauf, "Verlauf eines Eintrags", false),
        new LeseAnsichtDef(FehlzeitenKlasse, "Fehlzeiten einer Klasse bzw. Stufe", true),
        new LeseAnsichtDef(OffeneFaelle, "Offene Fälle einer Klasse bzw. Stufe", true),
        new LeseAnsichtDef(FehlzeitenUnterricht, "Fehlzeiten eines Unterrichts", true),
        new LeseAnsichtDef(Stundenblatt, "Stundenblatt (Anwesenheit)", true),
        new LeseAnsichtDef(StundenblattVerlauf, "Verlauf eines Stundenblatts", true),
        new LeseAnsichtDef(EintraegeKlasse, "Einträge einer Klasse bzw. Stufe", true),
        new LeseAnsichtDef(EintraegeStunde, "Einträge einer Stunde", true),
        new LeseAnsichtDef(EintraegeUnterricht, "Einträge eines Unterrichts", true),
        new LeseAnsichtDef(Teilnehmer, "Teilnehmer einer Lerngruppe", true),
        new LeseAnsichtDef(Gruppenliste, "Gruppenliste", true),
        new LeseAnsichtDef(NichtErfasst, "Nicht erfasst (Stunden anderer Lehrkräfte)", false),
        new LeseAnsichtDef(VertretungenTag, "Vertretungen mit Abwesenheitsgründen (Tag)", false),
        new LeseAnsichtDef(VertretungenLehrkraft, "Vertretungen mit Abwesenheitsgründen (Lehrkraft)", false),
        new LeseAnsichtDef(Leseprotokoll, "Leseprotokoll (Zugriffe → Ansichten)", false),
    };

    private static readonly Dictionary<string, LeseAnsichtDef> NachId = Alle.ToDictionary(a => a.Id, StringComparer.Ordinal);

    public static LeseAnsichtDef? Finde(string? id) => id != null && NachId.TryGetValue(id, out var a) ? a : null;

    public static string Text(string id) => Finde(id)?.Text ?? id;

    public static bool IstListe(string id) => Finde(id)?.Liste ?? false;

    private static string D(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    // ---- Gegenstand (lesbar, ohne Namen) ----

    public static string SchuelerGegenstand(int id, string? klasse)
        => string.IsNullOrWhiteSpace(klasse) ? $"Schüler-Id {id}" : $"Schüler-Id {id} ({klasse.Trim()})";

    /// <summary>„Klasse 05A“ bzw. „Stufe Q1“ (wie die Titel der Fehlzeitenlisten).</summary>
    public static string GruppenGegenstand(string klasse)
    {
        var k = klasse.Trim().ToUpperInvariant();
        return FehlzeitenService.IstOberstufe(k) ? $"Stufe {k}" : $"Klasse {k}";
    }

    public static string UnterrichtGegenstand(int unr, string lehrer) => $"Unterricht UNr {unr} · {lehrer.Trim().ToUpperInvariant()}";

    public static string StundeGegenstand(DateOnly datum, PlanStunde st)
        => $"Stunde {D(datum)}, {st.Stunde}. · {st.Fach} · UNr {st.GrundUNr} · {st.GrundLehrer.Trim().ToUpperInvariant()}";

    /// <summary>
    /// Lerngruppen einer Stunde – wie das Stundenblatt seine Teilnehmer findet (KlassenbuchService.TeilnehmerAsync):
    /// Sek-I-Gruppe (Schülergruppe), sonst Klasse bzw. Kurs. Sonstige ohne Klassenangabe haben keine Schüler.
    /// </summary>
    public static string? GruppenDerStunde(PlanStunde st)
        => Liste(st.Quellen
            .Where(q => q.Typ is "Klasse" or "Kurs")
            .Select(q => q.Typ == "Klasse" && !string.IsNullOrWhiteSpace(q.Schuelergruppe) ? q.Schuelergruppe! : q.Key));

    /// <summary>Lerngruppen eines Unterrichts (UNr + Lehrkraft) im heute gültigen Grundplan – wie GruppenDerStunde.</summary>
    public static async Task<string?> GruppenDesUnterrichtsAsync(AppDbContext db, int unr, string lehrer)
    {
        var ver = await KlassenbuchService.AktuelleVersionAsync(db);
        if (ver == null) return null;
        string l = lehrer.Trim().ToUpperInvariant();
        var slots = await db.GrundplanSlots.AsNoTracking()
            .Where(s => s.VersionId == ver.Id && s.UNr == unr)
            .Select(s => new { s.Lehrer, s.LerngruppeTyp, s.LerngruppeKey, s.Schuelergruppe })
            .ToListAsync();
        return Liste(slots
            .Where(s => string.Equals(s.Lehrer.Trim(), l, StringComparison.OrdinalIgnoreCase)
                        && s.LerngruppeTyp is "Klasse" or "Kurs")
            .Select(s => s.LerngruppeTyp == "Klasse" && !string.IsNullOrWhiteSpace(s.Schuelergruppe) ? s.Schuelergruppe! : s.LerngruppeKey));
    }

    /// <summary>Bezeichnungen mit Komma, GROSS, ohne doppelte; null = keine.</summary>
    public static string? Liste(IEnumerable<string?> werte)
    {
        var teile = werte
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w!.Trim().ToUpperInvariant().Replace(",", " "))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();
        return teile.Count == 0 ? null : string.Join(",", teile);
    }

    /// <summary>Teile von „Gruppe“ (für die Auskunft).</summary>
    public static IEnumerable<string> Teile(string? gruppe)
        => (gruppe ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Was angesehen wurde. IstPseudo null = aus Schüler bzw. Lerngruppe bestimmen.</summary>
/// <param name="GruppeLaden">Lerngruppen erst beim Anlegen einer neuen Zeile bestimmen (z. B. aus dem Grundplan).</param>
public sealed record LeseZugriff(
    string Ansicht, string Gegenstand, int? SchuelerId = null, int? LerngruppeId = null, string? Gruppe = null,
    bool? IstPseudo = null, Func<AppDbContext, Task<string?>>? GruppeLaden = null);

/// <summary>Leseprotokoll (Singleton, Program.cs).</summary>
public sealed class Leseprotokoll
{
    public const int MaxGegenstand = 200;
    public const int MaxGruppe = 1000;

    private readonly IServiceScopeFactory _scopes;
    private readonly Schuleinstellungen _einst;
    private readonly ILogger<Leseprotokoll> _log;
    private readonly SemaphoreSlim _sperre = new(1, 1);
    private readonly object _warnSperre = new();
    private DateTime _letzteWarnung = DateTime.MinValue;

    public Leseprotokoll(IServiceScopeFactory scopes, Schuleinstellungen einst, ILogger<Leseprotokoll> log)
    {
        _scopes = scopes;
        _einst = einst;
        _log = log;
    }

    /// <summary>Einen Lesezugriff festhalten (Schalter aus = nichts). Fehler brechen die Ansicht nie ab.</summary>
    public async Task AufzeichnenAsync(ClaimsPrincipal user, LeseZugriff z)
    {
        try
        {
            if (Rollen.BenutzerId(user) is not int kontoId) return;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await _einst.LeseprotokollAnAsync(db)) return;
            string akteur = user.Identity?.Name ?? "";
            string rolle = user.FindFirst(ClaimTypes.Role)?.Value ?? "";
            await SchreibenAsync(db, kontoId, akteur, rolle, z, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            bool melden;
            lock (_warnSperre)
            {
                melden = DateTime.UtcNow - _letzteWarnung > TimeSpan.FromMinutes(5);
                if (melden) _letzteWarnung = DateTime.UtcNow;
            }
            if (melden)
                _log.LogWarning(ex, "Leseprotokoll: Eintrag „{Ansicht}“ konnte nicht gespeichert werden – die Ansicht geht trotzdem.",
                    z.Ansicht);
        }
    }

    private async Task SchreibenAsync(AppDbContext db, int kontoId, string akteur, string rolle, LeseZugriff z, DateTime jetzt)
    {
        var tag = Ortszeit.TagAusUtc(jetzt);   // PB-1a (310): Tag in der Zeitzone des Programms
        string gegenstand = Kuerzen(z.Gegenstand, MaxGegenstand);
        int? schuelerId = z.SchuelerId;
        string ansicht = z.Ansicht;

        await _sperre.WaitAsync();
        try
        {
            var zeile = await db.LeseProtokolle.FirstOrDefaultAsync(p => p.Tag == tag && p.KontoId == kontoId
                && p.Ansicht == ansicht && p.SchuelerId == schuelerId && p.Gegenstand == gegenstand);
            if (zeile != null)
            {
                zeile.Anzahl++;
                zeile.LetzteZeit = jetzt;
            }
            else
            {
                string? gruppe = z.Gruppe;
                if (gruppe == null && z.GruppeLaden != null) gruppe = await z.GruppeLaden(db);
                db.LeseProtokolle.Add(new LeseProtokoll
                {
                    Tag = tag,
                    ErsteZeit = jetzt,
                    LetzteZeit = jetzt,
                    Anzahl = 1,
                    KontoId = kontoId,
                    Akteur = akteur,
                    Rolle = rolle,
                    Ansicht = ansicht,
                    SchuelerId = schuelerId,
                    LerngruppeId = z.LerngruppeId,
                    Gruppe = gruppe == null ? null : Kuerzen(gruppe, MaxGruppe),
                    Gegenstand = gegenstand,
                    IstPseudo = z.IstPseudo ?? await PseudoAsync(db, schuelerId, z.LerngruppeId, gruppe),
                });
            }
            await db.SaveChangesAsync();
        }
        finally
        {
            _sperre.Release();
        }
    }

    /// <summary>Pseudo-Merker (für „Pseudodaten entfernen“, P9): aus Schüler, Lerngruppe bzw. den Gruppen.</summary>
    private static async Task<bool> PseudoAsync(AppDbContext db, int? schuelerId, int? lerngruppeId, string? gruppe)
    {
        if (schuelerId is int sid)
            return await db.Schueler.AsNoTracking().Where(s => s.Id == sid).Select(s => s.IstPseudo).FirstOrDefaultAsync();
        if (lerngruppeId is int lid)
            return await db.Lerngruppen.AsNoTracking().Where(l => l.Id == lid).Select(l => l.IstPseudo).FirstOrDefaultAsync();
        var teile = LeseAnsichten.Teile(gruppe).ToList();
        if (teile.Count == 0) return false;
        return await db.Lerngruppen.AsNoTracking().AnyAsync(l => l.IstPseudo && teile.Contains(l.Bezeichnung.ToUpper()))
            || await db.Schueler.AsNoTracking().AnyAsync(s => s.IstPseudo && teile.Contains(s.Stufe.ToUpper()));
    }

    private static string Kuerzen(string text, int max)
    {
        var t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }

    // -----------------------------------------------------------------
    //  Frist (Aufräumen) – „Aufbewahrung der Protokolle“
    // -----------------------------------------------------------------

    /// <summary>Erster Tag, der bei dieser Frist bleibt (Zeitzone des Programms, echte Uhr – PB-1a, 310).</summary>
    public static DateOnly Grenze(int tage) => Ortszeit.Heute.AddDays(-tage);

    /// <summary>Einträge älter als die Frist löschen (0 = nicht löschen).</summary>
    public static int Aufraeumen(AppDbContext db, int tage)
    {
        if (tage <= 0) return 0;
        var grenze = Grenze(tage);
        return db.LeseProtokolle.Where(p => p.Tag < grenze).ExecuteDelete();
    }

    public static Task<int> ZaehlenAsync(AppDbContext db, int tage)
    {
        if (tage <= 0) return Task.FromResult(0);
        var grenze = Grenze(tage);
        return db.LeseProtokolle.CountAsync(p => p.Tag < grenze);
    }
}
