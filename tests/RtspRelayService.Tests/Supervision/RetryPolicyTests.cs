using FluentAssertions;
using RtspRelayService.Supervision;

namespace RtspRelayService.Tests.Supervision;

public sealed class RetryPolicyTests
{
    private readonly RetryPolicy _policy =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(20, 60)]
    [InlineData(int.MaxValue, 60)]
    public void GetDelay_IsExponentialAndCapped(int failureCount, int seconds) =>
        _policy.GetDelay(failureCount).Should().Be(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void NextFailureCount_ResetsAfterStableRun() =>
        _policy.NextFailureCount(4, TimeSpan.FromSeconds(60)).Should().Be(1);

    [Fact]
    public void NextFailureCount_IncrementsAfterShortRun() =>
        _policy.NextFailureCount(4, TimeSpan.FromSeconds(59)).Should().Be(5);

    [Fact]
    public void NextFailureCount_SaturatesAtMaximumIntegerValue() =>
        _policy.NextFailureCount(int.MaxValue, TimeSpan.Zero).Should().Be(int.MaxValue);
}
