using ActivityRecordingService.Configuration;

namespace ActivityRecordingService.Detection;

public sealed record OnnxTensorMetadata(long[] Shape, Type ElementType);

public static class OnnxTensorContractValidator
{
    public static void Validate(
        OnnxTensorMetadata input,
        OnnxTensorMetadata output,
        DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        ValidateInput(input, options);
        ValidateOutput(output, options);
    }

    private static void ValidateInput(OnnxTensorMetadata input, DetectionOptions options)
    {
        if (input.Shape.Length != 4)
        {
            throw new InvalidOperationException(
                $"Expected input tensor rank 4 but found {input.Shape.Length}. Shape: [{FormatShape(input.Shape)}].");
        }

        if (input.ElementType != typeof(float))
        {
            throw new InvalidOperationException(
                $"Model input must be a float tensor but found {input.ElementType.FullName}.");
        }

        var dimensions = input.Shape;
        if (IsInvalidDimension(dimensions[0]) ||
            IsInvalidDimension(dimensions[1]) ||
            IsInvalidDimension(dimensions[2]) ||
            IsInvalidDimension(dimensions[3]))
        {
            throw new InvalidOperationException(
                $"Model input dimensions must be positive or dynamic (-1). Shape: [{FormatShape(dimensions)}].");
        }

        if (dimensions[0] is not (1 or -1))
        {
            throw new InvalidOperationException(
                $"Input batch must be 1 or dynamic but found {dimensions[0]}.");
        }

        if (dimensions[1] is not (3 or -1))
        {
            throw new InvalidOperationException(
                $"Input channel count must be 3 or dynamic but found {dimensions[1]}.");
        }

        if ((dimensions[2] != -1 && dimensions[2] != options.Height) ||
            (dimensions[3] != -1 && dimensions[3] != options.Width))
        {
            throw new InvalidOperationException(
                $"Model input dimensions do not match Detection Width and Height. model={dimensions[2]}x{dimensions[3]}, configured={options.Height}x{options.Width}. Configure Detection to match the model or export a matching model.");
        }
    }

    private static void ValidateOutput(OnnxTensorMetadata output, DetectionOptions options)
    {
        if (output.Shape.Length != 3)
        {
            throw new InvalidOperationException(
                $"Expected output tensor rank 3 but found {output.Shape.Length}. Shape: [{FormatShape(output.Shape)}].");
        }

        if (output.ElementType != typeof(float))
        {
            throw new InvalidOperationException(
                $"Model output must be a float tensor but found {output.ElementType.FullName}.");
        }

        var dimensions = output.Shape;
        if (IsInvalidDimension(dimensions[0]) ||
            dimensions[1] == 0 ||
            IsInvalidDimension(dimensions[2]))
        {
            throw new InvalidOperationException(
                $"Model output dimensions must be positive or dynamic (-1), and channel count must be positive. Shape: [{FormatShape(dimensions)}].");
        }

        if (dimensions[0] != 1 && dimensions[0] != -1)
        {
            throw new InvalidOperationException(
                $"Output batch must be 1 or dynamic but found {dimensions[0]}.");
        }

        if (dimensions[1] == -1)
        {
            throw new InvalidOperationException(
                "Output channel count must be static so PersonClassId can be validated.");
        }

        if (4 + options.PersonClassId >= dimensions[1])
        {
            throw new InvalidOperationException(
                $"PersonClassId {options.PersonClassId} is outside the model output channels. The model provides {dimensions[1]} channels.");
        }
    }

    private static bool IsInvalidDimension(long dimension) =>
        dimension is <= -2 or 0;

    private static string FormatShape(long[] shape) => string.Join(", ", shape);
}
