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

    /// <summary>
    /// The tables and columns an operator can query, or a failure explaining why
    /// there are none to list.
    /// </summary>
    /// <remarks>
    /// Here rather than in the dashboard because introspection is per-engine SQL,
    /// and the moment the frontend holds one <c>information_schema</c> query it
    /// holds four — which is the pile of engine checks the capability model
    /// exists to prevent. A console with no query to type into it is a text box
    /// pointed at a database whose contents are a guess.
    /// </remarks>
    Task<Result<DatabaseSchema>> DescribeAsync(QueryExecution execution, CancellationToken ct);
}

public sealed record DatabaseSchema(IReadOnlyList<SchemaTable> Tables);

/// <param name="Namespace">
/// Schema in Postgres, database in MySQL, absent elsewhere. Carried separately so
/// the dashboard can group by it without parsing a qualified name.
/// </param>
public sealed record SchemaTable(
    string? Namespace,
    string Name,
    IReadOnlyList<SchemaColumn> Columns);

public sealed record SchemaColumn(string Name, string DataType, bool Nullable, bool IsPrimaryKey);

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
