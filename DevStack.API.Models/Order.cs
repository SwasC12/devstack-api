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

    // Voiding: once set, the order is excluded from revenue and its stock is
    // restored. One-way door fix — an order can be voided, not edited.
    public DateTime? VoidedAt { get; set; }
    public int? VoidedByUserId { get; set; }
    public string? VoidReason { get; set; }
}
