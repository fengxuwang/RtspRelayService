using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Ffmpeg;
using Microsoft.Extensions.Logging;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;
using RtspRelayService.Supervision;

namespace ActivityRecordingService.Recording;

public enum CameraProcessState
{
    Stopped,
    Starting,
    Running,
    Retrying
}

public sealed record CameraPipelineState(
    CameraProcessState Detection,
    CameraProcessState Recording,
    CameraEventRecorderSnapshot EventRecorder);

public sealed class CameraPipeline
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> InferenceGates = new();
    private static readonly Regex RollingSegmentNamePattern =
        new(@"^rolling_\d{9}\.mp4$", RegexOptions.Compiled);

    private readonly ValidatedCameraOptions _camera;
    private readonly ValidatedActivityRecordingOptions _service;
    private readonly IOutputProcessRunner _outputRunner;
    private readonly IProcessRunner _processRunner;
    private readonly IModelSession _modelSession;
    private readonly CameraEventRecorder _eventRecorder;
    private readonly RecordingCapacityGuard _capacityGuard;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CameraPipeline> _logger;
    private readonly RollingSegmentManager _segmentManager;
    private readonly SemaphoreSlim _inferenceGate;
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly EventStateMachine _stateMachine;
    private readonly object _snapshotGate = new();
    private readonly TimeSpan _rollingCleanupInterval;
    private readonly string _rollingDirectory;
    private readonly string _eventsDirectory;
    private readonly string[] _configuredUrls;
    private volatile CameraPipelineState _snapshot;
    private DateTimeOffset _nextRollingCleanupUtc = DateTimeOffset.MinValue;

    private string? _activeEventDirectory;
    private string? _activeSegmentPath;

    public CameraPipeline(
        ValidatedCameraOptions camera,
        ValidatedActivityRecordingOptions service,
        IOutputProcessRunner outputRunner,
        IProcessRunner processRunner,
        IModelSession modelSession,
        CameraEventRecorder eventRecorder,
        RecordingCapacityGuard capacityGuard,
        TimeProvider timeProvider,
        ILogger<CameraPipeline> logger)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _outputRunner = outputRunner ?? throw new ArgumentNullException(nameof(outputRunner));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _modelSession = modelSession ?? throw new ArgumentNullException(nameof(modelSession));
        _eventRecorder = eventRecorder ?? throw new ArgumentNullException(nameof(eventRecorder));
        _capacityGuard = capacityGuard ?? throw new ArgumentNullException(nameof(capacityGuard));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var settings = service.Settings;
        _segmentManager = new RollingSegmentManager(timeProvider, settings.Recording);
        _inferenceGate = InferenceGates.GetOrAdd(
            settings.Detection.MaxConcurrentInferences,
            static capacity => new SemaphoreSlim(capacity, capacity));
        _stateMachine = new EventStateMachine(new EventStateMachineOptions
        {
            TriggerHitCount = settings.Detection.TriggerHitCount,
            TriggerWindowSeconds = settings.Detection.TriggerWindowSeconds,
            EventCooldownSeconds = settings.Recording.EventCooldownSeconds,
            MaxEventDurationHours = settings.Recording.MaxEventDurationHours
        });
        _rollingCleanupInterval = TimeSpan.FromSeconds(Math.Max(
            1,
            settings.Recording.RollingSegmentSeconds));
        var safeCameraName = CreateSafeFileName(camera.Name);
        var recordingRoot = Path.GetFullPath(settings.RecordingRoot);
        _rollingDirectory = Path.Combine(recordingRoot, "rolling", safeCameraName);
        _eventsDirectory = Path.Combine(recordingRoot, "events", safeCameraName);
        _configuredUrls = [camera.DetectionUrl, camera.RecordUrl];
        _snapshot = new CameraPipelineState(
            CameraProcessState.Stopped,
            CameraProcessState.Stopped,
            eventRecorder.Snapshot);
    }

    public CameraPipelineState Snapshot => _snapshot;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_rollingDirectory);
        Directory.CreateDirectory(_eventsDirectory);

        var detectionLoop = RunDetectionLoopAsync(stoppingToken);
        var segmenterLoop = RunSegmenterLoopAsync(stoppingToken);
        try
        {
            await Task.WhenAll(detectionLoop, segmenterLoop);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancellation is the normal shutdown path; process cleanup still runs below.
        }
        finally
        {
            await CompleteEventAtShutdownAsync();
            _eventGate.Dispose();
        }
    }

    private async Task RunDetectionLoopAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = CreateRetryPolicy();
        var failureCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            SetState(CameraProcessState.Starting, null);
            IOutputProcess? process = null;
            var startedUtc = _timeProvider.GetUtcNow();
            int? processId = null;
            int? exitCode = null;
            try
            {
                var command = DetectionCommandBuilder.Build(
                    _service.FfmpegPath,
                    _camera,
                    _service.Settings.Detection,
                    TimeSpan.FromSeconds(_service.Settings.InputTimeoutSeconds));
                process = await _outputRunner.StartAsync(
                    command,
                    line => OnStandardError(line),
                    stoppingToken);
                processId = process.Id;
                SetState(CameraProcessState.Running, null);
                exitCode = await RunDetectionProcessAsync(process, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    throw new OperationCanceledException(stoppingToken);

                throw new InvalidOperationException(
                    $"The detection process exited with code {exitCode}.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetState(CameraProcessState.Retrying, null);
                var runDuration = _timeProvider.GetUtcNow() - startedUtc;
                failureCount = retryPolicy.NextFailureCount(failureCount, runDuration);
                _logger.LogWarning(
                    "Detection process lifecycle for camera {CameraName}: PID {PID}, ExitCode {ExitCode}, RetryCount {RetryCount}, RunDurationSeconds {RunDurationSeconds}, DelaySeconds {DelaySeconds}, ExceptionType {ExceptionType}, Error {Error}.",
                    _camera.Name,
                    processId,
                    exitCode,
                    failureCount,
                    runDuration.TotalSeconds,
                    retryPolicy.GetDelay(failureCount).TotalSeconds,
                    exception.GetType().Name,
                    UrlRedactor.RedactText(exception.Message, _configuredUrls));
                await KillAndDisposeOutputAsync(process, stoppingToken);
                process = null;
                await DelayAsync(
                    retryPolicy.GetDelay(failureCount),
                    _timeProvider,
                    stoppingToken);
                await AdvanceDetectionDowntimeAsync(stoppingToken);
            }
            finally
            {
                await KillAndDisposeOutputAsync(process, stoppingToken);
            }
        }
    }

    private async Task<int> RunDetectionProcessAsync(
        IOutputProcess process,
        CancellationToken cancellationToken)
    {
        var frames = Channel.CreateBounded<ImageFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        var inference = ProcessFramesAsync(frames.Reader, cancellationToken);
        var reading = ReadFramesAsync(process, frames.Writer, cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        var firstCompleted = await Task.WhenAny(reading, inference, exitTask);
        var inputTimeout = TimeSpan.FromSeconds(_service.Settings.InputTimeoutSeconds);

        if (firstCompleted == exitTask)
        {
            var exitCode = await exitTask;
            await WaitForDetectionCompanionAsync(reading, inputTimeout, cancellationToken);
            await WaitForDetectionCompanionAsync(inference, inputTimeout, cancellationToken);
            return exitCode;
        }

        if (firstCompleted == reading)
        {
            await reading;
            await WaitForDetectionCompanionAsync(exitTask, inputTimeout, cancellationToken);
            await WaitForDetectionCompanionAsync(inference, inputTimeout, cancellationToken);
            return await exitTask;
        }

        await inference;
        await WaitForDetectionCompanionAsync(reading, inputTimeout, cancellationToken);
        await WaitForDetectionCompanionAsync(exitTask, inputTimeout, cancellationToken);
        return await exitTask;
    }

    private async Task WaitForDetectionCompanionAsync(
        Task companion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (companion.IsCompleted)
        {
            await companion;
            return;
        }

        var completed = await Task.WhenAny(
            companion,
            DelayAsync(timeout, TimeProvider.System, cancellationToken));
        if (completed != companion)
            throw new TimeoutException(
                "The detection process did not stop after its input stopped.");

        await companion;
    }

    private async Task ReadFramesAsync(
        IOutputProcess process,
        ChannelWriter<ImageFrame> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var inputTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                inputTimeout.CancelAfter(TimeSpan.FromSeconds(_service.Settings.InputTimeoutSeconds));
                ImageFrame frame;
                try
                {
                    frame = await BgrFrameReader.ReadFrameAsync(
                        process.StandardOutput,
                        _service.Settings.Detection.Width,
                        _service.Settings.Detection.Height,
                        _timeProvider.GetUtcNow(),
                        inputTimeout.Token);
                    frame = frame with { TimestampUtc = _timeProvider.GetUtcNow() };
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested &&
                    inputTimeout.IsCancellationRequested)
                {
                    throw new TimeoutException("The detection input produced no frame before the input timeout.");
                }

                await writer.WriteAsync(frame, cancellationToken);
            }
        }
        catch (EndOfStreamException)
        {
            // Clean stdout EOF is evaluated by the supervisor before the process is restarted.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ProcessFramesAsync(
        ChannelReader<ImageFrame> frames,
        CancellationToken cancellationToken)
    {
        var detector = new PersonDetector(
            _modelSession,
            _service.Settings.Detection,
            _camera.Roi);

        while (await frames.WaitToReadAsync(cancellationToken))
        {
            while (frames.TryRead(out var frame))
            {
                await _inferenceGate.WaitAsync(cancellationToken);
                PersonFrameResult result;
                try
                {
                    result = await Task.Run(() => detector.Detect(frame), cancellationToken);
                }
                finally
                {
                    _inferenceGate.Release();
                }

                var transition = _stateMachine.Process(new DetectionSample(
                    result.TimestampUtc,
                    result.People.Count > 0,
                    result.People.Select(person => person.Confidence).FirstOrDefault()));
                if (transition is not null)
                    await ApplyTransitionAsync(transition, cancellationToken);
            }

            var idleTransition = _stateMachine.Advance(_timeProvider.GetUtcNow());
            if (idleTransition is not null)
                await ApplyTransitionAsync(idleTransition, cancellationToken);
        }
    }

    private async Task ApplyTransitionAsync(
        EventTransition transition,
        CancellationToken cancellationToken)
    {
        if (transition.Kind == EventTransitionKind.Complete)
        {
            await CompleteEventAsync(transition.Reason, cancellationToken);
            return;
        }

        if (transition.Kind == EventTransitionKind.CompleteAndStart)
            await CompleteEventAsync(transition.Reason, cancellationToken);

        if (!await StartEventAsync(transition.TimestampUtc, cancellationToken))
            _stateMachine.Reset();
    }

    private async Task AdvanceDetectionDowntimeAsync(CancellationToken cancellationToken)
    {
        var transition = _stateMachine.Advance(_timeProvider.GetUtcNow());
        if (transition is not null)
            await ApplyTransitionAsync(transition, cancellationToken);
    }

    private async Task<bool> StartEventAsync(
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        await _eventGate.WaitAsync(cancellationToken);
        try
        {
            var decision = await _capacityGuard.CanStartEventAsync(
                _camera.Name,
                cancellationToken);
            if (!decision.Allowed)
            {
                _logger.LogWarning(
                    "New event recording for camera {CameraName} was rejected by capacity control: {Reason}",
                    _camera.Name,
                    decision.BlockingReason ?? "capacity unavailable");
                return false;
            }

            var eventDirectory = Path.Combine(
                _eventsDirectory,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(eventDirectory);
            var reservation = _segmentManager.BeginEvent(
                _rollingDirectory,
                eventDirectory,
                startedUtc);
            await _eventRecorder.BeginEventAsync(
                startedUtc,
                reservation,
                cancellationToken);
            _activeEventDirectory = eventDirectory;
            _activeSegmentPath = reservation.ActiveSegmentPath;
            _logger.LogInformation(
                "Began event recording for camera {CameraName} event {EventId}.",
                _camera.Name,
                _eventRecorder.Snapshot.EventId);
            return true;
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private async Task CompleteEventAsync(
        EventEndReason reason,
        CancellationToken cancellationToken)
    {
        await _eventGate.WaitAsync(cancellationToken);
        var eventDirectory = _activeEventDirectory;
        _activeEventDirectory = null;
        _activeSegmentPath = null;
        try
        {
            if (eventDirectory is null)
                return;

            await _eventRecorder.CompleteAsync(reason, cancellationToken);
            _logger.LogInformation(
                "Completed event recording for camera {CameraName} event {EventId} reason {Reason}.",
                _camera.Name,
                _eventRecorder.Snapshot.EventId,
                reason);
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private async Task RunSegmenterLoopAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = CreateRetryPolicy();
        var failureCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            SetState(null, CameraProcessState.Starting);
            IManagedProcess? process = null;
            var startedUtc = _timeProvider.GetUtcNow();
            int? processId = null;
            int? exitCode = null;
            try
            {
                var command = SegmenterCommandBuilder.Build(
                    _service.FfmpegPath,
                    _camera,
                    _service.Settings.Recording,
                    _rollingDirectory,
                    TimeSpan.FromSeconds(_service.Settings.InputTimeoutSeconds),
                    GetNextRollingSequenceNumber(_rollingDirectory));
                process = await _processRunner.StartAsync(
                    command,
                    OnStandardError,
                    stoppingToken);
                processId = process.Id;
                SetState(null, CameraProcessState.Running);
                var exitTask = process.WaitForExitAsync(stoppingToken);
                while (!exitTask.IsCompleted && !stoppingToken.IsCancellationRequested)
                {
                    await DelayAsync(TimeSpan.FromMilliseconds(250), _timeProvider, stoppingToken);
                    await PollClosedSegmentAsync(stoppingToken);
                }

                exitCode = await exitTask;
                throw new InvalidOperationException(
                    $"The recording process exited with code {exitCode}.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetState(null, CameraProcessState.Retrying);
                var runDuration = _timeProvider.GetUtcNow() - startedUtc;
                failureCount = retryPolicy.NextFailureCount(failureCount, runDuration);
                _logger.LogWarning(
                    "Recording process lifecycle for camera {CameraName}: PID {PID}, ExitCode {ExitCode}, RetryCount {RetryCount}, RunDurationSeconds {RunDurationSeconds}, DelaySeconds {DelaySeconds}, ExceptionType {ExceptionType}, Error {Error}.",
                    _camera.Name,
                    processId,
                    exitCode,
                    failureCount,
                    runDuration.TotalSeconds,
                    retryPolicy.GetDelay(failureCount).TotalSeconds,
                    exception.GetType().Name,
                    UrlRedactor.RedactText(exception.Message, _configuredUrls));
                await CompleteRecordingStreamStoppedAsync();
                await DelayAsync(
                    retryPolicy.GetDelay(failureCount),
                    _timeProvider,
                    stoppingToken);
            }
            finally
            {
                await KillAndDisposeManagedAsync(process, stoppingToken);
            }
        }
    }

    private async Task PollClosedSegmentAsync(CancellationToken cancellationToken)
    {
        await _eventGate.WaitAsync(cancellationToken);
            var eventDirectory = _activeEventDirectory;
            try
            {
                if (eventDirectory is not null)
                {
                    var reservation = _segmentManager.ReserveClosedSegments(
                        _rollingDirectory,
                        eventDirectory);
                    _activeSegmentPath = reservation.ActiveSegmentPath;
                    foreach (var segment in reservation.CompletedSegments)
                    {
                        await _eventRecorder.AddClosedSegmentAsync(segment, cancellationToken);
                    }
                }
            else
            {
                var now = _timeProvider.GetUtcNow();
                if (now < _nextRollingCleanupUtc)
                    return;

                _nextRollingCleanupUtc = now + _rollingCleanupInterval;
                _segmentManager.DeleteUnreservedSegments(_rollingDirectory, now);
            }
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private async Task CompleteRecordingStreamStoppedAsync()
    {
        try
        {
            await CompleteEventAsync(EventEndReason.StreamStopped, CancellationToken.None);
            _logger.LogInformation(
                "The recording stream stopped event recording for camera {CameraName} with reason {Reason}.",
                _camera.Name,
                EventEndReason.StreamStopped);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                "Could not complete the active event after the recording stream stopped for camera {CameraName} with {ExceptionType}.",
                _camera.Name,
                exception.GetType().Name);
        }
    }

    private async Task CompleteEventAtShutdownAsync()
    {
        await _eventGate.WaitAsync(CancellationToken.None);
        var hasActiveEvent = _activeEventDirectory is not null;
        _activeEventDirectory = null;
        _activeSegmentPath = null;
        _eventGate.Release();
        if (!hasActiveEvent)
            return;

        try
        {
            await _eventRecorder.CompleteAsync(
                EventEndReason.ServiceStopping,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                "Could not complete the active event during shutdown for camera {CameraName} with {ExceptionType}.",
                _camera.Name,
                exception.GetType().Name);
        }
    }

    private RetryPolicy CreateRetryPolicy() => new(
        TimeSpan.FromSeconds(_service.Settings.InitialRestartDelaySeconds),
        TimeSpan.FromSeconds(_service.Settings.MaxRestartDelaySeconds),
        TimeSpan.FromSeconds(_service.Settings.StableRunSeconds));

    private async Task KillAndDisposeOutputAsync(
        IOutputProcess? process,
        CancellationToken cancellationToken)
    {
        if (process is null)
            return;

        try
        {
            using var shutdownTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(_service.Settings.ShutdownTimeoutSeconds));
            await process.KillTreeAsync(shutdownTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Detection process tree shutdown timed out for camera {CameraName}.",
                _camera.Name);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Detection process tree shutdown failed for camera {CameraName} with {ExceptionType}.",
                _camera.Name,
                exception.GetType().Name);
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    private async Task KillAndDisposeManagedAsync(
        IManagedProcess? process,
        CancellationToken cancellationToken)
    {
        if (process is null)
            return;

        try
        {
            using var shutdownTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(_service.Settings.ShutdownTimeoutSeconds));
            await process.KillTreeAsync(shutdownTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Recording process tree shutdown timed out for camera {CameraName}.",
                _camera.Name);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Recording process tree shutdown failed for camera {CameraName} with {ExceptionType}.",
                _camera.Name,
                exception.GetType().Name);
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    private void OnStandardError(string line)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogWarning(
                "FFmpeg diagnostics for camera {CameraName}: {Diagnostics}",
                _camera.Name,
                UrlRedactor.RedactText(line, _configuredUrls));
        }
    }

    private void SetState(CameraProcessState? detection, CameraProcessState? recording)
    {
        lock (_snapshotGate)
        {
            var current = _snapshot;
            _snapshot = new CameraPipelineState(
                detection ?? current.Detection,
                recording ?? current.Recording,
                _eventRecorder.Snapshot);
        }
    }

    private static async Task DelayAsync(
        TimeSpan delay,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = timeProvider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            delay,
            Timeout.InfiniteTimeSpan);
        await completion.Task.WaitAsync(cancellationToken);
    }

    private static string CreateSafeFileName(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '-')
                result.Append(character);
            else
                result.Append('_');
        }

        return result.Length == 0 ? "camera" : result.ToString();
    }

    private static string? FindActiveSegmentPath(string rollingDirectory)
    {
        if (!Directory.Exists(rollingDirectory))
            return null;

        return Directory.EnumerateFiles(rollingDirectory, "rolling_*.mp4")
            .Where(path => RollingSegmentNamePattern.IsMatch(Path.GetFileName(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .LastOrDefault();
    }

    private static int GetNextRollingSequenceNumber(string rollingDirectory)
    {
        if (!Directory.Exists(rollingDirectory))
            return 0;

        var highest = Directory.EnumerateFiles(rollingDirectory, "rolling_*.mp4")
            .Select(path => int.TryParse(
                Path.GetFileName(path)["rolling_".Length..].Split('.')[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence)
                ? sequence
                : -1)
            .DefaultIfEmpty(-1)
            .Max();
        return highest >= int.MaxValue
            ? throw new InvalidOperationException(
                "The rolling segment sequence exhausted the FFmpeg nine-digit filename range.")
            : highest + 1;
    }
}
