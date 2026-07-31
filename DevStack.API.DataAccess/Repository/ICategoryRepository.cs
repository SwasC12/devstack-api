using DevStack.API.Models;

namespace DevStack.API.DataAccess.Repository;

// The contract for category data access. Same shape as IMenuItemRepository —
// the logic layer depends on this interface, not the concrete class.
public interface ICategoryRepository
{
    Task<List<Category>> GetAllAsync();
    Task<Category?> GetByIdAsync(int id);
    Task<Category?> GetByNameAsync(string name); // duplicate check (case-insensitive via SQL collation)
    Task<Category> AddAsync(Category category);
    Task<Category?> UpdateAsync(Category category); // null if the id doesn't exist; renames cascade to menu items
    Task<bool> DeleteAsync(int id);
    Task<int> CountItemsUsingAsync(string categoryName);
}
