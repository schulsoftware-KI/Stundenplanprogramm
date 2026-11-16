using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VertretungApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class SchildImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Zugehoerigkeiten_SchuelerId_LerngruppeId_Schuljahr",
                table: "Zugehoerigkeiten");

            migrationBuilder.AddColumn<DateOnly>(
                name: "GueltigBis",
                table: "Zugehoerigkeiten",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "GueltigVon",
                table: "Zugehoerigkeiten",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Quelle",
                table: "Zugehoerigkeiten",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AbgangAm",
                table: "Schueler",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtIdNachnameGeb",
                table: "Schueler",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtIdSchild",
                table: "Schueler",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtIdVornameGeb",
                table: "Schueler",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ImportAm",
                table: "Schueler",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Schueler",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Quelle",
                table: "Lerngruppen",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SchildAb",
                table: "Lerngruppen",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SchildImportProtokolle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Zeit = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Akteur = table.Column<string>(type: "text", nullable: false),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    Abschnitt = table.Column<int>(type: "integer", nullable: false),
                    GiltAb = table.Column<DateOnly>(type: "date", nullable: false),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false),
                    Neu = table.Column<int>(type: "integer", nullable: false),
                    Geaendert = table.Column<int>(type: "integer", nullable: false),
                    Klassenwechsel = table.Column<int>(type: "integer", nullable: false),
                    Kurswechsel = table.Column<int>(type: "integer", nullable: false),
                    Abgaenge = table.Column<int>(type: "integer", nullable: false),
                    Unveraendert = table.Column<int>(type: "integer", nullable: false),
                    Uebersprungen = table.Column<int>(type: "integer", nullable: false),
                    StandPruefsumme = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchildImportProtokolle", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Zugehoerigkeiten_SchuelerId_LerngruppeId_Schuljahr_GueltigV~",
                table: "Zugehoerigkeiten",
                columns: new[] { "SchuelerId", "LerngruppeId", "Schuljahr", "GueltigVon" });

            migrationBuilder.CreateIndex(
                name: "IX_Schueler_ExtId",
                table: "Schueler",
                column: "ExtId");

            migrationBuilder.CreateIndex(
                name: "IX_Schueler_ExtIdSchild",
                table: "Schueler",
                column: "ExtIdSchild");

            migrationBuilder.CreateIndex(
                name: "IX_SchildImportProtokolle_Zeit",
                table: "SchildImportProtokolle",
                column: "Zeit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchildImportProtokolle");

            migrationBuilder.DropIndex(
                name: "IX_Zugehoerigkeiten_SchuelerId_LerngruppeId_Schuljahr_GueltigV~",
                table: "Zugehoerigkeiten");

            migrationBuilder.DropIndex(
                name: "IX_Schueler_ExtId",
                table: "Schueler");

            migrationBuilder.DropIndex(
                name: "IX_Schueler_ExtIdSchild",
                table: "Schueler");

            migrationBuilder.DropColumn(
                name: "GueltigBis",
                table: "Zugehoerigkeiten");

            migrationBuilder.DropColumn(
                name: "GueltigVon",
                table: "Zugehoerigkeiten");

            migrationBuilder.DropColumn(
                name: "Quelle",
                table: "Zugehoerigkeiten");

            migrationBuilder.DropColumn(
                name: "AbgangAm",
                table: "Schueler");

            migrationBuilder.DropColumn(
                name: "ExtIdNachnameGeb",
                table: "Schueler");

            migrationBuilder.DropColumn(
                name: "ExtIdSchild",
                table: "Schueler");

            migrationBuilder.DropColumn(
                name: "ExtIdVornameGeb",
                table: "Schueler");

            migrationBuilder.DropColumn(
                name: "ImportAm",
                table: "Schueler");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Schueler");

            migrationBuilder.DropColumn(
                name: "Quelle",
                table: "Lerngruppen");

            migrationBuilder.DropColumn(
                name: "SchildAb",
                table: "Lerngruppen");

            migrationBuilder.CreateIndex(
                name: "IX_Zugehoerigkeiten_SchuelerId_LerngruppeId_Schuljahr",
                table: "Zugehoerigkeiten",
                columns: new[] { "SchuelerId", "LerngruppeId", "Schuljahr" },
                unique: true);
        }
    }
}
