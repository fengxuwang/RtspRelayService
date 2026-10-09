using ActivityRecordingService.Api;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Api;

public sealed class ApiKeyRateLimiterTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-29T12:00:00.000Z");

    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public void Acquire_LimitsAttemptsAndReturnsRetryAfter()
    {
        var limiter = new ApiKeyRateLimiter(_timeProvider, permitLimit: 2, window: TimeSpan.FromMinutes(1));

        limiter.Acquire("203.0.113.10").Allowed.Should().BeTrue();
        limiter.Acquire("203.0.113.10").Allowed.Should().BeTrue();
        var rejected = limiter.Acquire("203.0.113.10");
        rejected.Allowed.Should().BeFalse();
        rejected.RetryAfter.Should().Be(TimeSpan.FromMinutes(1));

        limiter.Acquire("198.51.100.20").Allowed.Should().BeTrue();
    }

    [Fact]
    public void Acquire_OpensNewWindowAfterBackoffPeriod()
    {
        var limiter = new ApiKeyRateLimiter(_timeProvider, permitLimit: 1, window: TimeSpan.FromSeconds(30));
        limiter.Acquire("203.0.113.10");
        limiter.Acquire("203.0.113.10").Allowed.Should().BeFalse();

        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        limiter.Acquire("203.0.113.10").Allowed.Should().BeTrue();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan value) => _now += value;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
