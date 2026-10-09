using System.Security.Cryptography;
using System.Text;
using ActivityRecordingService.Api;
using ActivityRecordingService.Configuration;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Api;

public sealed class ApiKeyAuthenticatorTests
{
    [Fact]
    public void Verify_ValidKey_ReturnsConfiguredName()
    {
        var authenticator = new ApiKeyAuthenticator();
        var configuredKey = CreateConfiguredKey("playback-client", "test-playback-client-key");

        var credentialName = authenticator.Verify("test-playback-client-key", [configuredKey]);

        credentialName.Should().Be("playback-client");
    }

    [Fact]
    public void Verify_SameLengthInvalidKey_ReturnsNull()
    {
        var authenticator = new ApiKeyAuthenticator();
        var configuredKey = CreateConfiguredKey("playback-client", "test-playback-client-key");

        var credentialName = authenticator.Verify("test-playback-client-KEY", [configuredKey]);

        credentialName.Should().BeNull();
    }

    private static ApiKeyOptions CreateConfiguredKey(string name, string key)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(key),
            salt,
            210_000,
            HashAlgorithmName.SHA256,
            32);

        return new ApiKeyOptions
        {
            Name = name,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = 210_000
        };
    }
}
