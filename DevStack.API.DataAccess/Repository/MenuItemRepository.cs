using DevStack.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DevStack.API.DataAccess.Repository;

// The concrete implementation of the contract. This is the ONLY place that
// actually touches EF Core / the database. Keeping raw data access in one
// layer means the business logic never writes SQL or worries about EF.
public class MenuItemRepository : IMenuItemRepository
{
    private readonly DevStackDataModel _db;

    // The DbContext is injected in (DI again). `_db` is a private field holding
    // it for the lifetime of this repository.
    public MenuItemRepository(DevStackDataModel db)
    {
        _db = db;
    }

    public async Task<List<MenuItem>> GetAllAsync() =>
        await _db.MenuItems.Include(m => m.Sizes).OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync();

    public async Task<MenuItem?> GetByIdAsync(int id) =>
        await _db.MenuItems.Include(m => m.Sizes).FirstOrDefaultAsync(m => m.Id == id);

    public async Task<MenuItem> AddAsync(MenuItem item)
    {
        _db.MenuItems.Add(item);
        await _db.SaveChangesAsync();
        return item; // now has its DB-generated Id
    }

    public async Task<MenuItem?> UpdateAsync(MenuItem item)
    {
        var existing = await _db.MenuItems.Include(m => m.Sizes).FirstOrDefaultAsync(m => m.Id == item.Id);
        if (existing is null) return null;

        // Copy the incoming values onto the tracked entity, then save.
        // CreatedAt is intentionally left untouched — it's set once, on create.
        existing.Name = item.Name;
        existing.Category = item.Category;
        existing.Price = item.Price;
        existing.Description = item.Description;
        existing.ImageUrl = item.ImageUrl;
        existing.ImagePublicId = item.ImagePublicId;
        existing.IsAvailable = item.IsAvailable;
        existing.StockQuantity = item.StockQuantity;

        // Reconcile sizes: keep matching ids (update price/name), add new ones,
        // drop the ones the client removed. Deleting a size is safe mid-day -
        // existing order lines keep their SizeName/price snapshot.
        var incoming = item.Sizes ?? [];
        existing.Sizes.RemoveAll(s => incoming.All(n => n.Id != s.Id));
        foreach (var size in incoming)
        {
            var match = existing.Sizes.FirstOrDefault(s => s.Id == size.Id && size.Id != 0);
            if (match is not null)
            {
                match.Name = size.Name;
                match.Price = size.Price;
            }
            else
            {
                existing.Sizes.Add(new MenuSize
                {
                    MenuItemId = existing.Id,
                    Name = size.Name,
                    Price = size.Price
                });
            }
        }

        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var existing = await _db.MenuItems.FindAsync(id);
        if (existing is null) return false;

        _db.MenuItems.Remove(existing);
        await _db.SaveChangesAsync();
        return true;
    }
}
