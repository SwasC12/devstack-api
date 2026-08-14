namespace DevStack.API.Models;

// Recipe/plate costing: one line per ingredient. RecipeCost = Σ(CostPerUnit × Quantity).
// Plate cost feeds gross-profit tracking (used when present, else MenuItem.CostBasis).
public class RecipeLine
{
    public int Id { get; set; }
    public int MenuItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal CostPerUnit { get; set; }
    public decimal Quantity { get; set; }
}
