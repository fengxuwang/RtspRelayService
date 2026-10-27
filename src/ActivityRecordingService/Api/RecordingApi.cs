using System.Globalization;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Recording;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ActivityRecordingService.Api;

public static class RecordingApi
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public static void Map(WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        var api = app.MapGroup("/recordings/api");

        api.MapGet("/cameras", (IOptions<ActivityRecordingOptions> options) =>
            Results.Ok(ApiResult<CameraResponse[]>.Ok(options.Value.Cameras
                .Select(camera => new CameraResponse(
                    camera.Name,
                    ResolveCameraDisplayName(camera.Name, options.Value)))
                .ToArray())))
            .RequireAuthorization();

        api.MapGet("/events", async Task<IResult> (
            string? camera,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            IEventRepository repository,
            IOptions<ActivityRecordingOptions> options,
            CancellationToken cancellationToken,
            int offset = 0,
            int limit = DefaultPageSize) =>
        {
            if (offset < 0 || offset > int.MaxValue - limit || limit is < 1 or > MaxPageSize)
                return Results.BadRequest();
            if (fromUtc > toUtc)
                return Results.BadRequest();

            var activeEvents = new List<EventRecord>();
            var repositoryOffset = 0;
            while (activeEvents.Count < offset + limit)
            {
                var batch = await repository.ListEventsAsync(
                    camera,
                    fromUtc,
                    toUtc,
                    repositoryOffset,
                    MaxPageSize,
                    cancellationToken);
                if (batch.Count == 0)
                    break;

                // 列表只展示已结束的事件：删除的不可见，进行中的（Recording）对回放无意义
                activeEvents.AddRange(batch.Where(item =>
                    item.Status is not (EventStatus.Deleted or EventStatus.Recording)));
                repositoryOffset += batch.Count;
                if (batch.Count < MaxPageSize)
                    break;
            }

            var responses = new List<EventResponse>(limit);
            foreach (var item in activeEvents.Skip(offset).Take(limit))
            {
                var files = await repository.GetFilesAsync(item.EventId, cancellationToken);
                responses.Add(ToResponse(item, files.Select(ToFileResponse).ToArray(), options.Value));
            }

            return Results.Ok(ApiResult<EventPageResponse>.Ok(new EventPageResponse(responses, offset, limit)));
        }).RequireAuthorization();

        api.MapGet("/events/{eventId}", async Task<IResult> (
            Guid eventId,
            IEventRepository repository,
            IOptions<ActivityRecordingOptions> options,
            CancellationToken cancellationToken) =>
        {
            var item = await repository.GetEventAsync(eventId, cancellationToken);
            if (item is null || item.Status == EventStatus.Deleted)
                return Results.NotFound();

            var files = await repository.GetFilesAsync(eventId, cancellationToken);
            return Results.Ok(ApiResult<EventResponse>.Ok(ToResponse(item, files.Select(ToFileResponse).ToArray(), options.Value)));
        }).RequireAuthorization();

        api.MapGet("/events/{eventId}/playback-urls", async Task<IResult> (
            Guid eventId,
            IEventRepository repository,
            PlaybackUrlSigner signer,
            TimeProvider timeProvider,
            IOptions<ActivityRecordingOptions> options,
            CancellationToken cancellationToken) =>
        {
            var item = await repository.GetEventAsync(eventId, cancellationToken);
            if (item is null || item.Status == EventStatus.Deleted)
                return Results.NotFound();

            var files = await repository.GetFilesAsync(eventId, cancellationToken);
            var expiresAtUtc = timeProvider.GetUtcNow().AddMinutes(
                options.Value.Api.PlaybackUrlLifetimeMinutes);
            var urls = files
                .Where(file => file.Status == RecordingFileStatus.Ready)
                .Select(file => new PlaybackUrlResponse(
                    file.FileId,
                    file.PartIndex,
                    signer.CreateUrl(options.Value.Api.PublicBasePath, file.FileId, expiresAtUtc),
                    expiresAtUtc))
                .ToArray();
            return Results.Ok(ApiResult<PlaybackUrlResponse[]>.Ok(urls));
        }).RequireAuthorization();

        api.MapGet("/files", async Task<IResult> (
            string? camera,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            IEventRepository repository,
            PlaybackUrlSigner signer,
            TimeProvider timeProvider,
            IOptions<ActivityRecordingOptions> options,
            CancellationToken cancellationToken,
            int offset = 0,
            int limit = DefaultPageSize) =>
        {
            if (offset < 0 || offset > int.MaxValue - limit || limit is < 1 or > MaxPageSize)
                return Results.BadRequest();
            if (fromUtc > toUtc)
                return Results.BadRequest();

            var apiOptions = options.Value;
            var expiresAtUtc = timeProvider.GetUtcNow().AddMinutes(
                apiOptions.Api.PlaybackUrlLifetimeMinutes);
            var items = await repository.ListFileEventsAsync(
                camera,
                fromUtc,
                toUtc,
                offset,
                limit,
                cancellationToken);
            var responses = new List<FileListItemResponse>(items.Count);
            foreach (var item in items)
            {
                var playable = item.FileStatus == RecordingFileStatus.Ready;
                responses.Add(new FileListItemResponse(
                    item.FileId,
                    item.EventId,
                    item.CameraName,
                    ResolveCameraDisplayName(item.CameraName, apiOptions),
                    item.PartIndex,
                    item.Bytes,
                    item.FileStatus.ToString(),
                    item.StartedUtc,
                    item.EndedUtc,
                    playable ? signer.CreateUrl(apiOptions.Api.PublicBasePath, item.FileId, expiresAtUtc) : null,
                    playable ? expiresAtUtc : null));
            }

            return Results.Ok(ApiResult<FilePageResponse>.Ok(new FilePageResponse(responses, offset, limit)));
        }).RequireAuthorization();

        // 默认内联返回（无 Content-Disposition），浏览器播放器可边下边播；
        // dl=1 时改为附件下载并附带友好文件名。
        api.MapGet("/files/{fileId}/content", async Task<IResult> (
            Guid fileId,
            string? exp,
            string? nonce,
            string? signature,
            string? dl,
            PlaybackUrlSigner signer,
            IEventRepository repository,
            IOptions<ActivityRecordingOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (exp is null ||
                !long.TryParse(exp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAtUnix) ||
                nonce is null ||
                signature is null ||
                !signer.Validate(fileId, expiresAtUnix, nonce, signature))
            {
                return Results.Unauthorized();
            }

            var file = await repository.GetReadyFileAsync(fileId, cancellationToken);
            if (file is null)
                return Results.NotFound();

            if (!TryResolveConfinedPath(options.Value.RecordingRoot, file.RelativePath, out var fullPath) ||
                !File.Exists(fullPath))
            {
                return Results.NotFound();
            }

            var item = await repository.GetEventAsync(file.EventId, cancellationToken);
            if (item is null || item.Status == EventStatus.Deleted)
                return Results.NotFound();

            if (dl == "1")
            {
                return TypedResults.PhysicalFile(
                    fullPath,
                    "video/mp4",
                    CreateDownloadName(
                        ResolveCameraDisplayName(item.CameraName, options.Value),
                        item,
                        file),
                    enableRangeProcessing: true);
            }

            return TypedResults.PhysicalFile(
                fullPath,
                "video/mp4",
                enableRangeProcessing: true);
        });

        api.MapGet("/health", async Task<IResult> (
            IRecordingHealthMonitor monitor,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await monitor.GetHealthResponseAsync(cancellationToken));
        });
    }

    private static bool TryResolveConfinedPath(
        string recordingRoot,
        string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        if (relativePath.Split('/', '\\').Any(segment => segment == ".."))
            return false;

        try
        {
            var normalizedRoot = Path.GetFullPath(recordingRoot);
            var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(rootWithSeparator, comparison))
                return false;
            if (ContainsSymbolicLink(normalizedRoot, candidate))
                return false;

            fullPath = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool ContainsSymbolicLink(string normalizedRoot, string candidate)
    {
        var current = normalizedRoot;
        var relativePath = Path.GetRelativePath(normalizedRoot, candidate);
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment is "." or "..")
                return true;

            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                if (new DirectoryInfo(current).LinkTarget is not null)
                    return true;
            }
            else if (new FileInfo(current).LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static EventResponse ToResponse(
        EventRecord item,
        IReadOnlyList<RecordingFileResponse> files,
        ActivityRecordingOptions options) =>
        new(
            item.EventId,
            item.CameraName,
            ResolveCameraDisplayName(item.CameraName, options),
            item.StartedUtc,
            item.EndedUtc,
            item.Status.ToString(),
            item.FileBytes,
            item.FileCount,
            item.EndReason?.ToString(),
            files);

    private static string ResolveCameraDisplayName(
        string cameraName,
        ActivityRecordingOptions options) =>
        options.Cameras?.FirstOrDefault(camera =>
                string.Equals(camera.Name, cameraName, StringComparison.Ordinal)) is { } camera
            && !string.IsNullOrWhiteSpace(camera.DisplayName)
                ? camera.DisplayName
                : cameraName;

    private static RecordingFileResponse ToFileResponse(RecordingFileRecord file) =>
        new(
            file.FileId,
            file.EventId,
            file.PartIndex,
            file.Bytes,
            file.Status.ToString());

    // 下载文件名：{摄像头显示名}-{开始时间}-{结束时间}.mp4，时间取中国标准时间 yyyyMMddHHmmss。
    // 优先使用分节自身起止时间；旧数据无分节时间时回退事件时间，此时多分节起止相同，
    // 第 2 节起追加 -part{N} 避免重名。
    private static string CreateDownloadName(
        string cameraDisplayName,
        EventRecord item,
        RecordingFileRecord file)
    {
        var cameraName = new string(cameraDisplayName
            .Where(character => !Path.GetInvalidFileNameChars().Contains(character))
            .ToArray());
        if (cameraName.Length > 40)
            cameraName = cameraName[..40];

        var started = FormatDownloadTime(file.StartedUtc ?? item.StartedUtc);
        var ended = FormatDownloadTime(file.EndedUtc ?? item.EndedUtc ?? file.StartedUtc ?? item.StartedUtc);
        var partSuffix = file.PartIndex > 0 && string.Equals(started, ended, StringComparison.Ordinal)
            ? $"-part{file.PartIndex + 1}"
            : string.Empty;
        return $"{cameraName}-{started}-{ended}{partSuffix}.mp4";
    }

    private static string FormatDownloadTime(DateTimeOffset value) =>
        value.ToOffset(ChinaStandardTimeJsonConverter.Offset)
            .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}
