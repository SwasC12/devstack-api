namespace DevStack.API.Models;

// Return envelope for every business-logic method. The controller checks
// .IsSuccess to decide 200 vs 4xx/5xx; the caller never sees a bare exception.
public class ResultModel<T>
{
    public T? Data { get; init; }
    public string? Error { get; init; }
    public bool IsSuccess => Error is null;

    public static ResultModel<T> Success(T data) => new() { Data = data };
    public static ResultModel<T> Failure(string error) => new() { Error = error };
}

// Non-generic version for void-like operations (create, delete, etc. that don't
// return meaningful data but still need the success/failure envelope).
public class ResultModel
{
    public string? Error { get; init; }
    public bool IsSuccess => Error is null;

    public static ResultModel Success() => new();
    public static ResultModel Failure(string error) => new() { Error = error };
}
