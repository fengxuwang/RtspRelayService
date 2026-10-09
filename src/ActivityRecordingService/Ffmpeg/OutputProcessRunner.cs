using System.Diagnostics;
using System.Threading.Channels;
using RtspRelayService.Ffmpeg;

namespace ActivityRecordingService.Ffmpeg;

public sealed class OutputProcessRunner : IOutputProcessRunner
{
    private const int StandardErrorBufferCapacity = 256;
    private static readonly TimeSpan StandardErrorDrainTimeout = TimeSpan.FromMilliseconds(250);

    public Task<IOutputProcess> StartAsync(
        FfmpegCommand command,
        Action<string> onStandardError,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);

        var standardError = Channel.CreateBounded<string>(new BoundedChannelOptions(StandardErrorBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var standardErrorDispatchCancellation = new CancellationTokenSource();
        var standardErrorDispatch = DispatchStandardErrorAsync(
            standardError.Reader,
            onStandardError,
            standardErrorDispatchCancellation.Token);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                standardError.Writer.TryWrite(eventArgs.Data);
        };

        var started = false;
        try
        {
            started = process.Start();
            if (!started)
                throw new InvalidOperationException($"Failed to start {command.FileName}.");

            process.BeginErrorReadLine();
            return Task.FromResult<IOutputProcess>(new OutputProcess(
                process,
                process.StandardOutput.BaseStream,
                standardError.Writer,
                standardErrorDispatchCancellation,
                standardErrorDispatch));
        }
        catch
        {
            standardError.Writer.TryComplete();
            standardErrorDispatchCancellation.Cancel();

            if (started)
                StopProcess(process);
            else
                process.Dispose();

            standardErrorDispatchCancellation.Dispose();
            throw;
        }
    }

    private static async Task DispatchStandardErrorAsync(
        ChannelReader<string> standardError,
        Action<string> onStandardError,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in standardError.ReadAllAsync(cancellationToken))
            {
                try
                {
                    onStandardError(line);
                }
                catch
                {
                    // Logging sinks are external to process supervision and must not stop stderr draining.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed class OutputProcess(
        Process process,
        Stream standardOutput,
        ChannelWriter<string> standardError,
        CancellationTokenSource standardErrorDispatchCancellation,
        Task standardErrorDispatch) : IOutputProcess
    {
        private int standardErrorCompleted;
        private int disposed;

        public int Id => process.Id;

        public Stream StandardOutput => standardOutput;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
            CompleteStandardError();
            return process.ExitCode;
        }

        public async Task KillTreeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!process.HasExited)
                TryKillTree();

            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
            CompleteStandardError();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            CompleteStandardError();
            await DrainStandardErrorAsync();
            process.Dispose();

            if (standardErrorDispatch.IsCompleted)
                standardErrorDispatchCancellation.Dispose();
            else
                _ = DisposeDispatchCancellationWhenCompleteAsync();
        }

        private void CompleteStandardError()
        {
            if (Interlocked.Exchange(ref standardErrorCompleted, 1) == 0)
                standardError.TryComplete();
        }

        private void TryKillTree()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
        }

        private async Task DrainStandardErrorAsync()
        {
            if (await Task.WhenAny(
                    standardErrorDispatch,
                    Task.Delay(StandardErrorDrainTimeout)).ConfigureAwait(false) != standardErrorDispatch)
            {
                standardErrorDispatchCancellation.Cancel();
            }
        }

        private async Task DisposeDispatchCancellationWhenCompleteAsync()
        {
            try
            {
                await standardErrorDispatch.ConfigureAwait(false);
            }
            finally
            {
                standardErrorDispatchCancellation.Dispose();
            }
        }
    }
}
