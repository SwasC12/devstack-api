using DevStack.API.DataAccess.Repository;
using DevStack.API.Models;

namespace DevStack.API.PlatformLogic.CategoryLogic;

// Business logic for categories. Same shape as MenuItemLogic: every public
// method returns a ResultModel<T> and lets _errorHandling map exceptions to
// clean messages.
public class CategoryLogic : ICategoryLogic
{
    private readonly ICategoryRepository _repo;
    private readonly IErrorHandling _errorHandling;
    private readonly ICurrentShop _currentShop;

    public CategoryLogic(ICategoryRepository repo, IErrorHandling errorHandling, ICurrentShop currentShop)
    {
        _repo = repo;
        _errorHandling = errorHandling;
        _currentShop = currentShop;
    }

    // ── READ ──────────────────────────────────────────────────────────────────

    public async Task<ResultModel<List<Category>>> GetCategoriesAsync()
    {
        try
        {
            var categories = await _repo.GetAllAsync();
            return ResultModel<List<Category>>.Success(categories);
        }
        catch (Exception error)
        {
            return _errorHandling.HandleException<List<Category>>(error);
        }
    }

    public async Task<ResultModel<Category?>> GetCategoryAsync(int id)
    {
        try
        {
            var category = await _repo.GetByIdAsync(id)
                ?? throw new KeyNotFoundException($"Category {id} not found.");

            return ResultModel<Category?>.Success(category);
        }
        catch (Exception error)
        {
            return _errorHandling.HandleException<Category?>(error);
        }
    }

    // ── WRITE (create / rename) ───────────────────────────────────────────────

    // Single write path: Id == 0 means "new", anything else means "rename that one".
    public async Task<ResultModel<Category>> WriteCategoryAsync(Category category)
    {
        try
        {
            category.Name = category.Name.Trim();
            if (category.Name.Length == 0)
                throw new ArgumentException("Category name cannot be empty.");

            // Categories always belong to the current shop; the client can't change it.
            category.ShopId = _currentShop.ShopId;
            category.Station = category.Station is "kitchen" or "bar" ? category.Station : "both";

            // Category names must be unique (the dropdown and the rename cascade
            // both assume a name maps to one category). Ignore self when editing.
            var duplicate = await _repo.GetByNameAsync(category.Name);
            if (duplicate is not null && duplicate.Id != category.Id)
                throw new ArgumentException($"A category named '{category.Name}' already exists.");

            if (category.Id == 0)
            {
                category.CreatedAt = DateTime.UtcNow.AddHours(2);
                var created = await _repo.AddAsync(category);
                return ResultModel<Category>.Success(created);
            }

            var updated = await _repo.UpdateAsync(category)
                ?? throw new KeyNotFoundException($"Category {category.Id} not found.");

            return ResultModel<Category>.Success(updated);
        }
        catch (Exception error)
        {
            return _errorHandling.HandleException<Category>(error);
        }
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    public async Task<ResultModel> DeleteCategoryAsync(int id)
    {
        try
        {
            var category = await _repo.GetByIdAsync(id)
                ?? throw new KeyNotFoundException($"Category {id} not found.");

            // Don't leave menu items pointing at a dead category.
            var inUse = await _repo.CountItemsUsingAsync(category.Name);
            if (inUse > 0)
                throw new ArgumentException(
                    $"Category '{category.Name}' is used by {inUse} item(s). Move or delete those items first.");

            await _repo.DeleteAsync(id);
            return ResultModel.Success();
        }
        catch (Exception error)
        {
            return _errorHandling.HandleException(error);
        }
    }
}
