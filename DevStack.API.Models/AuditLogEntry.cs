namespace DevStack.API.Models;

// Full audit trail: every meaningful write (menu, users, discounts, voids,
// refunds, POs, expenses, customers, settings) with actor + timestamp.
public class AuditLogEntry
{
    public int Id { get; set; }
    public int? ShopId { get; set; } // null for platform-level actions
    public int? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
