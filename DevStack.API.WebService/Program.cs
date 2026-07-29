using DevStack.API.DataAccess;
using DevStack.API.DataAccess.Repository;
using DevStack.API.Models;
using DevStack.API.PlatformLogic;
using DevStack.API.PlatformLogic.MenuItemLogic;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── SERVICES (the DI container) ─────────────────────────────────────────────

// MVC controllers.
builder.Services.AddControllers();

// Swagger / OpenAPI UI (same tooling TDC uses).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// EF Core → SQL Server, using the connection string from appsettings.json.
builder.Services.AddDbContext<DevStackDataModel>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register our layers so DI can build the chain automatically:
//   MenuItemsController → IMenuItemLogic → IMenuItemRepository → DevStackDataModel
// "Scoped" = one instance per HTTP request (the correct lifetime for EF work).
builder.Services.AddScoped<IMenuItemRepository, MenuItemRepository>();
builder.Services.AddScoped<IMenuItemLogic, MenuItemLogic>();
builder.Services.AddScoped<IErrorHandling, ErrorHandlingService>();

// Cloudinary settings from appsettings.json (used by MenuItemLogic for image cleanup).
builder.Services.Configure<CloudinarySettings>(
    builder.Configuration.GetSection("Cloudinary"));

// CORS so the Angular app (a different origin) can call this API.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Apply any pending EF migrations on startup so the DB schema is always current,
// then seed a starter menu on first run (only if the table is empty).
// Wrapped in try/catch so a transient DB problem can't crash the whole app on
// boot — the API still starts, and DB errors surface per-request instead.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<DevStackDataModel>();
        db.Database.Migrate();

        if (!db.MenuItems.Any())
        {
            db.MenuItems.AddRange(
                new MenuItem { Name = "Espresso", Category = "Hot Drinks", Price = 2.50m, Description = "A single shot of rich, full-bodied espresso.", IsAvailable = true, CreatedAt = DateTime.UtcNow },
                new MenuItem { Name = "Cappuccino", Category = "Hot Drinks", Price = 3.75m, Description = "Espresso with steamed milk and a thick layer of foam.", IsAvailable = true, CreatedAt = DateTime.UtcNow },
                new MenuItem { Name = "Flat White", Category = "Hot Drinks", Price = 3.90m, Description = "Espresso with velvety micro-foamed milk.", IsAvailable = true, CreatedAt = DateTime.UtcNow },
                new MenuItem { Name = "Iced Latte", Category = "Cold Drinks", Price = 4.25m, Description = "Chilled espresso and milk over ice.", IsAvailable = true, CreatedAt = DateTime.UtcNow },
                new MenuItem { Name = "Cold Brew", Category = "Cold Drinks", Price = 4.50m, Description = "Slow-steeped for 18 hours, smooth and low-acid.", IsAvailable = true, CreatedAt = DateTime.UtcNow },
                new MenuItem { Name = "Croissant", Category = "Pastries", Price = 3.20m, Description = "Buttery, flaky, baked fresh each morning.", IsAvailable = true, CreatedAt = DateTime.UtcNow },
                new MenuItem { Name = "Blueberry Muffin", Category = "Pastries", Price = 3.00m, Description = "Loaded with real blueberries.", IsAvailable = true, CreatedAt = DateTime.UtcNow }
            );
            db.SaveChanges();
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database migration/seed failed on startup.");
    }
}

// ── HTTP PIPELINE ───────────────────────────────────────────────────────────
// Swagger enabled in all environments so we can sanity-check the live API too.
app.UseSwagger();
app.UseSwaggerUI();

// NOTE: no UseHttpsRedirection — on MonsterASP the app runs behind IIS, which
// already terminates HTTPS. Redirecting here just logs "failed to determine the
// https port" and can bounce API calls with unwanted redirects.
app.UseCors();
app.UseAuthorization();
app.MapControllers();

app.Run();
