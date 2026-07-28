using DevStack.API.Models;
using DevStack.API.PlatformLogic.ToolLogic;
using Microsoft.AspNetCore.Mvc;

namespace DevStack.API.WebService.Controllers;

// [ApiController] turns on helpful API behaviour (automatic model validation,
// binding, etc.). The route "api/tools" comes from the class name via
// [controller] = "Tools" (MVC strips the "Controller" suffix, lowercased).
[ApiController]
[Route("api/[controller]")]
public class ToolsController : ControllerBase
{
    // The controller depends only on the LOGIC layer's interface. It knows
    // nothing about repositories or EF — that's the point of the layering.
    private readonly IToolLogic _toolLogic;

    public ToolsController(IToolLogic toolLogic)
    {
        _toolLogic = toolLogic;
    }

    // GET api/tools
    [HttpGet]
    public async Task<ActionResult<List<Tool>>> GetAll() =>
        Ok(await _toolLogic.GetToolsAsync());

    // GET api/tools/5
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Tool>> Get(int id)
    {
        var tool = await _toolLogic.GetToolAsync(id);
        return tool is null ? NotFound() : Ok(tool);
    }

    // POST api/tools
    [HttpPost]
    public async Task<ActionResult<Tool>> Create(Tool tool)
    {
        var created = await _toolLogic.CreateToolAsync(tool);
        // 201 Created + a Location header pointing at the new resource.
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    // PUT api/tools/5
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Tool tool)
    {
        var ok = await _toolLogic.UpdateToolAsync(id, tool);
        return ok ? NoContent() : NotFound();
    }

    // DELETE api/tools/5
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _toolLogic.DeleteToolAsync(id);
        return ok ? NoContent() : NotFound();
    }
}
