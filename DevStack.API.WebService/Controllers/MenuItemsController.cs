using DevStack.API.DataAccess;
using DevStack.API.Models;
using DevStack.API.PlatformLogic.MenuItemLogic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DevStack.API.WebService.Controllers;

// [ApiController] turns on helpful API behaviour (automatic model validation,
// binding, etc.). The route "api/menuitems" comes from the class name via
// [controller] = "MenuItems" (MVC strips the "Controller" suffix, lowercased).
//
// Everything here is authenticated now — the public customer menu is gone, so
// the POS is the only consumer. Writes stay admin-only.
//
// Two write-side endpoints only, by design:
//   PUT    api/menuitems      → Write  (creates when Id == 0, otherwise edits)
//   DELETE api/menuitems/5    → Delete
// (Plus the GET reads.) PUT is used for the write because it's idempotent:
// sending the same item twice lands the same state.
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MenuItemsController : ControllerBase
{
    // The controller depends only on the LOGIC layer's interface. It knows
    // nothing about the data layer — that's the point of the layering.
    private readonly IMenuItemLogic _logic;
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public MenuItemsController(IMenuItemLogic logic, DevStackDataModel db, ICurrentShop currentShop)
    {
        _logic = logic;
        _db = db;
        _currentShop = currentShop;
    }

    // GET api/menuitems
    // Supports conditional GET: we hash the serialized menu into an ETag. When
    // the client sends a matching If-None-Match we return 304 (no body), so a
    // cold reopen with an unchanged menu costs a tiny round-trip instead of
    // re-downloading + re-parsing + re-caching the whole menu. The hash is of
    // this shop's data, so the ETag is naturally per-shop.
    [HttpGet]
    public async Task<ActionResult<List<MenuItem>>> GetAll()
    {
        var result = await _logic.GetItemsAsync();
        if (!result.IsSuccess) return StatusCode(500, result.Error);

        // Fingerprint for change-detection only: the casing here is irrelevant
        // (it's just hashed), so a default serialization is fine. The RESPONSE
        // body must go through Ok(...) so MVC serializes it with the app's
        // configured (camelCase) options - hand-serializing here produced
        // PascalCase and broke every client field.
        var fingerprint = System.Text.Json.JsonSerializer.Serialize(result.Data);
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint));
        var etag = "\"" + Convert.ToHexString(hash) + "\"";

        if (Request.Headers.IfNoneMatch.ToString() == etag)
            return StatusCode(StatusCodes.Status304NotModified);

        Response.Headers.ETag = etag;
        return Ok(result.Data);
    }

    // GET api/menuitems/stock — lightweight cross-till stock snapshot.
    // Returns ONLY id + on-hand + availability (no sizes/modifiers/recipe/
    // images), so a second till can keep its stock counts fresh with a tiny,
    // cheap poll instead of re-downloading the whole menu. The shop query
    // filter on _db.MenuItems scopes this to the caller's shop automatically.
    [HttpGet("stock")]
    public async Task<ActionResult<List<StockDto>>> GetStock()
    {
        var stock = await _db.MenuItems.AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new StockDto(m.Id, m.StockQuantity, m.IsAvailable))
            .ToListAsync();
        return Ok(stock);
    }

    // GET api/menuitems/5
    [HttpGet("{id:int}")]
    public async Task<ActionResult<MenuItem>> Get(int id)
    {
        var result = await _logic.GetItemAsync(id);
        if (!result.IsSuccess) return NotFound(new { error = result.Error });
        if (result.Data is null) return NotFound();
        return Ok(result.Data);
    }

    // PUT api/menuitems  — the single "write" call: create or edit.
    [Authorize(Roles = "admin")]
    [HttpPut]
    public async Task<ActionResult<MenuItem>> Write(MenuItem item)
    {
        var result = await _logic.WriteItemAsync(item);
        if (!result.IsSuccess) return BadRequest(new { error = result.Error });

        var isBrandNew = item.Id == 0;
        var userId = int.Parse(User.FindFirstValue("userId") ?? "0");
        await AuditLog.Write(_db, _currentShop.ShopId, userId,
            isBrandNew ? "item_create" : "item_update",
            $"'{result.Data!.Name}' (R{result.Data.Price:0.00})");
        await _db.SaveChangesAsync();
        return isBrandNew
            ? CreatedAtAction(nameof(Get), new { id = result.Data!.Id }, result.Data)
            : Ok(result.Data);
    }

    // DELETE api/menuitems/5
    [Authorize(Roles = "admin")]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _logic.DeleteItemAsync(id);
        if (!result.IsSuccess) return NotFound(new { error = result.Error });
        var userId = int.Parse(User.FindFirstValue("userId") ?? "0");
        await AuditLog.Write(_db, _currentShop.ShopId, userId, "item_delete", $"item #{id}");
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

// Tiny projection for the cross-till stock poll - deliberately NOT the full
// MenuItem graph.
public record StockDto(int Id, int StockQuantity, bool IsAvailable);
