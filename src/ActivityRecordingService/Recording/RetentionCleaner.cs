using ActivityRecordingService.Configuration;
using Microsoft.Extensions.Logging;

namespace ActivityRecordingService.Recording;

public sealed record CapacityDecision(bool Allowed, string? BlockingReason);

public sealed record CleanupResult(
    int DeletedEvents,
    long FreedBytes,
    bool NewEventsAllowed,
    string? BlockingReason);

public sealed class RecordingCapacityGuard
{
    public const string QuotaBlockingReason = "Recording storage quota reached.";
    public const string MinimumFreeSpaceBlockingReason = "Minimum free disk space reached.";

    private readonly IEventRepository _repository;
    private readonly IDiskSpaceMonitor _diskSpaceMonitor;
    private readonly ValidatedActivityRecordingOptions _options;

    public RecordingCapacityGuard(
        IEventRepository repository,
        IDiskSpaceMonitor diskSpaceMonitor,
        ValidatedActivityRecordingOptions options)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _diskSpaceMonitor = diskSpaceMonitor ?? throw new ArgumentNullException(nameof(diskSpaceMonitor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<CapacityDecision> CanStartEventAsync(
        string cameraName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cameraName))
            throw new ArgumentException("Camera name is required.", nameof(cameraName));

        cancellationToken.ThrowIfCancellationRequested();
        var recordingBytes = await _repository.GetRecordingBytesAsync(cancellationToken);
        if (recordingBytes >= _options.Settings.Recording.MaxRecordingBytes)
            return new CapacityDecision(false, QuotaBlockingReason);

        var space = _diskSpaceMonitor.GetSpace(_options.Settings.RecordingRoot);
        if (space.AvailableBytes <= _options.Settings.Recording.MinFreeBytes)
            return new CapacityDecision(false, MinimumFreeSpaceBlockingReason);

        return new CapacityDecision(true, null);
    }
}

public sealed class RetentionCleaner
{
    private const int BatchSize = 100;
    private const int AllEventsLimit = int.MaxValue;

    private readonly IEventRepository _repository;
    private readonly IDiskSpaceMonitor _diskSpaceMonitor;
    private readonly ValidatedActivityRecordingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionCleaner> _logger;
    private readonly string _recordingRoot;

    public RetentionCleaner(
        IEventRepository repository,
        IDiskSpaceMonitor diskSpaceMonitor,
        ValidatedActivityRecordingOptions options,
        TimeProvider timeProvider,
        ILogger<RetentionCleaner> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _diskSpaceMonitor = diskSpaceMonitor ?? throw new ArgumentNullException(nameof(diskSpaceMonitor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recordingRoot = Path.GetFullPath(_options.Settings.RecordingRoot);
    }

    public async Task<CleanupResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var metadataDeletedEvents = 0;
        var freedBytes = 0L;
        var reasons = new List<string>();

        var ageEligibleEvents = await _repository.ListDeletableEventsAsync(
            _timeProvider.GetUtcNow(),
            AllEventsLimit,
            cancellationToken);
        if (ageEligibleEvents.Count > 0)
        {
            reasons.Add("retention");
            foreach (var eventRecord in ageEligibleEvents)
            {
                var deletion = await TryDeleteEventAsync(eventRecord, cancellationToken);
                metadataDeletedEvents += deletion.MetadataDeleted ? 1 : 0;
                freedBytes += deletion.FreedBytes;
                _ = GetRecordingSpace();
            }
        }

        var recordingBytes = await _repository.GetRecordingBytesAsync(cancellationToken);
        var space = GetRecordingSpace();
        var quotaReached = recordingBytes >= _options.Settings.Recording.MaxRecordingBytes;
        var minimumFreeSpaceReached = space.AvailableBytes <= _options.Settings.Recording.MinFreeBytes;
        if (quotaReached)
            reasons.Add("quota");
        if (minimumFreeSpaceReached)
            reasons.Add("min-free-space");

        if (quotaReached || minimumFreeSpaceReached)
        {
            var capacityEligibleEvents = await ListOldestCapacityEligibleEventsAsync(cancellationToken);
            foreach (var eventRecord in capacityEligibleEvents)
            {
                if (!quotaReached && !minimumFreeSpaceReached)
                    break;

                cancellationToken.ThrowIfCancellationRequested();
                var deletion = await TryDeleteEventAsync(eventRecord, cancellationToken);
                metadataDeletedEvents += deletion.MetadataDeleted ? 1 : 0;
                freedBytes += deletion.FreedBytes;

                recordingBytes = await _repository.GetRecordingBytesAsync(cancellationToken);
                space = GetRecordingSpace();
                quotaReached = recordingBytes >= _options.Settings.Recording.MaxRecordingBytes;
                minimumFreeSpaceReached = space.AvailableBytes <= _options.Settings.Recording.MinFreeBytes;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        recordingBytes = await _repository.GetRecordingBytesAsync(cancellationToken);
        space = GetRecordingSpace();
        var decision = EvaluateCapacity(recordingBytes, space);

        if (reasons.Count > 0)
        {
            await _repository.AddCleanupAuditAsync(
                string.Join("+", reasons.Distinct()),
                metadataDeletedEvents,
                freedBytes,
                cancellationToken);
        }

        return new CleanupResult(
            metadataDeletedEvents,
            freedBytes,
            decision.Allowed,
            decision.BlockingReason);
    }

    private CapacityDecision EvaluateCapacity(long recordingBytes, DiskSpaceInfo space)
    {
        if (recordingBytes >= _options.Settings.Recording.MaxRecordingBytes)
            return new CapacityDecision(false, RecordingCapacityGuard.QuotaBlockingReason);
        if (space.AvailableBytes <= _options.Settings.Recording.MinFreeBytes)
            return new CapacityDecision(false, RecordingCapacityGuard.MinimumFreeSpaceBlockingReason);
        return new CapacityDecision(true, null);
    }

    private async Task<(bool MetadataDeleted, long FreedBytes)> TryDeleteEventAsync(
        EventRecord eventRecord,
        CancellationToken cancellationToken)
    {
        var freedBytes = 0L;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = await _repository.GetFilesAsync(eventRecord.EventId, cancellationToken);
            var deleteTargets = new List<(string Path, long Bytes)>(files.Count);
            foreach (var file in files)
            {
                var fullPath = ResolveConfinedPath(file.RelativePath);
                if (Directory.Exists(fullPath))
                    throw new InvalidDataException("A recording file path refers to a directory.");

                deleteTargets.Add((fullPath, File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0));
            }

            foreach (var target in deleteTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(target.Path))
                    continue;

                File.Delete(target.Path);
                freedBytes += target.Bytes;
            }

            await _repository.DeleteEventAsync(eventRecord.EventId, cancellationToken);
            return (true, freedBytes);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                "Recording cleanup failed for camera {CameraName} event {EventId} with {ExceptionType}",
                eventRecord.CameraName,
                eventRecord.EventId,
                exception.GetType());
            return (false, freedBytes);
        }
    }

    private DiskSpaceInfo GetRecordingSpace()
    {
        var space = _diskSpaceMonitor.GetSpace(_recordingRoot);
        if (space.TotalBytes < 0 || space.AvailableBytes < 0)
            throw new InvalidDataException("Disk space values must not be negative.");
        return space;
    }

    private async Task<IReadOnlyList<EventRecord>> ListOldestCapacityEligibleEventsAsync(
        CancellationToken cancellationToken)
    {
        var completedEvents = new List<EventRecord>();
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _repository.ListEventsAsync(
                cameraName: null,
                fromUtc: null,
                toUtc: null,
                offset,
                BatchSize,
                cancellationToken);
            completedEvents.AddRange(page.Where(eventRecord =>
                eventRecord.Status is EventStatus.Completed or EventStatus.Failed));
            if (page.Count < BatchSize)
                break;

            offset += page.Count;
        }

        return completedEvents
            .OrderBy(eventRecord => eventRecord.StartedUtc)
            .ThenBy(eventRecord => eventRecord.EventId)
            .ToArray();
    }

    private string ResolveConfinedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("A recording file has no relative path.");
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException("A recording file path must be relative to the recording root.");

        var fullPath = Path.GetFullPath(Path.Combine(_recordingRoot, relativePath));
        var pathFromRoot = Path.GetRelativePath(_recordingRoot, fullPath);
        if (pathFromRoot == ".." ||
            pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            pathFromRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(pathFromRoot))
        {
            throw new InvalidDataException("A recording file path escaped the recording root.");
        }

        return fullPath;
    }
}
