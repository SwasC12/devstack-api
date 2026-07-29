using DevStack.API.DataAccess.Repository;
using DevStack.API.Models;

namespace DevStack.API.PlatformLogic.ProjectLogic;

public class ProjectLogic : IProjectLogic
{
    private readonly IProjectRepository _repository;

    public ProjectLogic(IProjectRepository repository)
    {
        _repository = repository;
    }

    public Task<List<Project>> GetProjectsAsync() => _repository.GetAllAsync();

    public Task<Project?> GetProjectAsync(int id) => _repository.GetByIdAsync(id);

    public Task<Project> CreateProjectAsync(Project project)
    {
        project.CreatedAt = DateTime.UtcNow;
        project.Name = project.Name.Trim();
        return _repository.AddAsync(project);
    }

    public Task<bool> UpdateProjectAsync(int id, Project project)
    {
        project.Id = id;
        return _repository.UpdateAsync(project);
    }

    public Task<bool> DeleteProjectAsync(int id) => _repository.DeleteAsync(id);

}