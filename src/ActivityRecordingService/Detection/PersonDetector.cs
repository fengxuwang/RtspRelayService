using ActivityRecordingService.Configuration;

namespace ActivityRecordingService.Detection;

public sealed record PersonFrameResult(
    DateTimeOffset TimestampUtc,
    IReadOnlyList<PersonDetection> People);

public sealed class PersonDetector
{
    private readonly IModelSession _session;
    private readonly DetectionOptions _options;
    private readonly NormalizedRect? _roi;

    public PersonDetector(
        IModelSession session,
        DetectionOptions options,
        NormalizedRect? Roi)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.Width <= 0 || options.Height <= 0)
        {
            throw new ArgumentException("Detection dimensions must be positive.", nameof(options));
        }

        if (options.PersonClassId < 0)
        {
            throw new ArgumentException(
                "Person class id must be non-negative.",
                nameof(options));
        }

        _roi = Roi;
    }

    public PersonFrameResult Detect(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ArgumentException("Frame dimensions must be positive.", nameof(frame));
        }

        var input = ImagePreprocessor.ToNchw(frame, _options);
        var output = _session.Run(input);

        if (output.Shape.Length != 3 ||
            output.Shape[0] != 1 ||
            output.Shape[1] < 5 ||
            output.Shape[2] <= 0 ||
            4 + _options.PersonClassId >= output.Shape[1])
        {
            throw new InvalidOperationException(
                $"Unexpected YOLO output shape [{string.Join(", ", output.Shape)}].");
        }

        var anchors = output.Shape[2];
        var expectedValueCount = output.Shape[0] * output.Shape[1] * anchors;
        if (output.Values.Length != expectedValueCount)
        {
            throw new InvalidOperationException(
                $"YOLO output contains {output.Values.Length} values but its shape requires {expectedValueCount}.");
        }

        List<PersonDetection>? people = null;
        var scoreChannel = 4 + _options.PersonClassId;
        var xScale = (float)frame.Width / _options.Width;
        var yScale = (float)frame.Height / _options.Height;

        for (var anchor = 0; anchor < anchors; anchor++)
        {
            var confidence = output.Values[scoreChannel * anchors + anchor];
            if (confidence < _options.ConfidenceThreshold)
            {
                continue;
            }

            var centerX = output.Values[anchor] * xScale;
            var centerY = output.Values[anchors + anchor] * yScale;
            var width = output.Values[2 * anchors + anchor] * xScale;
            var height = output.Values[3 * anchors + anchor] * yScale;
            var x = centerX - width / 2;
            var y = centerY - height / 2;

            if (IsOutsideRoi(centerX / frame.Width, centerY / frame.Height))
            {
                continue;
            }

            people ??= [];
            people.Add(new PersonDetection(confidence, x, y, width, height));
        }

        return new PersonFrameResult(frame.TimestampUtc, people ?? []);
    }

    private bool IsOutsideRoi(float normalizedCenterX, float normalizedCenterY)
    {
        if (_roi is not { } roi)
        {
            return false;
        }

        return normalizedCenterX < roi.X ||
               normalizedCenterX > roi.X + roi.Width ||
               normalizedCenterY < roi.Y ||
               normalizedCenterY > roi.Y + roi.Height;
    }
}
