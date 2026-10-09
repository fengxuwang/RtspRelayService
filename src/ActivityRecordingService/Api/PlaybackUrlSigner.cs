using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ActivityRecordingService.Configuration;

namespace ActivityRecordingService.Api;

public sealed class PlaybackUrlSigner
{
    private const int NonceBytes = 16;
    private const int ClockSkewSeconds = 60;
    private static readonly byte[] FieldSeparator = "\n"u8.ToArray();

    private readonly TimeProvider _timeProvider;
    private readonly byte[] _signingKey;

    public PlaybackUrlSigner(TimeProvider timeProvider, ApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _signingKey = Encoding.UTF8.GetBytes(options.PlaybackUrlSigningKey);
        if (_signingKey.Length < 32)
        {
            Array.Clear(_signingKey);
            throw new ArgumentException("Playback signing keys must contain at least 32 bytes.", nameof(options));
        }

        _timeProvider = timeProvider;
    }

    public string CreateUrl(string publicBasePath, Guid fileId, DateTimeOffset expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(publicBasePath) || !publicBasePath.StartsWith('/'))
            throw new ArgumentException("Public base path must start with '/'.", nameof(publicBasePath));

        var exp = expiresAtUtc.ToUnixTimeSeconds();
        var nonce = EncodeBase64Url(RandomNumberGenerator.GetBytes(NonceBytes));
        var signature = EncodeBase64Url(CreateSignature(fileId, exp, nonce));
        var basePath = publicBasePath.TrimEnd('/');
        return $"{basePath}/api/files/{fileId}/content?exp={exp}&nonce={nonce}&signature={signature}";
    }

    public bool Validate(Guid fileId, long exp, string nonce, string signature)
    {
        if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(signature))
            return false;

        var nowUtc = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (exp + ClockSkewSeconds < nowUtc)
            return false;

        byte[]? presentedSignature = null;
        byte[]? expectedSignature = null;
        try
        {
            presentedSignature = DecodeBase64Url(signature);
            expectedSignature = CreateSignature(fileId, exp, nonce);
            return presentedSignature.Length == expectedSignature.Length &&
                CryptographicOperations.FixedTimeEquals(presentedSignature, expectedSignature);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (presentedSignature is not null)
                Array.Clear(presentedSignature);
            if (expectedSignature is not null)
                Array.Clear(expectedSignature);
        }
    }

    private byte[] CreateSignature(Guid fileId, long exp, string nonce)
    {
        using var hmac = new HMACSHA256(_signingKey);
        AppendField(hmac, fileId.ToString("D"));
        AppendSeparator(hmac);
        AppendField(hmac, exp.ToString(CultureInfo.InvariantCulture));
        AppendSeparator(hmac);
        AppendField(hmac, nonce);
        hmac.TransformFinalBlock([], 0, 0);
        return hmac.Hash ?? [];
    }

    private static void AppendField(HMAC hmac, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hmac.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }

    private static void AppendSeparator(HMAC hmac) =>
        hmac.TransformBlock(FieldSeparator, 0, FieldSeparator.Length, null, 0);

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new FormatException("Value is not URL-safe Base64.");

        var paddedLength = value.Length + (4 - value.Length % 4) % 4;
        var base64Characters = value.Replace('-', '+').Replace('_', '/').ToCharArray();
        var padded = new char[paddedLength];
        base64Characters.CopyTo(padded, 0);
        for (var index = base64Characters.Length; index < paddedLength; index++)
            padded[index] = '=';
        return Convert.FromBase64CharArray(padded, 0, paddedLength);
    }
}
