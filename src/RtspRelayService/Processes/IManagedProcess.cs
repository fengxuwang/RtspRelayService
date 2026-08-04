namespace RtspRelayService.Processes;

public interface IManagedProcess : IAsyncDisposable
{
    int Id { get; }

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    Task KillTreeAsync(CancellationToken cancellationToken);
}
