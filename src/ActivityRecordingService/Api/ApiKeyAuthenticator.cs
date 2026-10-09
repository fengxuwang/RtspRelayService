using System.Security.Cryptography;
using System.Text;
using ActivityRecordingService.Configuration;

namespace ActivityRecordingService.Api;

public sealed class ApiKeyAuthenticator
{
    public string? Verify(string? presentedKey, IReadOnlyList<ApiKeyOptions>? configuredKeys)
    {
        if (string.IsNullOrEmpty(presentedKey) || configuredKeys is null)
            return null;

        var presentedKeyBytes = Encoding.UTF8.GetBytes(presentedKey);
        try
        {
            foreach (var configuredKey in configuredKeys)
            {
                if (TryVerify(presentedKeyBytes, configuredKey, out var credentialName))
                    return credentialName;
            }
        }
        finally
        {
            Array.Clear(presentedKeyBytes);
        }

        return null;
    }

    private static bool TryVerify(
        byte[] presentedKey,
        ApiKeyOptions configuredKey,
        out string? credentialName)
    {
        credentialName = null;
        if (string.IsNullOrWhiteSpace(configuredKey.Name) || configuredKey.Iterations <= 0)
            return false;

        byte[]? salt = null;
        byte[]? expectedHash = null;
        byte[]? actualHash = null;
        try
        {
            salt = Convert.FromBase64String(configuredKey.Salt);
            expectedHash = Convert.FromBase64String(configuredKey.Hash);
            if (salt.Length == 0 || expectedHash.Length < 32)
                return false;

            actualHash = Rfc2898DeriveBytes.Pbkdf2(
                presentedKey,
                salt,
                configuredKey.Iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                return false;

            credentialName = configuredKey.Name;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (salt is not null)
                Array.Clear(salt);
            if (expectedHash is not null)
                Array.Clear(expectedHash);
            if (actualHash is not null)
                Array.Clear(actualHash);
        }
    }
}
