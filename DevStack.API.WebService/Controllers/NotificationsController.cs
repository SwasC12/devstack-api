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
    private readonly EmailService _email;

    public NotificationsController(DevStackDataModel db, IPushService push, EmailService email)
    {
        _db = db;
        _push = push;
        _email = email;
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

        // Fire pushes (best-effort): one per registered device. SendAsync
        // returns true (delivered), false (dead token → delete it), or null
        // (Firebase not configured on the server → skipped, token kept).
        var targetIds = targets.Select(t => t.Id).ToList();
        var tokens = await _db.PushTokens.Where(t => targetIds.Contains(t.UserId)).ToListAsync();
        var sent = 0; var failed = 0; var skipped = 0;
        foreach (var token in tokens)
        {
            var row = rows.FirstOrDefault(r => r.UserId == token.UserId);
            var ok = await _push.SendAsync(token, title, body, type, row?.Id);
            if (ok == true) sent++;
            else if (ok == false) { _db.PushTokens.Remove(token); failed++; }
            else skipped++;
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

        // pushConfigured=false → the server has no Firebase service account, so
        // no FCM was actually sent (in-app notifications still delivered). The
        // UI surfaces this so "0 devices" isn't mistaken for a client problem.
        return Ok(new
        {
            delivered = rows.Count,
            devices = tokens.Count,
            pushed = sent,
            skipped,
            failed,
            pushConfigured = PushService.IsReady
        });
    }

    public record EmailOwnerRequest(string Subject, string Body);

    // POST api/notifications/email-owner - superadmin: server-side email to one
    // shop's owner. Requires SMTP configured (Smtp:* config). The UI falls back
    // to a mailto: draft when this returns 503 (not configured) or 400 (no
    // owner email on file).
    [Authorize(Roles = "superadmin")]
    [HttpPost("email-owner")]
    public async Task<ActionResult> EmailOwner(int shopId, EmailOwnerRequest request)
    {
        var subject = request.Subject?.Trim();
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(body))
            return BadRequest(new { error = "Subject and body are required." });

        var shop = await _db.Shops.FirstOrDefaultAsync(s => s.Id == shopId);
        if (shop is null)
            return BadRequest(new { error = "That shop doesn't exist." });
        if (string.IsNullOrWhiteSpace(shop.OwnerEmail))
            return BadRequest(new { error = "This shop has no owner email on file - add one in Owner contact." });

        var email = _db.Shops.Where(s => s.Id == shopId).Select(s => s.OwnerEmail!).First();
        try
        {
            await _email.SendAsync(email, subject, body);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Server email is not configured - use the mailto draft instead." });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Email failed to send: {ex.Message}" });
        }

        _db.PlatformEvents.Add(new PlatformEvent
        {
            Type = "email_sent",
            ShopId = shopId,
            Detail = $"\"{subject}\" → {email}",
            CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok(new { sentTo = email });
    }

    // POST api/notifications/email-broadcast - superadmin: same subject/body to
    // every shop that has an owner email on file. Best-effort per recipient:
    // one bad address doesn't sink the rest. Only available when SMTP is set up.
    [Authorize(Roles = "superadmin")]
    [HttpPost("email-broadcast")]
    public async Task<ActionResult> EmailBroadcast(EmailOwnerRequest request)
    {
        var subject = request.Subject?.Trim();
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(body))
            return BadRequest(new { error = "Subject and body are required." });

        var targets = await _db.Shops
            .Where(s => s.OwnerEmail != null && s.OwnerEmail != "")
            .Select(s => new { s.Id, s.Name, OwnerEmail = s.OwnerEmail! })
            .ToListAsync();
        if (targets.Count == 0)
            return BadRequest(new { error = "No shops have an owner email on file yet." });

        if (!_email.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Server email is not configured." });

        var sent = 0;
        var failed = 0;
        foreach (var t in targets)
        {
            try
            {
                await _email.SendAsync(t.OwnerEmail, subject, body);
                sent++;
            }
            catch
            {
                failed++;
            }
        }

        _db.PlatformEvents.Add(new PlatformEvent
        {
            Type = "email_broadcast",
            Detail = $"\"{subject}\" → {sent} shop{(sent == 1 ? "" : "s")}{(failed > 0 ? $", {failed} failed" : "")}",
            CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok(new { sent, failed });
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
