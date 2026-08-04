namespace DevStack.API.Models;

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int MenuItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }

    // Size snapshot for sized items (null for legacy/single-price lines).
    public int? SizeId { get; set; }
    public string? SizeName { get; set; }
}
