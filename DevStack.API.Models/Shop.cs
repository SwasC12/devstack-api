namespace DevStack.API.Models;

// A tenant: one shop running on the shared instance. Every shop-scoped table
// carries a ShopId and every query is filtered to the current shop.
public class Shop
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty; // short, uppercase, unique — typed at login
    // Randomised, unguessable token for the PUBLIC loyalty join URL
    // (/join/<JoinToken>). Deliberately NOT the human Code: the code is a login
    // secret typed by staff, so it must never appear in a customer-facing QR.
    // Rotatable by the admin to invalidate an old printed poster.
    public string? JoinToken { get; set; }
    public string? LogoUrl { get; set; } // owner-customisable branding, shown in the POS
    public string? ReceiptQrUrl { get; set; } // scannable QR printed on receipts (WhatsApp / review / feedback link)
    public string? OwnerEmail { get; set; } // platform contact info (superadmin-maintained; future owner emails)
    public string? OwnerPhone { get; set; } // platform contact info (superadmin-maintained)
    public string? KitchenUrl { get; set; } // LAN webhook target for the kitchen display (e.g. http://192.168.1.50:8123)
    public bool IsActive { get; set; } = true; // suspended shops can't sign in (platform lifecycle)
    public bool IsArchived { get; set; } = false; // archived = hidden from the platform list + can't sign in (safe soft-delete)
    public DateTime CreatedAt { get; set; }

    // ── Loyalty (stamp card) ──────────────────────────────────────────────
    // Off by default. When on, a purchase with a customer attached earns one
    // stamp; at LoyaltyStampsRequired the customer can redeem LoyaltyReward.
    public bool LoyaltyEnabled { get; set; } = false;
    public int LoyaltyStampsRequired { get; set; } = 10;
    public string LoyaltyReward { get; set; } = "Free item";

    // ── Receipt customisation (per shop) ──────────────────────────────────
    // Nulls / the true defaults reproduce the current hardcoded receipt, so an
    // un-configured shop looks exactly as before.
    public string? ReceiptHeader { get; set; }   // extra line under the shop name (address / tagline)
    public string? ReceiptFooter { get; set; }   // custom thank-you / promo footer text
    public bool ReceiptShowVat { get; set; } = true;
    public bool ReceiptShowQr { get; set; } = true;
    public bool ReceiptShowCashier { get; set; } = true;
    public bool ReceiptShowLogo { get; set; } = true; // print the shop logo at the top of the receipt
}
