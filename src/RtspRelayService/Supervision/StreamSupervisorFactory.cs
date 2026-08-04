using RtspRelayService.Configuration;
using RtspRelayService.Processes;

namespace RtspRelayService.Supervision;

public sealed class StreamSupervisorFactory(
    ValidatedRelayOptions options,
    IProcessRunner processRunner,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IStreamSupervisorFactory
{
    public Func<CancellationToken, Task> Create(StreamOptions stream)
    {
        var retryPolicy = new RetryPolicy(
            TimeSpan.FromSeconds(options.Settings.InitialRestartDelaySeconds),
            TimeSpan.FromSeconds(options.Settings.MaxRestartDelaySeconds),
            TimeSpan.FromSeconds(options.Settings.StableRunSeconds));
        var supervisor = new StreamSupervisor(
            stream,
            options.FfmpegPath,
            processRunner,
            retryPolicy,
            TimeSpan.FromSeconds(options.Settings.InputTimeoutSeconds),
            TimeSpan.FromSeconds(options.Settings.ShutdownTimeoutSeconds),
            timeProvider,
            loggerFactory.CreateLogger<StreamSupervisor>());

        return supervisor.RunAsync;
    }
}
