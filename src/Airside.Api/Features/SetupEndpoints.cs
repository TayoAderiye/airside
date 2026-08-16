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
        ArgumentNullException.ThrowIfNull(request);

        // Checked before anything touches the database. This endpoint is
        // unauthenticated by necessity, so a body with a field missing has to come
        // back as a validation error — reaching the Secret constructor with a null
        // turns a typo into a 500 that tells the caller nothing and puts an
        // unhandled exception on the one path every install goes through.
        foreach (var (field, value) in new[]
        {
            ("setupToken", request.SetupToken),
            ("email", request.Email),
            ("password", request.Password),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new Error(
                    ErrorCodes.ValidationFailed,
                    $"'{field}' is required.",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = field }).ToProblem();
            }
        }

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
    /// <summary>
    /// The informational version, without build metadata.
    /// </summary>
    /// <remarks>
    /// Read from <c>AssemblyInformationalVersion</c> rather than
    /// <c>AssemblyName.Version</c>, which is always four-part and would report
    /// <c>0.1.0.0</c> for a <c>0.1.0</c> release. Anything after a <c>+</c> is
    /// SourceLink's commit hash and is trimmed: this value is compared against
    /// image tags, and a tag never carries it.
    /// </remarks>
    public static string Version { get; } = ReadVersion();

    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    private static string ReadVersion()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

public static class RateLimitPolicies
{
    public const string Authentication = "auth";
    public const string Destructive = "destructive";
}
