using DevStack.API.DataAccess.Repository;
using DevStack.API.Models;

namespace DevStack.API.PlatformLogic.ToolLogic;

// Business logic. It orchestrates: applies rules/defaults, then delegates the
// actual storage to the repository. Depends on IToolRepository (the contract),
// not the concrete ToolRepository — so it never sees EF Core.
public class ToolLogic : IToolLogic
{
    private readonly IToolRepository _repository;

    public ToolLogic(IToolRepository repository)
    {
        _repository = repository;
    }

    public Task<List<Tool>> GetToolsAsync() => _repository.GetAllAsync();

    public Task<Tool?> GetToolAsync(int id) => _repository.GetByIdAsync(id);

    public Task<Tool> CreateToolAsync(Tool tool)
    {
        // Business rules the API shouldn't trust the client for:
        tool.CreatedAt = DateTime.UtcNow;                 // server owns the timestamp
        tool.Name = tool.Name.Trim();                     // tidy input
        if (string.IsNullOrWhiteSpace(tool.Currency))
            tool.Currency = "USD";                        // sensible default
        if (!tool.IsPaid)
            tool.MonthlyCost = null;                      // free tools can't have a cost

        return _repository.AddAsync(tool);
    }

    public Task<bool> UpdateToolAsync(int id, Tool tool)
    {
        tool.Id = id;                                     // trust the route id, not the body
        if (!tool.IsPaid) tool.MonthlyCost = null;
        return _repository.UpdateAsync(tool);
    }

    public Task<bool> DeleteToolAsync(int id) => _repository.DeleteAsync(id);
}
