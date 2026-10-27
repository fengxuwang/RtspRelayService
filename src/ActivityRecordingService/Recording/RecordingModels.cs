namespace ActivityRecordingService.Recording;

using ActivityRecordingService.Detection;

public enum EventStatus
{
    Recording,
    Completed,
    Failed,
    Deleted
}

public enum RecordingFileStatus
{
    Reserved,
    Ready,
    Failed,
    Deleted
}

public sealed record EventRecord(
    Guid EventId,
    string CameraName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    EventStatus Status,
    long FileBytes,
    int FileCount,
    EventEndReason? EndReason = null);

public sealed record RecordingFileRecord(
    Guid FileId,
    Guid EventId,
    int PartIndex,
    string RelativePath,
    long Bytes,
    RecordingFileStatus Status,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? EndedUtc = null);

public sealed record FileEventItem(
    Guid FileId,
    Guid EventId,
    string CameraName,
    int PartIndex,
    long Bytes,
    RecordingFileStatus FileStatus,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    EventStatus EventStatus);
