using RtspRelayService.Configuration;

namespace RtspRelayService.Ffmpeg;

public static class FfmpegCommandBuilder
{
    public static FfmpegCommand Build(
        string ffmpegPath,
        StreamOptions stream,
        TimeSpan inputTimeout) => new(
        ffmpegPath,
        [
            "-nostdin",
            "-fflags", "+genpts",
            "-rtsp_transport", "tcp",
            "-timeout", ((long)inputTimeout.TotalMicroseconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "-i", stream.InputUrl,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", "copy",
            "-c:a", "aac",
            "-b:a", "32k",
            "-f", "flv",
            stream.OutputUrl
        ]);
}
