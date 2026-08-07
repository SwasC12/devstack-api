using System.Security.Claims;
using System.Text.RegularExpressions;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using DevStack.API.PlatformLogic.PushLogic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// In-app updater: the superadmin publishes APK releases; signed-in clients
// check the current version, download the APK, and check in with the version
// they run (feeds the "shops updated" dashboard). A publish pushes a
// notification to every shop admin.
[ApiController]
[Route("api/[controller]")]
public class AppController : ControllerBase
{
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    private readonly DevStackDataModel _db;
    private readonly IPushService _push;

    public AppController(DevStackDataModel db, IPushService push)
    {
        _db = db;
        _push = push;
    }

    public record CheckinRequest(string Version);

    // POST api/app/releases - superadmin: publish a new APK release. Becomes
    // the current release; the previous one is kept for rollback. Every shop
    // admin gets an in-app notification + FCM push.
    [Authorize(Roles = "superadmin")]
    [HttpPost("releases")]
    public async Task<ActionResult> PublishRelease(
        [FromForm] string version,
        [FromForm] string? releaseNotes,
        [FromForm] bool isRequired,
        IFormFile? file)
    {
        var v = version?.Trim() ?? "";
        if (!VersionPattern.IsMatch(v))
            return BadRequest(new { error = "Version must look like 1.3 or 1.3.0." });
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Attach the APK file." });
        if (!Path.GetExtension(file.FileName).Equals(".apk", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "The file must be an .apk." });
        if (await _db.AppReleases.AnyAsync(r => r.Version == v))
            return BadRequest(new { error = $"Version {v} was already published. Pick a new version." });

        var now = DateTime.UtcNow;
        await _db.AppReleases.Where(r => r.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false));

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);

        var release = new AppRelease
        {
            Version = v,
            ApkData = ms.ToArray(),
            SizeBytes = ms.Length,
            ReleaseNotes = releaseNotes?.Trim() ?? "",
            IsRequired = isRequired,
            IsCurrent = true,
            CreatedAtUtc = now
        };
        _db.AppReleases.Add(release);
        await _db.SaveChangesAsync();

        // Notify every shop admin (in-app rows + best-effort FCM push).
        var admins = await _db.Users.Where(u => u.Role == "admin").ToListAsync();
        if (admins.Count > 0)
        {
            var body = string.IsNullOrWhiteSpace(release.ReleaseNotes)
                ? "Tap to update."
                : release.ReleaseNotes.Length > 140 ? release.ReleaseNotes[..140] + "…" : release.ReleaseNotes;
            var rows = admins.Select(a => new Notification
            {
                ShopId = a.ShopId,
                UserId = a.Id,
                Title = $"CoffeeShop Pro {v} available",
                Body = body,
                Type = "update",
                CreatedAtUtc = now
            }).ToList();
            _db.Notifications.AddRange(rows);
            await _db.SaveChangesAsync();

            var adminIds = admins.Select(a => a.Id).ToList();
            var tokens = await _db.PushTokens.Where(t => adminIds.Contains(t.UserId)).ToListAsync();
            foreach (var token in tokens)
            {
                var row = rows.FirstOrDefault(r => r.UserId == token.UserId);
                var ok = await _push.SendAsync(token, $"CoffeeShop Pro {v} available", body, "update", row?.Id);
                if (ok == false) _db.PushTokens.Remove(token);
            }
            if (tokens.Count > 0) await _db.SaveChangesAsync();
        }

        return Ok(new { release.Id, release.Version, release.IsRequired, release.IsCurrent, release.SizeBytes, release.CreatedAtUtc });
    }

    // GET api/app/releases - superadmin: release history (no binaries).
    [Authorize(Roles = "superadmin")]
    [HttpGet("releases")]
    public async Task<ActionResult> ListReleases()
    {
        var releases = await _db.AppReleases
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => new { r.Id, r.Version, r.SizeBytes, r.ReleaseNotes, r.IsRequired, r.IsCurrent, r.CreatedAtUtc })
            .ToListAsync();
        return Ok(releases);
    }

    // DELETE api/app/releases/{id} - superadmin: remove a release. Deleting
    // the current one rolls the newest remaining release back to current.
    [Authorize(Roles = "superadmin")]
    [HttpDelete("releases/{id:int}")]
    public async Task<IActionResult> DeleteRelease(int id)
    {
        var release = await _db.AppReleases.FindAsync(id);
        if (release is null) return NotFound();

        _db.AppReleases.Remove(release);
        await _db.SaveChangesAsync();

        if (release.IsCurrent)
        {
            var next = await _db.AppReleases.OrderByDescending(r => r.CreatedAtUtc).FirstOrDefaultAsync();
            if (next is not null)
            {
                next.IsCurrent = true;
                await _db.SaveChangesAsync();
            }
        }
        return Ok();
    }

    // GET api/app/version - any signed-in user: current release metadata
    // (the client compares against its own version).
    [Authorize]
    [HttpGet("version")]
    public async Task<ActionResult> GetVersion()
    {
        var release = await _db.AppReleases
            .Where(r => r.IsCurrent)
            .OrderByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (release is null) return Ok(new { available = false });

        return Ok(new
        {
            available = true,
            release.Version,
            release.ReleaseNotes,
            release.IsRequired,
            release.SizeBytes
        });
    }

    // GET api/app/download - any signed-in user: stream the current APK.
    [Authorize]
    [HttpGet("download")]
    public async Task<IActionResult> Download()
    {
        var release = await _db.AppReleases
            .Where(r => r.IsCurrent)
            .OrderByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (release is null) return NotFound(new { error = "No release published yet." });

        return File(release.ApkData, "application/vnd.android.package-archive", $"CoffeeShopPro-{release.Version}.apk");
    }

    // POST api/app/checkin - any signed-in user: report the version this shop
    // runs. Upserted per shop; feeds the "shops updated" dashboard.
    [Authorize]
    [HttpPost("checkin")]
    public async Task<IActionResult> Checkin(CheckinRequest request)
    {
        var shopId = int.Parse(User.FindFirstValue("shopId") ?? "-1");
        if (shopId <= 0) return Ok(); // superadmins have no shop

        var version = (request.Version ?? "").Trim();
        var checkin = await _db.AppCheckins.FirstOrDefaultAsync(c => c.ShopId == shopId);
        if (checkin is null)
        {
            _db.AppCheckins.Add(new AppCheckin { ShopId = shopId, Version = version, LastSeenAtUtc = DateTime.UtcNow });
        }
        else
        {
            checkin.Version = version;
            checkin.LastSeenAtUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok();
    }
}
