namespace DevStack.API.Models;

// One payment against an order - supports split payments (cash + card) and
// house-account charges. Order.PaymentMethod stays as the primary method for
// receipts/back-compat; reports aggregate these rows.
public class OrderPayment
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ShopId { get; set; }
    public string Method { get; set; } = "cash"; // cash | card | account
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
}
