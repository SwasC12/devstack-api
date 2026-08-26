using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Superadmin: manage BRANDS (franchises / loyalty programmes). A brand groups
// shops and owns the shared loyalty programme (rules + public join token +
// members). This is where loyalty rules are set now that they're franchise-wide.
[ApiController]
[Route("api/brands")]
[Authorize(Roles = "superadmin")]
public class BrandsController : ControllerBase
{
    private readonly DevStackDataModel _db;
    public BrandsController(DevStackDataModel db) => _db = db;

    public record CreateBrandRequest(string Name);
    public record UpdateBrandRequest(string? Name, bool? LoyaltyEnabled, int? LoyaltyStampsRequired, string? LoyaltyReward, string? LogoUrl);

    // GET api/brands — all brands with shop + loyalty-member counts.
    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        var brands = await _db.Brands.OrderBy(b => b.Name)
            .Select(b => new { b.Id, b.Name, b.JoinToken, b.LoyaltyEnabled, b.LoyaltyStampsRequired, b.LoyaltyReward, b.LogoUrl, b.CreatedAt })
            .ToListAsync();

        var shopCounts = (await _db.Shops.GroupBy(s => s.BrandId).Select(g => new { BrandId = g.Key, Count = g.Count() }).ToListAsync())
            .Where(x => x.BrandId != null).ToDictionary(x => x.BrandId!.Value, x => x.Count);
        var memberCounts = (await _db.LoyaltyMembers.GroupBy(m => m.BrandId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.Count);

        var result = brands.Select(b => new
        {
            b.Id, b.Name, b.JoinToken, b.LoyaltyEnabled, b.LoyaltyStampsRequired, b.LoyaltyReward, b.LogoUrl, b.CreatedAt,
            ShopCount = shopCounts.TryGetValue(b.Id, out var sc) ? sc : 0,
            MemberCount = memberCounts.TryGetValue(b.Id, out var mc) ? mc : 0
        });
        return Ok(result);
    }

    // POST api/brands — create a brand (loyalty starts off; a join token is issued).
    [HttpPost]
    public async Task<ActionResult> Create(CreateBrandRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest(new { error = "Brand name is required." });

        var brand = new Brand
        {
            Name = name,
            JoinToken = await GenerateUniqueTokenAsync(),
            CreatedAt = DateTime.UtcNow.AddHours(2)
        };
        _db.Brands.Add(brand);
        await _db.SaveChangesAsync();
        return Ok(new { brand.Id, brand.Name, brand.JoinToken, brand.LoyaltyEnabled, brand.LoyaltyStampsRequired, brand.LoyaltyReward, brand.LogoUrl });
    }

    // PUT api/brands/{id} — edit name + the shared loyalty rules + logo.
    [HttpPut("{id:int}")]
    public async Task<ActionResult> Update(int id, UpdateBrandRequest request)
    {
        var brand = await _db.Brands.FindAsync(id);
        if (brand is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Name)) brand.Name = request.Name.Trim();
        if (request.LoyaltyEnabled.HasValue) brand.LoyaltyEnabled = request.LoyaltyEnabled.Value;
        if (request.LoyaltyStampsRequired.HasValue) brand.LoyaltyStampsRequired = Math.Clamp(request.LoyaltyStampsRequired.Value, 2, 100);
        if (request.LoyaltyReward != null)
        {
            var r = request.LoyaltyReward.Trim();
            brand.LoyaltyReward = r.Length == 0 ? "Free item" : r;
        }
        if (request.LogoUrl != null) brand.LogoUrl = string.IsNullOrWhiteSpace(request.LogoUrl) ? null : request.LogoUrl.Trim();
        await _db.SaveChangesAsync();
        return Ok(new { brand.Id, brand.Name, brand.JoinToken, brand.LoyaltyEnabled, brand.LoyaltyStampsRequired, brand.LoyaltyReward, brand.LogoUrl });
    }

    // DELETE api/brands/{id} — remove a brand that has NO shops assigned (e.g.
    // left behind after its shops were deleted). Also clears the brand's loyalty
    // members, which are dead data once no shop uses the brand.
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var brand = await _db.Brands.FindAsync(id);
        if (brand is null) return NotFound();

        if (await _db.Shops.AnyAsync(s => s.BrandId == id))
            return BadRequest(new { error = "This brand still has shops. Move or delete those shops first." });

        await _db.LoyaltyMembers.Where(m => m.BrandId == id).ExecuteDeleteAsync();
        _db.Brands.Remove(brand);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // POST api/brands/{id}/regenerate-token — rotate the public join token.
    [HttpPost("{id:int}/regenerate-token")]
    public async Task<ActionResult> RegenerateToken(int id)
    {
        var brand = await _db.Brands.FindAsync(id);
        if (brand is null) return NotFound();
        brand.JoinToken = await GenerateUniqueTokenAsync();
        await _db.SaveChangesAsync();
        return Ok(new { brand.Id, brand.JoinToken });
    }

    private async Task<string> GenerateUniqueTokenAsync()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            var sb = new System.Text.StringBuilder(12);
            foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            var candidate = sb.ToString();
            if (!await _db.Brands.AnyAsync(b => b.JoinToken == candidate)) return candidate;
        }
        return "B" + DateTime.UtcNow.Ticks.ToString("X");
    }
}
