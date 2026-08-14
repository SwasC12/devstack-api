namespace DevStack.API.Models;

public class MenuItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    // Referential integrity: the Categories table is the source of truth.
    // `Category` (the name string) is kept in sync as the denormalized label
    // the POS groups by - renaming a category cascades to items via the repo.
    public int? CategoryId { get; set; }
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImagePublicId { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int StockQuantity { get; set; } = 0;
    // Alert the shop owner (in-app + push) when stock drops to this level.
    public int LowStockThreshold { get; set; } = 5;
    public DateTime CreatedAt { get; set; }
    public int ShopId { get; set; }

    // Drink sizes (Small/Medium/Large...). Empty = sells at base Price only.
    public List<MenuSize> Sizes { get; set; } = [];

    // Modifier groups (Milk, Extras...). Each group holds its options with
    // price deltas. Empty = plain item.
    public List<ModifierGroup> ModifierGroups { get; set; } = [];

    // Plate/recipe costing: ingredient lines. RecipeCost = Σ(CostPerUnit × Quantity).
    public List<RecipeLine> RecipeLines { get; set; } = [];

    // Landed/manual cost per unit (set by PO receiving or the item form). Used
    // for gross-profit when no recipe is defined.
    public decimal CostBasis { get; set; }
}
