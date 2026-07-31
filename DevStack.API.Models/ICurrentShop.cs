namespace DevStack.API.Models;

// Contract for the tenant-scoped "current shop" of a request. Implemented in
// the WebService layer (reads the JWT claim; falls back to the first shop for
// unauthenticated requests). Placed here so the DataAccess and PlatformLogic
// layers can depend on it without a layering violation.
public interface ICurrentShop
{
    int ShopId { get; }
}
