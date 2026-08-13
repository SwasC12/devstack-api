using System.Text;
using System.Text.Json;
using DevStack.API.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Housekeeping: one-click JSON backups. Superadmin gets the whole platform;
// a shop admin gets their own shop only (tenant-scoped by the global query
// filters). Download and store it OFF the server. APK binaries are excluded
// (they are republishable) so the file stays small.
[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    private readonly DevStackDataModel _db;
    public AdminController(DevStackDataModel db) => _db = db;

    // GET api/admin/export - superadmin: full data snapshot as a downloadable .json
    [Authorize(Roles = "superadmin")]
    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        var payload = await BuildPayloadAsync(includePlatform: true);
        return File(
            Encoding.UTF8.GetBytes(Serialize(payload)),
            "application/json",
            $"devstack-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.json");
    }

    // GET api/admin/export/shop - shop admin (owner): this shop's own data
    // snapshot. The global ShopId query filters scope every query to the
    // signed-in shop, so a shop can never see another tenant's data.
    [Authorize(Roles = "admin")]
    [HttpGet("export/shop")]
    public async Task<IActionResult> ExportShop()
    {
        var payload = await BuildPayloadAsync(includePlatform: false);
        return File(
            Encoding.UTF8.GetBytes(Serialize(payload)),
            "application/json",
            $"shop-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.json");
    }

    private async Task<Dictionary<string, object?>> BuildPayloadAsync(bool includePlatform)
    {
        var payload = new Dictionary<string, object?>
        {
            ["exportedAtUtc"] = DateTime.UtcNow,
            ["shop"] = await _db.Shops.AsNoTracking().ToListAsync(),
            ["users"] = await _db.Users.AsNoTracking().ToListAsync(),
            ["categories"] = await _db.Categories.AsNoTracking().ToListAsync(),
            ["menuItems"] = await _db.MenuItems.AsNoTracking().ToListAsync(),
            ["sizes"] = await _db.MenuSizes.AsNoTracking().ToListAsync(),
            ["modifierGroups"] = await _db.ModifierGroups.AsNoTracking().ToListAsync(),
            ["modifiers"] = await _db.Modifiers.AsNoTracking().ToListAsync(),
            ["discounts"] = await _db.Discounts.AsNoTracking().ToListAsync(),
            ["orders"] = await _db.Orders.AsNoTracking().ToListAsync(),
            ["orderItems"] = await _db.OrderItems.AsNoTracking().ToListAsync(),
            ["orderItemModifiers"] = await _db.OrderItemModifiers.AsNoTracking().ToListAsync(),
            ["shifts"] = await _db.Shifts.AsNoTracking().ToListAsync(),
            ["notifications"] = await _db.Notifications.AsNoTracking().ToListAsync()
        };

        if (includePlatform)
        {
            payload["checkins"] = await _db.AppCheckins.AsNoTracking().ToListAsync();
            payload["releases"] = await _db.AppReleases.AsNoTracking()
                .Select(r => new { r.Id, r.Version, r.SizeBytes, r.ReleaseNotes, r.IsRequired, r.IsCurrent, r.CreatedAtUtc })
                .ToListAsync();
        }

        return payload;
    }

    private static string Serialize(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
        });
}
