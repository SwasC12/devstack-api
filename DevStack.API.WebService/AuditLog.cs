using DevStack.API.DataAccess;
using DevStack.API.Models;

namespace DevStack.API.WebService;

// Lightweight audit trail helper: call right before SaveChangesAsync in write
// endpoints. Tenant-scoped (ShopId) so the admin Audit tab sees only its shop;
// platform actions log with a null ShopId (visible to superadmin via
// IgnoreQueryFilters).
public static class AuditLog
{
    public static Task Write(DevStackDataModel db, int? shopId, int? userId, string action, string detail)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            ShopId = shopId,
            UserId = userId,
            Action = action,
            Detail = detail,
            CreatedAtUtc = DateTime.UtcNow
        });
        return Task.CompletedTask;
    }
}
