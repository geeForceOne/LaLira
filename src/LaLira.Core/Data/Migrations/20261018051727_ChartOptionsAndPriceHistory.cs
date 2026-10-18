using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaLira.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChartOptionsAndPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortfolioMonthValues");

            migrationBuilder.AddColumn<bool>(
                name: "OthersHidden",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OverviewMonths",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HistoryFetchedAt",
                table: "SecurityPrices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HiddenInTotals",
                table: "Portfolios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InOthers",
                table: "Portfolios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowInMainCurrency",
                table: "Portfolios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HiddenInTotals",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InOthers",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SecurityMonthPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Isin = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Month = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PriceDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityMonthPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityMonthPrices_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityMonthPrices_UserId_Isin_Currency_Month",
                table: "SecurityMonthPrices",
                columns: new[] { "UserId", "Isin", "Currency", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityMonthPrices");

            migrationBuilder.DropColumn(
                name: "OthersHidden",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "OverviewMonths",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "HistoryFetchedAt",
                table: "SecurityPrices");

            migrationBuilder.DropColumn(
                name: "HiddenInTotals",
                table: "Portfolios");

            migrationBuilder.DropColumn(
                name: "InOthers",
                table: "Portfolios");

            migrationBuilder.DropColumn(
                name: "ShowInMainCurrency",
                table: "Portfolios");

            migrationBuilder.DropColumn(
                name: "HiddenInTotals",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "InOthers",
                table: "Accounts");

            migrationBuilder.CreateTable(
                name: "PortfolioMonthValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Month = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioMonthValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioMonthValues_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioMonthValues_PortfolioId_Month_Currency",
                table: "PortfolioMonthValues",
                columns: new[] { "PortfolioId", "Month", "Currency" },
                unique: true);
        }
    }
}
