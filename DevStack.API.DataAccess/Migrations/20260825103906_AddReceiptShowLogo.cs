using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevStack.API.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptShowLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowLogo",
                table: "Shops",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiptShowLogo",
                table: "Shops");
        }
    }
}
