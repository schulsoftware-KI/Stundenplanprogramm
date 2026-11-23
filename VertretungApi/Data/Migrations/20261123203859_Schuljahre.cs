using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VertretungApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Schuljahre : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ZustaendigSchuljahr",
                table: "Benutzer",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Schuljahre",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Schuljahr = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AbgeschlossenAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AbgeschlossenVon = table.Column<string>(type: "text", nullable: true),
                    GeoeffnetAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GeoeffnetVon = table.Column<string>(type: "text", nullable: true),
                    GeoeffnetGrund = table.Column<string>(type: "text", nullable: true),
                    VorbereitetAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VorbereitetVon = table.Column<string>(type: "text", nullable: true),
                    ErfassungAb = table.Column<DateOnly>(type: "date", nullable: true),
                    ThemaAb = table.Column<DateOnly>(type: "date", nullable: true),
                    HalbjahrAb = table.Column<DateOnly>(type: "date", nullable: true),
                    ZustaendigkeitenAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ZustaendigkeitenVon = table.Column<string>(type: "text", nullable: true),
                    ArchivAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivVon = table.Column<string>(type: "text", nullable: true),
                    ArchivPruefsumme = table.Column<string>(type: "text", nullable: true),
                    ArchivGroesse = table.Column<long>(type: "bigint", nullable: true),
                    ArchivMitSchuelerEintraegen = table.Column<bool>(type: "boolean", nullable: true),
                    AufbewahrtAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AufbewahrtVon = table.Column<string>(type: "text", nullable: true),
                    AufbewahrtOrt = table.Column<string>(type: "text", nullable: true),
                    AufbewahrenBis = table.Column<DateOnly>(type: "date", nullable: true),
                    GeloeschtAm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GeloeschtVon = table.Column<string>(type: "text", nullable: true),
                    GeloeschtText = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schuljahre", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Schuljahre_Schuljahr",
                table: "Schuljahre",
                column: "Schuljahr",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Schuljahre");

            migrationBuilder.DropColumn(
                name: "ZustaendigSchuljahr",
                table: "Benutzer");
        }
    }
}
