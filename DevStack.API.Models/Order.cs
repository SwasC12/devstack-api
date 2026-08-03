namespace DevStack.API.Models;

public class Order
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal Total { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public int ShopId { get; set; }

    // Who sold it. Null only for pre-attribution (legacy) rows.
    public int? UserId { get; set; }

    // Payment: how it was taken and (for cash) the tendered amount + change.
    public string PaymentMethod { get; set; } = "cash"; // cash | card
    public decimal? AmountReceived { get; set; }
    public decimal? ChangeGiven { get; set; }

    // Discount applied at checkout (snapshot for receipts/reports).
    public int? DiscountId { get; set; }
    public string? DiscountName { get; set; }
    public decimal DiscountAmount { get; set; } // 0 when no discount

    // Voiding: once set, the order is excluded from revenue and its stock is
    // restored. One-way door fix — an order can be voided, not edited.
    public DateTime? VoidedAt { get; set; }
    public int? VoidedByUserId { get; set; }
    public string? VoidReason { get; set; }
}
