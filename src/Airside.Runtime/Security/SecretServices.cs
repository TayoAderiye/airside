using System.Security.Cryptography;
using Airside.Core.Common;
using Airside.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Secret = Airside.Core.Common.Secret;

namespace Airside.Runtime.Security;

/// <summary>
/// Encrypts secrets at rest with ASP.NET Core Data Protection.
/// </summary>
/// <remarks>
/// The key ring is persisted to a host bind mount so it survives container
/// replacement — losing it makes every stored secret unrecoverable, including
/// during a self-update.
/// <para>
/// Threat model, stated plainly: this protects against exfiltration of the
/// control-plane database — a stolen dump, a backup copied off the box. It does
/// not protect against host root, because the key ring is on the host and the
/// process that decrypts it runs there. Anyone holding the Docker socket already
/// has root-equivalent reach.
/// </para>
/// </remarks>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private const string Purpose = "Airside.Secrets.v1";

    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(Secret value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _protector.Protect(value.Reveal());
    }

    public Result<Secret> Unprotect(string protectedValue)
    {
        try
        {
            return new Secret(_protector.Unprotect(protectedValue));
        }
        catch (CryptographicException)
        {
            // A key ring restored from a different instance is a recoverable
            // operator error, and it deserves an actionable message rather than a
            // 500. The exception detail is deliberately not surfaced.
            return new Error(
                "secret.undecryptable",
                "This value cannot be decrypted with the current key ring. "
                + "It was most likely encrypted by a different Airside instance.");
        }
    }
}

public sealed class SecretGenerator : ISecretGenerator
{
    /// <summary>
    /// Deliberately excludes quotes, backslashes, and the URI-reserved characters
    /// that break naive connection-string parsers in engine clients we do not
    /// control. Narrowing the alphabet costs about 0.4 bits per character, which
    /// the default length more than covers — the goal is entropy, not character
    /// variety.
    /// </summary>
    private const string Alphabet =
        "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789-_";

    public Secret GeneratePassword(int length = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 16);
        return new Secret(RandomNumberGenerator.GetString(Alphabet, length));
    }

    public Secret GenerateToken(int byteLength = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLength, 16);

        // Base64Url so the token survives a console copy-paste and a URL without
        // escaping, which is exactly how a setup token gets used.
        return new Secret(Base64UrlEncode(RandomNumberGenerator.GetBytes(byteLength)));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Hashes a setup token for storage. Only the hash is persisted, so a stolen
    /// database dump does not hand over first-run access.
    /// </summary>
    public static string HashToken(Secret token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token.Reveal())));
    }

    /// <summary>Fixed-time comparison — a token check must not leak its prefix through timing.</summary>
    public static bool TokenMatches(Secret candidate, string storedHash)
    {
        ArgumentNullException.ThrowIfNull(storedHash);

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(HashToken(candidate)),
            System.Text.Encoding.UTF8.GetBytes(storedHash));
    }
}
