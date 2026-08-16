using Airside.Api.Contracts;
using Airside.Core.Domains;
using Airside.Data.Entities;
using Tls = Airside.Core.Domains.TlsMode;

namespace Airside.Api.Features.Domains;

/// <param name="TlsMode">
/// Required. Deliberately has no server-side default — see
/// <see cref="Core.Domains.TlsMode"/> for why a wrong default here is the most
/// opaque failure this tool can produce.
/// </param>
public sealed record AddDomainRequest(
    string Hostname,
    string TlsMode,
    bool SkipPreflight = false,
    Guid? RedirectToDomainId = null);

/// <param name="PrivateKeyPem">
/// Held as a secret from the moment it is bound. Never echoed back, never logged,
/// and encrypted before it reaches the database.
/// </param>
public sealed record UploadCertificateRequest(string CertificateChainPem, string PrivateKeyPem);

/// <param name="Preload">
/// Requires typed confirmation, because submission to the browser preload list is
/// effectively irreversible.
/// </param>
public sealed record HstsRequest(
    bool Enabled,
    int MaxAgeSeconds = 31536000,
    bool IncludeSubdomains = false,
    bool Preload = false,
    string? ConfirmHostname = null);

public sealed record PreflightCheckDto(
    string Id,
    string Severity,
    string Summary,
    string? Found,
    string? Expected,
    string? Remedy,
    DateTimeOffset? RetryAfter)
{
    public static PreflightCheckDto From(PreflightCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        return new PreflightCheckDto(
            check.Id,
            check.Severity.ToString().ToLowerInvariant(),
            check.Summary,
            check.Found,
            check.Expected,
            check.Remedy,
            check.RetryAfter);
    }
}

public sealed record PreflightReportDto(
    string Hostname,
    bool Blocks,
    IReadOnlyList<PreflightCheckDto> Checks)
{
    public static PreflightReportDto From(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new PreflightReportDto(
            report.Hostname,
            report.Blocks,
            [.. report.Checks.Select(PreflightCheckDto.From)]);
    }
}

/// <param name="TlsManagedByAirside">
/// False for <see cref="Core.Domains.TlsMode.External"/>. The UI shows "not
/// managed by Airside" rather than a status, because an unverified green badge
/// for a certificate Airside cannot see is worse than an honest gap.
/// </param>
/// <param name="ExpiresInDays">
/// Null when nothing is being served yet. Negative when already expired, which is
/// worth showing rather than clamping to zero.
/// </param>
public sealed record DomainDto(
    Guid Id,
    Guid ApplicationId,
    string Hostname,
    string DisplayHostname,
    bool IsPrimary,
    string TlsMode,
    string Status,
    bool TlsManagedByAirside,
    bool CertificateIsStaging,
    string? CertificateIssuer,
    string? CertificateSubject,
    IReadOnlyList<string> CertificateSans,
    string? CertificateFingerprint,
    DateTimeOffset? CertificateNotBefore,
    DateTimeOffset? CertificateNotAfter,
    int? ExpiresInDays,
    bool CertificateAutoRenew,
    Guid? RedirectToDomainId,
    HstsDto? Hsts,
    DateTimeOffset? LastCheckedAt,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<WarningDto> Warnings)
{
    public static DomainDto From(Domain d, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(d);

        var descriptor = TlsModeDescriptor.For(d.TlsMode);

        var expiresIn = d.CertificateNotAfter is { } notAfter
            ? (int)Math.Floor((notAfter - now).TotalDays)
            : (int?)null;

        return new DomainDto(
            d.Id,
            d.ApplicationId,
            d.Hostname,
            string.IsNullOrEmpty(d.DisplayHostname) ? d.Hostname : d.DisplayHostname,
            d.IsPrimary,
            d.TlsMode.ToString().ToLowerInvariant(),
            d.Status.ToString().ToLowerInvariant(),
            descriptor.ServesHttpsAtTheProxy,
            d.CertificateIsStaging,
            d.CertificateIssuer,
            d.CertificateSubject,
            d.CertificateSans?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [],
            d.CertificateFingerprint,
            d.CertificateNotBefore is null ? null : new DateTimeOffset(d.CertificateNotBefore.Value, TimeSpan.Zero),
            d.CertificateNotAfter is null ? null : new DateTimeOffset(d.CertificateNotAfter.Value, TimeSpan.Zero),
            expiresIn,
            d.CertificateAutoRenew,
            d.RedirectToDomainId,
            d.HstsEnabled
                ? new HstsDto(d.HstsMaxAgeSeconds, d.HstsIncludeSubdomains, d.HstsPreload)
                : null,
            d.LastCertificateCheckAt is null ? null : new DateTimeOffset(d.LastCertificateCheckAt.Value, TimeSpan.Zero),
            d.ErrorCode,
            d.ErrorMessage,
            BuildWarnings(d, expiresIn));
    }

    private static List<WarningDto> BuildWarnings(Domain d, int? expiresIn)
    {
        var warnings = new List<WarningDto>();

        if (d.TlsMode == Tls.External)
        {
            warnings.Add(new WarningDto(
                "domain.tls_not_managed",
                "TLS terminates upstream, so Airside cannot report on the certificate's issuer, expiry, "
                + "or validity. Check that this server is not also reachable directly on port 80 from the "
                + "internet, which would let traffic bypass your proxy entirely."));
        }

        if (d.TlsMode == Tls.Internal)
        {
            warnings.Add(new WarningDto(
                "domain.self_signed",
                "This hostname is served with a self-signed certificate from Airside's own authority. "
                + "Browsers will show a security warning."));
        }

        if (d.CertificateIsStaging)
        {
            warnings.Add(new WarningDto(
                "domain.staging_certificate",
                "This certificate came from Let's Encrypt's staging environment and is trusted by no "
                + "browser. Turn off staging mode and re-issue before relying on this hostname."));
        }

        if (d.Status is DomainStatus.Pending or DomainStatus.Issuing && d.TlsMode == Tls.Automatic)
        {
            warnings.Add(new WarningDto(
                "domain.awaiting_certificate",
                "The route is registered but no certificate is being served yet. This usually resolves "
                + "within a minute of the first request."));
        }

        // Manual certificates renew only when somebody replaces them, so the
        // warning starts earlier and is about a task rather than a fault.
        if (d.TlsMode == Tls.Manual && expiresIn is <= 30)
        {
            warnings.Add(new WarningDto(
                expiresIn <= 0 ? "domain.certificate_expired" : "domain.certificate_expiring",
                expiresIn <= 0
                    ? $"This certificate expired {Math.Abs(expiresIn.Value)} days ago and browsers are "
                      + "refusing the site. Upload a replacement."
                    : $"This certificate expires in {expiresIn} days. Nothing renews it automatically — "
                      + "upload a replacement before then.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["expiresInDays"] = expiresIn }));
        }
        else if (expiresIn is <= 14)
        {
            // For automatic modes, renewal happens at 30 days. Still under 14
            // means it has been failing for a fortnight with nobody noticing.
            warnings.Add(new WarningDto(
                "domain.certificate_expiring",
                $"The certificate expires in {expiresIn} days and automatic renewal appears not to have "
                + "run. Check that the hostname still resolves here and that port 80 is reachable.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["expiresInDays"] = expiresIn }));
        }

        return warnings;
    }
}

public sealed record HstsDto(int MaxAgeSeconds, bool IncludeSubdomains, bool Preload);

public sealed record CertificateDetailsDto(
    string Subject,
    string Issuer,
    IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    int DaysRemaining,
    string SerialNumber,
    string Sha256Fingerprint,
    string KeyAlgorithm,
    int KeySizeBits,
    bool IsSelfSigned,
    int ChainLength,
    IReadOnlyList<PreflightCheckDto> Findings);

/// <summary>The modes offered in the UI, with the one-line implications each carries.</summary>
public sealed record TlsModeDto(string Value, string Label, string Summary, bool Available);
