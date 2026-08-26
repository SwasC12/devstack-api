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
    private readonly IPushService _push;

    public PlatformController(DevStackDataModel db, IConfiguration config, IPushService push)
    {
        _db = db;
        _config = config;
        _push = push;
    }

    // GET api/platform/overview - counters + the last 10 audit events.
    [HttpGet("overview")]
    public async Task<ActionResult> GetOverview()
    {
        var now = DateTime.UtcNow.AddHours(2); // same clock the app stores
        var today = now.Date;
        var since30d = DateTime.UtcNow.AddDays(-30);

        // Load the (small) Shops table ONCE and derive all shop/billing counters
        // in memory — was 6 separate round trips.
        var shops = await _db.Shops.AsNoTracking()
            .Select(s => new { s.IsActive, s.BillingStatus, s.MonthlyPrice }).ToListAsync();

        // Both PlatformEvent counters in ONE query instead of two.
        var eventCounts = (await _db.PlatformEvents
            .Where(e => e.CreatedAtUtc >= since30d && (e.Type == "password_reset" || e.Type == "push_failed"))
            .GroupBy(e => e.Type).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.Count);

        var stats = new
        {
            totalShops = shops.Count,
            activeShops = shops.Count(s => s.IsActive),
            suspendedShops = shops.Count(s => !s.IsActive),
            ordersToday = await _db.Orders.IgnoreQueryFilters().CountAsync(o => o.CreatedAt >= today && o.VoidedAt == null),
            notificationsSent30d = await _db.Notifications.CountAsync(n => n.CreatedAtUtc >= since30d),
            passwordResets30d = eventCounts.TryGetValue("password_reset", out var pr) ? pr : 0,
            pushFailures30d = eventCounts.TryGetValue("push_failed", out var pf) ? pf : 0,
            // Billing rollup — recurring revenue to the platform owner.
            mrr = shops.Where(s => s.BillingStatus == "active").Sum(s => s.MonthlyPrice),
            trialShops = shops.Count(s => s.BillingStatus == "trial"),
            overdueShops = shops.Count(s => s.BillingStatus == "overdue")
        };

        var events = await _db.PlatformEvents
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(10)
            .Select(e => new { e.Id, e.Type, e.ShopId, e.Detail, e.CreatedAtUtc })
            .ToListAsync();

        // App-update status: current release vs what shops checked in with.
        // Project to Version ONLY — loading the full entity drags the APK binary
        // blob out of the DB (tens of MB), which made the overview take ~12s.
        var currentVersion = await _db.AppReleases
            .Where(r => r.IsCurrent)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => r.Version)
            .FirstOrDefaultAsync();
        var checkins = await _db.AppCheckins.ToListAsync();
        var shopsUpdated = currentVersion is null ? 0 : checkins.Count(c => c.Version == currentVersion);

        var update = new
        {
            currentVersion = currentVersion as string,
            shopsCheckedIn = checkins.Count,
            shopsUpdated,
            shopsOnOldVersion = checkins.Count - shopsUpdated
        };

        return Ok(new { stats, events, update });
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
            // Initialise Firebase on demand so the dot reflects the real config
            // state (green) immediately, not only after the first push send.
            push = _push.EnsureConfigured(),
            storage
        });
    }

    // GET api/platform/revenue-series?days=30[&shopId=] — daily revenue + order
    // count across ALL shops (or one shop when shopId is given), for the trend
    // chart. Cross-shop, so IgnoreQueryFilters; excludes voided orders. Days
    // with no sales are filled with zeros so the series is continuous.
    [HttpGet("revenue-series")]
    public async Task<ActionResult> GetRevenueSeries([FromQuery] int days = 30, [FromQuery] int? shopId = null)
    {
        days = Math.Clamp(days, 1, 365);
        var today = DateTime.UtcNow.AddHours(2).Date; // SAST, matches stored CreatedAt
        var start = today.AddDays(-(days - 1));

        var query = _db.Orders.IgnoreQueryFilters()
            .Where(o => o.VoidedAt == null && o.CreatedAt >= start && o.CreatedAt < today.AddDays(1));
        if (shopId is not null) query = query.Where(o => o.ShopId == shopId);

        var grouped = await query
            .GroupBy(o => o.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Revenue = g.Sum(o => o.Total), Orders = g.Count() })
            .ToListAsync();
        var byDate = grouped.ToDictionary(g => g.Date);

        var series = new List<object>(days);
        decimal totalRevenue = 0m;
        int totalOrders = 0;
        for (var i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            byDate.TryGetValue(d, out var row);
            var rev = row?.Revenue ?? 0m;
            var ord = row?.Orders ?? 0;
            totalRevenue += rev;
            totalOrders += ord;
            series.Add(new { date = d.ToString("yyyy-MM-dd"), revenue = rev, orders = ord });
        }

        return Ok(new { days, totalRevenue, totalOrders, series });
    }
}
