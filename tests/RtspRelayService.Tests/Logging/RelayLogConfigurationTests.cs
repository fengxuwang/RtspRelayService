using FluentAssertions;
using RtspRelayService.Configuration;
using RtspRelayService.Logging;
using Serilog;

namespace RtspRelayService.Tests.Logging;

public sealed class RelayLogConfigurationTests
{
    [Theory]
    [InlineData("camera-01", "camera-01")]
    [InlineData("front/door", "front_002Fdoor")]
    [InlineData("front\\door", "front_005Cdoor")]
    [InlineData("camera_01", "camera_005F01")]
    public void GetSafeStreamFileName_EncodesUnsafeOrAmbiguousCharacters(
        string streamName,
        string expected)
    {
        RelayLogConfiguration.GetSafeStreamFileName(streamName).Should().Be(expected);
    }

    [Fact]
    public void ConfigureFileSinks_WritesEachStreamToItsOwnFileAndAllEventsToRelayLog()
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-relay-logs-");
        try
        {
            var streams = new[]
            {
                Stream("camera-01"),
                Stream("front/door")
            };

            using (var logger = RelayLogConfiguration
                .ConfigureFileSinks(new LoggerConfiguration(), directory.FullName, streams, 14)
                .CreateLogger())
            {
                logger.Information("Service started");
                logger.Information("[{Stream}] connected", "camera-01");
                logger.Warning("[{Stream}] disconnected", "front/door");
            }

            var relayLog = ReadSingle(directory.FullName, "relay-*.log");
            relayLog.Should().Contain("Service started")
                .And.Contain("camera-01")
                .And.Contain("front/door");

            ReadSingle(Path.Combine(directory.FullName, "cameras"), "camera-01-*.log")
                .Should().Contain("camera-01")
                .And.NotContain("front/door")
                .And.NotContain("Service started");
            ReadSingle(Path.Combine(directory.FullName, "cameras"), "front_002Fdoor-*.log")
                .Should().Contain("front/door")
                .And.NotContain("camera-01")
                .And.NotContain("Service started");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static StreamOptions Stream(string name) => new()
    {
        Name = name,
        InputUrl = "rtsp://camera/live",
        OutputUrl = "rtmp://media/live/test"
    };

    private static string ReadSingle(string directory, string pattern) =>
        File.ReadAllText(Directory.GetFiles(directory, pattern).Should().ContainSingle().Which);
}
