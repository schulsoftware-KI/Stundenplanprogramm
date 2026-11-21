using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VertretungApi.Data;
using VertretungApi.Dtos;
using VertretungApi.Helpers;
using VertretungApi.Model;
using VertretungApi.Services;
using K = VertretungApi.Services.EinstellungsKatalog;

namespace VertretungApi.Controllers;

// =====================================================================
//  P3 – E-1 Schuleinstellungen in der App (Entscheidungen 138–141, 199–203), nur Admin (JWT):
//
//    GET  api/einstellungen              – alle Einstellungen mit Hilfetext, Standard, „gilt jetzt“, Abstimmung
//    PUT  api/einstellungen              – Änderungen speichern { aenderungen: [{ schluessel, wert,
//                                          abgestimmtAm?, abgestimmtMit? }], bestaetigt }
//                                          400 = Felder prüfen (felder: { schluessel: text }),
//                                          409 = eine kürzere Frist löscht Daten (loeschen: [...]) → mit bestaetigt
//    GET  api/einstellungen/protokoll    – Änderungen (wer, wann, alt → neu)
//
//  Öffentlich (ohne Anmeldung, Entscheidung 201) sind nur Name und Kurzname der Schule und die Dauer von
//  „Angemeldet bleiben“ – über GET api/auth/optionen und das Manifest.
//
//  Jede Änderung wirkt sofort (Zwischenspeicher wird verworfen), steht im Einstellungsprotokoll und im Log
//  (nur die Schlüssel). Ändert sich der Wert einer Einstellung mit Datenschutzwirkung, entfällt die bisherige
//  Abstimmung („abgestimmt am / mit“), wenn sie nicht im selben Schritt neu eingetragen wird. Eine kürzere
//  Frist zählt vorher die betroffenen Datensätze und braucht eine Bestätigung; danach wird gleich aufgeräumt.
// =====================================================================

[ApiController]
[Route("api/einstellungen")]
[Authorize(Roles = Rollen.Admin)]
public class EinstellungenController : ControllerBase
{
    private const int MaxAenderungen = 50;

    private readonly AppDbContext _db;
    private readonly Schuleinstellungen _einst;
    private readonly ILogger<EinstellungenController> _log;

    public EinstellungenController(AppDbContext db, Schuleinstellungen einst, ILogger<EinstellungenController> log)
    {
        _db = db;
        _einst = einst;
        _log = log;
    }

    private string Ich => User.Identity?.Name ?? "";

    private static readonly Regex Leerraum = new(@"\s+", RegexOptions.Compiled);
    private static string D(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------
    //  Lesen
    // -----------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Liste() => Ok(await ListeAsync());

    private async Task<EinstellungenDto> ListeAsync()
    {
        var zeilen = await _db.Einstellungen.AsNoTracking().ToDictionaryAsync(e => e.Schluessel, StringComparer.Ordinal);
        var heute = KlassenbuchService.Heute();
        string sj = _einst.Roh(zeilen, K.Schuljahr) ?? K.SchuljahrVon(heute);
        var kal = await _db.Kalender.AsNoTracking().FirstOrDefaultAsync(k => k.Schuljahr == sj);
        var w = _einst.Bauen(zeilen, sj, kal?.Start);

        var liste = new List<EinstellungDto>();
        foreach (var d in K.Alle)
        {
            zeilen.TryGetValue(d.Schluessel, out var z);
            string? roh = _einst.Roh(zeilen, d.Schluessel);
            liste.Add(new EinstellungDto(
                d.Schluessel, d.Gruppe, d.Titel, d.Typ.ToString().ToLowerInvariant(), roh, d.StandardText,
                Wirksam(d, roh, w, kal), d.Hilfe, d.DsbFrage, z?.AbgestimmtAm, z?.AbgestimmtMit, z?.GeaendertAm, z?.GeaendertVon,
                d.Typ == EinstellungsTyp.Zahl ? d.Min : null, d.Typ == EinstellungsTyp.Zahl ? d.Max : null,
                d.MaxLaenge > 0 ? d.MaxLaenge : null, d.Einheit, d.Loescht));
        }

        var hinweise = new List<string>();
        if (kal == null)
            hinweise.Add($"Für das Schuljahr {sj} gibt es in der App noch keinen Kalender – die Daten des Schuljahres werden " +
                         "erst damit geprüft (Desktop: Wocheneditor → „Grundplan veröffentlichen…“).");
        int offen = K.Alle.Count(x => x.MitDsb && !(zeilen.TryGetValue(x.Schluessel, out var zx) && zx.AbgestimmtAm != null));
        if (offen > 0)
            hinweise.Add(offen == 1
                ? "1 Einstellung mit Datenschutzwirkung ist noch nicht mit der bzw. dem Datenschutzbeauftragten abgestimmt."
                : $"{offen} Einstellungen mit Datenschutzwirkung sind noch nicht mit der bzw. dem Datenschutzbeauftragten abgestimmt.");

        return new EinstellungenDto(
            K.Gruppen.Select(g => new EinstellungsGruppeDto(g.Id, g.Titel, g.Text)).ToList(),
            liste,
            kal == null ? null : new EinstellungKalenderDto(kal.Schuljahr, kal.Start, kal.Ende),
            hinweise);
    }

    /// <summary>Was gerade gilt – lesbar, mit Herkunft beim Standard.</summary>
    private static string Wirksam(EinstellungsDef d, string? roh, SchulWerte w, KalenderDb? kal)
    {
        switch (d.Schluessel)
        {
            case K.SchuleName:
                return w.SchuleName.Length > 0 ? $"„{w.SchuleName}“" : "kein Name";
            case K.SchuleKurzname:
                return $"„{w.AppName}“";
            case K.Schuljahr:
                return roh == null ? $"{w.Schuljahr} (nach dem Datum)" : w.Schuljahr;
            case K.ErfassungAb:
                return D(w.ErfassungAb) + (roh != null ? "" : w.ErfassungAbAusKalender
                    ? " (erster Schultag laut Kalender)" : " (heute – für das Schuljahr gibt es noch keinen Kalender)");
            case K.ThemaAb:
                return D(w.ThemaAb) + (roh == null ? " (wie „Anwesenheit erfasst ab“)" : "");
            case K.HalbjahrAb:
            {
                if (kal == null)
                    return w.HalbjahrAb is DateOnly h0 ? D(h0) : "1. Februar (für das Schuljahr gibt es noch keinen Kalender)";
                var hj = FehlzeitenService.HalbjahrAb(kal.Start, kal.Ende, w.HalbjahrAb);
                if (hj == null) return "kein 2. Halbjahr (der 1. Februar liegt nicht im Schuljahr laut Kalender)";
                return D(hj.Value) + (w.HalbjahrAb == hj ? "" : " (1. Februar)");
            }
            case K.BerichteNurAdmin:
                return w.BerichteNurAdmin ? "an – nur die Verwaltung" : "aus – auch Lehrkräfte für ihre Klassen und Kurse";
            default:
                int zahl = d.Schluessel switch
                {
                    K.EintraegeLoeschen => w.EintraegeLoeschenNachTagen,
                    K.ProtokollTage => w.ProtokollTage,
                    K.SitzungStunden => w.SitzungStunden,
                    K.AngemeldetBleibenTage => w.AngemeldetBleibenTage,
                    _ => 0,
                };
                return K.Text(d, zahl.ToString(CultureInfo.InvariantCulture));
        }
    }

    [HttpGet("protokoll")]
    public async Task<IActionResult> Protokoll([FromQuery] int limit = 200)
    {
        var liste = await _db.EinstellungsProtokolle.AsNoTracking()
            .OrderByDescending(p => p.Zeit).ThenByDescending(p => p.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync();
        int gesamt = await _db.EinstellungsProtokolle.CountAsync();
        return Ok(new
        {
            eintraege = liste.Select(p => new EinstellungsProtokollDto(p.Id, p.Zeit, p.Akteur, p.Schluessel,
                p.Schluessel == "*" ? "Alle Einstellungen" : K.Finde(p.Schluessel)?.Titel ?? p.Schluessel,
                p.Alt, p.Neu, p.Details)).ToList(),
            gesamt
        });
    }

    // -----------------------------------------------------------------
    //  Speichern
    // -----------------------------------------------------------------

    private sealed class Geplant
    {
        public required EinstellungsDef Def { get; init; }
        public Einstellung? Zeile { get; init; }
        public string? AltWert { get; init; }
        public string? NeuWert { get; init; }
        public DateOnly? AltAm { get; init; }
        public string? AltMit { get; init; }
        public DateOnly? Am { get; set; }
        public string? Mit { get; set; }
        public bool WertGeaendert => AltWert != NeuWert;
        public bool DsbGeaendert => AltAm != Am || AltMit != Mit;
    }

    [HttpPut]
    public async Task<IActionResult> Speichern([FromBody] EinstellungenSpeichernDto dto)
    {
        var liste = dto?.Aenderungen ?? new List<EinstellungsAenderungDto>();
        if (liste.Count == 0) return BadRequest(new { fehler = "Keine Änderungen übermittelt." });
        if (liste.Count > MaxAenderungen) return BadRequest(new { fehler = "Zu viele Änderungen auf einmal." });

        var heute = KlassenbuchService.Heute();
        var zeilen = (await _db.Einstellungen.ToListAsync()).ToDictionary(e => e.Schluessel, StringComparer.Ordinal);
        var felder = new Dictionary<string, string>(StringComparer.Ordinal);
        var geplant = new List<Geplant>();
        var gesehen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var a in liste)
        {
            var d = K.Finde(a?.Schluessel);
            if (a == null || d == null) { felder[a?.Schluessel ?? "?"] = "Diese Einstellung gibt es nicht."; continue; }
            if (!gesehen.Add(d.Schluessel)) { felder[d.Schluessel] = "Doppelt übermittelt."; continue; }

            var neu = K.Normalisieren(d, a.Wert, out var fehler);
            if (fehler != null) { felder[d.Schluessel] = fehler; continue; }

            zeilen.TryGetValue(d.Schluessel, out var z);
            var p = new Geplant
            {
                Def = d, Zeile = z, AltWert = _einst.Roh(zeilen, d.Schluessel), NeuWert = neu,
                AltAm = z?.AbgestimmtAm, AltMit = z?.AbgestimmtMit, Am = z?.AbgestimmtAm, Mit = z?.AbgestimmtMit,
            };

            if (d.MitDsb)
            {
                bool gesendet = a.AbgestimmtAm != null || a.AbgestimmtMit != null;
                if (gesendet)
                {
                    DateOnly? am = null;
                    var amText = (a.AbgestimmtAm ?? "").Trim();
                    if (amText.Length > 0)
                    {
                        if (!DateOnly.TryParseExact(amText, new[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var t) || t.Year < 2000)
                        { felder[d.Schluessel] = "„Abgestimmt am“: bitte ein Datum wählen."; continue; }
                        if (t > heute) { felder[d.Schluessel] = "„Abgestimmt am“ liegt in der Zukunft."; continue; }
                        am = t;
                    }
                    var mit = Leerraum.Replace((a.AbgestimmtMit ?? "").Trim(), " ");
                    if (mit.Any(char.IsControl)) { felder[d.Schluessel] = "„Mit“: bitte ohne Steuerzeichen."; continue; }
                    if (mit.Length > K.MaxAbgestimmtMit)
                    { felder[d.Schluessel] = $"„Mit“: höchstens {K.MaxAbgestimmtMit} Zeichen."; continue; }
                    if ((am == null) != (mit.Length == 0))
                    {
                        felder[d.Schluessel] = "Bitte „abgestimmt am“ und „mit“ beide ausfüllen – oder beide leer lassen.";
                        continue;
                    }
                    p.Am = am;
                    p.Mit = mit.Length > 0 ? mit : null;
                }
                else if (p.WertGeaendert)
                {
                    // Die Abstimmung galt für den alten Wert.
                    p.Am = null;
                    p.Mit = null;
                }
            }

            if (p.WertGeaendert || p.DsbGeaendert) geplant.Add(p);
        }

        if (felder.Count > 0)
            return BadRequest(new { fehler = "Bitte die markierten Felder prüfen.", felder });
        if (geplant.Count == 0)
            return Ok(new { geaendert = 0, geloescht = (object?)null, hinweise = new List<string>(), stand = await ListeAsync() });

        // ---- Schuljahr: Daten im Kalender, „Thema ab“ nicht vor „Erfassung ab“ ----
        var hinweise = new List<string>();
        if (geplant.Any(x => x.Def.Gruppe == "schuljahr"))
        {
            string? R(string s) => geplant.FirstOrDefault(x => x.Def.Schluessel == s) is Geplant g ? g.NeuWert : _einst.Roh(zeilen, s);
            string sj = R(K.Schuljahr) ?? K.SchuljahrVon(heute);
            var kal = await _db.Kalender.AsNoTracking().FirstOrDefaultAsync(k => k.Schuljahr == sj);
            var erf = K.AlsDatum(R(K.ErfassungAb));
            var thema = K.AlsDatum(R(K.ThemaAb));
            var hj = K.AlsDatum(R(K.HalbjahrAb));
            var erfEff = erf ?? kal?.Start ?? heute;
            var themaEff = thema ?? erfEff;
            if (kal != null)
            {
                string zr = $"{D(kal.Start)} – {D(kal.Ende)}";
                if (erf is DateOnly e1 && (e1 < kal.Start || e1 > kal.Ende))
                    felder[K.ErfassungAb] = $"Liegt nicht im Schuljahr {sj} ({zr}).";
                if (thema is DateOnly t1 && (t1 < kal.Start || t1 > kal.Ende))
                    felder[K.ThemaAb] = $"Liegt nicht im Schuljahr {sj} ({zr}).";
                if (hj is DateOnly h1 && (h1 <= kal.Start || h1 > kal.Ende))
                    felder[K.HalbjahrAb] = $"Liegt nicht im Schuljahr {sj} – das 2. Halbjahr beginnt nach dem ersten Schultag ({zr}).";
            }
            else
            {
                hinweise.Add($"Für das Schuljahr {sj} gibt es in der App noch keinen Kalender – die Daten werden erst damit " +
                             "geprüft (Desktop: „Grundplan veröffentlichen…“).");
            }
            if (!felder.ContainsKey(K.ThemaAb) && themaEff < erfEff)
                felder[K.ThemaAb] = $"Darf nicht vor „Anwesenheit erfasst ab“ ({D(erfEff)}) liegen.";
            if (felder.Count > 0)
                return BadRequest(new { fehler = "Bitte die markierten Felder prüfen.", felder });
        }

        // ---- Kürzere Fristen: vorher zählen und nachfragen (141) ----
        var loeschen = new List<EinstellungsLoeschungDto>();
        foreach (var p in geplant.Where(x => x.Def.Loescht && x.WertGeaendert))
        {
            int std = K.StandardZahl(p.Def.Schluessel);
            int alt = K.AlsZahl(p.AltWert, std), neu = K.AlsZahl(p.NeuWert, std);
            if (neu <= 0 || (alt > 0 && neu >= alt)) continue;
            bool eintraege = p.Def.Schluessel == K.EintraegeLoeschen;
            int n = eintraege ? await Aufraeumen.EintraegeZaehlenAsync(_db, neu) : await Aufraeumen.ProtokolleZaehlenAsync(_db, neu);
            if (n == 0) continue;
            loeschen.Add(new EinstellungsLoeschungDto(p.Def.Schluessel, p.Def.Titel, n, eintraege
                ? $"{n} Klassenbucheinträge (samt Verlauf), deren Datum länger als {neu} Tage zurückliegt"
                : $"{n} Protokolleinträge (Konten, Downloads, Einstellungen), die älter als {neu} Tage sind"));
        }
        if (loeschen.Count > 0 && dto?.Bestaetigt != true)
            return Conflict(new
            {
                bestaetigen = true,
                fehler = "Mit der kürzeren Frist werden gleich beim Speichern gelöscht: " +
                         string.Join("; ", loeschen.Select(l => l.Text)) + ".",
                loeschen
            });

        // ---- Schreiben + Protokoll ----
        var jetzt = DateTime.UtcNow;
        string ich = Ich;
        foreach (var p in geplant)
        {
            var z = p.Zeile;
            if (z == null)
            {
                z = new Einstellung { Schluessel = p.Def.Schluessel };
                _db.Einstellungen.Add(z);
            }
            z.Wert = p.NeuWert;
            z.AbgestimmtAm = p.Am;
            z.AbgestimmtMit = p.Mit;
            z.GeaendertAm = jetzt;
            z.GeaendertVon = ich;

            string? details = null;
            if (p.DsbGeaendert)
                details = p.Am != null
                    ? $"abgestimmt am {D(p.Am.Value)} mit {p.Mit}"
                    : p.WertGeaendert && p.AltAm != null ? "Abstimmung entfällt (Wert geändert)" : "Abstimmung entfernt";
            _db.EinstellungsProtokolle.Add(new EinstellungsProtokoll
            {
                Zeit = jetzt,
                Akteur = ich,
                Schluessel = p.Def.Schluessel,
                Alt = p.WertGeaendert ? K.Text(p.Def, p.AltWert) : null,
                Neu = p.WertGeaendert ? K.Text(p.Def, p.NeuWert) : null,
                Details = details,
            });
        }
        await _db.SaveChangesAsync();
        _einst.Verwerfen();
        _log.LogInformation("Einstellungen geändert von {Akteur}: {Schluessel}", ich,
            string.Join(", ", geplant.Select(x => x.Def.Schluessel)));

        // Kürzere bzw. neue Frist: gleich aufräumen (bestätigt).
        object? geloescht = null;
        if (geplant.Any(x => x.Def.Loescht && x.WertGeaendert))
        {
            var erg = Aufraeumen.Ausfuehren(_db, await _einst.LesenAsync(_db), _log);
            geloescht = new { protokolle = erg.Protokolle, eintraege = erg.Eintraege };
        }

        return Ok(new { geaendert = geplant.Count, geloescht, hinweise, stand = await ListeAsync() });
    }
}
