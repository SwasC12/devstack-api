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

    public record SignupRequest(string? Name, string? Phone, string? Email, bool Consent, string? Password);
    public record MemberLookupRequest(string? Identifier, string? Password);
    public record MemberResumeRequest(string? LoyaltyCode);

    private const int MinPasswordLength = 6;

    // Card payload shown to the signed-in customer.
    private static object Card(Customer c, Shop s) => new
    {
        name = c.Name,
        loyaltyCode = c.LoyaltyCode,
        shopName = s.Name,
        reward = s.LoyaltyReward,
        stampsRequired = s.LoyaltyStampsRequired,
        stamps = c.LoyaltyStamps
    };

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

    // POST api/public/member/{token} — returning customer SIGNS IN with their
    // phone / email / loyalty code AND their password. The password is what
    // stops someone who merely knows a phone number from opening the card.
    [HttpPost("member/{token}")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> Member(string token, MemberLookupRequest request)
    {
        var shop = await ShopByTokenAsync(token, tracking: true);
        if (shop is null) return NotFound(new { error = "We couldn't find that shop." });

        var q = (request.Identifier ?? "").Trim();
        var password = request.Password ?? "";
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Enter your phone, email or loyalty code." });
        if (string.IsNullOrEmpty(password))
            return BadRequest(new { error = "Enter your password." });

        var upper = q.ToUpperInvariant();
        var lower = q.ToLowerInvariant();
        var qNorm = NormalizePhone(q);
        var customer = await _db.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ShopId == shop.Id &&
                (c.LoyaltyCode == upper
                 || (c.Email != null && c.Email.ToLower() == lower)
                 || (c.Phone != null && c.Phone.Replace(" ", "").Replace("-", "") == qNorm)));
        if (customer is null)
            return NotFound(new { error = "No loyalty card found. Please sign up." });

        if (string.IsNullOrEmpty(customer.LoyaltyPasswordHash))
        {
            // Legacy account created before passwords: first successful sign-in
            // sets the password (a one-time claim for the transition window).
            if (password.Length < MinPasswordLength)
                return BadRequest(new { error = $"Set a password of at least {MinPasswordLength} characters." });
            customer.LoyaltyPasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
            await _db.SaveChangesAsync();
        }
        else if (!BCrypt.Net.BCrypt.Verify(password, customer.LoyaltyPasswordHash))
        {
            return Unauthorized(new { error = "Incorrect password." });
        }

        return Ok(Card(customer, shop));
    }

    // POST api/public/member-resume/{token} — refresh the card for a device that
    // already signed in, using the loyalty code kept in that browser. No password
    // (the random code is the device's own session token); read-only.
    [HttpPost("member-resume/{token}")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> MemberResume(string token, MemberResumeRequest request)
    {
        var shop = await ShopByTokenAsync(token);
        if (shop is null) return NotFound(new { error = "We couldn't find that shop." });

        var code = (request.LoyaltyCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code)) return BadRequest(new { error = "Missing code." });

        var customer = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ShopId == shop.Id && c.LoyaltyCode == code);
        if (customer is null) return NotFound(new { error = "Card not found." });

        return Ok(Card(customer, shop));
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
        var password = request.Password ?? "";
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "Please enter your name." });
        if (string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "Please give a phone number or email." });
        if (password.Length < MinPasswordLength)
            return BadRequest(new { error = $"Please set a password of at least {MinPasswordLength} characters." });
        if (!request.Consent) return BadRequest(new { error = "Please accept the terms to join." });

        // Format validation (mirrors the client, but the server is the authority).
        if (!string.IsNullOrWhiteSpace(phone) && !IsValidPhone(phone))
            return BadRequest(new { error = "Please enter a valid phone number." });
        if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
            return BadRequest(new { error = "Please enter a valid email address." });

        // One membership per phone / email: a returning shopper must sign in
        // (Check my points), not create a second card. Phone is matched
        // space/dash-insensitively; email case-insensitively.
        var phoneNorm = NormalizePhone(phone);
        var emailNorm = email?.ToLowerInvariant();
        var dup = await _db.Customers.IgnoreQueryFilters().AnyAsync(c => c.ShopId == shop.Id &&
            ((phoneNorm != null && c.Phone != null && c.Phone.Replace(" ", "").Replace("-", "") == phoneNorm)
             || (emailNorm != null && c.Email != null && c.Email.ToLower() == emailNorm)));
        if (dup)
            return Conflict(new { error = "This email/number is already in use." });

        var customer = new Customer
        {
            ShopId = shop.Id,
            Name = name,
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone,
            Email = string.IsNullOrWhiteSpace(email) ? null : email,
            SelfSignup = true,
            MarketingConsent = request.Consent,
            CreatedAt = DateTime.UtcNow.AddHours(2),
            LoyaltyCode = await GenerateUniqueLoyaltyCodeAsync(),
            LoyaltyPasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        };
        _db.Customers.Add(customer);

        await _db.SaveChangesAsync();
        return Ok(Card(customer, shop));
    }

    // Digits-only phone (spaces/dashes stripped) for duplicate comparison; null
    // when there's no phone.
    private static string? NormalizePhone(string? phone)
        => string.IsNullOrWhiteSpace(phone) ? null : phone.Replace(" ", "").Replace("-", "");

    private static bool IsValidPhone(string phone)
    {
        var d = phone.Replace(" ", "").Replace("-", "");
        if (d.StartsWith("+")) d = d.Substring(1);
        return d.Length is >= 10 and <= 13 && d.All(char.IsDigit);
    }

    private static bool IsValidEmail(string email)
    {
        try { var a = new System.Net.Mail.MailAddress(email); return a.Address == email; }
        catch { return false; }
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
