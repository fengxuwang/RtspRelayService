using FluentAssertions;
using Microsoft.Extensions.Options;
using RtspRelayService.Configuration;

namespace RtspRelayService.Tests.Configuration;

public sealed class RelayOptionsValidatorTests
{
    private static RelayOptions ValidOptions() => new()
    {
        FfmpegPath = "ffmpeg/ffmpeg.exe",
        Streams =
        [
            new()
            {
                Name = "camera-01",
                Enabled = true,
                InputUrl = "rtsp://user:pass@camera.local/live",
                OutputUrl = "rtmp://media.example/live/camera-01?sign=secret"
            }
        ]
    };

    [Fact]
    public void Validate_ReturnsValidEnabledStreams()
    {
        var result = RelayOptionsValidator.Validate(ValidOptions(), "/app");

        result.ValidStreams.Should().ContainSingle()
            .Which.Name.Should().Be("camera-01");
        result.Errors.Should().BeEmpty();
        result.FfmpegPath.Should().Be(Path.GetFullPath("/app/ffmpeg/ffmpeg.exe"));
    }

    [Fact]
    public void Validate_SkipsInvalidStreamAndKeepsValidStream()
    {
        var options = ValidOptions();
        options.Streams.Add(new StreamOptions
        {
            Name = "broken",
            Enabled = true,
            InputUrl = "http://wrong/input",
            OutputUrl = "rtmp://media.example/live/broken"
        });

        var result = RelayOptionsValidator.Validate(options, "/app");

        result.ValidStreams.Should().ContainSingle();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("broken");
    }

    [Fact]
    public void Validate_RejectsNamesThatDifferOnlyByCase()
    {
        var options = ValidOptions();
        options.Streams.Add(new StreamOptions
        {
            Name = "CAMERA-01",
            Enabled = true,
            InputUrl = "rtsp://camera-2/live",
            OutputUrl = "rtmp://media.example/live/camera-2"
        });

        var act = () => RelayOptionsValidator.Validate(options, "/app");

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*duplicate*camera-01*");
    }

    [Fact]
    public void Validate_ThrowsWhenNoValidEnabledStreamRemains()
    {
        var options = ValidOptions();
        options.Streams[0].Enabled = false;

        var act = () => RelayOptionsValidator.Validate(options, "/app");

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*No valid enabled streams*");
    }

    [Fact]
    public void Validate_RejectsNonPositiveLogRetentionDays()
    {
        var options = ValidOptions();
        options.LogRetentionDays = 0;

        var act = () => RelayOptionsValidator.Validate(options, "/app");

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*LogRetentionDays*positive*");
    }

    [Fact]
    public void Validate_RejectsNonPositiveInputTimeoutSeconds()
    {
        var options = ValidOptions();
        options.InputTimeoutSeconds = 0;

        var act = () => RelayOptionsValidator.Validate(options, "/app");

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*InputTimeoutSeconds*positive*");
    }
}
