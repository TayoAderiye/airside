using Airside.Core.Common;
using Airside.Core.Databases;

namespace Airside.Core.Queries;

/// <summary>
/// Runs user-supplied queries against an attached database.
/// </summary>
/// <remarks>
/// An optional module behind <c>database.query</c>, which is independent of every
/// infrastructure permission. Restarting a database and reading its contents are
/// different rights.
/// </remarks>
public interface IQueryConsole
{
    QueryDialect Dialect { get; }

    Task<Result<QueryOutcome>> ExecuteAsync(QueryExecution execution, CancellationToken ct);
}

public sealed record QueryExecution(
    DatabaseEndpoint Endpoint,
    DatabaseCredentialValue Credential,
    string Statement,
    int MaxRows,
    TimeSpan Timeout,
    bool CallerHasDestructivePermission);

public sealed record QueryOutcome(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowsAffected,
    bool Truncated,
    TimeSpan Duration);

/// <summary>
/// Decides whether a command may run.
/// </summary>
/// <remarks>
/// <para>
/// The Redis implementation is the reason this exists. <c>KEYS *</c> on a large
/// production instance blocks the server for the duration of the scan — a real
/// outage cause, not a theoretical one — so it is blocked outright, and any key
/// browsing Airside offers uses <c>SCAN</c>. <c>FLUSHALL</c>, <c>FLUSHDB</c>,
/// <c>CONFIG SET</c>, and <c>SHUTDOWN</c> are gated behind
/// <c>database.query_destructive</c>.
/// </para>
/// <para>
/// The policy parses commands rather than pattern-matching strings. A denylist
/// checked with <c>StartsWith</c> is defeated by leading whitespace, casing, or
/// an inline comment, and a security control that can be bypassed by pressing
/// space first is not a control.
/// </para>
/// </remarks>
public interface ICommandPolicy
{
    QueryDialect Dialect { get; }

    /// <summary>
    /// Returns success, <c>query.command_blocked</c> for commands never permitted,
    /// or <c>query.command_requires_elevation</c> when the caller lacks
    /// <c>database.query_destructive</c>.
    /// </summary>
    Result Evaluate(string statement, bool callerHasDestructivePermission);
}
