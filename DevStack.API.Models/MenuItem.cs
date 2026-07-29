namespace DevStack.API.Models;

// The entity: one instance = one item on the coffee-shop menu
// (a drink, a pastry, etc.). Lives in the Models project so EVERY layer
// (DataAccess, PlatformLogic, WebService) shares the same shape.
public class MenuItem
{
    // Named "Id" → EF Core treats it as the auto-incrementing primary key.
    public int Id { get; set; }

    // `= string.Empty` keeps these non-null even before they're set.
    // ("Nullable" is enabled project-wide, so a plain string may not be null.)
    public string Name { get; set; } = string.Empty;

    // e.g. "Hot Drinks", "Cold Drinks", "Pastries", "Food".
    public string Category { get; set; } = string.Empty;

    // `decimal` (not double) for money — no floating-point rounding surprises.
    public decimal Price { get; set; }

    // `?` = allowed to be null (not every item has these).
    public string? Description { get; set; }

    // URL of the item photo, hosted on Cloudinary.
    public string? ImageUrl { get; set; }

    // Cloudinary public_id, needed to delete the image from Cloudinary.
    public string? ImagePublicId { get; set; }

    // Whether the item is currently sold / shown on the menu.
    public bool IsAvailable { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
