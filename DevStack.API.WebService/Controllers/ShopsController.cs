using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Mixed-audience controller:
//   - api/shops         — platform provisioning, superadmin only
//   - api/shops/me      — the CURRENT shop's branding; GET for any logged-in
//                         shop user (the POS shows the logo), PUT for the shop
//                         admin (owner customisation)
[ApiController]
[Route("api/[controller]")]
public class ShopsController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly EmailService _email;

    public ShopsController(DevStackDataModel db, EmailService email)
    {
        _db = db;
        _email = email;
    }

    public record CreateShopRequest(string Name, string Code, string AdminUsername, string AdminPassword, string AdminDisplayName, string? OwnerEmail = null);
    public record UpdateShopRequest(string Name, string? LogoUrl, string? ReceiptQrUrl, string? KitchenUrl = null);
    public record SetShopStatusRequest(bool IsActive);
    public record UpdateOwnerRequest(string? OwnerEmail, string? OwnerPhone);

    // GET api/shops — superadmin: list all shops with lightweight usage stats.
    // Orders carry a global query filter, so cross-shop counting must opt out
    // (superadmin has no shop scope — without IgnoreQueryFilters every count
    // would silently be scoped to the fallback/first shop).
    [Authorize(Roles = "superadmin")]
    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        // The single "current" published version, so the caller can flag shops
        // running something older (an at-risk signal on the platform dashboard).
        var currentVersion = await _db.AppReleases
            .Where(r => r.IsCurrent)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => r.Version)
            .FirstOrDefaultAsync();

        var shops = await _db.Shops
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Code,
                s.IsActive,
                s.CreatedAt,
                s.OwnerEmail,
                s.OwnerPhone,
                UserCount = _db.Users.Count(u => u.ShopId == s.Id),
                OrderCount = _db.Orders.IgnoreQueryFilters().Count(o => o.ShopId == s.Id),
                // Lifetime revenue (excludes voided) — feeds the richer table.
                Revenue = _db.Orders.IgnoreQueryFilters()
                    .Where(o => o.ShopId == s.Id && o.VoidedAt == null)
                    .Sum(o => (decimal?)o.Total) ?? 0m,
                LastOrderAt = _db.Orders.IgnoreQueryFilters()
                    .Where(o => o.ShopId == s.Id)
                    .OrderByDescending(o => o.CreatedAt)
                    .Select(o => (DateTime?)o.CreatedAt)
                    .FirstOrDefault(),
                // Installed app version + last check-in (from AppCheckins).
                AppVersion = _db.AppCheckins
                    .Where(c => c.ShopId == s.Id)
                    .OrderByDescending(c => c.LastSeenAtUtc)
                    .Select(c => c.Version)
                    .FirstOrDefault(),
                LastSeenAt = _db.AppCheckins
                    .Where(c => c.ShopId == s.Id)
                    .OrderByDescending(c => c.LastSeenAtUtc)
                    .Select(c => (DateTime?)c.LastSeenAtUtc)
                    .FirstOrDefault()
            })
            .ToListAsync();
        return Ok(new { currentVersion, shops });
    }

    // GET api/shops/{id}/detail — superadmin: everything for one shop's drawer:
    // headline stats, its staff, and its most recent orders. All cross-shop
    // reads use IgnoreQueryFilters (superadmin has no shop scope). Reads only.
    [Authorize(Roles = "superadmin")]
    [HttpGet("{id:int}/detail")]
    public async Task<ActionResult> Detail(int id)
    {
        var shop = await _db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (shop is null) return NotFound();

        var todayStart = DateTime.UtcNow.AddHours(2).Date; // SAST, matches overview
        var orders = _db.Orders.IgnoreQueryFilters().Where(o => o.ShopId == id);

        var orderCount = await orders.CountAsync();
        var revenue = (await orders.Where(o => o.VoidedAt == null).SumAsync(o => (decimal?)o.Total)) ?? 0m;
        var ordersToday = await orders.CountAsync(o => o.VoidedAt == null && o.CreatedAt >= todayStart);
        var revenueToday = (await orders.Where(o => o.VoidedAt == null && o.CreatedAt >= todayStart).SumAsync(o => (decimal?)o.Total)) ?? 0m;

        var users = await _db.Users.AsNoTracking()
            .Where(u => u.ShopId == id)
            .OrderBy(u => u.Role).ThenBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, HasPin = u.PinHash != null })
            .ToListAsync();

        var recentOrders = await orders.AsNoTracking()
            .OrderByDescending(o => o.CreatedAt)
            .Take(10)
            .Select(o => new { o.Id, o.CreatedAt, o.Total, o.PaymentMethod, IsVoided = o.VoidedAt != null })
            .ToListAsync();

        var checkin = await _db.AppCheckins.AsNoTracking()
            .Where(c => c.ShopId == id)
            .OrderByDescending(c => c.LastSeenAtUtc)
            .Select(c => new { c.Version, c.LastSeenAtUtc })
            .FirstOrDefaultAsync();

        return Ok(new
        {
            shop = new { shop.Id, shop.Name, shop.Code, shop.IsActive, shop.CreatedAt, shop.OwnerEmail, shop.OwnerPhone },
            stats = new
            {
                orderCount,
                revenue,
                ordersToday,
                revenueToday,
                userCount = users.Count,
                avgOrder = orderCount > 0 ? Math.Round(revenue / orderCount, 2) : 0m
            },
            appVersion = checkin?.Version,
            lastSeenAt = checkin?.LastSeenAtUtc,
            users,
            recentOrders
        });
    }

    // POST api/shops — superadmin: create a shop and its first admin.
    [Authorize(Roles = "superadmin")]
    [HttpPost]
    public async Task<ActionResult> Create(CreateShopRequest request)
    {
        var name = request.Name.Trim();
        var code = request.Code.Trim().ToUpperInvariant();
        var adminUsername = request.AdminUsername.Trim();
        var adminDisplayName = request.AdminDisplayName.Trim();

        if (name.Length == 0 || code.Length == 0)
            return BadRequest(new { error = "Shop name and code are required." });
        if (adminUsername.Length == 0 || string.IsNullOrEmpty(request.AdminPassword))
            return BadRequest(new { error = "The first admin needs a username and password." });

        var policyError = PasswordPolicy.Validate(request.AdminPassword);
        if (policyError is not null) return BadRequest(new { error = policyError });

        if (await _db.Shops.AnyAsync(s => s.Code == code))
            return BadRequest(new { error = $"A shop with code '{code}' already exists." });

        var shop = new Shop { Name = name, Code = code, CreatedAt = DateTime.UtcNow.AddHours(2) };
        _db.Shops.Add(shop);
        await _db.SaveChangesAsync();

        var admin = new AppUser
        {
            Username = adminUsername,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.AdminPassword),
            DisplayName = adminDisplayName,
            Role = "admin",
            ShopId = shop.Id
        };
        _db.Users.Add(admin);
        await _db.SaveChangesAsync();

        _db.PlatformEvents.Add(new PlatformEvent
        {
            Type = "shop_created",
            ShopId = shop.Id,
            Detail = $"{shop.Name} ({shop.Code}) — created with admin '{admin.Username}'",
            CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Welcome email: only when SMTP is configured and an owner email was
        // given. Best-effort - a mail failure never fails shop creation (the
        // one-time password is still returned in the response).
        var ownerEmail = request.OwnerEmail?.Trim();
        if (!string.IsNullOrEmpty(ownerEmail) && _email.IsConfigured)
        {
            try
            {
                await _email.SendAsync(ownerEmail,
                    $"Welcome to CoffeeShop Pro - {shop.Name} is ready",
                    $"Hi {adminDisplayName},\n\n" +
                    $"Your shop '{shop.Name}' ({shop.Code}) is live.\n\n" +
                    $"Sign in at the POS app with:\n" +
                    $"  Shop code: {shop.Code}\n" +
                    $"  Username:  {adminUsername}\n" +
                    $"  Password:  {request.AdminPassword}\n\n" +
                    $"Change the password after your first login. - The CoffeeShop Pro team");

                _db.PlatformEvents.Add(new PlatformEvent
                {
                    Type = "email_sent",
                    ShopId = shop.Id,
                    Detail = $"Welcome email → {ownerEmail}",
                    CreatedAtUtc = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
            }
            catch
            {
                // ignored - the response below still carries the credentials
            }
        }

        return CreatedAtAction(nameof(GetAll), new { id = shop.Id }, new { shop.Id, shop.Name, shop.Code, shop.IsActive, shop.CreatedAt });
    }

    // PUT api/shops/{id}/status — superadmin: suspend or reactivate a tenant.
    // Suspension is enforced at the door: login, PIN login and token refresh all
    // refuse a suspended shop, so existing sessions die within the access-token
    // lifetime (15 min) and nothing new can start.
    [Authorize(Roles = "superadmin")]
    [HttpPut("{id:int}/status")]
    public async Task<ActionResult> SetStatus(int id, SetShopStatusRequest request)
    {
        var shop = await _db.Shops.FindAsync(id);
        if (shop is null) return NotFound();

        shop.IsActive = request.IsActive;
        await _db.SaveChangesAsync();

        _db.PlatformEvents.Add(new PlatformEvent
        {
            Type = request.IsActive ? "shop_activated" : "shop_suspended",
            ShopId = shop.Id,
            Detail = $"{shop.Name} {(request.IsActive ? "activated" : "suspended")} by superadmin",
            CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok(new { shop.Id, shop.Name, shop.Code, shop.IsActive });
    }

    // PUT api/shops/{id}/owner - superadmin: owner contact details, used
    // later for direct emails to shop owners. Email/phone stay optional.
    [Authorize(Roles = "superadmin")]
    [HttpPut("{id:int}/owner")]
    public async Task<ActionResult> UpdateOwner(int id, UpdateOwnerRequest request)
    {
        var shop = await _db.Shops.FindAsync(id);
        if (shop is null) return NotFound();

        shop.OwnerEmail = string.IsNullOrWhiteSpace(request.OwnerEmail) ? null : request.OwnerEmail.Trim();
        shop.OwnerPhone = string.IsNullOrWhiteSpace(request.OwnerPhone) ? null : request.OwnerPhone.Trim();
        await _db.SaveChangesAsync();

        return Ok(new { shop.Id, shop.Name, shop.OwnerEmail, shop.OwnerPhone });
    }

    // POST api/shops/{id}/reset-admin-password - superadmin: set a fresh,
    // random password for the shop's first admin and return it ONCE. There is
    // no email system, so the caller (platform owner) relays it to the owner.
    // The returned password is intentionally not stored anywhere — only its
    // bcrypt hash lands in the database.
    //secrete
    [Authorize(Roles = "superadmin")]
    [HttpPost("{id:int}/reset-admin-password")]
    public async Task<ActionResult> ResetAdminPassword(int id)
    {
        var shop = await _db.Shops.FindAsync(id);
        if (shop is null) return NotFound();

        var admin = await _db.Users
            .Where(u => u.ShopId == shop.Id && u.Role == "admin")
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync();
        if (admin is null)
            return BadRequest(new { error = $"Shop '{shop.Name}' has no admin user to reset." });

        var password = GeneratePassword();
        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        await _db.SaveChangesAsync();

        _db.PlatformEvents.Add(new PlatformEvent
        {
            Type = "password_reset",
            ShopId = shop.Id,
            Detail = $"{shop.Name} — admin '{admin.Username}' password reset",
            CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        return Ok(new { password, username = admin.Username, displayName = admin.DisplayName });
    }

    // Random 18-char password, same rules as the platform's one-time seeds.
    private static string GeneratePassword(int length = 18)
    {
        const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        var sb = new System.Text.StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(chars[bytes[i] % chars.Length]);
        return sb.ToString();
    }

    // GET api/shops/me — any logged-in shop user: the current shop's branding.
    // The POS calls this so cashiers see the owner's logo too.
    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult> GetMe()
    {
        var shopId = int.Parse(User.FindFirstValue("shopId") ?? "-1");
        if (shopId <= 0) return NotFound(); // superadmins have no shop

        var shop = await _db.Shops.FindAsync(shopId);
        if (shop is null) return NotFound();

        return Ok(new { shop.Id, shop.Name, shop.Code, shop.LogoUrl, shop.ReceiptQrUrl, shop.KitchenUrl });
    }

    // PUT api/shops/me - shop admin (owner): update the current shop's branding.
    [Authorize(Roles = "admin")]
    [HttpPut("me")]
    public async Task<ActionResult> UpdateMe(UpdateShopRequest request)
    {
        var shopId = int.Parse(User.FindFirstValue("shopId") ?? "-1");
        if (shopId <= 0) return NotFound();

        var shop = await _db.Shops.FindAsync(shopId);
        if (shop is null) return NotFound();

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "Shop name cannot be empty." });

        shop.Name = name;
        shop.LogoUrl = request.LogoUrl?.Trim();
        shop.ReceiptQrUrl = string.IsNullOrWhiteSpace(request.ReceiptQrUrl) ? null : request.ReceiptQrUrl.Trim();
        shop.KitchenUrl = string.IsNullOrWhiteSpace(request.KitchenUrl) ? null : request.KitchenUrl.Trim();
        await _db.SaveChangesAsync();

        return Ok(new { shop.Id, shop.Name, shop.Code, shop.LogoUrl, shop.ReceiptQrUrl, shop.KitchenUrl });
    }
}
