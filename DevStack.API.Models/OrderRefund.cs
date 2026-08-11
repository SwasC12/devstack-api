namespace DevStack.API.Models;

// A partial/full refund on a completed order. Proper-POS semantics:
// - A VOID cancels an order that was never fulfilled -> stock is returned.
// - A REFUND gives money back on an order that WAS fulfilled -> stock is NOT
//   returned (the goods were already consumed/sold).
// Refunds are one-way, always require a reason, and are subtracted from
// revenue in summary/analytics (net revenue = money actually kept).
public class OrderRefund
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ShopId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = "cash";
    public DateTime CreatedAt { get; set; }
    public int UserId { get; set; }
}
