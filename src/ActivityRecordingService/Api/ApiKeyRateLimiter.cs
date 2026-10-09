namespace ActivityRecordingService.Api;

public sealed record ApiKeyRateLimitDecision(bool Allowed, TimeSpan? RetryAfter);

public sealed class ApiKeyRateLimiter
{
    private const int MaximumTrackedKeys = 10_000;
    private readonly TimeProvider _timeProvider;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Dictionary<string, WindowState> _windows = new(StringComparer.OrdinalIgnoreCase);

    public ApiKeyRateLimiter(
        TimeProvider timeProvider,
        int permitLimit = 5,
        TimeSpan? window = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (permitLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(permitLimit));
        _permitLimit = permitLimit;
        _window = window ?? TimeSpan.FromMinutes(1);
        if (_window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
    }

    public ApiKeyRateLimitDecision Acquire(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_windows.TryGetValue(key, out var state) &&
                now - state.WindowStartUtc >= _window)
            {
                state = state with { Count = 0, WindowStartUtc = now };
                _windows[key] = state;
            }

            state ??= new WindowState(now, 0);
            if (state.Count >= _permitLimit)
            {
                var retryAfter = state.WindowStartUtc + _window - now;
                if (retryAfter <= TimeSpan.Zero)
                    retryAfter = TimeSpan.FromSeconds(1);
                return new ApiKeyRateLimitDecision(false, retryAfter);
            }

            _windows[key] = state with { Count = state.Count + 1 };
            if (_windows.Count > MaximumTrackedKeys)
                RemoveExpiredWindows(now);
            return new ApiKeyRateLimitDecision(true, null);
        }
    }

    private void RemoveExpiredWindows(DateTimeOffset now)
    {
        foreach (var pair in _windows.Where(pair =>
                     now - pair.Value.WindowStartUtc >= _window)
                     .ToArray())
        {
            _windows.Remove(pair.Key);
        }
    }

    private sealed record WindowState(DateTimeOffset WindowStartUtc, int Count);
}
