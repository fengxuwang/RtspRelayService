using System.Net;
using System.Text;
using ActivityRecordingService.Api;
using ActivityRecordingService.Configuration;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Ffmpeg;
using ActivityRecordingService.Recording;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Processes;
using Serilog;

var bootstrapLogger = Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();
IReadOnlyCollection<string> configuredUrls = [];

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "ActivityRecordingService");

    var options = builder.Configuration
        .GetSection(ActivityRecordingOptions.SectionName)
        .Get<ActivityRecordingOptions>() ?? new ActivityRecordingOptions();
    configuredUrls = (options.Cameras ?? [])
        .Where(camera => camera is { Enabled: true })
        .SelectMany(camera => new[] { camera.DetectionUrl, camera.RecordUrl })
        .Where(url => !string.IsNullOrEmpty(url))
        .ToArray()!;

    if (!options.Enabled)
    {
        bootstrapLogger.Information("Activity recording is disabled.");
        var idleHostBuilder = Host.CreateApplicationBuilder(args);
        idleHostBuilder.Services.AddWindowsService(serviceOptions =>
            serviceOptions.ServiceName = "ActivityRecordingService");
        idleHostBuilder.Services.AddSerilog((_, logger) => logger
            .MinimumLevel.Information()
            .WriteTo.Console());
        var idleHost = idleHostBuilder.Build();
        await idleHost.RunAsync();
        return;
    }

    var diskSpaceMonitor = new DiskSpaceMonitor();
    var recordingSpace = builder.Environment.IsEnvironment("Testing")
        ? new DiskSpaceInfo(long.MaxValue, long.MaxValue)
        : diskSpaceMonitor.GetSpace(options.RecordingRoot);
    var validated = ActivityRecordingOptionsValidator.Validate(
        options,
        AppContext.BaseDirectory,
        recordingSpace.TotalBytes,
        recordingSpace.AvailableBytes);
    configuredUrls = validated.Cameras
        .SelectMany(camera => new[] { camera.DetectionUrl, camera.RecordUrl })
        .ToArray();

    var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    builder.Services.AddSerilog((_, logger) => logger
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logDirectory, "activity-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileTimeLimit: TimeSpan.FromDays(14)));

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(options.Api);
    builder.Services.AddSingleton<IOptions<ActivityRecordingOptions>>(
        new OptionsWrapper<ActivityRecordingOptions>(options));
    builder.Services.AddSingleton(validated);
    builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
    builder.Services.AddSingleton<IDiskSpaceMonitor>(diskSpaceMonitor);
    builder.Services.AddSingleton<IOutputProcessRunner, OutputProcessRunner>();
    builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
    builder.Services.AddSingleton<IEventRepository>(serviceProvider => new SqliteEventRepository(
        Path.Combine(validated.Settings.RecordingRoot, "activity-recording.db"),
        validated.Settings.Recording.RetentionDays));
    builder.Services.AddSingleton<IModelSession>(serviceProvider => new OnnxModelSession(
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            validated.Settings.Detection.ModelPath)),
        validated.Settings.Detection));
    builder.Services.AddSingleton<ApiKeyAuthenticator>();
    builder.Services.AddSingleton(serviceProvider => new ApiKeyRateLimiter(
        serviceProvider.GetRequiredService<TimeProvider>(),
        permitLimit: 5,
        window: TimeSpan.FromMinutes(1)));
    builder.Services.AddSingleton<JwtTokenIssuer>();
    builder.Services.AddSingleton<PlaybackUrlSigner>();
    builder.Services.AddSingleton<StartupRecovery>();
    builder.Services.AddSingleton<RecordingCapacityGuard>();
    builder.Services.AddSingleton<RetentionCleaner>();
    builder.Services.AddSingleton<RecordingHealthMonitor>();
    builder.Services.AddSingleton<IRecordingHealthMonitor>(serviceProvider =>
        serviceProvider.GetRequiredService<RecordingHealthMonitor>());

    foreach (var camera in validated.Cameras)
    {
        builder.Services.AddSingleton(serviceProvider =>
        {
            var eventRecorder = new CameraEventRecorder(
            camera,
            validated,
                serviceProvider.GetRequiredService<IEventRepository>(),
                new RollingSegmentManager(
                    serviceProvider.GetRequiredService<TimeProvider>(),
                    validated.Settings.Recording),
                new EventPartWriter(
                    validated.FfmpegPath,
                    serviceProvider.GetRequiredService<IProcessRunner>()),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ILogger<CameraEventRecorder>>());
            return new CameraPipeline(
                camera,
                validated,
                serviceProvider.GetRequiredService<IOutputProcessRunner>(),
                serviceProvider.GetRequiredService<IProcessRunner>(),
                serviceProvider.GetRequiredService<IModelSession>(),
                eventRecorder,
                serviceProvider.GetRequiredService<RecordingCapacityGuard>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ILogger<CameraPipeline>>());
        });
    }

    builder.Services.AddHostedService<ActivityRecordingHostedService>();
    builder.Services.AddRecordingApiJsonOptions();
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(jwt =>
        {
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = RecordingApiSecurity.JwtIssuer,
                ValidAudience = RecordingApiSecurity.JwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(validated.Settings.Api.JwtSigningKey)),
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        });
    builder.Services.AddAuthorization();
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(
        IPAddress.Parse(validated.Settings.Api.Host),
        validated.Settings.Api.Port));

    var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    RecordingApi.Map(app);
    await app.RunAsync();
}
catch (Exception exception)
{
    Environment.ExitCode = 1;
    bootstrapLogger.Fatal(
        "Activity recording service terminated unexpectedly ({ExceptionType}): {Error}",
        exception.GetType().FullName,
        UrlRedactor.RedactText(exception.Message, configuredUrls));
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;

public sealed class ActivityRecordingHostedService(
    IEventRepository repository,
    StartupRecovery startupRecovery,
    RetentionCleaner retentionCleaner,
    RecordingHealthMonitor healthMonitor,
    IEnumerable<CameraPipeline> pipelines,
    ILogger<ActivityRecordingHostedService> logger,
    ValidatedActivityRecordingOptions validatedOptions) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeSpan _stopTimeout = TimeSpan.FromSeconds(
        validatedOptions.Settings.ShutdownTimeoutSeconds);
    private Task _pipelineTask = Task.CompletedTask;
    private Task _cleanupTask = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await repository.InitializeAsync(cancellationToken);
        var recovery = await startupRecovery.RunAsync(cancellationToken);
        healthMonitor.SetStartupResult(recovery);

        var cleanup = await retentionCleaner.RunAsync(cancellationToken);
        healthMonitor.SetCleanupResult(cleanup);

        healthMonitor.SetPipelines(pipelines.ToArray());
        _pipelineTask = Task.WhenAll(pipelines.Select(
            pipeline => RunPipelineAsync(pipeline, logger)));
        _cleanupTask = Task.Run(() => RunCleanupLoopAsync(logger), _stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        var workers = Task.WhenAll(_pipelineTask, _cleanupTask);
        if (await Task.WhenAny(workers, Task.Delay(_stopTimeout, cancellationToken)) != workers)
            logger.LogWarning("Activity recording shutdown timed out while stopping camera pipelines.");
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RunPipelineAsync(
        CameraPipeline pipeline,
        ILogger<ActivityRecordingHostedService> hostLogger)
    {
        try
        {
            await pipeline.RunAsync(_stopping.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            hostLogger.LogError(
                "A camera pipeline terminated unexpectedly with {ExceptionType}.",
                exception.GetType().Name);
        }
    }

    private async Task RunCleanupLoopAsync(
        ILogger<ActivityRecordingHostedService> hostLogger)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                var cleanup = await retentionCleaner.RunAsync(_stopping.Token);
                healthMonitor.SetCleanupResult(cleanup);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            hostLogger.LogError(
                "Recording cleanup terminated unexpectedly with {ExceptionType}.",
                exception.GetType().Name);
        }
    }
}

public sealed class RecordingHealthMonitor(
    IEventRepository repository,
    IDiskSpaceMonitor diskSpaceMonitor,
    RecordingCapacityGuard capacityGuard,
    ValidatedActivityRecordingOptions options) : IRecordingHealthMonitor
{
    private volatile IReadOnlyList<CameraPipeline> _pipelines = [];
    private volatile RecoveryResult? _startupResult;
    private volatile CleanupResult? _cleanupResult;

    public void SetPipelines(IReadOnlyList<CameraPipeline> pipelines) => _pipelines = pipelines;

    public void SetStartupResult(RecoveryResult result) => _startupResult = result;

    public void SetCleanupResult(CleanupResult result) => _cleanupResult = result;

    public async Task<HealthResponse> GetHealthResponseAsync(CancellationToken cancellationToken)
    {
        var databaseReachable = false;
        var newEventsAllowed = false;
        CapacityDecision? capacityDecision = null;
        Exception? databaseException = null;
        Exception? capacityException = null;
        try
        {
            _ = await repository.GetRecordingBytesAsync(cancellationToken);
            databaseReachable = true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            databaseException = exception;
        }

        if (databaseReachable)
        {
            try
            {
                capacityDecision = await capacityGuard.CanStartEventAsync(
                    options.Cameras[0].Name,
                    cancellationToken);
                newEventsAllowed = capacityDecision.Allowed;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                capacityException = exception;
            }
        }

        DiskSpaceInfo? space = null;
        Exception? diskSpaceException = null;
        try
        {
            space = diskSpaceMonitor.GetSpace(options.Settings.RecordingRoot);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            diskSpaceException = exception;
        }

        var snapshots = _pipelines.Select(pipeline => pipeline.Snapshot).ToArray();
        var detection = GetProcessState(snapshots.Select(snapshot => snapshot.Detection));
        var recording = GetProcessState(snapshots.Select(snapshot => snapshot.Recording));
        var recorder = snapshots.Length == 0
            ? "NoCameras"
            : GetRecorderState(snapshots.Select(snapshot => snapshot.EventRecorder.State));
        var startupHasExceptions = _startupResult is { NewEventsAllowed: false };
        var cleanupBlocked = _cleanupResult is { NewEventsAllowed: false };
        var startupState = GetPhaseState(_startupResult?.NewEventsAllowed);
        var cleanupState = GetPhaseState(_cleanupResult?.NewEventsAllowed);
        var blockingReason = GetBlockingReason(
            _startupResult,
            _cleanupResult,
            databaseException,
            capacityException,
            diskSpaceException,
            databaseReachable,
            capacityDecision,
            detection,
            recording);
        var degraded = !databaseReachable ||
                       space is null ||
                       !newEventsAllowed ||
                       detection == CameraProcessState.Retrying ||
                       recording == CameraProcessState.Retrying ||
                       startupHasExceptions ||
                       cleanupBlocked;
        return new HealthResponse(
            degraded ? "Degraded" : "Healthy",
            "Running",
            detection == CameraProcessState.Stopped && snapshots.Length > 0 ? "Stopped" : detection.ToString(),
            recorder,
            space?.AvailableBytes ?? 0,
            databaseReachable,
            newEventsAllowed,
            startupState,
            cleanupState,
            blockingReason);
    }

    private static CameraProcessState GetProcessState(
        IEnumerable<CameraProcessState> states)
    {
        var materialized = states.ToArray();
        if (materialized.Length == 0)
            return CameraProcessState.Stopped;
        if (materialized.Any(state => state == CameraProcessState.Retrying))
            return CameraProcessState.Retrying;
        if (materialized.All(state => state == CameraProcessState.Running))
            return CameraProcessState.Running;
        if (materialized.All(state => state == CameraProcessState.Stopped))
            return CameraProcessState.Stopped;
        return CameraProcessState.Starting;
    }

    private static string GetRecorderState(
        IEnumerable<CameraEventRecorderState> states)
    {
        var materialized = states.ToArray();
        if (materialized.Any(state => state == CameraEventRecorderState.Failed))
            return "Failed";
        if (materialized.Any(state => state == CameraEventRecorderState.Recording))
            return "Recording";
        if (materialized.All(state => state == CameraEventRecorderState.Idle))
            return "Idle";
        return "Completed";
    }

    private static string GetPhaseState(bool? succeeded) => succeeded switch
    {
        null => "Pending",
        true => "Completed",
        false => "Failed"
    };

    private static string? GetBlockingReason(
        RecoveryResult? startupResult,
        CleanupResult? cleanupResult,
        Exception? databaseException,
        Exception? capacityException,
        Exception? diskSpaceException,
        bool databaseReachable,
        CapacityDecision? capacityDecision,
        CameraProcessState detection,
        CameraProcessState recording)
    {
        var reasons = new List<string>();
        if (!databaseReachable)
            reasons.Add($"DatabaseUnreachable:{databaseException?.GetType().Name ?? "Unknown"}");

        if (databaseReachable &&
            capacityDecision is null)
            reasons.Add($"CapacityCheck:{capacityException?.GetType().Name ?? "Unknown"}");

        if (diskSpaceException is not null)
            reasons.Add($"DiskSpace:{diskSpaceException.GetType().Name}");

        if (startupResult is { NewEventsAllowed: false } recovery &&
            recovery.ExceptionTypes.Count > 0)
        {
            reasons.Add(
                $"Startup recovery failed: {string.Join(", ", recovery.ExceptionTypes)}");
        }
        else if (startupResult is { NewEventsAllowed: false })
        {
            reasons.Add("Startup recovery failed.");
        }

        if (cleanupResult is { NewEventsAllowed: false } cleanup)
            reasons.Add(cleanup.BlockingReason ?? "Recording cleanup is blocked.");

        if (capacityDecision is { Allowed: false } decision)
            reasons.Add(decision.BlockingReason ?? "New event recording is blocked.");

        if (detection == CameraProcessState.Retrying)
            reasons.Add("At least one detection process is retrying.");
        if (recording == CameraProcessState.Retrying)
            reasons.Add("At least one recording process is retrying.");

        return reasons.Count == 0
            ? null
            : string.Join("; ", reasons.Distinct());
    }
}
