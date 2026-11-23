using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VertretungApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Datenschutz : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ZweiterFaktorCodes",
                table: "Benutzer",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZweiterFaktorGeheimnis",
                table: "Benutzer",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ZweiterFaktorSchritt",
                table: "Benutzer",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ZweiterFaktorSeit",
                table: "Benutzer",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AnmeldeProtokolle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Zeit = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    KontoId = table.Column<int>(type: "integer", nullable: true),
                    Konto = table.Column<string>(type: "text", nullable: false),
                    Rolle = table.Column<string>(type: "text", nullable: true),
                    Ergebnis = table.Column<string>(type: "text", nullable: false),
                    Erfolgreich = table.Column<bool>(type: "boolean", nullable: false),
                    Adresse = table.Column<string>(type: "text", nullable: true),
                    Schulnetz = table.Column<bool>(type: "boolean", nullable: true),
                    Geraet = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnmeldeProtokolle", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeseProtokolle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Tag = table.Column<DateOnly>(type: "date", nullable: false),
                    ErsteZeit = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LetzteZeit = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Anzahl = table.Column<int>(type: "integer", nullable: false),
                    KontoId = table.Column<int>(type: "integer", nullable: true),
                    Akteur = table.Column<string>(type: "text", nullable: false),
                    Rolle = table.Column<string>(type: "text", nullable: false),
                    Ansicht = table.Column<string>(type: "text", nullable: false),
                    SchuelerId = table.Column<int>(type: "integer", nullable: true),
                    LerngruppeId = table.Column<int>(type: "integer", nullable: true),
                    Gruppe = table.Column<string>(type: "text", nullable: true),
                    Gegenstand = table.Column<string>(type: "text", nullable: false),
                    IstPseudo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeseProtokolle", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnmeldeProtokolle_KontoId",
                table: "AnmeldeProtokolle",
                column: "KontoId");

            migrationBuilder.CreateIndex(
                name: "IX_AnmeldeProtokolle_Zeit",
                table: "AnmeldeProtokolle",
                column: "Zeit");

            migrationBuilder.CreateIndex(
                name: "IX_LeseProtokolle_KontoId",
                table: "LeseProtokolle",
                column: "KontoId");

            migrationBuilder.CreateIndex(
                name: "IX_LeseProtokolle_SchuelerId",
                table: "LeseProtokolle",
                column: "SchuelerId");

            migrationBuilder.CreateIndex(
                name: "IX_LeseProtokolle_Tag_KontoId_Ansicht_SchuelerId_Gegenstand",
                table: "LeseProtokolle",
                columns: new[] { "Tag", "KontoId", "Ansicht", "SchuelerId", "Gegenstand" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnmeldeProtokolle");

            migrationBuilder.DropTable(
                name: "LeseProtokolle");

            migrationBuilder.DropColumn(
                name: "ZweiterFaktorCodes",
                table: "Benutzer");

            migrationBuilder.DropColumn(
                name: "ZweiterFaktorGeheimnis",
                table: "Benutzer");

            migrationBuilder.DropColumn(
                name: "ZweiterFaktorSchritt",
                table: "Benutzer");

            migrationBuilder.DropColumn(
                name: "ZweiterFaktorSeit",
                table: "Benutzer");
        }
    }
}
