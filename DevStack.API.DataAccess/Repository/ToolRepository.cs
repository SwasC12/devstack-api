using DevStack.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.DataAccess.Repository;

// The concrete implementation of the contract. This is the ONLY place that
// actually touches EF Core / the database. Keeping raw data access in one
// layer means the business logic never writes SQL or worries about EF.
public class ToolRepository : IToolRepository
{
    private readonly DevStackDataModel _db;

    // The DbContext is injected in (DI again). `_db` is a private field holding
    // it for the lifetime of this repository.
    public ToolRepository(DevStackDataModel db)
    {
        _db = db;
    }

    public async Task<List<Tool>> GetAllAsync() =>
        await _db.Tools.OrderBy(t => t.Name).ToListAsync();

    public async Task<Tool?> GetByIdAsync(int id) =>
        await _db.Tools.FindAsync(id);

    public async Task<Tool> AddAsync(Tool tool)
    {
        _db.Tools.Add(tool);
        await _db.SaveChangesAsync();
        return tool; // now has its DB-generated Id
    }

    public async Task<bool> UpdateAsync(Tool tool)
    {
        var existing = await _db.Tools.FindAsync(tool.Id);
        if (existing is null) return false;

        // Copy the incoming values onto the tracked entity, then save.
        existing.Name = tool.Name;
        existing.Category = tool.Category;
        existing.Url = tool.Url;
        existing.Notes = tool.Notes;
        existing.IsPaid = tool.IsPaid;
        existing.MonthlyCost = tool.MonthlyCost;
        existing.Currency = tool.Currency;
        existing.Projects = tool.Projects;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var existing = await _db.Tools.FindAsync(id);
        if (existing is null) return false;

        _db.Tools.Remove(existing);
        await _db.SaveChangesAsync();
        return true;
    }
}
