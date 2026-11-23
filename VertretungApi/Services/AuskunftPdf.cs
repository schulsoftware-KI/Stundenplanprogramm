using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace VertretungApi.Services;

// =====================================================================
//  P6b (Entscheidungen 234, 235): Auskunft nach Art. 15 DSGVO als PDF (QuestPDF, wie Services/KlassenbuchPdf.cs).
//
//  A4 hoch. Kopf auf jeder Seite: Schule, „Auskunft nach Art. 15 DSGVO“, Person bzw. Konto, rechts „erstellt am … von …“;
//  bei Pseudodaten ein Band „Pseudodaten“. Inhalt: die Abschnitte aus Services/Auskunft.cs (Absätze, Begriff–Wert,
//  Tabellen mit Kopfzeile, die sich auf Folgeseiten wiederholt). Fuß: „Enthält personenbezogene Daten“ und Seitenzahl.
// =====================================================================

public static class AuskunftPdf
{
    static AuskunftPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    private const float Klein = 7.5f;

    public static byte[] Erzeuge(AuskunftDokument d)
        => Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.4f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(8.5f));

                page.Header().Element(c => Kopf(c, d));
                page.Content().PaddingTop(8).Element(c => Inhalt(c, d));
                page.Footer().Element(c => Fuss(c, d));
            });
        }).GeneratePdf();

    private static void Kopf(IContainer c, AuskunftDokument d)
    {
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(links =>
                {
                    if (!string.IsNullOrWhiteSpace(d.Schule))
                        links.Item().Text(d.Schule).FontSize(8).FontColor(Colors.Grey.Darken1);
                    links.Item().Text(d.Titel).FontSize(14).SemiBold();
                    links.Item().Text(d.Untertitel).FontSize(10);
                });
                row.ConstantItem(170).AlignRight().Column(rechts =>
                {
                    rechts.Item().AlignRight().Text($"erstellt am {d.ErstelltAm:dd.MM.yyyy HH:mm}").FontSize(8).FontColor(Colors.Grey.Darken1);
                    rechts.Item().AlignRight().Text($"von {d.ErstelltVon}").FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });
            if (d.IstPseudo)
                col.Item().PaddingTop(3).Background(Colors.Grey.Lighten3).PaddingVertical(2).PaddingHorizontal(5)
                    .Text("Pseudodaten – erfundene Testdaten, keine echte Person").FontSize(8).SemiBold().FontColor(Colors.Red.Darken2);
            col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Grey.Medium);
        });
    }

    private static void Fuss(IContainer c, AuskunftDokument d)
    {
        c.Column(col =>
        {
            col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            col.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text(txt =>
                {
                    txt.DefaultTextStyle(x => x.FontSize(Klein).FontColor(Colors.Grey.Darken1));
                    txt.Span("Enthält personenbezogene Daten – nur an die berechtigte Person aushändigen.").SemiBold();
                    txt.Span($"  {d.Titel} · {d.Untertitel}");
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

    private static void Inhalt(IContainer c, AuskunftDokument d)
    {
        c.Column(col =>
        {
            col.Spacing(6);
            foreach (var a in d.Abschnitte)
            {
                col.Item().PaddingTop(6).Text(a.Titel).FontSize(11.5f).SemiBold();
                foreach (var t in a.Teile)
                    col.Item().Element(x => Teil(x, t));
            }
        });
    }

    private static void Teil(IContainer c, AuskunftTeil t)
    {
        switch (t)
        {
            case AuskunftAbsatz p:
                if (p.Leise) c.Text(p.Text).FontSize(Klein).FontColor(Colors.Grey.Darken1);
                else c.Text(p.Text);
                break;
            case AuskunftZwischen z:
                c.PaddingTop(3).Text(z.Titel).FontSize(9.5f).SemiBold();
                break;
            case AuskunftPaare paare:
                Paare(c, paare);
                break;
            case AuskunftTabelle tab:
                Tabelle(c, tab);
                break;
        }
    }

    private static void Paare(IContainer c, AuskunftPaare p)
    {
        c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(120);
                cd.RelativeColumn();
            });
            foreach (var z in p.Zeilen)
            {
                Zelle(table.Cell()).Text(z.Key).SemiBold();
                Zelle(table.Cell()).Text(z.Value);
            }
        });
    }

    private static void Tabelle(IContainer c, AuskunftTabelle t)
    {
        if (t.Zeilen.Count == 0)
        {
            c.Text(t.Leer).Italic().FontColor(Colors.Grey.Darken1);
            return;
        }
        c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                for (int i = 0; i < t.Spalten.Count; i++)
                    cd.RelativeColumn(i < t.Breiten.Count ? t.Breiten[i] : 1f);
            });
            table.Header(h =>
            {
                foreach (var s in t.Spalten) Kopfzelle(h.Cell(), s);
            });
            foreach (var z in t.Zeilen)
                for (int i = 0; i < t.Spalten.Count; i++)
                    Zelle(table.Cell()).Text(i < z.Count ? z[i] : "");
        });
    }

    private static IContainer Zelle(IContainer c)
        => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(2.5f).PaddingHorizontal(3);

    private static void Kopfzelle(IContainer c, string text)
        => c.Background(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(3).Text(text).FontSize(8).SemiBold();
}
