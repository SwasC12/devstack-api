using System.Collections.Concurrent;

namespace DevStack.API.WebService;

// In-memory failed-attempt lockout for the login endpoints. Keyed by
// IP + username so one attacker can't lock out someone else's account by
// spamming wrong guesses. State is per-instance only — fine at
// single-instance scale; revisit if the API ever runs behind multiple instances.
public interface IAuthThrottle
{
    bool IsLockedOut(string key);
    void RecordFailure(string key);
    void Reset(string key);
}

public class AuthThrottleService : IAuthThrottle
{
    private static readonly ConcurrentDictionary<string, (int failures, DateTime lockedUntil)> _state = new();
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    public bool IsLockedOut(string key) =>
        _state.TryGetValue(key, out var s) && s.lockedUntil > DateTime.UtcNow;

    public void RecordFailure(string key)
    {
        _state.AddOrUpdate(key,
            _ => (1, DateTime.MinValue),
            (_, s) =>
            {
                // Stay locked for the whole window; don't extend it.
                if (s.lockedUntil > DateTime.UtcNow) return s;

                var failures = s.failures + 1;
                return failures >= MaxFailures
                    ? (0, DateTime.UtcNow.Add(LockoutDuration))
                    : (failures, DateTime.MinValue);
            });
    }

    public void Reset(string key) => _state.TryRemove(key, out _);
}
