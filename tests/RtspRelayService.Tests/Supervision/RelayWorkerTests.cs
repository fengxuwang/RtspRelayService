using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RtspRelayService.Configuration;
using RtspRelayService.Supervision;

namespace RtspRelayService.Tests.Supervision;

public sealed class RelayWorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task StartAsync_StartsEveryValidStreamConcurrently()
    {
        var startedNames = new ConcurrentBag<string>();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelegateFactory(stream => async token =>
        {
            startedNames.Add(stream.Name);
            if (startedNames.Count == 2)
                allStarted.TrySetResult();

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var worker = CreateWorker(factory, ["one", "two"]);

        await worker.StartAsync(default);
        await allStarted.Task.WaitAsync(TestTimeout);
        await worker.StopAsync(default);

        startedNames.Should().BeEquivalentTo("one", "two");
    }

    [Fact]
    public async Task StopAsync_WaitsForEverySupervisorToExit()
    {
        using var started = new CountdownEvent(2);
        using var stopping = new CountdownEvent(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelegateFactory(_ => async token =>
        {
            started.Signal();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                stopping.Signal();
                await release.Task;
            }
        });
        var worker = CreateWorker(factory, ["one", "two"]);

        await worker.StartAsync(default);
        started.Wait(TestTimeout).Should().BeTrue();
        var stopTask = worker.StopAsync(default);
        stopping.Wait(TestTimeout).Should().BeTrue();

        stopTask.IsCompleted.Should().BeFalse();
        release.TrySetResult();
        await stopTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task StopAsync_WithCancelledHostToken_JoinsSupervisorAndPropagatesItsFault()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelegateFactory(_ => async token =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                stopping.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("shutdown failed");
            }
        });
        var worker = CreateWorker(factory, ["one"]);
        using var hostStop = new CancellationTokenSource();

        await worker.StartAsync(default);
        await started.Task.WaitAsync(TestTimeout);
        hostStop.Cancel();
        var stopTask = worker.StopAsync(hostStop.Token);
        await stopping.Task.WaitAsync(TestTimeout);

        stopTask.IsCompleted.Should().BeFalse();
        release.TrySetResult();
        Func<Task> waitForStop = () => stopTask.WaitAsync(TestTimeout);
        await waitForStop.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("shutdown failed");
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task SupervisorFailure_IsLoggedWithoutStoppingOtherSupervisorOrApplication()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyStopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelegateFactory(stream => stream.Name == "broken" ? _ =>
        {
            failed.TrySetResult();
            throw new InvalidOperationException("supervisor failed");
        } : async token =>
        {
            healthyStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                healthyStopping.TrySetResult();
            }
        });
        var logger = new RecordingLogger<RelayWorker>();
        var worker = CreateWorker(factory, ["broken", "healthy"], logger);

        await worker.StartAsync(default);
        await failed.Task.WaitAsync(TestTimeout);
        await healthyStarted.Task.WaitAsync(TestTimeout);
        await WaitForConditionAsync(() => logger.Entries.Any(entry =>
            entry.Level == LogLevel.Critical
            && entry.Exception is InvalidOperationException));

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Critical
            && entry.Exception is InvalidOperationException
            && entry.Message.Contains("broken", StringComparison.Ordinal));
        healthyStopping.Task.IsCompleted.Should().BeFalse();

        await worker.StopAsync(default);
        await healthyStopping.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task StartAsync_LogsInvalidStreamErrorsAndStartsOnlyValidStreams()
    {
        var logger = new RecordingLogger<RelayWorker>();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DelegateFactory(stream => async token =>
        {
            started.TrySetResult(stream.Name);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var options = ValidatedOptions(["valid"], ["broken: InputUrl must use rtsp or rtsps."]);
        var worker = new RelayWorker(options, factory, logger);

        await worker.StartAsync(default);
        (await started.Task.WaitAsync(TestTimeout)).Should().Be("valid");
        await worker.StopAsync(default);

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains("broken", StringComparison.Ordinal));
    }

    private static RelayWorker CreateWorker(
        IStreamSupervisorFactory factory,
        string[] streamNames,
        ILogger<RelayWorker>? logger = null) =>
        new(
            ValidatedOptions(streamNames, []),
            factory,
            logger ?? new RecordingLogger<RelayWorker>());

    private static ValidatedRelayOptions ValidatedOptions(
        string[] streamNames,
        string[] errors) =>
        new(
            "/tools/ffmpeg",
            new RelayOptions(),
            streamNames.Select(name => new StreamOptions { Name = name }).ToArray(),
            errors);

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TestTimeout)
                throw new TimeoutException("Condition was not reached.");

            await Task.Yield();
        }
    }

    private sealed class DelegateFactory(
        Func<StreamOptions, Func<CancellationToken, Task>> create)
        : IStreamSupervisorFactory
    {
        public Func<CancellationToken, Task> Create(StreamOptions stream) => create(stream);
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
            _entries.Enqueue(new(logLevel, formatter(state, exception), exception));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
