using Airside.Core.Domains;

namespace Airside.Data.Entities;

/// <summary>
/// A hostname routed to an application.
/// </summary>
/// <remarks>
/// <para>
/// The database is the source of truth for routing, not Caddy. Routes added
/// through Caddy's admin API do not survive the proxy container being replaced,
/// so Airside reasserts every domain at startup and on a timer. That also means a
/// proxy replaced during an update comes back serving rather than empty.
/// </para>
/// <para>
/// Certificate material is never stored on this row. The private key lives in the
/// secret store and is referenced by id, on the same path as database credentials
/// — encrypted at rest, masked in responses, and audited on reveal.
/// </para>
/// </remarks>
public class Domain : Entity, ISoftDeletable
{
    public Guid ApplicationId { get; set; }

    public Application Application { get; set; } = null!;

    /// <summary>
    /// The punycode form, lowercased. Unique across the host.
    /// </summary>
    /// <remarks>
    /// Comparison and routing always use this rather than
    /// <see cref="DisplayHostname"/>. Two names can look identical and differ in
    /// codepoints, so comparing the display form would let one domain shadow
    /// another.
    /// </remarks>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>The Unicode form, for display only. Equal to <see cref="Hostname"/> for ASCII names.</summary>
    public string DisplayHostname { get; set; } = string.Empty;

    /// <summary>eTLD+1, stored so rate-limit accounting does not recompute it on every query.</summary>
    public string RegisteredDomain { get; set; } = string.Empty;

    public bool IsPrimary { get; set; }

    /// <summary>Required, with no default. See <see cref="Core.Domains.TlsMode"/>.</summary>
    public TlsMode TlsMode { get; set; }

    public DomainStatus Status { get; set; } = DomainStatus.Pending;

    /// <summary>What Caddy's admin API knows this route as.</summary>
    public string? RouteId { get; set; }

    /// <summary>
    /// The stored private key and chain, for <see cref="Core.Domains.TlsMode.Manual"/>.
    /// </summary>
    /// <remarks>
    /// A reference, never the material. Putting a key on this row would put it in
    /// every query result, every projection, and eventually a log line.
    /// </remarks>
    public Guid? CertificateSecretId { get; set; }

    public string? CertificateIssuer { get; set; }

    public string? CertificateSubject { get; set; }

    /// <summary>SANs as stored, newline-separated. Shown on the detail view.</summary>
    public string? CertificateSans { get; set; }

    public string? CertificateFingerprint { get; set; }

    public DateTime? CertificateNotBefore { get; set; }

    public DateTime? CertificateNotAfter { get; set; }

    /// <summary>
    /// False for <see cref="Core.Domains.TlsMode.Manual"/>, and that is the whole point.
    /// </summary>
    /// <remarks>
    /// Nothing renews an uploaded certificate. Ninety days after a successful
    /// setup the site goes down, and the only warning anyone gets is the one
    /// Airside chooses to send.
    /// </remarks>
    public bool CertificateAutoRenew { get; set; } = true;

    /// <summary>
    /// True when the certificate came from Let's Encrypt's staging environment.
    /// </summary>
    /// <remarks>
    /// Untrusted by every browser. Tracked separately from validity so the domain
    /// can be shown as explicitly untrusted rather than healthy, which is what a
    /// bare "certificate present" check would report.
    /// </remarks>
    public bool CertificateIsStaging { get; set; }

    public DateTime? LastCertificateCheckAt { get; set; }

    public DateTime? LastValidationAt { get; set; }

    /// <summary>The last pre-flight report as JSON, so the UI can show it without re-running the checks.</summary>
    public string? LastValidationJson { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Set when this hostname only redirects to another domain.</summary>
    public Guid? RedirectToDomainId { get; set; }

    public bool HstsEnabled { get; set; }

    public int HstsMaxAgeSeconds { get; set; } = 31536000;

    public bool HstsIncludeSubdomains { get; set; }

    /// <summary>
    /// Submission to the browser preload list, which is effectively irreversible.
    /// </summary>
    /// <remarks>
    /// Removal takes months and requires the domain to keep serving valid HTTPS
    /// throughout. A user who enables this and later needs plain HTTP has bricked
    /// the hostname in every major browser, including for subdomains they may not
    /// manage here.
    /// </remarks>
    public bool HstsPreload { get; set; }

    /// <summary>
    /// When the route was withdrawn, before the row is finally removed.
    /// </summary>
    /// <remarks>
    /// A grace period rather than an immediate delete: re-attaching within it
    /// reuses the existing certificate instead of asking the authority for a new
    /// one, which matters because a mistaken detach and re-attach otherwise costs
    /// a duplicate-certificate slot.
    /// </remarks>
    public DateTime? DetachedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// One ACME issuance attempt, kept for rate-limit accounting.
/// </summary>
/// <remarks>
/// Airside keeps its own ledger because ACME offers no way to ask how much
/// headroom remains — the server answers only by refusing, a week after it would
/// have been useful to know.
/// </remarks>
public class IssuanceAttempt : Entity
{
    public string Hostname { get; set; } = string.Empty;

    /// <summary>eTLD+1. Let's Encrypt's weekly certificate limit is counted against this, not the hostname.</summary>
    public string RegisteredDomain { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public bool Staging { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>Parsed from the ACME response, so the user sees a real time rather than "later".</summary>
    public DateTime? RetryAfter { get; set; }

    public DateTime AttemptedAt { get; set; }
}

/// <summary>
/// Stored certificate material for a <see cref="Core.Domains.TlsMode.Manual"/> domain.
/// </summary>
/// <remarks>
/// A separate table rather than columns on <see cref="Domain"/>, because the
/// private key must not travel with every domain query, projection, and list
/// response. Kept out of the way, it can only be read by code that asked for it
/// by name — which is the difference between a key that is hard to leak and one
/// that is merely not logged today.
/// </remarks>
public class DomainCertificate : Entity
{
    public Guid DomainId { get; set; }

    /// <summary>Leaf first, then intermediates. Normalised at upload rather than trusted.</summary>
    public string ChainPem { get; set; } = string.Empty;

    /// <summary>Encrypted with the Data Protection key ring, exactly as database passwords are.</summary>
    public string EncryptedPrivateKey { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTime NotBefore { get; set; }

    public DateTime NotAfter { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public DateTime UploadedAt { get; set; }
}
