using DevStack.API.DataAccess;
using DevStack.API.Models;
using DevStack.API.PlatformLogic.CategoryLogic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace DevStack.API.WebService.Controllers;

// Category management is an admin-only feature (same as UsersController).
//
// Endpoints:
//   GET    api/categories       → List
//   GET    api/categories/5     → One
//   PUT    api/categories       → Write (creates when Id == 0, otherwise renames)
//   DELETE api/categories/5     → Delete (blocked while items still use it)
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin,manager")]
public class CategoriesController : ControllerBase
{
    private readonly ICategoryLogic _logic;
    private readonly DevStackDataModel _db;
    private readonly ICurrentShop _currentShop;

    public CategoriesController(ICategoryLogic logic, DevStackDataModel db, ICurrentShop currentShop)
    {
        _logic = logic;
        _db = db;
        _currentShop = currentShop;
    }

    // GET api/categories
    [HttpGet]
    public async Task<ActionResult<List<Category>>> GetAll()
    {
        var result = await _logic.GetCategoriesAsync();
        return result.IsSuccess ? Ok(result.Data) : StatusCode(500, result.Error);
    }

    // GET api/categories/5
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Category>> Get(int id)
    {
        var result = await _logic.GetCategoryAsync(id);
        if (!result.IsSuccess) return NotFound(new { error = result.Error });
        if (result.Data is null) return NotFound();
        return Ok(result.Data);
    }

    // PUT api/categories  — the single "write" call: create or rename.
    [HttpPut]
    public async Task<ActionResult<Category>> Write(Category category)
    {
        var result = await _logic.WriteCategoryAsync(category);
        if (!result.IsSuccess) return BadRequest(new { error = result.Error });

        var isBrandNew = category.Id == 0;
        var userId = int.Parse(User.FindFirstValue("userId") ?? "0");
        await AuditLog.Write(_db, _currentShop.ShopId, userId,
            isBrandNew ? "category_create" : "category_update",
            $"'{result.Data!.Name}' (station: {result.Data.Station})");
        await _db.SaveChangesAsync();
        return isBrandNew
            ? CreatedAtAction(nameof(Get), new { id = result.Data!.Id }, result.Data)
            : Ok(result.Data);
    }

    // DELETE api/categories/5 — fails with a clear message while items use it.
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _logic.DeleteCategoryAsync(id);
        if (!result.IsSuccess) return BadRequest(new { error = result.Error });
        var userId = int.Parse(User.FindFirstValue("userId") ?? "0");
        await AuditLog.Write(_db, _currentShop.ShopId, userId, "category_delete", $"category #{id}");
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
