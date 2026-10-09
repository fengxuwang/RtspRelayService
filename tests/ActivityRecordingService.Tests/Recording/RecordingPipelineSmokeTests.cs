using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Ffmpeg;
using ActivityRecordingService.Recording;
using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;
using Xunit.Abstractions;

namespace ActivityRecordingService.Tests.Recording;

public sealed class RecordingPipelineSmokeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly DirectoryInfo _rootDirectory =
        Directory.CreateTempSubdirectory("recording-pipeline-smoke-");

    static RecordingPipelineSmokeTests()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public RecordingPipelineSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task FfmpegFixture_CreatesPlayablePartsAndUsesMetadataWhenModelExists()
    {
        var result = await RunSmokeAsync();

        result.PartPath.Should().NotBeNull();
        result.PartPath!.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
    }

    public void Dispose() => _rootDirectory.Delete(recursive: true);

    private async Task<SmokeResult> RunSmokeAsync()
    {
        var ffmpegPath = FindExecutable("ffmpeg");
        if (ffmpegPath is null)
        {
            throw new Xunit.Sdk.SkipException(
                "Recording pipeline smoke test skipped: ffmpeg was not found on PATH.");
        }

        var ffprobePath = FindExecutable("ffprobe");
        if (ffprobePath is null)
        {
            throw new Xunit.Sdk.SkipException(
                "Recording pipeline smoke test skipped: ffprobe was not found on PATH.");
        }

        var fixture = await CreateFfmpegFixtureAsync(ffmpegPath);
        Directory.EnumerateFiles(fixture.RollingDirectory, "rolling_*.mp4")
            .Should()
            .HaveCountGreaterThanOrEqualTo(2, "the H.264 fixture should produce bounded rolling segments");

        var modelPath = FindModelPath();
        if (modelPath is null)
        {
            var part = await WriteFixturePartAsync(fixture);
            await AssertPlayableAsync(ffprobePath, part);
            File.Exists(fixture.DatabasePath).Should().BeFalse(
                "the model-independent smoke path must not create SQLite metadata");
            _output.WriteLine(
                "Person detection assertions skipped: models/person-yolo11n.onnx is unavailable.");
            return new SmokeResult(part, EventId: null);
        }

        var startedUtc = DateTimeOffset.UtcNow;
        using (var modelSession = new OnnxModelSession(modelPath, fixture.Settings.Detection))
        {
            var process = new OutputProcessRunner();
            (await DetectsGeneratedFrameAsync(process, modelSession, fixture, ffmpegPath))
                .Should()
                .BeTrue("the model-present smoke path must exercise the detection transition");
        }

        await using (var repository = new SqliteEventRepository(
            fixture.DatabasePath,
            fixture.Settings.Recording.RetentionDays))
        {
            await repository.InitializeAsync(CancellationToken.None);
            var recorder = new CameraEventRecorder(
                fixture.Service.Cameras[0],
                fixture.Service,
                repository,
                fixture.SegmentManager,
                fixture.PartWriter,
                TimeProvider.System,
                NullLogger<CameraEventRecorder>.Instance);
            var reservation = fixture.SegmentManager.BeginEvent(
                fixture.RollingDirectory,
                fixture.EventDirectory,
                startedUtc);
            reservation.CompletedSegments.Should().NotBeEmpty();

            await recorder.BeginEventAsync(startedUtc, reservation, CancellationToken.None);
            var eventId = recorder.Snapshot.EventId
                ?? throw new InvalidOperationException("The smoke recorder did not create an event ID.");
            await recorder.CompleteAsync(EventEndReason.NoPerson, CancellationToken.None);

            var storedEvent = await repository.GetEventAsync(eventId, CancellationToken.None);
            storedEvent.Should().NotBeNull();
            storedEvent!.Status.Should().Be(EventStatus.Completed);
            storedEvent.FileCount.Should().Be(1);
            storedEvent.FileBytes.Should().BeGreaterThan(0);

            var files = await repository.GetFilesAsync(eventId, CancellationToken.None);
            files.Should().ContainSingle().Which.Status.Should().Be(RecordingFileStatus.Ready);
            var partPath = Path.GetFullPath(Path.Combine(
                _rootDirectory.FullName,
                files[0].RelativePath));
            await AssertPlayableAsync(ffprobePath, partPath);
            return new SmokeResult(partPath, eventId);
        }
    }

    private async Task<SmokeFixture> CreateFfmpegFixtureAsync(string ffmpegPath)
    {
        var fixturePath = Path.Combine(_rootDirectory.FullName, "fixture.mp4");
        var rollingDirectory = Path.Combine(_rootDirectory.FullName, "rolling", "smoke-camera");
        var eventDirectory = Path.Combine(_rootDirectory.FullName, "events", "smoke-camera", "event");
        Directory.CreateDirectory(rollingDirectory);
        Directory.CreateDirectory(eventDirectory);

        var processRunner = new ProcessRunner();
        await RunProcessAsync(
            processRunner,
            ffmpegPath,
            [
                "-y",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=320x180:rate=10",
                "-t",
                "4",
                "-c:v",
                "libx264",
                "-pix_fmt",
                "yuv420p",
                "-g",
                "10",
                "-movflags",
                "+faststart",
                fixturePath
            ]);
        await RunProcessAsync(
            processRunner,
            ffmpegPath,
            [
                "-y",
                "-i",
                fixturePath,
                "-map",
                "0:v:0",
                "-map",
                "0:a:0?",
                "-c:v",
                "copy",
                "-af",
                "asetpts=PTS-STARTPTS,aresample=async=1",
                "-c:a",
                "aac",
                "-f",
                "segment",
                "-segment_time",
                "1",
                "-reset_timestamps",
                "1",
                "-segment_format",
                "mp4",
                Path.Combine(rollingDirectory, "rolling_%09d.mp4")
            ]);

        var settings = new ActivityRecordingOptions
        {
            FfmpegPath = ffmpegPath,
            RecordingRoot = _rootDirectory.FullName,
            InputTimeoutSeconds = 10,
            ShutdownTimeoutSeconds = 5
        };
        settings.Detection.Width = 640;
        settings.Detection.Height = 360;
        settings.Detection.FramesPerSecond = 2;
        settings.Detection.ConfidenceThreshold = -0.01f;
        settings.Detection.TriggerHitCount = 1;
        settings.Recording.RollingSegmentSeconds = 1;
        settings.Recording.PreEventSeconds = 30;
        settings.Recording.EventPartDurationMinutes = 10;

        var camera = new ValidatedCameraOptions(
            "smoke-camera",
            fixturePath,
            fixturePath,
            Roi: null,
            RetentionDays: null,
            Settings: new CameraOptions());
        var service = new ValidatedActivityRecordingOptions(
            ffmpegPath,
            settings,
            [camera],
            Errors: []);

        return new SmokeFixture(
            fixturePath,
            rollingDirectory,
            eventDirectory,
            Path.Combine(_rootDirectory.FullName, "activity-recording.db"),
            settings,
            service,
            new RollingSegmentManager(TimeProvider.System, settings.Recording),
            new EventPartWriter(ffmpegPath, processRunner));
    }

    private static async Task<bool> DetectsGeneratedFrameAsync(
        IOutputProcessRunner outputRunner,
        IModelSession modelSession,
        SmokeFixture fixture,
        string ffmpegPath)
    {
        var process = await outputRunner.StartAsync(
            new FfmpegCommand(
                ffmpegPath,
                [
                    "-nostdin",
                    "-i",
                    fixture.VideoPath,
                    "-an",
                    "-vf",
                    $"fps={fixture.Settings.Detection.FramesPerSecond},scale={fixture.Settings.Detection.Width}:{fixture.Settings.Detection.Height}",
                    "-pix_fmt",
                    "bgr24",
                    "-f",
                    "rawvideo",
                    "pipe:1"
                ]),
                _ => { },
                CancellationToken.None);
        await using (process)
        {
            var detector = new PersonDetector(
                modelSession,
                fixture.Settings.Detection,
                fixture.Service.Cameras[0].Roi);
            var stateMachine = new EventStateMachine(new EventStateMachineOptions
            {
                TriggerHitCount = fixture.Settings.Detection.TriggerHitCount,
                TriggerWindowSeconds = fixture.Settings.Detection.TriggerWindowSeconds
            });
            var firstTimestampUtc = DateTimeOffset.UtcNow;
            var frameIndex = 0;

            while (stateMachine.State != CameraEventState.Recording)
            {
                ImageFrame frame;
                try
                {
                    frame = await BgrFrameReader.ReadFrameAsync(
                        process.StandardOutput,
                        fixture.Settings.Detection.Width,
                        fixture.Settings.Detection.Height,
                        firstTimestampUtc.AddMilliseconds(500 * frameIndex),
                        CancellationToken.None);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                var result = await Task.Run(() => detector.Detect(frame), CancellationToken.None);
                stateMachine.Process(new DetectionSample(
                    result.TimestampUtc,
                    result.People.Count > 0,
                    result.People.Select(person => person.Confidence).FirstOrDefault()));
                frameIndex++;
            }

            if (stateMachine.State == CameraEventState.Recording)
            {
                // The generated fixture outlasts the frames needed to trigger the event.
                // Terminate here so remaining rawvideo output cannot fill the pipe.
                await process.KillTreeAsync(CancellationToken.None);
            }
            else
            {
                var exitCode = await process.WaitForExitAsync(CancellationToken.None);
                exitCode.Should().Be(0);
            }

            return stateMachine.State == CameraEventState.Recording;
        }
    }

    private static async Task<string> WriteFixturePartAsync(
        SmokeFixture fixture)
    {
        var reservation = fixture.SegmentManager.BeginEvent(
            fixture.RollingDirectory,
            fixture.EventDirectory,
            DateTimeOffset.UtcNow);
        reservation.CompletedSegments.Should().NotBeEmpty();

        var result = await fixture.PartWriter.WriteAsync(
            new EventPartRequest(
                Guid.NewGuid(),
                fixture.Service.Cameras[0].Name,
                PartIndex: 0,
                fixture.EventDirectory,
                reservation.CompletedSegments),
            CancellationToken.None);
        File.Exists(result.FinalPath).Should().BeTrue();
        return result.FinalPath;
    }

    private static async Task AssertPlayableAsync(string ffprobePath, string partPath)
    {
        Path.IsPathRooted(partPath).Should().BeTrue();
        new FileInfo(partPath).Length.Should().BeGreaterThan(0);

        using var probe = new Process
        {
            StartInfo = new ProcessStartInfo(ffprobePath)
            {
                ArgumentList =
                {
                    "-v",
                    "error",
                    "-show_entries",
                    "format=format_name,duration",
                    "-of",
                    "csv=p=0",
                    partPath
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        probe.Start();
        var standardOutputTask = probe.StandardOutput.ReadToEndAsync();
        var standardErrorTask = probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync(CancellationToken.None);
        var output = (await standardOutputTask).Trim();
        var diagnostics = (await standardErrorTask).Trim();

        probe.ExitCode.Should().Be(
            0,
            "ffprobe failed. Diagnostics:\n{0}",
            diagnostics);
        output.Should().Contain("mov,mp4", "the part must use the MP4 container family");
        var fields = output.Split(',', StringSplitOptions.TrimEntries);
        fields.Should().HaveCountGreaterThanOrEqualTo(2);
        double.TryParse(
            fields[^1],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var duration).Should().BeTrue("ffprobe must report a numeric duration");
        duration.Should().BeGreaterThan(0);
    }

    private static async Task RunProcessAsync(
        ProcessRunner runner,
        string fileName,
        IReadOnlyList<string> arguments)
    {
        var diagnostics = new List<string>();
        var process = await runner.StartAsync(
            new FfmpegCommand(fileName, arguments),
            line =>
            {
                lock (diagnostics)
                {
                    diagnostics.Add(line);
                    while (diagnostics.Count > 32)
                        diagnostics.RemoveAt(0);
                }
            },
            CancellationToken.None);
        await using (process)
        {
            var exitCode = await process.WaitForExitAsync(CancellationToken.None);
            exitCode.Should().Be(
                0,
                "{0} failed. Diagnostics:\n{1}",
                fileName,
                string.Join('\n', diagnostics));
        }
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ""] : [""];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string? FindModelPath()
    {
        var repository = FindRepositoryRoot();
        string[] candidates =
        [
            Path.Combine(repository.FullName, "models", "person-yolo11n.onnx"),
            Path.Combine(repository.FullName, "src", "ActivityRecordingService", "models", "person-yolo11n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "person-yolo11n.onnx")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, ".git")) &&
               !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record SmokeResult(string PartPath, Guid? EventId);

    private sealed record SmokeFixture(
        string VideoPath,
        string RollingDirectory,
        string EventDirectory,
        string DatabasePath,
        ActivityRecordingOptions Settings,
        ValidatedActivityRecordingOptions Service,
        RollingSegmentManager SegmentManager,
        EventPartWriter PartWriter);
}
