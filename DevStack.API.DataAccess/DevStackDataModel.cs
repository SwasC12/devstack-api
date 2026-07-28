using DevStack.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.DataAccess;

// The EF Core DbContext — the bridge between C# objects and SQL Server tables.
// (TDC names their context the "DataModel", so we follow that.)
public class DevStackDataModel : DbContext
{
    // Options (which DB + connection string) are injected from WebService/Program.cs.
    public DevStackDataModel(DbContextOptions<DevStackDataModel> options)
        : base(options)
    {
    }

    // Becomes the "Tools" table; also the entry point for querying it.
    public DbSet<Tool> Tools => Set<Tool>();

    // OnModelCreating is where you fine-tune how entities map to columns
    // (the "Fluent API"). Here we tell SQL Server exactly how precise the
    // money column is: 18 total digits, 2 after the decimal point.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tool>()
            .Property(t => t.MonthlyCost)
            .HasPrecision(18, 2);
    }
}
