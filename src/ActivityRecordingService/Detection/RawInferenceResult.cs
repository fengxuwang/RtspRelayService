namespace ActivityRecordingService.Detection;

public sealed record RawInferenceResult(float[] Values, int[] Shape);
