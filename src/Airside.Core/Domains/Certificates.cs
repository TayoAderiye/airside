using Airside.Core.Common;

namespace Airside.Core.Domains;

/// <summary>
/// Validates a user-supplied certificate before anything is stored or served.
/// </summary>
/// <remarks>
/// Every problem caught here would otherwise surface as a TLS handshake failure
/// in a browser, which says nothing about the cause. A key that does not match
/// its certificate, a chain missing its intermediate, an expired cross-sign — all
/// of them look identical from the client side, and all of them are cheap to
/// detect at upload.
/// </remarks>
public interface ICertificateValidator
{
    /// <summary>
    /// Parses and checks a PEM bundle against a hostname.
    /// </summary>
    /// <remarks>
    /// Returns findings rather than throwing. Some are fatal, some are warnings
    /// the user may legitimately accept, and the caller needs to show both.
    /// </remarks>
    CertificateValidation Validate(CertificateUpload upload, string hostname);
}

/// <param name="PrivateKeyPem">
/// Held as a <see cref="Secret"/> from the moment it enters the process, so a log
/// line or a serialised request object cannot leak it even by accident.
/// </param>
public sealed record CertificateUpload(string CertificateChainPem, Secret PrivateKeyPem);

/// <param name="NormalisedChainPem">
/// The chain re-emitted leaf-first. Uploads arrive in every possible order and
/// some servers tolerate it; Caddy is stricter, so the order is fixed here rather
/// than hoped for.
/// </param>
public sealed record CertificateValidation(
    bool IsAcceptable,
    IReadOnlyList<CertificateFinding> Findings,
    CertificateDetails? Details,
    string? NormalisedChainPem);

public sealed record CertificateFinding(
    string Id,
    PreflightSeverity Severity,
    string Summary,
    string? Remedy = null);

public sealed record CertificateDetails(
    string Subject,
    string Issuer,
    IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string SerialNumber,
    string Sha256Fingerprint,
    string KeyAlgorithm,
    int KeySizeBits,
    bool IsSelfSigned,
    int ChainLength)
{
    public int DaysRemaining(DateTimeOffset now) => (int)Math.Floor((NotAfter - now).TotalDays);
}

/// <summary>Findings a certificate upload can produce.</summary>
public static class CertificateFindings
{
    public const string Unparseable = "certificate.unparseable";
    public const string KeyMismatch = "certificate.key_mismatch";
    public const string KeyEncrypted = "certificate.key_encrypted";
    public const string KeyWeak = "certificate.key_weak";
    public const string ChainIncomplete = "certificate.chain_incomplete";
    public const string ChainReordered = "certificate.chain_reordered";
    public const string ChainIntermediateExpired = "certificate.intermediate_expired";
    public const string Expired = "certificate.expired";
    public const string NotYetValid = "certificate.not_yet_valid";
    public const string ExpiringSoon = "certificate.expiring_soon";
    public const string HostnameNotCovered = "certificate.hostname_not_covered";
    public const string WildcardDoesNotCoverApex = "certificate.wildcard_excludes_apex";
    public const string SelfSigned = "certificate.self_signed";
}

/// <summary>
/// Counts ACME issuance attempts so Airside can warn before a limit is hit.
/// </summary>
/// <remarks>
/// <para>
/// Let's Encrypt's limits are easy for a provisioning tool to trip — a user
/// debugging one stubborn hostname can burn the failed-validation allowance in
/// minutes, and the resulting lockout looks exactly like the original problem.
/// Airside keeps its own ledger because the ACME server will not tell you how
/// close you are until you are over.
/// </para>
/// <para>
/// The numbers are configurable rather than compiled in. Let's Encrypt has
/// changed them before and will again, and a stale constant that warns too early
/// is a tool people learn to ignore.
/// </para>
/// </remarks>
public interface IIssuanceLedger
{
    Task RecordAsync(IssuanceAttemptRecord attempt, CancellationToken ct);

    /// <summary>Assesses headroom for a hostname without making an attempt.</summary>
    Task<RateLimitAssessment> AssessAsync(string hostname, bool staging, CancellationToken ct);
}

public sealed record IssuanceAttemptRecord(
    string Hostname,
    string RegisteredDomain,
    bool Succeeded,
    bool Staging,
    string? ErrorCode,
    DateTimeOffset? RetryAfter);

/// <param name="RetryAfter">
/// Parsed from the ACME response when a limit has already been hit, so the user
/// sees a real timestamp instead of "try again later".
/// </param>
public sealed record RateLimitAssessment(
    bool Exceeded,
    IReadOnlyList<PreflightCheck> Findings,
    DateTimeOffset? RetryAfter);
