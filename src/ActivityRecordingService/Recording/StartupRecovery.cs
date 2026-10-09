using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using Microsoft.Extensions.Logging;

namespace ActivityRecordingService.Recording;

public sealed record RecoveryResult(
    int ScannedReservedFiles,
    int ReclaimedFiles,
    int FailedFiles,
    int DeletedTemporaryFiles,
    long ReclaimedBytes,
    bool NewEventsAllowed,
    IReadOnlyList<string> ExceptionTypes);

public sealed class StartupRecovery
{
    private const int RecoveryBatchSize = 1000;
    private static readonly Regex FinalPartNamePattern =
        new(@"^part-(\d{3,})\.mp4$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IEventRepository _repository;
    private readonly ValidatedActivityRecordingOptions _options;
    private readonly ILogger<StartupRecovery> _logger;
    private readonly string _recordingRoot;

    public StartupRecovery(
        IEventRepository repository,
        ValidatedActivityRecordingOptions options,
        ILogger<StartupRecovery> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recordingRoot = Path.GetFullPath(options.Settings.RecordingRoot);
    }

    public async Task<RecoveryResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var exceptionTypes = new ConcurrentBag<string>();
        var initialActiveEvents = await _repository.ListEventsByStatusAsync(
            EventStatus.Recording,
            RecoveryBatchSize,
            cancellationToken);
        var scannedReservedFiles = 0;
        var reclaimedFiles = 0;
        var failedFiles = 0;
        var reservedTemporaryPaths = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlySet<Guid>? previousBatchFileIds = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await _repository.ListFilesByStatusAsync(
                RecordingFileStatus.Reserved,
                RecoveryBatchSize,
                cancellationToken);

            var batchFileIds = new HashSet<Guid>(batch.Select(file => file.FileId));
            if (batch.Count == RecoveryBatchSize &&
                previousBatchFileIds is not null &&
                batchFileIds.SetEquals(previousBatchFileIds))
            {
                throw new InvalidOperationException(
                    "Reserved file recovery did not advance to a new batch.");
            }

            scannedReservedFiles += batch.Count;
            foreach (var file in batch)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var finalPath = ResolveConfinedPath(file.RelativePath);
                    reservedTemporaryPaths.Add(GetTemporaryPath(finalPath));
                    if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
                    {
                        await _repository.SetFileReadyAsync(
                            file.FileId,
                            new FileInfo(finalPath).Length,
                            cancellationToken);
                        reclaimedFiles++;
                        continue;
                    }

                    await FailReservedFileAsync(file, cancellationToken);
                    failedFiles++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    exceptionTypes.Add(exception.GetType().Name);
                    failedFiles++;
                    _logger.LogWarning(
                        "Failed recording recovery for event {EventId} file {FileId} with {ExceptionType}.",
                        file.EventId,
                        file.FileId,
                        exception.GetType().Name);
                }
            }

            previousBatchFileIds = batchFileIds;
            if (batch.Count < RecoveryBatchSize)
                break;
        }

        reclaimedFiles += await ReconcileActiveEventsAsync(
            exceptionTypes,
            cancellationToken);

        var deletedTemporaryFiles = 0;
        var reclaimedBytes = 0L;
        foreach (var path in ListOrphanTemporaryPaths(
                     reservedTemporaryPaths,
                     initialActiveEvents))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                reclaimedBytes += new FileInfo(path).Length;
                File.Delete(path);
                deletedTemporaryFiles++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                exceptionTypes.Add(exception.GetType().Name);
            }
        }

        var result = new RecoveryResult(
            scannedReservedFiles,
            reclaimedFiles,
            failedFiles,
            deletedTemporaryFiles,
            reclaimedBytes,
            exceptionTypes.IsEmpty,
            exceptionTypes.Distinct().OrderBy(type => type, StringComparer.Ordinal).ToArray());
        _logger.LogInformation(
            "Recording recovery scanned {ScannedReservedFiles} reserved files, reclaimed {ReclaimedFiles}, failed {FailedFiles}, deleted {DeletedTemporaryFiles} temporary files, and reclaimed {ReclaimedBytes} bytes. Exception types: {ExceptionTypes}.",
            result.ScannedReservedFiles,
            result.ReclaimedFiles,
            result.FailedFiles,
            result.DeletedTemporaryFiles,
            result.ReclaimedBytes,
            result.ExceptionTypes.Count == 0 ? "none" : string.Join(", ", result.ExceptionTypes));
        return result;
    }

    private async Task FailReservedFileAsync(
        RecordingFileRecord file,
        CancellationToken cancellationToken)
    {
        await _repository.SetFileStatusAsync(
            file.FileId,
            RecordingFileStatus.Failed,
            cancellationToken);
        var eventRecord = await _repository.GetEventAsync(
            file.EventId,
            cancellationToken);
        if (eventRecord is null || eventRecord.Status is not EventStatus.Recording)
            return;

        await _repository.CompleteEventAsync(
            eventRecord.EventId,
            eventRecord.EndedUtc ?? eventRecord.StartedUtc,
            EventStatus.Failed,
            cancellationToken,
            EventEndReason.StartupIncomplete);
    }

    private async Task<int> ReconcileActiveEventsAsync(
        ConcurrentBag<string> exceptionTypes,
        CancellationToken cancellationToken)
    {
        var indexedFinalFiles = 0;
        var activeEvents = await _repository.ListEventsByStatusAsync(
            EventStatus.Recording,
            RecoveryBatchSize,
            cancellationToken);
        foreach (var eventRecord in activeEvents)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                indexedFinalFiles += await ReconcileActiveEventAsync(
                    eventRecord,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                exceptionTypes.Add(exception.GetType().Name);
                _logger.LogWarning(
                    "Could not reconcile active event {EventId} for camera {CameraName} with {ExceptionType}.",
                    eventRecord.EventId,
                    eventRecord.CameraName,
                    exception.GetType().Name);
            }
        }

        return indexedFinalFiles;
    }

    private async Task<int> ReconcileActiveEventAsync(
        EventRecord eventRecord,
        CancellationToken cancellationToken)
    {
        var files = await _repository.GetFilesAsync(
            eventRecord.EventId,
            cancellationToken);
        var eventDirectory = GetExpectedEventDirectory(eventRecord);
        var indexedFinalFiles = 0;
        if (Directory.Exists(eventDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(eventDirectory, "part-*.mp4"))
            {
                var match = FinalPartNamePattern.Match(Path.GetFileName(path));
                if (!match.Success)
                    continue;

                var partIndex = int.Parse(
                    match.Groups[1].Value,
                    CultureInfo.InvariantCulture);
                if (files.Any(file => file.PartIndex == partIndex))
                    continue;

                var bytes = new FileInfo(path).Length;
                if (bytes <= 0)
                    continue;

                await _repository.AddFileAsync(
                    new RecordingFileRecord(
                        Guid.NewGuid(),
                        eventRecord.EventId,
                        partIndex,
                        CreateRelativePath(path),
                        bytes,
                        RecordingFileStatus.Ready),
                    cancellationToken);
                indexedFinalFiles++;
            }

            files = await _repository.GetFilesAsync(
                eventRecord.EventId,
                cancellationToken);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var endedUtc = nowUtc >= eventRecord.StartedUtc
            ? nowUtc
            : eventRecord.StartedUtc;
        var hasPlayablePart = files.Any(file => file.Status == RecordingFileStatus.Ready);
        await _repository.CompleteEventAsync(
            eventRecord.EventId,
            endedUtc,
            hasPlayablePart ? EventStatus.Completed : EventStatus.Failed,
            cancellationToken,
            hasPlayablePart ? EventEndReason.StartupRecovered : EventEndReason.StartupIncomplete);
        return indexedFinalFiles;
    }

    private IEnumerable<string> ListOrphanTemporaryPaths(
        ISet<string> reservedTemporaryPaths,
        IReadOnlyList<EventRecord> initialActiveEvents)
    {
        var eventsRoot = Path.Combine(_recordingRoot, "events");
        if (Directory.Exists(eventsRoot))
        {
            foreach (var cameraDirectory in Directory.EnumerateDirectories(eventsRoot))
            {
                if (new DirectoryInfo(cameraDirectory).LinkTarget is not null)
                    continue;

                foreach (var eventDirectory in Directory.EnumerateDirectories(cameraDirectory))
                {
                    if (new DirectoryInfo(eventDirectory).LinkTarget is not null)
                        continue;

                    foreach (var path in Directory.EnumerateFiles(eventDirectory, "*.tmp.mp4"))
                    {
                        var fullPath = Path.GetFullPath(path);
                        if (!reservedTemporaryPaths.Contains(fullPath) &&
                            initialActiveEvents.All(active =>
                                !string.Equals(
                                    GetExpectedEventDirectory(active),
                                    Path.GetDirectoryName(fullPath),
                                    StringComparison.Ordinal)))
                        {
                            yield return fullPath;
                        }
                    }
                }
            }
        }

        var rollingRoot = Path.Combine(_recordingRoot, "rolling");
        if (!Directory.Exists(rollingRoot))
            yield break;

        foreach (var cameraDirectory in Directory.EnumerateDirectories(rollingRoot))
        {
            if (new DirectoryInfo(cameraDirectory).LinkTarget is not null)
                continue;

            foreach (var path in Directory.EnumerateFiles(cameraDirectory, "rolling_*.mp4"))
            {
                if (CouldBeSourceSegment(path, initialActiveEvents))
                    continue;

                yield return Path.GetFullPath(path);
            }
        }
    }

    private bool CouldBeSourceSegment(
        string path,
        IReadOnlyList<EventRecord> activeEvents)
    {
        var cameraDirectoryName = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
        var modifiedUtc = File.GetLastWriteTimeUtc(path);
        return activeEvents.Any(active =>
            string.Equals(
                CreateSafeFileName(active.CameraName),
                cameraDirectoryName,
                StringComparison.Ordinal) &&
            modifiedUtc >= active.StartedUtc.UtcDateTime.AddSeconds(
                -_options.Settings.Recording.PreEventSeconds));
    }

    private string GetExpectedEventDirectory(EventRecord eventRecord) =>
        Path.Combine(
            _recordingRoot,
            "events",
            CreateSafeFileName(eventRecord.CameraName),
            eventRecord.EventId.ToString("N"));

    private string CreateRelativePath(string fullPath)
    {
        var relativePath = Path.GetRelativePath(
            _recordingRoot,
            Path.GetFullPath(fullPath));
        if (relativePath.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Recovered event part is outside the recording root.");

        return relativePath.Replace('\\', '/');
    }

    private string ResolveConfinedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("A recording file path must be relative to the recording root.");
        }

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

    private static string GetTemporaryPath(string finalPath) =>
        Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $"{Path.GetFileNameWithoutExtension(finalPath)}.tmp.mp4");
}
