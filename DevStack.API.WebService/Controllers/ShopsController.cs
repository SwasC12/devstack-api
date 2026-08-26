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
    // New fields are nullable so a partial update (e.g. the POS saving only
    // branding) never silently resets loyalty / receipt settings — only fields
    // actually sent are applied.
    public record UpdateShopRequest(
        string Name, string? LogoUrl, string? ReceiptQrUrl, string? KitchenUrl = null,
        bool? LoyaltyEnabled = null, int? LoyaltyStampsRequired = null, string? LoyaltyReward = null,
        string? ReceiptHeader = null, string? ReceiptFooter = null,
        bool? ReceiptShowVat = null, bool? ReceiptShowQr = null, bool? ReceiptShowCashier = null,
        bool? ReceiptShowLogo = null);
    public record SetShopStatusRequest(bool IsActive);
    public record UpdateOwnerRequest(string? OwnerEmail, string? OwnerPhone);
    public record EditShopRequest(string Name, string Code);
    public record AddStaffRequest(string Username, string Password, string DisplayName, string Role, decimal? WageRate);
    public record UpdateStaffRequest(string? DisplayName, string? Role, decimal? WageRate);

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

        // Set-based aggregates instead of per-shop correlated subqueries: this
        // used to run 6 subqueries FOR EACH shop row (the slow platform page).
        // Now it's a handful of grouped queries stitched together in memory.
        var baseShops = await _db.Shops
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Code, s.IsActive, s.IsArchived, s.CreatedAt, s.OwnerEmail, s.OwnerPhone })
            .ToListAsync();

        var userCounts = (await _db.Users
            .GroupBy(u => u.ShopId)
            .Select(g => new { ShopId = g.Key, Count = g.Count() })
            .ToListAsync())
            .ToDictionary(x => x.ShopId, x => x.Count);

        var orderAgg = (await _db.Orders.IgnoreQueryFilters()
            .GroupBy(o => o.ShopId)
            .Select(g => new
            {
                ShopId = g.Key,
                Count = g.Count(),
                Revenue = g.Sum(o => o.VoidedAt == null ? o.Total : 0m),
                LastOrderAt = (DateTime?)g.Max(o => o.CreatedAt)
            })
            .ToListAsync())
            .ToDictionary(x => x.ShopId);

        // One check-in row per shop in practice; group + take latest defensively.
        var checkinMap = (await _db.AppCheckins.AsNoTracking().ToListAsync())
            .GroupBy(c => c.ShopId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.LastSeenAtUtc).First());

        var shops = baseShops.Select(s =>
        {
            orderAgg.TryGetValue(s.Id, out var o);
            checkinMap.TryGetValue(s.Id, out var ci);
            return new
            {
                s.Id, s.Name, s.Code, s.IsActive, s.IsArchived, s.CreatedAt, s.OwnerEmail, s.OwnerPhone,
                UserCount = userCounts.TryGetValue(s.Id, out var uc) ? uc : 0,
                OrderCount = o?.Count ?? 0,
                Revenue = o?.Revenue ?? 0m,
                LastOrderAt = o?.LastOrderAt,
                AppVersion = ci?.Version,
                LastSeenAt = (DateTime?)(ci?.LastSeenAtUtc)
            };
        }).ToList();

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

        var shop = new Shop { Name = name, Code = code, CreatedAt = DateTime.UtcNow.AddHours(2), JoinToken = await GenerateUniqueJoinTokenAsync() };
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

    // PUT api/shops/{id} — superadmin: rename a shop / change its login code.
    [Authorize(Roles = "superadmin")]
    [HttpPut("{id:int}")]
    public async Task<ActionResult> EditShop(int id, EditShopRequest request)
    {
        var shop = await _db.Shops.FindAsync(id);
        if (shop is null) return NotFound();

        var name = request.Name?.Trim();
        var code = request.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(code))
            return BadRequest(new { error = "Shop name and code are required." });
        if (await _db.Shops.AnyAsync(s => s.Code == code && s.Id != id))
            return BadRequest(new { error = $"A shop with code '{code}' already exists." });

        var oldCode = shop.Code;
        shop.Name = name;
        shop.Code = code;
        _db.PlatformEvents.Add(new PlatformEvent { Type = "shop_edited", ShopId = shop.Id, Detail = $"{shop.Name} — code {oldCode} → {code}", CreatedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        return Ok(new { shop.Id, shop.Name, shop.Code, shop.IsActive, shop.IsArchived });
    }

    // POST api/shops/{id}/archive — superadmin: safe soft-delete. Hidden from the
    // list and blocked from signing in (archived shops are also inactive), but no
    // data is destroyed. Restorable.
    [Authorize(Roles = "superadmin")]
    [HttpPost("{id:int}/archive")]
    public async Task<ActionResult> Archive(int id)
    {
        var shop = await _db.Shops.FindAsync(id);
        if (shop is null) return NotFound();
        shop.IsArchived = true;
        shop.IsActive = false;
        _db.PlatformEvents.Add(new PlatformEvent { Type = "shop_archived", ShopId = shop.Id, Detail = $"{shop.Name} archived", CreatedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        return Ok(new { shop.Id, shop.IsArchived, shop.IsActive });
    }

    [Authorize(Roles = "superadmin")]
    [HttpPost("{id:int}/restore")]
    public async Task<ActionResult> Restore(int id)
    {
        var shop = await _db.Shops.FindAsync(id);
        if (shop is null) return NotFound();
        shop.IsArchived = false;
        shop.IsActive = true;
        _db.PlatformEvents.Add(new PlatformEvent { Type = "shop_restored", ShopId = shop.Id, Detail = $"{shop.Name} restored", CreatedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        return Ok(new { shop.Id, shop.IsArchived, shop.IsActive });
    }

    // DELETE api/shops/{id} — superadmin: PERMANENTLY delete a shop. Only allowed
    // for shops with NO sales history (test/abandoned shops), so real financial
    // records can never be destroyed by accident — those must be archived instead.
    [Authorize(Roles = "superadmin")]
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> DeleteShop(int id)
    {
        var shop = await _db.Shops.FindAsync(id);
        if (shop is null) return NotFound();

        if (await _db.Orders.IgnoreQueryFilters().AnyAsync(o => o.ShopId == id))
            return BadRequest(new { error = "This shop has sales history and can't be deleted. Archive it instead." });

        await using var tx = await _db.Database.BeginTransactionAsync();
        // Gather child-table parent ids first (these tables hang off MenuItem /
        // ModifierGroup / PurchaseOrder, not ShopId directly). No orders exist,
        // so all order-child tables are already empty and need no clearing.
        var userIds = await _db.Users.IgnoreQueryFilters().Where(u => u.ShopId == id).Select(u => u.Id).ToListAsync();
        var menuItemIds = await _db.MenuItems.IgnoreQueryFilters().Where(m => m.ShopId == id).Select(m => m.Id).ToListAsync();
        var groupIds = await _db.ModifierGroups.Where(g => menuItemIds.Contains(g.MenuItemId)).Select(g => g.Id).ToListAsync();
        var poIds = await _db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.ShopId == id).Select(p => p.Id).ToListAsync();

        await _db.RefreshTokens.Where(t => userIds.Contains(t.UserId)).ExecuteDeleteAsync();
        await _db.PushTokens.Where(t => userIds.Contains(t.UserId)).ExecuteDeleteAsync();
        await _db.Notifications.Where(n => n.ShopId == id).ExecuteDeleteAsync();
        await _db.Modifiers.Where(m => groupIds.Contains(m.ModifierGroupId)).ExecuteDeleteAsync();
        await _db.ModifierGroups.Where(g => menuItemIds.Contains(g.MenuItemId)).ExecuteDeleteAsync();
        await _db.MenuSizes.Where(s => menuItemIds.Contains(s.MenuItemId)).ExecuteDeleteAsync();
        await _db.RecipeLines.Where(r => menuItemIds.Contains(r.MenuItemId)).ExecuteDeleteAsync();
        await _db.PurchaseOrderLines.Where(l => poIds.Contains(l.PurchaseOrderId)).ExecuteDeleteAsync();
        await _db.MenuItems.IgnoreQueryFilters().Where(m => m.ShopId == id).ExecuteDeleteAsync();
        await _db.Categories.IgnoreQueryFilters().Where(c => c.ShopId == id).ExecuteDeleteAsync();
        await _db.Discounts.IgnoreQueryFilters().Where(d => d.ShopId == id).ExecuteDeleteAsync();
        await _db.Customers.IgnoreQueryFilters().Where(c => c.ShopId == id).ExecuteDeleteAsync();
        await _db.Expenses.IgnoreQueryFilters().Where(e => e.ShopId == id).ExecuteDeleteAsync();
        await _db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.ShopId == id).ExecuteDeleteAsync();
        await _db.Suppliers.IgnoreQueryFilters().Where(s => s.ShopId == id).ExecuteDeleteAsync();
        await _db.Shifts.IgnoreQueryFilters().Where(s => s.ShopId == id).ExecuteDeleteAsync();
        await _db.AuditLog.IgnoreQueryFilters().Where(a => a.ShopId == id).ExecuteDeleteAsync();
        await _db.AppCheckins.Where(c => c.ShopId == id).ExecuteDeleteAsync();
        await _db.Users.IgnoreQueryFilters().Where(u => u.ShopId == id).ExecuteDeleteAsync();
        var name = shop.Name;
        _db.Shops.Remove(shop);
        await _db.SaveChangesAsync();
        _db.PlatformEvents.Add(new PlatformEvent { Type = "shop_deleted", ShopId = null, Detail = $"{name} ({shop.Code}) permanently deleted", CreatedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok(new { deleted = true });
    }

    // ── Staff management for ANY shop (superadmin) ────────────────────────────
    private static readonly string[] StaffRoles = { "admin", "manager", "cashier" };

    [Authorize(Roles = "superadmin")]
    [HttpPost("{id:int}/users")]
    public async Task<ActionResult> AddStaff(int id, AddStaffRequest request)
    {
        var shop = await _db.Shops.FindAsync(id);
        if (shop is null) return NotFound();

        var username = request.Username?.Trim();
        var displayName = request.DisplayName?.Trim();
        var role = request.Role?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(displayName))
            return BadRequest(new { error = "Username and display name are required." });
        if (!StaffRoles.Contains(role)) return BadRequest(new { error = "Role must be admin, manager or cashier." });
        var policyError = PasswordPolicy.Validate(request.Password);
        if (policyError is not null) return BadRequest(new { error = policyError });
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.ShopId == id && u.Username == username))
            return BadRequest(new { error = $"'{username}' already exists in this shop." });

        var user = new AppUser
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            DisplayName = displayName,
            Role = role!,
            ShopId = id,
            WageRate = request.WageRate
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return Ok(new { user.Id, user.Username, user.DisplayName, user.Role, user.WageRate });
    }

    [Authorize(Roles = "superadmin")]
    [HttpPut("{id:int}/users/{userId:int}")]
    public async Task<ActionResult> UpdateStaff(int id, int userId, UpdateStaffRequest request)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId && u.ShopId == id);
        if (user is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(request.DisplayName)) user.DisplayName = request.DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var role = request.Role.Trim().ToLowerInvariant();
            if (!StaffRoles.Contains(role)) return BadRequest(new { error = "Invalid role." });
            user.Role = role;
        }
        if (request.WageRate.HasValue) user.WageRate = request.WageRate.Value < 0 ? null : request.WageRate.Value;
        await _db.SaveChangesAsync();
        return Ok(new { user.Id, user.Username, user.DisplayName, user.Role, user.WageRate });
    }

    [Authorize(Roles = "superadmin")]
    [HttpPost("{id:int}/users/{userId:int}/reset-password")]
    public async Task<ActionResult> ResetStaffPassword(int id, int userId)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId && u.ShopId == id);
        if (user is null) return NotFound();
        var password = GeneratePassword();
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        await _db.SaveChangesAsync();
        return Ok(new { password, username = user.Username, displayName = user.DisplayName });
    }

    [Authorize(Roles = "superadmin")]
    [HttpDelete("{id:int}/users/{userId:int}")]
    public async Task<ActionResult> DeleteStaff(int id, int userId)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId && u.ShopId == id);
        if (user is null) return NotFound();
        // Never leave a shop with no admin.
        if (user.Role == "admin")
        {
            var adminCount = await _db.Users.IgnoreQueryFilters().CountAsync(u => u.ShopId == id && u.Role == "admin");
            if (adminCount <= 1) return BadRequest(new { error = "Can't remove the shop's only admin." });
        }
        await _db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync();
        await _db.PushTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync();
        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
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

        // Backfill: shops created before join tokens existed get one on first load.
        if (string.IsNullOrEmpty(shop.JoinToken))
        {
            shop.JoinToken = await GenerateUniqueJoinTokenAsync();
            await _db.SaveChangesAsync();
        }

        return Ok(ShopMe(shop));
    }

    // POST api/shops/me/regenerate-join-token — shop admin: rotate the public
    // loyalty join token. Any previously printed sign-up QR / poster stops
    // working immediately, so this is the fix if one leaks or is misused.
    [Authorize(Roles = "admin")]
    [HttpPost("me/regenerate-join-token")]
    public async Task<ActionResult> RegenerateJoinToken()
    {
        var shopId = int.Parse(User.FindFirstValue("shopId") ?? "-1");
        if (shopId <= 0) return NotFound();

        var shop = await _db.Shops.FindAsync(shopId);
        if (shop is null) return NotFound();

        shop.JoinToken = await GenerateUniqueJoinTokenAsync();
        await _db.SaveChangesAsync();
        return Ok(ShopMe(shop));
    }

    // Unguessable, QR-friendly join token (no ambiguous chars). 12 chars over a
    // 31-symbol alphabet ≈ 59 bits — not feasible to enumerate. Retries on the
    // (astronomically rare) collision against the unique index.
    private async Task<string> GenerateUniqueJoinTokenAsync()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            var sb = new System.Text.StringBuilder(12);
            foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            var candidate = sb.ToString();
            if (!await _db.Shops.AnyAsync(s => s.JoinToken == candidate))
                return candidate;
        }
        return "J" + DateTime.UtcNow.Ticks.ToString("X");
    }

    // PUT api/shops/me - shop admin (owner): update the current shop's branding,
    // loyalty and receipt settings.
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

        // Loyalty (only apply what was sent).
        if (request.LoyaltyEnabled.HasValue) shop.LoyaltyEnabled = request.LoyaltyEnabled.Value;
        if (request.LoyaltyStampsRequired.HasValue) shop.LoyaltyStampsRequired = Math.Clamp(request.LoyaltyStampsRequired.Value, 2, 100);
        if (request.LoyaltyReward != null)
        {
            var r = request.LoyaltyReward.Trim();
            shop.LoyaltyReward = r.Length == 0 ? "Free item" : r;
        }

        // Receipt customisation ("" clears a text field; absent = unchanged).
        if (request.ReceiptHeader != null) shop.ReceiptHeader = request.ReceiptHeader.Trim().Length == 0 ? null : request.ReceiptHeader.Trim();
        if (request.ReceiptFooter != null) shop.ReceiptFooter = request.ReceiptFooter.Trim().Length == 0 ? null : request.ReceiptFooter.Trim();
        if (request.ReceiptShowVat.HasValue) shop.ReceiptShowVat = request.ReceiptShowVat.Value;
        if (request.ReceiptShowQr.HasValue) shop.ReceiptShowQr = request.ReceiptShowQr.Value;
        if (request.ReceiptShowCashier.HasValue) shop.ReceiptShowCashier = request.ReceiptShowCashier.Value;
        if (request.ReceiptShowLogo.HasValue) shop.ReceiptShowLogo = request.ReceiptShowLogo.Value;

        await _db.SaveChangesAsync();
        return Ok(ShopMe(shop));
    }

    // Shared shape for GET/PUT me: branding + loyalty + receipt settings.
    private static object ShopMe(Shop s) => new
    {
        s.Id, s.Name, s.Code, s.JoinToken, s.LogoUrl, s.ReceiptQrUrl, s.KitchenUrl,
        s.LoyaltyEnabled, s.LoyaltyStampsRequired, s.LoyaltyReward,
        s.ReceiptHeader, s.ReceiptFooter, s.ReceiptShowVat, s.ReceiptShowQr, s.ReceiptShowCashier, s.ReceiptShowLogo
    };
}
