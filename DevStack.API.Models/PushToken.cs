namespace DevStack.API.Models;

// A device registered for Firebase Cloud Messaging push. One user can have
// several devices (tablet at the till + the owner's phone). Tokens are
// upserted on register and deleted on logout / when FCM reports them dead.
public class PushToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? ShopId { get; set; }
    public string Token { get; set; } = string.Empty; // FCM registration token
    public string Platform { get; set; } = "android"; // android | web | ios
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}
