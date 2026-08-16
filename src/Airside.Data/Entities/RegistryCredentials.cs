namespace Airside.Data.Entities;

/// <summary>
/// A login for a container registry.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by registry host rather than attached to a workload. A token for
/// <c>ghcr.io</c> works for every image on it, and asking an operator to attach
/// the same credential to each application separately produces copies that then
/// have to be rotated separately — which is how one of them ends up stale and a
/// deployment fails months later for no visible reason.
/// </para>
/// <para>
/// The secret is encrypted with the Data Protection key ring, on the same path as
/// database passwords and certificate keys: masked in every response, revealed
/// only through an audited endpoint, never logged.
/// </para>
/// </remarks>
public class RegistryCredential : Entity, ISoftDeletable
{
    /// <summary>
    /// The registry host, normalised — <c>ghcr.io</c>, <c>docker.io</c>, <c>registry.internal:5000</c>.
    /// </summary>
    /// <remarks>
    /// Normalised on the way in, because people paste <c>https://ghcr.io/</c> or
    /// <c>ghcr.io/myorg</c> when asked for a registry. Storing what was typed
    /// would produce a credential that saves successfully and never matches an
    /// image, with nothing to explain why.
    /// </remarks>
    public string Registry { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>What the operator called it, for telling two tokens for one registry apart.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// When a pull last used this.
    /// </summary>
    /// <remarks>
    /// The field that answers "is this still needed" before someone deletes a
    /// credential and finds out during the next deployment.
    /// </remarks>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Whether the credential worked the last time it was checked.
    /// </summary>
    /// <remarks>
    /// Registry tokens expire, and the failure surfaces as a pull that cannot find
    /// an image — which reads as a wrong tag rather than an expired token. Recording
    /// the outcome lets the credential itself say so.
    /// </remarks>
    public bool? LastVerificationSucceeded { get; set; }

    public DateTime? LastVerifiedAt { get; set; }

    public string? LastVerificationError { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTime? DeletedAt { get; set; }
}
