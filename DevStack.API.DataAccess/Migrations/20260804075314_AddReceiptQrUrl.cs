using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevStack.API.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptQrUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReceiptQrUrl",
                table: "Shops",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiptQrUrl",
                table: "Shops");
        }
    }
}
