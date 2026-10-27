namespace ActivityRecordingService.Configuration;

public sealed class ActivityRecordingOptions
{
    public const string SectionName = "ActivityRecording";

    public bool Enabled { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg/ffmpeg.exe";
    public string RecordingRoot { get; set; } = @"D:\ActivityRecordings";
    public int InitialRestartDelaySeconds { get; set; } = 5;
    public int MaxRestartDelaySeconds { get; set; } = 60;
    public int StableRunSeconds { get; set; } = 60;
    public int InputTimeoutSeconds { get; set; } = 10;
    public int ShutdownTimeoutSeconds { get; set; } = 10;
    public DetectionOptions Detection { get; set; } = new();
    public RecordingOptions Recording { get; set; } = new();
    public ApiOptions Api { get; set; } = new();
    public List<CameraOptions> Cameras { get; set; } = [];
}

public sealed class DetectionOptions
{
    public string ModelPath { get; set; } = "models/yolo11n.onnx";
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 360;
    public int FramesPerSecond { get; set; } = 3;
    public double ConfidenceThreshold { get; set; } = 0.45;
    public int PersonClassId { get; set; }
    public int TriggerHitCount { get; set; } = 2;
    public int TriggerWindowSeconds { get; set; } = 5;
    public int MaxConcurrentInferences { get; set; } = 2;
}

public sealed class RecordingOptions
{
    public int RollingSegmentSeconds { get; set; } = 5;
    public int PreEventSeconds { get; set; } = 30;
    public int EventCooldownSeconds { get; set; } = 30;
    public int EventPartDurationMinutes { get; set; } = 10;
    public long MaxPartBytes { get; set; } = 536_870_912;
    public int MaxEventDurationHours { get; set; } = 2;
    public int RetentionDays { get; set; } = 7;
    public long MaxRecordingBytes { get; set; } = 579_820_582_400;
    public long MinFreeBytes { get; set; } = 64_424_509_440;
}

public sealed class ApiOptions
{
    public string Host { get; set; } = "10.8.0.2";
    public int Port { get; set; } = 5090;
    public string PublicBasePath { get; set; } = "/recordings";
    // Base64-encoded HMAC key shared with the 51banshou-oauth service
    // (its token.private_key); JWTs are issued there and only validated here.
    public string JwtSigningKey { get; set; } = "";
    public string PlaybackUrlSigningKey { get; set; } = "";
    public int PlaybackUrlLifetimeMinutes { get; set; } = 10;
}

public sealed class CameraOptions
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; }
    public string DetectionUrl { get; set; } = "";
    public string RecordUrl { get; set; } = "";
    public NormalizedRect? Roi { get; set; }
    public int? RetentionDays { get; set; }
}

public sealed record NormalizedRect(double X, double Y, double Width, double Height);

public sealed record ValidatedActivityRecordingOptions(
    string FfmpegPath,
    ActivityRecordingOptions Settings,
    IReadOnlyList<ValidatedCameraOptions> Cameras,
    IReadOnlyList<string> Errors);

public sealed record ValidatedCameraOptions(
    string Name,
    string DetectionUrl,
    string RecordUrl,
    NormalizedRect? Roi,
    int? RetentionDays,
    CameraOptions Settings);
