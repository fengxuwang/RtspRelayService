namespace ActivityRecordingService.Detection;

public interface IModelSession : IDisposable
{
    RawInferenceResult Run(ReadOnlySpan<float> input);
}
