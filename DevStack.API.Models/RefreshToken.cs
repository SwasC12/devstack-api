namespace DevStack.API.Models;

// A single issued refresh token, stored HASHED (SHA-256 of the opaque value).
// Rotation: every use revokes the current token and issues a replacement, so a
// leaked token dies the moment it's replayed. Revoked tokens are kept (not
// purged) specifically so replay attempts are detectable.
public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public int? ReplacedByTokenId { get; set; }
    public int? ShopId { get; set; } // tenant context at issue time (audit / cleanup)
}
