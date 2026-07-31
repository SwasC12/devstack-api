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

    // POST /api/orders — place an order. Authorized so the order is tied to the
    // authenticated user's shop (POS is always signed in).
    [HttpPost]
    [Authorize]
    public async Task<ActionResult<Order>> PlaceOrder(PlaceOrderRequest request)
    {
        // Aggregate per menu item so the stock check holds even if the same
        // item appears more than once in the payload.
        var quantities = request.Items
            .GroupBy(i => i.MenuItemId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

        // Refuse to oversell: every line must be in stock before the order is
        // saved, otherwise a quantity over stock would silently be sold.
        var menuItems = new Dictionary<int, MenuItem>();
        foreach (var (menuItemId, quantity) in quantities)
        {
            if (!menuItems.TryGetValue(menuItemId, out var menuItem))
            {
                menuItem = await _db.MenuItems.FindAsync(menuItemId);
                if (menuItem is null)
                    return BadRequest(new { error = "An item in this order no longer exists." });
                menuItems[menuItemId] = menuItem;
            }

            if (!menuItem.IsAvailable)
                return BadRequest(new { error = $"'{menuItem.Name}' is no longer available." });

            if (quantity > menuItem.StockQuantity)
                return BadRequest(new { error = $"Not enough stock for '{menuItem.Name}' — only {menuItem.StockQuantity} left." });
        }

        var order = new Order
        {
            CreatedAt = DateTime.UtcNow.AddHours(2),
            Total = request.Items.Sum(i => i.Price * i.Quantity),
            ShopId = _currentShop.ShopId,
            Items = request.Items.Select(i => new OrderItem
            {
                MenuItemId = i.MenuItemId,
                Name = i.Name,
                Price = i.Price,
                Quantity = i.Quantity
            }).ToList()
        };

        _db.Orders.Add(order);

        // Decrement stock. Validated above, so this can't go negative.
        foreach (var (menuItemId, quantity) in quantities)
            menuItems[menuItemId].StockQuantity -= quantity;

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
