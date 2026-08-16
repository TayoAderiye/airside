using System.Threading.RateLimiting;
using Airside.Api.Contracts;
using Airside.Api.Features;
using Airside.Api.Features.Applications;
using Airside.Api.Features.Domains;
using Airside.Runtime.Domains;
using Airside.Runtime.Dns;
using Airside.Core.Domains;
using Airside.Api.Features.Databases;
using Airside.Api.Hosting;
using Airside.Api.Realtime;
using Airside.Api.Infrastructure;
using Airside.Api.Jobs;
using Airside.Api.Security;
using Airside.Core.Containers;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Data.Jobs;
using Airside.Data.Migrations.Postgres;
using Airside.Data.Migrations.Sqlite;
using Airside.Data.Seeding;
using Airside.Runtime;
using Airside.Runtime.Jobs;
using Airside.Runtime.Proxy;
using Airside.Runtime.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logging. The redaction policy is registered before anything else can log.
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Destructure.With(new SecretRedactionPolicy())
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

builder.Services.AddSingleton(TimeProvider.System);

// ---------------------------------------------------------------------------
// Store. Chosen at install; there is no migration path between the two, so the
// value is written once and read from InstanceSettings thereafter.
// ---------------------------------------------------------------------------
var storeOptions = builder.Configuration.GetSection(AirsideStoreOptions.Section).Get<AirsideStoreOptions>()
    ?? new AirsideStoreOptions();

switch (storeOptions.Provider)
{
    case StoreProvider.Sqlite:
        builder.Services.AddSqliteStore(storeOptions.ConnectionString);
        break;
    case StoreProvider.Postgres:
        builder.Services.AddPostgresStore(storeOptions.ConnectionString);
        break;
    default:
        throw new InvalidOperationException($"Unknown store provider '{storeOptions.Provider}'.");
}

builder.Services.Configure<AirsideStoreOptions>(builder.Configuration.GetSection(AirsideStoreOptions.Section));
builder.Services.AddAirsideData();
builder.Services.AddAirsideRuntime();
builder.Services.Configure<DockerOptions>(builder.Configuration.GetSection(DockerOptions.Section));

// ---------------------------------------------------------------------------
// Secrets. The key ring lives on a host bind mount so it survives the container
// being replaced — losing it makes every stored secret unrecoverable.
// ---------------------------------------------------------------------------
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(storeOptions.KeyRingPath))
    .SetApplicationName("Airside");

// ---------------------------------------------------------------------------
// Identity: the user store only. IdentityUserContext maps no role tables, so
// Identity supplies password hashing, lockout, security stamps, and TOTP while
// Airside's own permission model handles authorisation.
// ---------------------------------------------------------------------------
builder.Services.AddIdentityCore<AirsideUser>(options =>
{
    options.Password.RequiredLength = 12;
    options.Password.RequireNonAlphanumeric = false;
    options.User.RequireUniqueEmail = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddEntityFrameworkStores<AirsideDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "airside.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;

        // Always secure in production. On first run there is no domain and
        // therefore no publicly trusted certificate, so the installer's own
        // warning — not a relaxed cookie — is what covers that window.
        //
        // SameSite=Strict works for the live streams because EventSource sends
        // cookies on same-origin requests; the dashboard and API share an origin.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // An API returns status codes; it does not redirect a fetch() to a login
        // page and let the client parse HTML to discover it was signed out.
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization();
builder.Services.AddScoped<ClaimsFactory>();

// ---------------------------------------------------------------------------
// Jobs. The store is the queue; the channel is only a doorbell.
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<JobSignal>();
builder.Services.AddSingleton<IJobSignal>(sp => sp.GetRequiredService<JobSignal>());
builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
builder.Services.AddSingleton<IJobProgressObserver, JobEventPublisher>();
builder.Services.AddScoped<IJobHandlerRegistry, JobHandlerRegistry>();
builder.Services.AddHostedService<JobDispatcherService>();

builder.Services.AddScoped<IHostAllocationReader, HostAllocationReader>();
builder.Services.AddSingleton<AllocationGate>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<IDatabaseWorkloadStore, DatabaseWorkloadStore>();
builder.Services.AddScoped<IBackupStore, BackupStore>();
builder.Services.AddScoped<ApplicationService>();
builder.Services.AddScoped<IApplicationStore, ApplicationStore>();
builder.Services.AddScoped<DomainStore>();
builder.Services.AddScoped<IDomainStore>(sp => sp.GetRequiredService<DomainStore>());
builder.Services.AddScoped<IApplicationLifecycleStore, ApplicationLifecycleStore>();
builder.Services.AddScoped<LifecycleServices>();
builder.Services.AddScoped<IContainerRuntimeAccessor, ProxyNetworkAccessor>();
builder.Services.AddScoped<IHostnameRegistry>(sp => sp.GetRequiredService<DomainStore>());
builder.Services.AddScoped<IIssuanceLedger, IssuanceLedger>();
builder.Services.Configure<AcmeRateLimitOptions>(builder.Configuration.GetSection(AcmeRateLimitOptions.Section));
builder.Services.Configure<DnsOptions>(builder.Configuration.GetSection(DnsOptions.Section));
builder.Services.Configure<ReachabilityOptions>(builder.Configuration.GetSection(ReachabilityOptions.Section));
builder.Services.AddHostedService<CertificateExpiryService>();
builder.Services.AddAirsideForwardedHeaders(builder.Configuration);
builder.Services.AddHostedService<CertificateStoreCheck>();
builder.Services.AddHostedService<DomainResetCheck>();
builder.Services.Configure<CaddyOptions>(builder.Configuration.GetSection(CaddyOptions.Section));
builder.Services.AddHostedService<ProxyReconciliationService>();
builder.Services.AddHostedService<HostDiscoveryService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Keyed by IP: an attacker guessing passwords supplies the email, so
    // per-account limiting alone lets them cycle accounts freely.
    options.AddPolicy(RateLimitPolicies.Authentication, http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));

    options.AddPolicy(RateLimitPolicies.Destructive, http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Before anything that reads the client address: audit entries, rate limiting,
// and generated links are all wrong if this runs later. Only the proxies named
// in configuration are believed — see ForwardedHeaderSetup.
app.UseForwardedHeaders();

// ---------------------------------------------------------------------------
// Migrate and seed before serving traffic. A failed migration means the health
// check never passes, which is exactly what triggers the updater's rollback.
// ---------------------------------------------------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AirsideDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);

    var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
    await seeder.SeedAsync(storeOptions.Provider, CancellationToken.None).ConfigureAwait(false);

    AirsideBanner.Write(BuildInfo.Version, storeOptions.Provider.ToString().ToLowerInvariant());

    await SetupTokenPrinter.EnsureAsync(scope.ServiceProvider, CancellationToken.None).ConfigureAwait(false);
}

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// No /api/v1 prefix, deliberately. The self-updater polls this to decide whether
// to roll back, so it must not move between versions.
app.MapGet("/health", async (IContainerRuntime runtime, AirsideDbContext db, CancellationToken ct) =>
{
    var dbReachable = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
    var runtimeReachable = await runtime.IsAvailableAsync(ct).ConfigureAwait(false);

    // The store is a hard dependency; Docker is not. An unreachable daemon means
    // no workload operations, but the dashboard must still load and say so —
    // failing health here would make the updater roll back a perfectly good
    // release because the host's Docker was restarting.
    return dbReachable
        ? Results.Ok(new { status = "healthy", database = true, runtime = runtimeReachable })
        : Results.Json(new { status = "unhealthy", database = false, runtime = runtimeReachable },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous().WithTags("Health");

app.MapSetupEndpoints();
app.MapAuthEndpoints();
app.MapHostEndpoints();
app.MapJobEndpoints();
app.MapAuditEndpoints();
app.MapAccessEndpoints();
app.MapSystemEndpoints();
app.MapDatabaseEndpoints();
app.MapBackupEndpoints();
app.MapApplicationEndpoints();
app.MapApplicationLifecycleEndpoints();
app.MapDomainEndpoints();
app.MapDomainMoveEndpoints();
app.MapDashboardDomainEndpoints();

app.MapRealtimeEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so integration tests can drive the real pipeline via WebApplicationFactory.</summary>
public partial class Program;
