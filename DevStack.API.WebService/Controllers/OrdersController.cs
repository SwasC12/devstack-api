using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public OrdersController(DevStackDataModel db, ICurrentShop currentShop)
    {
        _db = db;
        _currentShop = currentShop;
    }

    public record PlaceOrderRequest(List<OrderItemRequest> Items);
    public record OrderItemRequest(int MenuItemId, string Name, decimal Price, int Quantity);
    public record VoidOrderRequest(string Reason);

    // POST /api/orders — place an order. Authorized so the order is tied to the
    // authenticated user's shop (POS is always signed in).
    //
    // Correctness: the client only says WHAT and HOW MANY. Price, name and
    // stock come from the database here — a buggy/malicious client can't sell
    // a cappuccino for R0.01. Stock is decremented with a conditional UPDATE
    // inside a transaction, so two tablets checking out the same item at once
    // can't oversell.
    [HttpPost]
    [Authorize]
    public async Task<ActionResult<Order>> PlaceOrder(PlaceOrderRequest request)
    {
        // Aggregate per menu item so the stock check holds even if the same
        // item appears more than once in the payload.
        var quantities = request.Items
            .GroupBy(i => i.MenuItemId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

        if (quantities.Count == 0)
            return BadRequest(new { error = "Order is empty." });

        var userId = int.Parse(User.FindFirstValue("userId")!);
        var order = new Order
        {
            CreatedAt = DateTime.UtcNow.AddHours(2),
            ShopId = _currentShop.ShopId,
            UserId = userId,
            Items = []
        };

        await using var tx = await _db.Database.BeginTransactionAsync();

        foreach (var (menuItemId, quantity) in quantities)
        {
            var menuItem = await _db.MenuItems.FindAsync(menuItemId);
            if (menuItem is null)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = "An item in this order no longer exists." });
            }
            if (!menuItem.IsAvailable)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = $"'{menuItem.Name}' is no longer available." });
            }

            // Atomic check-and-decrement: the UPDATE only affects the row if
            // enough stock remains right now, so concurrent orders can't both
            // grab the last one. Zero rows affected = stock ran out.
            var updated = await _db.MenuItems
                .Where(m => m.Id == menuItemId && m.StockQuantity >= quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.StockQuantity, m => m.StockQuantity - quantity));

            if (updated == 0)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = $"Not enough stock for '{menuItem.Name}' — only {menuItem.StockQuantity} left." });
            }

            // Server-side snapshot: DB price + DB name, never the client's.
            order.Items.Add(new OrderItem
            {
                MenuItemId = menuItemId,
                Name = menuItem.Name,
                Price = menuItem.Price,
                Quantity = quantity
            });
        }

        order.Total = order.Items.Sum(i => i.Price * i.Quantity);
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }

    // GET /api/orders — admin only. Enriched with the cashier's display name
    // so the history view can say who sold what.
    [Authorize(Roles = "admin")]
    [HttpGet]
    public async Task<ActionResult> GetOrders()
    {
        var orders = await _db.Orders.Include(o => o.Items).OrderByDescending(o => o.CreatedAt).ToListAsync();

        var userIds = orders.Where(o => o.UserId is not null).Select(o => o.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return Ok(orders.Select(o => new
        {
            o.Id,
            o.CreatedAt,
            o.Total,
            o.ShopId,
            o.UserId,
            CashierName = o.UserId is not null && users.TryGetValue(o.UserId.Value, out var name) ? name : null,
            o.VoidedAt,
            o.VoidedByUserId,
            o.VoidReason,
            Items = o.Items.Select(i => new { i.Id, i.MenuItemId, i.Name, i.Price, i.Quantity })
        }));
    }

    // POST /api/orders/{id}/void — admin only. The one-way door, now with a
    // key: a voided order is excluded from revenue and its stock is restored.
    [Authorize(Roles = "admin")]
    [HttpPost("{id:int}/void")]
    public async Task<IActionResult> VoidOrder(int id, VoidOrderRequest request)
    {
        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.VoidedAt is not null)
            return BadRequest(new { error = "This order is already voided." });

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            return BadRequest(new { error = "A reason is required to void an order." });

        order.VoidedAt = DateTime.UtcNow.AddHours(2);
        order.VoidedByUserId = int.Parse(User.FindFirstValue("userId")!);
        order.VoidReason = reason;

        // Put the stock back, one line at a time (quantities are already
        // aggregated, so the total restock is exact).
        foreach (var line in order.Items.GroupBy(i => i.MenuItemId))
        {
            var qty = line.Sum(i => i.Quantity);
            await _db.MenuItems
                .Where(m => m.Id == line.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.StockQuantity, m => m.StockQuantity + qty));
        }

        await _db.SaveChangesAsync();
        return Ok();
    }

    // GET /api/orders/5 — any logged-in user (tenant-scoped by the global filter).
    [Authorize]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Order>> GetOrder(int id)
    {
        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        return order is null ? NotFound() : Ok(order);
    }

    // GET /api/orders/summary — analytics (admin only). Voided orders are
    // excluded from every figure: revenue means money actually taken.
    [Authorize(Roles = "admin")]
    [HttpGet("summary")]
    public async Task<ActionResult> GetSummary()
    {
        var today = DateTime.UtcNow.AddHours(2).Date;
        var orders = await _db.Orders.Include(o => o.Items).ToListAsync();
        var live = orders.Where(o => o.VoidedAt is null).ToList();

        var revenue = live.Sum(o => o.Total);
        var todayRevenue = live.Where(o => o.CreatedAt >= today).Sum(o => o.Total);

        var topItems = live
            .SelectMany(o => o.Items)
            .GroupBy(i => i.Name)
            .Select(g => new { Name = g.Key, Quantity = g.Sum(i => i.Quantity), Revenue = g.Sum(i => i.Price * i.Quantity) })
            .OrderByDescending(g => g.Quantity)
            .Take(10)
            .ToList();

        return Ok(new
        {
            totalOrders = live.Count,
            totalRevenue = revenue,
            todayRevenue,
            todayOrders = live.Count(o => o.CreatedAt >= today),
            voidedOrders = orders.Count(o => o.VoidedAt is not null),
            topItems
        });
    }
}
