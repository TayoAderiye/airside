using System.Threading.RateLimiting;
using Airside.Api.Contracts;
using Airside.Api.Features;
using Airside.Api.Features.Applications;
using Airside.Api.Features.Databases;
using Airside.Api.Features.Domains;
using Airside.Api.Features.Notifications;
using Airside.Api.Features.Operations;
using Airside.Api.Features.Registries;
using Airside.Api.Hosting;
using Airside.Api.Infrastructure;
using Airside.Api.Jobs;
using Airside.Api.Realtime;
using Airside.Api.Security;
using Airside.Core.Containers;
using Airside.Core.Domains;
using Airside.Core.Naming;
using Airside.Core.Notifications;
using Airside.Core.Operations;
using Airside.Data;
using Airside.Data.Entities;
using Airside.Data.Jobs;
using Airside.Data.Migrations.Postgres;
using Airside.Data.Migrations.Sqlite;
using Airside.Data.Seeding;
using Airside.Runtime;
using Airside.Runtime.Dns;
using Airside.Runtime.Domains;
using Airside.Runtime.Jobs;
using Airside.Runtime.Notifications;
using Airside.Runtime.Operations;
using Airside.Runtime.Proxy;
using Airside.Runtime.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

// ---------------------------------------------------------------------------
// Health probe, before anything else starts.
//
// The runtime image is chiselled: no shell, no wget, no curl. A Docker
// HEALTHCHECK has to run inside the container it describes, so there was nothing
// available to run one with — the compose healthcheck called wget and could only
// ever fail, leaving the API permanently unhealthy, and the installer's
// readiness loop called the same missing binary and always concluded the API had
// not come up.
//
// So the probe is this binary. Heavier than curl, but it is the only executable
// in the image, and a healthcheck that cannot run is worth less than none at all.
// ---------------------------------------------------------------------------
if (args is ["--health", ..])
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    try
    {
        var url = Environment.GetEnvironmentVariable("AIRSIDE_HEALTH_URL")
            ?? "http://localhost:8080/health";

        using var health = await probe.GetAsync(new Uri(url)).ConfigureAwait(false);

        return health.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
    {
        // Unhealthy, not crashed. Docker reads the exit code and a stack trace
        // here would be written to the healthcheck log every interval.
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);

        return 1;
    }
}

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

// Checked here rather than discovered later, because Data Protection does not
// touch the directory until something is first encrypted — and the first thing
// that encrypts anything is issuing a session cookie. A key ring the process
// cannot write therefore produces a control plane that starts cleanly, reports
// healthy, migrates, seeds, accepts the setup token, creates the administrator,
// and then throws a 500 on the very first login. The operator is told
// "internal.unhandled" about a filesystem permission.
//
// This is exactly what a root-owned 0700 directory does to a container running
// as a non-root user, which is what the installer used to create.
KeyRingPreflight.Verify(storeOptions.KeyRingPath);

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

// Reads Airside's own containers so they appear beside the workloads it manages.
// Scoped rather than singleton: it asks Docker for current state, and a cached
// answer would show a stopped container as running.
builder.Services.AddScoped<Airside.Api.Features.SystemWorkloadReader>();
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

// Operations. The update options are resolved as a concrete instance rather than
// IOptions because the CLI-facing paths in them are also read at startup, before
// the options pipeline would normally be consulted.
builder.Services.AddSingleton(sp =>
{
    var options = new UpdateOptions();
    builder.Configuration.GetSection(UpdateOptions.Section).Bind(options);
    return options;
});
builder.Services.AddScoped<INotifier, Notifier>();
builder.Services.AddSingleton<ITotp, Totp>();
builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<IOptions<AirsideStoreOptions>>().Value;

    return new SystemBackupContext(
        store.Provider.ToString(),
        store.ConnectionString,
        store.KeyRingPath,
        AirsideLabels.SystemContainers.Database,
        "airside",
        "airside");
});
builder.Services.AddSingleton<ISystemBackupProvider, SystemBackupProvider>();
builder.Services.AddSingleton<UpdateOrchestrator>();
builder.Services.AddHostedService<MetricSampler>();
builder.Services.AddScoped<RegistryCredentialStore>();
builder.Services.AddScoped<IRegistryCredentialSource>(sp => sp.GetRequiredService<RegistryCredentialStore>());

// Notification dispatch. The webhook client is built on the guarded handler, so
// every outbound request is checked against the resolved address at connect time
// — see OutboundGuard for why validating the URL at save time is not a check.
builder.Services.AddSingleton(sp =>
{
    var dispatch = new DispatchOptions();
    builder.Configuration.GetSection(DispatchOptions.Section).Bind(dispatch);
    return dispatch;
});
builder.Services.AddHttpClient<WebhookTransport>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(sp => GuardedHttp.CreateHandler(
        sp.GetRequiredService<DispatchOptions>().AllowPrivateDestinations,
        sp.GetRequiredService<ILogger<WebhookTransport>>()));
builder.Services.AddSingleton<INotificationTransport>(sp => sp.GetRequiredService<WebhookTransport>());
builder.Services.AddSingleton<INotificationTransport>(sp =>
    new SlackTransport(sp.GetRequiredService<WebhookTransport>()));
builder.Services.AddSingleton<INotificationTransport, EmailTransport>();
builder.Services.AddHostedService<NotificationDispatcher>();
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
app.MapOperationsEndpoints();
app.MapMfaEndpoints();
app.MapRegistryEndpoints();
app.MapNotificationChannelEndpoints();
app.MapDashboardDomainEndpoints();

app.MapRealtimeEndpoints();
// Served in every environment, behind authentication. The UI is a separate
// codebase generated from this document, and gating it to Development means the
// people building against a real instance cannot read the contract of the thing
// they are building against. It describes routes rather than exposing them —
// every one still enforces its own permission.
app.MapOpenApi().RequireAuthorization();

await app.RunAsync().ConfigureAwait(false);

// The --health branch at the top returns an exit code, which makes this whole
// entry point int-returning. A clean shutdown is a zero.
return 0;

/// <summary>Exposed so integration tests can drive the real pipeline via WebApplicationFactory.</summary>
public partial class Program;
