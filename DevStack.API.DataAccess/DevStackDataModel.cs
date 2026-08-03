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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Multi-tenancy: every query on these tables is scoped to the current
        // shop automatically, so a missed .Where() can't leak across shops.
        modelBuilder.Entity<MenuItem>().HasQueryFilter(m => m.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Category>().HasQueryFilter(c => c.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Order>().HasQueryFilter(o => o.ShopId == _currentShop.ShopId);
        modelBuilder.Entity<Shift>().HasQueryFilter(s => s.ShopId == _currentShop.ShopId);

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

        modelBuilder.Entity<OrderItem>()
            .Property(i => i.Price)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderItem>()
            .HasOne<Order>()
            .WithMany(o => o.Items)
            .HasForeignKey(i => i.OrderId);
    }
}
