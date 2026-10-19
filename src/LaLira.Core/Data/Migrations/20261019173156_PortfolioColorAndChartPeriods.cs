using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaLira.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioColorAndChartPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountMonths",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PortfolioMonths",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Portfolios",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountMonths",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PortfolioMonths",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "Portfolios");
        }
    }
}
