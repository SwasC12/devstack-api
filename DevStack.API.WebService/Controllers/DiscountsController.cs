using DevStack.API.DataAccess;
using DevStack.API.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Discounts & specials (scheduled discounts). Same shape as categories:
//   GET    api/discounts    → any logged-in user (the POS lists live ones)
//   PUT    api/discounts    → admin: create / edit
//   DELETE api/discounts/5  → admin
// Money is computed server-side at checkout; these endpoints never receive an
// amount from the client beyond the configured value.
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DiscountsController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public DiscountsController(DevStackDataModel db, ICurrentShop currentShop)
    {
        _db = db;
        _currentShop = currentShop;
    }

    private int UserId => int.Parse(User.FindFirstValue("userId") ?? "0");

    // GET api/discounts — every discount for this shop, plus whether it is live
    // RIGHT NOW (SAST), so the POS can badge "happy hour" without clock math.
    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        var now = DateTime.UtcNow.AddHours(2);
        var discounts = await _db.Discounts
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.Type,
                d.Value,
                d.IsActive,
                d.DayOfWeek,
                d.StartTime,
                d.EndTime
            })
            .ToListAsync();

        // IsLiveAt needs the full entity; evaluate per row (cheap, small list).
        var live = await _db.Discounts.ToDictionaryAsync(d => d.Id, d => d.IsLiveAt(now));

        return Ok(discounts.Select(d => new
        {
            d.Id,
            d.Name,
            d.Type,
            d.Value,
            d.IsActive,
            d.DayOfWeek,
            d.StartTime,
            d.EndTime,
            IsLive = live.TryGetValue(d.Id, out var l) && l
        }));
    }

    // PUT api/discounts — the single write path: Id == 0 creates, else edits.
    [Authorize(Roles = "admin")]
    [HttpPut]
    public async Task<ActionResult<Discount>> Write(Discount discount)
    {
        discount.Name = discount.Name.Trim();
        if (discount.Name.Length == 0)
            return BadRequest(new { error = "Discount name cannot be empty." });
        if (discount.Type is not ("percent" or "fixed"))
            return BadRequest(new { error = "Discount type must be 'percent' or 'fixed'." });
        if (discount.Value <= 0)
            return BadRequest(new { error = "Discount value must be greater than zero." });
        if (discount.Type == "percent" && discount.Value > 100)
            return BadRequest(new { error = "Percentage discount cannot exceed 100%." });
        if (discount.StartTime is not null && discount.EndTime is not null && discount.EndTime <= discount.StartTime)
            return BadRequest(new { error = "End time must be after the start time." });

        discount.ShopId = _currentShop.ShopId;

        if (discount.Id == 0)
        {
            _db.Discounts.Add(discount);
            await AuditLog.Write(_db, _currentShop.ShopId, UserId, "discount_create", $"'{discount.Name}' ({discount.Type} {discount.Value})");
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetAll), new { id = discount.Id }, discount);
        }

        var existing = await _db.Discounts.FindAsync(discount.Id);
        if (existing is null) return NotFound();

        existing.Name = discount.Name;
        existing.Type = discount.Type;
        existing.Value = discount.Value;
        existing.IsActive = discount.IsActive;
        existing.DayOfWeek = discount.DayOfWeek;
        existing.StartTime = discount.StartTime;
        existing.EndTime = discount.EndTime;
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "discount_update", $"'{existing.Name}' ({existing.Type} {existing.Value})");
        await _db.SaveChangesAsync();
        return Ok(existing);
    }

    // DELETE api/discounts/5 — admin.
    [Authorize(Roles = "admin")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var discount = await _db.Discounts.FindAsync(id);
        if (discount is null) return NotFound();

        _db.Discounts.Remove(discount);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "discount_delete", $"'{discount.Name}'");
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
