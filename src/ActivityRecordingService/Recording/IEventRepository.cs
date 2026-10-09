namespace ActivityRecordingService.Recording;

using ActivityRecordingService.Detection;

public interface IEventRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task UpsertCameraAsync(
        string cameraName,
        bool enabled,
        int? retentionDays,
        CancellationToken cancellationToken);

    Task<Guid> CreateEventAsync(
        string cameraName,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken);

    Task CompleteEventAsync(
        Guid eventId,
        DateTimeOffset endedUtc,
        EventStatus status,
        CancellationToken cancellationToken,
        EventEndReason? endReason = null);

    Task AddFileAsync(
        RecordingFileRecord file,
        CancellationToken cancellationToken);

    Task SetFileStatusAsync(
        Guid fileId,
        RecordingFileStatus status,
        CancellationToken cancellationToken);

    Task SetFileReadyAsync(
        Guid fileId,
        long bytes,
        CancellationToken cancellationToken);

    Task<EventRecord?> GetEventAsync(
        Guid eventId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RecordingFileRecord>> GetFilesAsync(
        Guid eventId,
        CancellationToken cancellationToken);

    Task<RecordingFileRecord?> GetReadyFileAsync(
        Guid fileId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EventRecord>> ListEventsAsync(
        string? cameraName,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EventRecord>> ListEventsByStatusAsync(
        EventStatus status,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FileEventItem>> ListFileEventsAsync(
        string? cameraName,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<long> GetRecordingBytesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<EventRecord>> ListDeletableEventsAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken);

    Task AddCleanupAuditAsync(
        string reason,
        int eventCount,
        long freedBytes,
        CancellationToken cancellationToken);

    Task DeleteEventAsync(
        Guid eventId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RecordingFileRecord>> ListFilesByStatusAsync(
        RecordingFileStatus status,
        int limit,
        CancellationToken cancellationToken);
}
