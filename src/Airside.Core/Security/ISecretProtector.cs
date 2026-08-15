using Airside.Core.Common;

namespace Airside.Core.Security;

/// <summary>
/// Encrypts secrets at rest.
/// </summary>
/// <remarks>
/// <para>
/// Backed by ASP.NET Core Data Protection with the key ring on a host bind mount
/// so it survives container replacement. Protected payloads carry their own key
/// identifier, so no separate key-version column is needed anywhere.
/// </para>
/// <para>
/// The threat model, stated plainly: this protects against exfiltration of the
/// control-plane database — a stolen dump, a backup copied off the box. It does
/// <em>not</em> protect against host root, because the key ring lives on the host
/// and the process that decrypts it runs there. Anyone holding the Docker socket
/// already has root-equivalent reach. Overpromising here would be worse than not
/// encrypting at all, because it would misdirect someone's threat modelling.
/// </para>
/// </remarks>
public interface ISecretProtector
{
    string Protect(Secret value);

    /// <summary>
    /// Fails rather than throws when the payload cannot be decrypted — a key ring
    /// restored from a different instance is a recoverable operator error, and it
    /// should surface as an actionable message, not a 500.
    /// </summary>
    Result<Secret> Unprotect(string protectedValue);
}

/// <summary>Generates credentials. Centralised so nothing reaches for <c>Random</c>.</summary>
public interface ISecretGenerator
{
    /// <summary>
    /// A cryptographically random password.
    /// </summary>
    /// <remarks>
    /// Excludes characters that break naive connection-string parsers in engine
    /// clients we do not control. This narrows the alphabet, so the default length
    /// compensates — the goal is entropy, not character variety theatre.
    /// </remarks>
    Secret GeneratePassword(int length = 32);

    /// <summary>A one-time setup or invitation token. Only its hash is ever stored.</summary>
    Secret GenerateToken(int byteLength = 32);
}
