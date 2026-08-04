using System.Diagnostics;
using FluentAssertions;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;

namespace RtspRelayService.Tests.Processes;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task StartAsync_DrainsFinalStandardErrorLineAndReturnsExitCode()
    {
        var finalLineReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = OperatingSystem.IsWindows()
            ? new FfmpegCommand("cmd.exe", ["/c", "echo diagnostic 1>&2 & echo final diagnostic 1>&2 & exit /b 7"])
            : new FfmpegCommand("/bin/sh", ["-c", "echo diagnostic >&2; echo final diagnostic >&2; exit 7"]);

        var exitCode = 0;
        await using (var process = await new ProcessRunner().StartAsync(command, line =>
        {
            if (line == "final diagnostic")
                finalLineReceived.TrySetResult();
        }, default))
        {
            exitCode = await process.WaitForExitAsync(default);
        }

        await finalLineReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        exitCode.Should().Be(7);
    }

    [Fact]
    public async Task WaitForExitAsync_WhenAwaitUsingScopeExits_DrainsQueuedFinalStandardErrorLine()
    {
        var firstLineReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalLineReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstLine = new ManualResetEventSlim();
        var command = OperatingSystem.IsWindows()
            ? new FfmpegCommand("cmd.exe", ["/c", "echo first diagnostic 1>&2 & echo final diagnostic 1>&2"])
            : new FfmpegCommand("/bin/sh", ["-c", "echo first diagnostic >&2; echo final diagnostic >&2"]);

        try
        {
            Task releaseTask;
            await using (var process = await new ProcessRunner().StartAsync(command, line =>
            {
                if (line == "first diagnostic")
                {
                    firstLineReceived.TrySetResult();
                    releaseFirstLine.Wait();
                }

                if (line == "final diagnostic")
                    finalLineReceived.TrySetResult();
            }, default))
            {
                await firstLineReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(default);
                releaseTask = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    releaseFirstLine.Set();
                });
            }

            finalLineReceived.Task.IsCompleted.Should().BeTrue();
            await releaseTask;
            await finalLineReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseFirstLine.Set();
        }
    }

    [Fact]
    public async Task StartAsync_WhenStandardErrorSinkThrows_ContinuesDrainingLaterLines()
    {
        var finalLineReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = OperatingSystem.IsWindows()
            ? new FfmpegCommand("cmd.exe", ["/c", "echo first diagnostic 1>&2 & echo final diagnostic 1>&2"])
            : new FfmpegCommand("/bin/sh", ["-c", "echo first diagnostic >&2; echo final diagnostic >&2"]);

        await using var process = await new ProcessRunner().StartAsync(command, line =>
        {
            if (line == "first diagnostic")
                throw new InvalidOperationException("sink failed");

            if (line == "final diagnostic")
                finalLineReceived.TrySetResult();
        }, default);

        await process.WaitForExitAsync(default);
        await finalLineReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StartAsync_WhenCancelled_ThrowsBeforeStartingAProcess()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var command = OperatingSystem.IsWindows()
            ? new FfmpegCommand("cmd.exe", ["/c", "exit /b 0"])
            : new FfmpegCommand("/bin/sh", ["-c", "exit 0"]);

        var start = () => new ProcessRunner().StartAsync(command, _ => { }, cancellationSource.Token);

        await start.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitForExitAsync_WhenCancelled_ThrowsAndProcessCanBeKilled()
    {
        var command = OperatingSystem.IsWindows()
            ? new FfmpegCommand("cmd.exe", ["/c", "ping -n 31 127.0.0.1 > nul"])
            : new FfmpegCommand("/bin/sh", ["-c", "sleep 30"]);

        await using var process = await new ProcessRunner().StartAsync(command, _ => { }, default);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var wait = () => process.WaitForExitAsync(cancellationSource.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
        await process.KillTreeAsync(default);
    }

    [Fact]
    public async Task WaitForExitAsync_DoesNotBlockOnASlowStandardErrorSink()
    {
        var command = OperatingSystem.IsWindows()
            ? new FfmpegCommand("cmd.exe", ["/c", "for /L %i in (1,1,400) do @echo line-%i 1>&2"])
            : new FfmpegCommand("/bin/sh", ["-c", "i=0; while [ $i -lt 400 ]; do echo line-$i >&2; i=$((i + 1)); done"]);
        var stopwatch = Stopwatch.StartNew();

        await using (var process = await new ProcessRunner().StartAsync(command, _ => Thread.Sleep(10), default))
        {
            await process.WaitForExitAsync(default);
        }

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task KillTreeAsync_WhenProcessExitsQuickly_CompletesWithoutFailure()
    {
        var command = OperatingSystem.IsWindows()
            ? new FfmpegCommand("cmd.exe", ["/c", "exit /b 0"])
            : new FfmpegCommand("/bin/sh", ["-c", "exit 0"]);

        for (var attempt = 0; attempt < 25; attempt++)
        {
            await using var process = await new ProcessRunner().StartAsync(command, _ => { }, default);
            await process.KillTreeAsync(default);
        }
    }
}
