using Airside.Api.Contracts;
using Airside.Api.Infrastructure;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Runtime.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features;

/// <summary>
/// First-run setup.
/// </summary>
/// <remarks>
/// There is no default account at any point, not even briefly. The installer
/// prints a one-time token, only its hash is stored, and the first Super Admin is
/// created here or not at all — so a box that sits unreachable for ten minutes
/// after install has no credential to guess.
/// </remarks>
internal static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/setup").WithTags("Setup").AllowAnonymous();

        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/complete", CompleteAsync).RequireRateLimiting(RateLimitPolicies.Authentication);

        return app;
    }

    private static async Task<Ok<SetupStatusDto>> GetStatusAsync(
        AirsideDbContext db,
        CancellationToken ct)
    {
        var settings = await db.InstanceSettings.AsNoTracking().FirstAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new SetupStatusDto(
            SetupCompleted: settings.SetupCompletedAt is not null,
            StoreProvider: settings.StoreProvider.ToString().ToLowerInvariant(),
            Version: BuildInfo.Version,
            AwaitingDomain: settings.AwaitingDomain));
    }

    private static async Task<Results<Ok<CurrentUserDto>, ProblemHttpResult>> CompleteAsync(
        SetupCompleteRequest request,
        AirsideDbContext db,
        UserManager<AirsideUser> users,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var settings = await db.InstanceSettings.FirstAsync(ct).ConfigureAwait(false);

        var invalidToken = new Error(
            ErrorCodes.AuthSetupTokenInvalid,
            "The setup token is missing, expired, or incorrect.");

        if (settings.SetupCompletedAt is not null || settings.SetupTokenHash is null)
        {
            // Same error whether setup is already done or the token is wrong. A
            // distinct "already set up" response would tell an unauthenticated
            // caller that the instance is live and worth attacking.
            return invalidToken.ToProblem();
        }

        if (settings.SetupTokenExpiresAt is not null && settings.SetupTokenExpiresAt < timeProvider.GetUtcNow().UtcDateTime)
        {
            return invalidToken.ToProblem();
        }

        if (!SecretGenerator.TokenMatches(new Secret(request.SetupToken), settings.SetupTokenHash))
        {
            await audit.WriteAsync(new AuditEntry
            {
                Action = AuditActions.UserSignInFailed,
                Result = AuditResult.Denied,
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reason"] = "setup_token_mismatch",
                },
            }, ct).ConfigureAwait(false);

            return invalidToken.ToProblem();
        }

        var user = new AirsideUser
        {
            Id = Guid.CreateVersion7(),
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName,
            IsActive = true,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };

        var created = await users.CreateAsync(user, request.Password).ConfigureAwait(false);

        if (!created.Succeeded)
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "The password does not meet the minimum requirements.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["errors"] = created.Errors.Select(e => e.Description).ToList(),
                }).ToProblem();
        }

        var superAdmin = await db.Roles.FirstAsync(r => r.Slug == SystemRoles.SuperAdmin, ct).ConfigureAwait(false);
        db.UserRoles.Add(new UserRole
        {
            UserId = user.Id,
            RoleId = superAdmin.Id,
            GrantedAt = timeProvider.GetUtcNow().UtcDateTime,
        });

        settings.InstanceName = request.InstanceName;
        settings.SetupCompletedAt = timeProvider.GetUtcNow().UtcDateTime;

        // Burned immediately. A token that stays valid after use is a second
        // credential nobody is tracking.
        settings.SetupTokenHash = null;
        settings.SetupTokenExpiresAt = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.UserCreated,
            Result = AuditResult.Success,
            UserId = user.Id,
            UserEmailSnapshot = user.Email,
            ResourceKind = "user",
            ResourceId = user.Id,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = SystemRoles.SuperAdmin,
                ["viaSetupToken"] = true,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(new CurrentUserDto(
            user.Id,
            user.Email!,
            user.DisplayName,
            [SystemRoles.SuperAdmin],
            Permissions.All,
            MfaEnabled: false,
            MustChangePassword: false));
    }
}

public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}

public static class RateLimitPolicies
{
    public const string Authentication = "auth";
    public const string Destructive = "destructive";
}
