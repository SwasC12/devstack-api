using System.Security.Claims;
using DevStack.API.DataAccess;
using DevStack.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.WebService.Controllers;

// Suppliers + purchase orders + (partial) receiving + landed cost. Admin-only.
// Receiving adds stock and rolls freight/duty into each item's CostBasis.
[ApiController]
[Route("api/[controller]")]
public class SuppliersController : ControllerBase
{
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public SuppliersController(DevStackDataModel db, ICurrentShop currentShop)
    {
        _db = db;
        _currentShop = currentShop;
    }

    private int UserId => int.Parse(User.FindFirstValue("userId")!);

    // ── Suppliers ──────────────────────────────────────────────────────────

    public record SupplierWrite(string Name, string? Phone, string? Email);

    [Authorize(Roles = "admin")]
    [HttpGet]
    public async Task<ActionResult> GetSuppliers()
    {
        var suppliers = await _db.Suppliers.OrderBy(s => s.Name).AsNoTracking().ToListAsync();

        // One grouped query for open-order counts instead of a blocking Count
        // per supplier (the old N+1).
        var ids = suppliers.Select(s => s.Id).ToList();
        var openBySupplier = (await _db.PurchaseOrders.AsNoTracking()
            .Where(p => ids.Contains(p.SupplierId) && p.Status != "received")
            .GroupBy(p => p.SupplierId)
            .Select(g => new { SupplierId = g.Key, Open = g.Count() })
            .ToListAsync())
            .ToDictionary(x => x.SupplierId, x => x.Open);

        return Ok(suppliers.Select(s => new
        {
            s.Id, s.Name, s.Phone, s.Email, s.CreatedAt,
            OpenOrders = openBySupplier.TryGetValue(s.Id, out var n) ? n : 0
        }));
    }

    [Authorize(Roles = "admin")]
    [HttpPost]
    public async Task<ActionResult> CreateSupplier(SupplierWrite request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "Supplier name is required." });
        var supplier = new Supplier
        {
            ShopId = _currentShop.ShopId,
            Name = name,
            Phone = Trimmed(request.Phone, 50),
            Email = Trimmed(request.Email, 100),
            CreatedAt = DateTime.UtcNow.AddHours(2)
        };
        _db.Suppliers.Add(supplier);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "supplier_create", $"'{supplier.Name}'");
        await _db.SaveChangesAsync();
        return Ok(supplier);
    }

    [Authorize(Roles = "admin")]
    [HttpPut("{id:int}")]
    public async Task<ActionResult> UpdateSupplier(int id, SupplierWrite request)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == id);
        if (supplier is null) return NotFound();
        supplier.Name = request.Name?.Trim() ?? supplier.Name;
        supplier.Phone = Trimmed(request.Phone, 50);
        supplier.Email = Trimmed(request.Email, 100);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "supplier_update", $"'{supplier.Name}'");
        await _db.SaveChangesAsync();
        return Ok(supplier);
    }

    [Authorize(Roles = "admin")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteSupplier(int id)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == id);
        if (supplier is null) return NotFound();
        if (await _db.PurchaseOrders.AnyAsync(p => p.SupplierId == id))
            return BadRequest(new { error = "This supplier has purchase orders - delete those first." });
        _db.Suppliers.Remove(supplier);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "supplier_delete", $"'{supplier.Name}'");
        await _db.SaveChangesAsync();
        return Ok();
    }

    // ── Purchase orders ────────────────────────────────────────────────────

    public record PoLineWrite(int MenuItemId, int Quantity, decimal UnitCost);
    public record PoWrite(int SupplierId, decimal FreightCost, decimal DutyCost, string? Notes, List<PoLineWrite> Lines);

    [Authorize(Roles = "admin")]
    [HttpGet("orders")]
    public async Task<ActionResult> GetOrders()
    {
        var pos = await _db.PurchaseOrders
            .Include(p => p.Lines)
            .OrderByDescending(p => p.OrderedAt)
            .ToListAsync();
        var itemIds = pos.SelectMany(p => p.Lines).Select(l => l.MenuItemId).Distinct().ToList();
        var items = await _db.MenuItems.Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name);
        var suppliers = await _db.Suppliers.ToDictionaryAsync(s => s.Id, s => s.Name);

        return Ok(pos.Select(p => new
        {
            p.Id, p.OrderedAt, p.Status, p.FreightCost, p.DutyCost, p.Notes,
            SupplierId = p.SupplierId,
            SupplierName = suppliers.TryGetValue(p.SupplierId, out var sn) ? sn : "Unknown",
            Total = p.Lines.Sum(l => l.Quantity * l.UnitCost) + p.FreightCost + p.DutyCost,
            Lines = p.Lines.Select(l => new
            {
                l.Id, l.MenuItemId,
                ItemName = items.TryGetValue(l.MenuItemId, out var n) ? n : "Unknown",
                l.Quantity, l.UnitCost, l.ReceivedQuantity,
                LineTotal = l.Quantity * l.UnitCost
            })
        }));
    }

    [Authorize(Roles = "admin")]
    [HttpPost("orders")]
    public async Task<ActionResult> CreateOrder(PoWrite request)
    {
        if (!await _db.Suppliers.AnyAsync(s => s.Id == request.SupplierId))
            return BadRequest(new { error = "Pick a supplier first." });
        var lines = request.Lines.Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0)
            return BadRequest(new { error = "A purchase order needs at least one line." });
        var itemIds = lines.Select(l => l.MenuItemId).Distinct().ToList();
        var known = await _db.MenuItems.Where(m => itemIds.Contains(m.Id)).Select(m => m.Id).ToListAsync();
        if (known.Count != itemIds.Count)
            return BadRequest(new { error = "One of the items doesn't exist." });

        var po = new PurchaseOrder
        {
            ShopId = _currentShop.ShopId,
            SupplierId = request.SupplierId,
            OrderedAt = DateTime.UtcNow.AddHours(2),
            Status = "open",
            FreightCost = Math.Max(0, request.FreightCost),
            DutyCost = Math.Max(0, request.DutyCost),
            Notes = Trimmed(request.Notes, 500),
            Lines = lines.Select(l => new PurchaseOrderLine
            {
                MenuItemId = l.MenuItemId,
                Quantity = l.Quantity,
                UnitCost = Math.Max(0, l.UnitCost),
                ReceivedQuantity = 0
            }).ToList()
        };
        _db.PurchaseOrders.Add(po);
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "po_create", $"PO for supplier #{request.SupplierId}, {lines.Count} line(s)");
        await _db.SaveChangesAsync();
        return Ok(po);
    }

    // POST /api/suppliers/orders/{id}/receive - partial receiving. Each line
    // receives `quantity` units: stock goes up, the received cost (unit + a
    // share of freight/duty, i.e. landed cost) rolls into the item's CostBasis.
    [Authorize(Roles = "admin")]
    [HttpPost("orders/{id:int}/receive")]
    public async Task<ActionResult> Receive(int id, [FromBody] List<ReceiveLineRequest> received)
    {
        var po = await _db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id);
        if (po is null) return NotFound();

        var requested = received.Where(r => r.Quantity > 0).ToDictionary(r => r.LineId, r => r.Quantity);
        if (requested.Count == 0)
            return BadRequest(new { error = "Nothing to receive." });

        // Landed cost: freight + duty spread over the TOTAL ordered units, then
        // per line over the units being received now.
        var totalUnits = po.Lines.Sum(l => l.Quantity);
        var landedPerUnit = totalUnits > 0 ? (po.FreightCost + po.DutyCost) / totalUnits : 0m;

        var changed = new List<(string Name, decimal Cost, int Qty)>();
        foreach (var line in po.Lines)
        {
            if (!requested.TryGetValue(line.Id, out var qty)) continue;
            var remaining = line.Quantity - line.ReceivedQuantity;
            if (qty > remaining)
                return BadRequest(new { error = $"Line '{line.MenuItemId}' only has {remaining} unit(s) left to receive." });

            line.ReceivedQuantity += qty;
            var unitCost = line.UnitCost + landedPerUnit;
            var item = await _db.MenuItems.FirstOrDefaultAsync(m => m.Id == line.MenuItemId);
            if (item is not null)
            {
                item.StockQuantity += qty;
                // Rolling average cost basis (weighted by the existing stock).
                var existingValue = item.CostBasis * item.StockQuantity;
                item.CostBasis = item.StockQuantity > 0
                    ? Math.Round((existingValue + unitCost * qty) / item.StockQuantity, 4)
                    : unitCost;
                changed.Add((item.Name, unitCost, qty));
            }
        }

        po.Status = po.Lines.All(l => l.ReceivedQuantity >= l.Quantity) ? "received" : "partial";
        await AuditLog.Write(_db, _currentShop.ShopId, UserId, "po_receive",
            $"PO #{po.Id}: {changed.Sum(c => c.Qty)} unit(s) received (landed R{landedPerUnit:0.00}/unit)");
        await _db.SaveChangesAsync();
        return Ok(new { po.Id, po.Status, received = changed });
    }

    public record ReceiveLineRequest(int LineId, int Quantity);

    private static string? Trimmed(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : (v.Length > max ? v[..max] : v);
    }
}
