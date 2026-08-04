using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Device registration for Firebase Cloud Messaging. The app calls register
// after login (and whenever the token refreshes) and unregister on logout, so
// a signed-out till stops receiving pushes.
[ApiController]
[Route("api/[controller]")]
public class PushController : ControllerBase
{
    private readonly DevStackDataModel _db;

    public PushController(DevStackDataModel db) => _db = db;

    public record RegisterRequest(string Token, string? Platform = null);

    // POST api/push/register — upsert this device token against the current user.
    [Authorize]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length < 20)
            return BadRequest(new { error = "A valid device token is required." });

        var userId = int.Parse(User.FindFirstValue("userId")!);
        var shopId = int.Parse(User.FindFirstValue("shopId") ?? "-1");
        var platform = request.Platform is "web" or "ios" ? request.Platform : "android";
        var now = DateTime.UtcNow;

        // Same token moving between users (reinstall/relogin) follows the user.
        var existing = await _db.PushTokens.FirstOrDefaultAsync(t => t.Token == token);
        if (existing is not null)
        {
            existing.UserId = userId;
            existing.ShopId = shopId > 0 ? shopId : existing.ShopId;
            existing.Platform = platform;
            existing.LastSeenAtUtc = now;
        }
        else
        {
            _db.PushTokens.Add(new PushToken
            {
                UserId = userId,
                ShopId = shopId > 0 ? shopId : null,
                Token = token,
                Platform = platform,
                CreatedAtUtc = now,
                LastSeenAtUtc = now
            });
        }

        await _db.SaveChangesAsync();
        return Ok();
    }

    // POST api/push/unregister — remove this device (logout / token revoked).
    [Authorize]
    [HttpPost("unregister")]
    public async Task<IActionResult> Unregister(RegisterRequest request)
    {
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token)) return BadRequest();

        var userId = int.Parse(User.FindFirstValue("userId")!);
        var existing = await _db.PushTokens.FirstOrDefaultAsync(t => t.Token == token && t.UserId == userId);
        if (existing is not null)
        {
            _db.PushTokens.Remove(existing);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }
}
