using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Detection;

public sealed class ImagePreprocessorTests
{
    [Fact]
    public void ToNchw_ConvertsBgrToNormalizedPlanarData()
    {
        var frame = new ImageFrame(
            Width: 2,
            Height: 1,
            TimestampUtc: DateTimeOffset.Parse("2026-09-29T01:02:03.004Z"),
            Bgr: [1, 2, 3, 4, 5, 6]);
        var options = new DetectionOptions
        {
            Width = 2,
            Height = 1
        };

        var result = ImagePreprocessor.ToNchw(frame, options);

        result.Should().Equal(
            1 / 255f,
            4 / 255f,
            2 / 255f,
            5 / 255f,
            3 / 255f,
            6 / 255f);
    }
}
