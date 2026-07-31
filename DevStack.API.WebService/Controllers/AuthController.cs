using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace DevStack.API.WebService.Controllers;

// Rate-limited (per-IP) and backed by a failed-attempt lockout so a brute-force
// attack can't hammer the login endpoints. See Program.cs for the "auth" policy.
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly IConfiguration _config;
    private readonly IAuthThrottle _throttle;

    public AuthController(DevStackDataModel db, IConfiguration config, IAuthThrottle throttle)
    {
        _db = db;
        _config = config;
        _throttle = throttle;
    }

    public record LoginRequest(string ShopCode, string Username, string Password);
    public record SuperadminLoginRequest(string Username, string Password);
    public record LoginResponse(string Token, string Username, string DisplayName, string Role, int? ShopId, string? ShopName, string? ShopCode);
    public record UpdateProfileRequest(string CurrentPassword, string Username, string DisplayName, string? NewPassword);

    // Shop staff login — a shop code is always required. Superadmins are not in
    // a shop, so they can't use this endpoint; they sign in at superadmin-login.
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request)
    {
        var key = ThrottleKey(request.Username);
        if (_throttle.IsLockedOut(key))
            return StatusCode(StatusCodes.Status423Locked, new { error = "Too many failed attempts. Try again in a few minutes." });

        var code = request.ShopCode?.Trim();
        if (string.IsNullOrEmpty(code))
            return BadRequest(new { error = "Shop code is required." });

        var shop = await _db.Shops.FirstOrDefaultAsync(s => s.Code == code.ToUpperInvariant());
        if (shop is null)
        {
            _throttle.RecordFailure(key);
            return Unauthorized(new { error = "Invalid shop code or credentials." });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.ShopId == shop.Id && u.Username == request.Username);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _throttle.RecordFailure(key);
            return Unauthorized(new { error = "Invalid shop code or credentials." });
        }

        _throttle.Reset(key);
        var token = CreateToken(user, shop);
        return Ok(new LoginResponse(token, user.Username, user.DisplayName, user.Role, user.ShopId, shop.Name, shop.Code));
    }

    // Platform login — the platform owner (superadmin), not tied to any shop.
    // Shop users can't use this: it only matches Role == "superadmin".
    [HttpPost("superadmin-login")]
    public async Task<ActionResult<LoginResponse>> SuperadminLogin(SuperadminLoginRequest request)
    {
        var key = ThrottleKey(request.Username);
        if (_throttle.IsLockedOut(key))
            return StatusCode(StatusCodes.Status423Locked, new { error = "Too many failed attempts. Try again in a few minutes." });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Role == "superadmin" && u.Username == request.Username);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _throttle.RecordFailure(key);
            return Unauthorized(new { error = "Invalid platform credentials." });
        }

        _throttle.Reset(key);
        var token = CreateToken(user, null);
        return Ok(new LoginResponse(token, user.Username, user.DisplayName, user.Role, null, null, null));
    }

    // Self-service account update: change your own username, display name and
    // (optionally) password. "System wide" — the change is the account itself,
    // so it applies to every future login, not just this session. Usernames
    // must stay unique within the shop (or among superadmins).
    [Authorize]
    [HttpPost("profile")]
    public async Task<ActionResult> UpdateProfile(UpdateProfileRequest request)
    {
        var userId = int.Parse(User.FindFirstValue("userId")!);
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect." });

        var username = request.Username.Trim();
        if (username.Length == 0)
            return BadRequest(new { error = "Username cannot be empty." });
        if (username != user.Username)
        {
            var taken = await _db.Users.AnyAsync(u => u.ShopId == user.ShopId && u.Username == username);
            if (taken) return BadRequest(new { error = "Username already exists." });
        }

        user.Username = username;
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
            user.DisplayName = request.DisplayName.Trim();
        if (!string.IsNullOrEmpty(request.NewPassword))
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);

        await _db.SaveChangesAsync();
        return Ok(new { user.Username, user.DisplayName });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var userId = int.Parse(User.FindFirstValue("userId")!);
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect." });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _db.SaveChangesAsync();
        return Ok();
    }

    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    // Lockout identity: the caller's IP + the account they're trying, so failed
    // guesses for one user don't trip another user's lockout.
    private string ThrottleKey(string username) =>
        $"{HttpContext.Connection.RemoteIpAddress}|{username.Trim().ToLowerInvariant()}";

    private string CreateToken(AppUser user, Shop? shop)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new("userId", user.Id.ToString())
        };
        if (user.ShopId is not null)
            claims.Add(new Claim("shopId", user.ShopId.Value.ToString()));
        if (shop is not null)
            claims.Add(new Claim("shopName", shop.Name));

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
