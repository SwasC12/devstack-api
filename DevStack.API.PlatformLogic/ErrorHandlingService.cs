using DevStack.API.Models;

namespace DevStack.API.PlatformLogic;

// Keep this simple: maps common exception types to clean error messages
// so the controller (and ultimately the HTTP response) doesn't leak stack traces.
public class ErrorHandlingService : IErrorHandling
{
    public ResultModel HandleException(Exception error)
    {
        var message = error switch
        {
            KeyNotFoundException => error.Message,                      // 404 – "not found" is safe to echo
            UnauthorizedAccessException => error.Message,                // 403 – permission denial
            ArgumentException => error.Message,                         // 400 – bad input
            _ => "An unexpected error occurred. Please try again later." // 500 – hide internals
        };

        return ResultModel.Failure(message);
    }

    public ResultModel<T> HandleException<T>(Exception error)
    {
        var message = error switch
        {
            KeyNotFoundException => error.Message,
            UnauthorizedAccessException => error.Message,
            ArgumentException => error.Message,
            _ => "An unexpected error occurred. Please try again later."
        };

        return ResultModel<T>.Failure(message);
    }
}
