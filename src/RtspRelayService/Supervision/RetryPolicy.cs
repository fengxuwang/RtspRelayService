namespace RtspRelayService.Supervision;

public sealed class RetryPolicy(TimeSpan initial, TimeSpan maximum, TimeSpan stableRun)
{
    public TimeSpan GetDelay(int failureCount)
    {
        if (failureCount < 1)
            throw new ArgumentOutOfRangeException(nameof(failureCount));

        var exponent = Math.Min(failureCount - 1, 30);
        var ticks = Math.Min(initial.Ticks * Math.Pow(2, exponent), maximum.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    public int NextFailureCount(int currentFailureCount, TimeSpan runDuration)
    {
        if (runDuration >= stableRun)
            return 1;

        return currentFailureCount == int.MaxValue
            ? int.MaxValue
            : currentFailureCount + 1;
    }
}
