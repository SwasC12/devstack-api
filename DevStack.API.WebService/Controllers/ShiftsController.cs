using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ShiftsController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public ShiftsController(DevStackDataModel db, ICurrentShop currentShop)
    {
        _db = db;
        _currentShop = currentShop;
    }

    public record StartShiftRequest(decimal? StartingFloat = null);

    private int UserId => int.Parse(User.FindFirstValue("userId")!);

    [HttpGet("active")]
    public async Task<ActionResult> GetActive()
    {
        // NOTE: filter on EndTime, never on the computed Shift.IsActive — EF
        // can't translate the CLR getter to SQL and every shift query would 500.
        var shift = await _db.Shifts
            .Where(s => s.UserId == UserId && s.EndTime == null)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (shift is null) return Ok(new { active = false });

        return Ok(new { active = true, id = shift.Id, startTime = shift.StartTime });
    }

    [HttpPost("start")]
    public async Task<ActionResult> Start(StartShiftRequest request)
    {
        // End any existing active shift first
        var active = await _db.Shifts.Where(s => s.UserId == UserId && s.EndTime == null).ToListAsync();
        foreach (var s in active) s.EndTime = DateTime.UtcNow.AddHours(2);

        var shift = new Shift { UserId = UserId, StartTime = DateTime.UtcNow.AddHours(2), ShopId = _currentShop.ShopId, StartingFloat = request.StartingFloat ?? 0m };
        _db.Shifts.Add(shift);
        await _db.SaveChangesAsync();
        return Ok(new { id = shift.Id, startTime = shift.StartTime, startingFloat = shift.StartingFloat });
    }

    [HttpPost("end")]
    public async Task<IActionResult> End()
    {
        var shift = await _db.Shifts
            .Where(s => s.UserId == UserId && s.EndTime == null)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (shift is null) return BadRequest(new { error = "No active shift." });

        shift.EndTime = DateTime.UtcNow.AddHours(2);
        await _db.SaveChangesAsync();
        return Ok();
    }

    // GET /api/shifts/summary — the caller's latest shift with its sales
    // (orders attributed to them, voided excluded). Shown at clock-out.
    [HttpGet("summary")]
    public async Task<ActionResult> GetSummary()
    {
        var shift = await _db.Shifts
            .Where(s => s.UserId == UserId)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (shift is null) return Ok(new { shift = (object?)null, orderCount = 0, itemCount = 0, revenue = 0m, averageOrder = 0m });

        var end = shift.EndTime ?? DateTime.UtcNow.AddHours(2);
        var orders = await _db.Orders
            .Where(o => o.UserId == UserId
                && o.CreatedAt >= shift.StartTime
                && o.CreatedAt <= end
                && o.VoidedAt == null)
            .Include(o => o.Items)
            .ToListAsync();

        var voidedCount = await _db.Orders.CountAsync(o => o.UserId == UserId
            && o.CreatedAt >= shift.StartTime
            && o.CreatedAt <= end
            && o.VoidedAt != null);

        var itemCount = orders.Sum(o => o.Items.Sum(i => i.Quantity));
        var revenue = orders.Sum(o => o.Total);
        var cashRevenue = orders.Where(o => o.PaymentMethod == "cash").Sum(o => o.Total);
        var cardRevenue = revenue - cashRevenue;

        return Ok(new
        {
            shift = new { shift.Id, shift.StartTime, shift.EndTime, shift.StartingFloat, IsActive = shift.EndTime is null },
            orderCount = orders.Count,
            itemCount,
            revenue,
            averageOrder = orders.Count > 0 ? revenue / orders.Count : 0m,
            cashRevenue,
            cardRevenue,
            voidedCount,
            startingFloat = shift.StartingFloat
        });
    }

    // GET /api/shifts/timesheet?from=yyyy-MM-dd&to=yyyy-MM-dd - admin: hours
    // worked per employee for a date range (feeds payroll). Every shop user is
    // listed (zero-hour employees included so nobody is missed); open shifts
    // count up to "now" and are flagged active. Times are SAST.
    [Authorize(Roles = "admin")]
    [HttpGet("timesheet")]
    public async Task<ActionResult> GetTimesheet(string? from, string? to)
    {
        var now = DateTime.UtcNow.AddHours(2);
        var fromDate = DateTime.TryParseExact(from, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var f)
            ? f.Date : now.Date;
        var toDate = DateTime.TryParseExact(to, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var t)
            ? t.Date : now.Date;
        if (toDate < fromDate) toDate = fromDate;
        var rangeEnd = toDate.AddDays(1); // exclusive upper bound

        var users = await _db.Users
            .Where(u => u.ShopId == _currentShop.ShopId)
            .Select(u => new { u.Id, u.DisplayName, u.Role, u.WageRate })
            .ToListAsync();

        var shifts = await _db.Shifts
            .Where(s => s.StartTime >= fromDate && s.StartTime < rangeEnd)
            .OrderBy(s => s.StartTime)
            .ToListAsync();

        var employees = users.Select(u =>
        {
            var theirs = shifts.Where(s => s.UserId == u.Id).ToList();
            var rows = theirs.Select(s =>
            {
                var end = s.EndTime ?? now;
                var hours = Math.Round((decimal)(end - s.StartTime).TotalHours, 2);
                return new
                {
                    s.Id,
                    s.StartTime,
                    EndTime = s.EndTime,
                    hours,
                    Active = s.EndTime is null
                };
            }).ToList();
            var totalHours = Math.Round(rows.Sum(r => r.hours), 2);
            return new
            {
                u.Id,
                u.DisplayName,
                u.Role,
                u.WageRate,
                shiftCount = theirs.Count,
                totalHours,
                wageCost = u.WageRate is decimal rate ? Math.Round(totalHours * rate, 2) : 0m,
                shifts = rows
            };
        }).OrderByDescending(e => e.totalHours).ToList();

        return Ok(new { from = fromDate, to = toDate, employees });
    }
}
