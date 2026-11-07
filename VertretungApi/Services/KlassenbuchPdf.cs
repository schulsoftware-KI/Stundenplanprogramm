using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using VertretungApi.Dtos;

namespace VertretungApi.Services;

// =====================================================================
//  Klassenbuch/Kursheft als PDF (Phase 4.2, Entscheidung 44) mit QuestPDF –
//  dieselbe Bibliothek und Version wie im Desktop (VertretungsplanPdfExporter).
//
//  A4 hoch. Kopf auf jeder Seite: Art + Klasse/Kurs, Zeitraum, Schuljahr.
//  Fuß: „Enthält personenbezogene Daten", erstellt am/von, „Seite x von y".
//  Inhalt: Übersicht (Stunden, davon nicht erfasst) → je Unterrichtstag eine
//  Tabelle (Std · Fach/Lehrkraft · Thema/Hausaufgabe/Einträge · Anwesenheit) →
//  neue Seite: Fehlzeiten je Schüler (wie die Konferenzübersicht) → ggf.
//  Schülereinträge (Entscheidung 45).
//
//  QuestPDF-Community-Lizenz (für Schulen kostenlos), wie im Desktop.
// =====================================================================

public static class KlassenbuchPdf
{
    static KlassenbuchPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // Freitext (Thema, Hausaufgabe, Einträge) kann Zeichen enthalten, die keine Schrift
        // kennt (z. B. Emoji vom Handy). Dann lieber ein leeres Kästchen als ein Abbruch.
        // (Seit QuestPDF 2026.9 heißt die Einstellung ThrowOnMissingTextGlyphs.)
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    private const float Klein = 7.5f;
    // Farben bewusst direkt an der Aufrufstelle (Colors.* ist je nach QuestPDF-Version string
    // oder ein eigener Farbtyp): leise = Grey.Darken1, Linie = Grey.Lighten2,
    // rot = Red.Darken2, orange = Orange.Darken3.

    public static byte[] Erzeuge(Bericht b)
        => Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.4f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(8.5f));

                page.Header().Element(c => Kopf(c, b.Kopf));
                page.Content().PaddingTop(8).Element(c => Inhalt(c, b));
                page.Footer().Element(c => Fuss(c, b.Kopf));
            });
        }).GeneratePdf();

    // -----------------------------------------------------------------
    //  Kopf und Fuß
    // -----------------------------------------------------------------

    private static void Kopf(IContainer c, BerichtKopf k)
    {
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(links =>
                {
                    links.Item().Text($"{k.Art} · {k.Titel}").FontSize(14).SemiBold();
                    if (!string.IsNullOrWhiteSpace(k.Zusatz))
                        links.Item().Text(k.Zusatz).FontSize(9).FontColor(Colors.Grey.Darken1);
                });
                row.ConstantItem(170).AlignRight().Column(rechts =>
                {
                    rechts.Item().AlignRight().Text($"{k.Von:dd.MM.yyyy} – {k.Bis:dd.MM.yyyy}").FontSize(9).SemiBold();
                    if (!string.IsNullOrWhiteSpace(k.Schuljahr))
                        rechts.Item().AlignRight().Text($"Schuljahr {k.Schuljahr}").FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });
            col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Grey.Medium);
        });
    }

    private static void Fuss(IContainer c, BerichtKopf k)
    {
        c.Column(col =>
        {
            col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            col.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text(txt =>
                {
                    txt.DefaultTextStyle(x => x.FontSize(Klein).FontColor(Colors.Grey.Darken1));
                    txt.Span("Enthält personenbezogene Daten – vertraulich behandeln, nicht weitergeben.").SemiBold();
                    txt.Span($"  Erstellt am {k.ErstelltAm:dd.MM.yyyy HH:mm} von {k.ErstelltVon}.");
                });
                row.ConstantItem(70).AlignRight().Text(txt =>
                {
                    txt.DefaultTextStyle(x => x.FontSize(Klein).FontColor(Colors.Grey.Darken1));
                    txt.Span("Seite ");
                    txt.CurrentPageNumber();
                    txt.Span(" von ");
                    txt.TotalPages();
                });
            });
        });
    }

    // -----------------------------------------------------------------
    //  Inhalt
    // -----------------------------------------------------------------

    private static void Inhalt(IContainer c, Bericht b)
    {
        c.Column(col =>
        {
            col.Spacing(8);

            // Übersicht
            col.Item().Text(txt =>
            {
                txt.Span($"{b.StundenMitKlassenbuch} {(b.StundenMitKlassenbuch == 1 ? "Stunde" : "Stunden")} mit Klassenbuch");
                if (b.NichtErfasst > 0)
                    txt.Span($", davon {b.NichtErfasst} ohne erfasste Anwesenheit").FontColor(Colors.Red.Darken2).SemiBold();
                txt.Span(". Fehlzeiten je Schüler stehen im Anhang.");
                if (b.Kopf.MitSchuelerEintraegen) txt.Span(" Mit Schülereinträgen.");
            });

            if (b.Tage.Count == 0)
                col.Item().Text("Im Zeitraum gibt es keine Unterrichtsstunden.").Italic().FontColor(Colors.Grey.Darken1);

            foreach (var tag in b.Tage)
                col.Item().Element(x => TagBlock(x, tag));

            // Anhang: Fehlzeiten
            col.Item().PageBreak();
            col.Item().Text("Fehlzeiten im Zeitraum").FontSize(12).SemiBold();
            if (b.Fehlzeiten == null || b.Fehlzeiten.Schueler.Count == 0)
                col.Item().Text("Keine Schülerinnen und Schüler hinterlegt.").Italic().FontColor(Colors.Grey.Darken1);
            else
            {
                col.Item().Element(x => FehlzeitenTabelle(x, b.Fehlzeiten));
                col.Item().Text("Fehlstunden = fehlt + beurlaubt (beurlaubt zählt als entschuldigt); entsch./offen/unent. sind " +
                                "davon. Offene Verspätungen stehen in Klammern. Fehlquote = Fehlstunden ÷ erfasste Stunden, " +
                                "in denen die Schülerin bzw. der Schüler dabei sein sollte. Schulische Veranstaltungen zählen " +
                                "nicht als Fehlzeit.")
                    .FontSize(Klein).FontColor(Colors.Grey.Darken1);
            }

            // Anhang: Schülereinträge (Entscheidung 45)
            if (b.SchuelerEintraege != null)
            {
                col.Item().PaddingTop(10).Text("Schülereinträge").FontSize(12).SemiBold();
                if (b.SchuelerEintraege.Count == 0)
                    col.Item().Text("Im Zeitraum keine Schülereinträge.").Italic().FontColor(Colors.Grey.Darken1);
                else
                    col.Item().Element(x => SchuelerEintraegeTabelle(x, b.SchuelerEintraege));
            }
        });
    }

    private static void TagBlock(IContainer c, BerichtTag tag)
    {
        c.Column(col =>
        {
            col.Item().Background(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(5)
                .Text(BerichtService.TagText(tag.Datum)).FontSize(9.5f).SemiBold();
            if (tag.Hinweis != null)
                col.Item().PaddingHorizontal(5).PaddingTop(2).Text(tag.Hinweis).Italic().FontColor(Colors.Grey.Darken1);
            if (tag.Stunden.Count == 0) return;

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(24);     // Std
                    cd.RelativeColumn(2.1f);   // Fach, Lehrkraft, Status
                    cd.RelativeColumn(5.2f);   // Thema, Hausaufgabe, Klasseneinträge
                    cd.RelativeColumn(3.2f);   // Anwesenheit
                });
                foreach (var s in tag.Stunden)
                {
                    Zelle(table.Cell()).Text($"{s.Stunde}.").SemiBold();
                    Zelle(table.Cell()).Element(x => FachZelle(x, s));
                    Zelle(table.Cell()).Element(x => InhaltZelle(x, s));
                    Zelle(table.Cell()).Element(x => AnwesenheitZelle(x, s));
                }
            });
        });
    }

    private static IContainer Zelle(IContainer c)
        => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4);

    private static void FachZelle(IContainer c, BerichtStunde s)
    {
        c.Column(col =>
        {
            col.Item().Text(s.Fach).SemiBold();
            col.Item().Text(s.Lehrer);
            if (s.Klassen != null) col.Item().Text(s.Klassen).FontSize(Klein).FontColor(Colors.Grey.Darken1);
            if (s.Status != null)
                col.Item().Text(s.Status).FontSize(Klein).SemiBold().FontColor(s.MitKlassenbuch ? Colors.Orange.Darken3 : Colors.Red.Darken2);
        });
    }

    private static void InhaltZelle(IContainer c, BerichtStunde s)
    {
        c.Column(col =>
        {
            col.Spacing(2);
            if (!s.MitKlassenbuch)
            {
                col.Item().Text("kein Unterricht – kein Klassenbuch").Italic().FontColor(Colors.Grey.Darken1);
                return;
            }
            col.Item().Text(txt =>
            {
                txt.Span("Thema: ").SemiBold();
                if (string.IsNullOrWhiteSpace(s.Thema)) txt.Span("nicht eingetragen").Italic().FontColor(Colors.Grey.Darken1);
                else txt.Span(s.Thema);
            });
            if (!string.IsNullOrWhiteSpace(s.Hausaufgabe))
            {
                col.Item().Text(txt =>
                {
                    txt.Span(s.Faellig is DateOnly f ? $"Hausaufgabe bis {BerichtService.TagText(f)}: " : "Hausaufgabe: ").SemiBold();
                    txt.Span(s.Hausaufgabe);
                });
            }
            foreach (var e in s.Klasseneintraege)
            {
                col.Item().Text(txt =>
                {
                    txt.Span("Eintrag: ").SemiBold().FontColor(Colors.Orange.Darken3);
                    txt.Span(e);
                });
            }
        });
    }

    private static void AnwesenheitZelle(IContainer c, BerichtStunde s)
    {
        c.Column(col =>
        {
            col.Spacing(1);
            if (!s.MitKlassenbuch) return;
            if (!s.Erfasst)
            {
                col.Item().Text("Anwesenheit nicht erfasst").SemiBold().FontColor(Colors.Red.Darken2);
                return;
            }
            bool alle = s.Fehlt.Count + s.Verspaetet.Count + s.Beurlaubt.Count + s.Schulveranstaltung.Count == 0;
            if (alle) col.Item().Text("alle anwesend");
            Gruppe(col, "fehlt", s.Fehlt);
            Gruppe(col, "verspätet", s.Verspaetet);
            Gruppe(col, "beurlaubt", s.Beurlaubt);
            Gruppe(col, "schul. Veranst.", s.Schulveranstaltung);
            if (s.ErfasstText != null) col.Item().Text(s.ErfasstText).FontSize(Klein).FontColor(Colors.Grey.Darken1);
            if (s.GeaendertText != null) col.Item().Text(s.GeaendertText).FontSize(Klein).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void Gruppe(ColumnDescriptor col, string titel, IReadOnlyList<string> namen)
    {
        if (namen.Count == 0) return;
        col.Item().Text(txt =>
        {
            txt.Span($"{titel}: ").SemiBold();
            txt.Span(string.Join("; ", namen));
        });
    }

    // -----------------------------------------------------------------
    //  Anhang
    // -----------------------------------------------------------------

    private static void FehlzeitenTabelle(IContainer c, FzListeDto d)
    {
        c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.RelativeColumn(3.2f);       // Name
                cd.RelativeColumn(1);          // Fehlst.
                cd.RelativeColumn(1);          // entsch.
                cd.RelativeColumn(1);          // offen
                cd.RelativeColumn(1);          // unent.
                cd.RelativeColumn(1.1f);       // Quote
                cd.RelativeColumn(1.6f);       // Versp. (offen)
                cd.RelativeColumn(0.9f);       // Min.
            });
            table.Header(h =>
            {
                Kopfzelle(h.Cell(), "Name", false);
                foreach (var t in new[] { "Fehlst.", "entsch.", "offen", "unent.", "Quote", "Versp.", "Min." })
                    Kopfzelle(h.Cell(), t, true);
            });
            foreach (var z in d.Schueler)
            {
                Zelle(table.Cell()).Text(txt =>
                {
                    txt.Span($"{z.Nachname}, {z.Vorname}");
                    if (!string.IsNullOrWhiteSpace(z.Klasse) && d.Titel.StartsWith("Stufe", StringComparison.Ordinal) == false
                        && !d.Titel.EndsWith(z.Klasse, StringComparison.OrdinalIgnoreCase))
                        txt.Span($"  {z.Klasse}").FontSize(Klein).FontColor(Colors.Grey.Darken1);
                });
                Zahlen(table, z.Summe, z.Soll, false);
            }
            Zelle(table.Cell()).Text($"Gesamt ({d.Schueler.Count} Schüler)").SemiBold();
            Zahlen(table, d.Gesamt, d.Soll, true);
        });
    }

    private static void Zahlen(TableDescriptor table, FzSummeDto s, int soll, bool fett)
    {
        // art: 0 = normal, 1 = offen (orange), 2 = unentschuldigt (rot)
        void Z(string text, int art = 0)
        {
            var t = Zelle(table.Cell()).AlignRight().Text(text);
            if (fett) t.SemiBold();
            if (art == 1) t.FontColor(Colors.Orange.Darken3);
            if (art == 2) t.FontColor(Colors.Red.Darken2);
        }
        // Wie die Konferenzübersicht (4.1): entsch./offen/unent. = „davon" der Fehlstunden;
        // offene Verspätungen in Klammern bei „Versp.".
        Z(Zahl(s.Fehlstunden));
        Z(Zahl(s.Entschuldigt));
        Z(Zahl(s.Offen), s.Offen > 0 ? 1 : 0);
        Z(Zahl(s.Unentschuldigt), s.Unentschuldigt > 0 ? 2 : 0);
        Z(Prozent(s.Fehlstunden, soll));
        Z(s.VerspaetungenOffen > 0 ? $"{s.Verspaetungen} ({s.VerspaetungenOffen} offen)" : Zahl(s.Verspaetungen),
            s.VerspaetungenUnentschuldigt > 0 ? 2 : s.VerspaetungenOffen > 0 ? 1 : 0);
        Z(Zahl(s.VerspaetungMinuten));
    }

    private static string Zahl(int n) => n == 0 ? "–" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>„4,3 %" – mit Komma, unabhängig von der Spracheinstellung des Servers.</summary>
    private static string Prozent(int fehl, int soll)
        => soll <= 0 ? "–"
            : (Math.Round(1000.0 * fehl / soll) / 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                .Replace('.', ',') + " %";

    private static void Kopfzelle(IContainer c, string text, bool rechts)
    {
        var box = c.Background(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(4);
        if (rechts) box = box.AlignRight();
        box.Text(text).FontSize(8).SemiBold();
    }

    private static void SchuelerEintraegeTabelle(IContainer c, IReadOnlyList<BerichtSchuelerEintrag> liste)
    {
        c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(62);         // Datum, Stunde
                cd.RelativeColumn(2.4f);       // Schüler
                cd.RelativeColumn(1.8f);       // Kategorie
                cd.RelativeColumn(5);          // Text
                cd.RelativeColumn(1.2f);       // von
            });
            table.Header(h =>
            {
                Kopfzelle(h.Cell(), "Datum", false);
                Kopfzelle(h.Cell(), "Schüler", false);
                Kopfzelle(h.Cell(), "Kategorie", false);
                Kopfzelle(h.Cell(), "Text", false);
                Kopfzelle(h.Cell(), "von", false);
            });
            foreach (var e in liste)
            {
                Zelle(table.Cell()).Text(e.Stunde is int st ? $"{e.Datum:dd.MM.} · {st}." : $"{e.Datum:dd.MM.}");
                Zelle(table.Cell()).Text(e.Schueler);
                Zelle(table.Cell()).Text(e.Fach != null ? $"{e.Kategorie} ({e.Fach})" : e.Kategorie);
                Zelle(table.Cell()).Text(e.Text ?? "");
                Zelle(table.Cell()).Text(e.Von);
            }
        });
    }
}
