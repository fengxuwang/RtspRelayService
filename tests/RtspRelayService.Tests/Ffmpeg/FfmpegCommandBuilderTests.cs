using FluentAssertions;
using RtspRelayService.Configuration;
using RtspRelayService.Ffmpeg;

namespace RtspRelayService.Tests.Ffmpeg;

public sealed class FfmpegCommandBuilderTests
{
    [Fact]
    public void Build_UsesOptionalAudioAndKeepsEachUrlAsOneArgument()
    {
        var stream = new StreamOptions
        {
            Name = "front",
            InputUrl = "rtsp://user:p a&ss@camera/live?profile=1",
            OutputUrl = "rtmp://media/live/front?sign=a&expires=2"
        };

        var result = FfmpegCommandBuilder.Build(
            "/tools/ffmpeg",
            stream,
            TimeSpan.FromSeconds(10));

        result.FileName.Should().Be("/tools/ffmpeg");
        result.Arguments.Should().Equal(
            "-nostdin", "-fflags", "+genpts", "-rtsp_transport", "tcp",
            "-timeout", "10000000",
            "-i", stream.InputUrl,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "32k",
            "-f", "flv", stream.OutputUrl);
    }
}
