namespace DevStack.API.Models;

// A tenant: one shop running on the shared instance. Every shop-scoped table
// carries a ShopId and every query is filtered to the current shop.
public class Shop
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty; // short, uppercase, unique — typed at login
    public string? LogoUrl { get; set; } // owner-customisable branding, shown in the POS
    public string? ReceiptQrUrl { get; set; } // scannable QR printed on receipts (WhatsApp / review / feedback link)
    public string? OwnerEmail { get; set; } // platform contact info (superadmin-maintained; future owner emails)
    public string? OwnerPhone { get; set; } // platform contact info (superadmin-maintained)
    public bool IsActive { get; set; } = true; // suspended shops can't sign in (platform lifecycle)
    public DateTime CreatedAt { get; set; }
}
