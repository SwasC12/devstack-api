using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace DevStack.API.WebService.Controllers;

// Authentication: rate-limited per-IP, backed by a failed-attempt lockout, and
// two-layer tokens. The ACCESS token is short-lived (15 min) and only ever held
// in the frontend's memory. The REFRESH token lives in an HttpOnly cookie and
// rotates on every use, so a stolen cookie dies on first replay.
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private const string RefreshCookieName = "refresh_token";

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
    public record PinLoginRequest(string ShopCode, int UserId, string Pin);
    public record VerifyPinRequest(string? Pin);
    public record StaffRequest(string ShopCode);
    public record UpdateProfileRequest(string CurrentPassword, string Username, string DisplayName, string? NewPassword);
    public record RefreshRequest(string? RefreshToken);
    public record LogoutRequest(string? RefreshToken);
    // RefreshToken is only populated for native clients (X-Client: native) so
    // the Capacitor app can persist it in device storage; the web UI keeps the
    // HttpOnly-cookie-only model and never sees the raw value.
    public record LoginResponse(string Token, string Username, string DisplayName, string Role, int? ShopId, string? ShopName, string? ShopCode, string? RefreshToken = null);

    // ── Shop staff login (password) ──────────────────────────────────────────

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
        if (!shop.IsActive)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "This shop is suspended. Contact the platform owner." });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.ShopId == shop.Id && u.Username == request.Username);
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _throttle.RecordFailure(key);
            return Unauthorized(new { error = "Invalid shop code or credentials." });
        }

        _throttle.Reset(key);
        var access = CreateToken(user, shop);
        var rawRefresh = IssueRefreshToken(user);
        return Ok(BuildLoginResponse(access, user, shop, rawRefresh));
    }

    // ── Platform owner login ─────────────────────────────────────────────────

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
        var access = CreateToken(user, null);
        var rawRefresh = IssueRefreshToken(user);
        return Ok(BuildLoginResponse(access, user, null, rawRefresh));
    }

    // ── Staff PIN login (fast cashier sign-in) ──────────────────────────────

    // POST api/auth/superadmin-reset - platform recovery hatch: resets the
    // superadmin password without a superadmin login or DB access. Guarded by a
    // static ops key from config (Ops:Key / env Ops__Key) that is never a login
    // password itself. The fresh one-time password is returned ONCE; only its
    // bcrypt hash lands in the database, and the old refresh-token chain is
    // burned so any existing superadmin sessions die.
    [HttpPost("superadmin-reset")]
    public async Task<ActionResult> SuperadminReset()
    {
        var key = ThrottleKey("ops-reset");
        if (_throttle.IsLockedOut(key))
            return StatusCode(StatusCodes.Status423Locked, new { error = "Too many failed attempts. Try again in a few minutes." });

        var opsKey = _config["Ops:Key"];
        if (string.IsNullOrEmpty(opsKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Ops key is not configured on the server." });

        var presented = Request.Headers["X-Ops-Key"].ToString();
        var presentedBytes = System.Text.Encoding.UTF8.GetBytes(presented);
        var opsBytes = System.Text.Encoding.UTF8.GetBytes(opsKey);
        var matches = presentedBytes.Length > 0
                      && presentedBytes.Length == opsBytes.Length
                      && CryptographicOperations.FixedTimeEquals(presentedBytes, opsBytes);
        if (!matches)
        {
            _throttle.RecordFailure(key);
            return Unauthorized(new { error = "Invalid ops key." });
        }

        _throttle.Reset(key);
        var superadmin = await _db.Users.FirstOrDefaultAsync(u => u.Role == "superadmin");
        if (superadmin is null)
            return NotFound(new { error = "No superadmin account exists to reset." });

        var password = GeneratePassword();
        superadmin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);

        // Reset = kill existing sessions: burn the superadmin's whole refresh chain.
        await _db.RefreshTokens
            .Where(t => t.UserId == superadmin.Id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAtUtc, DateTime.UtcNow));

        await _db.SaveChangesAsync();

        return Ok(new { username = superadmin.Username, displayName = superadmin.DisplayName, password });
    }

    // Random 18-char password, same rules as the platform's one-time seeds.
    private static string GeneratePassword(int length = 18)
    {
        const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new System.Text.StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(chars[bytes[i] % chars.Length]);
        return sb.ToString();
    }

    [HttpPost("pin-login")]
    public async Task<ActionResult<LoginResponse>> PinLogin(PinLoginRequest request)
    {
        var key = ThrottleKey(request.Pin);
        if (_throttle.IsLockedOut(key))
            return StatusCode(StatusCodes.Status423Locked, new { error = "Too many failed attempts. Try again in a few minutes." });

        var shop = await _db.Shops.FirstOrDefaultAsync(s => s.Code == request.ShopCode.Trim().ToUpperInvariant());
        if (shop is null)
        {
            _throttle.RecordFailure(key);
            return Unauthorized(new { error = "Invalid shop or PIN." });
        }
        if (!shop.IsActive)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "This shop is suspended. Contact the platform owner." });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.ShopId == shop.Id && u.Id == request.UserId);
        if (user is null || string.IsNullOrEmpty(user.PinHash) || !BCrypt.Net.BCrypt.Verify(request.Pin, user.PinHash))
        {
            _throttle.RecordFailure(key);
            return Unauthorized(new { error = "Invalid PIN." });
        }

        _throttle.Reset(key);
        var access = CreateToken(user, shop);
        var rawRefresh = IssueRefreshToken(user);
        return Ok(BuildLoginResponse(access, user, shop, rawRefresh));
    }

    // POST api/auth/verify-pin - any signed-in user: is this the PIN of one
    // of the shop's admins? Used for manager-approval actions (voiding an
    // order). Same failed-attempt lockout as pin-login, keyed per PIN.
    [HttpPost("verify-pin")]
    public async Task<ActionResult> VerifyPin(VerifyPinRequest request)
    {
        var pin = request.Pin?.Trim() ?? "";
        var key = ThrottleKey(pin);
        if (_throttle.IsLockedOut(key))
            return StatusCode(StatusCodes.Status423Locked, new { error = "Too many failed attempts. Try again in a few minutes." });

        var shopId = int.Parse(User.FindFirstValue("shopId") ?? "-1");
        if (shopId <= 0) return Ok(new { valid = false }); // superadmins have no shop scope

        var admins = await _db.Users
            .Where(u => u.ShopId == shopId && u.Role == "admin" && u.PinHash != null)
            .Select(u => u.PinHash!)
            .ToListAsync();
        var valid = admins.Any(hash => BCrypt.Net.BCrypt.Verify(pin, hash));

        if (valid) _throttle.Reset(key); else _throttle.RecordFailure(key);
        return Ok(new { valid });
    }

    // Staff list for PIN sign-in - display names + roles only, gated by a valid
    // shop code. No usernames, no emails; this is the "tap who you are" screen.
    [HttpPost("staff")]
    public async Task<ActionResult> Staff(StaffRequest request)
    {
        var shop = await _db.Shops.FirstOrDefaultAsync(s => s.Code == request.ShopCode.Trim().ToUpperInvariant());
        if (shop is null) return NotFound(new { error = "Shop not found." });
        if (!shop.IsActive)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "This shop is suspended. Contact the platform owner." });

        var staff = await _db.Users
            .Where(u => u.ShopId == shop.Id && u.Role != "superadmin")
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName, u.Role })
            .ToListAsync();
        return Ok(staff);
    }

    // ── Token refresh ────────────────────────────────────────────────────────

    [HttpPost("refresh")]
    public async Task<ActionResult<LoginResponse>> Refresh(RefreshRequest request)
    {
        // Cookie first (web UI); native app falls back to the token it stored
        // in device storage, because WebView cookies don't reliably survive an
        // app kill on all Android devices.
        var raw = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(raw)) raw = request.RefreshToken;
        if (string.IsNullOrEmpty(raw)) return Unauthorized();

        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == HashToken(raw));
        if (stored is null) return Unauthorized();

        // A revoked token being presented means it leaked. Burn the user's whole
        // chain so the theft doesn't keep paying off.
        if (stored.RevokedAtUtc is not null)
        {
            var active = await _db.RefreshTokens.Where(t => t.UserId == stored.UserId && t.RevokedAtUtc == null).ToListAsync();
            foreach (var t in active) t.RevokedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            Response.Cookies.Delete(RefreshCookieName);
            return Unauthorized();
        }

        if (stored.ExpiresAtUtc < DateTime.UtcNow)
        {
            Response.Cookies.Delete(RefreshCookieName);
            return Unauthorized();
        }

        var user = await _db.Users.FindAsync(stored.UserId);
        if (user is null) return Unauthorized();

        Shop? shop = null;
        if (user.ShopId is not null)
        {
            shop = await _db.Shops.FindAsync(user.ShopId);
            // Suspended tenant: burn the whole token chain and refuse to refresh.
            // The 15-minute access token is the last thing that works.
            if (shop is not null && !shop.IsActive)
            {
                var active = await _db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAtUtc == null).ToListAsync();
                foreach (var t in active) t.RevokedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                Response.Cookies.Delete(RefreshCookieName);
                return Unauthorized();
            }
        }

        // Rotate: revoke the used token and hand back a fresh one.
        stored.RevokedAtUtc = DateTime.UtcNow;
        var (replacement, rawRefresh) = CreateRefreshToken(user);
        stored.ReplacedByTokenId = replacement.Id;
        await _db.SaveChangesAsync();

        // Light cleanup — dead tokens past their expiry.
        await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.ExpiresAtUtc < DateTime.UtcNow.AddDays(-30))
            .ExecuteDeleteAsync();

        var access = CreateToken(user, shop);
        return Ok(BuildLoginResponse(access, user, shop, rawRefresh));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request)
    {
        var raw = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(raw)) raw = request.RefreshToken;
        if (!string.IsNullOrEmpty(raw))
        {
            var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == HashToken(raw));
            if (stored is not null)
            {
                stored.RevokedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
        Response.Cookies.Delete(RefreshCookieName);
        return Ok();
    }

    // ── Account ──────────────────────────────────────────────────────────────

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var userId = int.Parse(User.FindFirstValue("userId")!);
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect." });

        var policyError = PasswordPolicy.Validate(request.NewPassword);
        if (policyError is not null) return BadRequest(new { error = policyError });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _db.SaveChangesAsync();
        return Ok();
    }

    // Self-service account update: username, display name and (optionally)
    // password. The change is the account itself, so it applies system-wide.
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

        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            var policyError = PasswordPolicy.Validate(request.NewPassword);
            if (policyError is not null) return BadRequest(new { error = policyError });
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        }

        user.Username = username;
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
            user.DisplayName = request.DisplayName.Trim();

        await _db.SaveChangesAsync();
        return Ok(new { user.Username, user.DisplayName });
    }

    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private LoginResponse BuildLoginResponse(string token, AppUser user, Shop? shop, string? rawRefresh = null) =>
        new(token, user.Username, user.DisplayName, user.Role, user.ShopId, shop?.Name, shop?.Code,
            IsNativeClient() ? rawRefresh : null);

    // The Capacitor app identifies itself so the API hands back the raw refresh
    // token in the response body (for device storage). Web clients never get it.
    private bool IsNativeClient() => Request.Headers["X-Client"].ToString() == "native";

    // Lockout identity: caller's IP + the account they're trying, so failed
    // guesses for one user don't trip another user's lockout.
    private string ThrottleKey(string username) =>
        $"{HttpContext.Connection.RemoteIpAddress}|{username.Trim().ToLowerInvariant()}";

    private string IssueRefreshToken(AppUser user)
    {
        var raw = GenerateRefreshToken();
        // Cashier sessions last a week (not 12h) so the till survives overnight
        // and weekend closures without a re-login; rotation keeps sliding it
        // forward on every use, and replay-burn still kills a stolen token.
        var lifetime = user.Role == "cashier" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(30);

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashToken(raw),
            ExpiresAtUtc = DateTime.UtcNow.Add(lifetime),
            CreatedAtUtc = DateTime.UtcNow,
            ShopId = user.ShopId
        });
        _db.SaveChanges();
        SetRefreshCookie(raw, lifetime);
        return raw;
    }

    // Creates a fresh RefreshToken row, returns it (caller links it as the
    // replacement), and sets the cookie. The raw value never touches storage.
    private (RefreshToken Token, string Raw) CreateRefreshToken(AppUser user)
    {
        var raw = GenerateRefreshToken();
        // Same cashier lifetime as IssueRefreshToken: 7 days, sliding on rotate.
        var lifetime = user.Role == "cashier" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(30);
        var token = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashToken(raw),
            ExpiresAtUtc = DateTime.UtcNow.Add(lifetime),
            CreatedAtUtc = DateTime.UtcNow,
            ShopId = user.ShopId
        };
        _db.RefreshTokens.Add(token);
        SetRefreshCookie(raw, lifetime);
        return (token, raw);
    }

    private void SetRefreshCookie(string rawToken, TimeSpan lifetime)
    {
        // Prod is HTTPS and cross-site (web UI on vercel.app / native WebView on
        // http://localhost → https API), so the cookie needs SameSite=None+Secure
        // to be sent with the refresh call. Dev over http is same-site (Lax).
        var isHttps = Request.IsHttps;
        Response.Cookies.Append(RefreshCookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = isHttps ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(lifetime)
        });
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_')
            .TrimEnd('=');
    }

    private static string HashToken(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

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
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
