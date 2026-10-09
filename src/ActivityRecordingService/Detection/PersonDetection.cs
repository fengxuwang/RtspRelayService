namespace ActivityRecordingService.Detection;

public sealed record PersonDetection(
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height);
