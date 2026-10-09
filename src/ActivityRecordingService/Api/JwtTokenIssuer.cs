using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ActivityRecordingService.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace ActivityRecordingService.Api;

public sealed class JwtTokenIssuer
{
    public const string Issuer = RecordingApiSecurity.JwtIssuer;
    public const string Audience = RecordingApiSecurity.JwtAudience;

    private readonly TimeProvider _timeProvider;
    private readonly ApiOptions _options;

    public JwtTokenIssuer(TimeProvider timeProvider, ApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        if (Encoding.UTF8.GetByteCount(options.JwtSigningKey) < 32)
            throw new ArgumentException("JWT signing keys must contain at least 32 bytes.", nameof(options));
        if (options.JwtLifetimeMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "JWT lifetime must be positive.");

        _timeProvider = timeProvider;
        _options = options;
    }

    public (string Token, DateTimeOffset ExpiresAtUtc) CreateToken(string credentialName)
    {
        if (string.IsNullOrWhiteSpace(credentialName))
            throw new ArgumentException("Credential name is required.", nameof(credentialName));

        var nowUtc = _timeProvider.GetUtcNow();
        var expiresAtUtc = nowUtc.AddMinutes(_options.JwtLifetimeMinutes);
        var keyBytes = Encoding.UTF8.GetBytes(_options.JwtSigningKey);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = nowUtc.UtcDateTime,
            NotBefore = nowUtc.UtcDateTime,
            Expires = expiresAtUtc.UtcDateTime,
            Subject = new ClaimsIdentity([new Claim("client", credentialName)]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(keyBytes),
                SecurityAlgorithms.HmacSha256)
        };
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(tokenDescriptor);
        return (handler.WriteToken(token), expiresAtUtc);
    }
}
