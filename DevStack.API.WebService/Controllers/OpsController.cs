using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevStack.API.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Headless platform-ops endpoints, gated by the static X-Ops-Key (Ops:Key) — no
// login needed, for one-off maintenance run by the platform owner / tooling.
[ApiController]
[Route("api/ops")]
[AllowAnonymous]
public class OpsController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly IConfiguration _config;

    public OpsController(DevStackDataModel db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private bool OpsAuthorized()
    {
        var opsKey = _config["Ops:Key"];
        if (string.IsNullOrEmpty(opsKey)) return false;
        var presented = Request.Headers["X-Ops-Key"].ToString();
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(opsKey);
        return a.Length > 0 && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // GET api/ops/overview-timing — times each query the platform overview runs,
    // to find what's slow. Ops-key gated.
    [HttpGet("overview-timing")]
    public async Task<ActionResult> OverviewTiming()
    {
        if (!OpsAuthorized()) return Unauthorized(new { error = "Invalid ops key." });
        var today = DateTime.UtcNow.AddHours(2).Date;
        var since30d = DateTime.UtcNow.AddDays(-30);
        var r = new Dictionary<string, object>();

        async Task<long> Time(Func<Task> work) { var sw = System.Diagnostics.Stopwatch.StartNew(); await work(); sw.Stop(); return sw.ElapsedMilliseconds; }

        r["shops"] = await Time(async () => await _db.Shops.AsNoTracking().Select(s => new { s.IsActive, s.BillingStatus, s.MonthlyPrice }).ToListAsync());
        r["eventCounts"] = await Time(async () => await _db.PlatformEvents.Where(e => e.CreatedAtUtc >= since30d && (e.Type == "password_reset" || e.Type == "push_failed")).GroupBy(e => e.Type).Select(g => new { g.Key, C = g.Count() }).ToListAsync());
        r["ordersToday"] = await Time(async () => await _db.Orders.IgnoreQueryFilters().CountAsync(o => o.CreatedAt >= today && o.VoidedAt == null));
        r["notifications30d"] = await Time(async () => await _db.Notifications.CountAsync(n => n.CreatedAtUtc >= since30d));
        r["eventsTop10"] = await Time(async () => await _db.PlatformEvents.OrderByDescending(e => e.CreatedAtUtc).Take(10).Select(e => e.Id).ToListAsync());
        r["currentReleaseFULL_old"] = await Time(async () => await _db.AppReleases.Where(x => x.IsCurrent).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync());
        r["currentVersion_fixed"] = await Time(async () => await _db.AppReleases.Where(x => x.IsCurrent).OrderByDescending(x => x.CreatedAtUtc).Select(x => x.Version).FirstOrDefaultAsync());
        r["checkins"] = await Time(async () => await _db.AppCheckins.ToListAsync());
        r["rowcounts"] = new
        {
            orders = await _db.Orders.IgnoreQueryFilters().CountAsync(),
            notifications = await _db.Notifications.CountAsync(),
            platformEvents = await _db.PlatformEvents.CountAsync(),
        };
        return Ok(r);
    }

    // POST api/ops/cloudinary-sweep?apply=false — find (and optionally delete)
    // Cloudinary images in the shop-* folders that no menu item / shop / brand
    // still references. Dry-run by default; apply=true deletes.
    [HttpPost("cloudinary-sweep")]
    public async Task<ActionResult> CloudinarySweep([FromQuery] bool apply = false)
    {
        if (!OpsAuthorized()) return Unauthorized(new { error = "Invalid ops key." });

        var cloud = _config["Cloudinary:CloudName"];
        var apiKey = _config["Cloudinary:ApiKey"];
        var apiSecret = _config["Cloudinary:ApiSecret"];
        if (string.IsNullOrWhiteSpace(cloud) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            return StatusCode(503, new { error = "Cloudinary is not configured on the server." });

        // ── Referenced public_ids (never delete these) ──
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = await _db.MenuItems.IgnoreQueryFilters()
            .Select(m => new { m.ImagePublicId, m.ImageUrl }).ToListAsync();
        foreach (var it in items)
        {
            if (!string.IsNullOrWhiteSpace(it.ImagePublicId)) referenced.Add(it.ImagePublicId);
            var fromUrl = PublicIdFromUrl(it.ImageUrl);
            if (fromUrl is not null) referenced.Add(fromUrl);
        }
        foreach (var url in await _db.Shops.Select(s => s.LogoUrl).ToListAsync())
        { var p = PublicIdFromUrl(url); if (p is not null) referenced.Add(p); }
        foreach (var url in await _db.Brands.Select(b => b.LogoUrl).ToListAsync())
        { var p = PublicIdFromUrl(url); if (p is not null) referenced.Add(p); }

        // ── All Cloudinary images in the shop-* folders ──
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:{apiSecret}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

        var all = new List<string>();
        string? cursor = null;
        do
        {
            var url = $"https://api.cloudinary.com/v1_1/{cloud}/resources/image?type=upload&prefix=shop-&max_results=500";
            if (cursor is not null) url += "&next_cursor=" + Uri.EscapeDataString(cursor);
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return StatusCode(502, new { error = $"Cloudinary list failed: {(int)resp.StatusCode}" });
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("resources", out var res))
                foreach (var r in res.EnumerateArray())
                    if (r.TryGetProperty("public_id", out var pid)) all.Add(pid.GetString()!);
            cursor = doc.RootElement.TryGetProperty("next_cursor", out var nc) ? nc.GetString() : null;
        } while (cursor is not null);

        var orphans = all.Where(p => !referenced.Contains(p)).ToList();

        if (!apply)
            return Ok(new { dryRun = true, totalInCloud = all.Count, referenced = referenced.Count, orphanCount = orphans.Count, orphans = orphans.Take(500) });

        // ── Delete orphans (batches of 100) ──
        var deleted = 0;
        for (var i = 0; i < orphans.Count; i += 100)
        {
            var batch = orphans.Skip(i).Take(100).ToList();
            var qs = string.Join("&", batch.Select(p => "public_ids[]=" + Uri.EscapeDataString(p)));
            // Admin bulk-delete path includes resource_type/type: /resources/image/upload
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"https://api.cloudinary.com/v1_1/{cloud}/resources/image/upload?{qs}");
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                // Response has a "deleted" map { public_id: "deleted"|"not_found" }.
                using var d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (d.RootElement.TryGetProperty("deleted", out var del))
                    foreach (var kv in del.EnumerateObject())
                        if (kv.Value.GetString() == "deleted") deleted++;
            }
        }
        return Ok(new { dryRun = false, orphanCount = orphans.Count, deleted });
    }

    // Extract a Cloudinary public_id (folder/name, no version, no extension) from
    // a delivery URL. Returns null for non-Cloudinary / empty URLs.
    private static string? PublicIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Contains("res.cloudinary.com")) return null;
        var marker = "/upload/";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var path = url[(idx + marker.Length)..];
        var q = path.IndexOf('?'); if (q >= 0) path = path[..q];
        // Drop a leading version segment like "v1699999999/".
        if (path.StartsWith('v'))
        {
            var slash = path.IndexOf('/');
            if (slash > 1 && path[1..slash].All(char.IsDigit)) path = path[(slash + 1)..];
        }
        var dot = path.LastIndexOf('.');
        if (dot > 0) path = path[..dot];
        return path.Length == 0 ? null : path;
    }
}
