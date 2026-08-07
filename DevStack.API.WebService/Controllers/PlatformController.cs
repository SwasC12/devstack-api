using DevStack.API.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Superadmin platform dashboard: cross-shop counters + recent activity feed.
// All counts must opt out of the shop-scope query filter (superadmin has no
// shop), exactly like the shops list does.
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "superadmin")]
public class PlatformController : ControllerBase
{
    private readonly DevStackDataModel _db;

    public PlatformController(DevStackDataModel db) => _db = db;

    // GET api/platform/overview - counters + the last 10 audit events.
    [HttpGet("overview")]
    public async Task<ActionResult> GetOverview()
    {
        var now = DateTime.UtcNow.AddHours(2); // same clock the app stores
        var today = now.Date;
        var since30d = DateTime.UtcNow.AddDays(-30);

        var stats = new
        {
            totalShops = await _db.Shops.CountAsync(),
            activeShops = await _db.Shops.CountAsync(s => s.IsActive),
            suspendedShops = await _db.Shops.CountAsync(s => !s.IsActive),
            ordersToday = await _db.Orders.IgnoreQueryFilters().CountAsync(o => o.CreatedAt >= today && o.VoidedAt == null),
            notificationsSent30d = await _db.Notifications.CountAsync(n => n.CreatedAtUtc >= since30d),
            passwordResets30d = await _db.PlatformEvents.CountAsync(e => e.Type == "password_reset" && e.CreatedAtUtc >= since30d),
            pushFailures30d = await _db.PlatformEvents.CountAsync(e => e.Type == "push_failed" && e.CreatedAtUtc >= since30d)
        };

        var events = await _db.PlatformEvents
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(10)
            .Select(e => new { e.Id, e.Type, e.ShopId, e.Detail, e.CreatedAtUtc })
            .ToListAsync();

        return Ok(new { stats, events });
    }
}
