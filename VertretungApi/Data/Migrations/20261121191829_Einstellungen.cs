using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VertretungApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Einstellungen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KlassenleitungGeaendert",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "KlassenleitungKlassen",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KlassenleitungNeu",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Lehrkraefte",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LehrkraefteEntfernt",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LehrkraefteGeaendert",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LehrkraefteNeu",
                table: "SchildImportProtokolle",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Einstellungen",
                columns: table => new
                {
                    Schluessel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Wert = table.Column<string>(type: "text", nullable: true),
                    GeaendertAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GeaendertVon = table.Column<string>(type: "text", nullable: true),
                    AbgestimmtAm = table.Column<DateOnly>(type: "date", nullable: true),
                    AbgestimmtMit = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Einstellungen", x => x.Schluessel);
                });

            migrationBuilder.CreateTable(
                name: "EinstellungsProtokolle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Zeit = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Akteur = table.Column<string>(type: "text", nullable: false),
                    Schluessel = table.Column<string>(type: "text", nullable: false),
                    Alt = table.Column<string>(type: "text", nullable: true),
                    Neu = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EinstellungsProtokolle", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EinstellungsProtokolle_Zeit",
                table: "EinstellungsProtokolle",
                column: "Zeit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Einstellungen");

            migrationBuilder.DropTable(
                name: "EinstellungsProtokolle");

            migrationBuilder.DropColumn(
                name: "KlassenleitungGeaendert",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "KlassenleitungKlassen",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "KlassenleitungNeu",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "Lehrkraefte",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "LehrkraefteEntfernt",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "LehrkraefteGeaendert",
                table: "SchildImportProtokolle");

            migrationBuilder.DropColumn(
                name: "LehrkraefteNeu",
                table: "SchildImportProtokolle");
        }
    }
}
