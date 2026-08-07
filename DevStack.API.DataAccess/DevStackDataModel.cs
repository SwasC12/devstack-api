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
    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();
    public DbSet<Modifier> Modifiers => Set<Modifier>();
    public DbSet<OrderItemModifier> OrderItemModifiers => Set<OrderItemModifier>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Multi-tenancy: every query on these tables is scoped to the current
        // shop automatically, so a missed .Where() can't leak across shops.
        modelBuilder.Entity<MenuItem>().HasQueryFilter(m => m.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Category>().HasQueryFilter(c => c.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Order>().HasQueryFilter(o => o.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Shift>().HasQueryFilter(s => s.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Discount>().HasQueryFilter(d => d.ShopId == _currentShop.ShopId);

        modelBuilder.Entity<Shop>()
            .Property(s => s.IsActive)
            .HasDefaultValue(true);

        modelBuilder.Entity<MenuItem>()
            .Property(i => i.Price)
            .HasPrecision(18, 2);

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

        modelBuilder.Entity<Shift>()
            .Property(s => s.StartingFloat)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderItemModifier>()
            .HasOne<OrderItem>()
            .WithMany(i => i.Modifiers)
            .HasForeignKey(m => m.OrderItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
