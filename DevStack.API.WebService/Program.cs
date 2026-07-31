using System.Text;
using DevStack.API.DataAccess;
using DevStack.API.DataAccess.Repository;
using DevStack.API.Models;
using DevStack.API.PlatformLogic;
using DevStack.API.PlatformLogic.CategoryLogic;
using DevStack.API.PlatformLogic.MenuItemLogic;
using DevStack.API.WebService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ── SERVICES ──────────────────────────────────────────────────────────────────

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Multi-tenancy: resolves the current shop per request (from the JWT claim).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentShop, CurrentShopService>();

// EF Core → SQL Server.
builder.Services.AddDbContext<DevStackDataModel>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// JWT auth.
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
builder.Services.AddAuthorization();

// Business layers.
builder.Services.AddScoped<IMenuItemRepository, MenuItemRepository>();
builder.Services.AddScoped<IMenuItemLogic, MenuItemLogic>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<ICategoryLogic, CategoryLogic>();
builder.Services.AddScoped<IErrorHandling, ErrorHandlingService>();

// Cloudinary settings (used by MenuItemLogic for image cleanup).
builder.Services.Configure<CloudinarySettings>(
    builder.Configuration.GetSection("Cloudinary"));

// CORS.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ── DB MIGRATE + SEED ─────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<DevStackDataModel>();
        db.Database.Migrate();

        // Platform superadmin — not tied to any shop. Used to provision shops.
        if (!db.Users.Any(u => u.Role == "superadmin"))
        {
            db.Users.Add(new AppUser
            {
                Username = "superadmin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("superadmin123"),
                DisplayName = "Super Admin",
                Role = "superadmin",
                ShopId = null
            });
            db.SaveChanges();
        }

        // First run (or upgrade from single-shop): create the default shop and
        // move every existing row into it so nothing is orphaned.
        if (!db.Shops.Any())
        {
            var shop = new Shop { Name = "Default Shop", Code = "DEVSTACK", CreatedAt = DateTime.UtcNow.AddHours(2) };
            db.Shops.Add(shop);
            db.SaveChanges();

            foreach (var item in db.MenuItems) item.ShopId = shop.Id;
            foreach (var cat in db.Categories) cat.ShopId = shop.Id;
            foreach (var order in db.Orders) order.ShopId = shop.Id;
            foreach (var shift in db.Shifts) shift.ShopId = shop.Id;
            foreach (var user in db.Users.Where(u => u.ShopId == null && u.Role != "superadmin")) user.ShopId = shop.Id;

            // Backfill categories from existing menu items' labels (only if the
            // categories table is still empty).
            if (!db.Categories.Any())
            {
                foreach (var name in db.MenuItems
                    .Where(m => !string.IsNullOrWhiteSpace(m.Category))
                    .Select(m => m.Category.Trim())
                    .Distinct())
                {
                    db.Categories.Add(new Category { Name = name, ShopId = shop.Id, CreatedAt = DateTime.UtcNow.AddHours(2) });
                }
            }
            db.SaveChanges();

            // Out-of-the-box admin for the default shop (skipped if a
            // pre-existing admin was backfilled above).
            if (!db.Users.Any(u => u.ShopId == shop.Id))
            {
                db.Users.Add(new AppUser
                {
                    Username = "admin",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                    DisplayName = "Admin",
                    Role = "admin",
                    ShopId = shop.Id
                });
                db.SaveChanges();
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database migration/seed failed on startup.");
    }
}

// ── HTTP PIPELINE ─────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
