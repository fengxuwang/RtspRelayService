using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Recording;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ActivityRecordingService.Tests.Recording;

public sealed class StartupRecoveryTests : IDisposable
{
    private readonly DirectoryInfo _rootDirectory =
        Directory.CreateTempSubdirectory("startup-recovery-");
    private readonly RecordingLogger _logger = new();

    static StartupRecoveryTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task Run_ReclaimsReadyFilesAndFailsOrphans()
    {
        await using var repository = await CreateRepositoryAsync();
        var readyEventId = await CreateReservedFileAsync(
            repository,
            "events/camera-a/ready",
            "part-000.mp4",
            finalBytes: 8,
            temporaryBytes: 8);
        var missingFinalEventId = await CreateReservedFileAsync(
            repository,
            "events/camera-a/missing-final",
            "part-000.mp4",
            finalBytes: null,
            temporaryBytes: 8);
        var emptyFinalEventId = await CreateReservedFileAsync(
            repository,
            "events/camera-a/empty-final",
            "part-000.mp4",
            finalBytes: 0,
            temporaryBytes: 8);
        var eventDirectory = Path.Combine(
            _rootDirectory.FullName,
            "events",
            "camera-a",
            "event-without-metadata");
        Directory.CreateDirectory(eventDirectory);
        var orphanTemporaryPath = Path.Combine(eventDirectory, "part-009.tmp.mp4");
        File.WriteAllBytes(orphanTemporaryPath, new byte[11]);
        var reservedSegmentPath = Path.Combine(
            _rootDirectory.FullName,
            "events",
            "camera-a",
            "ready",
            "rolling_000000000.mp4");
        File.WriteAllBytes(reservedSegmentPath, new byte[13]);
        var rollingDirectory = Path.Combine(
            _rootDirectory.FullName,
            "rolling",
            "camera-a");
        Directory.CreateDirectory(rollingDirectory);
        var unreservedRollingPath = Path.Combine(rollingDirectory, "rolling_000000001.mp4");
        File.WriteAllBytes(unreservedRollingPath, new byte[17]);
        var unrelatedPath = Path.Combine(rollingDirectory, "keep.ts");
        File.WriteAllBytes(unrelatedPath, new byte[19]);
        var rootTemporaryPath = Path.Combine(_rootDirectory.FullName, "outside-event.tmp.mp4");
        File.WriteAllBytes(rootTemporaryPath, new byte[23]);

        var recovery = new StartupRecovery(
            repository,
            CreateValidatedOptions(),
            _logger);
        var result = await recovery.RunAsync(CancellationToken.None);

        result.Should().Match<RecoveryResult>(value =>
            value.ScannedReservedFiles == 3 &&
            value.ReclaimedFiles == 1 &&
            value.FailedFiles == 2 &&
            value.DeletedTemporaryFiles == 1 &&
            value.ReclaimedBytes == 11 &&
            value.NewEventsAllowed &&
            value.ExceptionTypes.Count == 0);
        (await repository.GetFilesAsync(readyEventId, CancellationToken.None))
            .Should().ContainSingle().Which.Status.Should().Be(RecordingFileStatus.Ready);
        (await repository.GetEventAsync(readyEventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Completed);
        (await repository.GetEventAsync(readyEventId, CancellationToken.None))!.EndReason
            .Should().Be(EventEndReason.StartupRecovered);
        foreach (var eventId in new[] { missingFinalEventId, emptyFinalEventId })
        {
            (await repository.GetFilesAsync(eventId, CancellationToken.None))
                .Should().ContainSingle().Which.Status.Should().Be(RecordingFileStatus.Failed);
            (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
                .Should().Be(EventStatus.Failed);
        }

        File.Exists(orphanTemporaryPath).Should().BeFalse();
        File.Exists(unreservedRollingPath).Should().BeTrue();
        File.Exists(reservedSegmentPath).Should().BeTrue();
        File.Exists(unrelatedPath).Should().BeTrue();
        File.Exists(rootTemporaryPath).Should().BeTrue();
        _logger.InformationMessages.Should().ContainSingle().Which.Should().Be(
            "Recording recovery scanned 3 reserved files, reclaimed 1, failed 2, deleted 1 temporary files, and reclaimed 11 bytes. Exception types: none.");
        _logger.AllMessages.Should().NotContain(message =>
            message.Contains(_rootDirectory.FullName, StringComparison.Ordinal) ||
            message.Contains("rtsp://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_ProcessesEachReservedFileOnlyOnceAcrossBatches()
    {
        await using var repository = new BatchingEventRepository();
        var eventId = Guid.NewGuid();
        repository.Events.Add(new EventRecord(
            eventId,
            "camera-a",
            DateTimeOffset.Parse("2026-09-29T10:00:00.000Z"),
            EndedUtc: null,
            EventStatus.Recording,
            FileBytes: 0,
            FileCount: 0));
        repository.Files.AddRange(Enumerable.Range(0, 1000).Select(index => new RecordingFileRecord(
            Guid.NewGuid(),
            eventId,
            PartIndex: index,
            $"events/camera-a/missing-{index}/part-000.mp4",
            Bytes: 8,
            RecordingFileStatus.Reserved)));

        var recovery = new StartupRecovery(
            repository,
            CreateValidatedOptions(),
            _logger);
        var result = await recovery.RunAsync(CancellationToken.None);

        result.Should().Match<RecoveryResult>(value =>
            value.ScannedReservedFiles == 1000 &&
            value.FailedFiles == 1000 &&
            value.NewEventsAllowed);
        repository.Files.Should().OnlyContain(file =>
            file.Status == RecordingFileStatus.Failed);
        repository.ReservedListCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Run_IndexesUnindexedFinalPartAndCompletesActiveEvent()
    {
        await using var repository = await CreateRepositoryAsync();
        var eventId = await CreateActiveEventAsync(repository);
        var eventDirectory = Path.Combine(
            _rootDirectory.FullName,
            "events",
            "camera-a",
            eventId.ToString("N"));
        Directory.CreateDirectory(eventDirectory);
        var finalPath = Path.Combine(eventDirectory, "part-000.mp4");
        File.WriteAllBytes(finalPath, new byte[31]);

        var recovery = new StartupRecovery(repository, CreateValidatedOptions(), _logger);
        var result = await recovery.RunAsync(CancellationToken.None);

        result.ReclaimedFiles.Should().Be(1);
        var files = await repository.GetFilesAsync(eventId, CancellationToken.None);
        files.Should().ContainSingle().Which.Should().Match<RecordingFileRecord>(file =>
            file.RelativePath.EndsWith("part-000.mp4", StringComparison.Ordinal) &&
            file.Bytes == 31 &&
            file.Status == RecordingFileStatus.Ready);
        var storedEvent = await repository.GetEventAsync(eventId, CancellationToken.None);
        storedEvent!.Status.Should().Be(EventStatus.Completed);
        storedEvent.EndReason.Should().Be(EventEndReason.StartupRecovered);
    }

    [Fact]
    public async Task Run_FailsActiveEventWithoutPlayablePartAndPreservesPossibleSourceSegments()
    {
        await using var repository = await CreateRepositoryAsync();
        var eventId = await CreateActiveEventAsync(repository);
        var rollingDirectory = Path.Combine(
            _rootDirectory.FullName,
            "rolling",
            "camera-a");
        Directory.CreateDirectory(rollingDirectory);
        var rollingPath = Path.Combine(rollingDirectory, "rolling_000000012.mp4");
        File.WriteAllBytes(rollingPath, new byte[37]);

        var recovery = new StartupRecovery(repository, CreateValidatedOptions(), _logger);
        var result = await recovery.RunAsync(CancellationToken.None);

        result.FailedFiles.Should().Be(0);
        (await repository.GetEventAsync(eventId, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Failed);
        File.Exists(rollingPath).Should().BeTrue();
    }

    [Fact]
    public async Task Run_LogsFileIdentifierAndExceptionTypeWithoutPathWhenRecoveryFails()
    {
        await using var repository = await CreateRepositoryAsync();
        await repository.UpsertCameraAsync(
            "camera-a",
            enabled: true,
            retentionDays: null,
            CancellationToken.None);
        var eventId = await repository.CreateEventAsync(
            "camera-a",
            DateTimeOffset.Parse("2026-09-29T10:00:00.000Z"),
            CancellationToken.None);
        var fileId = Guid.NewGuid();
        await repository.AddFileAsync(
            new RecordingFileRecord(
                fileId,
                eventId,
                PartIndex: 0,
                "../outside.mp4",
                Bytes: 8,
                RecordingFileStatus.Reserved),
            CancellationToken.None);

        var recovery = new StartupRecovery(
            repository,
            CreateValidatedOptions(),
            _logger);
        var result = await recovery.RunAsync(CancellationToken.None);

        result.FailedFiles.Should().Be(1);
        result.NewEventsAllowed.Should().BeFalse();
        result.ExceptionTypes.Should().ContainSingle().Which.Should().Be("InvalidDataException");
        _logger.WarningMessages.Should().ContainSingle().Which.Should().Be(
            $"Failed recording recovery for event {eventId} file {fileId} with InvalidDataException.");
        _logger.AllMessages.Should().NotContain(message =>
            message.Contains(_rootDirectory.FullName, StringComparison.Ordinal));
    }

    private async Task<SqliteEventRepository> CreateRepositoryAsync()
    {
        var repository = new SqliteEventRepository(
            Path.Combine(_rootDirectory.FullName, "events.db"),
            defaultRetentionDays: 7);
        await repository.InitializeAsync(CancellationToken.None);
        return repository;
    }

    private async Task<Guid> CreateReservedFileAsync(
        SqliteEventRepository repository,
        string relativeDirectory,
        string fileName,
        long? finalBytes,
        long temporaryBytes)
    {
        await repository.UpsertCameraAsync(
            "camera-a",
            enabled: true,
            retentionDays: null,
            CancellationToken.None);
        var eventId = await repository.CreateEventAsync(
            "camera-a",
            DateTimeOffset.Parse("2026-09-29T10:00:00.000Z"),
            CancellationToken.None);
        var directory = Path.Combine(_rootDirectory.FullName, relativeDirectory);
        Directory.CreateDirectory(directory);
        if (finalBytes.HasValue)
            File.WriteAllBytes(
                Path.Combine(directory, fileName),
                new byte[finalBytes.Value]);
        if (temporaryBytes > 0)
            File.WriteAllBytes(
                Path.Combine(directory, Path.ChangeExtension(fileName, ".tmp.mp4")),
                new byte[temporaryBytes]);

        await repository.AddFileAsync(
            new RecordingFileRecord(
                Guid.NewGuid(),
                eventId,
                PartIndex: 0,
                $"{relativeDirectory}/{fileName}".Replace('\\', '/'),
                Bytes: 8,
                RecordingFileStatus.Reserved),
            CancellationToken.None);
        return eventId;
    }

    private static async Task<Guid> CreateActiveEventAsync(SqliteEventRepository repository)
    {
        await repository.UpsertCameraAsync(
            "camera-a",
            enabled: true,
            retentionDays: null,
            CancellationToken.None);
        return await repository.CreateEventAsync(
            "camera-a",
            DateTimeOffset.Parse("2026-09-29T10:00:00.000Z"),
            CancellationToken.None);
    }

    private ValidatedActivityRecordingOptions CreateValidatedOptions()
    {
        var settings = new ActivityRecordingOptions
        {
            RecordingRoot = _rootDirectory.FullName
        };
        return new ValidatedActivityRecordingOptions(
            "/usr/bin/ffmpeg",
            settings,
            [],
            Errors: []);
    }

    public void Dispose() => _rootDirectory.Delete(recursive: true);

    private sealed class RecordingLogger : ILogger<StartupRecovery>
    {
        public List<string> InformationMessages { get; } = [];

        public List<string> WarningMessages { get; } = [];

        public List<string> AllMessages { get; } = [];

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
            AllMessages.Add(message);
            if (logLevel == LogLevel.Information)
                InformationMessages.Add(message);
            if (logLevel == LogLevel.Warning)
                WarningMessages.Add(message);
        }
    }

    private sealed class BatchingEventRepository : IEventRepository
    {
        public List<EventRecord> Events { get; } = [];

        public List<RecordingFileRecord> Files { get; } = [];

        public int ReservedListCallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertCameraAsync(
            string cameraName,
            bool enabled,
            int? retentionDays,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Guid> CreateEventAsync(
            string cameraName,
            DateTimeOffset startedUtc,
            CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());

        public Task CompleteEventAsync(
            Guid eventId,
            DateTimeOffset endedUtc,
            EventStatus status,
            CancellationToken cancellationToken,
            EventEndReason? endReason = null)
        {
            var index = Events.FindIndex(item => item.EventId == eventId);
            if (index >= 0)
                Events[index] = Events[index] with
                {
                    EndedUtc = endedUtc,
                    Status = status,
                    EndReason = endReason
                };
            return Task.CompletedTask;
        }

        public Task AddFileAsync(RecordingFileRecord file, CancellationToken cancellationToken)
        {
            Files.Add(file);
            return Task.CompletedTask;
        }

        public Task SetFileStatusAsync(
            Guid fileId,
            RecordingFileStatus status,
            CancellationToken cancellationToken)
        {
            var index = Files.FindIndex(item => item.FileId == fileId);
            if (index >= 0)
                Files[index] = Files[index] with { Status = status };
            return Task.CompletedTask;
        }

        public Task SetFileReadyAsync(
            Guid fileId,
            long bytes,
            CancellationToken cancellationToken)
        {
            var index = Files.FindIndex(item => item.FileId == fileId);
            if (index >= 0)
                Files[index] = Files[index] with
                {
                    Bytes = bytes,
                    Status = RecordingFileStatus.Ready
                };
            return Task.CompletedTask;
        }

        public Task<EventRecord?> GetEventAsync(Guid eventId, CancellationToken cancellationToken) =>
            Task.FromResult(Events.SingleOrDefault(item => item.EventId == eventId));

        public Task<IReadOnlyList<RecordingFileRecord>> GetFilesAsync(
            Guid eventId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingFileRecord>>(
                Files.Where(item => item.EventId == eventId).ToList());

        public Task<RecordingFileRecord?> GetReadyFileAsync(Guid fileId, CancellationToken cancellationToken) =>
            Task.FromResult(Files.SingleOrDefault(item => item.FileId == fileId));

        public Task<IReadOnlyList<EventRecord>> ListEventsAsync(
            string? cameraName,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventRecord>>(Events.Skip(offset).Take(limit).ToList());

        public Task<IReadOnlyList<EventRecord>> ListEventsByStatusAsync(
            EventStatus status,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventRecord>>(
                Events.Where(item => item.Status == status).Take(limit).ToList());

        public Task<IReadOnlyList<FileEventItem>> ListFileEventsAsync(
            string? cameraName,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FileEventItem>>([]);

        public Task<long> GetRecordingBytesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Files.Where(item => item.Status == RecordingFileStatus.Ready)
                .Sum(item => item.Bytes));

        public Task<IReadOnlyList<EventRecord>> ListDeletableEventsAsync(
            DateTimeOffset nowUtc,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventRecord>>([]);

        public Task AddCleanupAuditAsync(
            string reason,
            int eventCount,
            long freedBytes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteEventAsync(Guid eventId, CancellationToken cancellationToken)
        {
            Events.RemoveAll(item => item.EventId == eventId);
            Files.RemoveAll(item => item.EventId == eventId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RecordingFileRecord>> ListFilesByStatusAsync(
            RecordingFileStatus status,
            int limit,
            CancellationToken cancellationToken)
        {
            if (status == RecordingFileStatus.Reserved &&
                ++ReservedListCallCount == 3)
            {
                throw new InvalidOperationException(
                    "The reserved file scan repeatedly returned a full batch without changing status.");
            }

            IReadOnlyList<RecordingFileRecord> batch = Files
                .Where(item => item.Status == status)
                .Take(limit)
                .ToList();
            return Task.FromResult(batch);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
