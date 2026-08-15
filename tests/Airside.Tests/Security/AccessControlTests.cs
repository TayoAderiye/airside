using Airside.Api.Security;
using Airside.Core.Security;
using Airside.Runtime.Security;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Airside.Tests.Security;

public class PermissionHandlerTests
{
    private static AuthorizationHandlerContext ContextFor(
        PermissionRequirement requirement,
        params string[] heldPermissions)
    {
        var identity = new ClaimsIdentity(
            heldPermissions.Select(p => new Claim(AirsideClaims.Permission, p)),
            authenticationType: "test");

        return new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(identity), resource: null);
    }

    [Fact]
    public async Task Handler_WithMatchingPermission_Succeeds()
    {
        var requirement = new PermissionRequirement(Permissions.DatabaseCreate);
        var context = ContextFor(requirement, Permissions.DatabaseCreate);

        await new PermissionHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_WithoutPermission_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(Permissions.DatabaseQuery);
        var context = ContextFor(requirement, Permissions.DatabaseLifecycle);

        await new PermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_PermissionMatchIsCaseSensitive()
    {
        // Permission codes are exact strings. A case-insensitive match would make
        // "Database.Create" work, and any leniency in an authorisation check is a
        // way in that nobody wrote down.
        var requirement = new PermissionRequirement(Permissions.DatabaseCreate);
        var context = ContextFor(requirement, "Database.Create");

        await new PermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public void RequirePermission_UnknownPermission_ThrowsAtRegistration()
    {
        // A typo would otherwise produce a policy nobody holds: the endpoint
        // fails closed but silently, which in production is indistinguishable
        // from a permissions bug.
        var builder = new StubEndpointBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.RequirePermission("databse.create"));
        Assert.Contains("not a known permission", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequirePermission_KnownPermission_IsAccepted()
    {
        var builder = new StubEndpointBuilder();

        var returned = builder.RequirePermission(Permissions.DatabaseCreate);

        Assert.Same(builder, returned);
        Assert.Single(builder.Conventions);
    }

    private sealed class StubEndpointBuilder : IEndpointConventionBuilder
    {
        public List<Action<EndpointBuilder>> Conventions { get; } = [];

        public void Add(Action<EndpointBuilder> convention) => Conventions.Add(convention);
    }
}

public class RoleSeparationTests
{
    /// <summary>
    /// The brief's own example: a user may be allowed to restart a database but
    /// not to read its contents. If these two ever overlap, the entire reason for
    /// permission-based authorisation has quietly evaporated.
    /// </summary>
    [Fact]
    public void InfrastructureAndQueryPermissions_AreDistinct()
    {
        Assert.NotEqual(Permissions.DatabaseLifecycle, Permissions.DatabaseQuery);
        Assert.Contains(Permissions.DatabaseQuery, Permissions.All, StringComparer.Ordinal);
        Assert.Contains(Permissions.DatabaseQueryDestructive, Permissions.All, StringComparer.Ordinal);
    }

    [Fact]
    public void SecretViewAndSecretRead_AreDistinct()
    {
        // Knowing that STRIPE_SECRET_KEY exists is not the same as knowing what
        // it is, and the two are separate permissions for that reason.
        Assert.NotEqual(Permissions.SecretView, Permissions.SecretRead);
    }

    [Fact]
    public void PermissionCatalogue_HasNoDuplicates()
    {
        Assert.Equal(Permissions.All.Count, Permissions.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PermissionCodes_FollowTheDottedConvention()
    {
        foreach (var code in Permissions.All)
        {
            Assert.Matches("^[a-z]+\\.[a-z_]+$", code);
        }
    }
}

public class SecretGeneratorTests
{
    [Fact]
    public void GeneratePassword_AvoidsConnectionStringBreakingCharacters()
    {
        var generator = new SecretGenerator();

        for (var i = 0; i < 200; i++)
        {
            var password = generator.GeneratePassword().Reveal();

            Assert.DoesNotContain('"', password);
            Assert.DoesNotContain('\'', password);
            Assert.DoesNotContain(';', password);
            Assert.DoesNotContain('\\', password);
            Assert.DoesNotContain('@', password);
            Assert.DoesNotContain(':', password);
            Assert.DoesNotContain('/', password);
        }
    }

    [Fact]
    public void GeneratePassword_ProducesDistinctValues()
    {
        var generator = new SecretGenerator();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 500; i++)
        {
            Assert.True(seen.Add(generator.GeneratePassword().Reveal()));
        }
    }

    [Fact]
    public void TokenMatches_CorrectToken_ReturnsTrue()
    {
        var generator = new SecretGenerator();
        var token = generator.GenerateToken();

        Assert.True(SecretGenerator.TokenMatches(token, SecretGenerator.HashToken(token)));
    }

    [Fact]
    public void TokenMatches_WrongToken_ReturnsFalse()
    {
        var generator = new SecretGenerator();
        var stored = SecretGenerator.HashToken(generator.GenerateToken());

        Assert.False(SecretGenerator.TokenMatches(generator.GenerateToken(), stored));
    }

    [Fact]
    public void GeneratePassword_RejectsWeakLengths()
    {
        var generator = new SecretGenerator();
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.GeneratePassword(8));
    }
}
