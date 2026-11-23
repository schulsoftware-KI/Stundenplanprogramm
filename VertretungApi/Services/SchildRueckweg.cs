using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Model;

namespace VertretungApi.Services;

// =====================================================================
//  P4b (5.3a, Entscheidungen 210–213): Rückweg der Fehlstunden nach SchILD (SchuelerFehlstunden.dat).
//
//  Übertragen werden Fehlstunden mit Bewertung entschuldigt (J) bzw. unentschuldigt (N); beurlaubte Stunden zählen als
//  entschuldigt (Frage A). Nicht übertragen: offene Fehlstunden, Verspätungen (SchILD kennt keine Minuten) und
//  schulische Veranstaltungen – die Zahlen stehen in der Vorschau. Dieselbe Stunde zweimal erfasst (zwei Lektionen
//  zur selben Zeit) zählt einmal, unentschuldigt vor entschuldigt.
//
//  Fach = Fach der Stunde laut Plan des Tages (bei einer Vertretung bleibt das Fach des Schülers, bei „verlegt hin“
//  das der verlegten Stunde); Lehrkraft = laut Grundplan (bei „verlegt hin“ die der verlegten Stunde). Die Übersetzung
//  in SchILD-Fach und SchILD-Kürzel macht der Desktop über Fächer- und Kürzeltabelle der Zuordnung.
//
//  Der Server kennt kein Geburtsdatum (72): Er liefert je Fehlstunde nur die Prüfsumme (ExtId) – Namen, Geburtsdatum
//  und Klasse des Abschnitts kommen im Desktop aus dem SchILD-Ordner. Nur Schüler der Art des Betriebs (Entwicklung:
//  Pseudodaten, Echtbetrieb: echte Daten, 83).
// =====================================================================

public static class SchildRueckweg
{
    public const string ArtAbruf = Jahresablauf.ArtSchildRueckweg;      // „SchildRueckweg“ – Desktop hat abgeholt
    public const string ArtDatei = "SchildRueckwegDatei";               // vollständige Datei geschrieben
    public const string ArtProbe = "SchildRueckwegProbe";               // Probedatei (Klassen/Stufen bzw. kürzerer Zeitraum)
    public static readonly string[] Arten = { ArtAbruf, ArtDatei, ArtProbe };

    public const string Entschuldigt = "entschuldigt", Unentschuldigt = "unentschuldigt", Beurlaubt = "beurlaubt";

    /// <summary>So viele Tage nach „2. Halbjahr ab“ ist das 1. Halbjahr noch der Standard (zuletzt beendet).</summary>
    public const int NachlaufTage = 42;

    private static string D(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------
    //  Zeiträume
    // -----------------------------------------------------------------

    /// <summary>
    /// Wählbare Zeiträume (1./2. Halbjahr bzw. das Schuljahr, wenn es kein 2. Halbjahr gibt) und der Standard: das laufende
    /// Halbjahr, in den ersten 6 Wochen des 2. Halbjahres noch das gerade beendete 1. Halbjahr.
    /// </summary>
    public static (List<RwZeitraumDto> Liste, string Standard) Zeitraeume(KalenderDb kal, DateOnly? h, DateOnly heute)
    {
        var liste = new List<RwZeitraumDto>();
        if (h is not DateOnly hj)
        {
            liste.Add(new RwZeitraumDto("sj", "Schuljahr", kal.Start, kal.Ende));
            return (liste, "sj");
        }
        liste.Add(new RwZeitraumDto("hj1", "1. Halbjahr", kal.Start, hj.AddDays(-1)));
        liste.Add(new RwZeitraumDto("hj2", "2. Halbjahr", hj, kal.Ende));
        string standard = heute < hj || heute < hj.AddDays(NachlaufTage) ? "hj1" : "hj2";
        return (liste, standard);
    }

    /// <summary>
    /// Zeitraum prüfen: im Kalender des Schuljahres, „bis“ höchstens heute (spätere Stunden gibt es noch nicht). null +
    /// fehler, wenn er nicht passt.
    /// </summary>
    public static (DateOnly Von, DateOnly Bis)? Pruefen(KalenderDb kal, DateOnly von, DateOnly bis, DateOnly heute, out string? fehler)
    {
        fehler = null;
        if (bis < von) { fehler = "„bis“ liegt vor „von“."; return null; }
        if (von < kal.Start || bis > kal.Ende)
        {
            fehler = $"Der Zeitraum muss im Schuljahr {kal.Schuljahr} liegen ({D(kal.Start)} – {D(kal.Ende)}).";
            return null;
        }
        if (von > heute) { fehler = "Der Zeitraum liegt ganz in der Zukunft."; return null; }
        return (von, bis > heute ? heute : bis);
    }

    // -----------------------------------------------------------------
    //  Fehlstunden
    // -----------------------------------------------------------------

    private sealed record Roh(int SchuelerId, string? ExtId, DateOnly Datum, int Stunde, string Fach, string Lehrer,
        string GrundLehrer, string LStatus, AnwesenheitStatus AStatus, FehlBewertung Bewertung);

    /// <summary>Rang bei doppelt erfassten Stunden: unentschuldigt vor entschuldigt vor beurlaubt.</summary>
    private static int Rang(string art) => art == Unentschuldigt ? 0 : art == Entschuldigt ? 1 : 2;

    /// <summary>Fehlstunden des Zeitraums (Zeilen nur mit Prüfsumme) und die Zahlen dazu.</summary>
    public static async Task<(List<RwZeileDto> Zeilen, RwZahlenDto Zahlen)> DatenAsync(AppDbContext db, DateOnly von,
        DateOnly bis, bool pseudo)
    {
        var roh = await db.Anwesenheiten.AsNoTracking()
            .Where(a => a.Lektion!.Datum >= von && a.Lektion.Datum <= bis && a.Schueler!.IstPseudo == pseudo
                        && a.Status != AnwesenheitStatus.Anwesend)
            .Select(a => new Roh(a.SchuelerId, a.Schueler!.ExtId, a.Lektion!.Datum, a.Lektion.Stunde, a.Lektion.Fach,
                a.Lektion.Lehrer, a.Lektion.GrundLehrer, a.Lektion.Status, a.Status, a.Bewertung))
            .ToListAsync();
        return Auswerten(roh);
    }

    private static (List<RwZeileDto>, RwZahlenDto) Auswerten(List<Roh> roh)
    {
        int offen = 0, versp = 0, veranst = 0;
        var kandidaten = new Dictionary<(int, DateOnly, int), (Roh R, string Art)>();
        int doppelt = 0;
        foreach (var r in roh)
        {
            string? art = null;
            switch (r.AStatus)
            {
                case AnwesenheitStatus.Verspaetet: versp++; break;
                case AnwesenheitStatus.Schulveranstaltung: veranst++; break;
                case AnwesenheitStatus.Beurlaubt: art = Beurlaubt; break;
                case AnwesenheitStatus.Fehlt:
                    if (r.Bewertung == FehlBewertung.Entschuldigt) art = Entschuldigt;
                    else if (r.Bewertung == FehlBewertung.Unentschuldigt) art = Unentschuldigt;
                    else offen++;
                    break;
            }
            if (art == null) continue;
            var key = (r.SchuelerId, r.Datum, r.Stunde);
            if (kandidaten.TryGetValue(key, out var alt))
            {
                doppelt++;
                if (Rang(art) < Rang(alt.Art)) kandidaten[key] = (r, art);
            }
            else kandidaten[key] = (r, art);
        }

        int ent = 0, unent = 0, beur = 0, ohne = 0;
        var schuelerOhne = new HashSet<int>();
        var schueler = new HashSet<int>();
        var zeilen = new List<RwZeileDto>(kandidaten.Count);
        foreach (var (r, art) in kandidaten.Values.OrderBy(x => x.R.Datum).ThenBy(x => x.R.Stunde).ThenBy(x => x.R.SchuelerId))
        {
            if (string.IsNullOrWhiteSpace(r.ExtId))
            {
                ohne++;
                schuelerOhne.Add(r.SchuelerId);
                continue;
            }
            if (art == Entschuldigt) ent++;
            else if (art == Unentschuldigt) unent++;
            else beur++;
            schueler.Add(r.SchuelerId);
            bool verlegt = string.Equals(r.LStatus, "VerlegtHin", StringComparison.Ordinal);
            string lehrkraft = (verlegt && !string.IsNullOrWhiteSpace(r.Lehrer) ? r.Lehrer : r.GrundLehrer).Trim().ToUpperInvariant();
            zeilen.Add(new RwZeileDto(r.ExtId!, r.Datum, r.Stunde, (r.Fach ?? "").Trim(), lehrkraft, art));
        }
        return (zeilen, new RwZahlenDto(ent, unent, beur, offen, versp, veranst, doppelt, ohne, schuelerOhne.Count, schueler.Count));
    }

    /// <summary>Stunden ohne Anwesenheit im Zeitraum (wie Klassenbuch → „Nicht erfasst“, Ansicht „Alle“).</summary>
    public static async Task<int> NichtErfasstAsync(AppDbContext db, SchulWerte w, DateOnly von, DateOnly bis, string akteur)
    {
        var admin = new KbNutzer(akteur, BenutzerRolle.Admin, null, Array.Empty<string>());
        var ne = await FehlzeitenService.NichtErfasstAsync(db, admin, "alle", null, null, w.ErfassungAb, w.ThemaAb,
            "anwesenheit", von, bis);
        return ne.OhneAnwesenheit;
    }

    /// <summary>Frühere Bereitstellungen, die den Zeitraum berühren (neueste zuerst, höchstens 20).</summary>
    public static async Task<List<RwFruehererDto>> FruehereAsync(AppDbContext db, DateOnly von, DateOnly bis)
        => await db.DownloadProtokolle.AsNoTracking()
            .Where(p => Arten.Contains(p.Art) && p.Von <= bis && p.Bis >= von)
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(20)
            .Select(p => new RwFruehererDto(p.Art, p.Zeit, p.Akteur, p.Von, p.Bis, p.Was))
            .ToListAsync();

    // -----------------------------------------------------------------
    //  Texte fürs Download-Protokoll (ohne Namen)
    // -----------------------------------------------------------------

    public static string AbrufText(RwZahlenDto z, string desktop)
        => $"Fehlstunden für SchILD – abgeholt vom {desktop}: {z.Entschuldigt + z.Unentschuldigt + z.Beurlaubt} Fehlstunden " +
           $"(entschuldigt {z.Entschuldigt}, unentschuldigt {z.Unentschuldigt}, beurlaubt {z.Beurlaubt}) von {z.Schueler} " +
           $"Schülern; nicht übertragen: offen {z.Offen}, Verspätungen {z.Verspaetungen}, ohne Prüfsumme {z.OhnePruefsumme}";

    public static string DateiText(RwDateiMeldungDto m, string desktop)
        => (m.Probe ? "Probedatei" : "Datei") + $" SchuelerFehlstunden.dat geschrieben ({desktop}): Jahr {m.Jahr}, Abschnitt " +
           $"{m.Abschnitt}, " + (m.Probe ? (string.IsNullOrWhiteSpace(m.Klassen) ? "alle Klassen" : "Klassen/Stufen " + m.Klassen) : "alle Klassen") +
           $" · {m.Zeilen} Zeilen, {m.Fehlstunden} Fehlstunden (J {m.Entschuldigt}, N {m.Unentschuldigt}) von {m.Schueler} Schülern";

    /// <summary>„Desktop (Windows-Benutzer)“ – ohne Steuerzeichen, gekürzt.</summary>
    public static string Desktop(string? benutzer)
    {
        var b = new string((benutzer ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (b.Length > 60) b = b[..60];
        return b.Length == 0 ? "Desktop" : $"Desktop ({b})";
    }

    /// <summary>Klassen-Text einer Probedatei säubern (nur Buchstaben, Ziffern, Komma, Leerzeichen; höchstens 200 Zeichen).</summary>
    public static string? KlassenText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(c => char.IsLetterOrDigit(c) || c is ',' or ' ' or '-' or '/').ToArray()).Trim();
        if (t.Length > 200) t = t[..200] + " …";
        return t.Length == 0 ? null : t;
    }
}
