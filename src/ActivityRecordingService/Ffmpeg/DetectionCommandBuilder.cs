using System.Globalization;
using ActivityRecordingService.Configuration;
using RtspRelayService.Ffmpeg;

namespace ActivityRecordingService.Ffmpeg;

public static class DetectionCommandBuilder
{
    public static FfmpegCommand Build(
        string ffmpegPath,
        ValidatedCameraOptions camera,
        DetectionOptions options,
        TimeSpan inputTimeout) => new(
        ffmpegPath,
        [
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", ((long)inputTimeout.TotalMicroseconds)
                .ToString(CultureInfo.InvariantCulture),
            "-i", camera.DetectionUrl,
            "-an",
            "-vf", $"fps={options.FramesPerSecond},scale={options.Width}:{options.Height}",
            "-pix_fmt", "bgr24",
            "-f", "rawvideo",
            "pipe:1"
        ]);
}
