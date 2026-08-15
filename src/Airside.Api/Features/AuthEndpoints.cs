using System.Security.Claims;
using Airside.Api.Contracts;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
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
