using DevStack.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.DataAccess.Repository;

// The concrete implementation of the category contract. This is the ONLY place
// that touches EF Core for categories. Renaming a category cascades to every
// menu item tagged with the old name, so nothing points at a dead category.
public class CategoryRepository : ICategoryRepository
{
    private readonly DevStackDataModel _db;

    public CategoryRepository(DevStackDataModel db)
    {
        _db = db;
    }

    public async Task<List<Category>> GetAllAsync() =>
        await _db.Categories.OrderBy(c => c.Name).ToListAsync();

    public async Task<Category?> GetByIdAsync(int id) =>
        await _db.Categories.FindAsync(id);

    public async Task<Category?> GetByNameAsync(string name) =>
        await _db.Categories.FirstOrDefaultAsync(c => c.Name == name);

    public async Task<Category> AddAsync(Category category)
    {
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        return category; // now has its DB-generated Id
    }

    public async Task<Category?> UpdateAsync(Category category)
    {
        var existing = await _db.Categories.FindAsync(category.Id);
        if (existing is null) return null;

        // Cascade a rename to every menu item using the old name, so items
        // keep pointing at the (renamed) category. `==` is case-insensitive
        // under SQL Server's default collation, matching how items are ordered.
        if (existing.Name != category.Name)
        {
            var items = await _db.MenuItems
                .Where(m => m.Category == existing.Name)
                .ToListAsync();
            foreach (var item in items)
                item.Category = category.Name;
        }

        existing.Name = category.Name;
        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var existing = await _db.Categories.FindAsync(id);
        if (existing is null) return false;

        _db.Categories.Remove(existing);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> CountItemsUsingAsync(string categoryName) =>
        await _db.MenuItems.CountAsync(m => m.Category == categoryName);
}
