using DevStack.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.DataAccess;

public class DevStackDataModel : DbContext
{
    private readonly ICurrentShop _currentShop;

    public DevStackDataModel(DbContextOptions<DevStackDataModel> options, ICurrentShop currentShop) : base(options)
    {
        _currentShop = currentShop;
    }

    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<MenuSize> MenuSizes => Set<MenuSize>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PushToken> PushTokens => Set<PushToken>();
    public DbSet<PlatformEvent> PlatformEvents => Set<PlatformEvent>();
    public DbSet<AppRelease> AppReleases => Set<AppRelease>();
    public DbSet<AppCheckin> AppCheckins => Set<AppCheckin>();
    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();
    public DbSet<Modifier> Modifiers => Set<Modifier>();
    public DbSet<OrderItemModifier> OrderItemModifiers => Set<OrderItemModifier>();
    public DbSet<OrderRefund> OrderRefunds => Set<OrderRefund>();
    public DbSet<RecipeLine> RecipeLines => Set<RecipeLine>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Multi-tenancy: every query on these tables is scoped to the current
        // shop automatically, so a missed .Where() can't leak across shops.
        modelBuilder.Entity<MenuItem>().HasQueryFilter(m => m.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Category>().HasQueryFilter(c => c.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Order>().HasQueryFilter(o => o.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Shift>().HasQueryFilter(s => s.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Discount>().HasQueryFilter(d => d.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Supplier>().HasQueryFilter(s => s.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<PurchaseOrder>().HasQueryFilter(p => p.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Customer>().HasQueryFilter(c => c.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<OrderPayment>().HasQueryFilter(p => p.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Expense>().HasQueryFilter(e => e.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<AuditLogEntry>().HasQueryFilter(a => a.ShopId == _currentShop.ShopId);

        modelBuilder.Entity<Shop>()
            .Property(s => s.IsActive)
            .HasDefaultValue(true);

        modelBuilder.Entity<MenuItem>()
            .Property(i => i.Price)
            .HasPrecision(18, 2);

        // CategoryId is a real FK now (the name string stays as the display
        // label). A category with items can't be deleted - the FK enforces it
        // even if a future code path skips the friendly guard in CategoryLogic.
        modelBuilder.Entity<MenuItem>()
            .HasOne<Category>()
            .WithMany()
            .HasForeignKey(m => m.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Order>()
            .Property(o => o.Total)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Order>()
            .Property(o => o.AmountReceived)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Order>()
            .Property(o => o.ChangeGiven)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Order>()
            .Property(o => o.DiscountAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Discount>()
            .Property(d => d.Value)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderItem>()
            .Property(i => i.Price)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderItem>()
            .HasOne<Order>()
            .WithMany(o => o.Items)
            .HasForeignKey(i => i.OrderId);

        modelBuilder.Entity<MenuSize>()
            .Property(s => s.Price)
            .HasPrecision(18, 2);

        // Sizes live and die with their item; an item's sizes are its own.
        modelBuilder.Entity<MenuSize>()
            .HasOne<MenuItem>()
            .WithMany(m => m.Sizes)
            .HasForeignKey(s => s.MenuItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MenuSize>()
            .HasIndex(s => new { s.MenuItemId, s.Name })
            .IsUnique();

        // ── Performance indexes ────────────────────────────────────────────────
        // Every tenant query goes through the ShopId global filter; the hot
        // read paths (kitchen queue, cash-up, analytics, summary, admin order
        // history, auth refresh) were doing full scans before these. Keep the
        // model config and the migration in sync - a mismatch blocks startup.
        modelBuilder.Entity<Order>()
            .HasIndex(o => new { o.ShopId, o.CreatedAt });
        modelBuilder.Entity<Order>()
            .HasIndex(o => new { o.ShopId, o.VoidedAt, o.CompletedAt, o.CreatedAt });
        modelBuilder.Entity<Order>()
            .HasIndex(o => o.UserId);
        modelBuilder.Entity<MenuItem>()
            .HasIndex(m => m.ShopId);
        modelBuilder.Entity<AppUser>()
            .HasIndex(u => u.ShopId);
        modelBuilder.Entity<Shift>()
            .HasIndex(s => s.ShopId);
        modelBuilder.Entity<Discount>()
            .HasIndex(d => d.ShopId);
        modelBuilder.Entity<Notification>()
            .HasIndex(n => n.ShopId);
        // Loyalty lookup: the POS finds a customer by the code their QR encodes.
        // Unique + filtered (nulls excluded) so pre-loyalty records don't clash.
        modelBuilder.Entity<Customer>()
            .HasIndex(c => c.LoyaltyCode)
            .IsUnique()
            .HasFilter("[LoyaltyCode] IS NOT NULL");

        // Auth hot path: every refresh/login looks a token up by hash and the
        // table keeps revoked tokens forever - unindexed this is a full scan.
        modelBuilder.Entity<RefreshToken>()
            .HasIndex(t => t.TokenHash)
            .IsUnique();
        modelBuilder.Entity<RefreshToken>()
            .HasIndex(t => t.UserId);
        modelBuilder.Entity<RefreshToken>()
            .HasIndex(t => t.ReplacedByTokenId);

        modelBuilder.Entity<OrderRefund>()
            .HasIndex(r => r.OrderId);

        // ── v1.8 feature indexes ────────────────────────────────────────────────
        modelBuilder.Entity<OrderPayment>()
            .HasIndex(p => p.OrderId);
        modelBuilder.Entity<Expense>()
            .HasIndex(e => new { e.ShopId, e.CreatedAt });
        modelBuilder.Entity<Customer>()
            .HasIndex(c => c.ShopId);
        modelBuilder.Entity<Supplier>()
            .HasIndex(s => s.ShopId);
        modelBuilder.Entity<PurchaseOrder>()
            .HasIndex(p => new { p.ShopId, p.Status });
        modelBuilder.Entity<PurchaseOrderLine>()
            .HasIndex(l => l.PurchaseOrderId);
        modelBuilder.Entity<AuditLogEntry>()
            .HasIndex(a => new { a.ShopId, a.CreatedAtUtc });

        modelBuilder.Entity<RecipeLine>()
            .HasOne<MenuItem>()
            .WithMany(m => m.RecipeLines)
            .HasForeignKey(r => r.MenuItemId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RecipeLine>()
            .Property(r => r.CostPerUnit)
            .HasPrecision(18, 2);
        modelBuilder.Entity<RecipeLine>()
            .Property(r => r.Quantity)
            .HasPrecision(18, 3);

        modelBuilder.Entity<PurchaseOrder>()
            .HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PurchaseOrder>()
            .Property(p => p.FreightCost)
            .HasPrecision(18, 2);
        modelBuilder.Entity<PurchaseOrder>()
            .Property(p => p.DutyCost)
            .HasPrecision(18, 2);
        modelBuilder.Entity<PurchaseOrderLine>()
            .HasOne<PurchaseOrder>()
            .WithMany(p => p.Lines)
            .HasForeignKey(l => l.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PurchaseOrderLine>()
            .Property(l => l.UnitCost)
            .HasPrecision(18, 2);
        modelBuilder.Entity<PurchaseOrderLine>()
            .HasOne<MenuItem>()
            .WithMany()
            .HasForeignKey(l => l.MenuItemId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OrderPayment>()
            .HasOne<Order>()
            .WithMany(o => o.Payments)
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderPayment>()
            .Property(p => p.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Expense>()
            .Property(e => e.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Customer>()
            .Property(c => c.Balance)
            .HasPrecision(18, 2);
        modelBuilder.Entity<Customer>()
            .Property(c => c.CreditLimit)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Order>()
            .Property(o => o.TipAmount)
            .HasPrecision(18, 2);
        modelBuilder.Entity<Order>()
            .Property(o => o.ServiceChargeAmount)
            .HasPrecision(18, 2);

        // House-account link: deleting a customer keeps their order history
        // (the id just becomes null on those orders).
        modelBuilder.Entity<Order>()
            .HasOne<Customer>()
            .WithMany()
            .HasForeignKey(o => o.AccountCustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MenuItem>()
            .Property(m => m.CostBasis)
            .HasPrecision(18, 2);

        modelBuilder.Entity<MenuItem>()
            .HasIndex(m => new { m.ShopId, m.Sku })
            .IsUnique();

        modelBuilder.Entity<Notification>()
            .HasIndex(n => n.UserId);

        modelBuilder.Entity<PushToken>()
            .HasIndex(t => t.Token)
            .IsUnique();

        modelBuilder.Entity<PushToken>()
            .HasIndex(t => t.UserId);

        modelBuilder.Entity<Modifier>()
            .Property(m => m.PriceDelta)
            .HasPrecision(18, 2);

        modelBuilder.Entity<ModifierGroup>()
            .HasMany(g => g.Modifiers)
            .WithOne()
            .HasForeignKey(m => m.ModifierGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ModifierGroup>()
            .HasOne<MenuItem>()
            .WithMany(m => m.ModifierGroups)
            .HasForeignKey(g => g.MenuItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderItemModifier>()
            .Property(m => m.PriceDelta)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderRefund>()
            .Property(r => r.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Shift>()
            .Property(s => s.StartingFloat)
            .HasPrecision(18, 2);

        modelBuilder.Entity<AppUser>()
            .Property(u => u.WageRate)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderItemModifier>()
            .HasOne<OrderItem>()
            .WithMany(i => i.Modifiers)
            .HasForeignKey(m => m.OrderItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
