using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Detection;

public sealed class PersonDetectorTests
{
    [Fact]
    public void Detect_DecodesHighestConfidencePersonAndScalesCoordinates()
    {
        var timestampUtc = DateTimeOffset.Parse("2026-09-29T01:02:03.004Z");
        using var session = new FakeModelSession(
            [
                // Anchor 0 and anchor 1 for each YOLO channel.
                320,
                320,
                180,
                180,
                100,
                100,
                200,
                200,
                0.90f,
                0.20f
            ],
            [1, 5, 2]);
        var detector = new PersonDetector(
            session,
            new DetectionOptions
            {
                Width = 640,
                Height = 360,
                ConfidenceThreshold = 0.45,
                PersonClassId = 0
            },
            Roi: null);

        var result = detector.Detect(new ImageFrame(640, 360, timestampUtc, new byte[640 * 360 * 3]));

        result.TimestampUtc.Should().Be(timestampUtc);
        result.People.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PersonDetection(
                Confidence: 0.90f,
                X: 270,
                Y: 80,
                Width: 100,
                Height: 200));
    }

    [Fact]
    public void Detect_IgnoresPersonBelowConfidenceThreshold()
    {
        using var session = new FakeModelSession(
            [
                320,
                320,
                180,
                180,
                100,
                100,
                200,
                200,
                0.44f,
                0.20f
            ],
            [1, 5, 2]);
        var detector = new PersonDetector(
            session,
            new DetectionOptions
            {
                Width = 640,
                Height = 360,
                ConfidenceThreshold = 0.45,
                PersonClassId = 0
            },
            Roi: null);

        var result = detector.Detect(new ImageFrame(640, 360, DateTimeOffset.UtcNow, new byte[640 * 360 * 3]));

        result.People.Should().BeEmpty();
    }

    [Fact]
    public void Detect_ThrowsForUnexpectedOutputShape()
    {
        using var session = new FakeModelSession([1f], [1, 6]);
        var detector = new PersonDetector(
            session,
            new DetectionOptions
            {
                Width = 640,
                Height = 360,
                ConfidenceThreshold = 0.45,
                PersonClassId = 0
            },
            Roi: null);

        var act = () => detector.Detect(new ImageFrame(640, 360, DateTimeOffset.UtcNow, new byte[640 * 360 * 3]));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Detect_AppliesOptionalRoi()
    {
        using var session = new FakeModelSession(
            [
                64,
                128,
                320,
                512,
                180,
                72,
                180,
                288,
                100,
                100,
                100,
                100,
                200,
                200,
                200,
                200,
                0.90f,
                0.90f,
                0.90f,
                0.90f
            ],
            [1, 5, 4]);
        var detector = new PersonDetector(
            session,
            new DetectionOptions
            {
                Width = 640,
                Height = 360,
                ConfidenceThreshold = 0.45,
                PersonClassId = 0
            },
            Roi: new NormalizedRect(
                X: 0.2,
                Y: 0.2,
                Width: 0.8,
                Height: 0.8));

        var result = detector.Detect(new ImageFrame(640, 360, DateTimeOffset.UtcNow, new byte[640 * 360 * 3]));

        result.People.Should().HaveCount(3);
    }

    private sealed class FakeModelSession(float[] values, int[] shape) : IModelSession
    {
        public RawInferenceResult Run(ReadOnlySpan<float> input) => new(values, shape);

        public void Dispose()
        {
        }
    }
}
