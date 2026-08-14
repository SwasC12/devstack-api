using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevStack.API.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddMenuItemCategoryFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                table: "MenuItems",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MenuItems_CategoryId",
                table: "MenuItems",
                column: "CategoryId");

            // Backfill: point every existing item at its category row (matched
            // by shop + name, case-insensitive). Items whose category string
            // has no matching row stay null - the write path recreates it.
            migrationBuilder.Sql("""
                UPDATE mi
                SET mi.CategoryId = c.Id
                FROM MenuItems mi
                INNER JOIN Categories c
                    ON c.ShopId = mi.ShopId
                    AND c.Name = mi.Category
                WHERE mi.CategoryId IS NULL
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_MenuItems_Categories_CategoryId",
                table: "MenuItems",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MenuItems_Categories_CategoryId",
                table: "MenuItems");

            migrationBuilder.DropIndex(
                name: "IX_MenuItems_CategoryId",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "MenuItems");
        }
    }
}
