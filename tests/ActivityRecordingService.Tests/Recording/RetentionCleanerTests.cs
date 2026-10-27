using System.Data;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Recording;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ActivityRecordingService.Tests.Recording;

public sealed class RetentionCleanerTests : IDisposable
{
    private const long DefaultMaxRecordingBytes = 540L * 1024 * 1024 * 1024;

    private static readonly DateTimeOffset NowUtc =
        DateTimeOffset.Parse("2026-09-29T12:00:00.000Z");

    private readonly DirectoryInfo _directory =
        Directory.CreateTempSubdirectory("retention-cleaner-");

    static RetentionCleanerTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task RunAsync_DeletesAgeEligibleEventsOldestFirstAndKeepsNewerAndActiveEvents()
    {
        await using var repository = await CreateRepositoryAsync();
        var oldEvent = await CreateCompletedEventAsync(
            repository,
            "camera-a",
            NowUtc.AddDays(-8),
            bytes: 101);
        var secondOldEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-7).AddMinutes(-2),
            bytes: 103);
        var recentEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-6),
            bytes: 57);
        var activeEvent = await repository.CreateEventAsync(
            "camera-a",
            NowUtc.AddDays(-8),
            CancellationToken.None);
        var orderingRepository = new DelegatingEventRepository(repository);

        (await orderingRepository.ListDeletableEventsAsync(
            NowUtc,
            limit: 100,
            CancellationToken.None))
            .Select(deletable => deletable.EventId)
            .Should()
            .NotContain(activeEvent);

        var result = await CreateCleaner(orderingRepository).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(
            DeletedEvents: 2,
            FreedBytes: 204,
            NewEventsAllowed: true,
            BlockingReason: null));
        orderingRepository.DeleteOrder.Should().Equal(oldEvent, secondOldEvent);
        await AssertEventDeletedAsync(repository, oldEvent);
        await AssertEventDeletedAsync(repository, secondOldEvent);
        await AssertEventRetainedAsync(repository, recentEvent);
        (await repository.GetEventAsync(activeEvent, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Recording);
        (await QueryCleanupAuditsAsync())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("retention", 2, 204L));
    }

    [Fact]
    public async Task RunAsync_HonorsCameraRetentionOverrideBeforeGlobalCutoff()
    {
        await using var repository = await CreateRepositoryAsync();
        await repository.UpsertCameraAsync(
            "short-camera",
            enabled: true,
            retentionDays: 3,
            CancellationToken.None);
        await repository.UpsertCameraAsync(
            "default-camera",
            enabled: true,
            retentionDays: null,
            CancellationToken.None);
        var shortEvent = await CreateCompletedEventAsync(
            repository,
            "short-camera",
            NowUtc.AddDays(-3).AddMinutes(-2),
            bytes: 80,
            cameraRetentionDays: 3);
        var defaultEvent = await CreateCompletedEventAsync(
            repository,
            "default-camera",
            NowUtc.AddDays(-4),
            bytes: 90);

        var result = await CreateCleaner(repository).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(1, 80, true, null));
        await AssertEventDeletedAsync(repository, shortEvent);
        await AssertEventRetainedAsync(repository, defaultEvent);
    }

    [Fact]
    public async Task RunAsync_DeletesAllAgeEligibleEventsBeyondQueryBatch()
    {
        await using var repository = await CreateRepositoryAsync();
        var eventIds = new List<Guid>();
        for (var index = 0; index < 101; index++)
        {
            eventIds.Add(await CreateCompletedEventAsync(
                repository,
                NowUtc.AddDays(-8).AddMinutes(index),
                bytes: 1));
        }

        var result = await CreateCleaner(repository).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(101, 101, true, null));
        foreach (var eventId in eventIds)
        {
            var eventRecord = await repository.GetEventAsync(eventId, CancellationToken.None);
            eventRecord!.Status.Should().Be(EventStatus.Deleted);
        }
    }

    [Fact]
    public async Task RunAsync_QuotaCleanupStartsWithOldestCompletedEvent()
    {
        await using var repository = await CreateRepositoryAsync();
        var oldEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-2),
            bytes: 120);
        var newerEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddHours(-12),
            bytes: 80);
        var settings = CreateSettings(maxRecordingBytes: 150);
        var repositoryWithOrder = new DelegatingEventRepository(repository);

        var result = await CreateCleaner(
            repositoryWithOrder,
            settings,
            availableBytes: 500.Gibibytes()).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(1, 120, true, null));
        repositoryWithOrder.DeleteOrder.Should().Equal(oldEvent);
        await AssertEventDeletedAsync(repository, oldEvent);
        await AssertEventRetainedAsync(repository, newerEvent);
        (await QueryCleanupAuditsAsync())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("quota", 1, 120L));
    }

    [Fact]
    public async Task RunAsync_CapacityCleanupPrefersOlderFailedEventOverNewerCompletedEvent()
    {
        await using var repository = await CreateRepositoryAsync();
        var olderFailedEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-2),
            bytes: 120,
            status: EventStatus.Failed);
        var newerCompletedEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddHours(-12),
            bytes: 80);
        var repositoryWithOrder = new DelegatingEventRepository(repository);

        var result = await CreateCleaner(
            repositoryWithOrder,
            CreateSettings(maxRecordingBytes: 150),
            availableBytes: 500.Gibibytes()).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(1, 120, true, null));
        repositoryWithOrder.DeleteOrder.Should().Equal(olderFailedEvent);
        (await repository.GetEventAsync(olderFailedEvent, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Deleted);
        (await repository.GetEventAsync(newerCompletedEvent, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Completed);
        (await QueryCleanupAuditsAsync())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("quota", 1, 120L));
    }

    [Fact]
    public async Task RunAsync_MinimumFreeSpaceCleanupReportsWhenWaterMarkCannotBeRestored()
    {
        await using var repository = await CreateRepositoryAsync();
        await CreateCompletedEventAsync(repository, NowUtc.AddHours(-1), bytes: 70);

        var result = await CreateCleaner(
            repository,
            CreateSettings(maxRecordingBytes: 500),
            availableBytes: 60.Gibibytes() - 1).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(
            DeletedEvents: 1,
            FreedBytes: 70,
            NewEventsAllowed: false,
            BlockingReason: "Minimum free disk space reached."));
        (await QueryCleanupAuditsAsync())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("min-free-space", 1, 70L));
    }

    [Fact]
    public async Task RunAsync_WhenNoEligibleEventCanRestoreSpace_AuditsBlockedPass()
    {
        await using var repository = await CreateRepositoryAsync();
        await repository.UpsertCameraAsync(
            "camera-a",
            enabled: true,
            retentionDays: null,
            CancellationToken.None);
        await repository.CreateEventAsync(
            "camera-a",
            NowUtc.AddMinutes(-1),
            CancellationToken.None);

        var result = await CreateCleaner(
            repository,
            CreateSettings(maxRecordingBytes: 500),
            availableBytes: 60.Gibibytes() - 1).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(
            DeletedEvents: 0,
            FreedBytes: 0,
            NewEventsAllowed: false,
            BlockingReason: "Minimum free disk space reached."));
        (await QueryCleanupAuditsAsync())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("min-free-space", 0, 0L));
    }

    [Fact]
    public async Task RunAsync_MinimumFreeSpaceCleanupRunsAtExactlySixtyGiB()
    {
        await using var repository = await CreateRepositoryAsync();
        await CreateCompletedEventAsync(repository, NowUtc.AddHours(-1), bytes: 70);

        var result = await CreateCleaner(
            repository,
            CreateSettings(maxRecordingBytes: 500),
            availableBytes: 60.Gibibytes()).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(
            DeletedEvents: 1,
            FreedBytes: 70,
            NewEventsAllowed: false,
            BlockingReason: "Minimum free disk space reached."));
        (await QueryCleanupAuditsAsync())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("min-free-space", 1, 70L));
    }

    [Fact]
    public async Task RunAsync_ConfinesDeletesToRecordingRootAndContinuesAfterPathFailure()
    {
        await using var repository = await CreateRepositoryAsync();
        var validEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-8),
            bytes: 130);
        var escapedPath = Path.Combine(
            "..",
            $"task-9-escaped-{Guid.NewGuid():N}.mp4");
        var escapedFile = Path.GetFullPath(Path.Combine(_directory.FullName, escapedPath));
        await File.WriteAllBytesAsync(escapedFile, new byte[210]);
        var escapedEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-7).AddMinutes(-1),
            bytes: 210,
            relativePath: escapedPath);

        var result = await CreateCleaner(repository).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(1, 130, true, null));
        File.Exists(escapedFile).Should().BeTrue();
        await AssertEventDeletedAsync(repository, validEvent);
        await AssertEventRetainedAsync(repository, escapedEvent);
        (await QueryCleanupAuditsAsync())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("retention", 1, 130L));
    }

    [Fact]
    public async Task RunAsync_ContinuesWhenMetadataDeletionFailsAfterFilesAreDeleted()
    {
        await using var repository = await CreateRepositoryAsync();
        var failedMetadataEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-8),
            bytes: 100);
        var laterEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-7).AddMinutes(-1),
            bytes: 40);
        var delegatedRepository = new DelegatingEventRepository(repository)
        {
            BeforeDeleteEvent = async eventId =>
            {
                var files = await repository.GetFilesAsync(eventId, CancellationToken.None);
                files.Should().NotBeEmpty();
                files.Select(file => ResolveRecordingPath(file.RelativePath))
                    .Should()
                    .OnlyContain(path => !File.Exists(path));
            },
            DeleteEventOverride = (eventId, cancellationToken) =>
                eventId == failedMetadataEvent
                    ? Task.FromException(new InvalidOperationException("metadata unavailable"))
                    : repository.DeleteEventAsync(eventId, cancellationToken)
        };

        var result = await CreateCleaner(delegatedRepository).RunAsync(CancellationToken.None);

        result.Should().Be(new CleanupResult(1, 140, true, null));
        delegatedRepository.DeleteOrder.Should().Equal(failedMetadataEvent, laterEvent);
        (await repository.GetEventAsync(failedMetadataEvent, CancellationToken.None))!.Status
            .Should().Be(EventStatus.Completed);
        await AssertEventDeletedAsync(repository, laterEvent);
        (await QueryCleanupAuditsAsync())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(("retention", 1, 140L));
    }

    [Fact]
    public async Task RunAsync_WhenCanceledDoesNotDeleteOrAudit()
    {
        await using var repository = await CreateRepositoryAsync();
        var oldEvent = await CreateCompletedEventAsync(
            repository,
            NowUtc.AddDays(-8),
            bytes: 64);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var act = () => CreateCleaner(repository).RunAsync(cancellationTokenSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await AssertEventRetainedAsync(repository, oldEvent);
        (await QueryCleanupAuditsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CanStartEventAsync_RejectsWhenTotalQuotaIsReached()
    {
        await using var repository = await CreateRepositoryAsync();
        await CreateCompletedEventAsync(repository, NowUtc.AddHours(-1), bytes: 150);

        var decision = await CreateGuard(
            repository,
            maxRecordingBytes: 150,
            availableBytes: 500).CanStartEventAsync("camera-a", CancellationToken.None);

        decision.Should().Be(new CapacityDecision(false, "Recording storage quota reached."));
    }

    [Fact]
    public async Task CanStartEventAsync_RejectsAtMinimumFreeSpace()
    {
        await using var repository = await CreateRepositoryAsync();
        await CreateCompletedEventAsync(repository, NowUtc.AddHours(-1), bytes: 10);
        var minimumFreeBytes = 60.Gibibytes();
        var guard = CreateGuard(
            repository,
            maxRecordingBytes: 500,
            availableBytes: minimumFreeBytes - 1);

        var rejected = await guard.CanStartEventAsync("camera-a", CancellationToken.None);
        var allowed = await CreateGuard(
            repository,
            maxRecordingBytes: 500,
            availableBytes: minimumFreeBytes).CanStartEventAsync(
            "camera-a",
            CancellationToken.None);

        rejected.Should().Be(new CapacityDecision(false, "Minimum free disk space reached."));
        allowed.Should().Be(new CapacityDecision(false, "Minimum free disk space reached."));
    }

    [Fact]
    public void GetSpace_RejectsRelativePath()
    {
        var act = () => new DiskSpaceMonitor().GetSpace("recordings");

        act.Should().Throw<ArgumentException>();
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private async Task<SqliteEventRepository> CreateRepositoryAsync()
    {
        var repository = new SqliteEventRepository(
            Path.Combine(_directory.FullName, "events.db"),
            defaultRetentionDays: 7);
        await repository.InitializeAsync(CancellationToken.None);
        return repository;
    }

    private RetentionCleaner CreateCleaner(
        IEventRepository repository,
        ActivityRecordingOptions? settings = null,
        long? availableBytes = null)
    {
        settings ??= CreateSettings();
        var available = availableBytes ?? 500.Gibibytes();
        return new RetentionCleaner(
            repository,
            new FakeDiskSpaceMonitor(new DiskSpaceInfo(1_000.Gibibytes(), available)),
            CreateOptions(settings),
            new FixedTimeProvider(NowUtc),
            new RecordingLogger<RetentionCleaner>());
    }

    private RecordingCapacityGuard CreateGuard(
        IEventRepository repository,
        long maxRecordingBytes,
        long availableBytes)
    {
        var settings = CreateSettings(maxRecordingBytes);
        return new RecordingCapacityGuard(
            repository,
            new FakeDiskSpaceMonitor(new DiskSpaceInfo(1_000.Gibibytes(), availableBytes)),
            CreateOptions(settings));
    }

    private ActivityRecordingOptions CreateSettings(
        long maxRecordingBytes = DefaultMaxRecordingBytes) => new()
        {
            RecordingRoot = _directory.FullName,
            Recording = new RecordingOptions
            {
                MaxRecordingBytes = maxRecordingBytes,
                MinFreeBytes = 60.Gibibytes()
            }
        };

    private static ValidatedActivityRecordingOptions CreateOptions(
        ActivityRecordingOptions settings) => new(
        "/usr/bin/ffmpeg",
        settings,
        [new ValidatedCameraOptions(
            "camera-a",
            "rtsp://camera.example/sub",
            "rtsp://camera.example/main",
            Roi: null,
            RetentionDays: null,
            Settings: new CameraOptions())],
        Errors: []);

    private async Task<Guid> CreateCompletedEventAsync(
        SqliteEventRepository repository,
        string cameraName,
        DateTimeOffset startedUtc,
        long bytes,
        string? relativePath = null,
        int? cameraRetentionDays = null,
        EventStatus status = EventStatus.Completed)
    {
        await repository.UpsertCameraAsync(
            cameraName,
            enabled: true,
            cameraRetentionDays,
            CancellationToken.None);
        var eventId = await repository.CreateEventAsync(
            cameraName,
            startedUtc,
            CancellationToken.None);
        await repository.CompleteEventAsync(
            eventId,
            startedUtc.AddMinutes(1),
            status,
            CancellationToken.None);
        relativePath ??= Path.Combine(
            "events",
            cameraName,
            $"{eventId:N}-part-000.mp4");
        var fullPath = ResolveRecordingPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, new byte[bytes]);
        await repository.AddFileAsync(
            new RecordingFileRecord(
                Guid.NewGuid(),
                eventId,
                PartIndex: 0,
                RelativePath: relativePath,
                Bytes: bytes,
                RecordingFileStatus.Ready),
            CancellationToken.None);
        return eventId;
    }

    private Task<Guid> CreateCompletedEventAsync(
        SqliteEventRepository repository,
        DateTimeOffset startedUtc,
        long bytes,
        string? relativePath = null,
        int? cameraRetentionDays = null,
        EventStatus status = EventStatus.Completed) => CreateCompletedEventAsync(
        repository,
        "camera-a",
        startedUtc,
        bytes,
        relativePath,
        cameraRetentionDays,
        status);

    private string ResolveRecordingPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(_directory.FullName, relativePath));

    private static async Task AssertEventDeletedAsync(
        SqliteEventRepository repository,
        Guid eventId)
    {
        var deleted = await repository.GetEventAsync(eventId, CancellationToken.None);
        deleted!.Status.Should().Be(EventStatus.Deleted);
        deleted.FileBytes.Should().Be(0);
        deleted.FileCount.Should().Be(0);
        (await repository.GetFilesAsync(eventId, CancellationToken.None)).Should().BeEmpty();
    }

    private static async Task AssertEventRetainedAsync(
        SqliteEventRepository repository,
        Guid eventId)
    {
        var retained = await repository.GetEventAsync(eventId, CancellationToken.None);
        retained!.Status.Should().Be(EventStatus.Completed);
        retained.FileCount.Should().Be(1);
    }

    private async Task<List<(string Reason, int EventCount, long FreedBytes)>> QueryCleanupAuditsAsync()
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory.FullName, "events.db"),
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reason, event_count, freed_bytes
            FROM cleanup_audits
            ORDER BY cleanup_audit_id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var audits = new List<(string, int, long)>();
        while (await reader.ReadAsync())
        {
            audits.Add((
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2)));
        }

        return audits;
    }

    private sealed class FakeDiskSpaceMonitor(DiskSpaceInfo info) : IDiskSpaceMonitor
    {
        public DiskSpaceInfo GetSpace(string path)
        {
            path.Should().NotBeNullOrEmpty();
            return info;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Messages.Add(formatter(state, exception));
        }
    }

    private sealed class DelegatingEventRepository(IEventRepository inner) : IEventRepository
    {
        private readonly IEventRepository _inner = inner;

        public List<Guid> DeleteOrder { get; } = [];

        public Func<Guid, Task>? BeforeDeleteEvent { get; init; }

        public Func<Guid, CancellationToken, Task>? DeleteEventOverride { get; init; }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            _inner.InitializeAsync(cancellationToken);

        public Task UpsertCameraAsync(
            string cameraName,
            bool enabled,
            int? retentionDays,
            CancellationToken cancellationToken) =>
            _inner.UpsertCameraAsync(cameraName, enabled, retentionDays, cancellationToken);

        public Task<Guid> CreateEventAsync(
            string cameraName,
            DateTimeOffset startedUtc,
            CancellationToken cancellationToken) =>
            _inner.CreateEventAsync(cameraName, startedUtc, cancellationToken);

        public Task CompleteEventAsync(
            Guid eventId,
            DateTimeOffset endedUtc,
            EventStatus status,
            CancellationToken cancellationToken,
            EventEndReason? endReason = null) =>
            _inner.CompleteEventAsync(eventId, endedUtc, status, cancellationToken, endReason);

        public Task AddFileAsync(
            RecordingFileRecord file,
            CancellationToken cancellationToken) =>
            _inner.AddFileAsync(file, cancellationToken);

        public Task SetFileStatusAsync(
            Guid fileId,
            RecordingFileStatus status,
            CancellationToken cancellationToken) =>
            _inner.SetFileStatusAsync(fileId, status, cancellationToken);

        public Task SetFileReadyAsync(
            Guid fileId,
            long bytes,
            CancellationToken cancellationToken) =>
            _inner.SetFileReadyAsync(fileId, bytes, cancellationToken);

        public Task<EventRecord?> GetEventAsync(Guid eventId, CancellationToken cancellationToken) =>
            _inner.GetEventAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<RecordingFileRecord>> GetFilesAsync(
            Guid eventId,
            CancellationToken cancellationToken) =>
            _inner.GetFilesAsync(eventId, cancellationToken);

        public Task<RecordingFileRecord?> GetReadyFileAsync(
            Guid fileId,
            CancellationToken cancellationToken) =>
            _inner.GetReadyFileAsync(fileId, cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListEventsAsync(
            string? cameraName,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            _inner.ListEventsAsync(cameraName, fromUtc, toUtc, offset, limit, cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListEventsByStatusAsync(
            EventStatus status,
            int limit,
            CancellationToken cancellationToken) =>
            _inner.ListEventsByStatusAsync(status, limit, cancellationToken);

        public Task<IReadOnlyList<FileEventItem>> ListFileEventsAsync(
            string? cameraName,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            _inner.ListFileEventsAsync(cameraName, fromUtc, toUtc, offset, limit, cancellationToken);

        public Task<long> GetRecordingBytesAsync(CancellationToken cancellationToken) =>
            _inner.GetRecordingBytesAsync(cancellationToken);

        public Task<IReadOnlyList<EventRecord>> ListDeletableEventsAsync(
            DateTimeOffset nowUtc,
            int limit,
            CancellationToken cancellationToken) =>
            _inner.ListDeletableEventsAsync(nowUtc, limit, cancellationToken);

        public Task AddCleanupAuditAsync(
            string reason,
            int eventCount,
            long freedBytes,
            CancellationToken cancellationToken) =>
            _inner.AddCleanupAuditAsync(reason, eventCount, freedBytes, cancellationToken);

        public async Task DeleteEventAsync(Guid eventId, CancellationToken cancellationToken)
        {
            DeleteOrder.Add(eventId);
            if (BeforeDeleteEvent is not null)
                await BeforeDeleteEvent(eventId);
            if (DeleteEventOverride is not null)
            {
                await DeleteEventOverride(eventId, cancellationToken);
                return;
            }

            await _inner.DeleteEventAsync(eventId, cancellationToken);
        }

        public Task<IReadOnlyList<RecordingFileRecord>> ListFilesByStatusAsync(
            RecordingFileStatus status,
            int limit,
            CancellationToken cancellationToken) =>
            _inner.ListFilesByStatusAsync(status, limit, cancellationToken);
    }
}

file static class CapacityTestExtensions
{
    public static long Gibibytes(this int value) => value * 1_073_741_824L;
}
