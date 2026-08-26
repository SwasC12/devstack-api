using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// PUBLIC, unauthenticated endpoints for customer self-enrolment. A shopper scans
// a BRAND join QR, which opens the web loyalty page. Loyalty is scoped to the
// BRAND (franchise), not one shop, so the same card works at every shop of the
// brand and never at another brand. Everything is keyed by the brand's random
// join token in the URL (never a login code). Signup/sign-in are rate-limited.
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
    private static object Card(LoyaltyMember m, Brand b) => new
    {
        name = m.Name,
        loyaltyCode = m.LoyaltyCode,
        shopName = b.Name,                 // the brand name (field kept as shopName for the UI)
        reward = b.LoyaltyReward,
        stampsRequired = b.LoyaltyStampsRequired,
        stamps = m.LoyaltyStamps
    };

    // Resolve the brand behind a public join token.
    private Task<Brand?> BrandByTokenAsync(string token, bool tracking = false)
    {
        var norm = (token ?? "").Trim().ToUpperInvariant();
        var q = tracking ? _db.Brands : _db.Brands.AsNoTracking();
        return q.FirstOrDefaultAsync(b => b.JoinToken == norm);
    }

    // GET api/public/shop/{token} — brand branding + loyalty summary for the page.
    [HttpGet("shop/{token}")]
    public async Task<ActionResult> GetShop(string token)
    {
        var brand = await BrandByTokenAsync(token);
        if (brand is null) return NotFound(new { error = "We couldn't find that shop." });
        return Ok(new
        {
            Name = brand.Name,
            LogoUrl = brand.LogoUrl,
            brand.LoyaltyEnabled,
            brand.LoyaltyReward,
            brand.LoyaltyStampsRequired
        });
    }

    // POST api/public/member/{token} — returning customer SIGNS IN with their
    // phone / email / loyalty code AND their password. The password is what stops
    // someone who merely knows a phone number from opening the card.
    [HttpPost("member/{token}")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> Member(string token, MemberLookupRequest request)
    {
        var brand = await BrandByTokenAsync(token, tracking: true);
        if (brand is null) return NotFound(new { error = "We couldn't find that shop." });

        var q = (request.Identifier ?? "").Trim();
        var password = request.Password ?? "";
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Enter your phone, email or loyalty code." });
        if (string.IsNullOrEmpty(password))
            return BadRequest(new { error = "Enter your password." });

        var upper = q.ToUpperInvariant();
        var lower = q.ToLowerInvariant();
        var qNorm = NormalizePhone(q);
        var member = await _db.LoyaltyMembers
            .FirstOrDefaultAsync(m => m.BrandId == brand.Id &&
                (m.LoyaltyCode == upper
                 || (m.Email != null && m.Email.ToLower() == lower)
                 || (m.Phone != null && m.Phone.Replace(" ", "").Replace("-", "") == qNorm)));
        if (member is null)
            return NotFound(new { error = "No loyalty card found. Please sign up." });

        if (string.IsNullOrEmpty(member.LoyaltyPasswordHash))
        {
            // Legacy member with no password: first sign-in sets it (transition).
            if (password.Length < MinPasswordLength)
                return BadRequest(new { error = $"Set a password of at least {MinPasswordLength} characters." });
            member.LoyaltyPasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
            await _db.SaveChangesAsync();
        }
        else if (!BCrypt.Net.BCrypt.Verify(password, member.LoyaltyPasswordHash))
        {
            return Unauthorized(new { error = "Incorrect password." });
        }

        return Ok(Card(member, brand));
    }

    // POST api/public/member-resume/{token} — refresh a card for a device that
    // already signed in, using the stored loyalty code. No password; read-only.
    [HttpPost("member-resume/{token}")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> MemberResume(string token, MemberResumeRequest request)
    {
        var brand = await BrandByTokenAsync(token);
        if (brand is null) return NotFound(new { error = "We couldn't find that shop." });

        var code = (request.LoyaltyCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code)) return BadRequest(new { error = "Missing code." });

        var member = await _db.LoyaltyMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.BrandId == brand.Id && m.LoyaltyCode == code);
        if (member is null) return NotFound(new { error = "Card not found." });

        return Ok(Card(member, brand));
    }

    // POST api/public/signup/{token} — enrol a customer into the brand's loyalty
    // programme and return the loyalty code their personal QR encodes.
    [HttpPost("signup/{token}")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> Signup(string token, SignupRequest request)
    {
        var brand = await BrandByTokenAsync(token, tracking: true);
        if (brand is null) return NotFound(new { error = "We couldn't find that shop." });
        if (!brand.LoyaltyEnabled) return BadRequest(new { error = "This shop's loyalty programme isn't active." });

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

        if (!string.IsNullOrWhiteSpace(phone) && !IsValidPhone(phone))
            return BadRequest(new { error = "Please enter a valid phone number." });
        if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
            return BadRequest(new { error = "Please enter a valid email address." });

        // One membership per phone / email within the BRAND.
        var phoneNorm = NormalizePhone(phone);
        var emailNorm = email?.ToLowerInvariant();
        var dup = await _db.LoyaltyMembers.AnyAsync(m => m.BrandId == brand.Id &&
            ((phoneNorm != null && m.Phone != null && m.Phone.Replace(" ", "").Replace("-", "") == phoneNorm)
             || (emailNorm != null && m.Email != null && m.Email.ToLower() == emailNorm)));
        if (dup)
            return Conflict(new { error = "This email/number is already in use." });

        var member = new LoyaltyMember
        {
            BrandId = brand.Id,
            Name = name,
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone,
            Email = string.IsNullOrWhiteSpace(email) ? null : email,
            SelfSignup = true,
            MarketingConsent = request.Consent,
            CreatedAt = DateTime.UtcNow.AddHours(2),
            LoyaltyCode = await GenerateUniqueLoyaltyCodeAsync(),
            LoyaltyPasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        };
        _db.LoyaltyMembers.Add(member);
        await _db.SaveChangesAsync();
        return Ok(Card(member, brand));
    }

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

    // Short, URL/QR-friendly, unambiguous code (no 0/O/1/I), unique across brands.
    private async Task<string> GenerateUniqueLoyaltyCodeAsync()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(10);
            var sb = new System.Text.StringBuilder(10);
            foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            var candidate = sb.ToString();
            if (!await _db.LoyaltyMembers.AnyAsync(m => m.LoyaltyCode == candidate))
                return candidate;
        }
        return "L" + DateTime.UtcNow.Ticks.ToString("X");
    }
}
