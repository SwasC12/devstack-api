namespace DevStack.API.Models;

// Customer directory + house account. Balance > 0 means the customer owes the
// shop (charged orders not yet settled); settle reduces it.
public class Customer
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal Balance { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    // Loyalty stamp balance: +1 per attended purchase, -LoyaltyStampsRequired
    // when a reward is redeemed.
    public int LoyaltyStamps { get; set; }
    // Per-customer loyalty identity: the code their personal QR encodes and the
    // POS scans / looks up at checkout. Unique across all shops. Null for
    // pre-loyalty records until first assigned.
    public string? LoyaltyCode { get; set; }
    // True when the customer enrolled themselves via the public join page.
    public bool SelfSignup { get; set; }
    // Consent captured at signup (POPIA): agreed to store details + loyalty comms.
    public bool MarketingConsent { get; set; }
}
