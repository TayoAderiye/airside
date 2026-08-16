namespace Airside.Data.Entities;

public enum DomainState
{
    /// <summary>Route registered; the ACME challenge has not completed yet.</summary>
    Pending,

    Active,
    Failed,
}

/// <summary>
/// A hostname routed to an application.
/// </summary>
/// <remarks>
/// The database is the source of truth for routing, not Caddy. Routes added
/// through Caddy's admin API do not survive a proxy restart unless Caddy resumes
/// its own autosaved config, so Airside reconciles every domain onto the proxy at
/// startup and on a timer. That also means a proxy container replaced during an
/// update comes back with the right routes rather than an empty config.
/// </remarks>
public class Domain : Entity, ISoftDeletable
{
    public Guid ApplicationId { get; set; }

    public Application Application { get; set; } = null!;

    public string Hostname { get; set; } = string.Empty;

    public bool IsPrimary { get; set; }

    public DomainState State { get; set; } = DomainState.Pending;

    /// <summary>What Caddy's admin API knows this route as.</summary>
    public string? RouteId { get; set; }

    // Certificate fields are a cache of what is actually served, refreshed on a
    // timer. Caddy owns issuance and renewal; these exist so the UI can show
    // issuer and expiry without a probe on every page load, and so an expiry
    // notification has something to compare against.
    public string? CertificateIssuer { get; set; }

    public DateTime? CertificateNotBefore { get; set; }

    public DateTime? CertificateNotAfter { get; set; }

    public bool CertificateAutoRenew { get; set; } = true;

    public DateTime? LastCertificateCheckAt { get; set; }

    public string? ErrorCode { get; set; }

    public DateTime? DeletedAt { get; set; }
}
