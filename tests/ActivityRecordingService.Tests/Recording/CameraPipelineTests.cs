using System.Collections.Concurrent;
using System.Threading.Channels;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Ffmpeg;
using ActivityRecordingService.Recording;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;

namespace ActivityRecordingService.Tests.Recording;

public sealed class CameraPipelineTests : IDisposable
{
    private static readonly DateTimeOffset StartedUtc =
        DateTimeOffset.Parse("2026-09-29T12:00:00.000Z");

    private readonly DirectoryInfo _rootDirectory =
        Directory.CreateTempSubdirectory("camera-pipeline-");
    private readonly PipelineTimeProvider _timeProvider = new(StartedUtc);
    private readonly List<SqliteEventRepository> _repositories = [];

    static CameraPipelineTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task TwoHitsStartRecordingAndThirtySecondsWithoutHitsCompletesIt()
    {
        var harness = await CreateHarnessAsync();
        harness.OutputRunner.Process = harness.OutputRunner.CreateProcess(
            FrameStream.FromHits(harness.Settings.Detection, hitCount: 2, noHitCount: 1),
            exitCode: 0);
        var thirdReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameStream = (FrameStream)harness.OutputRunner.Process.StandardOutput;
        frameStream.SecondReadGate = secondReadGate;
        frameStream.ReadGate = thirdReadGate;
        ((FrameStream)harness.OutputRunner.Process.StandardOutput).BeforeRead += frameIndex =>
        {
            if (frameIndex <= 2)
            {
                _timeProvider.Advance(TimeSpan.FromSeconds(frameIndex switch
                {
                    0 => 0,
                    1 => 1,
                    _ => 30
                }));
            }
        };
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);
        secondReadGate.TrySetResult();
        try
        {
            await harness.Recording.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Event did not start. Frames: {string.Join(",", harness.Model.ObservedMarkers)}. Logs: {string.Join("\\n", harness.Logger.Messages)}");
        }

        await harness.RecorderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
        harness.Recorder.Snapshot.EventId.Should().NotBeNull();
        var storedEvent = await harness.Repository.GetEventAsync(
            harness.Recorder.Snapshot.EventId!.Value,
            CancellationToken.None);
        storedEvent!.Status.Should().Be(EventStatus.Failed);
        storedEvent.EndedUtc.Should().BeOnOrAfter(StartedUtc.AddSeconds(30));
        storedEvent.EndedUtc.Should().BeBefore(StartedUtc.AddHours(2));

        cancellationSource.Cancel();
        await running;
    }

    [Fact]
    public async Task DetectionProcessFailureDoesNotImmediatelyKillRecording()
    {
        var harness = await CreateHarnessAsync();
        var process = harness.OutputRunner.CreateProcess(
            FrameStream.FromHits(harness.Settings.Detection, hitCount: 2, noHitCount: 0),
            exitCode: 1);
        var secondReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((FrameStream)process.StandardOutput).SecondReadGate = secondReadGate;
        harness.OutputRunner.Process = process;
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);
        secondReadGate.TrySetResult();

        await harness.Recording.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => pipeline.Snapshot.Detection == CameraProcessState.Retrying);

        pipeline.Snapshot.Recording.Should().Be(CameraProcessState.Running);
        pipeline.Snapshot.EventRecorder.State.Should().Be(CameraEventRecorderState.Recording);
        harness.ProcessRunner.Process!.KillCount.Should().Be(0);
        harness.Logger.Messages.Should().Contain(message =>
            message.Contains("PID 100", StringComparison.Ordinal) &&
            message.Contains("ExitCode 1", StringComparison.Ordinal) &&
            message.Contains("RetryCount 1", StringComparison.Ordinal) &&
            message.Contains("RunDurationSeconds", StringComparison.Ordinal));

        cancellationSource.Cancel();
        await running;
    }

    [Fact]
    public async Task RecordingProcessFailureCompletesActiveEventWithStreamStopped()
    {
        var harness = await CreateHarnessAsync();
        var frameStream = FrameStream.FromHits(harness.Settings.Detection, hitCount: 2, noHitCount: 0);
        var secondReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        frameStream.SecondReadGate = secondReadGate;
        harness.OutputRunner.Process = harness.OutputRunner.CreateProcess(
            frameStream,
            exitCode: 0);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);
        secondReadGate.TrySetResult();

        await harness.Recording.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.ProcessRunner.FailNext();
        _timeProvider.FireTimerDue(TimeSpan.FromMilliseconds(250));
        try
        {
            await WaitUntilAsync(() =>
                pipeline.Snapshot.Recording == CameraProcessState.Retrying &&
                harness.Recorder.Snapshot.State == CameraEventRecorderState.Failed);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Recording state: {pipeline.Snapshot.Recording}; recorder state: {harness.Recorder.Snapshot.State}; " +
                $"logs: {string.Join("\\n", harness.Logger.Messages)}");
        }

        pipeline.Snapshot.Recording.Should().Be(CameraProcessState.Retrying);
        harness.Recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
        await WaitUntilAsync(() => harness.Logger.Messages.Any(message =>
            message.Contains("PID 200", StringComparison.Ordinal) &&
            message.Contains("ExitCode 1", StringComparison.Ordinal) &&
            message.Contains("RetryCount 1", StringComparison.Ordinal) &&
            message.Contains("RunDurationSeconds", StringComparison.Ordinal)));
        harness.Logger.Messages.Should().Contain(message =>
            message.Contains("PID 200", StringComparison.Ordinal) &&
            message.Contains("ExitCode 1", StringComparison.Ordinal) &&
            message.Contains("RetryCount 1", StringComparison.Ordinal) &&
            message.Contains("RunDurationSeconds", StringComparison.Ordinal));
        var storedEvent = await harness.Repository.GetEventAsync(
            harness.Recorder.Snapshot.EventId!.Value,
            CancellationToken.None);
        storedEvent!.Status.Should().Be(EventStatus.Failed);
        storedEvent.EndReason.Should().Be(EventEndReason.StreamStopped);

        cancellationSource.Cancel();
        var finished = await Task.WhenAny(
            running,
            Task.Delay(TimeSpan.FromSeconds(2)));
        if (finished != running)
        {
            throw new TimeoutException(
                $"Pipeline shutdown did not finish. Detection kills: {harness.OutputRunner.Process!.KillCount}; " +
                $"recording kills: {harness.ProcessRunner.Process!.KillCount}; states: {pipeline.Snapshot}; " +
                $"recorder: {harness.Recorder.Snapshot.State}; logs: {string.Join("\\n", harness.Logger.Messages)}");
        }

        await running;
    }

    [Fact]
    public async Task CapacityRejectionPreventsEventRecorderBegin()
    {
        var harness = await CreateHarnessAsync();
        harness.Disk.AvailableBytes = 0;
        var frameStream = FrameStream.FromHits(harness.Settings.Detection, hitCount: 3, noHitCount: 0);
        frameStream.ModelEntered = harness.Model.Exited;
        frameStream.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Model.HoldInferenceAttempt = 2;
        harness.OutputRunner.Process = harness.OutputRunner.CreateProcess(
            frameStream,
            exitCode: 0);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);
        await harness.Model.TargetAttemptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Model.Release();
        frameStream.ReadGate!.TrySetResult();

        await harness.OutputRunner.Process!.StreamCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Repository.CreateEventCount.Should().Be(0);
        harness.Recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Idle);

        cancellationSource.Cancel();
        await running;
    }

    [Fact]
    public async Task NewerFramesReplaceOlderQueuedFramesWhileInferenceRuns()
    {
        var harness = await CreateHarnessAsync();
        var frameStream = FrameStream.FromValues(
            harness.Settings.Detection,
            0,
            0,
            255);
        harness.Model.HoldInferenceAttempt = 1;
        frameStream.ModelEntered = harness.Model.Entered;
        frameStream.BeforeRead += frameIndex =>
        {
            if (frameIndex == 1)
            {
                _timeProvider.Advance(TimeSpan.FromSeconds(20));
            }
            else if (frameIndex == 2)
            {
                _timeProvider.Advance(TimeSpan.FromSeconds(20));
            }
        };
        harness.OutputRunner.Process = harness.OutputRunner.CreateProcess(frameStream, exitCode: 0);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);

        await harness.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.OutputRunner.Process!.StreamCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Model.Release();
        await harness.Model.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => harness.Model.ObservedInputs.Count == 2);

        harness.Model.ObservedInputs.Should().Equal(0f, 1f);

        cancellationSource.Cancel();
        await running;
    }

    [Fact]
    public async Task RetryDelayFollowsFiveTenTwentyFortyThenSixtySeconds()
    {
        var harness = await CreateHarnessAsync();
        harness.OutputRunner.FailStartCount = 6;
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);

        foreach (var expectedDelay in new[]
                 {
                     TimeSpan.FromSeconds(5),
                     TimeSpan.FromSeconds(10),
                     TimeSpan.FromSeconds(20),
                     TimeSpan.FromSeconds(40),
                     TimeSpan.FromSeconds(60),
                     TimeSpan.FromSeconds(60)
                 })
        {
            await _timeProvider.WaitForTimerDueAsync(expectedDelay);
            _timeProvider.TimerShouldBePending(expectedDelay);
            _timeProvider.FireTimerDue(expectedDelay);
            await harness.OutputRunner.WaitForStartAttemptsAsync(
                expectedDelay == TimeSpan.FromSeconds(60) ? 6 : Array.IndexOf(
                    [5, 10, 20, 40, 60],
                    (int)expectedDelay.TotalSeconds) + 2);
        }

        cancellationSource.Cancel();
        await running;
    }

    [Fact]
    public async Task CancellationAlwaysKillsLiveDetectionAndRecordingProcessTrees()
    {
        var harness = await CreateHarnessAsync();
        harness.OutputRunner.Process = harness.OutputRunner.CreateProcess(
            FrameStream.FromHits(harness.Settings.Detection, hitCount: 100, noHitCount: 0),
            exitCode: 0);
        ((FrameStream)harness.OutputRunner.Process.StandardOutput).ModelEntered = harness.Model.Exited;
        harness.Model.HoldInferenceAttempt = 2;
        using var cancellationSource = new CancellationTokenSource();
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);
        await harness.Model.TargetAttemptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Model.Release();

        await harness.Recording.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        harness.OutputRunner.Process!.KillCount.Should().BeGreaterThanOrEqualTo(1);
        harness.ProcessRunner.Process!.KillCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task DetectionRestartRetainsActiveEventCooldown()
    {
        var harness = await CreateHarnessAsync();
        var firstStream = FrameStream.FromHits(
            harness.Settings.Detection,
            hitCount: 2,
            noHitCount: 0);
        harness.OutputRunner.EnqueueProcess(harness.OutputRunner.CreateProcess(firstStream, exitCode: 0));
        var secondStream = FrameStream.FromHits(
            harness.Settings.Detection,
            hitCount: 0,
            noHitCount: 1);
        secondStream.BeforeRead += _ => _timeProvider.Advance(TimeSpan.FromSeconds(30));
        harness.OutputRunner.EnqueueProcess(harness.OutputRunner.CreateProcess(secondStream, exitCode: 0));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);

        await harness.Recording.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _timeProvider.WaitForTimerDueAsync(TimeSpan.FromSeconds(5));
        _timeProvider.FireTimerDue(TimeSpan.FromSeconds(5));
        await harness.OutputRunner.WaitForStartAttemptsAsync(2);
        await harness.RecorderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Recorder.Snapshot.State.Should().Be(CameraEventRecorderState.Failed);
        harness.Repository.CreateEventCount.Should().Be(1);

        cancellationSource.Cancel();
        await running;
    }

    [Fact]
    public async Task RecordingStreamRestartContinuesRollingSequenceNumber()
    {
        var harness = await CreateHarnessAsync();
        CreateRollingSegment(5, StartedUtc.AddSeconds(-5));
        harness.OutputRunner.FailStartCount = int.MaxValue;
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);

        await WaitUntilAsync(() => harness.ProcessRunner.Commands.Count > 0);
        var arguments = harness.ProcessRunner.Commands[0].Arguments.ToArray();
        var startNumber = Array.IndexOf(arguments, "-segment_start_number");
        startNumber.Should().BeGreaterThanOrEqualTo(0);
        arguments[startNumber + 1].Should().Be("6");

        cancellationSource.Cancel();
        await running;
    }

    [Fact]
    public async Task FrameTimeoutWithLiveDetectionProcessCausesRetryAndProcessKill()
    {
        var harness = await CreateHarnessAsync();
        harness.Settings.InputTimeoutSeconds = 1;
        var frameStream = FrameStream.FromHits(
            harness.Settings.Detection,
            hitCount: 100,
            noHitCount: 0);
        frameStream.FirstReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OutputRunner.Process = harness.OutputRunner.CreateProcess(frameStream, exitCode: 0);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);

        await WaitUntilAsync(() => pipeline.Snapshot.Detection == CameraProcessState.Retrying);
        harness.OutputRunner.Process!.KillCount.Should().BeGreaterThanOrEqualTo(1);
        await _timeProvider.WaitForTimerDueAsync(TimeSpan.FromSeconds(5));
        _timeProvider.FireTimerDue(TimeSpan.FromSeconds(5));
        await harness.OutputRunner.WaitForStartAttemptsAsync(2);

        cancellationSource.Cancel();
        await running;
    }

    [Fact]
    public async Task IdleRollingCleanupRetainsConfiguredPreEventWindow()
    {
        var harness = await CreateHarnessAsync();
        harness.OutputRunner.FailStartCount = int.MaxValue;
        var old = CreateRollingSegment(0, StartedUtc.AddMinutes(-1));
        var boundary = CreateRollingSegment(1, StartedUtc.AddSeconds(-30));
        var withinWindow = CreateRollingSegment(2, StartedUtc.AddSeconds(-15));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeline = harness.CreatePipeline(cancellationSource);
        var running = pipeline.RunAsync(cancellationSource.Token);
        await _timeProvider.WaitForTimerDueAsync(TimeSpan.FromMilliseconds(250));
        _timeProvider.FireTimerDue(TimeSpan.FromMilliseconds(250));

        await WaitUntilAsync(() => !File.Exists(old));
        File.Exists(old).Should().BeFalse();
        File.Exists(boundary).Should().BeTrue();
        File.Exists(withinWindow).Should().BeTrue();

        var oldDuringInterval = CreateRollingSegment(3, StartedUtc.AddMinutes(-2));
        await _timeProvider.WaitForTimerDueAsync(TimeSpan.FromMilliseconds(250));
        _timeProvider.FireTimerDue(TimeSpan.FromMilliseconds(250));
        File.Exists(oldDuringInterval).Should().BeTrue();

        _timeProvider.Advance(TimeSpan.FromSeconds(harness.Settings.Recording.RollingSegmentSeconds));
        await _timeProvider.WaitForTimerDueAsync(TimeSpan.FromMilliseconds(250));
        _timeProvider.FireTimerDue(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => !File.Exists(oldDuringInterval));
        File.Exists(oldDuringInterval).Should().BeFalse();

        cancellationSource.Cancel();
        await running;
    }

    private async Task<PipelineHarness> CreateHarnessAsync()
    {
        var settings = new ActivityRecordingOptions
        {
            FfmpegPath = "/usr/bin/ffmpeg",
            RecordingRoot = _rootDirectory.FullName,
            InitialRestartDelaySeconds = 5,
            MaxRestartDelaySeconds = 60,
            StableRunSeconds = 60,
            InputTimeoutSeconds = 10,
            ShutdownTimeoutSeconds = 2
        };
        settings.Detection.MaxConcurrentInferences = 1;
        var camera = new ValidatedCameraOptions(
            "camera-a",
            "rtsp://user:secret@camera.example/sub",
            "rtsp://user:secret@camera.example/main",
            Roi: null,
            RetentionDays: null,
            Settings: new CameraOptions());
        var service = new ValidatedActivityRecordingOptions(
            settings.FfmpegPath,
            settings,
            [camera],
            Errors: []);
        var innerRepository = new SqliteEventRepository(
            Path.Combine(_rootDirectory.FullName, "events.db"),
            defaultRetentionDays: 7);
        _repositories.Add(innerRepository);
        var repository = new CountingEventRepository(innerRepository);
        await repository.InitializeAsync(CancellationToken.None);

        var rollingDirectory = Path.Combine(
            _rootDirectory.FullName,
            "rolling",
            "camera-a");
        var eventDirectory = Path.Combine(
            _rootDirectory.FullName,
            "events",
            "camera-a",
            "event-directory");
        Directory.CreateDirectory(rollingDirectory);
        Directory.CreateDirectory(eventDirectory);

        var recorderLogger = new ListLogger<CameraEventRecorder>();
        var recorder = new CameraEventRecorder(
            camera,
            service,
            repository,
            new RollingSegmentManager(_timeProvider, settings.Recording),
            new EventPartWriter(settings.FfmpegPath, new NoStartProcessRunner()),
            _timeProvider,
            recorderLogger);

        return new PipelineHarness(
            settings,
            service,
            repository,
            recorder,
            _timeProvider,
            rollingDirectory,
            eventDirectory);
    }

    public void Dispose()
    {
        foreach (var repository in _repositories)
            repository.Dispose();
        _rootDirectory.Delete(recursive: true);
    }

    private string CreateRollingSegment(int sequence, DateTimeOffset modifiedUtc)
    {
        var path = Path.Combine(
            _rootDirectory.FullName,
            "rolling",
            "camera-a",
            $"rolling_{sequence:D9}.mp4");
        File.WriteAllBytes(path, new byte[sequence + 1]);
        File.SetLastWriteTimeUtc(path, modifiedUtc.UtcDateTime);
        return path;
    }

    private sealed class PipelineHarness(
        ActivityRecordingOptions settings,
        ValidatedActivityRecordingOptions service,
        CountingEventRepository repository,
        CameraEventRecorder recorder,
        PipelineTimeProvider timeProvider,
        string rollingDirectory,
        string eventDirectory)
    {
        public ActivityRecordingOptions Settings { get; } = settings;

        public ValidatedActivityRecordingOptions Service { get; } = service;

        public CountingEventRepository Repository { get; } = repository;

        public CameraEventRecorder Recorder { get; } = recorder;

        public ListLogger<CameraPipeline> Logger { get; } = new();

        public FakeOutputProcessRunner OutputRunner { get; } = new();

        public FakeProcessRunner ProcessRunner { get; } = new();

        public FakeModelSession Model { get; } = new();

        public FakeDiskSpaceMonitor Disk { get; } = new()
        {
            AvailableBytes = 500L * 1024 * 1024 * 1024,
            TotalBytes = 1000L * 1024 * 1024 * 1024
        };

        public ProcessGate Recording { get; } = new();

        public TaskCompletionSource RecorderCompleted { get; } = CreateBoundSource();

        public CameraPipeline CreatePipeline(CancellationTokenSource cancellationSource) =>
            new(
                Service.Cameras[0],
                Service,
                OutputRunner,
                ProcessRunner,
                Model,
                Recorder,
                new RecordingCapacityGuard(
                    Repository,
                    Disk,
                    Service),
                timeProvider,
                new PipelineLogger(
                    Recording,
                    RecorderCompleted,
                    Logger));

        public string RollingDirectory => rollingDirectory;

        public string EventDirectory => eventDirectory;
    }

    private static TaskCompletionSource CreateBoundSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class PipelineLogger(
        ProcessGate recordingStarted,
        TaskCompletionSource completed,
        ListLogger<CameraPipeline> logger) : ILogger<CameraPipeline>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            logger.Record(message);
            if (message.Contains("Began event", StringComparison.Ordinal))
                recordingStarted.Started.TrySetResult();
            if (message.Contains("Completed event", StringComparison.Ordinal) ||
                message.Contains("Failed to write event part", StringComparison.Ordinal))
                completed.TrySetResult();
        }
    }

    private sealed class ProcessGate
    {
        public TaskCompletionSource Started { get; } = CreateBoundSource();
    }

    private sealed class FakeOutputProcessRunner : IOutputProcessRunner
    {
        private int _startAttempts;
        private readonly Queue<FakeOutputProcess> _queuedProcesses = new();

        public FakeOutputProcess? Process { get; set; }

        public void EnqueueProcess(FakeOutputProcess process) => _queuedProcesses.Enqueue(process);

        public int FailStartCount { get; set; }

        public Task<IOutputProcess> StartAsync(
            FfmpegCommand command,
            Action<string> onStandardError,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Increment(ref _startAttempts);
            if (attempt <= FailStartCount)
                throw new InvalidOperationException("detection process failed to start");

            if (_queuedProcesses.Count > 0)
            {
                Process = _queuedProcesses.Dequeue();
                return Task.FromResult<IOutputProcess>(Process);
            }

            Process ??= new FakeOutputProcess(new MemoryStream(), 0);
            return Task.FromResult<IOutputProcess>(Process);
        }

        public async Task WaitForStartAttemptsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref _startAttempts) < count)
            {
                if (timeout.IsCancellationRequested)
                    throw new TimeoutException($"Expected {count} starts, saw {Volatile.Read(ref _startAttempts)}.");
                await Task.Yield();
            }
        }

        public FakeOutputProcess CreateProcess(FrameStream stream, int exitCode) => new(stream, exitCode);
    }

    private sealed class FakeOutputProcess(Stream stream, int exitCode) : IOutputProcess
    {
        private readonly Stream _stream = stream;
        private readonly int _exitCode = exitCode;
        private readonly TaskCompletionSource _exit = CreateBoundSource();
        private readonly TaskCompletionSource _completed = CreateBoundSource();
        private int _killCount;

        public int Id => 100;

        public int KillCount => _killCount;

        public Stream StandardOutput => _stream;

        public TaskCompletionSource StreamCompleted =>
            _stream is FrameStream frameStream ? frameStream.Completed : _completed;

        public TaskCompletionSource Failed => _exit;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await StreamCompleted.Task.WaitAsync(cancellationToken);
            if (_exitCode != 0)
                _exit.TrySetResult();
            return _exitCode;
        }

        public async Task KillTreeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _killCount);
            if (_stream is FrameStream frameStream)
                frameStream.Cancel();
            else
                _completed.TrySetResult();
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource _failNow = CreateBoundSource();

        public List<FfmpegCommand> Commands { get; } = [];

        public FakeManagedProcess? Process { get; private set; }

        public Task<IManagedProcess> StartAsync(
            FfmpegCommand command,
            Action<string> onStandardError,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            Process = new FakeManagedProcess(_failNow.Task);
            return Task.FromResult<IManagedProcess>(Process);
        }

        public Task FailNext() => _failNow.TrySetResult() ? Task.CompletedTask : Task.CompletedTask;
    }

    private sealed class FakeManagedProcess(Task failTask) : IManagedProcess
    {
        private readonly TaskCompletionSource _exited = CreateBoundSource();

        public int Id => 200;

        private int _killCount;

        public int KillCount => _killCount;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            WaitForExitCoreAsync(cancellationToken);

        private async Task<int> WaitForExitCoreAsync(CancellationToken cancellationToken)
        {
            var completed = await Task.WhenAny(
                failTask,
                _exited.Task,
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            if (completed == failTask)
                return 1;

            await _exited.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public async Task KillTreeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _killCount);
            _exited.TrySetResult();
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeModelSession : IModelSession
    {
        private readonly TaskCompletionSource _release = CreateBoundSource();

        private int _attempt;

        public int? HoldInferenceAttempt { get; set; }

        public ConcurrentQueue<float> ObservedInputs { get; } = new();

        public ConcurrentQueue<float> ObservedMarkers { get; } = new();

        public TaskCompletionSource Entered { get; } = CreateBoundSource();

        public TaskCompletionSource Exited { get; } = CreateBoundSource();

        public TaskCompletionSource TargetAttemptEntered { get; } = CreateBoundSource();

        public RawInferenceResult Run(ReadOnlySpan<float> input)
        {
            _ = input;
            ObservedInputs.Enqueue(input.IsEmpty ? 0f : input[0]);
            ObservedMarkers.Enqueue(input.Length < 2 ? 0f : input[1]);
            var attempt = Interlocked.Increment(ref _attempt);
            Entered.TrySetResult();
            if (HoldInferenceAttempt == attempt)
            {
                TargetAttemptEntered.TrySetResult();
                _release.Task.Wait();
            }

            var confidence = input.IsEmpty ? 0f : input[0];
            var result = new RawInferenceResult(
                [0.5f, 0.5f, 0.1f, 0.1f, confidence],
                [1, 5, 1]);
            Exited.TrySetResult();
            return result;
        }

        public void Release() => _release.TrySetResult();

        public void Dispose()
        {
        }
    }

    private sealed class FakeDiskSpaceMonitor : IDiskSpaceMonitor
    {
        public long AvailableBytes { get; set; }

        public long TotalBytes { get; set; }

        public DiskSpaceInfo GetSpace(string path) => new(TotalBytes, AvailableBytes);
    }

    private sealed class CountingEventRepository(IEventRepository inner) : IEventRepository
    {
        public int CreateEventCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            inner.InitializeAsync(cancellationToken);

        public Task UpsertCameraAsync(
            string cameraName,
            bool enabled,
            int? retentionDays,
            CancellationToken cancellationToken) =>
            inner.UpsertCameraAsync(cameraName, enabled, retentionDays, cancellationToken);

        public async Task<Guid> CreateEventAsync(
            string cameraName,
            DateTimeOffset startedUtc,
            CancellationToken cancellationToken)
        {
            CreateEventCount++;
            return await inner.CreateEventAsync(cameraName, startedUtc, cancellationToken);
        }

        public Task CompleteEventAsync(
            Guid eventId,
            DateTimeOffset endedUtc,
            EventStatus status,
            CancellationToken cancellationToken,
            EventEndReason? endReason = null) =>
            inner.CompleteEventAsync(eventId, endedUtc, status, cancellationToken, endReason);

        public Task AddFileAsync(
            RecordingFileRecord file,
            CancellationToken cancellationToken) =>
            inner.AddFileAsync(file, cancellationToken);

        public Task SetFileStatusAsync(
            Guid fileId,
            RecordingFileStatus status,
            CancellationToken cancellationToken) =>
            inner.SetFileStatusAsync(fileId, status, cancellationToken);

        public Task SetFileReadyAsync(
            Guid fileId,
            long bytes,
            CancellationToken cancellationToken) =>
            inner.SetFileReadyAsync(fileId, bytes, cancellationToken);

        public Task<EventRecord?> GetEventAsync(Guid eventId, CancellationToken cancellationToken) =>
            inner.GetEventAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<RecordingFileRecord>> GetFilesAsync(
            Guid eventId,
            CancellationToken cancellationToken) =>
            inner.GetFilesAsync(eventId, cancellationToken);

        public Task<RecordingFileRecord?> GetReadyFileAsync(
            Guid fileId,
            CancellationToken cancellationToken) =>
            inner.GetReadyFileAsync(fileId, cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListEventsAsync(
            string? cameraName,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListEventsAsync(cameraName, fromUtc, toUtc, offset, limit, cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListEventsByStatusAsync(
            EventStatus status,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListEventsByStatusAsync(status, limit, cancellationToken);

        public Task<IReadOnlyList<FileEventItem>> ListFileEventsAsync(
            string? cameraName,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListFileEventsAsync(cameraName, offset, limit, cancellationToken);

        public Task<long> GetRecordingBytesAsync(CancellationToken cancellationToken) =>
            inner.GetRecordingBytesAsync(cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListDeletableEventsAsync(
            DateTimeOffset nowUtc,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListDeletableEventsAsync(nowUtc, limit, cancellationToken);

        public Task AddCleanupAuditAsync(
            string reason,
            int eventCount,
            long freedBytes,
            CancellationToken cancellationToken) =>
            inner.AddCleanupAuditAsync(reason, eventCount, freedBytes, cancellationToken);

        public Task DeleteEventAsync(Guid eventId, CancellationToken cancellationToken) =>
            inner.DeleteEventAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<RecordingFileRecord>> ListFilesByStatusAsync(
            RecordingFileStatus status,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListFilesByStatusAsync(status, limit, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class NoStartProcessRunner : IProcessRunner
    {
        public Task<IManagedProcess> StartAsync(
            FfmpegCommand command,
            Action<string> onStandardError,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Part encoding should not run in these pipeline tests.");
    }

    private sealed class FrameStream : Stream
    {
        private readonly byte[][] _frames;
        private readonly CancellationTokenSource _cancellation = new();
        private int _index;
        private int _offset;

        private FrameStream(DetectionOptions options, byte[][] frames)
        {
            _frames = frames;
        }

        public event Action<int>? BeforeRead;

        public TaskCompletionSource Completed { get; } = CreateBoundSource();

        public TaskCompletionSource? ModelEntered { get; set; }

        public TaskCompletionSource? ReadGate { get; set; }

        public TaskCompletionSource? SecondReadGate { get; set; }

        public TaskCompletionSource? FirstReadGate { get; set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get; set; }

        public static FrameStream FromHits(
            DetectionOptions options,
            int hitCount,
            int noHitCount)
        {
            var frames = Enumerable
                .Range(0, hitCount)
                .Select(index => CreateFrame(options, hit: true, index))
                .Concat(Enumerable
                    .Range(0, noHitCount)
                    .Select(index => CreateFrame(options, hit: false, hitCount + index)))
                .ToArray();
            return new FrameStream(options, frames);
        }

        public static FrameStream FromValues(
            DetectionOptions options,
            params byte[] values) => new(
            options,
            values.Select(value => Enumerable.Repeat(value, options.Width * options.Height * 3).ToArray()).ToArray());

        public void Cancel()
        {
            _cancellation.Cancel();
            FirstReadGate?.TrySetResult();
            ReadGate?.TrySetResult();
            SecondReadGate?.TrySetResult();
            Completed.TrySetResult();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadCore(buffer.AsSpan(offset, count));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadAsyncCoreAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        private async Task<int> ReadAsyncCoreAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            if (_offset == 0)
                BeforeRead?.Invoke(_index);

            if (_index == 0 && _offset == 0 && FirstReadGate is not null)
                await FirstReadGate.Task.WaitAsync(cancellationToken);
            if (_index == 1 && _offset == 0 && SecondReadGate is not null)
                await SecondReadGate.Task.WaitAsync(cancellationToken);
            if (_index == 2 && _offset == 0 && ReadGate is not null)
                await ReadGate.Task.WaitAsync(cancellationToken);

            return ReadCore(destination.Span);
        }

        private int ReadCore(Span<byte> destination)
        {
            if (_offset == 0)
                BeforeRead?.Invoke(_index);

            if (_index >= _frames.Length)
            {
                Completed.TrySetResult();
                return 0;
            }

            var frame = _frames[_index];
            var copied = Math.Min(destination.Length, frame.Length - _offset);
            frame.AsSpan(_offset, copied).CopyTo(destination);
            _offset += copied;
            if (_offset == frame.Length)
            {
                _index++;
                _offset = 0;
            }

            return copied;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static byte[] CreateFrame(DetectionOptions options, bool hit, int marker)
        {
            var frame = Enumerable.Repeat((byte)(hit ? 255 : 0), options.Width * options.Height * 3).ToArray();
            frame[1] = (byte)Math.Clamp(marker, 0, 255);
            return frame;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public void Record(string message)
        {
            lock (_gate)
            {
                _messages.Add(message);
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Record(formatter(state, exception));
    }

    private sealed class PipelineTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimeTimer> _timers = [];
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan amount)
        {
            lock (_gate)
            {
                _now += amount;
            }

        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ManualTimeTimer timer;
            lock (_gate)
            {
                timer = new ManualTimeTimer(callback, state, dueTime);
                _timers.Add(timer);
            }

            return timer;
        }

        public void TimerShouldBePending(TimeSpan dueTime)
        {
            lock (_gate)
            {
                _timers.Should().Contain(timer => timer.DueTime == dueTime);
            }
        }

        public void FireTimerDue(TimeSpan dueTime)
        {
            ManualTimeTimer timer;
            lock (_gate)
            {
                timer = _timers.First(item => item.DueTime == dueTime);
                _timers.Remove(timer);
            }

            timer.Fire();
        }

        public async Task WaitForTimerDueAsync(TimeSpan dueTime)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!HasTimerDue(dueTime))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }

        private bool HasTimerDue(TimeSpan dueTime)
        {
            lock (_gate)
            {
                return _timers.Any(timer => timer.DueTime == dueTime);
            }
        }
    }

    private sealed class ManualTimeTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
