using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  Phase 5.2b-1 (Entscheidungen 68/79/165): Gültigkeit der Zugehörigkeiten.
//
//  Eine Zugehörigkeit gilt von GueltigVon bis GueltigBis (jeweils einschließlich);
//  leer = ab Schuljahresbeginn bzw. offen. Ohne Datum (Seed, Gruppenlisten der
//  Fachlehrkräfte, alle Zeilen vor 5.2b) verhält sich alles wie bisher.
//
//  Maßgeblich ist der Tag der Stunde: Stundenblatt-Teilnehmer, Schülerplan je Tag,
//  Soll/Fehlquote je Stunde, Einträge und Listen einer Klasse über einen Zeitraum.
//  „Heute" gilt für Anzeigen des aktuellen Stands (Klasse eines Schülers, Konten,
//  Zuständigkeit, Gruppenübersicht, Lerngruppen-Listen).
//  Wer in einer Stunde schon eine Anwesenheit hat, bleibt in ihrer Liste (79) – das
//  regelt das Stundenblatt selbst („gehört nicht mehr zur Liste").
//
//  Sek-I-Gruppen aus SchILD (81): Ab SchildAb kommt die Liste aus SchILD (Gruppeneditor nur
//  lesen); bis zum Vortag gilt die Liste der Fachlehrkraft. Eine Gruppe ohne eigene Liste
//  bekommt die SchILD-Liste ab Schuljahresbeginn (b-2) – so bleibt keine Stunde „offen".
// =====================================================================

public static class Gueltigkeit
{
    public const string QuelleHand = "Hand";
    public const string QuelleSeed = "Seed";
    public const string QuelleSchild = "SchILD";

    /// <summary>Zugehörigkeiten, die am <paramref name="tag"/> gelten.</summary>
    public static IQueryable<Zugehoerigkeit> GueltigAm(this IQueryable<Zugehoerigkeit> q, DateOnly tag)
        => q.Where(z => (z.GueltigVon == null || z.GueltigVon <= tag) && (z.GueltigBis == null || z.GueltigBis >= tag));

    /// <summary>Zugehörigkeiten, die an mindestens einem Tag von <paramref name="von"/> bis <paramref name="bis"/> gelten.</summary>
    public static IQueryable<Zugehoerigkeit> GueltigIm(this IQueryable<Zugehoerigkeit> q, DateOnly von, DateOnly bis)
        => q.Where(z => (z.GueltigVon == null || z.GueltigVon <= bis) && (z.GueltigBis == null || z.GueltigBis >= von));

    /// <summary>Gilt ein Zeitraum (von–bis, leer = offen) am <paramref name="tag"/>?</summary>
    public static bool GiltAm(DateOnly? von, DateOnly? bis, DateOnly tag)
        => (von == null || von <= tag) && (bis == null || bis >= tag);

    /// <summary>Gilt die Zugehörigkeit am <paramref name="tag"/>? (im Speicher)</summary>
    public static bool GiltAm(this Zugehoerigkeit z, DateOnly tag) => GiltAm(z.GueltigVon, z.GueltigBis, tag);

    /// <summary>Ist die Zeile aus SchILD (dann ändert sie nur der Import)?</summary>
    public static bool IstSchild(string? quelle)
        => string.Equals(quelle, QuelleSchild, StringComparison.OrdinalIgnoreCase);

    /// <summary>Kommt die Liste der Gruppe am <paramref name="tag"/> aus SchILD (Gruppeneditor nur lesen)?</summary>
    public static bool ListeAusSchild(Lerngruppe lg, DateOnly tag)
        => IstSchild(lg.Quelle) && lg.SchildAb is DateOnly ab && tag >= ab;

    /// <summary>Alle Lerngruppen eines Schülers mit ihrer Gültigkeit (Schülerplan je Tag).</summary>
    public static async Task<SchuelerGruppen> SchuelerGruppenAsync(AppDbContext db, int schuelerId)
        => new SchuelerGruppen((await db.Zugehoerigkeiten.AsNoTracking()
                .Where(z => z.SchuelerId == schuelerId)
                .Select(z => new { z.Lerngruppe!.Bezeichnung, z.GueltigVon, z.GueltigBis })
                .ToListAsync())
            .Select(z => (Schluessel: z.Bezeichnung, Von: z.GueltigVon, Bis: z.GueltigBis))
            .ToList());
}

/// <summary>Lerngruppen-Schlüssel eines Schülers mit Gültigkeit (Phase 5.2b-1).</summary>
public sealed class SchuelerGruppen
{
    private readonly List<(string Schluessel, DateOnly? Von, DateOnly? Bis)> _zeilen;

    public SchuelerGruppen(List<(string Schluessel, DateOnly? Von, DateOnly? Bis)> zeilen) => _zeilen = zeilen;

    /// <summary>Schlüssel, die am <paramref name="tag"/> gelten.</summary>
    public List<string> Am(DateOnly tag)
        => _zeilen.Where(z => Gueltigkeit.GiltAm(z.Von, z.Bis, tag))
                  .Select(z => z.Schluessel)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
}
