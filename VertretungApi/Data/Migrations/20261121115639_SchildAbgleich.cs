using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VertretungApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class SchildAbgleich : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KennungVorher",
                table: "SchildImportProtokolle",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NeuZugeordnet",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "NeuZuordnung",
                table: "SchildImportProtokolle",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SchluesselKennung",
                table: "SchildImportProtokolle",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Zusammengefuehrt",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Klassenleitung",
                table: "Lerngruppen",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Lehrkraefte",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kuerzel = table.Column<string>(type: "text", nullable: false),
                    Nachname = table.Column<string>(type: "text", nullable: false),
                    Vorname = table.Column<string>(type: "text", nullable: false),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false),
                    ImportAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lehrkraefte", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lehrkraefte_Kuerzel_IstPseudo",
                table: "Lehrkraefte",
                columns: new[] { "Kuerzel", "IstPseudo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Lehrkraefte");

            migrationBuilder.DropColumn(
                name: "KennungVorher",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "NeuZugeordnet",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "NeuZuordnung",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "SchluesselKennung",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "Zusammengefuehrt",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "Klassenleitung",
                table: "Lerngruppen");
        }
    }
}
