using Microsoft.Extensions.Hosting;
using RtspRelayService.Configuration;

namespace RtspRelayService.Supervision;

public sealed class RelayWorker : IHostedService
{
    private readonly ValidatedRelayOptions _options;
    private readonly IStreamSupervisorFactory _factory;
    private readonly ILogger<RelayWorker> _logger;
    private CancellationTokenSource? _stop;
    private Task[] _tasks = [];

    public RelayWorker(
        ValidatedRelayOptions options,
        IStreamSupervisorFactory factory,
        ILogger<RelayWorker> logger)
    {
        _options = options;
        _factory = factory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var error in _options.Errors)
            _logger.LogError("Skipping invalid stream: {Error}", error);

        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stop = stop;
        _tasks = _options.ValidStreams
            .Select(stream => Task.Run(
                () => RunSupervisorAsync(stream, stop.Token),
                CancellationToken.None))
            .ToArray();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken _)
    {
        var stop = _stop;
        if (stop is null)
            return;

        var allTasks = Task.WhenAll(_tasks);
        try
        {
            await stop.CancelAsync();
            await allTasks;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _stop, null, stop), stop))
            {
                _tasks = [];
                stop.Dispose();
            }
        }
    }

    private async Task RunSupervisorAsync(
        StreamOptions stream,
        CancellationToken stoppingToken)
    {
        try
        {
            await _factory.Create(stream)(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogCritical(
                exception,
                "[{Stream}] Stream supervisor failed during shutdown",
                stream.Name);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogCritical(
                exception,
                "[{Stream}] Stream supervisor stopped unexpectedly",
                stream.Name);
        }
    }
}
