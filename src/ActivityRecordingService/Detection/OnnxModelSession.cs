using ActivityRecordingService.Configuration;
using Microsoft.ML.OnnxRuntime;

namespace ActivityRecordingService.Detection;

public sealed class OnnxModelSession : IModelSession
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly long[] _inputShape;
    private readonly DetectionOptions _options;
    private bool _disposed;

    public OnnxModelSession(string modelPath, DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("Model path must not be empty.", nameof(modelPath));
        }

        if (options.Width <= 0 || options.Height <= 0)
        {
            throw new ArgumentException("Detection dimensions must be positive.", nameof(options));
        }

        _options = options;
        _session = new InferenceSession(modelPath);
        try
        {
            if (_session.InputMetadata.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one model input but found {_session.InputMetadata.Count}.");
            }

            _inputName = _session.InputNames.Single();
            var inputMetadata = _session.InputMetadata.Values.Single();
            var outputMetadata = _session.OutputMetadata.Values.Single();
            OnnxTensorContractValidator.Validate(
                new OnnxTensorMetadata(
                    Array.ConvertAll<int, long>(inputMetadata.Dimensions, static dimension => dimension),
                    inputMetadata.ElementType),
                new OnnxTensorMetadata(
                    Array.ConvertAll<int, long>(outputMetadata.Dimensions, static dimension => dimension),
                    outputMetadata.ElementType),
                options);
            _inputShape = [1, 3, options.Height, options.Width];
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public RawInferenceResult Run(ReadOnlySpan<float> input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var expectedValueCount = _options.Width * _options.Height * 3;
        if (input.Length != expectedValueCount)
        {
            throw new ArgumentException(
                $"Input must contain {expectedValueCount} float values.",
                nameof(input));
        }

        var inputValues = input.ToArray();
        using var inputOrtValue = OrtValue.CreateTensorValueFromMemory(
            inputValues,
            _inputShape);
        var inputs = new Dictionary<string, OrtValue>
        {
            [_inputName] = inputOrtValue
        };

        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, inputs, _session.OutputNames);
        if (outputs.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected one model output but found {outputs.Count}.");
        }

        OrtValue? outputOrtValue = null;
        foreach (var output in outputs)
        {
            outputOrtValue = output;
        }

        var outputShape = outputOrtValue!.GetTensorTypeAndShape();
        var shape = Array.ConvertAll<long, int>(
            outputShape.Shape,
            static dimension => (int)dimension);
        var values = outputOrtValue.GetTensorDataAsSpan<float>().ToArray();

        return new RawInferenceResult(values, shape);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }
}
