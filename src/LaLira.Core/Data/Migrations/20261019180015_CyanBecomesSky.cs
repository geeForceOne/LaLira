using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaLira.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CyanBecomesSky : Migration
    {
        /// <inheritdoc />
        // The palette of 16 replaced Cyan with Sky; accounts and portfolios keep their color.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Accounts SET Color = 'sky' WHERE Color = 'cyan';");
            migrationBuilder.Sql("UPDATE Portfolios SET Color = 'sky' WHERE Color = 'cyan';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Accounts SET Color = 'cyan' WHERE Color = 'sky';");
            migrationBuilder.Sql("UPDATE Portfolios SET Color = 'cyan' WHERE Color = 'sky';");
        }
    }
}
