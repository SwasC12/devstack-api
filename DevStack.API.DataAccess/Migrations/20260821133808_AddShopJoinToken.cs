using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevStack.API.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddShopJoinToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JoinToken",
                table: "Shops",
                type: "nvarchar(450)",
                nullable: true);

            // Backfill existing shops with a distinct random token (NEWID() is
            // evaluated per row) so their public join URL works immediately,
            // without waiting for an admin to open Settings.
            migrationBuilder.Sql(
                "UPDATE [Shops] SET [JoinToken] = SUBSTRING(REPLACE(CONVERT(varchar(40), NEWID()), '-', ''), 1, 12) WHERE [JoinToken] IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Shops_JoinToken",
                table: "Shops",
                column: "JoinToken",
                unique: true,
                filter: "[JoinToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Shops_JoinToken",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "JoinToken",
                table: "Shops");
        }
    }
}
