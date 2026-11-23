using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P6b – 5.4 Grund der Entschuldigung (Entscheidung 236).
//
//  Schalter „Grund der Entschuldigung erfassen“ (Einstellungen → Datenschutz, Standard an): aus → die App zeigt keine
//  Auswahl mehr, gespeichert wird „ohne Angabe“ (FehlGrund.OhneAngabe, FehlzeitenService.GrundAusEingabe). Schon
//  gespeicherte Gründe bleiben – „Vorhandene Gründe entfernen…“ (nur bei ausgeschaltetem Schalter) setzt sie auf „ohne
//  Angabe“: an den Fehlstunden, an den Entschuldigungen (Zeiträume) und im Änderungsverlauf des Klassenbuchs (dort steht
//  der Grund in Klammern, z. B. „entschuldigt (Krankheit)“). Die Bewertung „entschuldigt“ bleibt. Vorher zählen, Rückfrage
//  mit Zahl, Eintrag im Einstellungsprotokoll (ohne Namen).
// =====================================================================

public sealed record GruendeZahlen(int Fehlstunden, int Entschuldigungen, int Verlauf)
{
    public int Gesamt => Fehlstunden + Entschuldigungen + Verlauf;

    public string Text()
        => $"Gründe an {Fehlstunden} {(Fehlstunden == 1 ? "Fehlstunde" : "Fehlstunden")} und {Entschuldigungen} " +
           $"{(Entschuldigungen == 1 ? "Entschuldigung" : "Entschuldigungen")}" +
           (Verlauf > 0 ? $", dazu {Verlauf} {(Verlauf == 1 ? "Eintrag" : "Einträge")} im Änderungsverlauf" : "");
}

public static class GrundSchalter
{
    /// <summary>Wie der Grund im Änderungsverlauf steht (FehlzeitenService.BewertungText / Entschuldigung).</summary>
    private static readonly string[] Klammern =
    {
        "(" + FehlzeitenService.GrundText(FehlGrund.Krankheit) + ")",
        "(" + FehlzeitenService.GrundText(FehlGrund.Arzttermin) + ")",
        "(" + FehlzeitenService.GrundText(FehlGrund.Familiaer) + ")",
        "(" + FehlzeitenService.GrundText(FehlGrund.Sonstiges) + ")",
    };

    private static readonly string OhneKlammer = "(" + FehlzeitenService.OhneAngabeText + ")";

    /// <summary>Text ohne Grund – „entschuldigt (Krankheit)“ → „entschuldigt (ohne Angabe)“. Unverändert, wenn keiner drinsteht.</summary>
    public static string? OhneGrund(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = text;
        foreach (var k in Klammern) t = t.Replace(k, OhneKlammer, StringComparison.Ordinal);
        return t;
    }

    public static bool HatGrund(string? text) => text != null && Klammern.Any(k => text.Contains(k, StringComparison.Ordinal));

    private static IQueryable<Anwesenheit> AnwesenheitenMitGrund(AppDbContext db)
        => db.Anwesenheiten.Where(a => a.Grund != null && a.Grund != FehlGrund.OhneAngabe);

    private static IQueryable<Entschuldigung> EntschuldigungenMitGrund(AppDbContext db)
        => db.Entschuldigungen.Where(e => e.Grund != FehlGrund.OhneAngabe);

    /// <summary>Verlaufseinträge, in denen ein Grund stehen kann (genau geprüft wird im Speicher).</summary>
    private static IQueryable<KlassenbuchProtokoll> VerlaufKandidaten(AppDbContext db)
        => db.KlassenbuchProtokolle.Where(p => (p.Art == "Bewertung" || p.Art == "Entschuldigung")
                                               && ((p.Alt != null && p.Alt.Contains("(")) || (p.Neu != null && p.Neu.Contains("("))));

    public static async Task<GruendeZahlen> ZaehlenAsync(AppDbContext db)
    {
        int fehl = await AnwesenheitenMitGrund(db).CountAsync();
        int ent = await EntschuldigungenMitGrund(db).CountAsync();
        var verlauf = await VerlaufKandidaten(db).AsNoTracking().Select(p => new { p.Alt, p.Neu }).ToListAsync();
        return new GruendeZahlen(fehl, ent, verlauf.Count(v => HatGrund(v.Alt) || HatGrund(v.Neu)));
    }

    /// <summary>Alle vorhandenen Gründe auf „ohne Angabe“ setzen (in Stücken). Rückgabe: was geändert wurde.</summary>
    public static async Task<GruendeZahlen> EntfernenAsync(AppDbContext db)
    {
        int fehl = 0, ent = 0, verlauf = 0;
        for (int runde = 0; runde < 10000; runde++)
        {
            var teil = await AnwesenheitenMitGrund(db).OrderBy(a => a.Id).Take(1000).ToListAsync();
            if (teil.Count == 0) break;
            foreach (var a in teil) a.Grund = FehlGrund.OhneAngabe;
            await db.SaveChangesAsync();
            fehl += teil.Count;
            foreach (var a in teil) db.Entry(a).State = EntityState.Detached;
        }
        for (int runde = 0; runde < 10000; runde++)
        {
            var teil = await EntschuldigungenMitGrund(db).OrderBy(e => e.Id).Take(1000).ToListAsync();
            if (teil.Count == 0) break;
            foreach (var e in teil) e.Grund = FehlGrund.OhneAngabe;
            await db.SaveChangesAsync();
            ent += teil.Count;
            foreach (var e in teil) db.Entry(e).State = EntityState.Detached;
        }
        int nachId = 0;
        for (int runde = 0; runde < 10000; runde++)
        {
            int ab = nachId;
            var teil = await VerlaufKandidaten(db).Where(p => p.Id > ab).OrderBy(p => p.Id).Take(1000).ToListAsync();
            if (teil.Count == 0) break;
            nachId = teil[^1].Id;
            foreach (var p in teil)
            {
                if (!HatGrund(p.Alt) && !HatGrund(p.Neu)) continue;
                p.Alt = OhneGrund(p.Alt);
                p.Neu = OhneGrund(p.Neu);
                verlauf++;
            }
            await db.SaveChangesAsync();
            foreach (var p in teil) db.Entry(p).State = EntityState.Detached;
        }
        return new GruendeZahlen(fehl, ent, verlauf);
    }
}
