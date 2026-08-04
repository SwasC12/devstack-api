namespace DevStack.API.Models;

public class MenuItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImagePublicId { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int StockQuantity { get; set; } = 0;
    public DateTime CreatedAt { get; set; }
    public int ShopId { get; set; }

    // Drink sizes (Small/Medium/Large...). Empty = sells at base Price only.
    public List<MenuSize> Sizes { get; set; } = [];

    // Modifier groups (Milk, Extras...). Each group holds its options with
    // price deltas. Empty = plain item.
    public List<ModifierGroup> ModifierGroups { get; set; } = [];
}
