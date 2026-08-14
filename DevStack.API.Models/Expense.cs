namespace DevStack.API.Models;

// Expenses & petty cash: cash out of the till (supplies, milk runs, etc.).
public class Expense
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public string Category { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? UserId { get; set; }
}
