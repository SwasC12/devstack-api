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
    public async Task<ActionResult> Start()
    {
        // End any existing active shift first
        var active = await _db.Shifts.Where(s => s.UserId == UserId && s.EndTime == null).ToListAsync();
        foreach (var s in active) s.EndTime = DateTime.UtcNow.AddHours(2);

        var shift = new Shift { UserId = UserId, StartTime = DateTime.UtcNow.AddHours(2), ShopId = _currentShop.ShopId };
        _db.Shifts.Add(shift);
        await _db.SaveChangesAsync();
        return Ok(new { id = shift.Id, startTime = shift.StartTime });
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
}
