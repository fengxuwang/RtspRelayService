using System.Text;
using ActivityRecordingService.Ffmpeg;
using FluentAssertions;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;

namespace ActivityRecordingService.Tests.Ffmpeg;

public sealed class EventPartWriterTests : IDisposable
{
    private readonly DirectoryInfo _eventDirectory =
        Directory.CreateTempSubdirectory("event-part-writer-");

    [Fact]
    public async Task WriteAsync_CreatesSafeConcatManifestAndAtomicallyRenamesPart()
    {
        var firstSource = CreateSegment("rolling_000000000.mp4");
        var secondSource = CreateSegment("it's rolling_000000001.mp4");
        var runner = new FakeProcessRunner();
        var writer = new EventPartWriter("/usr/bin/ffmpeg", runner);
        var request = CreateRequest(
        [
            new RollingSegment(firstSource, Bytes: 3, TimeSpan.FromSeconds(5)),
            new RollingSegment(secondSource, Bytes: 5, TimeSpan.FromSeconds(5))
        ]);

        var result = await writer.WriteAsync(request, CancellationToken.None);

        var manifestPath = Path.Combine(_eventDirectory.FullName, "part-000.ffconcat");
        var temporaryPath = Path.Combine(_eventDirectory.FullName, "part-000.tmp.mp4");
        var finalPath = Path.Combine(_eventDirectory.FullName, "part-000.mp4");
        var expectedCommand = new FfmpegCommand("/usr/bin/ffmpeg",
        [
            "-y",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            manifestPath,
            "-map",
            "0:v:0",
            "-map",
            "0:a:0?",
            "-c:v",
            "copy",
            "-c:a",
            "copy",
            "-movflags",
            "+faststart",
            "-f",
            "mp4",
            temporaryPath
        ]);
        runner.Command!.FileName.Should().Be(expectedCommand.FileName);
        runner.Command.Arguments.Should().Equal(expectedCommand.Arguments);
        runner.ManifestBytes.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
        runner.Manifest!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Should().Equal(
            "ffconcat version 1.0",
            $"file '{firstSource}'",
            $"file '{secondSource.Replace("'", "'\\''", StringComparison.Ordinal)}'");
        File.Exists(temporaryPath).Should().BeFalse();
        File.Exists(manifestPath).Should().BeFalse();
        File.ReadAllBytes(finalPath).Should().Equal([1, 2, 3, 4, 5, 6, 7, 8]);
        result.Should().Be(new EventPartResult(finalPath, Bytes: 8));
        File.Exists(firstSource).Should().BeTrue();
        File.Exists(secondSource).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_WhenFinalFileAlreadyExists_PreservesItAndRemovesTemporaryOutput()
    {
        var source = CreateSegment("rolling_000000000.mp4");
        var runner = new FakeProcessRunner();
        var writer = new EventPartWriter("/usr/bin/ffmpeg", runner);
        var finalPath = Path.Combine(_eventDirectory.FullName, "part-000.mp4");
        File.WriteAllBytes(finalPath, [99]);
        var request = CreateRequest([new RollingSegment(source, Bytes: 1, TimeSpan.FromSeconds(5))]);

        var write = () => writer.WriteAsync(request, CancellationToken.None);

        await write.Should().ThrowAsync<EventPartWriteException>();
        File.ReadAllBytes(finalPath).Should().Equal([99]);
        File.Exists(Path.Combine(_eventDirectory.FullName, "part-000.tmp.mp4"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_eventDirectory.FullName, "part-000.ffconcat"))
            .Should().BeTrue();
        File.Exists(source).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_WhenFfmpegFails_ThrowsWithoutRenamingTemporaryOutput()
    {
        var source = CreateSegment("rolling_000000000.mp4");
        var runner = new FakeProcessRunner { ExitCode = 17 };
        var writer = new EventPartWriter("/usr/bin/ffmpeg", runner);
        var request = CreateRequest([new RollingSegment(source, Bytes: 1, TimeSpan.FromSeconds(5))]);

        var write = () => writer.WriteAsync(request, CancellationToken.None);

        await write.Should().ThrowAsync<EventPartWriteException>()
            .WithMessage("FFmpeg failed for event * part 0 with exit code 17. Diagnostics:*");
        File.Exists(Path.Combine(_eventDirectory.FullName, "part-000.tmp.mp4"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_eventDirectory.FullName, "part-000.ffconcat"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_eventDirectory.FullName, "part-000.mp4"))
            .Should().BeFalse();
        File.Exists(source).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_WhenFfmpegFails_BoundsAndRedactsStandardErrorTail()
    {
        var source = CreateSegment("rolling_000000000.mp4");
        var runner = new FakeProcessRunner
        {
            ExitCode = 17,
            StandardErrorLines = Enumerable.Range(0, 30)
                .Select(index =>
                    $"line-{index} rtsp://admin:secret-{index}@camera.example/input {_eventDirectory.FullName}")
                .ToArray()
        };
        var writer = new EventPartWriter("/usr/bin/ffmpeg", runner);
        var request = CreateRequest([new RollingSegment(source, Bytes: 1, TimeSpan.FromSeconds(5))]);

        var write = () => writer.WriteAsync(request, CancellationToken.None);

        var failure = (await write.Should().ThrowAsync<EventPartWriteException>()).Which;
        var diagnostics = failure.Diagnostics.Split('\n');
        diagnostics.Should().HaveCount(16);
        diagnostics[0].Should().StartWith("line-14 ");
        diagnostics[^1].Should().StartWith("line-29 ");
        failure.Diagnostics.Should().NotContain("secret");
        failure.Diagnostics.Should().NotContain(_eventDirectory.FullName);
        failure.Diagnostics.Should().NotContain("rtsp://");
        failure.Diagnostics.Should().NotContain("rtsps://");
        failure.Diagnostics.Should().NotContain("rtmp://");
        failure.Diagnostics.Should().NotContain("rtmps://");
        failure.Diagnostics.Should().NotContain("camera.example");
        failure.Diagnostics.Should().NotContain("@");
        diagnostics.Should().OnlyContain(line =>
            line.Contains("[redacted-url]", StringComparison.Ordinal) &&
            line.Length <= 512);
    }

    private EventPartRequest CreateRequest(IReadOnlyList<RollingSegment> segments) =>
        new(
            Guid.NewGuid(),
            "camera-a",
            PartIndex: 0,
            _eventDirectory.FullName,
            segments);

    private string CreateSegment(string fileName)
    {
        var path = Path.Combine(_eventDirectory.FullName, fileName);
        File.WriteAllBytes(path, new byte[8]);
        return Path.GetFullPath(path);
    }

    public void Dispose() => _eventDirectory.Delete(recursive: true);

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private byte[] _outputBytes = [1, 2, 3, 4, 5, 6, 7, 8];

        public FfmpegCommand? Command { get; private set; }

        public string? Manifest { get; private set; }

        public byte[] ManifestBytes { get; private set; } = [];

        public int ExitCode { get; set; }

        public IReadOnlyList<string> StandardErrorLines { get; set; } = ["ffmpeg diagnostics"];

        public Task<IManagedProcess> StartAsync(
            FfmpegCommand command,
            Action<string> onStandardError,
            CancellationToken cancellationToken)
        {
            Command = command;
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var line in StandardErrorLines)
                onStandardError(line);

            var manifestPath = command.Arguments[Array.IndexOf(command.Arguments.ToArray(), "-i") + 1];
            ManifestBytes = File.ReadAllBytes(manifestPath);
            Manifest = File.ReadAllText(manifestPath, Encoding.UTF8);
            var outputPath = command.Arguments[^1];
            File.WriteAllBytes(outputPath, _outputBytes);

            return Task.FromResult<IManagedProcess>(new FakeManagedProcess(ExitCode));
        }

        public void SetOutput(byte[] bytes) => _outputBytes = bytes;
    }

    private sealed class FakeManagedProcess(int exitCode) : IManagedProcess
    {
        public int Id => 12345;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(exitCode);

        public Task KillTreeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
