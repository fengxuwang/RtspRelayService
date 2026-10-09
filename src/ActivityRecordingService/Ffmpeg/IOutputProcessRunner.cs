using RtspRelayService.Ffmpeg;

namespace ActivityRecordingService.Ffmpeg;

public interface IOutputProcessRunner
{
    Task<IOutputProcess> StartAsync(
        FfmpegCommand command,
        Action<string> onStandardError,
        CancellationToken cancellationToken);
}
