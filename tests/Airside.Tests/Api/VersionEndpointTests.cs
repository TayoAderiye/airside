using System.Reflection;
using Airside.Api.Contracts;
using Airside.Api.Features;
using Airside.Core.Containers;
using Airside.Data;
using Airside.Tests.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Airside.Tests.Api;

/// <summary>
/// That the dashboard can always ask the API what version it is.
/// </summary>
/// <remarks>
/// <para>
/// The UI ships as its own container and can therefore be a different version
/// from the API it is talking to. It resolves that by asking, on load, before it
/// renders anything — which only works if asking is possible while logged out.
/// </para>
/// <para>
/// The failure this guards against is quiet and misleading rather than loud. Put
/// this endpoint behind authentication and a stale dashboard no longer learns it
/// is stale; it gets a 401 from the one call meant to explain the problem, and
/// reports "cannot reach the API" to an operator whose API is running perfectly.
/// </para>
/// </remarks>
public sealed class VersionEndpointTests
{
    [Fact]
    public void TheVersionEndpointIsReachableWithoutAuthentication()
    {
        var endpoint = FindRoute("/api/v1/version");

        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    /// <summary>
    /// The neighbouring route in the same group, to show the assertion above
    /// distinguishes anything.
    /// </summary>
    /// <remarks>
    /// Without this, mapping the group in a way that attached no authorization
    /// metadata at all would make the anonymous assertion pass for the wrong
    /// reason, and the test would go on passing after the property it exists to
    /// protect had stopped being true.
    /// </remarks>
    [Fact]
    public void TheSystemInfoEndpointBesideItStillRequiresAuthentication()
    {
        var endpoint = FindRoute("/api/v1/system/info");

        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>());
    }

    /// <summary>
    /// <see cref="VersionDto"/> is documented as frozen, so the freeze is asserted.
    /// </summary>
    /// <remarks>
    /// A comment saying "do not rename this" is advice. This is the part that
    /// fails the build. Adding a property is allowed and does not break a reader
    /// that ignores it, so only the existing one is pinned.
    /// </remarks>
    [Fact]
    public void TheVersionResponseStillCarriesAStringNamedVersion()
    {
        var property = typeof(VersionDto).GetProperty(
            "Version",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property.PropertyType);
    }

    private static RouteEndpoint FindRoute(string pattern)
    {
        var builder = WebApplication.CreateBuilder();

        // Minimal APIs decide at map time whether a parameter comes from the
        // container or the request body, so the handlers' dependencies have to be
        // registered even though nothing here ever dispatches a request — an
        // unknown type on a GET is inferred as a body and throws on the spot.
        builder.Services.AddDbContext<AirsideDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        builder.Services.AddSingleton<IContainerRuntime, FakeContainerRuntime>();
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.MapSystemEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        return Assert.Single(
            endpoints,
            e => string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal));
    }
}
