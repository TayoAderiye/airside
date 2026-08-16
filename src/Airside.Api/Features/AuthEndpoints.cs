using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Features.Operations;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Operations;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapGet("/me", GetMeAsync).RequireAuthorization();

        return app;
    }

    private static async Task<Results<Ok<CurrentUserDto>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        AirsideDbContext db,
        UserManager<AirsideUser> users,
        ClaimsFactory claimsFactory,
        IAuditWriter audit,
        ITotp totp,
        ISecretProtector protector,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        // One error for every failure mode below. Distinguishing "no such user"
        // from "wrong password" turns the login form into an account enumerator.
        var invalid = new Error(ErrorCodes.AuthInvalidCredentials, "Email or password is incorrect.");

        var user = await users.FindByEmailAsync(request.Email).ConfigureAwait(false);

        if (user is null)
        {
            await WriteFailureAsync(audit, null, request.Email, http, "unknown_user", ct).ConfigureAwait(false);
            return invalid.ToProblem();
        }

        if (await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            await WriteFailureAsync(audit, user.Id, user.Email, http, "locked_out", ct).ConfigureAwait(false);
            return new Error(
                ErrorCodes.AuthAccountLocked,
                "This account is temporarily locked after too many failed attempts.").ToProblem();
        }

        if (!await users.CheckPasswordAsync(user, request.Password).ConfigureAwait(false))
        {
            // Feeds Identity's lockout counter, which is the reason to use its
            // user store rather than hand-rolling a password check.
            await users.AccessFailedAsync(user).ConfigureAwait(false);
            await WriteFailureAsync(audit, user.Id, user.Email, http, "bad_password", ct).ConfigureAwait(false);
            return invalid.ToProblem();
        }

        if (!user.IsActive)
        {
            await WriteFailureAsync(audit, user.Id, user.Email, http, "deactivated", ct).ConfigureAwait(false);
            return invalid.ToProblem();
        }

        var mfa = await db.UserMfa.FirstOrDefaultAsync(m => m.UserId == user.Id, ct).ConfigureAwait(false);

        // Only a confirmed enrolment gates login. An unconfirmed one is a secret
        // the user may never have successfully scanned, and enforcing it would
        // lock them out of the account they would have to use to fix it.
        if (mfa?.ConfirmedAt is not null)
        {
            var challenge = await CheckSecondFactorAsync(
                mfa, request.TotpCode, totp, protector, users, user, audit, http, ct).ConfigureAwait(false);

            if (challenge is not null)
            {
                return challenge;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var session = new UserSession
        {
            UserId = user.Id,
            ExpiresAt = now.AddDays(7),
            LastSeenAt = now,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http.Request.Headers.UserAgent.ToString(),
        };

        db.UserSessions.Add(session);
        user.LastLoginAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var claims = await claimsFactory.BuildAsync(user.Id, session.Id, ct).ConfigureAwait(false);
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = session.ExpiresAt })
            .ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.UserSignIn,
            Result = AuditResult.Success,
            UserId = user.Id,
            UserEmailSnapshot = user.Email,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http.Request.Headers.UserAgent.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(await BuildCurrentUserAsync(db, user, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Validates the second factor, returning a problem to answer with or
    /// <c>null</c> when the code is accepted.
    /// </summary>
    /// <remarks>
    /// A successful check mutates <paramref name="mfa"/> — advancing the used
    /// time step, or burning a redeemed recovery code — and the caller saves it.
    /// Both mutations are the entire protection against replay, so neither is
    /// optional.
    /// </remarks>
    private static async Task<ProblemHttpResult?> CheckSecondFactorAsync(
        UserMfa mfa,
        string? submitted,
        ITotp totp,
        ISecretProtector protector,
        UserManager<AirsideUser> users,
        AirsideUser user,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        switch (MfaChallenge.Evaluate(mfa, submitted, totp, protector))
        {
            case MfaOutcome.Accepted:
                return null;

            case MfaOutcome.AcceptedWithRecoveryCode:
                await audit.WriteAsync(new AuditEntry
                {
                    Action = "user.mfa_recovery_code_used",
                    Result = AuditResult.Success,
                    UserId = user.Id,
                    UserEmailSnapshot = user.Email,
                    ResourceKind = "user",
                    ResourceId = user.Id,
                    IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                    Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["remaining"] = RecoveryCodes.Remaining(mfa),
                    },
                }, ct).ConfigureAwait(false);

                return null;

            case MfaOutcome.Missing:
                return new Error(
                    ErrorCodes.AuthMfaRequired,
                    "This account requires a code from your authenticator.").ToProblem();

            case MfaOutcome.SecretUnreadable:
                // The key ring was replaced, so the stored secret can never
                // validate again. Refusing is the only safe answer — accepting
                // the password alone would silently drop the second factor for
                // whoever turned up after the failure.
                await WriteFailureAsync(audit, user.Id, user.Email, http, "mfa_secret_unreadable", ct)
                    .ConfigureAwait(false);

                return new Error(
                    ErrorCodes.AuthMfaInvalid,
                    "The stored second factor for this account cannot be read, which usually means the Data "
                    + "Protection key ring was replaced. Recovery requires host access.").ToProblem();

            default:
                // Counts towards lockout. Without this a six-digit code is a
                // million guesses against an endpoint that has already accepted
                // the password.
                await users.AccessFailedAsync(user).ConfigureAwait(false);
                await WriteFailureAsync(audit, user.Id, user.Email, http, "mfa_invalid_code", ct)
                    .ConfigureAwait(false);

                return new Error(
                    ErrorCodes.AuthMfaInvalid,
                    "That code is not valid. Check the clock on the device running your authenticator — "
                    + "more than a minute out produces codes this server cannot accept.").ToProblem();
        }
    }

    private static async Task<NoContent> LogoutAsync(
        AirsideDbContext db,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var sessionId = http.User.FindFirstValue(AirsideClaims.SessionId);

        if (Guid.TryParse(sessionId, out var id))
        {
            var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);

            if (session is not null)
            {
                session.RevokedAt = timeProvider.GetUtcNow().UtcDateTime;
                session.RevokedReason = "signed out";
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUserDto>, UnauthorizedHttpResult>> GetMeAsync(
        AirsideDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userId, out var id))
        {
            return TypedResults.Unauthorized();
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);

        return user is null
            ? TypedResults.Unauthorized()
            : TypedResults.Ok(await BuildCurrentUserAsync(db, user, ct).ConfigureAwait(false));
    }

    private static async Task<CurrentUserDto> BuildCurrentUserAsync(
        AirsideDbContext db,
        AirsideUser user,
        CancellationToken ct)
    {
        var roles = await db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.Role.Slug)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var permissions = await db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == user.Id)
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.PermissionCode)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new CurrentUserDto(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            roles,
            permissions,
            user.TwoFactorEnabled,
            user.MustChangePassword);
    }

    private static Task WriteFailureAsync(
        IAuditWriter audit,
        Guid? userId,
        string? email,
        HttpContext http,
        string reason,
        CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.UserSignInFailed,
            Result = AuditResult.Denied,
            UserId = userId,
            UserEmailSnapshot = email,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http.Request.Headers.UserAgent.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = reason },
        }, ct);
}
