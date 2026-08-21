using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevStack.API.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerLoyaltyIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoyaltyCode",
                table: "Customers",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MarketingConsent",
                table: "Customers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SelfSignup",
                table: "Customers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_LoyaltyCode",
                table: "Customers",
                column: "LoyaltyCode",
                unique: true,
                filter: "[LoyaltyCode] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_LoyaltyCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "LoyaltyCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "MarketingConsent",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "SelfSignup",
                table: "Customers");
        }
    }
}
