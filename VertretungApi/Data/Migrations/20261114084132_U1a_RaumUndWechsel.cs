using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VertretungApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class U1a_RaumUndWechsel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Raum",
                table: "GrundplanSlots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FachGeaendert",
                table: "Eintraege",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KlassenGeaendert",
                table: "Eintraege",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RaumGeaendert",
                table: "Eintraege",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Raum",
                table: "GrundplanSlots");

            migrationBuilder.DropColumn(
                name: "FachGeaendert",
                table: "Eintraege");

            migrationBuilder.DropColumn(
                name: "KlassenGeaendert",
                table: "Eintraege");

            migrationBuilder.DropColumn(
                name: "RaumGeaendert",
                table: "Eintraege");
        }
    }
}
