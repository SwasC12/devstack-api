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

    // AsSplitQuery: three independent collection Includes (Sizes,
    // ModifierGroups->Modifiers, RecipeLines) in ONE query make EF Core emit a
    // LEFT JOIN across all of them - a cartesian explosion where the row count
    // is items x sizes x modifiers x recipeLines and every row re-sends the
    // parent columns (including the long ImageUrl). Over a remote DB that is
    // the "extremely slow" menu load. Split queries fetch each collection in
    // its own round trip, so the payload is linear in the data, not the product.
    public async Task<List<MenuItem>> GetAllAsync() =>
        await _db.MenuItems.AsNoTracking()
            .Include(m => m.Sizes)
            .Include(m => m.ModifierGroups).ThenInclude(g => g.Modifiers)
            .Include(m => m.RecipeLines)
            .OrderBy(i => i.Category).ThenBy(i => i.Name)
            .AsSplitQuery()
            .ToListAsync();

    public async Task<MenuItem?> GetByIdAsync(int id) =>
        await _db.MenuItems.AsNoTracking()
            .Include(m => m.Sizes)
            .Include(m => m.ModifierGroups).ThenInclude(g => g.Modifiers)
            .Include(m => m.RecipeLines)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Id == id);

    public async Task<MenuItem> AddAsync(MenuItem item)
    {
        _db.MenuItems.Add(item);
        await _db.SaveChangesAsync();
        return item; // now has its DB-generated Id
    }

    public async Task<MenuItem?> UpdateAsync(MenuItem item)
    {
        var existing = await _db.MenuItems
            .Include(m => m.Sizes)
            .Include(m => m.ModifierGroups).ThenInclude(g => g.Modifiers)
            .Include(m => m.RecipeLines)
            .FirstOrDefaultAsync(m => m.Id == item.Id);
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
        existing.Sku = string.IsNullOrWhiteSpace(item.Sku) ? null : item.Sku.Trim();
        existing.CostBasis = Math.Max(0, item.CostBasis);

        // Reconcile recipe lines: keep matching ids (update cost/qty), add new,
        // drop removed - same pattern as sizes and modifier groups.
        var incomingRecipe = item.RecipeLines ?? [];
        existing.RecipeLines.RemoveAll(r => incomingRecipe.All(n => n.Id != r.Id));
        foreach (var line in incomingRecipe)
        {
            var match = existing.RecipeLines.FirstOrDefault(r => r.Id == line.Id && line.Id != 0);
            if (match is not null)
            {
                match.Name = line.Name.Trim();
                match.CostPerUnit = Math.Max(0, line.CostPerUnit);
                match.Quantity = Math.Max(0, line.Quantity);
            }
            else
            {
                existing.RecipeLines.Add(new RecipeLine
                {
                    MenuItemId = existing.Id,
                    Name = line.Name.Trim(),
                    CostPerUnit = Math.Max(0, line.CostPerUnit),
                    Quantity = Math.Max(0, line.Quantity)
                });
            }
        }

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

        // Reconcile modifier groups (and their options) the same way: keep
        // matching ids, add new, drop removed. Deleting a group mid-day is safe -
        // order lines keep their snapshots.
        var incomingGroups = item.ModifierGroups ?? [];
        existing.ModifierGroups.RemoveAll(g => incomingGroups.All(n => n.Id != g.Id));
        foreach (var group in incomingGroups)
        {
            var match = existing.ModifierGroups.FirstOrDefault(g => g.Id == group.Id && group.Id != 0);
            if (match is not null)
            {
                match.Name = group.Name;
                match.IsMulti = group.IsMulti;
            }
            else
            {
                match = new ModifierGroup { MenuItemId = existing.Id, Name = group.Name, IsMulti = group.IsMulti };
                existing.ModifierGroups.Add(match);
            }

            var incomingMods = group.Modifiers ?? [];
            match.Modifiers.RemoveAll(m => incomingMods.All(n => n.Id != m.Id));
            foreach (var mod in incomingMods)
            {
                var modMatch = match.Modifiers.FirstOrDefault(m => m.Id == mod.Id && mod.Id != 0);
                if (modMatch is not null)
                {
                    modMatch.Name = mod.Name;
                    modMatch.PriceDelta = mod.PriceDelta;
                }
                else
                {
                    match.Modifiers.Add(new Modifier { Name = mod.Name, PriceDelta = mod.PriceDelta });
                }
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

    public async Task<bool> SkuExistsAsync(string sku, int excludeId) =>
        await _db.MenuItems.AnyAsync(m => m.Id != excludeId && m.Sku != null && m.Sku.ToLower() == sku.ToLower());
}
