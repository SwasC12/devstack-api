namespace DevStack.API.Models;

// An in-app notification: the owner-facing inbox (bell in the app) AND the
// source of truth for push. Superadmin broadcasts create one row per target
// user; a push is then fired to every device token that user has registered.
public class Notification
{
    public int Id { get; set; }
    public int? ShopId { get; set; }        // null = platform-wide (superadmin source)
    public int? UserId { get; set; }        // null = role-wide (legacy/grouped)
    public string? Role { get; set; }       // target role when no UserId
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Type { get; set; } = "info"; // info | update | alert
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}
