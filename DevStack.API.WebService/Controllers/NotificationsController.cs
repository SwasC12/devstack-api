using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using DevStack.API.PlatformLogic.PushLogic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Owner-facing inbox. The superadmin broadcast is the only writer today; the
// inbox itself is just "what's mine, mark read". Push is a side effect of
// broadcasting - the DB row is the source of truth, FCM is the delivery.
[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly IPushService _push;

    public NotificationsController(DevStackDataModel db, IPushService push)
    {
        _db = db;
        _push = push;
    }

    public record BroadcastRequest(string Title, string Body, string? Type = null, int? ShopId = null);

    // POST api/notifications/broadcast — superadmin only. Targets every shop
    // admin (or one shop's admins) with an in-app notification AND a device
    // push to their registered phones/tablets. E.g. "New version 2.4 is live".
    [Authorize(Roles = "superadmin")]
    [HttpPost("broadcast")]
    public async Task<ActionResult> Broadcast(BroadcastRequest request)
    {
        var title = request.Title?.Trim();
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body))
            return BadRequest(new { error = "Title and body are required." });

        var type = request.Type is "alert" or "info" ? request.Type : "update";

        if (request.ShopId is not null
            && !await _db.Shops.AnyAsync(s => s.Id == request.ShopId))
            return BadRequest(new { error = "That shop doesn't exist." });

        var targets = await _db.Users
            .Where(u => u.Role == "admin" && (request.ShopId == null || u.ShopId == request.ShopId))
            .ToListAsync();

        if (targets.Count == 0)
            return BadRequest(new { error = "No admins to notify." });

        var now = DateTime.UtcNow;
        var rows = targets.Select(t => new Notification
        {
            ShopId = t.ShopId,
            UserId = t.Id,
            Title = title,
            Body = body,
            Type = type,
            CreatedAtUtc = now
        }).ToList();
        _db.Notifications.AddRange(rows);
        await _db.SaveChangesAsync();

        // Fire pushes (best-effort): one per registered device. Dead tokens
        // are deleted; a failed send never fails the broadcast.
        var targetIds = targets.Select(t => t.Id).ToList();
        var tokens = await _db.PushTokens.Where(t => targetIds.Contains(t.UserId)).ToListAsync();
        var failed = 0;
        foreach (var token in tokens)
        {
            var row = rows.FirstOrDefault(r => r.UserId == token.UserId);
            var ok = await _push.SendAsync(token, title, body, type, row?.Id);
            if (ok == false) { _db.PushTokens.Remove(token); failed++; }
        }

        // Superadmin audit trail: the broadcast itself + any dead/undelivered
        // pushes (these feed the platform overview counters and activity feed).
        _db.PlatformEvents.Add(new PlatformEvent
        {
            Type = "broadcast_sent",
            ShopId = request.ShopId,
            Detail = $"\"{title}\" → {rows.Count} owner{(rows.Count == 1 ? "" : "s")}{(failed > 0 ? $", {failed} push{(failed == 1 ? "" : "es")} failed" : "")}",
            CreatedAtUtc = DateTime.UtcNow
        });
        if (failed > 0)
        {
            _db.PlatformEvents.Add(new PlatformEvent
            {
                Type = "push_failed",
                ShopId = request.ShopId,
                Detail = $"{failed} push{(failed == 1 ? "" : "es")} failed for \"{title}\"",
                CreatedAtUtc = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();

        return Ok(new { delivered = rows.Count, pushed = tokens.Count - failed });
    }

    // GET api/notifications — the signed-in user's inbox, unread first.
    [Authorize]
    [HttpGet]
    public async Task<ActionResult> GetMine()
    {
        var userId = int.Parse(User.FindFirstValue("userId")!);
        var items = await _db.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(50)
            .ToListAsync();

        return Ok(new
        {
            unread = items.Count(n => n.ReadAtUtc is null),
            items = items.Select(n => new { n.Id, n.Title, n.Body, n.Type, n.CreatedAtUtc, n.ReadAtUtc })
        });
    }

    // POST api/notifications/{id}/read — mark one of mine as read.
    [Authorize]
    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        var userId = int.Parse(User.FindFirstValue("userId")!);
        var item = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (item is null) return NotFound();

        item.ReadAtUtc = item.ReadAtUtc ?? DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    // POST api/notifications/read-all — the "clear the bell" action.
    [Authorize]
    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = int.Parse(User.FindFirstValue("userId")!);
        await _db.Notifications
            .Where(n => n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, DateTime.UtcNow));
        return Ok();
    }
}
