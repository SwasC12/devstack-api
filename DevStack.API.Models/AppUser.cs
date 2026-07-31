namespace DevStack.API.Models;

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "admin"; // superadmin | admin | cashier
    public int? ShopId { get; set; } // null for superadmins; shop staff belong to one shop
}
