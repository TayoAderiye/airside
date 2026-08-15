using System.Text;
using Airside.Core.Common;
using Airside.Core.Databases;
using Airside.Core.Queries;

namespace Airside.Runtime.Queries;

/// <summary>
/// Splits a Redis command line into an argument vector the way redis-cli does.
/// </summary>
/// <remarks>
/// <para>
/// The policy below parses rather than pattern-matches, and this is why. A
/// denylist checked with <c>StartsWith</c> is defeated by a leading space, by
/// casing, and — least obviously — by quoting the command name: redis-cli accepts
/// <c>"FLUSHALL"</c> with the quotes and executes it. A control that can be
/// bypassed by pressing space first is not a control.
/// </para>
/// <para>
/// The same tokens become the exec argument vector, so what the policy inspected
/// and what actually runs cannot diverge.
/// </para>
/// </remarks>
public static class RedisCommandTokenizer
{
    public static IReadOnlyList<string> Tokenize(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (quote is not null)
            {
                if (c == '\\' && i + 1 < commandLine.Length && quote == '"')
                {
                    current.Append(commandLine[++i]);
                    continue;
                }

                if (c == quote)
                {
                    quote = null;
                    continue;
                }

                current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                // A quote opens a token even mid-word, which is what makes
                // FLUSH"ALL" and "FLUSHALL" both resolve to FLUSHALL.
                quote = c;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            current.Append(c);
            inToken = true;
        }

        if (quote is not null)
        {
            throw new FormatException("The command has an unterminated quote.");
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

/// <summary>
/// Decides whether a Redis command may run.
/// </summary>
/// <remarks>
/// Three tiers, because "destructive" is not one thing. Some commands lose data
/// and a senior operator should be able to run them deliberately; others hand an
/// attacker the host and no permission Airside offers should reach them through a
/// web console.
/// </remarks>
public sealed class RedisCommandPolicy : ICommandPolicy
{
    /// <summary>
    /// Never permitted, at any permission level.
    /// </summary>
    /// <remarks>
    /// These are not destructive, they are compromise primitives.
    /// <list type="bullet">
    /// <item><c>MODULE</c> loads a shared object into the Redis process: arbitrary
    /// native code execution inside the container.</item>
    /// <item><c>MIGRATE</c> transfers keys to an arbitrary host and port — a data
    /// exfiltration primitive that needs no other vulnerability.</item>
    /// <item><c>REPLICAOF</c> / <c>SLAVEOF</c> makes the instance a replica of a
    /// server the caller chooses, which replaces its entire dataset and is the
    /// basis of a well-known Redis takeover chain.</item>
    /// <item><c>DEBUG</c> includes <c>SEGFAULT</c> and can corrupt the instance.</item>
    /// <item><c>MONITOR</c> streams every command from every client, including
    /// the credentials other applications send on connect.</item>
    /// <item><c>ACL</c> lets the caller grant themselves permissions Airside did
    /// not give them, escaping the platform's own authorisation model.</item>
    /// </list>
    /// A user who genuinely needs these has host access; that is a different
    /// conversation from a query console.
    /// </remarks>
    private static readonly HashSet<string> AlwaysBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODULE", "MIGRATE", "REPLICAOF", "SLAVEOF", "DEBUG", "MONITOR", "ACL", "SYNC", "PSYNC",
    };

    /// <summary>
    /// Permitted only with <c>database.query_destructive</c>.
    /// </summary>
    /// <remarks>
    /// <c>KEYS</c> is here rather than merely discouraged: it walks the entire
    /// keyspace while blocking every other client, and on a large production
    /// instance that is an outage, not a slow query. Any key browsing Airside
    /// offers uses <c>SCAN</c>.
    /// </remarks>
    private static readonly HashSet<string> RequiresElevation = new(StringComparer.OrdinalIgnoreCase)
    {
        "KEYS", "FLUSHALL", "FLUSHDB", "SHUTDOWN", "SWAPDB", "RESET", "FAILOVER",
        "SAVE", "BGSAVE", "BGREWRITEAOF",
        "SCRIPT", "EVAL", "EVALSHA", "FUNCTION",
    };

    /// <summary>
    /// Commands whose danger depends on the subcommand. <c>CONFIG GET</c> is
    /// harmless; <c>CONFIG SET</c> can disable persistence or change
    /// <c>maxmemory</c> out from under the platform's own accounting.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ElevatedSubcommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CONFIG"] = new(StringComparer.OrdinalIgnoreCase) { "SET", "RESETSTAT", "REWRITE" },
            ["CLIENT"] = new(StringComparer.OrdinalIgnoreCase) { "KILL", "PAUSE", "UNPAUSE", "NO-EVICT" },
            ["CLUSTER"] = new(StringComparer.OrdinalIgnoreCase) { "RESET", "FORGET", "FAILOVER", "SETSLOT" },
            ["LATENCY"] = new(StringComparer.OrdinalIgnoreCase) { "RESET" },
        };

    public QueryDialect Dialect => QueryDialect.RedisCommand;

    public Result Evaluate(string statement, bool callerHasDestructivePermission)
    {
        IReadOnlyList<string> tokens;

        try
        {
            tokens = RedisCommandTokenizer.Tokenize(statement ?? string.Empty);
        }
        catch (FormatException ex)
        {
            return new Error(ErrorCodes.ValidationFailed, ex.Message);
        }

        if (tokens.Count == 0)
        {
            return new Error(ErrorCodes.ValidationFailed, "Enter a command.");
        }

        var command = tokens[0];

        if (AlwaysBlocked.Contains(command))
        {
            return new Error(
                ErrorCodes.QueryCommandBlocked,
                $"{command.ToUpperInvariant()} is never permitted through the console. It can compromise "
                + "the instance or the host rather than merely destroying data.",
                Metadata(command, null));
        }

        var subcommand = tokens.Count > 1 ? tokens[1] : null;

        var needsElevation = RequiresElevation.Contains(command)
            || (subcommand is not null
                && ElevatedSubcommands.TryGetValue(command, out var subs)
                && subs.Contains(subcommand));

        if (needsElevation && !callerHasDestructivePermission)
        {
            return new Error(
                ErrorCodes.QueryCommandRequiresElevation,
                Explain(command, subcommand),
                Metadata(command, subcommand));
        }

        return Result.Ok();
    }

    private static string Explain(string command, string? subcommand) =>
        command.ToUpperInvariant() switch
        {
            "KEYS" => "KEYS scans the whole keyspace and blocks every other client until it finishes. "
                + "Use SCAN instead, or ask for the destructive-command permission.",
            "FLUSHALL" or "FLUSHDB" => $"{command.ToUpperInvariant()} erases data irreversibly and needs "
                + "the destructive-command permission.",
            "SHUTDOWN" => "Stop the database from its lifecycle controls rather than through the console; "
                + "SHUTDOWN here needs the destructive-command permission.",
            "CONFIG" => "CONFIG SET changes runtime configuration that Airside's own accounting depends on, "
                + "and needs the destructive-command permission.",
            _ => $"{command.ToUpperInvariant()}"
                + (subcommand is null ? string.Empty : $" {subcommand.ToUpperInvariant()}")
                + " needs the destructive-command permission.",
        };

    private static Dictionary<string, object?> Metadata(string command, string? subcommand)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command.ToUpperInvariant(),
            ["requiredPermission"] = Core.Security.Permissions.DatabaseQueryDestructive,
        };

        if (subcommand is not null)
        {
            metadata["subcommand"] = subcommand.ToUpperInvariant();
        }

        return metadata;
    }
}

/// <summary>
/// The SQL and MongoDB consoles run whatever the caller writes.
/// </summary>
/// <remarks>
/// Deliberately permissive, and worth being explicit about rather than shipping a
/// reassuring denylist. Blocking <c>DROP TABLE</c> while allowing
/// <c>UPDATE users SET …</c> with no <c>WHERE</c> would be security theatre: both
/// destroy the data, and the second is likelier. The control that means something
/// is <c>database.query</c> being a separate permission from everything else, so
/// an operator who may restart a database cannot necessarily read or rewrite it.
/// <para>
/// Redis is different, and has a real policy, because there the danger is not the
/// data — it is <c>MODULE LOAD</c> executing native code and <c>KEYS</c> taking
/// the server offline.
/// </para>
/// </remarks>
public sealed class PermissiveCommandPolicy(QueryDialect dialect) : ICommandPolicy
{
    public QueryDialect Dialect => dialect;

    public Result Evaluate(string statement, bool callerHasDestructivePermission) =>
        string.IsNullOrWhiteSpace(statement)
            ? new Error(ErrorCodes.ValidationFailed, "Enter a statement.")
            : Result.Ok();
}
