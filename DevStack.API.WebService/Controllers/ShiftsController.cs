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

    public ShiftsController(DevStackDataModel db) => _db = db;

    private int UserId => int.Parse(User.FindFirstValue("userId")!);

    [HttpGet("active")]
    public async Task<ActionResult> GetActive()
    {
        var shift = await _db.Shifts
            .Where(s => s.UserId == UserId && s.IsActive)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (shift is null) return Ok(new { active = false });

        return Ok(new { active = true, id = shift.Id, startTime = shift.StartTime });
    }

    [HttpPost("start")]
    public async Task<ActionResult> Start()
    {
        // End any existing active shift first
        var active = await _db.Shifts.Where(s => s.UserId == UserId && s.IsActive).ToListAsync();
        foreach (var s in active) s.EndTime = DateTime.UtcNow.AddHours(2);

        var shift = new Shift { UserId = UserId, StartTime = DateTime.UtcNow.AddHours(2) };
        _db.Shifts.Add(shift);
        await _db.SaveChangesAsync();
        return Ok(new { id = shift.Id, startTime = shift.StartTime });
    }

    [HttpPost("end")]
    public async Task<IActionResult> End()
    {
        var shift = await _db.Shifts
            .Where(s => s.UserId == UserId && s.IsActive)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (shift is null) return BadRequest(new { error = "No active shift." });

        shift.EndTime = DateTime.UtcNow.AddHours(2);
        await _db.SaveChangesAsync();
        return Ok();
    }
}
