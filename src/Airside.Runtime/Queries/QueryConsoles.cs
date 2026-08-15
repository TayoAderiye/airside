using System.Diagnostics;
using System.Globalization;
using System.Text;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Core.Queries;

namespace Airside.Runtime.Queries;

/// <summary>
/// Runs queries by executing the engine's own client inside the container.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not four client libraries. Npgsql, MySqlConnector, the MongoDB
/// driver, and StackExchange.Redis would add a large dependency surface to a
/// control plane whose job is not to be a database client, and each image already
/// ships a client that speaks its own protocol correctly.
/// </para>
/// <para>
/// The cost is real and worth naming: results arrive as text, so every column is
/// a string and type fidelity is lost. For a console whose output is rendered in
/// a browser table that is an acceptable trade; it would not be for an ORM.
/// </para>
/// <para>
/// The statement is passed as a single argument-vector element. It is arbitrary
/// by design — that is what a query console is — and there is no shell, so the
/// statement cannot escape into a command.
/// </para>
/// </remarks>
internal abstract class ExecQueryConsole(IContainerRuntime runtime, ICommandPolicy policy) : IQueryConsole
{
    public abstract QueryDialect Dialect { get; }

    protected IContainerRuntime Runtime { get; } = runtime;

    protected abstract IReadOnlyList<string> BuildArgv(QueryExecution execution);

    protected abstract IReadOnlyList<EnvironmentEntry> BuildEnvironment(QueryExecution execution);

    protected abstract QueryOutcome Parse(string output, int maxRows, TimeSpan duration);

    public async Task<Result<QueryOutcome>> ExecuteAsync(QueryExecution execution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var gate = policy.Evaluate(execution.Statement, execution.CallerHasDestructivePermission);

        if (gate.IsFailure)
        {
            return gate.Failure!;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(execution.Timeout);

        var stopwatch = Stopwatch.StartNew();
        using var output = new MemoryStream();

        ExecResult result;

        try
        {
            result = await Runtime.Containers.ExecAsync(
                new ExecRequest(execution.Endpoint.ContainerId, BuildArgv(execution), BuildEnvironment(execution)),
                output,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Error(
                ErrorCodes.QueryTimeout,
                $"The query did not finish within {execution.Timeout.TotalSeconds:0} seconds and was abandoned.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["timeoutSeconds"] = (int)execution.Timeout.TotalSeconds,
                });
        }

        stopwatch.Stop();

        if (result.ExitCode != 0)
        {
            // The client's own message, which is the useful one — "relation does
            // not exist" beats "the query failed". It is engine output, not a
            // platform internal, so it is safe to pass through.
            return new Error(ErrorCodes.ValidationFailed, Truncate(result.StandardError));
        }

        return Parse(Encoding.UTF8.GetString(output.ToArray()), execution.MaxRows, stopwatch.Elapsed);
    }

    protected static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000] + "…";

    /// <summary>
    /// Splits a delimited line, honouring double-quoted fields.
    /// </summary>
    /// <remarks>
    /// A naive split on the delimiter corrupts every row containing one inside a
    /// value, which for a console showing user data is most of them.
    /// </remarks>
    protected static List<string> SplitDelimited(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    protected static QueryOutcome ParseDelimited(string output, char delimiter, int maxRows, TimeSpan duration)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return new QueryOutcome([], [], 0, false, duration);
        }

        var columns = SplitDelimited(lines[0], delimiter);
        var dataLines = lines.Skip(1).ToList();
        var truncated = dataLines.Count > maxRows;

        var rows = dataLines
            .Take(maxRows)
            .Select(l => (IReadOnlyList<object?>)SplitDelimited(l, delimiter).Cast<object?>().ToList())
            .ToList();

        return new QueryOutcome(columns, rows, rows.Count, truncated, duration);
    }
}

internal sealed class PostgresQueryConsole(IContainerRuntime runtime, ICommandPolicy policy, string username)
    : ExecQueryConsole(runtime, policy)
{
    public override QueryDialect Dialect => QueryDialect.Sql;

    protected override IReadOnlyList<string> BuildArgv(QueryExecution execution) =>
    [
        "psql", "--username", username, "--dbname", execution.Endpoint.DatabaseName!,
        "--csv", "--no-psqlrc", "--set", "ON_ERROR_STOP=1",
        "--command", execution.Statement,
    ];

    protected override IReadOnlyList<EnvironmentEntry> BuildEnvironment(QueryExecution execution) =>
        [new EnvironmentEntry("PGPASSWORD", execution.Credential.Password, true)];

    protected override QueryOutcome Parse(string output, int maxRows, TimeSpan duration) =>
        ParseDelimited(output, ',', maxRows, duration);
}

internal sealed class MySqlQueryConsole(IContainerRuntime runtime, ICommandPolicy policy, string username)
    : ExecQueryConsole(runtime, policy)
{
    public override QueryDialect Dialect => QueryDialect.Sql;

    protected override IReadOnlyList<string> BuildArgv(QueryExecution execution) =>
    [
        "mysql", "--user", username, "--host", "127.0.0.1",
        "--database", execution.Endpoint.DatabaseName!,
        "--batch", "--raw", "--execute", execution.Statement,
    ];

    protected override IReadOnlyList<EnvironmentEntry> BuildEnvironment(QueryExecution execution) =>
        [new EnvironmentEntry("MYSQL_PWD", execution.Credential.Password, true)];

    protected override QueryOutcome Parse(string output, int maxRows, TimeSpan duration) =>
        ParseDelimited(output, '\t', maxRows, duration);
}

internal sealed class MongoQueryConsole(IContainerRuntime runtime, ICommandPolicy policy, string username)
    : ExecQueryConsole(runtime, policy)
{
    public override QueryDialect Dialect => QueryDialect.MongoShell;

    protected override IReadOnlyList<string> BuildArgv(QueryExecution execution) =>
    [
        "mongosh", "--quiet", "--json", "relaxed",
        "--username", username,
        "--password", execution.Credential.Password.Reveal(),
        "--authenticationDatabase", "admin",
        execution.Endpoint.DatabaseName!,
        "--eval", execution.Statement,
    ];

    protected override IReadOnlyList<EnvironmentEntry> BuildEnvironment(QueryExecution execution) => [];

    /// <summary>
    /// Mongo results are documents, not rows, so they are returned as a single
    /// JSON column rather than forced into a grid whose columns would change per
    /// document.
    /// </summary>
    protected override QueryOutcome Parse(string output, int maxRows, TimeSpan duration)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        var truncated = lines.Count > maxRows;
        var rows = lines.Take(maxRows)
            .Select(l => (IReadOnlyList<object?>)new List<object?> { l })
            .ToList();

        return new QueryOutcome(["result"], rows, rows.Count, truncated, duration);
    }
}

internal sealed class RedisQueryConsole(IContainerRuntime runtime, ICommandPolicy policy)
    : ExecQueryConsole(runtime, policy)
{
    public override QueryDialect Dialect => QueryDialect.RedisCommand;

    /// <summary>
    /// The tokens the policy inspected become the argument vector verbatim.
    /// </summary>
    /// <remarks>
    /// Tokenising once and reusing the result is what stops the check and the
    /// execution disagreeing — a second, differently-implemented split is exactly
    /// how a blocked command ends up running.
    /// </remarks>
    protected override IReadOnlyList<string> BuildArgv(QueryExecution execution) =>
        ["redis-cli", .. RedisCommandTokenizer.Tokenize(execution.Statement)];

    protected override IReadOnlyList<EnvironmentEntry> BuildEnvironment(QueryExecution execution) =>
        [new EnvironmentEntry("REDISCLI_AUTH", execution.Credential.Password, true)];

    protected override QueryOutcome Parse(string output, int maxRows, TimeSpan duration)
    {
        var lines = output.Split('\n', StringSplitOptions.None)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        var truncated = lines.Count > maxRows;
        var rows = lines.Take(maxRows)
            .Select(l => (IReadOnlyList<object?>)new List<object?> { l })
            .ToList();

        return new QueryOutcome(["reply"], rows, rows.Count, truncated, duration);
    }
}

/// <summary>Builds the right console for an engine, with its policy attached.</summary>
public interface IQueryConsoleFactory
{
    IQueryConsole Create(DatabaseEngineKind engine, string? username);
}

internal sealed class QueryConsoleFactory(IContainerRuntime runtime) : IQueryConsoleFactory
{
    private readonly RedisCommandPolicy _redisPolicy = new();
    private readonly PermissiveCommandPolicy _sqlPolicy = new(QueryDialect.Sql);
    private readonly PermissiveCommandPolicy _mongoPolicy = new(QueryDialect.MongoShell);

    public IQueryConsole Create(DatabaseEngineKind engine, string? username) => engine switch
    {
        DatabaseEngineKind.Postgres => new PostgresQueryConsole(runtime, _sqlPolicy, username!),
        DatabaseEngineKind.MySql => new MySqlQueryConsole(runtime, _sqlPolicy, username!),
        DatabaseEngineKind.MongoDb => new MongoQueryConsole(runtime, _mongoPolicy, username!),
        DatabaseEngineKind.Redis => new RedisQueryConsole(runtime, _redisPolicy),
        _ => throw new NotSupportedException($"No query console for {engine}."),
    };
}
