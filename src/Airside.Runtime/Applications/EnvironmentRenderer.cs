using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;

namespace Airside.Runtime.Applications;

/// <summary>A database an application is attached to, resolved at render time.</summary>
public sealed record AttachedDatabase(
    Guid AttachmentId,
    DatabaseEngineKind Engine,
    string EnvKeyPrefix,
    DatabaseEndpoint Endpoint,
    DatabaseCredentialValue Credential);

/// <summary>A manually-entered variable, already decrypted.</summary>
public sealed record ManualVariable(string Key, Secret Value, bool IsSecret);

public sealed record RenderedEnvironment(
    IReadOnlyList<EnvironmentEntry> Entries,
    IReadOnlyList<string> InjectedKeys);

/// <summary>
/// Builds the environment a container is started with.
/// </summary>
/// <remarks>
/// <para>
/// Injected connection details are computed here, at deploy time, and are never
/// stored. That is the whole point: if they were rows, rotating a database
/// credential would leave the running container holding a password that no longer
/// works while the UI cheerfully showed the new one. Rendering them means the
/// next deploy always carries the live credential, and nothing can drift.
/// </para>
/// <para>
/// Manual variables are applied last and deliberately do not override injected
/// keys — see <see cref="Render"/>.
/// </para>
/// </remarks>
public sealed class EnvironmentRenderer(IDatabaseEngineRegistry engines)
{
    public RenderedEnvironment Render(
        IReadOnlyList<ManualVariable> manual,
        IReadOnlyList<AttachedDatabase> attachments)
    {
        ArgumentNullException.ThrowIfNull(manual);
        ArgumentNullException.ThrowIfNull(attachments);

        var entries = new List<EnvironmentEntry>();
        var injected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var attachment in attachments)
        {
            var engine = engines.Get(attachment.Engine);
            var details = engine.BuildConnectionDetails(attachment.Endpoint, attachment.Credential);

            foreach (var entry in engine.BuildInjectedEnvironment(attachment.EnvKeyPrefix, details))
            {
                entries.Add(entry);
                injected.Add(entry.Key);
            }
        }

        foreach (var variable in manual)
        {
            // An injected key wins. A manual entry shadowing DATABASE_URL would
            // point the application at something other than the database it is
            // attached to, while the attachment screen still claimed otherwise —
            // and the collision is already rejected when the variable is created,
            // so reaching here means the attachment came second.
            if (injected.Contains(variable.Key))
            {
                continue;
            }

            entries.Add(new EnvironmentEntry(variable.Key, variable.Value, variable.IsSecret));
        }

        return new RenderedEnvironment(entries, [.. injected]);
    }

    /// <summary>
    /// The keys an attachment would inject, without needing a live credential.
    /// </summary>
    /// <remarks>
    /// Used to detect a prefix collision before an attachment is created, and to
    /// show the operator what an attachment will add.
    /// </remarks>
    public IReadOnlyList<string> InjectedKeysFor(DatabaseEngineKind engineKind, string prefix)
    {
        var engine = engines.Get(engineKind);
        var caps = engine.Capabilities;

        var placeholder = new ConnectionDetails(
            "host",
            caps.DefaultPort,
            caps.SupportsDatabaseName ? "name" : null,
            caps.SupportsUserAccounts ? "user" : null,
            new Secret("password"),
            new Secret("url"));

        return [.. engine.BuildInjectedEnvironment(prefix, placeholder).Select(e => e.Key)];
    }
}
