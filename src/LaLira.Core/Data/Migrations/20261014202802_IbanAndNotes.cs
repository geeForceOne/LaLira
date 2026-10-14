using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaLira.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class IbanAndNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Positions",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Iban",
                table: "Accounts",
                type: "TEXT",
                maxLength: 34,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Note",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "Iban",
                table: "Accounts");
        }
    }
}
