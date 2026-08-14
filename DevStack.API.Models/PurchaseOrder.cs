namespace DevStack.API.Models;

// Purchase order to a supplier. Status: open → partial → received. Freight and
// duty (landed cost) are distributed across received lines proportionally and
// roll into each item's CostBasis.
public class PurchaseOrder
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public int SupplierId { get; set; }
    public DateTime OrderedAt { get; set; }
    public string Status { get; set; } = "open"; // open | partial | received
    public decimal FreightCost { get; set; }
    public decimal DutyCost { get; set; }
    public string? Notes { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}

public class PurchaseOrderLine
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public int MenuItemId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public int ReceivedQuantity { get; set; }
}
