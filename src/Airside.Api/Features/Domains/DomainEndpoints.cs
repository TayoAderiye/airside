using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Features.Applications;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Domains;
using Airside.Core.Jobs;
using Airside.Core.Proxy;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Dns;
using Airside.Runtime.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Domains;

internal static class DomainEndpoints
{
    public static IEndpointRouteBuilder MapDomainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tls-modes", GetModes)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainRead);

        app.MapGet("/api/v1/applications/{id:guid}/domains", ListAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainRead);

        app.MapPost("/api/v1/applications/{id:guid}/domains", AddAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage)

            // ACME quota is a shared, exhaustible resource. Without a limit here
            // one script can burn a week's issuance for a whole registered domain.
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        // A dry run. Deliberately available before a domain exists so the checks
        // can be shown while the hostname is still being typed.
        app.MapPost("/api/v1/domains/preflight", PreflightAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage);

        app.MapPost("/api/v1/domains/{domainId:guid}/recheck", RecheckAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage);

        app.MapPost("/api/v1/domains/{domainId:guid}/delete", RemoveAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage);

        app.MapPost("/api/v1/domains/{domainId:guid}/certificate", UploadCertificateAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage);

        app.MapGet("/api/v1/domains/{domainId:guid}/certificate", GetCertificateAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainRead);

        app.MapPut("/api/v1/domains/{domainId:guid}/hsts", SetHstsAsync)
            .WithTags("Domains")
            .RequirePermission(Permissions.DomainManage);

        return app;
    }

    /// <summary>The modes and what each one means, so the UI never has to hard-code the copy.</summary>
    private static Ok<IReadOnlyList<TlsModeDto>> GetModes() =>
        TypedResults.Ok<IReadOnlyList<TlsModeDto>>(
        [
            .. TlsModeDescriptor.All.Values.Select(d => new TlsModeDto(
                d.Mode.ToString().ToLowerInvariant(),
                Label(d.Mode),
                d.Summary,
                d.IsImplemented)),
        ]);

    private static string Label(TlsMode mode) => mode switch
    {
        TlsMode.Automatic => "Automatic (recommended)",
        TlsMode.AutomaticDns => "Automatic over DNS",
        TlsMode.Manual => "Upload my own certificate",
        TlsMode.External => "Terminated by a load balancer or CDN",
        TlsMode.Internal => "Self-signed (development and internal use)",
        TlsMode.OnDemand => "Issued on first request",
        _ => mode.ToString(),
    };

    private static async Task<Ok<IReadOnlyList<DomainDto>>> ListAsync(
        Guid id,
        AirsideDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var domains = await db.Domains
            .AsNoTracking()
            .Where(d => d.ApplicationId == id)
            .OrderByDescending(d => d.IsPrimary)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        return TypedResults.Ok<IReadOnlyList<DomainDto>>([.. domains.Select(d => DomainDto.From(d, now))]);
    }

    /// <summary>Runs the checks without attaching anything.</summary>
    private static async Task<Results<Ok<PreflightReportDto>, ProblemHttpResult>> PreflightAsync(
        AddDomainRequest request,
        IDomainPreflight preflight,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseMode(request.TlsMode, out var mode, out var modeError))
        {
            return modeError!.ToProblem();
        }

        if (!PublicSuffixList.TryNormalise(request.Hostname, out var hostname, out _))
        {
            return Invalid("That is not a valid hostname.").ToProblem();
        }

        var report = await preflight.RunAsync(new PreflightRequest(hostname, mode), ct).ConfigureAwait(false);

        return TypedResults.Ok(PreflightReportDto.From(report));
    }

    private static async Task<Results<Ok<PreflightReportDto>, ProblemHttpResult>> RecheckAsync(
        Guid domainId,
        AirsideDbContext db,
        IDomainPreflight preflight,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such domain.").ToProblem();
        }

        var report = await preflight
            .RunAsync(new PreflightRequest(domain.Hostname, domain.TlsMode, domain.Id), ct)
            .ConfigureAwait(false);

        // Cached so the list view can show the last outcome without re-running
        // network checks for every domain on every page load.
        domain.LastValidationAt = timeProvider.GetUtcNow().UtcDateTime;
        domain.LastValidationJson = System.Text.Json.JsonSerializer.Serialize(PreflightReportDto.From(report));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(PreflightReportDto.From(report));
    }

    /// <summary>
    /// Binds a hostname to an application.
    /// </summary>
    /// <remarks>
    /// Pre-flight runs synchronously so a mistake is a 409 with the resolved value
    /// and the expected one, rather than a job that fails minutes later with an
    /// ACME error naming none of the causes.
    /// </remarks>
    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> AddAsync(
        Guid id,
        AddDomainRequest request,
        AirsideDbContext db,
        IJobQueue jobs,
        IAuditWriter audit,
        IDomainPreflight preflight,
        IPublicSuffixList suffixes,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseMode(request.TlsMode, out var mode, out var modeError))
        {
            return modeError!.ToProblem();
        }

        if (!TlsModeDescriptor.For(mode).IsImplemented)
        {
            return new Error(
                "domain.tls_mode_unavailable",
                TlsModeDescriptor.For(mode).Summary).ToProblem();
        }

        if (!PublicSuffixList.TryNormalise(request.Hostname, out var hostname, out var display))
        {
            return Invalid("That is not a valid hostname.").ToProblem();
        }

        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);

        if (app is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such application.").ToProblem();
        }

        var report = await preflight
            .RunAsync(new PreflightRequest(hostname, mode, SkipExternalProbes: request.SkipPreflight), ct)
            .ConfigureAwait(false);

        // Conflicts and syntax always block. The rest can be overridden, because
        // an operator who knows their DNS is about to change should not be stopped
        // by a check that is only correct at this instant.
        var fatal = report.Blocking
            .Where(c => !request.SkipPreflight || IsAlwaysFatal(c.Id))
            .ToList();

        if (fatal.Count > 0)
        {
            var first = fatal[0];

            return new Error(first.Id, first.Summary, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["found"] = first.Found,
                ["expected"] = first.Expected,
                ["remedy"] = first.Remedy,
                ["retryAfter"] = first.RetryAfter,
                ["checks"] = PreflightReportDto.From(report).Checks,
            }).ToProblem();
        }

        var userId = CurrentUserId(http);

        var domain = new Domain
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = id,
            Hostname = hostname,
            DisplayHostname = display,
            RegisteredDomain = suffixes.GetRegisteredDomain(hostname) ?? hostname,
            TlsMode = mode,
            IsPrimary = !await db.Domains.AnyAsync(d => d.ApplicationId == id, ct).ConfigureAwait(false),
            Status = DomainStatus.Pending,
            RedirectToDomainId = request.RedirectToDomainId,

            // Only Automatic renews itself. Saying so on the row means the expiry
            // sweep does not have to infer it from the mode later.
            CertificateAutoRenew = mode is TlsMode.Automatic or TlsMode.Internal,
        };

        db.Domains.Add(domain);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            DomainJobTypes.Bind,
            new DomainPayload(domain.Id, Bind: true, hostname),
            id,
            userId,
            $"{DomainJobTypes.Bind}:{domain.Id}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "domain.bound",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "domain",
            ResourceId = domain.Id,
            ResourceSlugSnapshot = hostname,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tlsMode"] = mode.ToString() },
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, DomainJobTypes.Bind, id);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>
    /// Checks no override can bypass.
    /// </summary>
    /// <remarks>
    /// Everything else describes the world, which can change a minute later. These
    /// describe Airside's own state or a name that can never work, so overriding
    /// them would only produce a route that is wrong for as long as it exists.
    /// </remarks>
    private static bool IsAlwaysFatal(string checkId) => checkId is
        PreflightChecks.HostnameConflict
        or PreflightChecks.HostnameDashboard
        or PreflightChecks.HostnameSyntax
        or PreflightChecks.HostnameReserved
        or PreflightChecks.HostnameWildcard;

    private static async Task<Results<Accepted<JobAccepted>, ProblemHttpResult>> RemoveAsync(
        Guid domainId,
        AirsideDbContext db,
        IJobQueue jobs,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such domain.").ToProblem();
        }

        if (await db.Domains.AnyAsync(d => d.RedirectToDomainId == domainId, ct).ConfigureAwait(false))
        {
            return new Error(
                "domain.redirect_target_in_use",
                $"Another hostname redirects to '{domain.Hostname}'. Remove that redirect first, or "
                + "removing this would leave it pointing nowhere.").ToProblem();
        }

        var userId = CurrentUserId(http);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Detached now, deleted later. The grace period means re-attaching within
        // it reuses the existing certificate instead of spending a
        // duplicate-certificate slot on a mistake.
        domain.DetachedAt = now;
        domain.Status = DomainStatus.Detaching;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var jobId = await jobs.EnqueueAsync(
            DomainJobTypes.Unbind,
            new DomainPayload(domainId, Bind: false, domain.Hostname),
            domain.ApplicationId,
            userId,
            $"{DomainJobTypes.Unbind}:{domainId}",
            ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "domain.unbound",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "domain",
            ResourceId = domainId,
            ResourceSlugSnapshot = domain.Hostname,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        var accepted = JobAccepted.From(jobId, DomainJobTypes.Unbind, domain.ApplicationId);
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>
    /// Accepts a certificate and key, validates them, and hot-reloads the proxy.
    /// </summary>
    /// <remarks>
    /// The reload is verified afterwards by looking at what is actually being
    /// served. A 200 from the admin API means Caddy accepted the configuration,
    /// not that the new certificate is on the wire.
    /// </remarks>
    private static async Task<Results<Ok<CertificateDetailsDto>, ProblemHttpResult>> UploadCertificateAsync(
        Guid domainId,
        UploadCertificateRequest request,
        AirsideDbContext db,
        ICertificateValidator validator,
        ISecretProtector protector,
        IProxyManager proxy,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such domain.").ToProblem();
        }

        if (domain.TlsMode != TlsMode.Manual)
        {
            return new Error(
                "domain.not_manual_tls",
                $"'{domain.Hostname}' is set to {domain.TlsMode} TLS. Switch it to Manual before "
                + "uploading a certificate, or the uploaded one would never be served.").ToProblem();
        }

        var upload = new CertificateUpload(request.CertificateChainPem, new Secret(request.PrivateKeyPem));
        var validation = validator.Validate(upload, domain.Hostname);

        if (!validation.IsAcceptable || validation.Details is null)
        {
            return new Error(
                "domain.certificate_rejected",
                validation.Findings.FirstOrDefault(f => f.Severity == PreflightSeverity.Blocking)?.Summary
                    ?? "The certificate could not be used.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["findings"] = validation.Findings
                        .Select(f => new { id = f.Id, severity = f.Severity.ToString().ToLowerInvariant(), f.Summary, f.Remedy })
                        .ToList(),
                }).ToProblem();
        }

        var details = validation.Details;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var existing = await db.DomainCertificates
            .FirstOrDefaultAsync(c => c.DomainId == domainId, ct).ConfigureAwait(false);

        var record = existing ?? new DomainCertificate { Id = Guid.CreateVersion7(), DomainId = domainId };

        record.ChainPem = validation.NormalisedChainPem!;
        record.EncryptedPrivateKey = protector.Protect(upload.PrivateKeyPem);
        record.Fingerprint = details.Sha256Fingerprint;
        record.NotBefore = details.NotBefore.UtcDateTime;
        record.NotAfter = details.NotAfter.UtcDateTime;
        record.UploadedByUserId = CurrentUserId(http);
        record.UploadedAt = now;

        if (existing is null)
        {
            db.DomainCertificates.Add(record);
        }

        domain.CertificateSecretId = record.Id;
        domain.CertificateIssuer = details.Issuer;
        domain.CertificateSubject = details.Subject;
        domain.CertificateSans = string.Join('\n', details.SubjectAlternativeNames);
        domain.CertificateFingerprint = details.Sha256Fingerprint;
        domain.CertificateNotBefore = details.NotBefore.UtcDateTime;
        domain.CertificateNotAfter = details.NotAfter.UtcDateTime;

        // The point of the whole expiry-tracking apparatus: nothing renews this.
        domain.CertificateAutoRenew = false;
        domain.Status = DomainStatus.Active;
        domain.ErrorCode = null;
        domain.ErrorMessage = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await proxy.LoadCertificateAsync(
            new ManualCertificate(domain.Hostname, record.ChainPem, upload.PrivateKeyPem), ct)
            .ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "domain.certificate_uploaded",
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "domain",
            ResourceId = domainId,
            ResourceSlugSnapshot = domain.Hostname,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fingerprint"] = details.Sha256Fingerprint,
                ["notAfter"] = details.NotAfter,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(ToDetails(details, validation.Findings));
    }

    /// <summary>
    /// Reads the certificate live rather than from the cache.
    /// </summary>
    /// <remarks>
    /// The stored fields exist so a list view does not probe every domain. When
    /// somebody asks about one specifically, the honest answer is what is being
    /// served right now — a certificate Caddy believes it holds but is not
    /// presenting is exactly the failure worth surfacing.
    /// </remarks>
    private static async Task<Results<Ok<CertificateDetailsDto>, ProblemHttpResult>> GetCertificateAsync(
        Guid domainId,
        AirsideDbContext db,
        IProxyManager proxy,
        DomainStore store,
        CancellationToken ct)
    {
        var domain = await db.Domains.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such domain.").ToProblem();
        }

        if (domain.TlsMode == TlsMode.External)
        {
            return new Error(
                "domain.tls_not_managed",
                "TLS for this hostname terminates upstream, so Airside holds no certificate for it and "
                + "cannot report on one.").ToProblem();
        }

        var served = await proxy.GetCertificateAsync(domain.Hostname, ct).ConfigureAwait(false);
        await store.RecordCertificateAsync(domainId, served, ct).ConfigureAwait(false);

        if (served is null)
        {
            return new Error(
                "domain.no_certificate_served",
                "No certificate is being served for this hostname yet. For automatic certificates the "
                + "usual cause is DNS: the name has to resolve to this server before the challenge can "
                + "complete.").ToProblem();
        }

        return TypedResults.Ok(new CertificateDetailsDto(
            domain.CertificateSubject ?? domain.Hostname,
            served.Issuer,
            domain.CertificateSans?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [domain.Hostname],
            served.NotBefore,
            served.NotAfter,
            (int)Math.Floor((served.NotAfter - DateTimeOffset.UtcNow).TotalDays),
            string.Empty,
            domain.CertificateFingerprint ?? string.Empty,
            string.Empty,
            0,
            IsSelfSigned: domain.TlsMode == TlsMode.Internal,
            ChainLength: 0,
            Findings: []));
    }

    /// <summary>
    /// Turns HSTS on or off for a hostname.
    /// </summary>
    /// <remarks>
    /// Preload takes typed confirmation. Submission to the browser preload list is
    /// effectively irreversible — removal takes months and needs valid HTTPS
    /// throughout — so a user who enables it and later needs plain HTTP has
    /// bricked that hostname in every major browser.
    /// </remarks>
    private static async Task<Results<Ok<DomainDto>, ProblemHttpResult>> SetHstsAsync(
        Guid domainId,
        HstsRequest request,
        AirsideDbContext db,
        IProxyManager proxy,
        DomainStore store,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false);

        if (domain is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such domain.").ToProblem();
        }

        if (request.Enabled && request.Preload)
        {
            if (!string.Equals(request.ConfirmHostname, domain.Hostname, StringComparison.Ordinal))
            {
                return new Error(
                    "domain.preload_confirmation_required",
                    $"Type '{domain.Hostname}' to confirm. Submitting a domain to the browser preload "
                    + "list cannot be undone quickly: removal takes months, and until it completes every "
                    + "major browser will refuse to load this hostname over plain HTTP. If you may ever "
                    + "need HTTP on this name — or on any subdomain of it — do not enable preload.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["confirmField"] = "confirmHostname",
                        ["expected"] = domain.Hostname,
                    }).ToProblem();
            }

            if (!request.IncludeSubdomains)
            {
                // Browsers only accept a preload submission that covers subdomains
                // and carries a year-long max-age. Storing it without them would
                // set a directive the user believes is active and no browser
                // honours.
                return new Error(
                    "domain.preload_requires_subdomains",
                    "The preload list only accepts entries that include subdomains and use a max-age of "
                    + "at least one year. Enable includeSubDomains, and be aware that it covers every "
                    + "subdomain of this hostname — including any Airside does not manage.").ToProblem();
            }
        }

        domain.HstsEnabled = request.Enabled;
        domain.HstsMaxAgeSeconds = request.MaxAgeSeconds;
        domain.HstsIncludeSubdomains = request.IncludeSubdomains;
        domain.HstsPreload = request.Enabled && request.Preload;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var target = await store.GetAsync(domainId, ct).ConfigureAwait(false);

        if (target?.CurrentContainerName is not null)
        {
            await proxy.UpsertRouteAsync(
                new RouteSpec(
                    target.Hostname,
                    new UpstreamTarget(target.CurrentContainerName, target.ContainerPort),
                    target.TlsMode,
                    target.Hsts,
                    target.RedirectTo,
                    Maintenance: !target.ApplicationIsRunning),
                ct).ConfigureAwait(false);
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = "domain.hsts_changed",
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "domain",
            ResourceId = domainId,
            ResourceSlugSnapshot = domain.Hostname,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["enabled"] = domain.HstsEnabled,
                ["preload"] = domain.HstsPreload,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(DomainDto.From(domain, timeProvider.GetUtcNow().UtcDateTime));
    }

    private static CertificateDetailsDto ToDetails(
        CertificateDetails details, IReadOnlyList<CertificateFinding> findings) =>
        new(
            details.Subject,
            details.Issuer,
            details.SubjectAlternativeNames,
            details.NotBefore,
            details.NotAfter,
            details.DaysRemaining(DateTimeOffset.UtcNow),
            details.SerialNumber,
            details.Sha256Fingerprint,
            details.KeyAlgorithm,
            details.KeySizeBits,
            details.IsSelfSigned,
            details.ChainLength,
            [
                .. findings.Select(f => new PreflightCheckDto(
                    f.Id, f.Severity.ToString().ToLowerInvariant(), f.Summary, null, null, f.Remedy, null)),
            ]);

    private static bool TryParseMode(string? value, out TlsMode mode, out Error? error)
    {
        if (Enum.TryParse(value, ignoreCase: true, out mode))
        {
            error = null;
            return true;
        }

        // No fallback to Automatic. A caller that omitted the mode gets told, not
        // quietly given the one that starts contacting a certificate authority.
        error = new Error(
            ErrorCodes.ValidationFailed,
            "A TLS mode is required. Choose one of: "
            + string.Join(", ", TlsModeDescriptor.All.Values.Where(d => d.IsImplemented)
                .Select(d => d.Mode.ToString().ToLowerInvariant())) + ".",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "tlsMode" });

        return false;
    }

    private static Error Invalid(string message) => new(
        ErrorCodes.ValidationFailed,
        message,
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "hostname" });

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
