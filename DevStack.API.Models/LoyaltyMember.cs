namespace DevStack.API.Models;

// A loyalty customer, scoped to a Brand (franchise) rather than a single shop —
// so the same card / phone earns and redeems across every shop of the brand,
// with ONE shared stamp balance. Kept SEPARATE from Customer (which remains the
// per-shop house account / store-credit record).
public class LoyaltyMember
{
    public int Id { get; set; }
    public int BrandId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    // Personal QR token (the customer's "LOY:<code>" QR). Unique across ALL brands.
    public string? LoyaltyCode { get; set; }
    public int LoyaltyStamps { get; set; }              // shared balance for the whole brand
    public string? LoyaltyPasswordHash { get; set; }    // required to view the card via "Check my points"
    public bool MarketingConsent { get; set; }
    public bool SelfSignup { get; set; }
    public DateTime CreatedAt { get; set; }
}
