using System.Globalization;
using ActivityRecordingService.Api;
using ActivityRecordingService.Configuration;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Api;

public sealed class PlaybackUrlSignerTests
{
    private static readonly DateTimeOffset NowUtc =
        DateTimeOffset.Parse("2026-09-29T10:00:00Z");

    [Fact]
    public void Validate_AcceptsFreshUrl()
    {
        var signer = new PlaybackUrlSigner(
            new FixedTimeProvider(NowUtc),
            ValidOptions());
        var fileId = Guid.NewGuid();

        var url = signer.CreateUrl("/recordings", fileId, NowUtc.AddMinutes(10));
        var parts = ParseUrl(url);

        url.Should().StartWith($"/recordings/api/files/{fileId}/content?");
        parts.Exp.Should().Be(NowUtc.AddMinutes(10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        parts.Nonce.Should().NotBeNullOrEmpty();
        parts.Signature.Should().NotBeNullOrEmpty();
        signer.Validate(fileId, long.Parse(parts.Exp, CultureInfo.InvariantCulture), parts.Nonce, parts.Signature)
            .Should().BeTrue();
    }

    [Fact]
    public void Validate_AllowsSixtySecondsClockSkew()
    {
        var signer = new PlaybackUrlSigner(
            new FixedTimeProvider(NowUtc),
            ValidOptions());
        var fileId = Guid.NewGuid();
        var expiresAtUtc = NowUtc.AddSeconds(-30);
        var parts = ParseUrl(signer.CreateUrl("/recordings", fileId, expiresAtUtc));

        signer.Validate(fileId, long.Parse(parts.Exp, CultureInfo.InvariantCulture), parts.Nonce, parts.Signature)
            .Should().BeTrue();
    }

    [Fact]
    public void Validate_RejectsExpiredAndTamperedUrls()
    {
        var timeProvider = new FixedTimeProvider(NowUtc);
        var signer = new PlaybackUrlSigner(timeProvider, ValidOptions());
        var fileId = Guid.NewGuid();
        var parts = ParseUrl(signer.CreateUrl("/recordings", fileId, NowUtc.AddMinutes(10)));
        var exp = long.Parse(parts.Exp, CultureInfo.InvariantCulture);

        signer.Validate(Guid.NewGuid(), exp, parts.Nonce, parts.Signature).Should().BeFalse();
        signer.Validate(fileId, exp + 1, parts.Nonce, parts.Signature).Should().BeFalse();
        signer.Validate(fileId, exp, parts.Nonce + "A", parts.Signature).Should().BeFalse();
        signer.Validate(fileId, exp, parts.Nonce, ToggleFirstCharacter(parts.Signature)).Should().BeFalse();

        timeProvider.Set(NowUtc.AddMinutes(10).AddSeconds(61));
        signer.Validate(fileId, exp, parts.Nonce, parts.Signature).Should().BeFalse();
    }

    [Fact]
    public void CreateUrl_GeneratesUniqueNonces()
    {
        var signer = new PlaybackUrlSigner(new FixedTimeProvider(NowUtc), ValidOptions());
        var fileId = Guid.NewGuid();

        var first = ParseUrl(signer.CreateUrl("/recordings", fileId, NowUtc.AddMinutes(10)));
        var second = ParseUrl(signer.CreateUrl("/recordings", fileId, NowUtc.AddMinutes(10)));

        first.Nonce.Should().NotBe(second.Nonce);
    }

    private static ApiOptions ValidOptions() => new()
    {
        PlaybackUrlSigningKey = "this-is-a-valid-playback-signing-key-0123456789"
    };

    private static string ToggleFirstCharacter(string value) =>
        value[0] == 'A' ? "B" + value[1..] : "A" + value[1..];

    private static UrlParts ParseUrl(string url)
    {
        var queryIndex = url.IndexOf('?');
        queryIndex.Should().BeGreaterThanOrEqualTo(0);
        var query = url[(queryIndex + 1)..];
        var values = query.Split('&')
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => part[1]);

        return new UrlParts(values["exp"], values["nonce"], values["signature"]);
    }

    private sealed record UrlParts(string Exp, string Nonce, string Signature);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Set(DateTimeOffset value) => _now = value;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
