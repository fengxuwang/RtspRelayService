using ActivityRecordingService.Detection;

namespace ActivityRecordingService.Ffmpeg;

public static class BgrFrameReader
{
    public static async ValueTask<ImageFrame> ReadFrameAsync(
        Stream source,
        int width,
        int height,
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bgr = new byte[checked(width * height * 3)];
        await source.ReadExactlyAsync(bgr, cancellationToken);
        return new ImageFrame(width, height, timestampUtc, bgr);
    }
}
