using System.Globalization;
using ActivityRecordingService.Configuration;
using RtspRelayService.Ffmpeg;

namespace ActivityRecordingService.Ffmpeg;

public static class SegmenterCommandBuilder
{
    public static FfmpegCommand Build(
        string ffmpegPath,
        ValidatedCameraOptions camera,
        RecordingOptions options,
        string rollingDirectory,
        TimeSpan inputTimeout,
        int startSequenceNumber = 0) => new(
        ffmpegPath,
        [
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", ((long)inputTimeout.TotalMicroseconds)
                .ToString(CultureInfo.InvariantCulture),
            "-i", camera.RecordUrl,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", "copy",
            "-af", "asetpts=PTS-STARTPTS,aresample=async=1",
            "-c:a", "aac",
            "-f", "segment",
            "-segment_time", options.RollingSegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-segment_start_number", startSequenceNumber.ToString(CultureInfo.InvariantCulture),
            "-reset_timestamps", "1",
            "-avoid_negative_ts", "make_zero",
            "-segment_format", "mp4",
            Path.Combine(rollingDirectory, "rolling_%09d.mp4")
        ]);
}
