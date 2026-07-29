using DevStack.API.Models;

namespace DevStack.API.PlatformLogic.MenuItemLogic;

public interface IMenuItemLogic
{
    Task<ResultModel<List<MenuItem>>> GetItemsAsync();
    Task<ResultModel<MenuItem?>> GetItemAsync(int id);
    Task<ResultModel<MenuItem>> WriteItemAsync(MenuItem item);
    Task<ResultModel> DeleteItemAsync(int id);
}
