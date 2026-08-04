namespace RtspRelayService.Configuration;

public sealed class RelayOptions
{
    public const string SectionName = "Relay";
    public string FfmpegPath { get; set; } = "ffmpeg/ffmpeg.exe";
    public int InitialRestartDelaySeconds { get; set; } = 5;
    public int MaxRestartDelaySeconds { get; set; } = 60;
    public int StableRunSeconds { get; set; } = 60;
    public int InputTimeoutSeconds { get; set; } = 10;
    public int ShutdownTimeoutSeconds { get; set; } = 10;
    public int LogRetentionDays { get; set; } = 14;
    public List<StreamOptions> Streams { get; set; } = [];
}

public sealed class StreamOptions
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string InputUrl { get; set; } = "";
    public string OutputUrl { get; set; } = "";
}

public sealed record ValidatedRelayOptions(
    string FfmpegPath,
    RelayOptions Settings,
    IReadOnlyList<StreamOptions> ValidStreams,
    IReadOnlyList<string> Errors);
