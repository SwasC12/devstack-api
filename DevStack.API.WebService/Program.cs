using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using DevStack.API.DataAccess;
using DevStack.API.DataAccess.Repository;
using DevStack.API.Models;
using DevStack.API.PlatformLogic;
using DevStack.API.PlatformLogic.CategoryLogic;
using DevStack.API.PlatformLogic.MenuItemLogic;
using DevStack.API.PlatformLogic.PushLogic;
using DevStack.API.WebService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
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

// Brute-force defence: a per-IP cap on auth endpoints + a failed-attempt
// lockout service. The "auth" policy is applied via [EnableRateLimiting].
builder.Services.AddSingleton<IAuthThrottle, AuthThrottleService>();
builder.Services.AddSingleton<IPushService, PushService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many attempts. Try again shortly." });
    };
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

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

// CORS — locked to configured frontend origins (never *), and credentials are
// allowed because the refresh token travels in a cookie.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
            .WithExposedHeaders("ETag"))); // kitchen poll reads the ETag for conditional GETs

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
        // No password lives in code: take one from config (Seed:SuperadminPassword
        // / env Seed__SuperadminPassword) or generate a one-time password and log it.
        if (!db.Users.Any(u => u.Role == "superadmin"))
        {
            var pw = builder.Configuration["Seed:SuperadminPassword"];
            if (string.IsNullOrEmpty(pw))
            {
                pw = GeneratePassword();
                logger.LogWarning("Superadmin created with a generated password. Change it after first login.");
                logger.LogInformation("One-time superadmin password: {Password}", pw);
            }
            db.Users.Add(new AppUser
            {
                Username = "superadmin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(pw),
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
            // pre-existing admin was backfilled above). Same no-password-in-code
            // rule as the superadmin: config or a generated one-time password.
            if (!db.Users.Any(u => u.ShopId == shop.Id))
            {
                var pw = builder.Configuration["Seed:DefaultAdminPassword"];
                if (string.IsNullOrEmpty(pw))
                {
                    pw = GeneratePassword();
                    logger.LogWarning("Default shop admin created with a generated password. Change it after first login.");
                    logger.LogInformation("One-time default admin password: {Password}", pw);
                }
                db.Users.Add(new AppUser
                {
                    Username = "admin",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(pw),
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
// Security headers: no clickjacking, no MIME sniffing, tight-ish CSP.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; connect-src 'self' https:; frame-ancestors 'none'";
    await next();
});

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Fresh installs never get a password from code. Take one from configuration
// (Seed:SuperadminPassword / Seed:DefaultAdminPassword, or the Seed__* env
// vars), otherwise generate a strong one-time password and log it once.
static string GeneratePassword(int length = 18)
{
    const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*";
    var bytes = RandomNumberGenerator.GetBytes(length);
    var sb = new StringBuilder(length);
    for (var i = 0; i < length; i++) sb.Append(chars[bytes[i] % chars.Length]);
    return sb.ToString();
}
