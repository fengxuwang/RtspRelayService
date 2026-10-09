using System.Diagnostics;
using ActivityRecordingService.Detection;
using ActivityRecordingService.Ffmpeg;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Ffmpeg;

public sealed class BgrFrameReaderTests
{
    [Fact]
    public async Task ReadFrame_ReturnsExactlyWidthTimesHeightTimesThreeBytes()
    {
        await using var source = new MemoryStream([10, 11, 12, 13, 14, 15]);
        var timestampUtc = DateTimeOffset.Parse("2026-09-29T01:02:03.004Z");

        var frame = await BgrFrameReader.ReadFrameAsync(
            source,
            width: 2,
            height: 1,
            timestampUtc,
            CancellationToken.None);

        frame.Width.Should().Be(2);
        frame.Height.Should().Be(1);
        frame.TimestampUtc.Should().Be(timestampUtc);
        frame.Bgr.Should().Equal([10, 11, 12, 13, 14, 15]);
    }

    [Fact]
    public async Task ReadFrame_ThrowsWhenStreamEndsEarly()
    {
        var source = new MemoryStream(new byte[5]);
        Func<Task> read = async () => await BgrFrameReader.ReadFrameAsync(
            source,
            width: 2,
            height: 1,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        try
        {
            await read.Should().ThrowAsync<EndOfStreamException>();
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReadFrame_WhenCancelled_ThrowsOperationCanceled()
    {
        var source = new MemoryStream(new byte[6]);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        Func<Task> read = async () => await BgrFrameReader.ReadFrameAsync(
            source,
            width: 2,
            height: 1,
            DateTimeOffset.UtcNow,
            cancellationSource.Token);

        try
        {
            await read.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            await source.DisposeAsync();
        }
    }
}
