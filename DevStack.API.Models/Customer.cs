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
}
