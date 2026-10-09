using System.Globalization;
using System.Text.RegularExpressions;
using ActivityRecordingService.Configuration;

namespace ActivityRecordingService.Ffmpeg;

public sealed record RollingSegment(string FilePath, long Bytes, TimeSpan Duration);

public sealed record EventSegmentReservation(
    IReadOnlyList<RollingSegment> CompletedSegments,
    string? ActiveSegmentPath);

public sealed class RollingSegmentManager
{
    private static readonly Regex SegmentNamePattern =
        new(@"^rolling_(\d{9})\.mp4$", RegexOptions.Compiled);

    private readonly TimeProvider _timeProvider;
    private readonly RecordingOptions _options;

    public RollingSegmentManager(TimeProvider timeProvider, RecordingOptions options)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.RollingSegmentSeconds <= 0)
        {
            throw new ArgumentException(
                "RollingSegmentSeconds must be positive.",
                nameof(options));
        }
    }

    public EventSegmentReservation BeginEvent(
        string rollingDirectory,
        string eventDirectory,
        DateTimeOffset eventStartedUtc)
    {
        ValidatePaths(rollingDirectory, eventDirectory);

        var segments = FindSegments(rollingDirectory);
        var completedSegments = new List<RollingSegment>();
        if (segments.Count == 0)
        {
            return new EventSegmentReservation(completedSegments, ActiveSegmentPath: null);
        }

        var active = segments[^1];
        var cutoffUtc = eventStartedUtc.AddSeconds(-_options.PreEventSeconds);
        foreach (var segment in segments.Take(segments.Count - 1))
        {
            if (File.GetLastWriteTimeUtc(segment.SourcePath) < cutoffUtc)
            {
                continue;
            }

            completedSegments.Add(MoveSegment(
                segment.SourcePath,
                segment.Sequence,
                eventDirectory));
        }

        return new EventSegmentReservation(
            completedSegments,
            active.SourcePath);
    }

    public EventSegmentReservation ReserveClosedSegments(
        string rollingDirectory,
        string eventDirectory)
    {
        ValidatePaths(rollingDirectory, eventDirectory);

        var segments = FindSegments(rollingDirectory);
        if (segments.Count == 0)
        {
            return new EventSegmentReservation([], ActiveSegmentPath: null);
        }

        var completedSegments = segments
            .Take(segments.Count - 1)
            .Select(segment => MoveSegment(segment.SourcePath, segment.Sequence, eventDirectory))
            .ToList();

        return new EventSegmentReservation(completedSegments, segments[^1].SourcePath);
    }

    public void DeleteUnreservedSegments(
        string rollingDirectory,
        DateTimeOffset eventStartedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollingDirectory);

        var rollingFullPath = Path.GetFullPath(rollingDirectory);
        var cutoffUtc = eventStartedUtc.AddSeconds(-_options.PreEventSeconds);
        foreach (var path in Directory.EnumerateFiles(rollingFullPath, "rolling_*.mp4"))
        {
            if (ParseSegment(path) is null ||
                File.GetLastWriteTimeUtc(path) >= cutoffUtc)
            {
                continue;
            }

            File.Delete(path);
        }
    }

    private static void ValidatePaths(string rollingDirectory, string eventDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventDirectory);
    }

    private static List<RollingFile> FindSegments(string rollingDirectory)
    {
        return Directory.EnumerateFiles(rollingDirectory, "rolling_*.mp4")
            .Select(path => (Path: path, Sequence: ParseSegment(path)))
            .Where(segment => segment.Sequence is not null)
            .Select(segment => new RollingFile(
                Path.GetFullPath(segment.Path),
                segment.Sequence!.Value))
            .OrderBy(segment => segment.Sequence)
            .ToList();
    }

    private static int? ParseSegment(string path)
    {
        var match = SegmentNamePattern.Match(Path.GetFileName(path));
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }

    private RollingSegment MoveSegment(
        string sourcePath,
        int sequence,
        string eventDirectory)
    {
        var destinationPath = Path.Combine(
            Path.GetFullPath(eventDirectory),
            Path.GetFileName(sourcePath));
        if (File.Exists(destinationPath))
        {
            throw new IOException(
                $"Cannot reserve segment because the destination already exists: {destinationPath}");
        }

        File.Move(sourcePath, destinationPath);
        return new RollingSegment(
            destinationPath,
            new FileInfo(destinationPath).Length,
            TimeSpan.FromSeconds(_options.RollingSegmentSeconds));
    }

    private sealed record RollingFile(string SourcePath, int Sequence);
}
