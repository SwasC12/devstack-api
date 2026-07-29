using DevStack.API.Models;
using DevStack.API.PlatformLogic.MenuItemLogic;
using Microsoft.AspNetCore.Mvc;

namespace DevStack.API.WebService.Controllers;

// [ApiController] turns on helpful API behaviour (automatic model validation,
// binding, etc.). The route "api/menuitems" comes from the class name via
// [controller] = "MenuItems" (MVC strips the "Controller" suffix, lowercased).
//
// Two write-side endpoints only, by design:
//   PUT    api/menuitems      → Write  (creates when Id == 0, otherwise edits)
//   DELETE api/menuitems/5    → Delete
// (Plus the GET reads.) PUT is used for the write because it's idempotent:
// sending the same item twice lands the same state.
[ApiController]
[Route("api/[controller]")]
public class MenuItemsController : ControllerBase
{
    // The controller depends only on the LOGIC layer's interface. It knows
    // nothing about the data layer — that's the point of the layering.
    private readonly IMenuItemLogic _logic;

    public MenuItemsController(IMenuItemLogic logic)
    {
        _logic = logic;
    }

    // GET api/menuitems
    [HttpGet]
    public async Task<ActionResult<List<MenuItem>>> GetAll()
    {
        var result = await _logic.GetItemsAsync();
        return result.IsSuccess ? Ok(result.Data) : StatusCode(500, result.Error);
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
    [HttpPut]
    public async Task<ActionResult<MenuItem>> Write(MenuItem item)
    {
        var result = await _logic.WriteItemAsync(item);
        if (!result.IsSuccess) return BadRequest(new { error = result.Error });

        // The logic layer sets CreatedAt only on new items, so we can check it
        // to decide 201 vs 200. (Simpler than returning a separate flag.)
        var isBrandNew = item.Id == 0;
        return isBrandNew
            ? CreatedAtAction(nameof(Get), new { id = result.Data!.Id }, result.Data)
            : Ok(result.Data);
    }

    // DELETE api/menuitems/5
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _logic.DeleteItemAsync(id);
        return result.IsSuccess ? NoContent() : NotFound(new { error = result.Error });
    }
}
