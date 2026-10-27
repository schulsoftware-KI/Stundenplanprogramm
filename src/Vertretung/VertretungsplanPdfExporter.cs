#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Stundenplan_V2.Vertretung
{
    // =====================================================================
    //  VertretungsplanPdfExporter – Tagesvertretungsplan als PDF (QuestPDF).
    //
    //  Zwei Sichten:
    //   - Lehrer-Sicht (mitGrund = true): enthält "entfällt bei" und den Grund.
    //   - Aushang-/Schüler-Sicht (mitGrund = false): OHNE Grund (Datenschutz).
    //
    //  QuestPDF ist über die Community-Lizenz für Schulen kostenlos; sie wird im
    //  statischen Konstruktor gesetzt, damit am Programmstart nichts nötig ist.
    // =====================================================================

    public static class VertretungsplanPdfExporter
    {
        static VertretungsplanPdfExporter()
        {
            // Für kleine Organisationen/Schulen kostenlos.
            QuestPDF.Settings.License = LicenseType.Community;
        }

        /// <summary>
        /// Schreibt den Tagesvertretungsplan für <paramref name="tag"/> nach
        /// <paramref name="zielPfad"/>. <paramref name="mitGrund"/> = true liefert
        /// die Lehrer-Sicht (mit Grund), false die Aushang-Sicht (ohne Grund).
        /// </summary>
        public static void Erzeuge(
            IEnumerable<VertretungsEintrag> eintraege, DateTime tag, bool mitGrund,
            string zielPfad, string schuljahr = "")
        {
            var tages = eintraege
                .Where(v => v.Datum.Date == tag.Date)
                .OrderBy(v => v.Stunde)
                .ThenBy(v => string.Join(",", v.Klassen))
                .ToList();

            string datumLang = tag.ToString("dddd, d. MMMM yyyy", CultureInfo.GetCultureInfo("de-DE"));
            string sicht = mitGrund ? "Lehrer-Sicht" : "Aushang";

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.4f, Unit.Centimetre);
                    page.DefaultTextStyle(t => t.FontSize(10));

                    // Kopf
                    page.Header().Column(col =>
                    {
                        col.Item().Text("Vertretungsplan").FontSize(18).SemiBold();
                        col.Item().Text(datumLang).FontSize(12);
                        col.Item().Text(txt =>
                        {
                            txt.DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken1));
                            txt.Span(sicht);
                            if (!string.IsNullOrWhiteSpace(schuljahr)) txt.Span($"   ·   Schuljahr {schuljahr}");
                            txt.Span($"   ·   Stand {DateTime.Now:dd.MM.yyyy HH:mm}");
                        });
                        col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                    });

                    // Inhalt
                    page.Content().PaddingTop(8).Element(c => Tabelle(c, tages, mitGrund));

                    // Fuß
                    page.Footer().AlignRight().Text(txt =>
                    {
                        txt.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1));
                        txt.Span("Seite ");
                        txt.CurrentPageNumber();
                        txt.Span(" / ");
                        txt.TotalPages();
                    });
                });
            }).GeneratePdf(zielPfad);
        }

        private static void Tabelle(IContainer container, List<VertretungsEintrag> tages, bool mitGrund)
        {
            if (tages.Count == 0)
            {
                container.Text("Für diesen Tag sind keine Vertretungen/Entfälle eingetragen.")
                         .Italic().FontColor(Colors.Grey.Darken1);
                return;
            }

            container.Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(34);   // Std
                    c.RelativeColumn(1.4f); // Klasse(n)
                    c.RelativeColumn(1.1f); // Fach
                    if (mitGrund) c.RelativeColumn(1.1f); // entfällt bei
                    c.RelativeColumn(1.3f); // Vertretung
                    c.RelativeColumn(1.4f); // Art
                    if (mitGrund) c.RelativeColumn(1.3f); // Grund
                    c.RelativeColumn(1.6f); // Bemerkung
                });

                table.Header(h =>
                {
                    HeaderZelle(h.Cell(), "Std");
                    HeaderZelle(h.Cell(), "Klasse(n)");
                    HeaderZelle(h.Cell(), "Fach");
                    if (mitGrund) HeaderZelle(h.Cell(), "entfällt bei");
                    HeaderZelle(h.Cell(), "Vertretung");
                    HeaderZelle(h.Cell(), "Art");
                    if (mitGrund) HeaderZelle(h.Cell(), "Grund");
                    HeaderZelle(h.Cell(), "Bemerkung");
                });

                foreach (var v in tages)
                {
                    bool entfall = v.Art == Vertretungsart.Entfall;
                    string vertretung = entfall ? "entfällt"
                        : v.Art == Vertretungsart.Verlegung ? "verlegt"
                        : (v.VertretungsLehrer ?? "");

                    Zelle(table.Cell(), v.Stunde.ToString(), entfall);
                    Zelle(table.Cell(), string.Join(", ", v.Klassen), entfall);
                    Zelle(table.Cell(), v.Fach, entfall);
                    if (mitGrund) Zelle(table.Cell(), v.OriginalLehrer, entfall);
                    Zelle(table.Cell(), vertretung, entfall);
                    Zelle(table.Cell(), ArtText(v.Art), entfall);
                    if (mitGrund) Zelle(table.Cell(), GrundText(v.Grund), entfall);
                    Zelle(table.Cell(), v.Bemerkung, entfall);
                }
            });
        }

        private static void HeaderZelle(IContainer c, string text)
            => c.Background(Colors.Grey.Lighten2).Border(0.5f).BorderColor(Colors.Grey.Medium)
                .PaddingVertical(4).PaddingHorizontal(5).Text(text).SemiBold();

        private static void Zelle(IContainer c, string text, bool entfall)
        {
            var box = c.Border(0.5f).BorderColor(Colors.Grey.Lighten1)
                       .PaddingVertical(3).PaddingHorizontal(5);
            var t = box.Text(text ?? "");
            if (entfall) t.FontColor(Colors.Red.Darken1);
        }

        // ---- deutsche Beschriftungen ----

        public static string ArtText(Vertretungsart a) => a switch
        {
            Vertretungsart.Vertretung => "Vertretung",
            Vertretungsart.BetreuungAufsicht => "Betreuung/Aufsicht",
            Vertretungsart.EVA => "Eigenverantw. Arbeiten",
            Vertretungsart.Raumvertretung => "Raumvertretung",
            Vertretungsart.Verlegung => "Verlegung",
            Vertretungsart.Entfall => "Entfall",
            Vertretungsart.Zusammenlegung => "Zusammenlegung",
            _ => a.ToString()
        };

        public static string GrundText(VertretungsGrund g) => g switch
        {
            VertretungsGrund.Krankheit => "Krankheit",
            VertretungsGrund.Fortbildung => "Fortbildung",
            VertretungsGrund.Klassenfahrt => "Klassenfahrt",
            VertretungsGrund.Exkursion => "Exkursion",
            VertretungsGrund.Pruefung => "Prüfung",
            VertretungsGrund.Dienstbefreiung => "Dienstbefreiung",
            VertretungsGrund.Sonstiges => "Sonstiges",
            _ => g.ToString()
        };
    }
}
