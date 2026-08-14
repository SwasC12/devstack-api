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

    // Optional customer + order-level note (collection, delivery, etc.).
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? Notes { get; set; }

    // Voiding: once set, the order is excluded from revenue and its stock is
    // restored. One-way door fix — an order can be voided, not edited.
    public DateTime? VoidedAt { get; set; }
    public int? VoidedByUserId { get; set; }
    public string? VoidReason { get; set; }

    // Refunds against this order (money back, stock NOT returned).
    public List<OrderRefund> Refunds { get; set; } = [];

    // Kitchen display: when the kitchen taps "Done" the order leaves the live
    // queue (it stays in revenue - completed just means made/served).
    public DateTime? CompletedAt { get; set; }

    // Service mode: dine-in (with table number) vs takeaway. Drives the kitchen
    // display card and the receipt.
    public string? DineMode { get; set; } // dinein | takeaway
    public string? TableNumber { get; set; }

    // KDS hold & send: when set, the order sits in the kitchen's held strip
    // instead of the live queue until "Send" clears it.
    public DateTime? HeldAt { get; set; }

    // Money extras: tip added by the customer + service charge (auto or manual).
    public decimal TipAmount { get; set; }
    public decimal ServiceChargeAmount { get; set; }

    // House account: when set, the order was charged to this customer's tab
    // (balance increased; settle separately). Payments rows carry the detail.
    public int? AccountCustomerId { get; set; }

    // Split payments: one row per tender (cash/card/account). Reports use these.
    public List<OrderPayment> Payments { get; set; } = [];
}
