using ActivityRecordingService.Configuration;
using ActivityRecordingService.Ffmpeg;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Ffmpeg;

public sealed class DetectionCommandBuilderTests
{
    [Fact]
    public void Build_UsesRawBgrFramesAtConfiguredRate()
    {
        var camera = CreateCamera();
        var options = new DetectionOptions
        {
            Width = 640,
            Height = 360,
            FramesPerSecond = 3
        };

        var result = DetectionCommandBuilder.Build(
            "/tools/ffmpeg",
            camera,
            options,
            TimeSpan.FromSeconds(10));

        result.FileName.Should().Be("/tools/ffmpeg");
        result.Arguments.Should().Equal(
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", "10000000",
            "-i", camera.DetectionUrl,
            "-an",
            "-vf", "fps=3,scale=640:360",
            "-pix_fmt", "bgr24",
            "-f", "rawvideo", "pipe:1");
    }

    private static ValidatedCameraOptions CreateCamera() =>
        new(
            "front",
            "rtsp://camera.example/sub",
            "rtsp://camera.example/main",
            Roi: null,
            RetentionDays: null,
            Settings: new CameraOptions
            {
                Name = "front",
                Enabled = true,
                DetectionUrl = "rtsp://camera.example/sub",
                RecordUrl = "rtsp://camera.example/main"
            });
}
