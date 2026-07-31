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

    public ShopsController(DevStackDataModel db) => _db = db;

    public record CreateShopRequest(string Name, string Code, string AdminUsername, string AdminPassword, string AdminDisplayName);
    public record UpdateShopRequest(string Name, string? LogoUrl);

    // GET api/shops — superadmin: list all shops.
    [Authorize(Roles = "superadmin")]
    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        var shops = await _db.Shops
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Code, s.CreatedAt })
            .ToListAsync();
        return Ok(shops);
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

        return CreatedAtAction(nameof(GetAll), new { id = shop.Id }, new { shop.Id, shop.Name, shop.Code, shop.CreatedAt });
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

        return Ok(new { shop.Id, shop.Name, shop.Code, shop.LogoUrl });
    }

    // PUT api/shops/me — shop admin (owner): update the current shop's branding.
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
        await _db.SaveChangesAsync();

        return Ok(new { shop.Id, shop.Name, shop.Code, shop.LogoUrl });
    }
}
