using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Customer directory + house accounts. Balance > 0 = customer owes the shop
// (orders charged to their tab); settle reduces it. All admin-only.
[ApiController]
[Route("api/[controller]")]
public class CustomersController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public CustomersController(DevStackDataModel db, ICurrentShop currentShop)
    {
        _db = db;
        _currentShop = currentShop;
    }

    private int UserId => int.Parse(User.FindFirstValue("userId")!);

    public record CustomerWrite(string Name, string? Phone, string? Email, decimal CreditLimit, string? Notes);
    public record SettleRequest(decimal Amount, string Method);

    [Authorize(Roles = "admin,manager")]
    [HttpGet]
    public async Task<ActionResult> GetAll([FromQuery] string? q = null)
    {
        var query = _db.Customers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c => c.Name.Contains(term) || (c.Phone != null && c.Phone.Contains(term)));
        }
        var customers = await query.OrderBy(c => c.Name).AsNoTracking().ToListAsync();

        // Aggregate order stats in ONE grouped query instead of 2 blocking
        // queries per customer (the old N+1: Count + Sum inside the in-memory
        // Select). OrderCount counts all orders; OrderTotal excludes voided -
        // same semantics as before.
        var ids = customers.Select(c => c.Id).ToList();
        var agg = (await _db.Orders.AsNoTracking()
            .Where(o => o.AccountCustomerId != null && ids.Contains(o.AccountCustomerId.Value))
            .GroupBy(o => o.AccountCustomerId!.Value)
            .Select(g => new
            {
                CustomerId = g.Key,
                OrderCount = g.Count(),
                OrderTotal = g.Where(o => o.VoidedAt == null).Sum(o => (decimal?)o.Total) ?? 0
            })
            .ToListAsync())
            .ToDictionary(a => a.CustomerId);

        return Ok(customers.Select(c =>
        {
            agg.TryGetValue(c.Id, out var a);
            return new
            {
                c.Id, c.Name, c.Phone, c.Email, c.CreditLimit, c.Balance, c.Notes, c.CreatedAt,
                OrderCount = a?.OrderCount ?? 0,
                OrderTotal = a?.OrderTotal ?? 0m
            };
        }));
    }

    [Authorize(Roles = "admin,manager")]
    [HttpPost]
    public async Task<ActionResult> Create(CustomerWrite request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "Customer name is required." });

        var customer = new Customer
        {
            ShopId = _currentShop.ShopId,
            Name = name,
            Phone = Trimmed(request.Phone, 50),
            Email = Trimmed(request.Email, 100),
            CreditLimit = Math.Max(0, request.CreditLimit),
            Notes = Trimmed(request.Notes, 500),
            CreatedAt = DateTime.UtcNow.AddHours(2)
        };
        _db.Customers.Add(customer);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "customer_create", $"'{customer.Name}'");
        await _db.SaveChangesAsync();
        return Ok(customer);
    }

    [Authorize(Roles = "admin,manager")]
    [HttpPut("{id:int}")]
    public async Task<ActionResult> Update(int id, CustomerWrite request)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();

        var oldName = customer.Name;
        customer.Name = request.Name?.Trim() ?? customer.Name;
        customer.Phone = Trimmed(request.Phone, 50);
        customer.Email = Trimmed(request.Email, 100);
        customer.CreditLimit = Math.Max(0, request.CreditLimit);
        customer.Notes = Trimmed(request.Notes, 500);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "customer_update", $"'{oldName}' → '{customer.Name}'");
        await _db.SaveChangesAsync();
        return Ok(customer);
    }

    [Authorize(Roles = "admin,manager")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();
        if (customer.Balance > 0.001m)
            return BadRequest(new { error = $"'{customer.Name}' still owes R{customer.Balance:0.00} - settle the account first." });

        _db.Customers.Remove(customer);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "customer_delete", $"'{customer.Name}'");
        await _db.SaveChangesAsync();
        return Ok();
    }

    // POST /api/customers/{id}/settle - reduce the balance (cash/card/account).
    [Authorize(Roles = "admin,manager")]
    [HttpPost("{id:int}/settle")]
    public async Task<IActionResult> Settle(int id, SettleRequest request)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();
        if (request.Amount <= 0)
            return BadRequest(new { error = "Settlement amount must be positive." });

        var method = request.Method is "cash" or "card" or "account" ? request.Method : "cash";
        var amount = Math.Round(Math.Min(request.Amount, customer.Balance), 2);
        if (amount <= 0)
            return BadRequest(new { error = "This customer has no balance to settle." });

        customer.Balance -= amount;
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "customer_settle", $"'{customer.Name}' R{amount:0.00} ({method})");
        await _db.SaveChangesAsync();
        return Ok(new { customer.Id, customer.Balance, settled = amount });
    }

    private static string? Trimmed(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : (v.Length > max ? v[..max] : v);
    }
}
