namespace Airside.Core.Domains;

/// <summary>
/// Let's Encrypt's published limits, as configuration rather than constants.
/// </summary>
/// <remarks>
/// <para>
/// These change. They have changed several times, and a compiled-in number that
/// warns at the wrong threshold is worse than no warning: a tool that cries wolf
/// gets its warnings dismissed, including the one that mattered.
/// </para>
/// <para>
/// Airside counts attempts itself because the ACME protocol offers no way to ask
/// how much headroom is left. The server tells you only by refusing, at which
/// point the answer arrives a week too late.
/// </para>
/// </remarks>
public sealed class AcmeRateLimitOptions
{
    public const string Section = "Airside:Acme";

    /// <summary>Certificates per registered domain per week.</summary>
    public int CertificatesPerRegisteredDomainPerWeek { get; set; } = 50;

    /// <summary>Identical SAN sets per week. Re-issuing the same single hostname hits this first.</summary>
    public int DuplicateCertificatesPerWeek { get; set; } = 5;

    /// <summary>
    /// Failed validations per hostname per hour.
    /// </summary>
    /// <remarks>
    /// The one a user debugging a stubborn domain will actually hit, and the
    /// cruellest: the lockout that follows looks exactly like the original
    /// failure, so it reads as "still broken" rather than "stop and wait".
    /// </remarks>
    public int FailedValidationsPerHostnamePerHour { get; set; } = 5;

    /// <summary>New orders per account per three hours.</summary>
    public int NewOrdersPerThreeHours { get; set; } = 300;

    /// <summary>Warn once usage reaches this fraction of a limit.</summary>
    public double WarnAtFraction { get; set; } = 0.8;

    /// <summary>
    /// Use Let's Encrypt's staging environment.
    /// </summary>
    /// <remarks>
    /// Staging has limits high enough to iterate against, and issues certificates
    /// from an untrusted root. Anyone debugging a domain should be able to switch
    /// here rather than burning production quota — but a domain issued from
    /// staging must be labelled untrusted and re-issued against production before
    /// it counts as healthy, or the setting becomes a trap of its own.
    /// </remarks>
    public bool UseStagingDirectory { get; set; }

    public const string ProductionDirectory = "https://acme-v02.api.letsencrypt.org/directory";

    public const string StagingDirectory = "https://acme-staging-v02.api.letsencrypt.org/directory";

    public string Directory => UseStagingDirectory ? StagingDirectory : ProductionDirectory;
}
