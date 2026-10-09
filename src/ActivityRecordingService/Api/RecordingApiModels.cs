namespace ActivityRecordingService.Api;

/// <summary>
/// Non-secret identity values shared by token issuance and JWT Bearer validation.
/// </summary>
public static class RecordingApiSecurity
{
    public const string JwtIssuer = "activity-recording-service";
    public const string JwtAudience = "activity-recording-service-clients";
}

public sealed record TokenRequest(string ApiKey);

public sealed record TokenResponse(
    string TokenType,
    string AccessToken,
    DateTimeOffset ExpiresAt);

public sealed record EventPageResponse(
    IReadOnlyList<EventResponse> Items,
    int Offset,
    int Limit);

public sealed record EventResponse(
    Guid EventId,
    string CameraName,
    string CameraDisplayName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    long FileBytes,
    int FileCount,
    string? EndReason,
    IReadOnlyList<RecordingFileResponse> Files);

public sealed record RecordingFileResponse(
    Guid FileId,
    Guid EventId,
    int PartIndex,
    long Bytes,
    string Status);

public sealed record PlaybackUrlResponse(
    Guid FileId,
    int PartIndex,
    string Url,
    DateTimeOffset ExpiresAt);

public sealed record FilePageResponse(
    IReadOnlyList<FileListItemResponse> Items,
    int Offset,
    int Limit);

public sealed record FileListItemResponse(
    Guid FileId,
    Guid EventId,
    string CameraName,
    string CameraDisplayName,
    int PartIndex,
    long Bytes,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? PlaybackUrl,
    DateTimeOffset? PlaybackExpiresAt);

public sealed record HealthResponse(
    string Status,
    string ServiceState,
    string CameraState,
    string RecorderState,
    long DiskAvailableBytes,
    bool DatabaseReachable,
    bool NewEventsAllowed,
    string StartupState,
    string CleanupState,
    string? BlockingReason)
;

public interface IRecordingHealthMonitor
{
    Task<HealthResponse> GetHealthResponseAsync(CancellationToken cancellationToken);
}
