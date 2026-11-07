using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VertretungApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Grundlinie : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Benutzer",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Benutzername = table.Column<string>(type: "text", nullable: false),
                    PasswortHash = table.Column<string>(type: "text", nullable: false),
                    Rolle = table.Column<int>(type: "integer", nullable: false),
                    LehrerKuerzel = table.Column<string>(type: "text", nullable: true),
                    SchuelerId = table.Column<int>(type: "integer", nullable: true),
                    SicherheitsStempel = table.Column<string>(type: "text", nullable: false),
                    Zustaendig = table.Column<string>(type: "text", nullable: true),
                    MussPasswortAendern = table.Column<bool>(type: "boolean", nullable: false),
                    Gesperrt = table.Column<bool>(type: "boolean", nullable: false),
                    AngelegtAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LetzteAnmeldung = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PasswortGeaendertAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Benutzer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DownloadProtokolle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Zeit = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Akteur = table.Column<string>(type: "text", nullable: false),
                    Art = table.Column<string>(type: "text", nullable: false),
                    Was = table.Column<string>(type: "text", nullable: false),
                    Von = table.Column<DateOnly>(type: "date", nullable: false),
                    Bis = table.Column<DateOnly>(type: "date", nullable: false),
                    MitSchuelerEintraegen = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadProtokolle", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GrundplanVersionen",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    GueltigAb = table.Column<DateOnly>(type: "date", nullable: false),
                    Bezeichnung = table.Column<string>(type: "text", nullable: false),
                    Erstellt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrundplanVersionen", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Kalender",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: false),
                    Ende = table.Column<DateOnly>(type: "date", nullable: false),
                    RasterTage = table.Column<string>(type: "text", nullable: false),
                    Aktualisiert = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Kalender", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KlassenbuchLektionen",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Datum = table.Column<DateOnly>(type: "date", nullable: false),
                    Stunde = table.Column<int>(type: "integer", nullable: false),
                    GrundUNr = table.Column<int>(type: "integer", nullable: false),
                    GrundLehrer = table.Column<string>(type: "text", nullable: false),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    UNr = table.Column<int>(type: "integer", nullable: false),
                    Fach = table.Column<string>(type: "text", nullable: false),
                    Lehrer = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Lerngruppen = table.Column<string>(type: "text", nullable: false),
                    Klassen = table.Column<string>(type: "text", nullable: false),
                    ErfasstVon = table.Column<string>(type: "text", nullable: true),
                    ErfasstAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GeaendertVon = table.Column<string>(type: "text", nullable: true),
                    GeaendertAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false),
                    Thema = table.Column<string>(type: "text", nullable: true),
                    Hausaufgabe = table.Column<string>(type: "text", nullable: true),
                    HausaufgabeFaellig = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KlassenbuchLektionen", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KlassenbuchProtokolle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Zeit = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Akteur = table.Column<string>(type: "text", nullable: false),
                    Art = table.Column<string>(type: "text", nullable: false),
                    LektionId = table.Column<int>(type: "integer", nullable: true),
                    Gruppe = table.Column<string>(type: "text", nullable: true),
                    SchuelerId = table.Column<int>(type: "integer", nullable: true),
                    Alt = table.Column<string>(type: "text", nullable: true),
                    Neu = table.Column<string>(type: "text", nullable: true),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false),
                    EintragId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KlassenbuchProtokolle", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KontoProtokolle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Zeit = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Akteur = table.Column<string>(type: "text", nullable: false),
                    Aktion = table.Column<string>(type: "text", nullable: false),
                    KontoId = table.Column<int>(type: "integer", nullable: true),
                    Konto = table.Column<string>(type: "text", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KontoProtokolle", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lerngruppen",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Typ = table.Column<int>(type: "integer", nullable: false),
                    Bezeichnung = table.Column<string>(type: "text", nullable: false),
                    Stufe = table.Column<string>(type: "text", nullable: false),
                    Fach = table.Column<string>(type: "text", nullable: true),
                    Kursart = table.Column<int>(type: "integer", nullable: false),
                    LehrerKuerzel = table.Column<string>(type: "text", nullable: true),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false),
                    BearbeitetVon = table.Column<string>(type: "text", nullable: true),
                    BearbeitetAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lerngruppen", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Schueler",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Nachname = table.Column<string>(type: "text", nullable: false),
                    Vorname = table.Column<string>(type: "text", nullable: false),
                    Stufe = table.Column<string>(type: "text", nullable: false),
                    ExtId = table.Column<string>(type: "text", nullable: true),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schueler", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tage",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Datum = table.Column<DateOnly>(type: "date", nullable: false),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    Aktualisiert = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UnterrichtEinstellungen",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    UNr = table.Column<int>(type: "integer", nullable: false),
                    Lehrer = table.Column<string>(type: "text", nullable: false),
                    HausaufgabenSichtbar = table.Column<bool>(type: "boolean", nullable: false),
                    GeaendertVon = table.Column<string>(type: "text", nullable: true),
                    GeaendertAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnterrichtEinstellungen", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GrundplanSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VersionId = table.Column<int>(type: "integer", nullable: false),
                    WTag = table.Column<string>(type: "text", nullable: false),
                    WTagIndex = table.Column<int>(type: "integer", nullable: false),
                    Stunde = table.Column<int>(type: "integer", nullable: false),
                    UNr = table.Column<int>(type: "integer", nullable: false),
                    Lehrer = table.Column<string>(type: "text", nullable: false),
                    Fach = table.Column<string>(type: "text", nullable: false),
                    Fachraum = table.Column<string>(type: "text", nullable: true),
                    Stammraum = table.Column<string>(type: "text", nullable: true),
                    LerngruppeTyp = table.Column<string>(type: "text", nullable: false),
                    LerngruppeKey = table.Column<string>(type: "text", nullable: false),
                    Klasse = table.Column<string>(type: "text", nullable: false),
                    Schuelergruppe = table.Column<string>(type: "text", nullable: true),
                    Art = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrundplanSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrundplanSlots_GrundplanVersionen_VersionId",
                        column: x => x.VersionId,
                        principalTable: "GrundplanVersionen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FreieZeiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    KalenderId = table.Column<int>(type: "integer", nullable: false),
                    Typ = table.Column<string>(type: "text", nullable: false),
                    Von = table.Column<DateOnly>(type: "date", nullable: false),
                    Bis = table.Column<DateOnly>(type: "date", nullable: false),
                    Bezeichnung = table.Column<string>(type: "text", nullable: false),
                    Klassen = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FreieZeiten", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FreieZeiten_Kalender_KalenderId",
                        column: x => x.KalenderId,
                        principalTable: "Kalender",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Entschuldigungen",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SchuelerId = table.Column<int>(type: "integer", nullable: false),
                    Von = table.Column<DateOnly>(type: "date", nullable: false),
                    Bis = table.Column<DateOnly>(type: "date", nullable: false),
                    Grund = table.Column<int>(type: "integer", nullable: false),
                    ErfasstVon = table.Column<string>(type: "text", nullable: false),
                    ErfasstAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entschuldigungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Entschuldigungen_Schueler_SchuelerId",
                        column: x => x.SchuelerId,
                        principalTable: "Schueler",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KlassenbuchEintraege",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Art = table.Column<int>(type: "integer", nullable: false),
                    Kategorie = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: true),
                    SchuelerId = table.Column<int>(type: "integer", nullable: true),
                    Datum = table.Column<DateOnly>(type: "date", nullable: false),
                    Stunde = table.Column<int>(type: "integer", nullable: true),
                    GrundUNr = table.Column<int>(type: "integer", nullable: true),
                    GrundLehrer = table.Column<string>(type: "text", nullable: true),
                    UNr = table.Column<int>(type: "integer", nullable: true),
                    Lehrer = table.Column<string>(type: "text", nullable: true),
                    Fach = table.Column<string>(type: "text", nullable: false),
                    Klassen = table.Column<string>(type: "text", nullable: false),
                    Lerngruppen = table.Column<string>(type: "text", nullable: false),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    VerfasstVon = table.Column<string>(type: "text", nullable: false),
                    VerfasstAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GeaendertVon = table.Column<string>(type: "text", nullable: true),
                    GeaendertAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KlassenbuchEintraege", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KlassenbuchEintraege_Schueler_SchuelerId",
                        column: x => x.SchuelerId,
                        principalTable: "Schueler",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Zugehoerigkeiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SchuelerId = table.Column<int>(type: "integer", nullable: false),
                    LerngruppeId = table.Column<int>(type: "integer", nullable: false),
                    Schuljahr = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Zugehoerigkeiten", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Zugehoerigkeiten_Lerngruppen_LerngruppeId",
                        column: x => x.LerngruppeId,
                        principalTable: "Lerngruppen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Zugehoerigkeiten_Schueler_SchuelerId",
                        column: x => x.SchuelerId,
                        principalTable: "Schueler",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Eintraege",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Stunde = table.Column<int>(type: "integer", nullable: false),
                    Klassen = table.Column<string>(type: "text", nullable: false),
                    Fach = table.Column<string>(type: "text", nullable: false),
                    Lehrer = table.Column<string>(type: "text", nullable: true),
                    OriginalLehrer = table.Column<string>(type: "text", nullable: true),
                    Raum = table.Column<string>(type: "text", nullable: true),
                    Art = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Grund = table.Column<string>(type: "text", nullable: true),
                    Bemerkung = table.Column<string>(type: "text", nullable: true),
                    Fachgerecht = table.Column<bool>(type: "boolean", nullable: true),
                    IstAenderung = table.Column<bool>(type: "boolean", nullable: false),
                    UNr = table.Column<int>(type: "integer", nullable: true),
                    GrundUNr = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Eintraege", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Eintraege_Tage_TagId",
                        column: x => x.TagId,
                        principalTable: "Tage",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Anwesenheiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LektionId = table.Column<int>(type: "integer", nullable: false),
                    SchuelerId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Minuten = table.Column<int>(type: "integer", nullable: true),
                    GeaendertVon = table.Column<string>(type: "text", nullable: true),
                    GeaendertAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Bewertung = table.Column<int>(type: "integer", nullable: false),
                    Grund = table.Column<int>(type: "integer", nullable: true),
                    EntschuldigungId = table.Column<int>(type: "integer", nullable: true),
                    BewertetVon = table.Column<string>(type: "text", nullable: true),
                    BewertetAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Festgelegt = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Anwesenheiten", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Anwesenheiten_Entschuldigungen_EntschuldigungId",
                        column: x => x.EntschuldigungId,
                        principalTable: "Entschuldigungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Anwesenheiten_KlassenbuchLektionen_LektionId",
                        column: x => x.LektionId,
                        principalTable: "KlassenbuchLektionen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Anwesenheiten_Schueler_SchuelerId",
                        column: x => x.SchuelerId,
                        principalTable: "Schueler",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Anwesenheiten_EntschuldigungId",
                table: "Anwesenheiten",
                column: "EntschuldigungId");

            migrationBuilder.CreateIndex(
                name: "IX_Anwesenheiten_LektionId_SchuelerId",
                table: "Anwesenheiten",
                columns: new[] { "LektionId", "SchuelerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Anwesenheiten_SchuelerId",
                table: "Anwesenheiten",
                column: "SchuelerId");

            migrationBuilder.CreateIndex(
                name: "IX_Benutzer_Benutzername",
                table: "Benutzer",
                column: "Benutzername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadProtokolle_Zeit",
                table: "DownloadProtokolle",
                column: "Zeit");

            migrationBuilder.CreateIndex(
                name: "IX_Eintraege_TagId",
                table: "Eintraege",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Entschuldigungen_SchuelerId_Von",
                table: "Entschuldigungen",
                columns: new[] { "SchuelerId", "Von" });

            migrationBuilder.CreateIndex(
                name: "IX_FreieZeiten_KalenderId",
                table: "FreieZeiten",
                column: "KalenderId");

            migrationBuilder.CreateIndex(
                name: "IX_GrundplanSlots_VersionId_Lehrer",
                table: "GrundplanSlots",
                columns: new[] { "VersionId", "Lehrer" });

            migrationBuilder.CreateIndex(
                name: "IX_GrundplanSlots_VersionId_LerngruppeKey",
                table: "GrundplanSlots",
                columns: new[] { "VersionId", "LerngruppeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_GrundplanSlots_VersionId_WTagIndex_Stunde",
                table: "GrundplanSlots",
                columns: new[] { "VersionId", "WTagIndex", "Stunde" });

            migrationBuilder.CreateIndex(
                name: "IX_GrundplanVersionen_Schuljahr_GueltigAb",
                table: "GrundplanVersionen",
                columns: new[] { "Schuljahr", "GueltigAb" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Kalender_Schuljahr",
                table: "Kalender",
                column: "Schuljahr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KlassenbuchEintraege_Datum_Stunde_GrundUNr_GrundLehrer",
                table: "KlassenbuchEintraege",
                columns: new[] { "Datum", "Stunde", "GrundUNr", "GrundLehrer" });

            migrationBuilder.CreateIndex(
                name: "IX_KlassenbuchEintraege_SchuelerId",
                table: "KlassenbuchEintraege",
                column: "SchuelerId");

            migrationBuilder.CreateIndex(
                name: "IX_KlassenbuchEintraege_UNr_GrundLehrer",
                table: "KlassenbuchEintraege",
                columns: new[] { "UNr", "GrundLehrer" });

            migrationBuilder.CreateIndex(
                name: "IX_KlassenbuchLektionen_Datum_Stunde_GrundUNr_GrundLehrer",
                table: "KlassenbuchLektionen",
                columns: new[] { "Datum", "Stunde", "GrundUNr", "GrundLehrer" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KlassenbuchProtokolle_EintragId",
                table: "KlassenbuchProtokolle",
                column: "EintragId");

            migrationBuilder.CreateIndex(
                name: "IX_KlassenbuchProtokolle_Gruppe",
                table: "KlassenbuchProtokolle",
                column: "Gruppe");

            migrationBuilder.CreateIndex(
                name: "IX_KlassenbuchProtokolle_LektionId",
                table: "KlassenbuchProtokolle",
                column: "LektionId");

            migrationBuilder.CreateIndex(
                name: "IX_KontoProtokolle_KontoId",
                table: "KontoProtokolle",
                column: "KontoId");

            migrationBuilder.CreateIndex(
                name: "IX_KontoProtokolle_Zeit",
                table: "KontoProtokolle",
                column: "Zeit");

            migrationBuilder.CreateIndex(
                name: "IX_Lerngruppen_Schuljahr_Bezeichnung",
                table: "Lerngruppen",
                columns: new[] { "Schuljahr", "Bezeichnung" });

            migrationBuilder.CreateIndex(
                name: "IX_Tage_Schuljahr_Datum",
                table: "Tage",
                columns: new[] { "Schuljahr", "Datum" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnterrichtEinstellungen_Schuljahr_UNr_Lehrer",
                table: "UnterrichtEinstellungen",
                columns: new[] { "Schuljahr", "UNr", "Lehrer" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Zugehoerigkeiten_LerngruppeId",
                table: "Zugehoerigkeiten",
                column: "LerngruppeId");

            migrationBuilder.CreateIndex(
                name: "IX_Zugehoerigkeiten_SchuelerId_LerngruppeId_Schuljahr",
                table: "Zugehoerigkeiten",
                columns: new[] { "SchuelerId", "LerngruppeId", "Schuljahr" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Anwesenheiten");

            migrationBuilder.DropTable(
                name: "Benutzer");

            migrationBuilder.DropTable(
                name: "DownloadProtokolle");

            migrationBuilder.DropTable(
                name: "Eintraege");

            migrationBuilder.DropTable(
                name: "FreieZeiten");

            migrationBuilder.DropTable(
                name: "GrundplanSlots");

            migrationBuilder.DropTable(
                name: "KlassenbuchEintraege");

            migrationBuilder.DropTable(
                name: "KlassenbuchProtokolle");

            migrationBuilder.DropTable(
                name: "KontoProtokolle");

            migrationBuilder.DropTable(
                name: "UnterrichtEinstellungen");

            migrationBuilder.DropTable(
                name: "Zugehoerigkeiten");

            migrationBuilder.DropTable(
                name: "Entschuldigungen");

            migrationBuilder.DropTable(
                name: "KlassenbuchLektionen");

            migrationBuilder.DropTable(
                name: "Tage");

            migrationBuilder.DropTable(
                name: "Kalender");

            migrationBuilder.DropTable(
                name: "GrundplanVersionen");

            migrationBuilder.DropTable(
                name: "Lerngruppen");

            migrationBuilder.DropTable(
                name: "Schueler");
        }
    }
}
