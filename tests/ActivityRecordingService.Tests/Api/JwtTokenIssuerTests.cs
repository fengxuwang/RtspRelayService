using System.IdentityModel.Tokens.Jwt;
using ActivityRecordingService.Api;
using ActivityRecordingService.Configuration;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Api;

public sealed class JwtTokenIssuerTests
{
    private static readonly DateTimeOffset NowUtc =
        DateTimeOffset.Parse("2026-09-29T10:00:00Z");

    [Fact]
    public void CreateToken_UsesConfiguredIssuerAudienceClientAndLifetime()
    {
        var issuer = new JwtTokenIssuer(
            new FixedTimeProvider(NowUtc),
            ValidOptions());

        var result = issuer.CreateToken("playback-client");
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);

        token.Issuer.Should().Be(JwtTokenIssuer.Issuer);
        token.Audiences.Should().Contain(JwtTokenIssuer.Audience);
        token.Claims.Should().Contain(claim =>
            claim.Type == "client" &&
            claim.Value == "playback-client");
        token.ValidFrom.Should().Be(NowUtc.UtcDateTime);
        token.ValidTo.Should().Be(NowUtc.AddMinutes(15).UtcDateTime);
        result.ExpiresAtUtc.Should().Be(NowUtc.AddMinutes(15));
    }

    private static ApiOptions ValidOptions() => new()
    {
        JwtSigningKey = "this-is-a-valid-jwt-signing-key-0123456789",
        JwtLifetimeMinutes = 15
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
