using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Expenses & petty cash: money out of the till. Admin-only; feeds the journal
// and the cash-up (net after expenses).
[ApiController]
[Route("api/[controller]")]
public class ExpensesController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public ExpensesController(DevStackDataModel db, ICurrentShop currentShop)
    {
        _db = db;
        _currentShop = currentShop;
    }

    private int UserId => int.Parse(User.FindFirstValue("userId")!);

    public record ExpenseWrite(string Category, decimal Amount, string? Note);

    [Authorize(Roles = "admin,manager")]
    [HttpGet]
    public async Task<ActionResult> GetAll([FromQuery] string? from = null, [FromQuery] string? to = null)
    {
        var query = _db.Expenses.AsQueryable();
        if (DateTime.TryParse(from, out var f)) query = query.Where(e => e.CreatedAt >= f.Date);
        if (DateTime.TryParse(to, out var t)) query = query.Where(e => e.CreatedAt < t.Date.AddDays(1));
        var expenses = await query.OrderByDescending(e => e.CreatedAt).Take(500).ToListAsync();

        var userIds = expenses.Where(e => e.UserId is not null).Select(e => e.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return Ok(new
        {
            total = expenses.Sum(e => e.Amount),
            items = expenses.Select(e => new
            {
                e.Id, e.Category, e.Amount, e.Note, e.CreatedAt,
                By = e.UserId is not null && users.TryGetValue(e.UserId.Value, out var n) ? n : null
            })
        });
    }

    [Authorize(Roles = "admin,manager")]
    [HttpPost]
    public async Task<ActionResult> Create(ExpenseWrite request)
    {
        var category = request.Category?.Trim();
        if (string.IsNullOrEmpty(category))
            return BadRequest(new { error = "Expense category is required." });
        if (request.Amount <= 0)
            return BadRequest(new { error = "Expense amount must be positive." });

        var expense = new Expense
        {
            ShopId = _currentShop.ShopId,
            Category = category,
            Amount = Math.Round(request.Amount, 2),
            Note = request.Note?.Trim(),
            CreatedAt = DateTime.UtcNow.AddHours(2),
            UserId = UserId
        };
        _db.Expenses.Add(expense);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "expense_add", $"'{category}' R{expense.Amount:0.00}");
        await _db.SaveChangesAsync();
        return Ok(expense);
    }

    [Authorize(Roles = "admin,manager")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var expense = await _db.Expenses.FirstOrDefaultAsync(e => e.Id == id);
        if (expense is null) return NotFound();
        _db.Expenses.Remove(expense);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "expense_delete", $"'{expense.Category}' R{expense.Amount:0.00}");
        await _db.SaveChangesAsync();
        return Ok();
    }
}
