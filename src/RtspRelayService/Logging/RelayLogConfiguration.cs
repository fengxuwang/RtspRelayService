using System.Globalization;
using System.Text;
using RtspRelayService.Configuration;
using Serilog;
using Serilog.Events;

namespace RtspRelayService.Logging;

public static class RelayLogConfiguration
{
    private const long FileSizeLimitBytes = 100 * 1024 * 1024;

    public static LoggerConfiguration ConfigureFileSinks(
        LoggerConfiguration configuration,
        string logDirectory,
        IEnumerable<StreamOptions> streams,
        int retentionDays)
    {
        var retention = TimeSpan.FromDays(retentionDays);
        AddFileSink(configuration, Path.Combine(logDirectory, "relay-.log"), retention);

        foreach (var stream in streams)
        {
            var streamName = stream.Name;
            var path = Path.Combine(
                logDirectory,
                "cameras",
                $"{GetSafeStreamFileName(streamName)}-.log");
            configuration.WriteTo.Logger(nested =>
            {
                nested.Filter.ByIncludingOnly(logEvent => IsForStream(logEvent, streamName));
                AddFileSink(nested, path, retention);
            });
        }

        return configuration;
    }

    public static string GetSafeStreamFileName(string streamName)
    {
        var result = new StringBuilder(streamName.Length);
        foreach (var character in streamName)
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '-')
                result.Append(character);
            else
                result.Append('_').Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static bool IsForStream(LogEvent logEvent, string streamName) =>
        logEvent.Properties.TryGetValue("Stream", out var property)
        && property is ScalarValue { Value: string value }
        && string.Equals(value, streamName, StringComparison.OrdinalIgnoreCase);

    private static void AddFileSink(
        LoggerConfiguration configuration,
        string path,
        TimeSpan retention) =>
        configuration.WriteTo.File(
            path,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: FileSizeLimitBytes,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: null,
            retainedFileTimeLimit: retention);
}
