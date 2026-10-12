using ActivityRecordingService.Configuration;
using ActivityRecordingService.Ffmpeg;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Ffmpeg;

public sealed class SegmenterCommandBuilderTests
{
    [Fact]
    public void Build_UsesStreamCopyAndBoundedSegments()
    {
        var camera = CreateCamera();
        var options = new RecordingOptions
        {
            RollingSegmentSeconds = 5
        };

        var result = SegmenterCommandBuilder.Build(
            "/tools/ffmpeg",
            camera,
            options,
            "/recordings/front/rolling",
            TimeSpan.FromSeconds(10),
            startSequenceNumber: 17);

        result.FileName.Should().Be("/tools/ffmpeg");
        result.Arguments.Should().Equal(
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", "10000000",
            "-i", camera.RecordUrl,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", "copy",
            "-af", "asetpts=PTS-STARTPTS,aresample=async=1",
            "-c:a", "aac",
            "-f", "segment",
            "-segment_time", "5",
            "-segment_start_number", "17",
            "-reset_timestamps", "1",
            "-avoid_negative_ts", "make_zero",
            "-segment_format", "mp4",
            Path.Combine("/recordings/front/rolling", "rolling_%09d.mp4"));
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
