using DevStack.API.DataAccess;
using DevStack.API.DataAccess.Repository;
using DevStack.API.Models;
using DevStack.API.PlatformLogic.ToolLogic;
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
//   ToolsController → IToolLogic → IToolRepository → DevStackDataModel
// "Scoped" = one instance per HTTP request (the correct lifetime for EF work).
builder.Services.AddScoped<IToolRepository, ToolRepository>();
builder.Services.AddScoped<IToolLogic, ToolLogic>();

// CORS so the Angular app (a different origin) can call this API.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Apply any pending EF migrations on startup so the DB schema is always current,
// then seed a few of your real tools on first run (only if the table is empty).
// Wrapped in try/catch so a transient DB problem can't crash the whole app on
// boot — the API still starts, and DB errors surface per-request instead.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<DevStackDataModel>();
        db.Database.Migrate();

        if (!db.Tools.Any())
        {
            db.Tools.AddRange(
            new Tool { Name = "Supabase", Category = "Database", Url = "https://supabase.com", IsPaid = false, Currency = "USD", Projects = "Gearheads Blog", CreatedAt = DateTime.UtcNow },
            new Tool { Name = "Cloudinary", Category = "Media", Url = "https://cloudinary.com", IsPaid = false, Currency = "USD", Projects = "Gearheads Blog", CreatedAt = DateTime.UtcNow },
            new Tool { Name = "MonsterASP.NET", Category = "Hosting", Url = "https://monsterasp.net", IsPaid = true, MonthlyCost = 3m, Currency = "USD", Projects = "DevStack", CreatedAt = DateTime.UtcNow },
            new Tool { Name = "Resend", Category = "Email", Url = "https://resend.com", IsPaid = false, Currency = "USD", CreatedAt = DateTime.UtcNow },
            new Tool { Name = "Vercel", Category = "Hosting", Url = "https://vercel.com", IsPaid = false, Currency = "USD", Projects = "DevStack", CreatedAt = DateTime.UtcNow }
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
