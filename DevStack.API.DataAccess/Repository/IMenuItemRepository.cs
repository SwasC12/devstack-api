using DevStack.API.Models;

namespace DevStack.API.DataAccess.Repository;

// An "interface" = a contract. It lists WHAT operations exist, not HOW they
// work. The logic layer depends on this interface, not the concrete class —
// so we could swap the implementation (or fake it in a test) without changing
// anything upstream. This is the heart of "dependency inversion".
public interface IMenuItemRepository
{
    Task<List<MenuItem>> GetAllAsync();
    Task<MenuItem?> GetByIdAsync(int id);
    Task<MenuItem> AddAsync(MenuItem item);
    Task<MenuItem?> UpdateAsync(MenuItem item); // null if the id doesn't exist
    Task<bool> DeleteAsync(int id);
    Task<bool> SkuExistsAsync(string sku, int excludeId); // case-insensitive, per shop (global filter)
}
