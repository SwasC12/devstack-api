using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevStack.API.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandAndLoyaltyMember : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BrandId",
                table: "Shops",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Brands",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    JoinToken = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    LoyaltyEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LoyaltyStampsRequired = table.Column<int>(type: "int", nullable: false),
                    LoyaltyReward = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LogoUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoyaltyMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BrandId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LoyaltyCode = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    LoyaltyStamps = table.Column<int>(type: "int", nullable: false),
                    LoyaltyPasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MarketingConsent = table.Column<bool>(type: "bit", nullable: false),
                    SelfSignup = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoyaltyMembers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Brands_JoinToken",
                table: "Brands",
                column: "JoinToken",
                unique: true,
                filter: "[JoinToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LoyaltyMembers_BrandId",
                table: "LoyaltyMembers",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_LoyaltyMembers_LoyaltyCode",
                table: "LoyaltyMembers",
                column: "LoyaltyCode",
                unique: true,
                filter: "[LoyaltyCode] IS NOT NULL");

            // ── Additive data migration (copies, never deletes) ──────────────
            // 1) One brand per existing shop, REUSING the shop's unique join token
            //    and its loyalty config — so any join QR already printed keeps
            //    working (it now resolves to the brand).
            migrationBuilder.Sql(@"
INSERT INTO [Brands] ([Name],[JoinToken],[LoyaltyEnabled],[LoyaltyStampsRequired],[LoyaltyReward],[LogoUrl],[CreatedAt])
SELECT [Name],[JoinToken],[LoyaltyEnabled],[LoyaltyStampsRequired],[LoyaltyReward],[LogoUrl],[CreatedAt] FROM [Shops];");

            // 2) Link each shop to its brand (join tokens are unique + present).
            migrationBuilder.Sql(@"
UPDATE s SET s.[BrandId] = b.[Id]
FROM [Shops] s INNER JOIN [Brands] b ON b.[JoinToken] = s.[JoinToken];");

            // 3) Copy existing loyalty customers into brand-scoped members. The
            //    original Customer rows are left intact as a fallback.
            migrationBuilder.Sql(@"
INSERT INTO [LoyaltyMembers] ([BrandId],[Name],[Phone],[Email],[LoyaltyCode],[LoyaltyStamps],[LoyaltyPasswordHash],[MarketingConsent],[SelfSignup],[CreatedAt])
SELECT s.[BrandId], c.[Name], c.[Phone], c.[Email], c.[LoyaltyCode], c.[LoyaltyStamps], c.[LoyaltyPasswordHash], c.[MarketingConsent], c.[SelfSignup], c.[CreatedAt]
FROM [Customers] c INNER JOIN [Shops] s ON s.[Id] = c.[ShopId]
WHERE c.[LoyaltyCode] IS NOT NULL AND s.[BrandId] IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Brands");

            migrationBuilder.DropTable(
                name: "LoyaltyMembers");

            migrationBuilder.DropColumn(
                name: "BrandId",
                table: "Shops");
        }
    }
}
