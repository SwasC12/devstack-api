using DevStack.API.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Full audit trail viewer: every logged write with actor + timestamp.
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
public class AuditController : ControllerBase
{
    private readonly DevStackDataModel _db;

    public AuditController(DevStackDataModel db) => _db = db;

    [HttpGet]
    public async Task<ActionResult> GetAll([FromQuery] string? from = null, [FromQuery] string? to = null, [FromQuery] int limit = 300)
    {
        var query = _db.AuditLog.AsQueryable();
        if (DateTime.TryParse(from, out var f)) query = query.Where(a => a.CreatedAtUtc >= f.Date);
        if (DateTime.TryParse(to, out var t)) query = query.Where(a => a.CreatedAtUtc < t.Date.AddDays(1));
        var entries = await query.OrderByDescending(a => a.CreatedAtUtc).Take(Math.Clamp(limit, 1, 1000)).ToListAsync();

        var userIds = entries.Where(a => a.UserId is not null).Select(a => a.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return Ok(entries.Select(a => new
        {
            a.Id, a.Action, a.Detail,
            a.CreatedAtUtc,
            By = a.UserId is not null && users.TryGetValue(a.UserId.Value, out var n) ? n : "system"
        }));
    }
}
