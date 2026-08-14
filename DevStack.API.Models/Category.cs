namespace DevStack.API.Models;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    // KDS routing: which display(s) handle this category. kitchen | bar | both.
    public string Station { get; set; } = "both";
    public DateTime CreatedAt { get; set; }
    public int ShopId { get; set; }
}
