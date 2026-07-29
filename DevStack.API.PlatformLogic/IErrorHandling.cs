using DevStack.API.Models;

namespace DevStack.API.PlatformLogic;

// Maps any exception to a ResultModel with a user-safe message, keeping the ugly
// details out of the controller (and the HTTP response). Inject this everywhere.
public interface IErrorHandling
{
    ResultModel HandleException(Exception error);
    ResultModel<T> HandleException<T>(Exception error);
}
