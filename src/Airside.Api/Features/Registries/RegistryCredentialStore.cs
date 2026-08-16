using Airside.Core.Containers;
using Airside.Core.Security;
using Airside.Data;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Features.Registries;

/// <inheritdoc />
public sealed class RegistryCredentialStore(
    AirsideDbContext db,
    ISecretProtector protector,
    TimeProvider timeProvider,
    ILogger<RegistryCredentialStore> logger) : IRegistryCredentialSource
{
    public async Task<RegistryAuth?> ResolveAsync(ImageReference image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);

        var registry = RegistryHost.Of(image);

        var credential = await db.RegistryCredentials
            .FirstOrDefaultAsync(c => c.Registry == registry, ct)
            .ConfigureAwait(false);

        if (credential is null)
        {
            // The overwhelmingly common case: a public image. Returning null rather
            // than failing keeps every existing pull working untouched.
            return null;
        }

        var password = protector.Unprotect(credential.EncryptedPassword);

        if (password.IsFailure)
        {
            // A key ring restored from a different instance. Reported here rather
            // than passed on as an empty password, which the registry would answer
            // with a 401 that reads as "wrong username".
            logger.LogError(
                "The stored credential for {Registry} could not be decrypted. This usually means the Data "
                + "Protection key ring was replaced or restored from another instance. Re-enter it.",
                registry);

            return null;
        }

        credential.LastUsedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new RegistryAuth(registry, credential.Username, password.Value);
    }

    /// <summary>Reads a credential back for a verification attempt, without touching last-used.</summary>
    public async Task<RegistryAuth?> PeekAsync(Guid id, CancellationToken ct)
    {
        var credential = await db.RegistryCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct)
            .ConfigureAwait(false);

        if (credential is null)
        {
            return null;
        }

        var password = protector.Unprotect(credential.EncryptedPassword);

        return password.IsFailure
            ? null
            : new RegistryAuth(credential.Registry, credential.Username, password.Value);
    }
}
