using Airside.Api.Features;
using Airside.Api.Realtime;
using Airside.Core.Containers;
using Airside.Core.Security;
using Airside.Data;
using Airside.Tests.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Airside.Tests.Api;

/// <summary>
/// That both kinds of workload have a live log stream, behind the same
/// permission.
/// </summary>
/// <remarks>
/// <para>
/// Only databases had one. The Monitoring screen — whose entire purpose is
/// watching what the host is doing — answered "applications have no live log
/// stream yet" for every application, and "read this on the host" for Airside's
/// own four containers. On a host with no database provisioned, that was every
/// row on the page.
/// </para>
/// <para>
/// These assert the routes exist and are gated, not what they emit. What they
/// emit needs a Docker daemon and is covered by the integration tests.
/// </para>
/// </remarks>
public sealed class LogStreamRoutingTests
{
    [Theory]
    [InlineData("/api/v1/databases/{id:guid}/logs/stream")]
    [InlineData("/api/v1/applications/{id:guid}/logs/stream")]
    public void BothKindsOfWorkloadHaveALogStream(string pattern) => Assert.NotNull(FindRoute(pattern));

    [Theory]
    [InlineData("/api/v1/databases/{id:guid}/logs/stream")]
    [InlineData("/api/v1/applications/{id:guid}/logs/stream")]
    public void ALogStreamRequiresThePermissionToReadLogs(string pattern)
    {
        // Adding the application route by pointing it at the existing handler
        // makes it easy to add the route and forget the gate, at which point
        // every signed-in user can read any container's output.
        var endpoint = FindRoute(pattern);

        var required = endpoint.Metadata
            .OfType<IAuthorizeData>()
            .Select(a => a.Policy)
            .ToList();

        Assert.Contains(Permissions.LogsRead, required);
    }

    private static RouteEndpoint FindRoute(string pattern)
    {
        var builder = WebApplication.CreateBuilder();

        // Minimal APIs decide at map time whether a parameter comes from the
        // container or the request body, so the handlers' dependencies have to
        // be registered even though nothing here dispatches a request.
        builder.Services.AddDbContext<AirsideDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        builder.Services.AddSingleton<IContainerRuntime, FakeContainerRuntime>();
        builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.MapRealtimeEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        return Assert.Single(
            endpoints,
            e => string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal));
    }
}
