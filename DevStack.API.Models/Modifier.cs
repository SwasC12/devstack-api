namespace DevStack.API.Models;

// A modifier group on a menu item: e.g. Milk (single choice) or Extras (multi).
public class ModifierGroup
{
    public int Id { get; set; }
    public int MenuItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsMulti { get; set; } // false = radio (pick one), true = checkboxes
    public List<Modifier> Modifiers { get; set; } = [];
}

public class Modifier
{
    public int Id { get; set; }
    public int ModifierGroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PriceDelta { get; set; } // added to the line unit price (0 = free)
}
