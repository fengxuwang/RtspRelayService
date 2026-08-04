using Microsoft.Extensions.Options;

namespace RtspRelayService.Configuration;

public static class RelayOptionsValidator
{
    public static ValidatedRelayOptions Validate(RelayOptions options, string baseDirectory)
    {
        var globalErrors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.FfmpegPath))
            globalErrors.Add("FfmpegPath is required.");
        if (options.InitialRestartDelaySeconds <= 0)
            globalErrors.Add("InitialRestartDelaySeconds must be positive.");
        if (options.MaxRestartDelaySeconds < options.InitialRestartDelaySeconds)
            globalErrors.Add("MaxRestartDelaySeconds must be at least InitialRestartDelaySeconds.");
        if (options.StableRunSeconds <= 0 || options.ShutdownTimeoutSeconds <= 0)
            globalErrors.Add("StableRunSeconds and ShutdownTimeoutSeconds must be positive.");
        if (options.InputTimeoutSeconds <= 0)
            globalErrors.Add("InputTimeoutSeconds must be positive.");
        if (options.LogRetentionDays <= 0)
            globalErrors.Add("LogRetentionDays must be positive.");

        var enabled = options.Streams.Where(x => x.Enabled).ToList();
        var duplicates = enabled
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Count() > 1)
            .Select(x => x.Key)
            .ToArray();
        if (duplicates.Length > 0)
            globalErrors.Add($"Found duplicate stream names: {string.Join(", ", duplicates)}.");
        if (globalErrors.Count > 0)
            throw new OptionsValidationException(RelayOptions.SectionName, typeof(RelayOptions), globalErrors);

        var valid = new List<StreamOptions>();
        var streamErrors = new List<string>();
        foreach (var stream in enabled)
        {
            var errors = ValidateStream(stream);
            if (errors.Count == 0)
                valid.Add(stream);
            else
                streamErrors.Add($"{stream.Name}: {string.Join(" ", errors)}");
        }

        if (valid.Count == 0)
            throw new OptionsValidationException(
                RelayOptions.SectionName,
                typeof(RelayOptions),
                ["No valid enabled streams remain."]);

        var path = Path.IsPathRooted(options.FfmpegPath)
            ? options.FfmpegPath
            : Path.Combine(baseDirectory, options.FfmpegPath);
        return new(Path.GetFullPath(path), options, valid, streamErrors);
    }

    private static List<string> ValidateStream(StreamOptions stream)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(stream.Name))
            errors.Add("Name is required.");
        if (!HasScheme(stream.InputUrl, "rtsp", "rtsps"))
            errors.Add("InputUrl must use rtsp or rtsps.");
        if (!HasScheme(stream.OutputUrl, "rtmp", "rtmps"))
            errors.Add("OutputUrl must use rtmp or rtmps.");
        return errors;
    }

    private static bool HasScheme(string value, params string[] schemes) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);
}
