using RtspRelayService.Configuration;
using RtspRelayService.Ffmpeg;
using RtspRelayService.Logging;
using RtspRelayService.Processes;
using RtspRelayService.Supervision;
using Serilog;

var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Log.Logger = RelayLogConfiguration.ConfigureFileSinks(
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(),
        logDirectory,
        [],
        14)
    .CreateBootstrapLogger();

IReadOnlyCollection<string> configuredUrls = [];
try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "RtspRelayService");

    var relay = builder.Configuration
        .GetRequiredSection(RelayOptions.SectionName)
        .Get<RelayOptions>()
        ?? throw new InvalidOperationException("Relay configuration is missing.");
    configuredUrls = relay.Streams
        .SelectMany(stream => new[] { stream.InputUrl, stream.OutputUrl })
        .ToArray();
    var validated = RelayOptionsValidator.Validate(relay, AppContext.BaseDirectory);
    if (!File.Exists(validated.FfmpegPath))
        throw new FileNotFoundException("FFmpeg executable was not found.", validated.FfmpegPath);

    builder.Services.AddSerilog((_, logger) =>
        RelayLogConfiguration.ConfigureFileSinks(
            logger
                .MinimumLevel.Information()
                .WriteTo.Console(),
            logDirectory,
            validated.ValidStreams,
            relay.LogRetentionDays));
    builder.Services.AddSingleton(validated);
    builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
    builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
    builder.Services.AddSingleton<IStreamSupervisorFactory, StreamSupervisorFactory>();
    builder.Services.AddHostedService<RelayWorker>();

    await builder.Build().RunAsync();
}
catch (Exception exception)
{
    Environment.ExitCode = 1;
    Log.Fatal(
        "RTSP relay service terminated unexpectedly ({ExceptionType}): {Error}",
        exception.GetType().FullName,
        UrlRedactor.RedactText(exception.Message, configuredUrls));
}
finally
{
    Log.CloseAndFlush();
}
