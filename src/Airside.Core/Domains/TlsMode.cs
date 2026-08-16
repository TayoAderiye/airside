namespace Airside.Core.Domains;

/// <summary>
/// How a hostname gets its certificate.
/// </summary>
/// <remarks>
/// <para>
/// Required on every domain, with no default. A silent default here produces the
/// worst failure state this tool has: a user who meant to terminate at
/// CloudFront gets <see cref="Automatic"/>, Caddy spends days failing an ACME
/// challenge that can never succeed, and nothing in the interface says why.
/// Making the choice explicit costs one click and removes a whole class of
/// support question.
/// </para>
/// <para>
/// Only <see cref="Automatic"/> leaves Caddy's automatic HTTPS switched on. Every
/// other mode must add the hostname to Caddy's skip list, or Caddy will pursue
/// its own issuance alongside whatever was configured — two issuers racing for
/// one hostname, which burns ACME quota and produces certificates that flap.
/// </para>
/// </remarks>
public enum TlsMode
{
    /// <summary>Caddy obtains and renews via ACME HTTP-01. The normal path.</summary>
    Automatic,

    /// <summary>
    /// ACME DNS-01, the only challenge that can validate a wildcard.
    /// </summary>
    /// <remarks>
    /// Not implemented. It needs a DNS provider plugin compiled into the Caddy
    /// image and provider credentials Airside would have to store and scope, so
    /// it is modelled but rejected at the service layer.
    /// </remarks>
    AutomaticDns,

    /// <summary>
    /// A certificate the user supplies. <b>Nothing renews it.</b>
    /// </summary>
    /// <remarks>
    /// The expiry tracking in <c>ManualCertificateExpiryService</c> is not a
    /// convenience. Ninety days after a successful setup the site goes down at
    /// whatever hour the certificate happens to expire, and the only warning the
    /// user gets is the one Airside chooses to give them.
    /// </remarks>
    Manual,

    /// <summary>
    /// TLS terminates upstream — an ALB, CloudFront, Cloudflare, or a corporate
    /// proxy. Caddy serves plain HTTP and Airside reports TLS as unknown.
    /// </summary>
    External,

    /// <summary>
    /// Caddy's own local CA. Publicly untrusted by design.
    /// </summary>
    /// <remarks>
    /// For local development, air-gapped installs, and internal hostnames that
    /// could never pass a public challenge. Also the right answer behind
    /// Cloudflare's "Full" mode, which encrypts to the origin without validating
    /// it.
    /// </remarks>
    Internal,

    /// <summary>Issued at first request. Not implemented; needs an ask endpoint.</summary>
    OnDemand,
}

/// <summary>What each mode implies, in one place rather than scattered through switches.</summary>
public sealed record TlsModeDescriptor(
    TlsMode Mode,
    bool IsImplemented,
    bool RequiresPreflight,
    bool RequiresUploadedCertificate,
    bool CaddyManagesIssuance,
    bool ServesHttpsAtTheProxy,
    string Summary)
{
    /// <summary>
    /// True when Caddy must be told to leave this hostname alone.
    /// </summary>
    /// <remarks>
    /// Every mode except <see cref="TlsMode.Automatic"/>. Caddy's automatic HTTPS
    /// is opt-out, not opt-in, so a hostname configured for manual or external
    /// TLS still gets an ACME attempt unless it is skipped explicitly.
    /// </remarks>
    public bool NeedsAutomaticHttpsSkip => Mode != TlsMode.Automatic;

    public static TlsModeDescriptor For(TlsMode mode) => All[mode];

    public static IReadOnlyDictionary<TlsMode, TlsModeDescriptor> All { get; } =
        new Dictionary<TlsMode, TlsModeDescriptor>
        {
            [TlsMode.Automatic] = new(
                TlsMode.Automatic, IsImplemented: true, RequiresPreflight: true,
                RequiresUploadedCertificate: false, CaddyManagesIssuance: true, ServesHttpsAtTheProxy: true,
                "Airside obtains and renews a free certificate automatically. The hostname must already "
                + "resolve to this server and port 80 must be reachable."),

            [TlsMode.AutomaticDns] = new(
                TlsMode.AutomaticDns, IsImplemented: false, RequiresPreflight: false,
                RequiresUploadedCertificate: false, CaddyManagesIssuance: true, ServesHttpsAtTheProxy: true,
                "Automatic issuance validated over DNS, which is the only way to cover a wildcard. "
                + "Not available yet: it needs a DNS provider plugin and provider credentials."),

            [TlsMode.Manual] = new(
                TlsMode.Manual, IsImplemented: true, RequiresPreflight: false,
                RequiresUploadedCertificate: true, CaddyManagesIssuance: false, ServesHttpsAtTheProxy: true,
                "You supply the certificate and private key. Nothing renews it — Airside tracks the expiry "
                + "date and warns you, but replacing it before it expires is your responsibility."),

            [TlsMode.External] = new(
                TlsMode.External, IsImplemented: true, RequiresPreflight: false,
                RequiresUploadedCertificate: false, CaddyManagesIssuance: false, ServesHttpsAtTheProxy: false,
                "HTTPS terminates at a load balancer or CDN in front of this server, which serves plain "
                + "HTTP. Airside cannot see the certificate and will not report on it."),

            [TlsMode.Internal] = new(
                TlsMode.Internal, IsImplemented: true, RequiresPreflight: false,
                RequiresUploadedCertificate: false, CaddyManagesIssuance: true, ServesHttpsAtTheProxy: true,
                "A self-signed certificate from Airside's own authority. Browsers will show a security "
                + "warning. For local development, air-gapped installs, and internal-only hostnames."),

            [TlsMode.OnDemand] = new(
                TlsMode.OnDemand, IsImplemented: false, RequiresPreflight: false,
                RequiresUploadedCertificate: false, CaddyManagesIssuance: true, ServesHttpsAtTheProxy: true,
                "Issued the first time each hostname is requested. Not available yet."),
        };
}

/// <summary>
/// Where a domain is in its lifecycle.
/// </summary>
/// <remarks>
/// <see cref="Expiring"/> and <see cref="Expired"/> are distinct from
/// <see cref="Failed"/> deliberately: a certificate running out is a scheduled,
/// predictable event that wants a countdown, whereas a failure wants an error.
/// Collapsing them would put "your site breaks in three days" behind the same
/// badge as "something went wrong once".
/// </remarks>
public enum DomainStatus
{
    Pending,
    Validating,
    Issuing,
    Active,
    Expiring,
    Expired,
    Failed,
    Detaching,
}
