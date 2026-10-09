using ActivityRecordingService.Configuration;

namespace ActivityRecordingService.Detection;

public static class ImagePreprocessor
{
    public static float[] ToNchw(ImageFrame frame, DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ArgumentException("Frame dimensions must be positive.", nameof(frame));
        }

        if (options.Width <= 0 || options.Height <= 0)
        {
            throw new ArgumentException("Detection dimensions must be positive.", nameof(options));
        }

        if (frame.Width != options.Width || frame.Height != options.Height)
        {
            throw new ArgumentException(
                $"Frame dimensions ({frame.Width}x{frame.Height}) must match detection dimensions " +
                $"({options.Width}x{options.Height}).",
                nameof(frame));
        }

        var expectedByteCount = frame.Width * frame.Height * 3;
        if (frame.Bgr.Length != expectedByteCount)
        {
            throw new ArgumentException(
                $"Frame data must contain {expectedByteCount} BGR bytes.",
                nameof(frame));
        }

        var pixelCount = frame.Width * frame.Height;
        var result = new float[pixelCount * 3];

        for (var channel = 0; channel < 3; channel++)
        {
            var channelOffset = channel * pixelCount;
            for (var pixel = 0; pixel < pixelCount; pixel++)
            {
                result[channelOffset + pixel] = frame.Bgr[pixel * 3 + channel] / 255f;
            }
        }

        return result;
    }
}
