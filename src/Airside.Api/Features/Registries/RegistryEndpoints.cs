using System.Security.Claims;
using Airside.Api.Infrastructure;
using Airside.Api.Security;
using Airside.Core.Audit;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Security;
using Airside.Data;
using Airside.Data.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Registries;

/// <param name="Registry">
/// Accepts <c>ghcr.io</c>, <c>https://ghcr.io/</c>, or <c>ghcr.io/myorg</c> — all
/// normalise to the host. Rejecting the forms people actually paste would produce
/// a credential that saves and never matches.
/// </param>
/// <param name="Password">
/// A password or, far more often, an access token. Stored encrypted and never
/// returned.
/// </param>
public sealed record SaveRegistryCredentialRequest(
    string Registry,
    string Username,
    string Password,
    string? DisplayName = null);

/// <param name="PasswordMasked">
/// Always the mask. The real value leaves the database only through the reveal
/// endpoint, which requires an elevated permission and writes an audit entry.
/// </param>
public sealed record RegistryCredentialDto(
    Guid Id,
    string Registry,
    string Username,
    string PasswordMasked,
    string DisplayName,
    DateTimeOffset? LastUsedAt,
    bool? LastVerificationSucceeded,
    DateTimeOffset? LastVerifiedAt,
    string? LastVerificationError,
    DateTimeOffset CreatedAt)
{
    public static RegistryCredentialDto From(RegistryCredential c)
    {
        ArgumentNullException.ThrowIfNull(c);

        return new RegistryCredentialDto(
            c.Id,
            c.Registry,
            c.Username,
            Secret.Mask,
            c.DisplayName,
            c.LastUsedAt is null ? null : new DateTimeOffset(c.LastUsedAt.Value, TimeSpan.Zero),
            c.LastVerificationSucceeded,
            c.LastVerifiedAt is null ? null : new DateTimeOffset(c.LastVerifiedAt.Value, TimeSpan.Zero),
            c.LastVerificationError,
            new DateTimeOffset(c.CreatedAt, TimeSpan.Zero));
    }
}

public sealed record VerifyRegistryRequest(string TestImage);

internal static class RegistryEndpoints
{
    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/registries").WithTags("Registries");

        group.MapGet("/", ListAsync).RequirePermission(Permissions.ApplicationRead);
        group.MapPut("/", SaveAsync).RequirePermission(Permissions.ServerManage);
        group.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(Permissions.ServerManage);

        group.MapPost("/{id:guid}/reveal", RevealAsync)
            .RequirePermission(Permissions.SecretRead)
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/{id:guid}/verify", VerifyAsync)
            .RequirePermission(Permissions.ServerManage)
            .RequireRateLimiting(RateLimitPolicies.Destructive);

        return app;
    }

    private static async Task<Ok<IReadOnlyList<RegistryCredentialDto>>> ListAsync(
        AirsideDbContext db,
        CancellationToken ct)
    {
        var rows = await db.RegistryCredentials
            .AsNoTracking()
            .OrderBy(c => c.Registry)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<RegistryCredentialDto>>(
            [.. rows.Select(RegistryCredentialDto.From)]);
    }

    /// <summary>
    /// Creates or replaces the credential for a registry.
    /// </summary>
    /// <remarks>
    /// An upsert rather than separate create and update, because there can only be
    /// one credential per registry — two would make which is used depend on query
    /// order, and a pull failing because it picked the stale one is not something
    /// anyone would think to look for.
    /// </remarks>
    private static async Task<Results<Ok<RegistryCredentialDto>, ProblemHttpResult>> SaveAsync(
        SaveRegistryCredentialRequest request,
        AirsideDbContext db,
        ISecretProtector protector,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "A username and password or token are required.").ToProblem();
        }

        var registry = RegistryHost.Normalise(request.Registry);

        if (registry.Length == 0 || registry.Contains(' ', StringComparison.Ordinal))
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "That is not a valid registry host. Use a name such as ghcr.io or registry.internal:5000.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "registry" }).ToProblem();
        }

        var userId = CurrentUserId(http);

        var existing = await db.RegistryCredentials
            .FirstOrDefaultAsync(c => c.Registry == registry, ct)
            .ConfigureAwait(false);

        var credential = existing ?? new RegistryCredential
        {
            Id = Guid.CreateVersion7(),
            Registry = registry,
            CreatedByUserId = userId,
        };

        credential.Username = request.Username.Trim();
        credential.EncryptedPassword = protector.Protect(new Secret(request.Password));
        credential.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? registry
            : request.DisplayName.Trim();

        // Replacing the secret invalidates whatever the last check said about it.
        credential.LastVerificationSucceeded = null;
        credential.LastVerifiedAt = null;
        credential.LastVerificationError = null;

        if (existing is null)
        {
            db.RegistryCredentials.Add(credential);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = existing is null ? "registry.credential_created" : "registry.credential_updated",
            Result = AuditResult.Success,
            UserId = userId,
            ResourceKind = "registry",
            ResourceId = credential.Id,
            ResourceSlugSnapshot = registry,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),

            // The username, never the secret. An audit log that recorded the token
            // would be a second copy of it in a table designed to be readable.
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["username"] = credential.Username,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(RegistryCredentialDto.From(credential));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        AirsideDbContext db,
        IAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var credential = await db.RegistryCredentials
            .FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);

        if (credential is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such credential.").ToProblem();
        }

        // Warned about rather than blocked. Airside cannot know whether an image
        // is private without trying to pull it, so refusing the delete would be a
        // guess — but the next deployment failing with an image-not-found is a
        // consequence worth naming here.
        var affected = await db.Applications
            .AsNoTracking()
            .Where(a => a.SourceImageRef != null && a.SourceImageRef.StartsWith(credential.Registry))
            .Select(a => a.Slug)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        credential.DeletedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "registry.credential_deleted",
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "registry",
            ResourceId = id,
            ResourceSlugSnapshot = credential.Registry,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["applicationsUsingThisRegistry"] = affected,
            },
        }, ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Returns the stored secret once, to an elevated caller, and records it.
    /// </summary>
    /// <remarks>
    /// The same path as revealing a database password: the value exists so it can
    /// be copied into a CI system or rotated by hand, and every read is written to
    /// the audit log so that "who has seen this token" has an answer.
    /// </remarks>
    private static async Task<Results<Ok<RevealedSecretDto>, ProblemHttpResult>> RevealAsync(
        Guid id,
        AirsideDbContext db,
        ISecretProtector protector,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        var credential = await db.RegistryCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);

        if (credential is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such credential.").ToProblem();
        }

        var password = protector.Unprotect(credential.EncryptedPassword);

        if (password.IsFailure)
        {
            return new Error(
                "registry.secret_unreadable",
                "The stored secret could not be decrypted, which usually means the Data Protection key "
                + "ring was replaced or restored from another instance. Re-enter the credential.").ToProblem();
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = "registry.credential_revealed",
            Result = AuditResult.Success,
            UserId = CurrentUserId(http),
            ResourceKind = "registry",
            ResourceId = id,
            ResourceSlugSnapshot = credential.Registry,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        }, ct).ConfigureAwait(false);

        return TypedResults.Ok(new RevealedSecretDto(credential.Username, password.Value.Reveal()));
    }

    /// <summary>
    /// Checks the credential by actually pulling something with it.
    /// </summary>
    /// <remarks>
    /// A real pull rather than a login call, because a token can authenticate and
    /// still lack pull scope on the repository that matters — which surfaces as a
    /// deployment failure long after the credential was saved and pronounced fine.
    /// </remarks>
    private static async Task<Results<Ok<RegistryCredentialDto>, ProblemHttpResult>> VerifyAsync(
        Guid id,
        VerifyRegistryRequest request,
        AirsideDbContext db,
        RegistryCredentialStore store,
        IContainerRuntime runtime,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credential = await db.RegistryCredentials
            .FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);

        if (credential is null)
        {
            return new Error(ErrorCodes.WorkloadNotFound, "No such credential.").ToProblem();
        }

        if (string.IsNullOrWhiteSpace(request.TestImage))
        {
            return new Error(
                ErrorCodes.ValidationFailed,
                "Name an image on this registry to test with — a token can authenticate and still lack "
                + "pull scope on the repository you care about.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = "testImage" }).ToProblem();
        }

        ImageReference image;

        try
        {
            image = ImageReference.Parse(request.TestImage.Trim());
        }
        catch (FormatException ex)
        {
            return new Error(ErrorCodes.ValidationFailed, ex.Message).ToProblem();
        }

        if (!string.Equals(RegistryHost.Of(image), credential.Registry, StringComparison.Ordinal))
        {
            return new Error(
                "registry.image_elsewhere",
                $"'{request.TestImage}' is on {RegistryHost.Of(image)}, not {credential.Registry}. Testing "
                + "with it would prove nothing about this credential.").ToProblem();
        }

        var auth = await store.PeekAsync(id, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            await runtime.Images.PullAsync(image, null, auth, ct).ConfigureAwait(false);

            credential.LastVerificationSucceeded = true;
            credential.LastVerificationError = null;
        }
        catch (ContainerRuntimeException ex)
        {
            credential.LastVerificationSucceeded = false;

            // Truncated: registry errors can be long, and the useful part is at
            // the front. The whole thing is in the logs.
            credential.LastVerificationError = ex.Message.Length > 400
                ? ex.Message[..400]
                : ex.Message;
        }

        credential.LastVerifiedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(RegistryCredentialDto.From(credential));
    }

    private static Guid? CurrentUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public sealed record RevealedSecretDto(string Username, string Password);
