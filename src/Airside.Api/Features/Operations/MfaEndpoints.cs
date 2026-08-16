using System.Security.Claims;
using System.Security.Cryptography;
using Airside.Api.Infrastructure;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Operations;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Operations;

/// <param name="ProvisioningUri">
/// The <c>otpauth://</c> URI for the QR code. Contains the shared secret, so it is
/// returned exactly once and never stored anywhere it could be read back.
/// </param>
/// <param name="RecoveryCodes">
/// Shown once and never again — only their hashes are kept. A code that can be
/// read out of the database later is a second factor a database dump defeats.
/// </param>
public sealed record MfaEnrolmentDto(
    string Secret,
    string ProvisioningUri,
    IReadOnlyList<string> RecoveryCodes);

public sealed record ConfirmMfaRequest(string Code);

public sealed record MfaStatusDto(bool Enrolled, bool Confirmed, int RecoveryCodesRemaining);

internal static class MfaEndpoints
{
    private const int RecoveryCodeCount = 10;

    public static IEndpointRouteBuilder MapMfaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/account/mfa").WithTags("Account").RequireAuthorization();

        group.MapGet("/", GetStatusAsync);
        group.MapPost("/enrol", EnrolAsync).RequireRateLimiting(RateLimitPolicies.Authentication);
        group.MapPost("/confirm", ConfirmAsync).RequireRateLimiting(RateLimitPolicies.Authentication);
        group.MapPost("/disable", DisableAsync).RequireRateLimiting(RateLimitPolicies.Authentication);

        return app;
    }

    private static async Task<Ok<MfaStatusDto>> GetStatusAsync(
        AirsideDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);

        var mfa = await db.UserMfa.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId, ct).ConfigureAwait(false);

        return TypedResults.Ok(new MfaStatusDto(
            mfa is not null,
            mfa?.ConfirmedAt is not null,
            mfa is null
                ? 0
                : mfa.RecoveryCodeHashes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length));
    }

    /// <summary>
    /// Generates a secret and recovery codes, and returns them once.
    /// </summary>
    /// <remarks>
    /// Enrolment is not complete here. The secret is stored unconfirmed until the
    /// user proves their authenticator holds it — marking it active at this point
    /// would lock people out with a QR code they never successfully scanned, and
    /// the account they would use to fix it is the one now demanding a code.
    /// </remarks>
    private static async Task<Results<Ok<MfaEnrolmentDto>, ProblemHttpResult>> EnrolAsync(
        AirsideDbContext db,
        ITotp totp,
        ISecretProtector protector,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = CurrentUserId(http);

        if (userId is null)
        {
            return new Error(ErrorCodes.ValidationFailed, "Not signed in.").ToProblem();
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);

        if (user is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such user.").ToProblem();
        }

        var existing = await db.UserMfa.FirstOrDefaultAsync(m => m.UserId == userId, ct).ConfigureAwait(false);

        if (existing?.ConfirmedAt is not null)
        {
            return new Error(
                "mfa.already_enrolled",
                "Two-factor authentication is already active on this account. Disable it before enrolling "
                + "a new authenticator.").ToProblem();
        }

        var secret = totp.GenerateSecret();
        var codes = GenerateRecoveryCodes();

        var record = existing ?? new UserMfa { Id = Guid.CreateVersion7(), UserId = userId.Value };

        record.EncryptedSecret = protector.Protect(new Secret(secret));
        record.RecoveryCodeHashes = string.Join('\n', codes.Select(HashRecoveryCode));
        record.ConfirmedAt = null;
        record.LastUsedTimeStep = 0;

        if (existing is null)
        {
            db.UserMfa.Add(record);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "user.mfa_enrolment_started",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "user",
            ResourceId = userId,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(new MfaEnrolmentDto(
            secret,
            totp.BuildProvisioningUri(secret, "Airside", user.Email ?? user.UserName ?? "airside"),
            codes));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ConfirmAsync(
        ConfirmMfaRequest request,
        AirsideDbContext db,
        ITotp totp,
        ISecretProtector protector,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = CurrentUserId(http);

        var record = await db.UserMfa.FirstOrDefaultAsync(m => m.UserId == userId, ct).ConfigureAwait(false);

        if (record is null)
        {
            return new Error("mfa.not_enrolled", "Start enrolment before confirming a code.").ToProblem();
        }

        var secret = protector.Unprotect(record.EncryptedSecret);

        if (secret.IsFailure)
        {
            return new Error(
                "mfa.secret_unreadable",
                "The stored secret could not be decrypted, which usually means the Data Protection key "
                + "ring was replaced. Enrol again.").ToProblem();
        }

        if (!totp.TryValidate(secret.Value.Reveal(), request.Code, record.LastUsedTimeStep, out var step))
        {
            await audit.WriteAsync(new AuditEntry
            {
                Action = "user.mfa_confirm_failed",
                Result = AuditResult.Denied,
                UserId = userId,
                ResourceKind = "user",
                ResourceId = userId,
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            }, ct).ConfigureAwait(false);

            return new Error(
                "mfa.invalid_code",
                "That code is not valid. Check the time on the device running your authenticator — a clock "
                + "more than a minute out produces codes the server cannot accept.").ToProblem();
        }

        record.ConfirmedAt = timeProvider.GetUtcNow().UtcDateTime;
        record.LastUsedTimeStep = step;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "user.mfa_enabled",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "user",
            ResourceId = userId,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Turns off the second factor, which requires a current code.
    /// </summary>
    /// <remarks>
    /// Requiring a code to disable is the point: without it, a stolen session
    /// cookie is enough to remove the factor that the cookie alone was not
    /// supposed to defeat.
    /// </remarks>
    private static async Task<Results<NoContent, ProblemHttpResult>> DisableAsync(
        ConfirmMfaRequest request,
        AirsideDbContext db,
        ITotp totp,
        ISecretProtector protector,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = CurrentUserId(http);

        var record = await db.UserMfa.FirstOrDefaultAsync(m => m.UserId == userId, ct).ConfigureAwait(false);

        if (record is null)
        {
            return TypedResults.NoContent();
        }

        var secret = protector.Unprotect(record.EncryptedSecret);

        var accepted = secret.IsSuccess
            && (totp.TryValidate(secret.Value.Reveal(), request.Code, record.LastUsedTimeStep, out _)
                || MatchesRecoveryCode(record, request.Code));

        if (!accepted)
        {
            return new Error(
                "mfa.invalid_code",
                "Enter a current code from your authenticator, or one of your recovery codes.").ToProblem();
        }

        db.UserMfa.Remove(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "user.mfa_disabled",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "user",
            ResourceId = userId,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Recovery codes, generated from a cryptographic source.
    /// </summary>
    /// <remarks>
    /// Grouped into two blocks with a hyphen, because these get written down and
    /// typed back by hand. The alphabet omits characters that are read wrong from
    /// paper.
    /// </remarks>
    private static List<string> GenerateRecoveryCodes()
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        var codes = new List<string>(RecoveryCodeCount);

        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var chars = new char[10];

            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            codes.Add($"{new string(chars[..5])}-{new string(chars[5..])}");
        }

        return codes;
    }

    private static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalise(code))));

    private static bool MatchesRecoveryCode(UserMfa record, string candidate)
    {
        var hash = HashRecoveryCode(candidate);
        var remaining = record.RecoveryCodeHashes.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        var index = remaining.FindIndex(h => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(h),
            System.Text.Encoding.ASCII.GetBytes(hash)));

        if (index < 0)
        {
            return false;
        }

        // Burned on use. A recovery code that still works after being used is a
        // password written on paper.
        remaining.RemoveAt(index);
        record.RecoveryCodeHashes = string.Join('\n', remaining);

        return true;
    }

    private static string Normalise(string code) =>
        code.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
