using RtspRelayService.Configuration;

namespace RtspRelayService.Supervision;

public interface IStreamSupervisorFactory
{
    Func<CancellationToken, Task> Create(StreamOptions stream);
}
