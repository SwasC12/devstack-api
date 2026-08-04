namespace DevStack.API.Models;

// Snapshot of the modifiers chosen on one order line ("Oat +R8", "Extra shot
// +R12"). Prices are baked in so receipts/reports stay right even if the menu
// changes later.
public class OrderItemModifier
{
    public int Id { get; set; }
    public int OrderItemId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal PriceDelta { get; set; }
}
