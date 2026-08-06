using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using DevStack.API.PlatformLogic.PushLogic;
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
    private readonly IPushService _push;

    public OrdersController(DevStackDataModel db, ICurrentShop currentShop, IPushService push)
    {
        _db = db;
        _currentShop = currentShop;
        _push = push;
    }

    public record PlaceOrderRequest(List<OrderItemRequest> Items, string? PaymentMethod = null, decimal? AmountReceived = null, int? DiscountId = null, string? CustomerName = null, string? CustomerPhone = null, string? Notes = null);
    public record OrderItemRequest(int MenuItemId, string Name, decimal Price, int Quantity, int? SizeId = null, string? Note = null, List<int>? ModifierIds = null);
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
        // Aggregate per (item, size, modifier set) so distinct configurations
        // stay separate lines and the stock check holds per configuration.
        static string ModsKey(List<int>? ids) => string.Join(",", (ids ?? []).OrderBy(x => x));
        var groups = request.Items
            .GroupBy(i => (i.MenuItemId, i.SizeId, ModsKey(i.ModifierIds)))
            .Select(g => (Key: g.Key, Qty: g.Sum(i => i.Quantity), Line: g.First()))
            .ToList();

        if (groups.Count == 0)
            return BadRequest(new { error = "Order is empty." });

        var userId = int.Parse(User.FindFirstValue("userId")!);
        var method = request.PaymentMethod?.Trim().ToLowerInvariant() == "card" ? "card" : "cash";
        var order = new Order
        {
            CreatedAt = DateTime.UtcNow.AddHours(2),
            ShopId = _currentShop.ShopId,
            UserId = userId,
            PaymentMethod = method,
            Items = [],
            CustomerName = Trimmed(request.CustomerName, 100),
            CustomerPhone = Trimmed(request.CustomerPhone, 50),
            Notes = Trimmed(request.Notes, 1000)
        };

        await using var tx = await _db.Database.BeginTransactionAsync();

        var lowStockAlerts = new List<(string Name, int Remaining)>();

        foreach (var (key, quantity, line) in groups)
        {
            var (menuItemId, sizeId, _) = key;
            var menuItem = await _db.MenuItems
                .Include(m => m.Sizes)
                .Include(m => m.ModifierGroups).ThenInclude(g => g.Modifiers)
                .FirstOrDefaultAsync(m => m.Id == menuItemId);
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

            // Size rules: sized items MUST be ordered with one of their sizes
            // (price comes from the size); single-price items must NOT carry one.
            MenuSize? size = null;
            if (menuItem.Sizes.Count > 0)
            {
                size = sizeId is null ? null : menuItem.Sizes.FirstOrDefault(s => s.Id == sizeId);
                if (size is null)
                {
                    await tx.RollbackAsync();
                    var opts = string.Join(", ", menuItem.Sizes.Select(s => s.Name));
                    return BadRequest(new { error = $"'{menuItem.Name}' needs a size ({opts})." });
                }
            }
            else if (sizeId is not null)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = $"'{menuItem.Name}' has no sizes to select." });
            }

            // Modifier rules: ids must belong to this item; single-choice groups
            // take at most one. Prices come from the DB, never the client.
            var modDeltas = 0m;
            var modSnapshots = new List<OrderItemModifier>();
            var requestedModIds = line.ModifierIds ?? [];
            var knownModIds = menuItem.ModifierGroups.SelectMany(g => g.Modifiers.Select(m => m.Id)).ToHashSet();
            if (requestedModIds.Any(id => !knownModIds.Contains(id)))
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = $"'{menuItem.Name}' has invalid modifiers." });
            }
            foreach (var group in menuItem.ModifierGroups)
            {
                var picked = requestedModIds.Where(id => group.Modifiers.Any(m => m.Id == id)).ToList();
                if (picked.Count == 0) continue;
                if (!group.IsMulti && picked.Count > 1)
                {
                    await tx.RollbackAsync();
                    return BadRequest(new { error = $"Pick at most one option from '{group.Name}'." });
                }
                foreach (var modId in picked)
                {
                    var mod = group.Modifiers.First(m => m.Id == modId);
                    modDeltas += mod.PriceDelta;
                    modSnapshots.Add(new OrderItemModifier { GroupName = group.Name, Name = mod.Name, PriceDelta = mod.PriceDelta });
                }
            }

            // Atomic check-and-decrement: the UPDATE only affects the row if
            // enough stock remains right now, so concurrent orders can't both
            // grab the last one. Zero rows affected = stock ran out.
            var updated = await _db.MenuItems
                .Where(m => m.Id == menuItemId && m.StockQuantity >= quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.StockQuantity, m => m.StockQuantity - quantity));

            // Track anything that just crossed its low-stock threshold (the
            // tracked entity is stale - ExecuteUpdate bypasses the change
            // tracker, so read the post-decrement value fresh).
            if (updated > 0)
            {
                var after = await _db.MenuItems.Where(m => m.Id == menuItemId)
                    .Select(m => new { m.Name, m.StockQuantity, m.LowStockThreshold }).FirstAsync();
                if (after.StockQuantity <= after.LowStockThreshold)
                    lowStockAlerts.Add((after.Name, after.StockQuantity));
            }

            if (updated == 0)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = $"Not enough stock for '{menuItem.Name}' — only {menuItem.StockQuantity} left." });
            }

            // Server-side snapshot: DB price + DB name, never the client's.
            // Sized items snapshot the chosen size too, so receipts and reports
            // stay correct even if the size is later renamed or deleted.
            var unitPrice = (size?.Price ?? menuItem.Price) + modDeltas;
            order.Items.Add(new OrderItem
            {
                MenuItemId = menuItemId,
                Name = menuItem.Name,
                Price = unitPrice,
                Quantity = quantity,
                SizeId = size?.Id,
                SizeName = size?.Name,
                Note = Trimmed(line.Note, 500),
                Modifiers = modSnapshots
            });
        }

        order.Total = order.Items.Sum(i => i.Price * i.Quantity);

        // Discount: only the ID comes from the client — existence, schedule and
        // the amount are decided here, server-side.
        if (request.DiscountId is not null)
        {
            var discount = await _db.Discounts.FirstOrDefaultAsync(d => d.Id == request.DiscountId);
            if (discount is null || !discount.IsLiveAt(DateTime.UtcNow.AddHours(2)))
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = "That discount isn't available right now." });
            }

            order.DiscountId = discount.Id;
            order.DiscountName = discount.Name;
            order.DiscountAmount = discount.Type == "percent"
                ? Math.Round(order.Total * discount.Value / 100m, 2)
                : Math.Min(discount.Value, order.Total);
            order.Total -= order.DiscountAmount;
        }

        // Cash: the tendered amount must cover the total; change is computed
        // server-side, never trusted from the client.
        if (method == "cash")
        {
            if (request.AmountReceived is null || request.AmountReceived < 0)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = "Cash payment needs the amount received." });
            }
            if (request.AmountReceived < order.Total)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = "Amount received is less than the total." });
            }
            order.AmountReceived = request.AmountReceived;
            order.ChangeGiven = request.AmountReceived - order.Total;
        }

        _db.Orders.Add(order);

        // Low-stock alerts: one in-app notification per shop admin, plus a
        // device push to each registered admin tablet/phone.
        List<Notification>? alertRows = null;
        List<PushToken>? alertTokens = null;
        if (lowStockAlerts.Count > 0)
        {
            var admins = await _db.Users.Where(u => u.Role == "admin" && u.ShopId == order.ShopId).ToListAsync();
            if (admins.Count > 0)
            {
                var now = DateTime.UtcNow;
                alertRows = new List<Notification>();
                foreach (var a in lowStockAlerts)
                {
                    var body = $"'{a.Name}' is running low - only {a.Remaining} left.";
                    foreach (var admin in admins)
                        alertRows.Add(new Notification { ShopId = order.ShopId, UserId = admin.Id, Title = "Low stock", Body = body, Type = "alert", CreatedAtUtc = now });
                }
                _db.Notifications.AddRange(alertRows);
                var adminIds = admins.Select(a => a.Id).ToList();
                alertTokens = await _db.PushTokens.Where(t => adminIds.Contains(t.UserId)).ToListAsync();
            }
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        // Push is best-effort and never fails the order; dead tokens are dropped.
        if (alertRows is not null && alertTokens is not null && alertTokens.Count > 0)
        {
            foreach (var token in alertTokens)
            {
                var row = alertRows.FirstOrDefault(r => r.UserId == token.UserId);
                var ok = await _push.SendAsync(token, "Low stock", row?.Body ?? "An item is running low.", "alert", row?.Id);
                if (ok == false) _db.PushTokens.Remove(token);
            }
            await _db.SaveChangesAsync();
        }

        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }

    // GET /api/orders — admin only. Enriched with the cashier's display name
    // so the history view can say who sold what.
    [Authorize(Roles = "admin")]
    [HttpGet]
    public async Task<ActionResult> GetOrders()
    {
        var orders = await _db.Orders
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .OrderByDescending(o => o.CreatedAt).ToListAsync();

        var userIds = orders.Where(o => o.UserId is not null).Select(o => o.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return Ok(orders.Select(o => new
        {
            o.Id,
            o.CreatedAt,
            o.Total,
            VatAmount = Math.Round(o.Total * 15m / 115m, 2),
            o.ShopId,
            o.UserId,
            CashierName = o.UserId is not null && users.TryGetValue(o.UserId.Value, out var name) ? name : null,
            o.PaymentMethod,
            o.AmountReceived,
            o.ChangeGiven,
            o.DiscountId,
            o.DiscountName,
            o.DiscountAmount,
            o.CustomerName,
            o.CustomerPhone,
            o.Notes,
            o.VoidedAt,
            o.VoidedByUserId,
            o.VoidReason,
            Items = o.Items.Select(i => new
            {
                i.Id, i.MenuItemId, i.Name, i.Price, i.Quantity, i.SizeId, i.SizeName, i.Note,
                Modifiers = i.Modifiers.Select(m => new { m.GroupName, m.Name, m.PriceDelta })
            })
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
        var order = await _db.Orders
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .FirstOrDefaultAsync(o => o.Id == id);
        return order is null ? NotFound() : Ok(order);
    }

    private static string? Trimmed(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : (v.Length > max ? v[..max] : v);
    }

    // GET /api/orders/analytics?days=14 — admin only. Owner analytics: daily
    // revenue series, per-cashier totals and per-category sales. Voided orders
    // excluded; everything scoped to the current shop by the global filter.
    [Authorize(Roles = "admin")]
    [HttpGet("analytics")]
    public async Task<ActionResult> GetAnalytics([FromQuery] int days = 14)
    {
        days = Math.Clamp(days, 1, 90);
        var from = DateTime.UtcNow.AddHours(2).Date.AddDays(-(days - 1));

        var orders = await _db.Orders
            .Where(o => o.VoidedAt == null && o.CreatedAt >= from)
            .Include(o => o.Items)
            .ToListAsync();

        var daily = orders
            .GroupBy(o => o.CreatedAt.Date)
            .OrderBy(g => g.Key)
            .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), revenue = g.Sum(o => o.Total), orders = g.Count() })
            .ToList();

        // Per-cashier: display names joined from Users.
        var userIds = orders.Where(o => o.UserId is not null).Select(o => o.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var cashiers = orders
            .Where(o => o.UserId is not null)
            .GroupBy(o => o.UserId!.Value)
            .Select(g => new
            {
                name = users.TryGetValue(g.Key, out var n) ? n : "Unknown",
                orders = g.Count(),
                revenue = g.Sum(o => o.Total)
            })
            .OrderByDescending(c => c.revenue)
            .ToList();

        // Per-category: line items don't carry the category, so join to the
        // current menu items (deleted/renamed items fall into "Other").
        var menuItems = await _db.MenuItems.ToDictionaryAsync(m => m.Id, m => m.Category);
        var categories = orders
            .SelectMany(o => o.Items)
            .GroupBy(i => menuItems.TryGetValue(i.MenuItemId, out var cat) && !string.IsNullOrWhiteSpace(cat) ? cat : "Other")
            .Select(g => new
            {
                name = g.Key,
                quantity = g.Sum(i => i.Quantity),
                revenue = g.Sum(i => i.Price * i.Quantity)
            })
            .OrderByDescending(g => g.revenue)
            .ToList();

        return Ok(new
        {
            days,
            totals = new
            {
                revenue = orders.Sum(o => o.Total),
                orders = orders.Count,
                items = orders.Sum(o => o.Items.Sum(i => i.Quantity))
            },
            daily,
            cashiers,
            categories
        });
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
            .GroupBy(i => i.SizeName is null ? i.Name : $"{i.Name} ({i.SizeName})")
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
