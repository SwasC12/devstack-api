using System.Text;
using System.Text.Json;
using DevStack.API.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Superadmin housekeeping: one-click JSON backup of the whole tenant data
// (shops, users, menu + modifiers, orders + lines, shifts, discounts,
// notifications, release metadata). Download and store it OFF the server.
// APK binaries are excluded (they are republishable) so the file stays small.
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
        var payload = new Dictionary<string, object?>
        {
            ["exportedAtUtc"] = DateTime.UtcNow,
            ["shops"] = await _db.Shops.AsNoTracking().ToListAsync(),
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
            ["checkins"] = await _db.AppCheckins.AsNoTracking().ToListAsync(),
            ["notifications"] = await _db.Notifications.AsNoTracking().ToListAsync(),
            ["releases"] = await _db.AppReleases.AsNoTracking()
                .Select(r => new { r.Id, r.Version, r.SizeBytes, r.ReleaseNotes, r.IsRequired, r.IsCurrent, r.CreatedAtUtc })
                .ToListAsync()
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
        });
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"devstack-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.json");
    }
}
