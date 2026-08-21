using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// PUBLIC, unauthenticated endpoints for customer self-enrolment. A shopper
// scans the shop's join QR, which opens the web signup page; the page reads the
// shop's branding here and posts the signup here. Everything is scoped to the
// shop's random JOIN TOKEN in the URL (never the human login code) - no auth,
// no shop claim - so all data access opts out of the per-shop query filter and
// sets ShopId explicitly from the resolved shop. Signup is rate-limited.
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicController : ControllerBase
{
    private readonly DevStackDataModel _db;
    public PublicController(DevStackDataModel db) => _db = db;

    public record SignupRequest(string? Name, string? Phone, string? Email, bool Consent);
    public record MemberLookupRequest(string? PhoneOrCode);

    // Resolve the shop behind a public join token (active shops only).
    private Task<Shop?> ShopByTokenAsync(string token, bool tracking = false)
    {
        var norm = (token ?? "").Trim().ToUpperInvariant();
        var q = tracking ? _db.Shops : _db.Shops.AsNoTracking();
        return q.FirstOrDefaultAsync(s => s.JoinToken == norm && s.IsActive);
    }

    // GET api/public/shop/{token} — branding + loyalty summary for the signup
    // page. Only active shops; the real login code is NEVER exposed.
    [HttpGet("shop/{token}")]
    public async Task<ActionResult> GetShop(string token)
    {
        var shop = await ShopByTokenAsync(token);
        if (shop is null) return NotFound(new { error = "We couldn't find that shop." });
        return Ok(new
        {
            shop.Name,
            shop.LogoUrl,
            shop.LoyaltyEnabled,
            shop.LoyaltyReward,
            shop.LoyaltyStampsRequired
        });
    }

    // POST api/public/member/{token} — returning customer signs in with their
    // phone number or personal loyalty code to view their card (name + points).
    [HttpPost("member/{token}")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> Member(string token, MemberLookupRequest request)
    {
        var shop = await ShopByTokenAsync(token);
        if (shop is null) return NotFound(new { error = "We couldn't find that shop." });

        var q = (request.PhoneOrCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Enter your phone number or loyalty code." });

        var upper = q.ToUpperInvariant();
        var customer = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ShopId == shop.Id && (c.Phone == q || c.LoyaltyCode == upper));
        if (customer is null)
            return NotFound(new { error = "No loyalty card found. Please sign up." });

        return Ok(new
        {
            name = customer.Name,
            loyaltyCode = customer.LoyaltyCode,
            shopName = shop.Name,
            reward = shop.LoyaltyReward,
            stampsRequired = shop.LoyaltyStampsRequired,
            stamps = customer.LoyaltyStamps
        });
    }

    // POST api/public/signup/{token} — enrol (or re-find) a customer for the shop
    // and return the loyalty code their personal QR encodes.
    [HttpPost("signup/{token}")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> Signup(string token, SignupRequest request)
    {
        var shop = await ShopByTokenAsync(token, tracking: true);
        if (shop is null) return NotFound(new { error = "We couldn't find that shop." });
        if (!shop.LoyaltyEnabled) return BadRequest(new { error = "This shop's loyalty programme isn't active." });

        var name = request.Name?.Trim();
        var phone = request.Phone?.Trim();
        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "Please enter your name." });
        if (string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "Please give a phone number or email." });
        if (!request.Consent) return BadRequest(new { error = "Please accept the terms to join." });

        // De-dupe by phone within the shop: a returning shopper gets their
        // existing card back rather than a duplicate.
        Customer? customer = null;
        if (!string.IsNullOrWhiteSpace(phone))
            customer = await _db.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.ShopId == shop.Id && c.Phone == phone);

        if (customer is null)
        {
            customer = new Customer
            {
                ShopId = shop.Id,
                Name = name,
                Phone = string.IsNullOrWhiteSpace(phone) ? null : phone,
                Email = string.IsNullOrWhiteSpace(email) ? null : email,
                SelfSignup = true,
                MarketingConsent = request.Consent,
                CreatedAt = DateTime.UtcNow.AddHours(2),
                LoyaltyCode = await GenerateUniqueLoyaltyCodeAsync()
            };
            _db.Customers.Add(customer);
        }
        else
        {
            // Returning customer: refresh contact details + ensure they have a code.
            customer.Name = name;
            if (!string.IsNullOrWhiteSpace(email)) customer.Email = email;
            customer.MarketingConsent = request.Consent;
            customer.LoyaltyCode ??= await GenerateUniqueLoyaltyCodeAsync();
        }

        await _db.SaveChangesAsync();
        return Ok(new
        {
            name = customer.Name,
            loyaltyCode = customer.LoyaltyCode,
            shopName = shop.Name,
            reward = shop.LoyaltyReward,
            stampsRequired = shop.LoyaltyStampsRequired,
            stamps = customer.LoyaltyStamps
        });
    }

    // Short, URL/QR-friendly, unambiguous code (no 0/O/1/I). Retries on the rare
    // collision against the unique index.
    private async Task<string> GenerateUniqueLoyaltyCodeAsync()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(10);
            var sb = new System.Text.StringBuilder(10);
            foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            var candidate = sb.ToString();
            if (!await _db.Customers.IgnoreQueryFilters().AnyAsync(c => c.LoyaltyCode == candidate))
                return candidate;
        }
        // Extremely unlikely fallback: timestamp-suffixed.
        return "L" + DateTime.UtcNow.Ticks.ToString("X");
    }
}
