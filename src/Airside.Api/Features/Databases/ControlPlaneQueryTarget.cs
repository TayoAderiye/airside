using Airside.Api.Infrastructure;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Naming;
using Airside.Core.Queries;
using Airside.Core.Security;
using Airside.Data.Entities;
using Npgsql;

namespace Airside.Api.Features.Databases;

/// <summary>
/// Makes Airside's own store queryable from the query console.
/// </summary>
/// <remarks>
/// <para>
/// This was refused at first, on the grounds that the store holds every
/// credential, session and audit row on the host. That reasoning does not
/// survive contact with the product: an Airside login is a root login, the
/// README says so, and the documented recovery path for a locked-out
/// administrator is <c>docker exec airside-db psql</c> — the same access,
/// through a worse interface. Refusing in the dashboard protected nothing and
/// cost the operator the one tool that would have answered their question.
/// </para>
/// <para>
/// What it does keep: writes still require <c>database.query_destructive</c>,
/// exactly as they do for any other database, so the guard is the product's
/// existing one rather than a rule invented for this case. Reads are audited
/// under their own action so "who looked at the control-plane store" is a
/// question the audit log can answer.
/// </para>
/// <para>
/// Credentials come from the connection string the API already holds, because
/// there is nowhere else for them to come from: the store is created by compose
/// and has no row in <c>DatabaseCredentials</c>.
/// </para>
/// </remarks>
internal sealed class ControlPlaneQueryTarget(
    AirsideStoreOptions store,
    IContainerRuntime runtime)
{
    /// <summary>
    /// The query target for the control-plane store, or a failure explaining why
    /// there is not one.
    /// </summary>
    public async Task<Result<(DatabaseEndpoint Endpoint, DatabaseCredentialValue Credential)>> ResolveAsync(
        CancellationToken ct)
    {
        if (store.Provider != StoreProvider.Postgres)
        {
            // The SQLite store is a file inside the API container, with no
            // container of its own to exec into and no server to speak to.
            return Result.Fail<(DatabaseEndpoint, DatabaseCredentialValue)>(new Error(
                "query.control_plane_not_queryable",
                "This instance stores its state in SQLite, which has no database container to query. "
                + "The file is at " + AirsideLabels.HostPaths.Data + "/airside.db on the host."));
        }

        var container = await runtime.Containers
            .FindAsync(AirsideLabels.SystemContainers.Database, ct)
            .ConfigureAwait(false);

        if (container is null)
        {
            return Result.Fail<(DatabaseEndpoint, DatabaseCredentialValue)>(new Error(
                ErrorCodes.WorkloadNotFound,
                "The control-plane store container is not running."));
        }

        NpgsqlConnectionStringBuilder parsed;

        try
        {
            parsed = new NpgsqlConnectionStringBuilder(store.ConnectionString);
        }
        catch (ArgumentException ex)
        {
            // Never surfaces the connection string itself, which carries the
            // password for the store holding every other secret.
            return Result.Fail<(DatabaseEndpoint, DatabaseCredentialValue)>(new Error(
                "query.control_plane_not_queryable",
                $"The configured store connection string could not be parsed: {ex.Message}"));
        }

        if (string.IsNullOrEmpty(parsed.Username) || string.IsNullOrEmpty(parsed.Password))
        {
            return Result.Fail<(DatabaseEndpoint, DatabaseCredentialValue)>(new Error(
                "query.control_plane_not_queryable",
                "The configured store connection string carries no username and password, so the console "
                + "has no credentials to connect with."));
        }

        return Result.Ok((
            new DatabaseEndpoint(
                container.Id,
                AirsideLabels.SystemContainers.Database,
                0,
                parsed.Database ?? "airside"),
            new DatabaseCredentialValue(parsed.Username, new Secret(parsed.Password))));
    }

    public static DatabaseEngineKind Engine => DatabaseEngineKind.Postgres;
}
