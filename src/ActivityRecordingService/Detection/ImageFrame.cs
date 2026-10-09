namespace ActivityRecordingService.Detection;

public sealed record ImageFrame(
    int Width,
    int Height,
    DateTimeOffset TimestampUtc,
    byte[] Bgr);
