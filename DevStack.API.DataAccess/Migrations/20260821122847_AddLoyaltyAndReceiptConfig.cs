using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevStack.API.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddLoyaltyAndReceiptConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LoyaltyEnabled",
                table: "Shops",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LoyaltyReward",
                table: "Shops",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LoyaltyStampsRequired",
                table: "Shops",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptFooter",
                table: "Shops",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptHeader",
                table: "Shops",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowCashier",
                table: "Shops",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowQr",
                table: "Shops",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowVat",
                table: "Shops",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LoyaltyStamps",
                table: "Customers",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoyaltyEnabled",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "LoyaltyReward",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "LoyaltyStampsRequired",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "ReceiptFooter",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "ReceiptHeader",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "ReceiptShowCashier",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "ReceiptShowQr",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "ReceiptShowVat",
                table: "Shops");

            migrationBuilder.DropColumn(
                name: "LoyaltyStamps",
                table: "Customers");
        }
    }
}
