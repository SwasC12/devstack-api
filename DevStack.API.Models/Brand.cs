namespace DevStack.API.Models;

// A franchise / loyalty programme that one or more shops belong to. The loyalty
// rules, the shared stamp balance's config, and the public join token all live
// here — so a customer's card works at EVERY shop of the brand, and never at a
// shop of a different brand. A standalone independent shop is its own one-shop
// brand.
public class Brand
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? JoinToken { get; set; }              // public /join/<token> URL (unique, unguessable)
    public bool LoyaltyEnabled { get; set; } = false;
    public int LoyaltyStampsRequired { get; set; } = 10;
    public string LoyaltyReward { get; set; } = "Free item";
    public string? LogoUrl { get; set; }                // optional brand logo for the loyalty page
    public DateTime CreatedAt { get; set; }
}
