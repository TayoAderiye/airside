using Airside.Core.Common;
using Airside.Core.Databases;
using Airside.Runtime.Applications;
using Airside.Tests.Databases;
using Airside.Tests.Fakes;

namespace Airside.Tests.Applications;

/// <summary>
/// Build-context path handling, which is the boundary between a repository and
/// the control plane's own filesystem.
/// </summary>
public class BuildContextPathTests
{
    private const string Root = "/build/context";

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("docker/Dockerfile")]
    [InlineData("./services/api/Dockerfile")]
    public void PathsInsideTheContextAreAccepted(string path)
    {
        Assert.True(BuildContextPaths.ResolveWithin(Root, path).IsSuccess, path);
    }

    [Theory]
    [InlineData("../Dockerfile")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/../../../etc/shadow")]
    [InlineData("/etc/passwd")]
    [InlineData("/var/lib/airside/keys/key.xml")]
    [InlineData("~/.ssh/id_rsa")]
    public void PathsEscapingTheContextAreRejected(string path)
    {
        // The key ring lives on the same filesystem as the build workspace, so a
        // path that escapes is a read of the control plane's own secrets.
        var result = BuildContextPaths.ResolveWithin(Root, path);

        Assert.True(result.IsFailure, $"'{path}' must be rejected.");
        Assert.Equal("dockerfilePath", result.Failure!.Metadata!["field"]);
    }

    [Fact]
    public void NormalisationDoesNotOpenAHole()
    {
        // a/../../x resolves above the root even though no segment starts with
        // "..", which is why the check is on the resolved path rather than the
        // raw string.
        Assert.True(BuildContextPaths.ResolveWithin(Root, "a/../../x").IsFailure);
    }

    [Fact]
    public void ASiblingDirectoryWithASharedPrefixIsNotInside()
    {
        // /build/context-evil starts with /build/context but is a different
        // directory — the separator in the boundary check is what catches it.
        Assert.True(BuildContextPaths.ResolveWithin(Root, "../context-evil/Dockerfile").IsFailure);
    }

    [Fact]
    public void NullBytesAreRejected()
    {
        Assert.True(BuildContextPaths.ResolveWithin(Root, "Dockerfile\0.txt").IsFailure);
    }

    [Fact]
    public void EmptyPathsAreRejected()
    {
        Assert.True(BuildContextPaths.ResolveWithin(Root, "").IsFailure);
        Assert.True(BuildContextPaths.ResolveWithin(Root, "   ").IsFailure);
    }
}

public class GitUrlTests
{
    [Theory]
    [InlineData("https://github.com/tayo/app.git")]
    [InlineData("http://gitea.internal/team/app.git")]
    public void HttpAndHttpsAreAccepted(string url)
    {
        Assert.True(GitSource.ValidateUrl(url).IsSuccess, url);
    }

    [Theory]
    [InlineData("file:///var/lib/airside/keys")]
    [InlineData("ssh://git@github.com/tayo/app.git")]
    [InlineData("git://github.com/tayo/app.git")]
    public void OtherTransportsAreRejected(string url)
    {
        // libgit2 supports these. file:// would clone the control plane's own
        // disk into a build context, and ssh:// would use whatever key the
        // process can reach — neither is something a repository URL should be
        // able to request.
        var result = GitSource.ValidateUrl(url);

        Assert.True(result.IsFailure, $"'{url}' must be rejected.");
        Assert.Equal("repositoryUrl", result.Failure!.Metadata!["field"]);
    }

    [Fact]
    public void MalformedUrlsAreRejected()
    {
        Assert.True(GitSource.ValidateUrl("not a url").IsFailure);
        Assert.True(GitSource.ValidateUrl("").IsFailure);
        Assert.True(GitSource.ValidateUrl(null).IsFailure);
    }
}

public class BuildLogCappingTests
{
    [Fact]
    public void ShortLogsPassThroughUnchanged()
    {
        var (content, truncated) = BuildLog.Cap("step 1\nstep 2\ndone");

        Assert.Equal("step 1\nstep 2\ndone", content);
        Assert.False(truncated);
    }

    [Fact]
    public void LongLogsKeepBothEnds()
    {
        // The useful parts of a failed build are the first error and the last
        // line. A naive truncation keeps the head and loses the failure, which is
        // the only reason anyone opens the log.
        var log = "FIRST LINE\n"
            + string.Join('\n', Enumerable.Range(0, 200_000).Select(i => $"filler line {i}"))
            + "\nLAST LINE: build failed";

        var (content, truncated) = BuildLog.Cap(log);

        Assert.True(truncated);
        Assert.StartsWith("FIRST LINE", content, StringComparison.Ordinal);
        Assert.EndsWith("LAST LINE: build failed", content, StringComparison.Ordinal);
        Assert.Contains("lines omitted", content, StringComparison.Ordinal);
        Assert.True(content.Length < log.Length);
    }
}

/// <summary>
/// The rendered environment, which is where a rotated credential either reaches
/// the application or silently does not.
/// </summary>
public class EnvironmentRendererTests
{
    private static EnvironmentRenderer Renderer() => new(new FakeEngineRegistry());

    private static AttachedDatabase Attachment(
        DatabaseEngineKind engine,
        string prefix,
        string password = "current-password") =>
        new(
            Guid.CreateVersion7(),
            engine,
            prefix,
            new DatabaseEndpoint("cid", "airside-db-orders", 5432, "orders"),
            new DatabaseCredentialValue("app", new Secret(password)));

    [Fact]
    public void InjectedValuesComeFromTheCredentialGivenAtRenderTime()
    {
        // Nothing is stored, so a rotation reaches the application on its next
        // deploy without anyone editing an environment variable — the failure
        // this design exists to prevent is a container holding a dead password
        // while the UI shows the new one.
        var renderer = Renderer();

        var before = renderer.Render([], [Attachment(DatabaseEngineKind.Postgres, "DATABASE", "old-password")]);
        var after = renderer.Render([], [Attachment(DatabaseEngineKind.Postgres, "DATABASE", "new-password")]);

        Assert.Contains(before.Entries, e => e.Key == "DATABASE_PASSWORD" && e.Value.Reveal() == "old-password");
        Assert.Contains(after.Entries, e => e.Key == "DATABASE_PASSWORD" && e.Value.Reveal() == "new-password");
    }

    [Fact]
    public void RedisInjectsItsOwnKeysAndNotTheDatabaseSet()
    {
        var rendered = Renderer().Render([], [Attachment(DatabaseEngineKind.Redis, "REDIS")]);

        var keys = rendered.Entries.Select(e => e.Key).ToList();

        Assert.Equal(["REDIS_HOST", "REDIS_PORT", "REDIS_PASSWORD", "REDIS_URL"], keys);
    }

    [Fact]
    public void TwoAttachmentsWithDifferentPrefixesDoNotCollide()
    {
        var rendered = Renderer().Render(
            [],
            [
                Attachment(DatabaseEngineKind.Postgres, "DATABASE"),
                Attachment(DatabaseEngineKind.Postgres, "ANALYTICS"),
            ]);

        var keys = rendered.Entries.Select(e => e.Key).ToList();

        Assert.Contains("DATABASE_URL", keys, StringComparer.Ordinal);
        Assert.Contains("ANALYTICS_URL", keys, StringComparer.Ordinal);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ManualVariablesAreIncluded()
    {
        var rendered = Renderer().Render(
            [new ManualVariable("LOG_LEVEL", new Secret("debug"), false)],
            []);

        Assert.Contains(rendered.Entries, e => e.Key == "LOG_LEVEL" && e.Value.Reveal() == "debug");
    }

    [Fact]
    public void AManualVariableCannotShadowAnInjectedOne()
    {
        // The collision is refused when the variable is created, so reaching here
        // means the attachment came second. Letting the manual value win would
        // point the application somewhere other than the database the attachment
        // screen says it is attached to.
        var rendered = Renderer().Render(
            [new ManualVariable("DATABASE_URL", new Secret("postgres://elsewhere"), true)],
            [Attachment(DatabaseEngineKind.Postgres, "DATABASE")]);

        var url = rendered.Entries.Single(e => e.Key == "DATABASE_URL");

        Assert.DoesNotContain("elsewhere", url.Value.Reveal(), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryValueIsASecretSoAContainerSpecCannotLeak()
    {
        var rendered = Renderer().Render(
            [new ManualVariable("LOG_LEVEL", new Secret("debug"), false)],
            [Attachment(DatabaseEngineKind.Postgres, "DATABASE")]);

        Assert.All(rendered.Entries, e => Assert.Equal(Secret.Mask, e.Value.ToString()));
    }

    [Fact]
    public void InjectedKeysCanBeListedWithoutACredential()
    {
        // Used to detect a prefix collision before the attachment exists.
        var keys = Renderer().InjectedKeysFor(DatabaseEngineKind.MongoDb, "MONGO");

        Assert.Contains("MONGO_URI", keys, StringComparer.Ordinal);
        Assert.DoesNotContain("MONGO_NAME", keys, StringComparer.Ordinal);
    }
}
