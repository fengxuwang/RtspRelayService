using RtspRelayService.Configuration;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;

namespace RtspRelayService.Supervision;

public sealed class StreamSupervisor(
    StreamOptions stream,
    string ffmpegPath,
    IProcessRunner processRunner,
    RetryPolicy retryPolicy,
    TimeSpan inputTimeout,
    TimeSpan shutdownTimeout,
    TimeProvider timeProvider,
    ILogger<StreamSupervisor> logger)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var failureCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var runtime = TimeSpan.Zero;

            try
            {
                await using var process = await processRunner.StartAsync(
                    FfmpegCommandBuilder.Build(ffmpegPath, stream, inputTimeout),
                    line => logger.LogWarning(
                        "[{Stream}] FFmpeg: {Line}",
                        stream.Name,
                        UrlRedactor.RedactText(line, [stream.InputUrl, stream.OutputUrl])),
                    stoppingToken);
                var startedAt = timeProvider.GetTimestamp();
                logger.LogInformation(
                    "[{Stream}] FFmpeg started with PID {Pid}",
                    stream.Name,
                    process.Id);

                try
                {
                    var exitCode = await process.WaitForExitAsync(stoppingToken);
                    runtime = timeProvider.GetElapsedTime(startedAt);
                    logger.LogWarning(
                        "[{Stream}] FFmpeg exited with code {ExitCode} after {Runtime}",
                        stream.Name,
                        exitCode,
                        runtime);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await StopProcessAsync(process);
                    break;
                }
                catch (Exception exception)
                {
                    runtime = timeProvider.GetElapsedTime(startedAt);
                    var stopped = await StopProcessAsync(process);
                    LogProcessFailure("monitoring", exception);
                    if (!stopped)
                        return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogProcessFailure("startup", exception);
            }

            failureCount = retryPolicy.NextFailureCount(failureCount, runtime);
            var delay = retryPolicy.GetDelay(failureCount);
            logger.LogInformation(
                "[{Stream}] Restarting in {Delay}; failure {FailureCount}",
                stream.Name, delay, failureCount);
            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> StopProcessAsync(IManagedProcess process)
    {
        using var timeout = new CancellationTokenSource(shutdownTimeout);
        try
        {
            await process.KillTreeAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogCritical(
                "[{Stream}] FFmpeg process tree did not exit within {Timeout}",
                stream.Name,
                shutdownTimeout);
            return false;
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "[{Stream}] Failed to stop FFmpeg process tree: {Error}",
                stream.Name,
                RedactExceptionMessage(exception));
            return false;
        }
    }

    private void LogProcessFailure(string stage, Exception exception) =>
        logger.LogError(
            "[{Stream}] FFmpeg {Stage} failed: {Error}",
            stream.Name,
            stage,
            RedactExceptionMessage(exception));

    private string RedactExceptionMessage(Exception exception) =>
        UrlRedactor.RedactText(
            exception.Message,
            [stream.InputUrl, stream.OutputUrl]);
}
