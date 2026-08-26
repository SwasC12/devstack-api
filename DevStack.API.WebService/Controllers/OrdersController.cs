using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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

    public record PlaceOrderRequest(List<OrderItemRequest> Items, string? PaymentMethod = null, decimal? AmountReceived = null, int? DiscountId = null, string? CustomerName = null, string? CustomerPhone = null, string? Notes = null, string? DineMode = null, string? TableNumber = null, List<PaymentRequest>? Payments = null, decimal? Tip = null, decimal? ServiceChargePct = null, int? AccountCustomerId = null, int? LoyaltyCustomerId = null, int? LoyaltyMemberId = null, bool RedeemLoyalty = false);
    public record PaymentRequest(string Method, decimal Amount);
    public record OrderItemRequest(int MenuItemId, string Name, decimal Price, int Quantity, int? SizeId = null, string? Note = null, List<int>? ModifierIds = null);
    public record VoidOrderRequest(string Reason);
    public record RefundOrderRequest(decimal Amount, string Reason);

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
            Notes = Trimmed(request.Notes, 1000),
            DineMode = request.DineMode?.Trim().ToLowerInvariant() == "dinein" ? "dinein" : "takeaway",
            TableNumber = Trimmed(request.TableNumber, 20),
            AccountCustomerId = request.AccountCustomerId
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

            // Track anything that just CROSSED its low-stock threshold (the
            // tracked entity is stale - ExecuteUpdate bypasses the change
            // tracker, so read the post-decrement value fresh). Alerting only
            // on the crossing, not every sale, keeps the bell spam-free.
            if (updated > 0)
            {
                var after = await _db.MenuItems.Where(m => m.Id == menuItemId)
                    .Select(m => new { m.Name, m.StockQuantity, m.LowStockThreshold }).FirstAsync();
                if (after.StockQuantity <= after.LowStockThreshold
                    && menuItem.StockQuantity > after.LowStockThreshold)
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

        // Tips + service charge: extras on top of the sale. The cash drawer
        // sees the grand total; revenue reports keep the sale separate.
        order.ServiceChargeAmount = request.ServiceChargePct is > 0
            ? Math.Round(order.Total * Math.Min(request.ServiceChargePct.Value, 50m) / 100m, 2)
            : 0m;
        order.TipAmount = request.Tip is > 0 ? Math.Round(request.Tip.Value, 2) : 0m;
        var grandTotal = order.Total + order.ServiceChargeAmount + order.TipAmount;

        // Payments: split payments carry a list; legacy single-method orders
        // build one row. Every row lands in OrderPayments so reports can
        // aggregate by method exactly.
        List<PaymentRequest> tenders;
        if (request.Payments is { Count: > 0 })
        {
            tenders = request.Payments
                .Where(p => p.Amount > 0)
                .Select(p => new PaymentRequest(
                    p.Method?.Trim().ToLowerInvariant() is "card" or "account" ? p.Method.Trim().ToLowerInvariant() : "cash",
                    Math.Round(p.Amount, 2)))
                .ToList();
        }
        else if (method == "cash")
        {
            tenders = [new PaymentRequest("cash", grandTotal)];
        }
        else
        {
            tenders = [new PaymentRequest("card", grandTotal)];
        }

        var tenderTotal = tenders.Sum(t => t.Amount);

        // Single non-cash tender (a plain card or account sale, no cash): the
        // SERVER is the source of truth on price, so charge exactly what is due.
        // The client sends the amount from its own displayed total, which can be
        // a few cents — or a stale-cached price — above the server's recomputed
        // grand total; that used to fail checkout with "Non-cash payments exceed
        // the R… due" even on a plain exact card sale. Clamp the tender DOWN to
        // the grand total so the card is charged precisely what's owed. (We only
        // clamp down: a tender BELOW the total still correctly fails as
        // "doesn't cover", which forces a menu refresh if a price went up.)
        if (tenders.Count == 1 && tenders[0].Method != "cash" && tenderTotal > grandTotal)
        {
            tenders[0] = tenders[0] with { Amount = grandTotal };
            tenderTotal = grandTotal;
        }

        var isAccount = tenders.Any(t => t.Method == "account");

        // House account: the full amount goes on the customer's tab. Credit
        // limit is enforced; the balance is only raised once, atomically.
        Customer? accountCustomer = null;
        if (isAccount)
        {
            if (request.AccountCustomerId is null)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = "An account payment needs a customer." });
            }
            accountCustomer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == request.AccountCustomerId);
            if (accountCustomer is null)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = "That customer doesn't exist." });
            }
            var afterBalance = accountCustomer.Balance + tenderTotal;
            if (accountCustomer.CreditLimit > 0 && afterBalance > accountCustomer.CreditLimit)
            {
                await tx.RollbackAsync();
                return BadRequest(new { error = $"'{accountCustomer.Name}' is over their credit limit (R{accountCustomer.CreditLimit:0.00})." });
            }
            accountCustomer.Balance = afterBalance;
            order.PaymentMethod = "account";
        }

        var cashPaid = tenders.Where(t => t.Method == "cash").Sum(t => t.Amount);
        var nonCashPaid = tenderTotal - cashPaid;

        // Tenders must COVER the grand total; the only allowed excess is cash
        // (the cashier gives change back). Cards/accounts charge exactly.
        if (tenderTotal + 0.005m < grandTotal)
        {
            await tx.RollbackAsync();
            return BadRequest(new { error = $"Payments total R{tenderTotal:0.00} doesn't cover the R{grandTotal:0.00} due." });
        }
        var excess = tenderTotal - grandTotal;
        if (excess > cashPaid + 0.005m)
        {
            await tx.RollbackAsync();
            return BadRequest(new { error = $"Non-cash payments exceed the R{grandTotal:0.00} due." });
        }

        if (cashPaid > 0)
        {
            order.AmountReceived = cashPaid;
            // ALWAYS record change (including R0.00): the receipt must print
            // the change line for legal / till-reconciliation purposes, even
            // when the customer paid exact.
            var change = cashPaid - (grandTotal - nonCashPaid);
            order.ChangeGiven = Math.Round(change, 2);
        }

        var now = DateTime.UtcNow.AddHours(2);
        order.Payments = tenders.Select(t => new OrderPayment
        {
            ShopId = order.ShopId,
            Method = t.Method,
            Amount = t.Amount,
            CreatedAt = now
        }).ToList();

        _db.Orders.Add(order);

        // Low-stock alerts: one in-app notification per shop admin/manager (no
        // FCM push - the bell in the admin UI is the delivery). Managers run
        // inventory too, so they get these alerts alongside admins.
        List<Notification>? alertRows = null;
        if (lowStockAlerts.Count > 0)
        {
            var admins = await _db.Users.Where(u => (u.Role == "admin" || u.Role == "manager") && u.ShopId == order.ShopId).ToListAsync();
            if (admins.Count > 0)
            {
                var alertNow = DateTime.UtcNow;
                alertRows = new List<Notification>();
                foreach (var a in lowStockAlerts)
                {
                    var body = $"'{a.Name}' is running low - only {a.Remaining} left.";
                    foreach (var admin in admins)
                        alertRows.Add(new Notification { ShopId = order.ShopId, UserId = admin.Id, Title = "Low stock", Body = body, Type = "alert", CreatedAtUtc = alertNow });
                }
                _db.Notifications.AddRange(alertRows);
            }
        }

        // Loyalty: earn or redeem a stamp for the attached BRAND loyalty member,
        // inside the same transaction as the sale. Loyalty is franchise-scoped, so
        // the member + shared balance live on the brand this shop belongs to.
        // (LoyaltyCustomerId is the pre-brand field name — still a member id.)
        var loyaltyMemberId = request.LoyaltyMemberId ?? request.LoyaltyCustomerId;
        if (loyaltyMemberId is int memberId)
        {
            var brandId = await _db.Shops.Where(s => s.Id == order.ShopId).Select(s => s.BrandId).FirstOrDefaultAsync();
            if (brandId is int bid)
            {
                var brand = await _db.Brands.FindAsync(bid);
                var member = await _db.LoyaltyMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.BrandId == bid);
                if (brand is { LoyaltyEnabled: true } && member is not null)
                {
                    var required = brand.LoyaltyStampsRequired;
                    if (request.RedeemLoyalty && member.LoyaltyStamps >= required)
                        member.LoyaltyStamps -= required;      // redeem: spend the required stamps
                    else if (!request.RedeemLoyalty)
                        member.LoyaltyStamps += 1;             // earn one stamp
                    // redeem attempted below threshold → leave balance unchanged
                }
            }
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }

    // GET /api/orders — admin only. Enriched with the cashier's display name
    // so the history view can say who sold what. Bounded: optional from/to
    // date range (defaults to the last 30 days) + offset/limit paging, with
    // the total row count in X-Total-Count so the UI can show "load more".
    // The whole history used to be loaded in one shot — that's the query that
    // got slower every single day.
    [Authorize(Roles = "admin,manager")]
    [HttpGet]
    public async Task<ActionResult> GetOrders([FromQuery] string? from = null, [FromQuery] string? to = null, [FromQuery] int limit = 200, [FromQuery] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(offset, 0);

        var query = _db.Orders.AsQueryable();
        if (DateTime.TryParse(from, out var fromDate))
            query = query.Where(o => o.CreatedAt >= fromDate.Date);
        if (DateTime.TryParse(to, out var toDate))
            query = query.Where(o => o.CreatedAt < toDate.Date.AddDays(1));

        var total = await query.CountAsync();
        // AsNoTracking (read-only) + AsSplitQuery: three sibling collection
        // Includes (Items->Modifiers, Refunds, Payments) in one query is a
        // cartesian explosion (rows = orders x items x refunds x payments).
        // Split queries fetch each collection separately - linear, not product.
        var orders = await query
            .AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .Include(o => o.Refunds)
            .Include(o => o.Payments)
            .OrderByDescending(o => o.CreatedAt)
            .Skip(offset).Take(limit)
            .AsSplitQuery()
            .ToListAsync();

        var userIds = orders.Where(o => o.UserId is not null).Select(o => o.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        Response.Headers["X-Total-Count"] = total.ToString();
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
            o.HeldAt,
            o.TipAmount,
            o.ServiceChargeAmount,
            o.AccountCustomerId,
            Payments = o.Payments.Select(p => new { p.Method, p.Amount }),
            RefundedAmount = o.Refunds.Sum(r => r.Amount),
            Refunds = o.Refunds.Select(r => new { r.Id, r.Amount, r.Reason, r.PaymentMethod, r.CreatedAt }),
            Items = o.Items.Select(i => new
            {
                i.Id, i.MenuItemId, i.Name, i.Price, i.Quantity, i.SizeId, i.SizeName, i.Note,
                Modifiers = i.Modifiers.Select(m => new { m.GroupName, m.Name, m.PriceDelta })
            })
        }));
    }

    // POST /api/orders/{id}/void — admin only. The one-way door, now with a
    // key: a voided order is excluded from revenue and its stock is restored.
    [Authorize(Roles = "admin,manager")]
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
        await AuditLog.Write(_db, order.ShopId, order.VoidedByUserId, "order_void", $"#{order.Id} R{order.Total:0.00} - {reason}");

        // House account: a voided account order takes the charge back off the
        // customer's tab (the sale never happened).
        if (order.AccountCustomerId is not null)
        {
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == order.AccountCustomerId);
            if (customer is not null)
                customer.Balance = Math.Max(0, customer.Balance - (order.Total + order.TipAmount + order.ServiceChargeAmount));
        }

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

    // POST /api/orders/{id}/refund - admin only (manager PIN verified on the
    // client). Proper-POS semantics: a refund returns MONEY, not stock - the
    // items were already sold/consumed (void is the stock-return path for
    // unfulfilled orders). Cannot exceed what is still refundable on the
    // order (total minus previous refunds); voided orders can't be refunded.
    [Authorize(Roles = "admin,manager")]
    [HttpPost("{id:int}/refund")]
    public async Task<IActionResult> RefundOrder(int id, RefundOrderRequest request)
    {
        var order = await _db.Orders.Include(o => o.Refunds).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.VoidedAt is not null)
            return BadRequest(new { error = "This order is voided and excluded from revenue - no refund needed." });

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            return BadRequest(new { error = "A reason is required for a refund." });
        if (request.Amount <= 0)
            return BadRequest(new { error = "Refund amount must be greater than zero." });

        var alreadyRefunded = order.Refunds.Sum(r => r.Amount);
        var refundable = order.Total - alreadyRefunded;
        if (request.Amount > refundable + 0.001m)
            return BadRequest(new { error = $"Cannot refund more than the remaining R{refundable:0.00}." });

        _db.OrderRefunds.Add(new OrderRefund
        {
            OrderId = order.Id,
            ShopId = order.ShopId,
            Amount = Math.Round(request.Amount, 2),
            Reason = reason,
            PaymentMethod = "cash",
            CreatedAt = DateTime.UtcNow.AddHours(2),
            UserId = int.Parse(User.FindFirstValue("userId")!)
        });
        await AuditLog.Write(_db, order.ShopId, int.Parse(User.FindFirstValue("userId")!), "order_refund", $"#{order.Id} R{request.Amount:0.00} - {reason}");
        // House account: the refund reduces what the customer owes.
        if (order.AccountCustomerId is not null)
        {
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == order.AccountCustomerId);
            if (customer is not null)
                customer.Balance = Math.Max(0, customer.Balance - Math.Round(request.Amount, 2));
        }
        await _db.SaveChangesAsync();
        return Ok();
    }

    // GET /api/orders/5 - any logged-in user (tenant-scoped by the global filter).
    [Authorize]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Order>> GetOrder(int id)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .Include(o => o.Refunds)
            .AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == id);
        return order is null ? NotFound() : Ok(order);
    }

    private static string? Trimmed(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : (v.Length > max ? v[..max] : v);
    }

    // GET /api/orders/kitchen - any logged-in user. Live queue for the kitchen
    // display tablet: not voided, not completed, not held, from the last
    // `minutes`. `station=kitchen|bar` routes by category station - the order
    // shows if any of its items belong to that station (items are filtered to
    // the station's own). Items + modifiers + notes only (no prices).
    [Authorize]
    [HttpGet("kitchen")]
    public async Task<ActionResult> GetKitchenOrders([FromQuery] int minutes = 120, [FromQuery] string? station = null)
    {
        minutes = Math.Clamp(minutes, 15, 480);
        var cutoff = DateTime.UtcNow.AddHours(2).AddMinutes(-minutes);
        var stationFilter = station is "kitchen" or "bar" ? station : null;

        // Station map: menu item id → station (via its category). Built once
        // per call; the category table is small.
        var catStation = await _db.Categories
            .Select(c => new { c.Name, c.Station }).ToListAsync();
        var catStationMap = catStation.ToDictionary(c => c.Name.ToLowerInvariant(), c => c.Station);
        var itemCat = await _db.MenuItems
            .Select(m => new { m.Id, m.Category }).ToListAsync();
        var itemStation = itemCat.ToDictionary(m => m.Id,
            m => catStationMap.TryGetValue(m.Category.ToLowerInvariant(), out var st) ? st : "both");
        static bool Matches(string st, string? filter) =>
            filter is null || st == "both" || st == filter;

        // Lightweight projection first (ETag safety net). With a station filter
        // we need each order's item menu ids to decide membership.
        var queue = await _db.Orders
            .Where(o => o.VoidedAt == null && o.CompletedAt == null && o.HeldAt == null && o.CreatedAt >= cutoff)
            .Select(o => new { o.Id, o.CreatedAt, ItemIds = o.Items.Select(i => i.MenuItemId).ToList() })
            .ToListAsync();
        queue = queue.Where(o => stationFilter is null || o.ItemIds.Any(id => Matches(itemStation.GetValueOrDefault(id, "both"), stationFilter))).ToList();
        var tag = $"\"{_currentShop.ShopId}:{queue.Count}:{(queue.Count == 0 ? 0 : queue.Max(o => o.CreatedAt.Ticks))}:{queue.Aggregate(0L, (a, o) => a ^ o.Id)}:{queue.Sum(o => o.ItemIds.Count)}\"";
        if (Request.Headers.IfNoneMatch.ToString() == tag)
            return StatusCode(StatusCodes.Status304NotModified);

        // Queue actually changed (or first call) - load the full make-list.
        var orders = await _db.Orders
            .Where(o => o.VoidedAt == null && o.CompletedAt == null && o.HeldAt == null && o.CreatedAt >= cutoff)
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync();

        Response.Headers.ETag = tag;
        return Ok(orders
            .Where(o => stationFilter is null || o.Items.Any(i => Matches(itemStation.GetValueOrDefault(i.MenuItemId, "both"), stationFilter)))
            .Select(o => new
            {
                o.Id,
                o.CreatedAt,
                o.CustomerName,
                o.Notes,
                o.DineMode,
                o.TableNumber,
                Items = o.Items
                    .Where(i => stationFilter is null || Matches(itemStation.GetValueOrDefault(i.MenuItemId, "both"), stationFilter))
                    .Select(i => new
                    {
                        i.Name, i.Quantity, i.SizeName, i.Note,
                        Modifiers = i.Modifiers.Select(m => m.Name)
                    })
            }));
    }

    // GET /api/orders/kitchen/held - orders the kitchen put on hold (same
    // station filtering). They sit here until "Send" puts them back in the
    // live queue. No ETag - this list is small and changes on tap.
    [Authorize]
    [HttpGet("kitchen/held")]
    public async Task<ActionResult> GetHeldOrders([FromQuery] string? station = null)
    {
        var stationFilter = station is "kitchen" or "bar" ? station : null;
        var catStation = await _db.Categories.Select(c => new { c.Name, c.Station }).ToListAsync();
        var catStationMap = catStation.ToDictionary(c => c.Name.ToLowerInvariant(), c => c.Station);
        var itemCat = await _db.MenuItems.Select(m => new { m.Id, m.Category }).ToListAsync();
        var itemStation = itemCat.ToDictionary(m => m.Id,
            m => catStationMap.TryGetValue(m.Category.ToLowerInvariant(), out var st) ? st : "both");
        static bool Matches(string st, string? filter) =>
            filter is null || st == "both" || st == filter;

        var orders = await _db.Orders
            .Where(o => o.VoidedAt == null && o.CompletedAt == null && o.HeldAt != null)
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .OrderBy(o => o.HeldAt)
            .ToListAsync();

        return Ok(orders
            .Where(o => stationFilter is null || o.Items.Any(i => Matches(itemStation.GetValueOrDefault(i.MenuItemId, "both"), stationFilter)))
            .Select(o => new
            {
                o.Id,
                o.HeldAt,
                o.CustomerName,
                o.Notes,
                o.DineMode,
                o.TableNumber,
                Items = o.Items
                    .Where(i => stationFilter is null || Matches(itemStation.GetValueOrDefault(i.MenuItemId, "both"), stationFilter))
                    .Select(i => new
                    {
                        i.Name, i.Quantity, i.SizeName, i.Note,
                        Modifiers = i.Modifiers.Select(m => m.Name)
                    })
            }));
    }

    // POST /api/orders/{id}/hold - kitchen pauses the order (held strip).
    // POST /api/orders/{id}/send - puts it back in the live queue.
    [Authorize]
    [HttpPost("{id:int}/hold")]
    public async Task<IActionResult> HoldOrder(int id)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.VoidedAt is not null || order.CompletedAt is not null)
            return BadRequest(new { error = "This order is finished." });
        order.HeldAt = DateTime.UtcNow.AddHours(2);
        await _db.SaveChangesAsync();
        return Ok();
    }

    [Authorize]
    [HttpPost("{id:int}/send")]
    public async Task<IActionResult> SendOrder(int id)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        order.HeldAt = null;
        await _db.SaveChangesAsync();
        return Ok();
    }

    // GET /api/orders/cashup?date=yyyy-MM-dd - admin only. End-of-day cash-up:
    // totals per payment method (split payments included), tips, expenses,
    // discounts, refunds and per-cashier breakdown for a day (defaults to
    // today, SAST). Voided orders excluded everywhere.
    [Authorize(Roles = "admin,manager")]
    [HttpGet("cashup")]
    public async Task<ActionResult> GetCashup([FromQuery] string? date = null)
    {
        var dayStart = DateTime.UtcNow.AddHours(2).Date;
        if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsed))
            dayStart = parsed.Date;
        var dayEnd = dayStart.AddDays(1);

        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.VoidedAt == null && o.CreatedAt >= dayStart && o.CreatedAt < dayEnd)
            .Include(o => o.Refunds)
            .Include(o => o.Payments)
            .AsSplitQuery()
            .ToListAsync();

        // Money in per method: prefer the split-payment rows; legacy orders
        // (pre-split) fall back to the order-level fields.
        decimal Paid(Order o, string method) =>
            o.Payments.Count > 0
                ? o.Payments.Where(p => p.Method == method).Sum(p => p.Amount)
                : (o.PaymentMethod == method ? o.Total + o.TipAmount + o.ServiceChargeAmount : 0m);
        decimal RefundsOf(Order o) => o.Refunds.Sum(r => r.Amount);
        decimal CashIn(Order o) => Paid(o, "cash") + Paid(o, "card") + Paid(o, "account");

        var byMethod = new[] { "cash", "card", "account" }
            .Select(m => new
            {
                method = m,
                gross = orders.Sum(o => Paid(o, m)),
                refunds = orders.Where(o => o.PaymentMethod == m).Sum(RefundsOf),
                orders = orders.Count(o => o.PaymentMethod == m || o.Payments.Any(p => p.Method == m))
            })
            .ToList();

        var expenses = await _db.Expenses
            .Where(e => e.CreatedAt >= dayStart && e.CreatedAt < dayEnd)
            .ToListAsync();

        var userIds = orders.Where(o => o.UserId is not null).Select(o => o.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var cashiers = orders
            .Where(o => o.UserId is not null)
            .GroupBy(o => o.UserId!.Value)
            .Select(g => new
            {
                name = users.TryGetValue(g.Key, out var n) ? n : "Unknown",
                orders = g.Count(),
                sales = g.Sum(o => o.Total),
                tips = g.Sum(o => o.TipAmount),
                refunds = g.Sum(RefundsOf),
                net = g.Sum(CashIn) - g.Sum(RefundsOf)
            })
            .OrderByDescending(c => c.net)
            .ToList();

        var cashIn = orders.Sum(CashIn);
        var refunds = orders.Sum(RefundsOf);
        var tips = orders.Sum(o => o.TipAmount);
        var serviceCharges = orders.Sum(o => o.ServiceChargeAmount);
        var expenseTotal = expenses.Sum(e => e.Amount);

        return Ok(new
        {
            date = dayStart.ToString("yyyy-MM-dd"),
            totals = new
            {
                orders = orders.Count,
                gross = orders.Sum(o => o.Total),
                discounts = orders.Sum(o => o.DiscountAmount),
                tips,
                serviceCharges,
                refunds,
                expenses = expenseTotal,
                cashIn,
                net = cashIn - refunds - expenseTotal
            },
            byMethod,
            cashiers,
            expenseItems = expenses.Select(e => new { e.Id, e.Category, e.Amount, e.Note, e.CreatedAt })
        });
    }

    // POST /api/orders/{id}/complete - any logged-in user. Kitchen taps
    // "Done": the order leaves the live queue but stays in revenue.
    [Authorize]
    [HttpPost("{id:int}/complete")]
    public async Task<IActionResult> CompleteOrder(int id)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.VoidedAt is not null)
            return BadRequest(new { error = "This order is voided." });
        if (order.CompletedAt is not null)
            return BadRequest(new { error = "This order is already completed." });
        order.CompletedAt = DateTime.UtcNow.AddHours(2);
        await _db.SaveChangesAsync();
        return Ok();
    }

    // GET /api/orders/analytics?days=14 - admin only. Owner analytics: daily
    // revenue series, per-cashier totals and per-category sales. Voided orders
    // excluded; everything scoped to the current shop by the global filter.
    // Lightweight: only the columns needed for the report are pulled (no
    // item/modifier/refund graph), and the window is bounded by `days`.
    [Authorize(Roles = "admin,manager")]
    [HttpGet("analytics")]
    public async Task<ActionResult> GetAnalytics([FromQuery] int days = 14)
    {
        days = Math.Clamp(days, 1, 90);
        var from = DateTime.UtcNow.AddHours(2).Date.AddDays(-(days - 1));

        var orders = await _db.Orders
            .Where(o => o.VoidedAt == null && o.CreatedAt >= from)
            .Select(o => new { o.Id, o.CreatedAt, o.Total, o.UserId })
            .ToListAsync();
        var ids = orders.Select(o => o.Id).ToList();

        var refunds = ids.Count == 0 ? []
            : await _db.OrderRefunds.Where(r => ids.Contains(r.OrderId))
                .Select(r => new { r.OrderId, r.Amount }).ToListAsync();
        var items = ids.Count == 0 ? []
            : await _db.OrderItems.Where(i => ids.Contains(i.OrderId))
                .Select(i => new { i.OrderId, i.MenuItemId, i.Name, i.Price, i.Quantity, i.SizeName }).ToListAsync();

        decimal NetRevenue(int orderId, decimal total) =>
            total - refunds.Where(r => r.OrderId == orderId).Sum(r => r.Amount);

        var daily = orders
            .GroupBy(o => o.CreatedAt.Date)
            .OrderBy(g => g.Key)
            .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), revenue = g.Sum(o => NetRevenue(o.Id, o.Total)), orders = g.Count() })
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
                revenue = g.Sum(o => NetRevenue(o.Id, o.Total))
            })
            .OrderByDescending(c => c.revenue)
            .ToList();

        // Per-category: line items don't carry the category, so join to the
        // current menu items (deleted/renamed items fall into "Other").
        // Cost per unit = recipe cost when a recipe exists, else CostBasis -
        // that feeds gross-profit per category.
        var costData = await _db.MenuItems
            .Select(m => new { m.Id, m.Category, m.CostBasis, RecipeCost = m.RecipeLines.Sum(r => r.CostPerUnit * r.Quantity) })
            .ToListAsync();
        var costByItem = costData.ToDictionary(m => m.Id, m => m.RecipeCost > 0 ? m.RecipeCost : m.CostBasis);
        var categories = items
            .GroupBy(i => costData.FirstOrDefault(c => c.Id == i.MenuItemId)?.Category is { Length: > 0 } cat ? cat : "Other")
            .Select(g => new
            {
                name = g.Key,
                quantity = g.Sum(i => i.Quantity),
                revenue = g.Sum(i => i.Price * i.Quantity),
                cost = g.Sum(i => costByItem.TryGetValue(i.MenuItemId, out var c) ? c * i.Quantity : 0m),
                grossProfit = g.Sum(i => i.Price * i.Quantity - (costByItem.TryGetValue(i.MenuItemId, out var c2) ? c2 * i.Quantity : 0m))
            })
            .OrderByDescending(g => g.revenue)
            .ToList();

        var totalCost = items.Sum(i => costByItem.TryGetValue(i.MenuItemId, out var c) ? c * i.Quantity : 0m);
        var totalRevenue = items.Sum(i => i.Price * i.Quantity);

        return Ok(new
        {
            days,
            totals = new
            {
                revenue = orders.Sum(o => NetRevenue(o.Id, o.Total)),
                orders = orders.Count,
                items = items.Sum(i => i.Quantity),
                cost = totalCost,
                grossProfit = totalRevenue - totalCost,
                grossMarginPct = totalRevenue > 0 ? Math.Round((totalRevenue - totalCost) / totalRevenue * 100m, 1) : 0m
            },
            daily,
            cashiers,
            categories
        });
    }

    // GET /api/orders/journal?from&to - admin only. The transaction journal:
    // every money event in chronological order with a running cash balance.
    // Sales/tips/service charges add; voids, refunds and expenses subtract.
    [Authorize(Roles = "admin,manager")]
    [HttpGet("journal")]
    public async Task<ActionResult> GetJournal([FromQuery] string? from = null, [FromQuery] string? to = null)
    {
        var query = _db.Orders.Where(o => o.VoidedAt == null);
        if (DateTime.TryParse(from, out var f)) query = query.Where(o => o.CreatedAt >= f.Date);
        if (DateTime.TryParse(to, out var t)) query = query.Where(o => o.CreatedAt < t.Date.AddDays(1));
        var orders = await query.Select(o => new
        {
            o.Id, o.CreatedAt, o.Total, o.TipAmount, o.ServiceChargeAmount, o.PaymentMethod,
            o.CustomerName, o.UserId, o.AccountCustomerId
        }).ToListAsync();

        var fromD = DateTime.TryParse(from, out var ff) ? ff.Date : DateTime.MinValue;
        var toD = DateTime.TryParse(to, out var tt) ? tt.Date.AddDays(1) : DateTime.MaxValue;
        var refunds = await _db.OrderRefunds
            .Where(r => r.CreatedAt >= fromD && r.CreatedAt < toD)
            .Select(r => new { r.Id, r.OrderId, r.Amount, r.Reason, r.CreatedAt }).ToListAsync();
        var expenses = await _db.Expenses
            .Where(e => e.CreatedAt >= fromD && e.CreatedAt < toD)
            .Select(e => new { e.Id, e.Category, e.Amount, e.Note, e.CreatedAt }).ToListAsync();

        var userIds = orders.Where(o => o.UserId is not null).Select(o => o.UserId!.Value).Distinct()
            .Concat(refunds.Where(r => r.Id > 0).Select(_ => 0)) // no-op, refunds carry no user here
            .Distinct().ToList();
        var users = await _db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        var events = new List<(DateTime At, string Type, string Ref, string Detail, decimal Delta)>();
        foreach (var o in orders)
        {
            events.Add((o.CreatedAt, "sale", $"#{o.Id}", $"{(o.CustomerName is null ? "Walk-in" : o.CustomerName)} · {o.PaymentMethod}", o.Total));
        }
        foreach (var v in await _db.Orders.Where(o => o.VoidedAt != null
                && (fromD == DateTime.MinValue || o.CreatedAt >= fromD) && (toD == DateTime.MaxValue || o.CreatedAt < toD))
            .Select(o => new { o.Id, VoidedAt = o.VoidedAt!.Value, o.VoidReason, o.Total }).ToListAsync())
            events.Add((v.VoidedAt, "void", $"#{v.Id}", v.VoidReason ?? "Voided", -v.Total));
        foreach (var r in refunds)
            events.Add((r.CreatedAt, "refund", $"#{r.OrderId}", r.Reason ?? "Refund", -r.Amount));
        foreach (var e in expenses)
            events.Add((e.CreatedAt, "expense", $"E{e.Id}", $"{e.Category}{(e.Note is null ? "" : $" · {e.Note}")}", -e.Amount));

        var ordered = events.OrderBy(e => e.At).ToList();
        decimal balance = 0;
        var rows = ordered.Select(e =>
        {
            balance += e.Delta;
            return new { e.At, e.Type, e.Ref, e.Detail, e.Delta, Balance = balance };
        }).ToList();

        return Ok(new { openingBalance = 0m, closingBalance = balance, events = rows });
    }

    // GET /api/orders/summary — analytics (admin only). Voided orders are
    // excluded from every figure: revenue means money actually taken.
    // Used to load EVERY order (plus items and refunds) into memory on every
    // admin page load; now it's three cheap aggregate queries against the
    // (ShopId, CreatedAt) index.
    [Authorize(Roles = "admin,manager")]
    [HttpGet("summary")]
    public async Task<ActionResult> GetSummary()
    {
        var today = DateTime.UtcNow.AddHours(2).Date;

        var allTime = await _db.Orders
            .Where(o => o.VoidedAt == null)
            .GroupBy(o => 1)
            .Select(g => new { count = g.Count(), revenue = g.Sum(o => o.Total) })
            .FirstOrDefaultAsync();
        var todayTotals = await _db.Orders
            .Where(o => o.VoidedAt == null && o.CreatedAt >= today)
            .GroupBy(o => 1)
            .Select(g => new { count = g.Count(), revenue = g.Sum(o => o.Total) })
            .FirstOrDefaultAsync();
        var voided = await _db.Orders.CountAsync(o => o.VoidedAt != null);

        // Top items: last 30 days of line items, grouped in memory over a
        // bounded window (the previous version scanned every order ever).
        var from = today.AddDays(-30);
        var recent = await _db.Orders
            .Where(o => o.VoidedAt == null && o.CreatedAt >= from)
            .Select(o => o.Id)
            .ToListAsync();
        var topItems = recent.Count == 0 ? []
            : await _db.OrderItems.Where(i => recent.Contains(i.OrderId))
                .Select(i => new { i.Name, i.SizeName, i.Price, i.Quantity })
                .ToListAsync();
        var top = topItems
            .GroupBy(i => i.SizeName is null ? i.Name : $"{i.Name} ({i.SizeName})")
            .Select(g => new { Name = g.Key, Quantity = g.Sum(i => i.Quantity), Revenue = g.Sum(i => i.Price * i.Quantity) })
            .OrderByDescending(g => g.Quantity)
            .Take(10)
            .ToList();

        return Ok(new
        {
            totalOrders = allTime?.count ?? 0,
            totalRevenue = allTime?.revenue ?? 0,
            todayRevenue = todayTotals?.revenue ?? 0,
            todayOrders = todayTotals?.count ?? 0,
            voidedOrders = voided,
            topItems = top
        });
    }
}
