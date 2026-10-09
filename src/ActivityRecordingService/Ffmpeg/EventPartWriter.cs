using System.Text;
using System.Text.RegularExpressions;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;

namespace ActivityRecordingService.Ffmpeg;

public sealed record EventPartRequest(
    Guid EventId,
    string CameraName,
    int PartIndex,
    string EventDirectory,
    IReadOnlyList<RollingSegment> Segments);

public sealed record EventPartResult(string FinalPath, long Bytes);

public sealed class EventPartWriteException(
    string message,
    string diagnostics,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Diagnostics { get; } = diagnostics;
}

public sealed class EventPartWriter
{
    private const int StandardErrorTailLines = 16;
    private const int StandardErrorLineLength = 512;
    private static readonly Regex UrlPattern = new(
        @"\b[a-z][a-z0-9+.-]*://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LocalPathPattern = new(
        @"(?<![\w:/])/[^\s""'<>]+|(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s""'<>]+|\\{2}[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _ffmpegPath;
    private readonly IProcessRunner _processRunner;

    public EventPartWriter(
        string ffmpegPath,
        IProcessRunner processRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(processRunner);

        _ffmpegPath = ffmpegPath;
        _processRunner = processRunner;
    }

    public async Task<EventPartResult> WriteAsync(
        EventPartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        Directory.CreateDirectory(request.EventDirectory);
        var manifestPath = Path.Combine(
            request.EventDirectory,
            $"part-{request.PartIndex:000}.ffconcat");
        var temporaryPath = Path.Combine(
            request.EventDirectory,
            $"part-{request.PartIndex:000}.tmp.mp4");
        var finalPath = Path.Combine(
            request.EventDirectory,
            $"part-{request.PartIndex:000}.mp4");

        await File.WriteAllTextAsync(
            manifestPath,
            CreateManifest(request.Segments),
            Utf8NoBom,
            cancellationToken);

        var knownPaths = new[]
        {
            request.EventDirectory,
            manifestPath,
            temporaryPath,
            finalPath
        }
            .Concat(request.Segments.Select(segment => segment.FilePath))
            .ToArray();
        var standardErrorTail = new BoundedRedactedTail(knownPaths);
        IManagedProcess? process = null;
        try
        {
            process = await _processRunner.StartAsync(
                new FfmpegCommand(_ffmpegPath,
                [
                    "-y",
                    "-f",
                    "concat",
                    "-safe",
                    "0",
                    "-i",
                    manifestPath,
                    "-map",
                    "0:v:0",
                    "-map",
                    "0:a:0?",
                    "-c:v",
                    "copy",
                    "-c:a",
                    "copy",
                    "-movflags",
                    "+faststart",
                    "-f",
                    "mp4",
                    temporaryPath
                ]),
                standardErrorTail.Add,
                cancellationToken);

            int exitCode;
            try
            {
                exitCode = await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await KillWithoutThrowingAsync(process);
                throw;
            }

            if (exitCode != 0)
            {
                var diagnostics = standardErrorTail.ToString();
                throw new EventPartWriteException(
                    $"FFmpeg failed for event {request.EventId} part {request.PartIndex} with exit code {exitCode}. Diagnostics: {diagnostics}",
                    diagnostics);
            }

            if (!File.Exists(temporaryPath))
            {
                var diagnostics = standardErrorTail.ToString();
                throw new EventPartWriteException(
                    $"FFmpeg did not create event {request.EventId} part {request.PartIndex}. Diagnostics: {diagnostics}",
                    diagnostics);
            }

            try
            {
                File.Move(temporaryPath, finalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var diagnostics = standardErrorTail.ToString();
                throw new EventPartWriteException(
                    $"Failed to rename event {request.EventId} part {request.PartIndex}. Diagnostics: {diagnostics}",
                    diagnostics,
                    exception);
            }

            var bytes = new FileInfo(finalPath).Length;
            TryDelete(manifestPath);
            return new EventPartResult(finalPath, bytes);
        }
        finally
        {
            if (process is not null)
                await process.DisposeAsync();
        }
    }

    private static string CreateManifest(IReadOnlyList<RollingSegment> segments)
    {
        var builder = new StringBuilder("ffconcat version 1.0\n");
        foreach (var segment in segments)
        {
            var escapedPath = segment.FilePath.Replace("'", "'\\''", StringComparison.Ordinal);
            builder.Append("file '").Append(escapedPath).Append("'\n");
        }

        return builder.ToString();
    }

    private static void ValidateRequest(EventPartRequest request)
    {
        if (request.EventId == Guid.Empty)
            throw new ArgumentException("Event ID is required.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CameraName);
        if (request.PartIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Part index must not be negative.");
        if (request.Segments.Count == 0)
            throw new ArgumentException("At least one segment is required.", nameof(request));

        if (!Path.IsPathRooted(request.EventDirectory))
            throw new ArgumentException("Event directory must be absolute.", nameof(request));

        foreach (var segment in request.Segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
            if (segment.Bytes < 0)
                throw new ArgumentException("Segment bytes must not be negative.", nameof(request));
            if (segment.Duration <= TimeSpan.Zero)
                throw new ArgumentException("Segment duration must be positive.", nameof(request));

            if (!Path.IsPathRooted(segment.FilePath))
                throw new ArgumentException("Segment paths must be absolute.", nameof(request));
            if (segment.FilePath.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("Segment paths must not contain line breaks.", nameof(request));
        }
    }

    private sealed class BoundedRedactedTail(string[] knownPaths)
    {
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            var normalized = line.ReplaceLineEndings(" ");
            var redacted = UrlPattern.Replace(normalized, "[redacted-url]");
            foreach (var path in knownPaths.OrderByDescending(path => path.Length))
                redacted = redacted.Replace(path, "<path>", StringComparison.OrdinalIgnoreCase);

            redacted = LocalPathPattern.Replace(redacted, "<path>");
            if (redacted.Length > StandardErrorLineLength)
                redacted = redacted[..StandardErrorLineLength];

            _lines.Enqueue(redacted);
            while (_lines.Count > StandardErrorTailLines)
                _lines.Dequeue();
        }

        public override string ToString() => string.Join('\n', _lines);
    }

    private static async Task KillWithoutThrowingAsync(IManagedProcess process)
    {
        try
        {
            await process.KillTreeAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
