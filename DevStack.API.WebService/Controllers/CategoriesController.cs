using DevStack.API.Models;
using DevStack.API.PlatformLogic.CategoryLogic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
[Authorize(Roles = "admin")]
public class CategoriesController : ControllerBase
{
    private readonly ICategoryLogic _logic;

    public CategoriesController(ICategoryLogic logic)
    {
        _logic = logic;
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
        return isBrandNew
            ? CreatedAtAction(nameof(Get), new { id = result.Data!.Id }, result.Data)
            : Ok(result.Data);
    }

    // DELETE api/categories/5 — fails with a clear message while items use it.
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _logic.DeleteCategoryAsync(id);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }
}
