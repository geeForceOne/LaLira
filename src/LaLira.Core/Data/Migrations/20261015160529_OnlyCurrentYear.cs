using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaLira.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class OnlyCurrentYear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OnlyCurrentYear",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnlyCurrentYear",
                table: "Accounts");
        }
    }
}
