using DevStack.API.DataAccess;
using DevStack.API.PlatformLogic.PushLogic;
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
    private readonly IConfiguration _config;

    public PlatformController(DevStackDataModel db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

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

    // GET api/platform/health - REAL availability checks, not theater:
    // API (this response), database (live ping), push (Firebase init state),
    // storage (Cloudinary reachability). Storage is null when no cloud name
    // is configured (local dev) - the UI shows it as n/a rather than red.
    [HttpGet("health")]
    public async Task<ActionResult> GetHealth()
    {
        bool? storage = null;
        var cloud = _config.GetSection("Cloudinary")?["CloudName"];
        if (!string.IsNullOrWhiteSpace(cloud))
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var resp = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Head, $"https://res.cloudinary.com/{cloud}/image/upload"));
                storage = true; // any HTTP response means reachable
            }
            catch
            {
                storage = false;
            }
        }

        return Ok(new
        {
            api = true,
            database = await _db.Database.CanConnectAsync(),
            push = PushService.IsReady,
            storage
        });
    }
}
