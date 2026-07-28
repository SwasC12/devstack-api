using DevStack.API.Models;

namespace DevStack.API.PlatformLogic.ToolLogic;

// The contract for the business-logic layer. The WebService (controller)
// depends on THIS, so the controller stays thin and knows nothing about EF.
public interface IToolLogic
{
    Task<List<Tool>> GetToolsAsync();
    Task<Tool?> GetToolAsync(int id);
    Task<Tool> CreateToolAsync(Tool tool);
    Task<bool> UpdateToolAsync(int id, Tool tool);
    Task<bool> DeleteToolAsync(int id);
}
