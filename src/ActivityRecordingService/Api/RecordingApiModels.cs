namespace ActivityRecordingService.Api;

using System.Text.Json.Serialization;

/// <summary>
/// 51banshou 统一响应包装，对齐 management-api 的 ResponseResult：
/// 成功 code=0、载荷在 data；字段名 error_description 保持后端惯例的下划线风格。
/// </summary>
public sealed record ApiResult<T>(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription,
    [property: JsonPropertyName("trackId")] string TrackId,
    [property: JsonPropertyName("data")] T? Data)
{
    public static ApiResult<T> Ok(T data) => new(0, null, null, Guid.NewGuid().ToString(), data);
}

public sealed record CameraResponse(
    string Name,
    string DisplayName);

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
