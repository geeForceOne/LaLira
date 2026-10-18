using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaLira.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ColorsRatesAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AlertsCheckedAt",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MainCurrency",
                table: "UserSettings",
                type: "TEXT",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Accounts",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    From = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    To = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Month = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RateDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Rate = table.Column<decimal>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioMonthValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PortfolioId = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
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

            migrationBuilder.CreateTable(
                name: "PriceAlerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PortfolioId = table.Column<int>(type: "INTEGER", nullable: false),
                    Isin = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Above = table.Column<bool>(type: "INTEGER", nullable: false),
                    Limit = table.Column<decimal>(type: "TEXT", nullable: false),
                    Armed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastFiredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceAlerts_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_From_To_Month",
                table: "ExchangeRates",
                columns: new[] { "From", "To", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioMonthValues_PortfolioId_Month_Currency",
                table: "PortfolioMonthValues",
                columns: new[] { "PortfolioId", "Month", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceAlerts_PortfolioId_Isin",
                table: "PriceAlerts",
                columns: new[] { "PortfolioId", "Isin" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropTable(
                name: "PortfolioMonthValues");

            migrationBuilder.DropTable(
                name: "PriceAlerts");

            migrationBuilder.DropColumn(
                name: "AlertsCheckedAt",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "MainCurrency",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "Accounts");
        }
    }
}
