using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Ffmpeg;
using Microsoft.Extensions.Logging;

namespace ActivityRecordingService.Recording;

public enum CameraEventRecorderState
{
    Idle,
    Recording,
    Completed,
    Failed
}

public sealed record CameraEventRecorderSnapshot(
    CameraEventRecorderState State,
    Guid? EventId,
    DateTimeOffset? StartedUtc,
    int NextPartIndex,
    int PendingSegmentCount,
    TimeSpan PendingDuration,
    long PendingBytes);

public sealed class CameraEventRecorder
{
    private readonly ValidatedCameraOptions _camera;
    private readonly ValidatedActivityRecordingOptions _service;
    private readonly IEventRepository _repository;
    private readonly RollingSegmentManager _segmentManager;
    private readonly EventPartWriter _partWriter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CameraEventRecorder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<RollingSegment> _pendingSegments = [];

    private Guid? _eventId;
    private Guid? _activeReservedFileId;
    private DateTimeOffset? _startedUtc;
    private string? _eventDirectory;
    private int _nextPartIndex;
    private TimeSpan _eventDuration;
    private TimeSpan _pendingDuration;
    private long _pendingBytes;
    private bool _hasPublishedPart;
    private CameraEventRecorderSnapshot _snapshot = CreateSnapshot(
        CameraEventRecorderState.Idle,
        null,
        null,
        0,
        0,
        TimeSpan.Zero,
        0);

    public CameraEventRecorder(
        ValidatedCameraOptions camera,
        ValidatedActivityRecordingOptions service,
        IEventRepository repository,
        RollingSegmentManager segmentManager,
        EventPartWriter partWriter,
        TimeProvider timeProvider,
        ILogger<CameraEventRecorder> logger)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _segmentManager = segmentManager ?? throw new ArgumentNullException(nameof(segmentManager));
        _partWriter = partWriter ?? throw new ArgumentNullException(nameof(partWriter));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CameraEventRecorderSnapshot Snapshot => _snapshot;

    public async Task BeginEventAsync(
        DateTimeOffset startedUtc,
        EventSegmentReservation reservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_snapshot.State is not (CameraEventRecorderState.Idle
                    or CameraEventRecorderState.Completed
                    or CameraEventRecorderState.Failed))
                throw new InvalidOperationException("The camera already has an active or finalized event aggregate.");

            await _repository.UpsertCameraAsync(
                _camera.Name,
                enabled: true,
                _camera.RetentionDays,
                cancellationToken);
            var eventId = await _repository.CreateEventAsync(
                _camera.Name,
                startedUtc,
                cancellationToken);

            _eventId = eventId;
            _activeReservedFileId = null;
            _startedUtc = startedUtc;
            _eventDirectory = null;
            _nextPartIndex = 0;
            _eventDuration = TimeSpan.Zero;
            _pendingDuration = TimeSpan.Zero;
            _pendingBytes = 0;
            _hasPublishedPart = false;
            _pendingSegments.Clear();
            _snapshot = CreateSnapshot(
                CameraEventRecorderState.Recording,
                eventId,
                startedUtc,
                _nextPartIndex,
                _pendingSegments.Count,
                _pendingDuration,
                _pendingBytes);
            foreach (var segment in reservation.CompletedSegments)
                AddSegmentToAggregate(segment);
            _logger.LogInformation(
                "Began event recording for camera {CameraName} event {EventId}.",
                _camera.Name,
                eventId);

            if (_pendingSegments.Count > 0 && ShouldFlushPart())
                await FlushPartAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (_eventId is null || _snapshot.State is not CameraEventRecorderState.Recording)
                throw;

            await FailActivePartAsync(exception, _nextPartIndex);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddClosedSegmentAsync(
        RollingSegment segment,
        CancellationToken cancellationToken)
    {
        await WaitForGateOrFailOnCancellationAsync(cancellationToken);

        try
        {
            EnsureRecording();
            AddSegmentToAggregate(segment);
            _snapshot = CreateSnapshot(
                CameraEventRecorderState.Recording,
                _eventId,
                _startedUtc,
                _nextPartIndex,
                _pendingSegments.Count,
                _pendingDuration,
                _pendingBytes);

            if (ShouldFlushPart())
                await FlushPartAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (!IsActiveAggregate)
                throw;

            await FailActivePartAsync(exception, _nextPartIndex);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(
        EventEndReason reason,
        CancellationToken cancellationToken)
    {
        await WaitForGateOrFailOnCancellationAsync(cancellationToken);

        try
        {
            EnsureRecording();

            if (_pendingSegments.Count > 0)
                await FlushPartAsync(cancellationToken);

            if (!_hasPublishedPart)
            {
                await FailActivePartAsync(
                    new InvalidOperationException(
                        $"Event {_eventId!.Value} ended with reason {reason} before a playable part was published."),
                    _nextPartIndex,
                    reason);
                return;
            }

            await _repository.CompleteEventAsync(
                _eventId!.Value,
                GetEventTimestamp(),
                EventStatus.Completed,
                cancellationToken,
                reason);
            _snapshot = CreateSnapshot(
                CameraEventRecorderState.Completed,
                _eventId,
                _startedUtc,
                _nextPartIndex,
                _pendingSegments.Count,
                _pendingDuration,
                _pendingBytes);
            _logger.LogInformation(
                "Completed event recording for camera {CameraName} event {EventId} reason {Reason}.",
                _camera.Name,
                _eventId,
                reason);
        }
        catch (Exception exception)
        {
            if (!IsActiveAggregate)
                throw;

            await FailActivePartAsync(exception, _nextPartIndex, reason);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FailAsync(
        string error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        await WaitForGateOrFailOnCancellationAsync(cancellationToken);

        try
        {
            if (_snapshot.State is not CameraEventRecorderState.Recording)
                return;

            await FailActivePartAsync(
                new InvalidOperationException(error),
                _nextPartIndex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ShouldFlushPart() =>
        _pendingDuration >= TimeSpan.FromMinutes(_service.Settings.Recording.EventPartDurationMinutes) ||
        _pendingBytes >= _service.Settings.Recording.MaxPartBytes ||
        _eventDuration >= TimeSpan.FromHours(_service.Settings.Recording.MaxEventDurationHours);

    private async Task WaitForGateOrFailOnCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (IsActiveAggregate)
                    await FailActivePartAsync(exception, _nextPartIndex);
            }
            finally
            {
                _gate.Release();
            }

            throw;
        }
    }

    private async Task FlushPartAsync(CancellationToken cancellationToken)
    {
        var eventId = _eventId!.Value;
        var partIndex = _nextPartIndex;
        var eventDirectory = _eventDirectory!;
        var request = new EventPartRequest(
            eventId,
            _camera.Name,
            partIndex,
            eventDirectory,
            _pendingSegments.ToArray());
        var finalPath = Path.Combine(eventDirectory, $"part-{partIndex:000}.mp4");
        var fileId = Guid.NewGuid();
        await _repository.AddFileAsync(
            new RecordingFileRecord(
                fileId,
                eventId,
                partIndex,
                CreateRelativePath(finalPath),
                Bytes: 0,
                RecordingFileStatus.Reserved),
            cancellationToken);
        _activeReservedFileId = fileId;
        var result = await _partWriter.WriteAsync(request, cancellationToken);
        if (!Path.GetFullPath(result.FinalPath).Equals(
                Path.GetFullPath(finalPath),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The part writer returned the wrong final path for event {eventId} part {partIndex}.");
        }

        await _repository.SetFileReadyAsync(
            fileId,
            result.Bytes,
            cancellationToken);
        _activeReservedFileId = null;
        _hasPublishedPart = true;
        _nextPartIndex = partIndex + 1;

        foreach (var segment in _pendingSegments)
        {
            if (File.Exists(segment.FilePath))
                File.Delete(segment.FilePath);
        }

        _pendingSegments.Clear();
        _pendingDuration = TimeSpan.Zero;
        _pendingBytes = 0;
        _snapshot = CreateSnapshot(
            _snapshot.State,
            _eventId,
            _startedUtc,
            _nextPartIndex,
            _pendingSegments.Count,
            _pendingDuration,
            _pendingBytes);
        _logger.LogInformation(
            "Wrote event part for camera {CameraName} event {EventId} part {PartIndex}.",
            _camera.Name,
            eventId,
            partIndex);
    }

    private async Task FailActivePartAsync(
        Exception exception,
        int partIndex,
        EventEndReason? endReason = null)
    {
        _snapshot = CreateSnapshot(
            CameraEventRecorderState.Failed,
            _eventId,
            _startedUtc,
            _nextPartIndex,
            _pendingSegments.Count,
            _pendingDuration,
            _pendingBytes);

        if (_activeReservedFileId is { } fileId)
        {
            var finalPath = _eventDirectory is null
                ? null
                : Path.Combine(_eventDirectory, $"part-{partIndex:000}.mp4");
            if (finalPath is null || !File.Exists(finalPath))
            {
                await _repository.SetFileStatusAsync(
                    fileId,
                    RecordingFileStatus.Failed,
                    CancellationToken.None);
            }
        }

        await _repository.CompleteEventAsync(
            _eventId!.Value,
            GetEventTimestamp(),
            EventStatus.Failed,
            CancellationToken.None,
            endReason);
        var diagnostics = exception is EventPartWriteException partException
            ? partException.Diagnostics
            : string.Empty;
        _logger.LogError(
            """
            Failed to write event part for camera {CameraName} event {EventId} part {PartIndex}
            with {ExceptionType}. Diagnostics: {Diagnostics}
            """,
            _camera.Name,
            _eventId,
            partIndex,
            exception.GetType().Name,
            string.IsNullOrWhiteSpace(diagnostics) ? "<none>" : diagnostics);
    }

    private void AddSegmentToAggregate(RollingSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Bytes < 0)
            throw new ArgumentException("Segment bytes must not be negative.", nameof(segment));
        if (segment.Duration <= TimeSpan.Zero)
            throw new ArgumentException("Segment duration must be positive.", nameof(segment));

        var segmentDirectory = Path.GetFullPath(Path.GetDirectoryName(segment.FilePath)!);
        if (_eventDirectory is null)
        {
            var recordingRoot = Path.GetFullPath(_service.Settings.RecordingRoot);
            var relativeDirectory = Path.GetRelativePath(recordingRoot, segmentDirectory);
            if (relativeDirectory.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativeDirectory))
            {
                throw new ArgumentException(
                    "Event segments must be inside the recording root.",
                    nameof(segment));
            }

            _eventDirectory = segmentDirectory;
        }
        else if (!segmentDirectory.Equals(
                     Path.GetFullPath(_eventDirectory),
                     StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "All event segments must use the same event directory.",
                nameof(segment));
        }

        _pendingSegments.Add(segment);
        _pendingDuration += segment.Duration;
        _eventDuration += segment.Duration;
        _pendingBytes += segment.Bytes;
    }

    private string CreateRelativePath(string finalPath)
    {
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(_service.Settings.RecordingRoot),
            Path.GetFullPath(finalPath));
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Event part is outside the recording root.");

        return relativePath.Replace('\\', '/');
    }

    private void EnsureRecording()
    {
        if (_snapshot.State == CameraEventRecorderState.Recording && _eventId is not null)
            return;

        throw new InvalidOperationException(
            _snapshot.State == CameraEventRecorderState.Failed
                ? "The failed event aggregate must be reset before recording another part."
                : "The camera does not have an active event.");
    }

    private DateTimeOffset GetEventTimestamp()
    {
        var nowUtc = _timeProvider.GetUtcNow();
        return nowUtc >= _startedUtc ? nowUtc : _startedUtc!.Value;
    }

    private bool IsActiveAggregate =>
        _snapshot.State == CameraEventRecorderState.Recording && _eventId is not null;

    private static CameraEventRecorderSnapshot CreateSnapshot(
        CameraEventRecorderState state,
        Guid? eventId,
        DateTimeOffset? startedUtc,
        int nextPartIndex,
        int pendingSegmentCount,
        TimeSpan pendingDuration,
        long pendingBytes) =>
        new(
            state,
            eventId,
            startedUtc,
            nextPartIndex,
            pendingSegmentCount,
            pendingDuration,
            pendingBytes);
}
