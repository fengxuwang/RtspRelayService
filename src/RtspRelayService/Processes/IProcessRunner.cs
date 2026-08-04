using RtspRelayService.Ffmpeg;

namespace RtspRelayService.Processes;

public interface IProcessRunner
{
    Task<IManagedProcess> StartAsync(
        FfmpegCommand command,
        Action<string> onStandardError,
        CancellationToken cancellationToken);
}
