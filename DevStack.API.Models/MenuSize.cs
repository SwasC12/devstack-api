namespace DevStack.API.Models;

// A drink-size option on a menu item: e.g. Small / Medium / Large, each with
// its own price. An item with no sizes sells at its base Price exactly as
// before; an item with sizes MUST be ordered with one of them (the POS shows
// a size picker and the server refuses size-less lines for sized items).
public class MenuSize
{
    public int Id { get; set; }
    public int MenuItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}
