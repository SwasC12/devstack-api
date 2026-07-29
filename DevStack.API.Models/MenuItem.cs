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
}
