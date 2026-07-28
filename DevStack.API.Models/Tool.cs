namespace DevStack.API.Models;

// The entity: one instance = one service/tool you use (Supabase, Vercel, ...).
// Lives in the Models project so EVERY layer (DataAccess, PlatformLogic,
// WebService) can reference the same shape without duplicating it.
public class Tool
{
    // Named "Id" → EF Core treats it as the auto-incrementing primary key.
    public int Id { get; set; }

    // `= string.Empty` keeps these non-null even before they're set.
    // ("Nullable" is enabled project-wide, so a plain string may not be null.)
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    // `?` = allowed to be null (not every tool has these).
    public string? Url { get; set; }
    public string? Notes { get; set; }

    public bool IsPaid { get; set; }

    // `decimal` (not double) for money — no floating-point rounding surprises.
    public decimal? MonthlyCost { get; set; }
    public string Currency { get; set; } = "USD";

    // Which projects use this tool (comma-separated for now).
    public string? Projects { get; set; }

    public DateTime CreatedAt { get; set; }
}
