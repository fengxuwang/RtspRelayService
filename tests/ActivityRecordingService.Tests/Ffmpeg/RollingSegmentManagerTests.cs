using ActivityRecordingService.Configuration;
using ActivityRecordingService.Ffmpeg;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Ffmpeg;

public sealed class RollingSegmentManagerTests : IDisposable
{
    private static readonly DateTimeOffset TestTimeUtc =
        DateTimeOffset.Parse("2026-09-22T10:00:00.000Z");

    private readonly FixedTimeProvider _time = new(TestTimeUtc);
    private readonly DirectoryInfo _rollingDirectory =
        Directory.CreateTempSubdirectory("rolling-segments-");

    public void Dispose()
    {
        _rollingDirectory.Delete(recursive: true);
    }

    [Fact]
    public void BeginEvent_MovesCompletedSegmentsAndKeepsHighestSequenceActive()
    {
        var (manager, eventDirectory) = CreateManager();
        var sources = Enumerable.Range(0, 6)
            .Select(sequence => CreateSegment(sequence, bytes: (byte)(sequence + 1)))
            .ToArray();

        var reservation = manager.BeginEvent(
            _rollingDirectory.FullName,
            eventDirectory,
            TestTimeUtc);

        var destinations = sources
            .Take(5)
            .Select(Path.GetFileName)
            .Select(name => Path.Combine(eventDirectory, name!))
            .ToArray();
        reservation.CompletedSegments.Select(segment => segment.FilePath)
            .Should().Equal(destinations);
        reservation.CompletedSegments.Should().Equal(sources.Take(5).Select((source, index) =>
            new RollingSegment(
                destinations[index],
                index + 1,
                TimeSpan.FromSeconds(5))));
        reservation.ActiveSegmentPath.Should().Be(sources[5]);

        sources.Take(5).Should().OnlyContain(path => !File.Exists(path));
        destinations.Should().OnlyContain(path => File.Exists(path));
        File.Exists(sources[5]).Should().BeTrue();
    }

    [Fact]
    public void BeginEvent_ReservesOnlyCompletedSegmentsInsidePreEventWindow()
    {
        var (manager, eventDirectory) = CreateManager();
        var oldSources = new[]
        {
            CreateSegment(0, bytes: 1, TestTimeUtc.AddSeconds(-55)),
            CreateSegment(1, bytes: 2, TestTimeUtc.AddSeconds(-50)),
            CreateSegment(2, bytes: 3, TestTimeUtc.AddSeconds(-45))
        };
        var sources = new[]
        {
            CreateSegment(3, bytes: 4, TestTimeUtc.AddSeconds(-25)),
            CreateSegment(4, bytes: 5, TestTimeUtc.AddSeconds(-20)),
            CreateSegment(5, bytes: 6, TestTimeUtc.AddSeconds(-15)),
            CreateSegment(6, bytes: 7, TestTimeUtc.AddSeconds(-10)),
            CreateSegment(7, bytes: 8, TestTimeUtc.AddSeconds(-5))
        };

        var reservation = manager.BeginEvent(
            _rollingDirectory.FullName,
            eventDirectory,
            TestTimeUtc);

        var destinations = sources
            .Take(4)
            .Select(Path.GetFileName)
            .Select(name => Path.Combine(eventDirectory, name!))
            .ToArray();
        reservation.CompletedSegments.Select(segment => segment.FilePath)
            .Should().Equal(destinations);
        reservation.ActiveSegmentPath.Should().Be(sources[4]);
        oldSources.Should().OnlyContain(path => File.Exists(path));
        sources.Take(4).Should().OnlyContain(path => !File.Exists(path));
        File.Exists(sources[4]).Should().BeTrue();
    }

    [Fact]
    public void BeginEvent_IgnoresMalformedAndNonSegmentFiles()
    {
        var (manager, eventDirectory) = CreateManager();
        var active = CreateSegment(7, bytes: 7);
        File.WriteAllText(Path.Combine(_rollingDirectory.FullName, "rolling_0000000000.mp4"), "too long");
        File.WriteAllText(Path.Combine(_rollingDirectory.FullName, "rolling_not-a-segment.mp4"), "malformed");
        File.WriteAllText(Path.Combine(_rollingDirectory.FullName, "other_000000006.mp4"), "unrelated");

        var reservation = manager.BeginEvent(
            _rollingDirectory.FullName,
            eventDirectory,
            TestTimeUtc);

        reservation.CompletedSegments.Should().BeEmpty();
        reservation.ActiveSegmentPath.Should().Be(active);
    }

    [Fact]
    public void BeginEvent_ThrowsWhenCompletedDestinationExists()
    {
        var (manager, eventDirectory) = CreateManager();
        var source = CreateSegment(0, bytes: 1);
        CreateSegment(1, bytes: 2);
        var destination = Path.Combine(eventDirectory, Path.GetFileName(source));
        File.WriteAllText(destination, "reserved");

        var move = () => manager.BeginEvent(
            _rollingDirectory.FullName,
            eventDirectory,
            TestTimeUtc);

        move.Should().Throw<IOException>().WithMessage($"*{destination}*");
        File.Exists(source).Should().BeTrue();
        File.ReadAllText(destination).Should().Be("reserved");
    }

    [Fact]
    public void ReserveClosedSegments_MovesClosedSegmentsAndKeepsHighestAsActive()
    {
        var (manager, eventDirectory) = CreateManager();
        Enumerable.Range(0, 6).ToList()
            .ForEach(sequence => CreateSegment(sequence, bytes: (byte)(sequence + 1)));

        var reservation = manager.ReserveClosedSegments(
            _rollingDirectory.FullName,
            eventDirectory);

        reservation.CompletedSegments.Should().HaveCount(5);
        reservation.ActiveSegmentPath.Should().EndWith("rolling_000000005.mp4");
        reservation.CompletedSegments.Select(x => Path.GetFileName(x.FilePath))
            .Should().Equal("rolling_000000000.mp4", "rolling_000000001.mp4", "rolling_000000002.mp4", "rolling_000000003.mp4", "rolling_000000004.mp4");
        Directory.GetFiles(_rollingDirectory.FullName, "rolling_*.mp4")
            .Should().ContainSingle().Which.Should().Be(reservation.ActiveSegmentPath);
        Directory.GetFiles(eventDirectory, "rolling_*.mp4").Should().HaveCount(5);
    }

    [Fact]
    public void ReserveClosedSegments_MovesNewlyClosedSegmentsOnNextPoll()
    {
        var (manager, eventDirectory) = CreateManager();
        var first = CreateSegment(0, bytes: 1);
        var second = CreateSegment(1, bytes: 2);

        var firstReservation = manager.ReserveClosedSegments(
            _rollingDirectory.FullName,
            eventDirectory);

        File.Move(second, Path.Combine(eventDirectory, Path.GetFileName(second)));
        var third = CreateSegment(2, bytes: 3);
        var secondReservation = manager.ReserveClosedSegments(
            _rollingDirectory.FullName,
            eventDirectory);

        firstReservation.CompletedSegments.Should().ContainSingle()
            .Which.FilePath.Should().Be(Path.Combine(eventDirectory, Path.GetFileName(first)));
        secondReservation.CompletedSegments.Should().BeEmpty();
        File.Exists(third).Should().BeTrue();
    }

    [Fact]
    public void DeleteUnreservedSegments_DeletesOldNonEventFilesOnly()
    {
        var (manager, eventDirectory) = CreateManager();
        var old = CreateSegment(0, bytes: 1, TestTimeUtc.AddMinutes(-1));
        var boundary = CreateSegment(1, bytes: 2, TestTimeUtc.AddSeconds(-30));
        var withinWindow = CreateSegment(2, bytes: 3, TestTimeUtc.AddSeconds(-15));
        var malformed = Path.Combine(_rollingDirectory.FullName, "rolling_notes.mp4");
        File.WriteAllText(malformed, "outside manager scope");
        var eventFile = Path.Combine(eventDirectory, "rolling_000000003.mp4");
        File.WriteAllText(eventFile, "event-owned");

        manager.DeleteUnreservedSegments(_rollingDirectory.FullName, TestTimeUtc);

        File.Exists(old).Should().BeFalse();
        File.Exists(boundary).Should().BeTrue();
        File.Exists(withinWindow).Should().BeTrue();
        File.Exists(malformed).Should().BeTrue();
        File.Exists(eventFile).Should().BeTrue();
    }

    private (RollingSegmentManager Manager, string EventDirectory) CreateManager()
    {
        var eventDirectory = Path.Combine(_rollingDirectory.FullName, "event");
        Directory.CreateDirectory(eventDirectory);
        var options = new RecordingOptions
        {
            RollingSegmentSeconds = 5,
            PreEventSeconds = 30
        };
        return (new RollingSegmentManager(_time, options), eventDirectory);
    }

    private string CreateSegment(
        int sequence,
        byte bytes = 1,
        DateTimeOffset? modifiedUtc = null)
    {
        var path = Path.Combine(
            _rollingDirectory.FullName,
            $"rolling_{sequence:D9}.mp4");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(
            path,
            (modifiedUtc ?? TestTimeUtc).UtcDateTime);
        return path;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
