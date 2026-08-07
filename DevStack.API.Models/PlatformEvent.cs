namespace DevStack.API.Models;

// Superadmin audit trail + analytics: every notable platform action is
// recorded here (shop lifecycle, password resets, broadcasts, push failures).
// Powers the platform overview counters and the live activity feed on the
// superadmin Shops page.
public class PlatformEvent
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty; // shop_created | shop_suspended | shop_activated | password_reset | broadcast_sent | push_failed
    public int? ShopId { get; set; }
    public string Detail { get; set; } = string.Empty; // human-readable, e.g. "Rosebank — reset admin password (owner)"
    public DateTime CreatedAtUtc { get; set; }
}
