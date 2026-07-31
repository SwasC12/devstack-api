using DevStack.API.Models;

namespace DevStack.API.PlatformLogic.CategoryLogic;

public interface ICategoryLogic
{
    Task<ResultModel<List<Category>>> GetCategoriesAsync();
    Task<ResultModel<Category?>> GetCategoryAsync(int id);
    Task<ResultModel<Category>> WriteCategoryAsync(Category category);
    Task<ResultModel> DeleteCategoryAsync(int id);
}
