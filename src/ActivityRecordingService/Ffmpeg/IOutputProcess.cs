namespace ActivityRecordingService.Ffmpeg;

public interface IOutputProcess
{
    int Id { get; }

    Stream StandardOutput { get; }

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    Task KillTreeAsync(CancellationToken cancellationToken);

    ValueTask DisposeAsync();
}
