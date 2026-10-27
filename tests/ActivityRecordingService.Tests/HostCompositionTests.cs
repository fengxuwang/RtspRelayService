using System.Reflection;
using System.Text.Json;
using ActivityRecordingService.Api;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Recording;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActivityRecordingService.Tests;

public sealed class HostCompositionTests : IDisposable
{
    private readonly DirectoryInfo _rootDirectory =
        Directory.CreateTempSubdirectory("activity-host-composition-");
    private WebApplicationFactory<Program>? _factory;

    [Fact]
    public void ProductionProvider_BindsOptionsAndCreatesDistinctCameraRecorders()
    {
        var factory = CreateProductionFactory();
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<ActivityRecordingOptions>>();
        var pipelines = scope.ServiceProvider.GetRequiredService<IEnumerable<CameraPipeline>>().ToArray();

        options.Value.RecordingRoot.Should().Be(Path.Combine(_rootDirectory.FullName, "recordings"));
        options.Value.Api.JwtSigningKey.Should().Be("cHJvZHVjdGlvbi1qd3Qtc2lnbmluZy1rZXk=");
        options.Value.Api.PlaybackUrlSigningKey.Should().Be("production-playback-key-0123456789");
        pipelines.Should().HaveCount(2);
        GetRecorder(pipelines[0]).Should().NotBeSameAs(GetRecorder(pipelines[1]));

        scope.ServiceProvider.GetRequiredService<PlaybackUrlSigner>().Should().NotBeNull();
    }

    [Fact]
    public async Task ProductionProvider_RegistersHealthInterfaceAndMapsHealthEndpoint()
    {
        var factory = CreateProductionFactory();
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<IEventRepository>()
            .InitializeAsync(CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<IRecordingHealthMonitor>()
            .Should().NotBeNull();

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/recordings/api/health");

        response.IsSuccessStatusCode.Should().BeTrue();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("serviceState").GetString().Should().Be("Running");
    }

    [Fact]
    public async Task HealthEndpoint_ExposesStartupCleanupStatesAndBlockingReason()
    {
        var factory = CreateProductionFactory();
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<IEventRepository>()
            .InitializeAsync(CancellationToken.None);
        var monitor = scope.ServiceProvider.GetRequiredService<IRecordingHealthMonitor>();
        SetMonitorState(
            monitor,
            "SetStartupResult",
            new RecoveryResult(
                ScannedReservedFiles: 1,
                ReclaimedFiles: 0,
                FailedFiles: 1,
                DeletedTemporaryFiles: 0,
                ReclaimedBytes: 0,
                NewEventsAllowed: false,
                ExceptionTypes: ["IOException"]));
        SetMonitorState(
            monitor,
            "SetCleanupResult",
            new CleanupResult(
                DeletedEvents: 0,
                FreedBytes: 0,
                NewEventsAllowed: false,
                BlockingReason: "Minimum free disk space reached."));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/recordings/api/health");

        response.IsSuccessStatusCode.Should().BeTrue();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("startupState").GetString().Should().Be("Failed");
        document.RootElement.GetProperty("cleanupState").GetString().Should().Be("Failed");
        document.RootElement.GetProperty("blockingReason").GetString().Should().Be(
            "Startup recovery failed: IOException; Minimum free disk space reached.");
    }

    [Fact]
    public async Task HealthEndpoint_WhenCapacityCheckFails_ReturnsDegradedWithSanitizedReason()
    {
        var factory = CreateProductionFactory(new ThrowingDiskSpaceMonitor());
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<IEventRepository>()
            .InitializeAsync(CancellationToken.None);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/recordings/api/health");

        response.IsSuccessStatusCode.Should().BeTrue();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("Degraded");
        root.GetProperty("databaseReachable").GetBoolean().Should().BeTrue();
        root.GetProperty("newEventsAllowed").GetBoolean().Should().BeFalse();
        root.GetProperty("blockingReason").GetString().Should().Be(
            "CapacityCheck:InvalidOperationException; DiskSpace:InvalidOperationException");
    }

    private WebApplicationFactory<Program> CreateProductionFactory(
        IDiskSpaceMonitor? diskSpaceMonitor = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            var ffmpegPath = Path.Combine(_rootDirectory.FullName, "ffmpeg");
            var modelPath = Path.Combine(_rootDirectory.FullName, "model.onnx");
            File.WriteAllText(ffmpegPath, string.Empty);
            File.WriteAllText(modelPath, string.Empty);
            var recordingRoot = Path.Combine(_rootDirectory.FullName, "recordings");

            builder.UseSetting("ActivityRecording:Enabled", "true");
            builder.UseSetting("ActivityRecording:FfmpegPath", ffmpegPath);
            builder.UseSetting("ActivityRecording:RecordingRoot", recordingRoot);
            builder.UseSetting("ActivityRecording:Detection:ModelPath", modelPath);
            builder.UseSetting("ActivityRecording:Api:Host", "127.0.0.1");
            builder.UseSetting("ActivityRecording:Api:Port", "5099");
            builder.UseSetting("ActivityRecording:Api:JwtSigningKey", "cHJvZHVjdGlvbi1qd3Qtc2lnbmluZy1rZXk=");
            builder.UseSetting("ActivityRecording:Api:PlaybackUrlSigningKey", "production-playback-key-0123456789");
            builder.UseSetting("ActivityRecording:Cameras:0:Name", "camera-a");
            builder.UseSetting("ActivityRecording:Cameras:0:Enabled", "true");
            builder.UseSetting("ActivityRecording:Cameras:0:DetectionUrl", "rtsp://camera-a.example/stream");
            builder.UseSetting("ActivityRecording:Cameras:0:RecordUrl", "rtsp://camera-a.example/main");
            builder.UseSetting("ActivityRecording:Cameras:1:Name", "camera-b");
            builder.UseSetting("ActivityRecording:Cameras:1:Enabled", "true");
            builder.UseSetting("ActivityRecording:Cameras:1:DetectionUrl", "rtsp://camera-b.example/stream");
            builder.UseSetting("ActivityRecording:Cameras:1:RecordUrl", "rtsp://camera-b.example/main");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IDiskSpaceMonitor>();
                services.AddSingleton(diskSpaceMonitor ?? new TestDiskSpaceMonitor());
                services.RemoveAll<IModelSession>();
                services.AddSingleton<IModelSession>(new TestModelSession());
            });
        });

        _factory = factory;
        return factory;
    }

    private static CameraEventRecorder GetRecorder(CameraPipeline pipeline)
    {
        var field = typeof(CameraPipeline).GetField(
            "_eventRecorder",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field!.GetValue(pipeline).Should().BeAssignableTo<CameraEventRecorder>().Subject;
    }

    private static void SetMonitorState(object monitor, string methodName, object state) =>
        monitor.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(monitor, [state]);

    private sealed class TestDiskSpaceMonitor : IDiskSpaceMonitor
    {
        public DiskSpaceInfo GetSpace(string path) =>
            new(long.MaxValue, long.MaxValue);
    }

    private sealed class ThrowingDiskSpaceMonitor : IDiskSpaceMonitor
    {
        public DiskSpaceInfo GetSpace(string path) => throw new InvalidOperationException(
            "disk inspection failed");
    }

    private sealed class TestModelSession : IModelSession
    {
        public RawInferenceResult Run(ReadOnlySpan<float> input) => new([], []);

        public void Dispose()
        {
        }
    }

    public void Dispose()
    {
        if (_factory is not null)
        {
            try
            {
                // Release the singleton repository before disposing the factory so the
                // SQLite file handle is deterministically closed before the temp
                // directory is deleted (Windows cannot delete open files).
                if (_factory.Services.GetService<IEventRepository>() is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            _factory.Dispose();
        }

        _rootDirectory.Delete(recursive: true);
    }

    [Fact]
    public void Constructor_UsesConfiguredShutdownTimeout()
    {
        var settings = new ActivityRecordingOptions { ShutdownTimeoutSeconds = 37 };
        var validated = new ValidatedActivityRecordingOptions(
            "/usr/bin/ffmpeg",
            settings,
            [],
            []);

        using var service = new ActivityRecordingHostedService(
            null!,
            null!,
            null!,
            null!,
            [],
            NullLogger<ActivityRecordingHostedService>.Instance,
            validated);

        var field = typeof(ActivityRecordingHostedService).GetField(
            "_stopTimeout",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field!.GetValue(service).Should().Be(TimeSpan.FromSeconds(37));
    }
}
