using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RtspRelayService.Configuration;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;
using RtspRelayService.Supervision;

namespace RtspRelayService.Tests.Supervision;

public sealed class StreamSupervisorFactoryTests
{
    [Fact]
    public async Task Create_UsesValidatedFfmpegPathAndReturnsRunnableSupervisor()
    {
        var stream = new StreamOptions
        {
            Name = "camera-01",
            InputUrl = "rtsp://camera.example/live",
            OutputUrl = "rtmp://media.example/live/camera-01"
        };
        var settings = new RelayOptions
        {
            InitialRestartDelaySeconds = 5,
            MaxRestartDelaySeconds = 60,
            StableRunSeconds = 60,
            InputTimeoutSeconds = 12,
            ShutdownTimeoutSeconds = 10
        };
        var options = new ValidatedRelayOptions(
            "/opt/relay/ffmpeg",
            settings,
            [stream],
            []);
        var runner = new RecordingRunner();
        var factory = new StreamSupervisorFactory(
            options,
            runner,
            TimeProvider.System,
            NullLoggerFactory.Instance);
        using var stop = new CancellationTokenSource();

        var running = factory.Create(stream)(stop.Token);
        var command = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        command.FileName.Should().Be("/opt/relay/ffmpeg");
        command.Arguments.Should().ContainInOrder("-timeout", "12000000", "-i");
        runner.Process.WasKilled.Should().BeTrue();
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public TaskCompletionSource<FfmpegCommand> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RecordingProcess Process { get; } = new();

        public Task<IManagedProcess> StartAsync(
            FfmpegCommand command,
            Action<string> onStandardError,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(command);
            return Task.FromResult<IManagedProcess>(Process);
        }
    }

    private sealed class RecordingProcess : IManagedProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 42;
        public bool WasKilled { get; private set; }
        public Task<int> WaitForExitAsync(CancellationToken token) => _exit.Task.WaitAsync(token);

        public Task KillTreeAsync(CancellationToken token)
        {
            WasKilled = true;
            _exit.TrySetResult(-1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
