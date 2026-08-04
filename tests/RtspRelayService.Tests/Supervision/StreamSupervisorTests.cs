using System.Diagnostics;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RtspRelayService.Configuration;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;
using RtspRelayService.Supervision;

namespace RtspRelayService.Tests.Supervision;

public sealed class StreamSupervisorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task RunAsync_RestartsOnlyAfterConfiguredDelay()
    {
        var time = new ObservableTimeProvider();
        var runner = new FakeRunner();
        var supervisor = CreateSupervisor(runner, time);
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(stop.Token);
        await runner.WaitForStartsAsync(1);
        runner.Processes[0].Exit(1);
        await time.WaitForDelaysAsync(1);
        time.Advance(TimeSpan.FromSeconds(4));
        runner.StartCount.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await runner.WaitForStartsAsync(2);
        stop.Cancel();
        await running.WaitAsync(TestTimeout);

        runner.Processes[1].WasKilled.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WhenCancelledDuringBackoff_DoesNotStartAgain()
    {
        var time = new ObservableTimeProvider();
        var runner = new FakeRunner();
        var supervisor = CreateSupervisor(runner, time);
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(stop.Token);
        await runner.WaitForStartsAsync(1);
        runner.Processes[0].Exit(1);
        await time.WaitForDelaysAsync(1);
        stop.Cancel();
        await running.WaitAsync(TestTimeout);
        time.Advance(TimeSpan.FromMinutes(1));

        runner.StartCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WhenProcessWaitFails_LogsAndRetriesAfterBackoff()
    {
        var time = new ObservableTimeProvider();
        var runner = new FakeRunner();
        var logger = new RecordingLogger<StreamSupervisor>();
        var supervisor = CreateSupervisor(runner, time, logger);
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(stop.Token);
        await runner.WaitForStartsAsync(1);
        runner.Processes[0].Fail(new InvalidOperationException("wait failed"));
        await time.WaitForDelaysAsync(1);

        time.Advance(TimeSpan.FromSeconds(5));
        await runner.WaitForStartsAsync(2);
        stop.Cancel();
        await running.WaitAsync(TestTimeout);

        runner.Processes[0].WasKilled.Should().BeTrue();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("wait failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(KillFailureMode.Timeout)]
    [InlineData(KillFailureMode.Exception)]
    public async Task RunAsync_WhenFailedProcessCannotBeStopped_DoesNotStartReplacement(
        KillFailureMode failureMode)
    {
        var time = new ObservableTimeProvider();
        var runner = new FakeRunner();
        var supervisor = CreateSupervisor(
            runner,
            time,
            shutdownTimeout: TimeSpan.FromMilliseconds(20));
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(stop.Token);
        await runner.WaitForStartsAsync(1);
        runner.Processes[0].KillFailureMode = failureMode;
        runner.Processes[0].Fail(new InvalidOperationException("wait failed"));

        await running.WaitAsync(TestTimeout);
        time.Advance(TimeSpan.FromMinutes(1));

        runner.StartCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WhenProcessStartFails_LogsAndRetriesAfterBackoff()
    {
        var time = new ObservableTimeProvider();
        var runner = new FakeRunner();
        runner.FailNextStart(new InvalidOperationException("start failed"));
        var logger = new RecordingLogger<StreamSupervisor>();
        var supervisor = CreateSupervisor(runner, time, logger);
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(stop.Token);
        await runner.WaitForStartAttemptsAsync(1);
        await time.WaitForDelaysAsync(1);

        time.Advance(TimeSpan.FromSeconds(5));
        await runner.WaitForStartsAsync(1);
        stop.Cancel();
        await running.WaitAsync(TestTimeout);

        runner.StartAttemptCount.Should().Be(2);
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("start failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenCancelledDuringProcessStart_StopsWithoutRetrying()
    {
        var time = new ObservableTimeProvider();
        var runner = new FakeRunner();
        var resumeStart = runner.PauseNextStart();
        var supervisor = CreateSupervisor(runner, time);
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(stop.Token);
        await runner.WaitForStartAttemptsAsync(1);
        stop.Cancel();
        await running.WaitAsync(TestTimeout);
        resumeStart.TrySetResult();
        time.Advance(TimeSpan.FromMinutes(1));

        runner.StartAttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WhenStartupIsSlowButProcessRunIsShort_UsesNextBackoffDelay()
    {
        var time = new ObservableTimeProvider();
        var runner = new FakeRunner();
        var supervisor = CreateSupervisor(runner, time);
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(stop.Token);
        await runner.WaitForStartsAsync(1);
        runner.Processes[0].Exit(1);
        await time.WaitForDelaysAsync(1);

        var resumeStart = runner.PauseNextStart();
        time.Advance(TimeSpan.FromSeconds(5));
        await runner.WaitForStartAttemptsAsync(2);
        time.Advance(TimeSpan.FromSeconds(60));
        resumeStart.TrySetResult();
        await runner.WaitForStartsAsync(2);

        runner.Processes[1].Exit(1);
        await time.WaitForDelaysAsync(2);
        time.Advance(TimeSpan.FromSeconds(5));
        runner.StartCount.Should().Be(2);
        time.Advance(TimeSpan.FromSeconds(5));
        await runner.WaitForStartsAsync(3);

        stop.Cancel();
        await running.WaitAsync(TestTimeout);
    }

    private static StreamSupervisor CreateSupervisor(
        FakeRunner runner,
        TimeProvider time,
        ILogger<StreamSupervisor>? logger = null,
        TimeSpan? shutdownTimeout = null) => new(
        new StreamOptions
        {
            Name = "camera-01",
            InputUrl = "rtsp://user:pass@camera/live",
            OutputUrl = "rtmp://media/live/camera?sign=secret"
        },
        "/tools/ffmpeg",
        runner,
        new RetryPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)),
        TimeSpan.FromSeconds(10),
        shutdownTimeout ?? TimeSpan.FromSeconds(10),
        time,
        logger ?? NullLogger<StreamSupervisor>.Instance);

    private sealed class FakeRunner : IProcessRunner
    {
        private TaskCompletionSource? _nextStartGate;
        private Exception? _nextStartFailure;
        private int _startAttemptCount;

        public List<FakeProcess> Processes { get; } = [];
        public int StartCount => Processes.Count;
        public int StartAttemptCount => Volatile.Read(ref _startAttemptCount);

        public async Task<IManagedProcess> StartAsync(
            FfmpegCommand command,
            Action<string> onStandardError,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startAttemptCount);
            var startGate = Interlocked.Exchange(ref _nextStartGate, null);
            if (startGate is not null)
                await startGate.Task.WaitAsync(cancellationToken);

            var startFailure = Interlocked.Exchange(ref _nextStartFailure, null);
            if (startFailure is not null)
                throw startFailure;

            Processes.Add(new FakeProcess());
            return Processes[^1];
        }

        public void FailNextStart(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _nextStartFailure, exception, null) is not null)
                throw new InvalidOperationException("A process start failure is already configured.");
        }

        public TaskCompletionSource PauseNextStart()
        {
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.Exchange(ref _nextStartGate, startGate) is not null)
                throw new InvalidOperationException("A process start is already paused.");

            return startGate;
        }

        public Task WaitForStartsAsync(int count) =>
            WaitForCountAsync(() => StartCount, count, "process starts");

        public Task WaitForStartAttemptsAsync(int count) =>
            WaitForCountAsync(() => StartAttemptCount, count, "process start attempts");

        private static async Task WaitForCountAsync(
            Func<int> getCount,
            int expected,
            string description)
        {
            var timeout = Stopwatch.StartNew();
            while (getCount() < expected)
            {
                if (timeout.Elapsed > TestTimeout)
                    throw new TimeoutException(
                        $"Expected {expected} {description}, but observed {getCount()}.");

                await Task.Yield();
            }
        }
    }

    private sealed class FakeProcess : IManagedProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 42;
        public bool WasKilled { get; private set; }
        public KillFailureMode KillFailureMode { get; set; }
        public void Exit(int code) => _exit.TrySetResult(code);
        public void Fail(Exception exception) => _exit.TrySetException(exception);
        public Task<int> WaitForExitAsync(CancellationToken token) => _exit.Task.WaitAsync(token);

        public async Task KillTreeAsync(CancellationToken token)
        {
            WasKilled = true;
            if (KillFailureMode == KillFailureMode.Timeout)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return;
            }

            if (KillFailureMode == KillFailureMode.Exception)
                throw new InvalidOperationException("kill failed");

            _exit.TrySetResult(-1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public enum KillFailureMode
    {
        None,
        Timeout,
        Exception
    }

    private sealed class ObservableTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private int _delayCount;

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _delayCount);
            return timer;
        }

        public async Task WaitForDelaysAsync(int count)
        {
            var timeout = Stopwatch.StartNew();
            while (Volatile.Read(ref _delayCount) < count)
            {
                if (timeout.Elapsed > TestTimeout)
                    throw new TimeoutException(
                        $"Expected {count} scheduled delays, but observed {_delayCount}.");

                await Task.Yield();
            }
        }

        public void Advance(TimeSpan duration) => _inner.Advance(duration);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new(logLevel, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
