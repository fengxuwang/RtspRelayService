using FluentAssertions;
using RtspRelayService.Ffmpeg;

namespace RtspRelayService.Tests.Ffmpeg;

public sealed class UrlRedactorTests
{
    [Theory]
    [InlineData("rtsp://admin:secret@camera/live", "rtsp://***:***@camera/live")]
    [InlineData("rtmp://media/live/key?sign=secret&expires=2", "rtmp://media/live/key?***")]
    [InlineData("rtmps://media/live/key", "rtmps://media/live/key")]
    public void RedactUrl_HidesSensitiveParts(string input, string expected) =>
        UrlRedactor.RedactUrl(input).Should().Be(expected);

    [Fact]
    public void RedactText_ReplacesConfiguredRawUrls()
    {
        const string input = "Opening rtsp://admin:secret@camera/live failed";
        var result = UrlRedactor.RedactText(input, ["rtsp://admin:secret@camera/live"]);

        result.Should().Be("Opening rtsp://***:***@camera/live failed");
    }

    [Fact]
    public void RedactText_HidesSensitiveUrlsThatWereNotLoadedFromConfiguration()
    {
        const string input =
            "Startup failed for rtsp://operator:private@camera/live and "
            + "rtmp://media/live/camera?sign=private-token";

        var result = UrlRedactor.RedactText(input, []);

        result.Should().Be(
            "Startup failed for rtsp://***:***@camera/live and "
            + "rtmp://media/live/camera?***");
    }
}
