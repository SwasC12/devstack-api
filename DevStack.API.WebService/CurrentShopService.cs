using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService;

// Resolves which shop the current request belongs to. Authenticated requests
// carry a "shopId" JWT claim. Unauthenticated requests (the public menu) fall
// back to the first/default shop, resolved once per request in a fresh scope
// so we never run a query on the very DbContext that is mid-query.
public class CurrentShopService : ICurrentShop
{
    private readonly IHttpContextAccessor _http;
    private readonly IServiceProvider _services;
    private int? _cached;

    public CurrentShopService(IHttpContextAccessor http, IServiceProvider services)
    {
        _http = http;
        _services = services;
    }

    public int ShopId => _cached ??= Resolve();

    private int Resolve()
    {
        var claim = _http.HttpContext?.User.FindFirstValue("shopId");
        if (claim is not null && int.TryParse(claim, out var id))
            return id;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DevStackDataModel>();
        return db.Shops.OrderBy(s => s.Id).Select(s => s.Id).FirstOrDefault();
    }
}
