using Airside.Core.Domains;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Dns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Airside.Api.Features.Domains;

/// <inheritdoc />
public sealed class IssuanceLedger(
    AirsideDbContext db,
    IPublicSuffixList suffixes,
    IOptions<AcmeRateLimitOptions> options,
    TimeProvider timeProvider) : IIssuanceLedger
{
    public async Task RecordAsync(IssuanceAttemptRecord attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        db.IssuanceAttempts.Add(new IssuanceAttempt
        {
            Id = Guid.CreateVersion7(),
            Hostname = attempt.Hostname,
            RegisteredDomain = attempt.RegisteredDomain,
            Succeeded = attempt.Succeeded,
            Staging = attempt.Staging,
            ErrorCode = attempt.ErrorCode,
            RetryAfter = attempt.RetryAfter?.UtcDateTime,
            AttemptedAt = timeProvider.GetUtcNow().UtcDateTime,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<RateLimitAssessment> AssessAsync(string hostname, bool staging, CancellationToken ct)
    {
        var limits = options.Value;
        var now = timeProvider.GetUtcNow();

        // Staging exists precisely so people can iterate without this accounting
        // mattering. Warning about production quota while someone is deliberately
        // avoiding it would train them to ignore the warning that counts.
        if (staging || limits.UseStagingDirectory)
        {
            return new RateLimitAssessment(Exceeded: false, [], null);
        }

        var registered = suffixes.GetRegisteredDomain(hostname) ?? hostname;
        var weekAgo = now.AddDays(-7).UtcDateTime;
        var hourAgo = now.AddHours(-1).UtcDateTime;

        var attempts = await db.IssuanceAttempts
            .AsNoTracking()
            .Where(a => !a.Staging && a.AttemptedAt >= weekAgo)
            .Where(a => a.RegisteredDomain == registered || a.Hostname == hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var findings = new List<PreflightCheck>();
        DateTimeOffset? retryAfter = null;

        // A pending Retry-After from the authority beats every local count: the
        // limit has already been hit and the server has said when it resets.
        var pending = attempts
            .Where(a => a.RetryAfter is not null && a.RetryAfter > now.UtcDateTime)
            .MaxBy(a => a.RetryAfter);

        if (pending?.RetryAfter is { } until)
        {
            retryAfter = new DateTimeOffset(until, TimeSpan.Zero);

            findings.Add(new PreflightCheck(
                PreflightChecks.RateLimit, PreflightSeverity.Blocking,
                "Let's Encrypt has rate-limited this domain.",
                Found: pending.ErrorCode,
                Expected: $"available again at {retryAfter.Value:u}",
                Remedy: "Wait until the time above, or switch on staging mode in settings to keep "
                    + "debugging without using production quota. Certificates from staging are not "
                    + "trusted by browsers.",
                RetryAfter: retryAfter));

            return new RateLimitAssessment(Exceeded: true, findings, retryAfter);
        }

        var issuedThisWeek = attempts.Count(a => a.Succeeded && a.RegisteredDomain == registered);

        findings.AddRange(Assess(
            PreflightChecks.RateLimit, issuedThisWeek, limits.CertificatesPerRegisteredDomainPerWeek,
            limits.WarnAtFraction,
            $"certificates issued for {registered} in the last week",
            "Let's Encrypt allows a limited number of certificates per domain per week. The oldest "
                + "attempt drops out of the window seven days after it was made."));

        var duplicates = attempts.Count(a => a.Succeeded && a.Hostname == hostname);

        findings.AddRange(Assess(
            PreflightChecks.RateLimit, duplicates, limits.DuplicateCertificatesPerWeek,
            limits.WarnAtFraction,
            $"certificates issued for exactly '{hostname}' in the last week",
            "Re-issuing the same hostname repeatedly hits the duplicate-certificate limit first. If you "
                + "are debugging, switch on staging mode rather than reissuing against production."));

        var failuresThisHour = attempts.Count(a =>
            !a.Succeeded && a.Hostname == hostname && a.AttemptedAt >= hourAgo);

        findings.AddRange(Assess(
            PreflightChecks.RateLimit, failuresThisHour, limits.FailedValidationsPerHostnamePerHour,
            limits.WarnAtFraction,
            $"failed validations for '{hostname}' in the last hour",
            "Once this limit is reached the authority refuses further attempts for an hour, and the "
                + "refusal looks exactly like the original failure — so it reads as 'still broken' rather "
                + "than 'stop and wait'. Fix the cause before trying again."));

        return new RateLimitAssessment(
            findings.Exists(f => f.Severity == PreflightSeverity.Blocking), findings, retryAfter);
    }

    /// <summary>Emits nothing while there is headroom, a warning near the limit, and a block at it.</summary>
    private static IEnumerable<PreflightCheck> Assess(
        string id, int used, int limit, double warnAt, string subject, string explanation)
    {
        if (used >= limit)
        {
            yield return new PreflightCheck(
                id, PreflightSeverity.Blocking,
                $"The limit on {subject} has been reached.",
                Found: $"{used} of {limit}",
                Remedy: explanation);
        }
        else if (used >= limit * warnAt)
        {
            yield return new PreflightCheck(
                id, PreflightSeverity.Warning,
                $"Approaching the limit on {subject}.",
                Found: $"{used} of {limit}",
                Remedy: explanation);
        }
    }
}
