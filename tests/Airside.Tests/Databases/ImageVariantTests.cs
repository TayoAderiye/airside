using Airside.Api.Features.Databases;
using Airside.Core.Common;
using Airside.Core.Containers;
using Airside.Core.Databases;
using Airside.Data.Entities;
using Airside.Tests.Fakes;

namespace Airside.Tests.Databases;

/// <summary>
/// Variant resolution, which fails at image-pull time if it is wrong — long
/// after the mistake was made and with an error that names a tag rather than a
/// cause.
/// </summary>
public class ImageVariantTests
{
    [Fact]
    public void PostgresDefaultsToAlpine()
    {
        var engine = EngineFactory.Postgres();

        Assert.Equal(ImageVariant.Alpine, engine.Capabilities.DefaultVariant);
        Assert.Equal("postgres:16-alpine", engine.ResolveImage("16", ImageVariant.Alpine).ToString());
    }

    [Fact]
    public void RedisDefaultsToAlpine()
    {
        var engine = EngineFactory.Redis();

        Assert.Equal(ImageVariant.Alpine, engine.Capabilities.DefaultVariant);
        Assert.Equal("redis:7.4-alpine", engine.ResolveImage("7.4", ImageVariant.Alpine).ToString());
    }

    [Fact]
    public void PostgresDebianIsTheUnsuffixedTag()
    {
        Assert.Equal("postgres:16", EngineFactory.Postgres().ResolveImage("16", ImageVariant.Debian).ToString());
        Assert.Equal("redis:7.4", EngineFactory.Redis().ResolveImage("7.4", ImageVariant.Debian).ToString());
    }

    [Fact]
    public void MySqlAndMongoDefaultToDebianAndNeverCarryASuffix()
    {
        // The failure this guards against: a shared default of Alpine leaking
        // into a resolver for an engine that publishes no Alpine image, producing
        // mysql:8.4-alpine — a tag that does not exist.
        Assert.Equal(ImageVariant.Debian, EngineFactory.MySql().Capabilities.DefaultVariant);
        Assert.Equal(ImageVariant.Debian, EngineFactory.MongoDb().Capabilities.DefaultVariant);

        Assert.Equal("mysql:8.4", EngineFactory.MySql().ResolveImage("8.4", ImageVariant.Debian).ToString());
        Assert.Equal("mongo:8.0", EngineFactory.MongoDb().ResolveImage("8.0", ImageVariant.Debian).ToString());
    }

    [Fact]
    public void MySqlAndMongoNeverProduceAnAlpineTagEvenIfAskedDirectly()
    {
        // Belt and braces: even a caller that gets past validation cannot make the
        // resolver emit a tag upstream does not publish.
        Assert.DoesNotContain(
            "alpine",
            EngineFactory.MySql().ResolveImage("8.4", ImageVariant.Alpine).ToString(),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "alpine",
            EngineFactory.MongoDb().ResolveImage("8.0", ImageVariant.Alpine).ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEngineDeclaresItsDefaultAmongItsSupportedVariants()
    {
        foreach (var engine in EngineFactory.All())
        {
            Assert.NotEmpty(engine.Capabilities.SupportedVariants);
            Assert.Contains(engine.Capabilities.DefaultVariant, engine.Capabilities.SupportedVariants);
        }
    }

    [Fact]
    public void SingleVariantEnginesRejectTheVariantTheyDoNotPublish()
    {
        var result = EngineFactory.MySql().Validate(Spec.MySql() with { Variant = ImageVariant.Alpine });

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFieldNotApplicable, result.Failure!.Code);
        Assert.Equal("imageVariant", result.Failure.Metadata!["field"]);
    }

    [Fact]
    public void MultiVariantEnginesAcceptEither()
    {
        Assert.True(EngineFactory.Postgres().Validate(Spec.Postgres() with { Variant = ImageVariant.Alpine }).IsSuccess);
        Assert.True(EngineFactory.Postgres().Validate(Spec.Postgres() with { Variant = ImageVariant.Debian }).IsSuccess);
    }

    [Fact]
    public void OmittingTheVariantIsAlwaysValid()
    {
        foreach (var (engine, spec) in EngineFactory.AllWithSpecs())
        {
            Assert.True(engine.Validate(spec with { Variant = null }).IsSuccess);
        }
    }

    [Fact]
    public void ContainerSpecUsesTheEngineDefaultWhenNoVariantIsGiven()
    {
        Slug.TryCreate("orders", out var slug);
        var context = new ProvisionContext("c", "n", "v", new Dictionary<string, string>(StringComparer.Ordinal));

        var mysql = EngineFactory.MySql()
            .BuildContainerSpec(Spec.MySql() with { Slug = slug, Variant = null }, context);

        Assert.Equal("mysql:8.4", mysql.Image.ToString());

        var postgres = EngineFactory.Postgres()
            .BuildContainerSpec(Spec.Postgres() with { Slug = slug, Variant = null }, context);

        Assert.Equal("postgres:16-alpine", postgres.Image.ToString());
    }
}

public class ImageReferenceParsingTests
{
    [Fact]
    public void ParsesARepoDigest()
    {
        // The form Docker reports in RepoDigests, and what Airside records so
        // later resolutions go by digest rather than tag.
        var image = ImageReference.Parse("postgres@sha256:abc123");

        Assert.Equal("postgres", image.Repository);
        Assert.Equal("sha256:abc123", image.Digest);
        Assert.Equal("postgres@sha256:abc123", image.ToString());
    }

    [Fact]
    public void ParsesATaggedReference()
    {
        var image = ImageReference.Parse("pgvector/pgvector:pg16");

        Assert.Equal("pgvector/pgvector", image.Repository);
        Assert.Equal("pg16", image.Tag);
    }

    [Fact]
    public void DoesNotMistakeARegistryPortForATag()
    {
        // registry.example.com:5000/team/image has a colon before the last slash;
        // splitting on it would produce a nonsense repository.
        var image = ImageReference.Parse("registry.example.com:5000/team/image");

        Assert.Equal("registry.example.com:5000/team/image", image.Repository);
        Assert.Equal("latest", image.Tag);
    }

    [Fact]
    public void DefaultsToLatestWhenNoTagIsGiven()
    {
        Assert.Equal("latest", ImageReference.Parse("postgres").Tag);
    }
}

public class VariantImmutabilityTests
{
    private static DatabaseInstance Existing(ImageVariant variant) => new()
    {
        Slug = "orders",
        Engine = DatabaseEngineKind.Postgres,
        Version = "16",
        ImageVariant = variant,
    };

    [Fact]
    public void ChangingTheVariantIsRejected()
    {
        var result = DatabaseService.RejectVariantChange(Existing(ImageVariant.Alpine), ImageVariant.Debian);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFieldNotApplicable, result.Failure!.Code);
        Assert.Equal("alpine", result.Failure.Metadata!["current"]);
        Assert.Equal("debian", result.Failure.Metadata["requested"]);
    }

    [Fact]
    public void RestatingTheSameVariantIsAllowed()
    {
        // An update that resends the current value is not a change, and rejecting
        // it would make every full-object PUT fail.
        Assert.True(DatabaseService.RejectVariantChange(Existing(ImageVariant.Alpine), ImageVariant.Alpine).IsSuccess);
    }

    [Fact]
    public void OmittingTheVariantIsAllowed()
    {
        Assert.True(DatabaseService.RejectVariantChange(Existing(ImageVariant.Debian), null).IsSuccess);
    }

    [Fact]
    public void TheRejectionExplainsWhyRatherThanJustRefusing()
    {
        var message = DatabaseService.RejectVariantChange(
            Existing(ImageVariant.Alpine), ImageVariant.Debian).Failure!.Message;

        Assert.Contains("libc", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restore", message, StringComparison.OrdinalIgnoreCase);
    }
}
