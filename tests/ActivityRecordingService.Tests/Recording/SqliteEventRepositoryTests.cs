using System.Data;
using System.Reflection;
using ActivityRecordingService.Recording;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ActivityRecordingService.Tests.Recording;

public sealed class SqliteEventRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset StartedUtc =
        DateTimeOffset.Parse("2026-09-22T08:00:00.000Z");

    private readonly DirectoryInfo _databaseDirectory =
        Directory.CreateTempSubdirectory("sqlite-event-repository-");

    static SqliteEventRepositoryTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [Fact]
    public async Task Initialize_CreatesSchemaIdempotently()
    {
        await using var repository = await CreateRepositoryAsync();
        await repository.InitializeAsync(CancellationToken.None);

        await repository.UpsertCameraAsync("camera-a", true, null, CancellationToken.None);
        var eventId = await repository.CreateEventAsync(
            "camera-a",
            StartedUtc,
            CancellationToken.None);

        (await repository.GetEventAsync(eventId, CancellationToken.None))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Dispose_UsesUnpooledWalConnectionForDeterministicCleanup()
    {
        var repository = await CreateRepositoryAsync();
        var connection = GetActiveConnection(repository);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode";
            var journalMode = await command.ExecuteScalarAsync();

            journalMode.Should().Be("wal");
            connection.ConnectionString.Should().Contain(
                "Pooling=False",
                "the long-lived repository must not leave database handles in the SQLite pool");
        }

        await repository.DisposeAsync();

        foreach (var databaseFile in Directory.GetFiles(_databaseDirectory.FullName))
            File.Delete(databaseFile);
    }

    [Fact]
    public async Task CreateCompleteAndListEvent_RoundTripsMetadata()
    {
        var startedWithOffset = StartedUtc.ToOffset(TimeSpan.FromHours(2));
        var endedWithOffset = StartedUtc.AddMinutes(5).ToOffset(TimeSpan.FromHours(2));
        await using var repository = await CreateRepositoryAsync();
        await repository.UpsertCameraAsync("camera-a", true, null, CancellationToken.None);
        var eventId = await repository.CreateEventAsync(
            "camera-a",
            startedWithOffset,
            CancellationToken.None);

        await repository.CompleteEventAsync(
            eventId,
            endedWithOffset,
            EventStatus.Completed,
            CancellationToken.None);

        var expected = new EventRecord(
            eventId,
            "camera-a",
            StartedUtc,
            StartedUtc.AddMinutes(5),
            EventStatus.Completed,
            FileBytes: 0,
            FileCount: 0);
        var actual = await repository.GetEventAsync(eventId, CancellationToken.None);
        actual.Should().Be(expected);
        actual!.StartedUtc.Offset.Should().Be(TimeSpan.Zero);
        actual.EndedUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
        (await repository.ListEventsAsync(null, null, null, 0, 10, CancellationToken.None))
            .Should().ContainSingle().Which.Should().Be(expected);
        (await repository.GetRecordingBytesAsync(CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task AddReadyFile_IncrementsEventSizeAndCount()
    {
        await using var repository = await CreateRepositoryAsync();
        var eventId = await CreateEventAsync(repository, "camera-a", StartedUtc);
        var firstFile = new RecordingFileRecord(
            Guid.NewGuid(),
            eventId,
            PartIndex: 0,
            RelativePath: "events/camera-a'; DROP TABLE events;--/part-000.mp4",
            Bytes: 100,
            RecordingFileStatus.Ready);
        var secondFile = new RecordingFileRecord(
            Guid.NewGuid(),
            eventId,
            PartIndex: 1,
            RelativePath: "events/camera-a/part-001.mp4",
            Bytes: 28,
            RecordingFileStatus.Ready);

        await repository.AddFileAsync(firstFile, CancellationToken.None);
        await repository.AddFileAsync(secondFile, CancellationToken.None);

        var expectedEvent = new EventRecord(
            eventId,
            "camera-a",
            StartedUtc,
            null,
            EventStatus.Recording,
            FileBytes: 128,
            FileCount: 2);
        (await repository.GetEventAsync(eventId, CancellationToken.None))
            .Should().Be(expectedEvent);
        (await repository.GetFilesAsync(eventId, CancellationToken.None))
            .Should().ContainInOrder(firstFile, secondFile);
        (await repository.GetReadyFileAsync(firstFile.FileId, CancellationToken.None))
            .Should().Be(firstFile);
        (await repository.GetRecordingBytesAsync(CancellationToken.None))
            .Should().Be(128);
    }

    [Fact]
    public async Task SetFileStatus_TransitionsFileAndRecomputesAggregates()
    {
        await using var repository = await CreateRepositoryAsync();
        var eventId = await CreateEventAsync(repository, "camera-a", StartedUtc);
        var file = CreateFile(eventId, partIndex: 0, bytes: 123);
        await repository.AddFileAsync(file, CancellationToken.None);

        await repository.SetFileStatusAsync(
            file.FileId,
            RecordingFileStatus.Failed,
            CancellationToken.None);

        (await repository.GetReadyFileAsync(file.FileId, CancellationToken.None))
            .Should().BeNull();
        (await repository.ListFilesByStatusAsync(RecordingFileStatus.Failed, 10, CancellationToken.None))
            .Should().ContainSingle().Which.Should().Be(file with { Status = RecordingFileStatus.Failed });
        (await repository.GetEventAsync(eventId, CancellationToken.None))
            .Should().Be(new EventRecord(
                eventId,
                "camera-a",
                StartedUtc,
                null,
                EventStatus.Recording,
                FileBytes: 0,
                FileCount: 0));
        (await repository.GetRecordingBytesAsync(CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task ListEvents_FiltersCameraAndTimeRange()
    {
        await using var repository = await CreateRepositoryAsync();
        await repository.UpsertCameraAsync("camera-a", true, null, CancellationToken.None);
        await repository.UpsertCameraAsync("camera-b", true, null, CancellationToken.None);
        var first = await repository.CreateEventAsync(
            "camera-a", StartedUtc, CancellationToken.None);
        await repository.CreateEventAsync(
            "camera-b", StartedUtc.AddMinutes(5), CancellationToken.None);
        var third = await repository.CreateEventAsync(
            "camera-a", StartedUtc.AddMinutes(10), CancellationToken.None);

        var events = await repository.ListEventsAsync(
            "camera-a",
            StartedUtc.AddMinutes(1),
            StartedUtc.AddMinutes(10),
            offset: 0,
            limit: 10,
            CancellationToken.None);

        events.Should().ContainSingle().Which.EventId.Should().Be(third);
        events[0].EventId.Should().NotBe(first);
    }

    [Fact]
    public async Task ListEvents_AppliesPaginationNewestFirst()
    {
        await using var repository = await CreateRepositoryAsync();
        var first = await CreateEventAsync(repository, "camera-a", StartedUtc);
        var second = await CreateEventAsync(repository, "camera-a", StartedUtc.AddMinutes(1));
        var third = await CreateEventAsync(repository, "camera-a", StartedUtc.AddMinutes(2));

        var events = await repository.ListEventsAsync(
            null,
            null,
            null,
            offset: 1,
            limit: 2,
            CancellationToken.None);

        events.Should().HaveCount(2);
        events[0].EventId.Should().Be(second);
        events[1].EventId.Should().Be(first);
        events.Select(@event => @event.EventId).Should().NotContain(third);
    }

    [Fact]
    public async Task DeleteEvent_RemovesFileRowsAndMarksEventDeleted()
    {
        await using var repository = await CreateRepositoryAsync();
        var eventId = await CreateEventAsync(repository, "camera-a", StartedUtc);
        await repository.AddFileAsync(
            CreateFile(eventId, partIndex: 0, bytes: 512),
            CancellationToken.None);
        await repository.CompleteEventAsync(
            eventId,
            StartedUtc.AddMinutes(1),
            EventStatus.Completed,
            CancellationToken.None);

        await repository.DeleteEventAsync(eventId, CancellationToken.None);

        var deletedEvent = await repository.GetEventAsync(eventId, CancellationToken.None);
        deletedEvent.Should().NotBeNull();
        deletedEvent!.Status.Should().Be(EventStatus.Deleted);
        deletedEvent.FileBytes.Should().Be(0);
        deletedEvent.FileCount.Should().Be(0);
        (await repository.GetFilesAsync(eventId, CancellationToken.None))
            .Should().BeEmpty();
        (await repository.ListFilesByStatusAsync(RecordingFileStatus.Ready, 10, CancellationToken.None))
            .Should().BeEmpty();
        (await repository.GetRecordingBytesAsync(CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task ListDeletableEvents_HonorsCameraRetentionOverride()
    {
        await using var repository = await CreateRepositoryAsync(defaultRetentionDays: 7);
        await repository.UpsertCameraAsync(
            "short-retention", true, retentionDays: 3, CancellationToken.None);
        await repository.UpsertCameraAsync(
            "default-retention", true, retentionDays: null, CancellationToken.None);
        var shortEvent = await repository.CreateEventAsync(
            "short-retention",
            StartedUtc,
            CancellationToken.None);
        await CreateEventAsync(repository, "default-retention", StartedUtc);
        var activeEvent = await repository.CreateEventAsync(
            "short-retention",
            StartedUtc.AddMinutes(1),
            CancellationToken.None);
        await repository.CompleteEventAsync(
            shortEvent,
            StartedUtc.AddMinutes(1),
            EventStatus.Completed,
            CancellationToken.None);
        (await repository.GetEventAsync(shortEvent, CancellationToken.None))!
            .Status.Should().Be(EventStatus.Completed);

        var deletable = await repository.ListDeletableEventsAsync(
            StartedUtc.AddDays(3).AddMinutes(2),
            limit: 10,
            CancellationToken.None);

        deletable.Should().ContainSingle().Which.EventId.Should().Be(shortEvent);
    }

    [Fact]
    public async Task AddCleanupAudit_RoundTripsReasonCountAndBytes()
    {
        await using var repository = await CreateRepositoryAsync();

        await repository.AddCleanupAuditAsync(
            "retention; delete from events; --",
            eventCount: 2,
            freedBytes: 1_024,
            CancellationToken.None);

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reason, event_count, freed_bytes
            FROM cleanup_audits
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var hasRow = await reader.ReadAsync();

        hasRow.Should().BeTrue();
        reader.GetString(0).Should().Be("retention; delete from events; --");
        reader.GetInt32(1).Should().Be(2);
        reader.GetInt64(2).Should().Be(1_024);
    }

    private string DatabasePath =>
        Path.Join(_databaseDirectory.FullName, "events.db");

    private async Task<SqliteEventRepository> CreateRepositoryAsync(
        int defaultRetentionDays = 7)
    {
        var repository = new SqliteEventRepository(
            DatabasePath,
            defaultRetentionDays);
        await repository.InitializeAsync(CancellationToken.None);
        return repository;
    }

    private static async Task<Guid> CreateEventAsync(
        IEventRepository repository,
        string cameraName,
        DateTimeOffset startedUtc)
    {
        await repository.UpsertCameraAsync(cameraName, true, null, CancellationToken.None);
        return await repository.CreateEventAsync(cameraName, startedUtc, CancellationToken.None);
    }

    private static RecordingFileRecord CreateFile(
        Guid eventId,
        int partIndex,
        long bytes) => new(
            Guid.NewGuid(),
            eventId,
            partIndex,
            $"events/camera-a/part-{partIndex:000}.mp4",
            bytes,
            RecordingFileStatus.Ready);

    private static SqliteConnection GetActiveConnection(
        SqliteEventRepository repository) =>
        typeof(SqliteEventRepository)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(repository) as SqliteConnection ??
        throw new InvalidOperationException("The repository connection was not initialized.");

    public void Dispose() => _databaseDirectory.Delete(recursive: true);
}
