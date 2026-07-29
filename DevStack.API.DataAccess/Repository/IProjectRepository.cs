using DevStack.API.Models;

namespace DevStack.API.DataAccess.Repository;

// An "interface" = a contract. It lists WHAT operations exist, not HOW they
// work. The logic layer depends on this interface, not the concrete class —
// so we could swap the implementation (or fake it in a test) without changing
// anything upstream. This is the heart of "dependency inversion".
public interface IProjectRepository
{
    Task<List<Project>> GetAllAsync();
    Task<Project?> GetByIdAsync(int id);
    Task<Project> AddAsync(Project project);
    Task<bool> UpdateAsync(Project project);
    Task<bool> DeleteAsync(int id);
}
