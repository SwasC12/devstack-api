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

    public OrdersController(DevStackDataModel db) => _db = db;

    public record PlaceOrderRequest(List<OrderItemRequest> Items);
    public record OrderItemRequest(int MenuItemId, string Name, decimal Price, int Quantity);

    // POST /api/orders — place an order (anyone can, no auth needed for POS)
    [HttpPost]
    public async Task<ActionResult<Order>> PlaceOrder(PlaceOrderRequest request)
    {
        var order = new Order
        {
            CreatedAt = DateTime.UtcNow.AddHours(2),
            Total = request.Items.Sum(i => i.Price * i.Quantity),
            Items = request.Items.Select(i => new OrderItem
            {
                MenuItemId = i.MenuItemId,
                Name = i.Name,
                Price = i.Price,
                Quantity = i.Quantity
            }).ToList()
        };

        _db.Orders.Add(order);

        // Decrement stock for each item
        foreach (var item in request.Items)
        {
            var menuItem = await _db.MenuItems.FindAsync(item.MenuItemId);
            if (menuItem is not null)
                menuItem.StockQuantity = Math.Max(0, menuItem.StockQuantity - item.Quantity);
        }

        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }

    // GET /api/orders — admin only
    [Authorize(Roles = "admin")]
    [HttpGet]
    public async Task<ActionResult<List<Order>>> GetOrders()
    {
        var orders = await _db.Orders.Include(o => o.Items).OrderByDescending(o => o.CreatedAt).ToListAsync();
        return Ok(orders);
    }

    // GET /api/orders/5
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Order>> GetOrder(int id)
    {
        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        return order is null ? NotFound() : Ok(order);
    }

    // GET /api/orders/summary — analytics (admin only)
    [Authorize(Roles = "admin")]
    [HttpGet("summary")]
    public async Task<ActionResult> GetSummary()
    {
        var today = DateTime.UtcNow.AddHours(2).Date;
        var orders = await _db.Orders.Include(o => o.Items).ToListAsync();

        var revenue = orders.Sum(o => o.Total);
        var todayRevenue = orders.Where(o => o.CreatedAt >= today).Sum(o => o.Total);

        var topItems = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => i.Name)
            .Select(g => new { Name = g.Key, Quantity = g.Sum(i => i.Quantity), Revenue = g.Sum(i => i.Price * i.Quantity) })
            .OrderByDescending(g => g.Quantity)
            .Take(10)
            .ToList();

        return Ok(new
        {
            totalOrders = orders.Count,
            totalRevenue = revenue,
            todayRevenue,
            todayOrders = orders.Count(o => o.CreatedAt >= today),
            topItems
        });
    }
}
