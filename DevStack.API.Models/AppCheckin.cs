namespace DevStack.API.Models;

// One row per shop: which app version each shop is currently running, and
// when it last checked in. Powers the "shops updated" superadmin dashboard.
public class AppCheckin
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public string Version { get; set; } = string.Empty;
    public DateTime LastSeenAtUtc { get; set; }
}
