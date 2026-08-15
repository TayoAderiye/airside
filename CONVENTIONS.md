# Airside — Conventions

Status: **approved.** No implementation code exists yet.

This document is written before the code, deliberately. Contributors pattern-match
on whatever the first commits establish, so the patterns are decided here rather
than discovered later. If you are adding code and this document does not cover
your case, extend it in the same pull request.

Rules are stated as rules. Where a rule exists to prevent a specific failure, the
failure is named — a rule whose reason is unclear gets worked around.

---

## 1. Project structure

```
src/Airside.Core                      no infrastructure dependencies
src/Airside.Data                      → Core
src/Airside.Data.Migrations.Postgres  → Data   (generated code only)
src/Airside.Data.Migrations.Sqlite    → Data   (generated code only)
src/Airside.Runtime                   → Core
src/Airside.Api                       → Core, Data, both Migrations, Runtime
src/Airside.Cli                       → nothing
tests/Airside.Tests                   → all
```

Dependencies point downward only. See `ARCHITECTURE.md` for why each project
exists.

**Enforced by CI, not by reviewer memory:**

- `Airside.Core.csproj` must have no `PackageReference` matching
  `Docker.DotNet`, `Microsoft.EntityFrameworkCore*`, `Microsoft.AspNetCore.*`,
  `Npgsql*`, or `Serilog.AspNetCore`. `Microsoft.Extensions.Logging.Abstractions`
  is allowed.
- Only `Airside.Runtime` may reference `Docker.DotNet`.
- `Airside.Cli` may not have a `ProjectReference` at all.

### Both providers, every time

Airside supports Postgres and SQLite, chosen at install. That imposes rules that
only bite when broken, so they are checked mechanically:

- No raw SQL that is not valid on both providers. No `jsonb`. JSON goes through
  EF Core's owned-entity `ToJson()`.
- No `SELECT … FOR UPDATE`. SQLite has no row locks; see the allocation gate in
  `ARCHITECTURE.md` §5.
- Provider-specific DDL — the append-only audit enforcement, for instance — lives
  only in provider-specific migrations, never in `Airside.Data`.
- **Every schema change is generated for both providers in the same pull
  request.** CI runs `dotnet ef migrations has-pending-model-changes` against
  each and fails if either is behind. A migration that lands for one provider and
  not the other breaks the other store's install, and nothing else will catch it.

Shared build settings live in `Directory.Build.props`; package versions live in
`Directory.Packages.props` (central package management). No version numbers in
individual `.csproj` files.

`Nullable` and `ImplicitUsings` enabled solution-wide. `TreatWarningsAsErrors`
enabled. `EnableNETAnalyzers` with `AnalysisLevel latest-recommended`.

`.editorconfig` is the formatting authority. `dotnet format --verify-no-changes`
runs in CI. Do not argue about formatting in review.

---

## 2. Endpoint style — minimal APIs

**Minimal APIs, grouped per feature, registered by an extension method.** Not
controllers. Chosen because it keeps endpoints thin by making it awkward to put
logic in them, and because .NET 10's OpenAPI generation for minimal APIs is what
the UI phase's generated client will consume.

```csharp
// Airside.Api/Features/Databases/DatabaseEndpoints.cs
internal static class DatabaseEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/databases")
                       .WithTags("Databases")
                       .RequireAuthorization();

        group.MapPost("/", CreateAsync)
             .RequirePermission(Permissions.DatabaseCreate)
             .WithValidation<CreateDatabaseRequest>();

        return app;
    }

    private static async Task<Results<Accepted<JobAcceptedResponse>, ProblemHttpResult>>
        CreateAsync(CreateDatabaseRequest request,
                    IDatabaseService databases,
                    CancellationToken ct)
    {
        var result = await databases.EnqueueProvisionAsync(request.ToCommand(), ct);
        return result.ToAcceptedOrProblem();
    }
}
```

**The endpoint discipline rule:** an endpoint handler binds input, calls exactly
one service method, and maps the result. If it contains a conditional about
domain state, the conditional belongs in the service. Handlers that grow past
roughly fifteen lines are a review comment.

- One file per feature group, under `Api/Features/<Feature>/`.
- Every route is versioned: `/api/v1/...`.
- Return typed results (`Results<Ok<T>, ProblemHttpResult>`), never bare
  `IResult`. The OpenAPI document is generated from these types, and the UI phase
  depends on it being accurate.
- Handlers are `private static`. No handler state.

---

## 3. Results and errors

### Domain layer: `Result` / `Result<T>`

Expected failures are values, not exceptions. Exceptions are for genuinely
exceptional conditions — a Docker daemon that has vanished, a corrupt key ring.

```csharp
public readonly record struct Error(
    string Code,                                  // stable, dotted, lowercase
    string Message,                               // human-readable, no secrets
    IReadOnlyDictionary<string, object?>? Metadata = null);
```

`Code` is a stable API contract. The UI switches on it. Renaming a code is a
breaking change. Format: `<domain>.<snake_case_reason>`, e.g.
`resource.insufficient_memory`, `database.slug_taken`, `redis.command_blocked`.

`Message` is for humans and must never contain a secret, a connection string, or
a raw exception message from an external system.

`Metadata` carries the machine-readable specifics. The resource rejection the
brief requires looks like:

```json
{
  "code": "resource.insufficient_memory",
  "message": "Not enough memory available to provision this database.",
  "metadata": { "requestedMb": 4096, "availableMb": 2048,
                "capacityMb": 8192, "allocatedMb": 5120, "reservedMb": 1024 }
}
```

The UI renders the numbers from `metadata`. It never parses `message`.

### Wire format: RFC 9457 `ProblemDetails`

Errors are `ProblemDetails`, with `code` and `metadata` as extensions.

**Successes are not wrapped in an envelope.** `200 OK` returns the resource
directly. An envelope on success duplicates what the status line already says and
makes every generated client unwrap a layer for nothing.

Status mapping is centralised in one place — do not choose status codes at the
call site:

| Situation | Status |
|---|---|
| Read succeeded | `200` |
| Resource created synchronously | `201` + `Location` |
| Long-running operation enqueued | `202` + job ID |
| Validation failure | `400` with per-field errors |
| Unauthenticated | `401` |
| Authenticated, permission missing | `403` |
| Not found, or found but not visible to this user | `404` |
| Conflict — slug taken, workload busy, wrong state | `409` |
| Allocation rejected | `409` |
| Rate limited | `429` |

`404` for "exists but you cannot see it" is deliberate: `403` confirms existence
to someone who should not know.

### Exceptions

One global exception handler (`IExceptionHandler`). It logs the full exception
with a correlation ID and returns a `500` `ProblemDetails` containing that ID and
nothing else. No exception messages, no stack traces, no inner-exception chains
in a response — on a system with root-equivalent host reach, an error message is
an information-disclosure surface.

Never `catch (Exception) { }`. Never catch to convert to a generic failure without
logging.

---

## 4. Validation

FluentValidation, one validator per request DTO, applied by an endpoint filter.
Chosen over data annotations because the rules are conditional on engine
capabilities — Redis must reject a database name, Postgres must require one — and
that reads badly as attributes.

**Reject, do not sanitise.** If input does not match, return `400`. Never strip
characters, trim into validity, or coerce. A sanitiser that silently rewrites
input is a parser bug waiting to become a security bug.

Validation runs before the handler. A handler never re-validates.

### Slugs

Every user-supplied name that reaches a container, volume, network, or DNS label
is a **slug**:

```
^[a-z][a-z0-9-]{1,30}[a-z0-9]$
```

Lowercase, starts with a letter, ends alphanumeric, 3–32 characters, no
consecutive hyphens. Validated once at the boundary into a `Slug` value type;
everything downstream takes `Slug`, not `string`.

This is the single most load-bearing validation in the system. Container names,
volume names, network names, and Caddy route matchers are all derived from slugs.
A `Slug` that cannot hold an invalid value means no downstream code needs to
wonder.

Display names are free text, stored, escaped on output, and never used to
construct an identifier.

---

## 5. Dependency injection

Each project exposes exactly one registration extension:

```csharp
services.AddAirsideCore();
services.AddAirsideData(configuration);
services.AddAirsideRuntime(configuration);
```

**No assembly scanning, no convention-based auto-registration.** Explicit
registration is greppable, works under trimming, and means a contributor can find
the implementation of an interface by reading one file instead of inferring a
naming convention.

Lifetimes: `Singleton` for stateless clients and caches; `Scoped` for anything
touching `DbContext`; `Transient` rarely, and never for something holding a
connection. Background job handlers resolve a fresh scope per job — a job that
outlives an HTTP request must not share its `DbContext`.

Configuration binds to `IOptions<T>` with validation at startup
(`ValidateOnStart`). A missing or malformed setting fails the process at boot,
not at first use in production.

---

## 6. Entities and persistence

### Base type

```csharp
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

### Key strategy: UUIDv7

Sequential, so B-tree index locality is not destroyed the way random v4 destroys
it. Globally unique, so IDs survive a future multi-host merge. Safe to expose —
no enumeration, unlike sequential integers. Generated in application code, so an
entity has its identity before it is saved and a job can reference it immediately.

Every entity also has a **`Slug`**, unique per host, which is what appears in URLs
and container names. IDs are for references; slugs are for humans and Docker.

### Rules

- `DateTimeOffset`, always UTC, always via an injected `TimeProvider`.
  `DateTime.Now` and `DateTime.UtcNow` are banned — they make time untestable and
  the job/backup/certificate-expiry code is full of time.
- One `IEntityTypeConfiguration<T>` per entity, in `Airside.Data/Configurations/`.
  No mapping attributes on entities; `Core` must not reference EF Core.
- Migrations are checked in and never edited after merge.
- **Expand, then contract. Additive changes only within a release.** A migration
  that drops or renames a column makes rollback impossible without restoring the
  pre-update dump — because the updater puts the previous image back against the
  new schema, and old code meets a column that is gone. Losing every write since
  the update began is not an acceptable rollback. Destructive changes split
  across two releases: N+1 stops using the column, N+2 drops it. Where this
  genuinely cannot be done, the migration is flagged so the updater knows a
  database restore is required rather than an image swap.
- **No `decimal` in any entity.** SQLite stores `decimal` as TEXT and cannot
  compare or aggregate it server-side, so a `WHERE allocated > x` misbehaves
  silently on one provider and not the other. Memory and storage are `long`
  bytes; CPU is `long` nano-CPUs — the units Docker's `HostConfig` takes anyway.
- Optimistic concurrency uses an application-managed `RowVersion` `Guid`. Not
  Postgres `xmin`, which does not exist on SQLite.
- `AsNoTracking()` on every read-only query.
- No lazy loading. Explicit `Include`.
- Soft delete (`DeletedAt`) for workloads, because the state machines include
  `Deleted` and audit references must not dangle. Filtered by a global query
  filter.
- Audit entities have no update or delete path anywhere in the codebase, plus a
  database-level grant restriction.

---

## 7. Logging

Serilog, structured, JSON to stdout (the container captures it).

```csharp
// Yes — structured properties
logger.LogInformation("Provisioned database {Slug} on {Host} in {ElapsedMs}ms",
                      slug, hostName, elapsed.TotalMilliseconds);

// No — interpolated, unqueryable
logger.LogInformation($"Provisioned database {slug}");
```

Message templates are constant strings. Interpolated log messages are a build
error via analyzer.

**Levels:** `Debug` for flow detail, off in production. `Information` for state
changes worth an operator's attention. `Warning` for a recoverable problem —
retry, drift, approaching a threshold. `Error` for a failed operation.
`Critical` for the process being unable to continue.

Every request and every job carries a correlation ID that appears in every log
line for that operation, and in the `500` response body.

### Secrets never reach a log

Enforced structurally, in three layers:

1. A `Secret` wrapper type in `Airside.Core` whose `ToString()` returns `"***"`,
   so accidental interpolation is harmless. The value comes out only via an
   explicit `Reveal()` call.
2. A Serilog destructuring policy that redacts any property named `password`,
   `secret`, `token`, `key`, `connectionString`, `requirepass`, or `apiKey`, at
   any depth.
3. Environment variables are logged as keys only, never values, for entries
   flagged secret.

Docker exec argv is logged with the argument vector redacted whenever a
credential is passed — and credentials are passed by environment (`PGPASSWORD`,
`MYSQL_PWD`), never as an argument, since argv is visible in the container's
process list.

---

## 8. Async and cancellation

- Every I/O method is async. No `.Result`, no `.Wait()`, no
  `.GetAwaiter().GetResult()`.
- `CancellationToken` is the last parameter, named `ct`, on every async method,
  and it is passed through. A token that is accepted and ignored is worse than no
  token.
- `ConfigureAwait` is not used — there is no synchronization context in ASP.NET
  Core, and adding it everywhere is noise.
- Methods returning `Task` are suffixed `Async`.
- Long-running work uses the job system. A background operation started with a
  fire-and-forget `Task.Run` from a request is a bug.

---

## 9. Container runtime rules

These are security requirements, not style. They are why `Airside.Runtime` exists
as a separate reviewable project.

- **No shell.** Docker exec takes `string[] argv`. Nothing constructs a command
  line. Nothing passes `sh -c`, `bash -c`, or `/bin/sh`. There is no
  string-formatting helper for commands, because the way to make a rule hold is
  to remove the tool that breaks it.
- **No host command execution.** If a future operation genuinely requires it, it
  goes through a fixed allowlist of parameterised commands with no interpolation
  of user data, and it is reviewed as a security change.
- **Volume paths are allowlisted.** Bind mounts are permitted only under the
  managed volume root. Arbitrary host paths from user input are rejected — a
  bind mount of `/` into a user container is a full host compromise.
- **Every managed object is labelled**, without exception, or reconciliation
  cannot see it:

  ```
  airside.managed      = true
  airside.workload-id  = <uuid>
  airside.kind         = database | application | system
  airside.slug         = <slug>
  airside.engine       = postgres | mysql | mongodb | redis   (databases)
  airside.deployment-id= <uuid>                                (app containers)
  airside.system       = true                                  (the three system containers)
  ```

  Label keys live as constants in one file, shared with the CLI as linked source.
  No string literals at call sites.

- **Naming is derived, never user-supplied:**

  ```
  container  airside-db-<slug>  |  airside-app-<slug>-<deployment-short-id>
  volume     airside-vol-<slug>-<purpose>
  network    airside-net-db-<slug>  |  airside-net-app-<slug>
  ```

- **Security options on every created container:** `no-new-privileges`, dropped
  capabilities, read-only rootfs where the workload allows it.

  We cannot force a user's image to run non-root — the image's `USER` decides,
  and overriding it breaks images that legitimately need to write to paths owned
  by root. The platform **detects** a root-running container and surfaces a
  warning rather than silently failing to deliver a guarantee.

- **Docker stats CPU requires two samples.** A single non-streaming stats call has
  no previous CPU reading and yields 0%. The metrics reader keeps a previous
  sample per container and returns `null`, not `0`, until it has two.

---

## 10. Testing

xUnit. `Airside.Tests` holds both suites, separated by trait.

- **Unit tests** — no Docker, no database. `IContainerRuntime` and
  `IDatabaseEngine` are faked. These cover the allocation arithmetic, state
  machines, validation, slug rules, capability dispatch, and job compensation
  logic. They must run in seconds.
- **Integration tests** — `[Trait("Category", "Integration")]`, require a real
  Docker daemon, use Testcontainers. These cover the Docker runtime, exec stream
  demuxing, the Caddy client, backup and restore round-trips, and migrations.

Naming: `MethodName_Scenario_ExpectedOutcome`.

Non-negotiable coverage, because these are the failure modes that produce silent
data loss or a security hole:

- Allocation admission at and across the reserve boundary.
- Job compensation: a handler failing at each step leaves nothing orphaned.
- Job idempotency: re-running a provision for the same workload ID creates one
  container.
- Exec stream demuxing with interleaved stderr.
- Redis restore: stop, replace, start.
- Redis command allowlist, including case and whitespace variants.
- Secret masking in every API response shape.
- System containers rejected for delete, stop, and resize — including as Super
  Admin.
- Deleting a database without the volume opt-in leaves the volume.

**Never claim a test passes without running it.** A pull request describing
behaviour that was not executed is worse than one that says the behaviour is
untested.

---

## 11. Commits and pull requests

- Conventional commits: `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`.
- A pull request states what was built, what was run, and what was not verified.
- CI runs build, `dotnet format --verify-no-changes`, unit tests, the project
  dependency checks from §1, and integration tests.
- Any change to the container runtime, the query console allowlist, secret
  handling, or the Caddy client is labelled `security` and gets a second review.
