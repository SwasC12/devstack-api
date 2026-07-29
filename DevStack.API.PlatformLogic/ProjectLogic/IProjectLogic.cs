using DevStack.API.Models;

namespace DevStack.API.PlatformLogic.ProjectLogic;

public interface IProjectLogic
{
    Task<List<Project>> GetProjectsAsync();
    Task<Project?> GetProjectAsync(int id);
    Task<Project> CreateProjectAsync(Project project);
    Task<bool> UpdateProjectAsync(int id, Project project);
    Task<bool> DeleteProjectAsync(int id);
}