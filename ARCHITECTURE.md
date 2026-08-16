# Airside — Architecture

Status: **layout and conventions approved.** The entity model, core interfaces,
and API contract are not yet designed. No implementation code exists yet.

Airside is a self-hosted control plane for a single Linux server. It provisions
databases, deploys applications, attaches domains with TLS, and manages access —
all as containers on one host, with the architecture kept honest about a future
where more hosts are added behind agents.

---

## 1. Solution layout

Eight projects. Every one is justified below; if a justification stops being
true, the project should be merged away.

```
Airside.sln
├── src/
│   ├── Airside.Core/                     no infra dependencies at all
│   ├── Airside.Data/                     → Core
│   ├── Airside.Data.Migrations.Postgres/ → Data   (generated code only)
│   ├── Airside.Data.Migrations.Sqlite/   → Data   (generated code only)
│   ├── Airside.Runtime/                  → Core
│   ├── Airside.Api/                      → Core, Data, both Migrations, Runtime
│   └── Airside.Cli/                      → (nothing)
└── tests/
    └── Airside.Tests/                    → all of the above
```

Dependency direction is strictly downward. `Core` references nothing outside the
BCL. Nothing references `Api`. Nothing references `Cli`.

### `Airside.Core`

Entities, enums, value objects, error types, and every interface the rest of the
system talks to: `IContainerRuntime`, `IDatabaseEngine`, `IProxyManager`,
`IJobQueue`, `ISecretProtector`, `IHostResourceReader`, `IAllocationPolicy`.
Also the pure domain services — allocation arithmetic, slug validation,
container/volume/network naming, the engine capability registry.

**Why it exists:** it is the seam that makes Docker an implementation detail. If
`Docker.DotNet` types leak into `Core`, the Podman/remote-agent story is dead and
the tests need a real daemon. The rule is mechanical and enforceable: `Core.csproj`
has no `PackageReference` to `Docker.DotNet`, `Npgsql`, `Microsoft.EntityFrameworkCore`,
or anything `Microsoft.AspNetCore.*`. A CI check asserts this.

### `Airside.Data`

`AirsideDbContext`, EF Core entity configurations, and query/repository
implementations of the `Core` persistence interfaces. The model lives here; the
migrations do not (see below).

**Why it exists:** migrations are a build artifact with their own tooling
(`dotnet ef`) and their own churn. Keeping them out of `Api` means the API project
does not carry the EF design-time packages, and `Core` never sees a `DbContext`.

### `Airside.Data.Migrations.Postgres` and `Airside.Data.Migrations.Sqlite`

Generated migration code and nothing else. No hand-written types.

**Why they exist:** Airside supports both stores, chosen at install time
(§4a). EF Core migrations emit provider-specific DDL, and an assembly can hold
exactly one `DbContextModelSnapshot` — so two providers cannot share one
migrations assembly. This is an EF Core constraint, not a design preference.
Splitting them is the documented approach and the only one that keeps
`dotnet ef migrations add` working normally for contributors.

The trade is real and permanent: **every schema change is generated and tested
twice.** A pull request touching an entity that lands one migration and not the
other breaks the other store's install, so CI generates both and fails on a
pending model difference in either.

### `Airside.Runtime`

Everything that touches the outside world: `DockerContainerRuntime`,
`CaddyProxyManager`, `HostResourceReader`, the four `IDatabaseEngine`
implementations, backup/restore executors, the reconciler, and the job handlers
that drive them.

**Why it exists:** this is the only assembly allowed to reference `Docker.DotNet`
and to open sockets to Caddy or a database engine. Concentrating that makes the
security review — "never build shell strings from user input" — a review of one
project rather than of the whole tree.

**Why not split it further** (`Airside.Docker`, `Airside.Engines`, `Airside.Proxy`):
they would share the same dependency set, change together, and be referenced
together. Folders give the same navigability at zero assembly cost.

### `Airside.Api`

Minimal-API endpoint groups, authentication and authorisation policies, live
server-sent-event endpoints, the job dispatcher hosted service, the reconciliation hosted service,
and the composition root. Produces the `airside-api` container image.

### `Airside.Cli`

The `airside` binary. Published **NativeAOT, self-contained, single-file** so the
installer drops one file on the host with no .NET runtime dependency.

**Why it exists and why it references nothing:** the CLI's entire purpose is to
work on the day the API is broken. If it called the API, it would be useless
exactly when it is needed. So it talks to the Docker socket directly and reads a
small on-disk state file at `/var/lib/airside/state.json`. It cannot reference
`Core` (which will pull EF-shaped types) or `Data` without dragging AOT-hostile
dependencies into a binary that must stay small and trim-clean. The handful of
label and path constants it needs are shared via a **linked source file**
(`<Compile Include="../Airside.Core/Naming/AirsideLabels.cs" Link="..." />`), not
a project reference — one definition, no assembly coupling.

### `Airside.Tests`

One test project. Unit tests run against fakes; integration tests are marked with
a trait and require a real Docker daemon (Testcontainers). Split only if the
integration suite's runtime forces it.

### Deliberately absent: `Airside.Updater`

The updater container is **the CLI image running `airside update --execute`**.
Same code path as the escape hatch, so the escape hatch is exercised by every
UI-driven update rather than rotting until the emergency. See §7.

---

## 2. Runtime topology

```
EC2 host
│
├── /var/run/docker.sock ────────┐ (bind-mounted into airside-api only)
├── /var/lib/airside/keys        │  Data Protection key ring (bind mount)
├── /var/lib/airside/state.json  │  updater/rollback state, readable by CLI
├── /proc, /sys (ro bind)        │  host metrics
│                                │
├── /var/lib/airside/data        │  SQLite store, when --store=sqlite
│                                │
├── airside-api ─────────────────┘   networks: airside-internal, airside-net-app-*
├── airside-db (control-plane store) networks: airside-internal
│                                    — Postgres only; absent under --store=sqlite
├── airside-proxy (Caddy)            networks: airside-internal + every app network
│                                    published: 80, 443
├── airside-net-db-<slug>    one per database   ← only attached apps join
├── airside-net-app-<slug>   one per application ← proxy joins for routing
├── database containers      airside-db-<slug>
├── application containers   airside-app-<slug>-<deployment>
└── named volumes            airside-vol-<slug>-<purpose>
```

**Network isolation is pairwise, not zone-based.** There is no shared "edge"
network that every app sits on. Caddy attaches to each application's own network,
and an application joins a database's network only on attach. Two applications
cannot reach each other; an application can reach only the databases explicitly
attached to it. Attaching and detaching a running container to a network is a
live Docker operation — no restart required.

> **Installer must configure Docker's address pool.** Docker's default
> `default-address-pools` allocates a /16 per bridge network from
> `172.17.0.0/16`–`172.31.0.0/16` — roughly **15 networks total**. A
> per-workload-network design hits that wall at ~15 workloads with an opaque
> "could not find an available, non-overlapping IPv4 address pool" error. The
> installer writes `/etc/docker/daemon.json` with
> `{"default-address-pools":[{"base":"172.16.0.0/12","size":24}]}` (4096 networks)
> and restarts the daemon before Airside starts. This is a first-commit installer
> requirement, not a scaling concern for later.

---

## 3. Control-plane store — two providers, chosen at install

The installer takes `--store=postgres|sqlite`. **Postgres is the default.**
SQLite exists because the install path is the product: it removes a container
from the critical boot path, removes a credential to manage, and turns
`airside backup --system` into a file copy. An admin running a handful of
workloads on a small box should not have to operate a database to operate a
control plane.

The choice is made once, at install, and recorded in
`/var/lib/airside/state.json` so the CLI knows how to back the system up without
talking to the API. **There is no migration path between the two providers in the
MVP**, and the installer says so before it proceeds — offering a switch that
silently loses data is worse than not offering one.

Supporting both is not free, and the constraints below are permanent. They exist
because a rule broken on one provider is not caught by tests on the other:

- **No provider-specific SQL.** No raw SQL with Postgres syntax, no `jsonb`
  columns. JSON goes through EF Core's owned-entity `ToJson()` mapping, which
  both providers support.
- **No `xmin` concurrency tokens.** Optimistic concurrency uses an
  application-managed `RowVersion` `Guid`, which behaves identically on both.
- **No `decimal` in entities.** SQLite stores `decimal` as TEXT and cannot
  compare or aggregate it server-side, so a `WHERE allocated > x` silently
  misbehaves. Every resource figure is a `long` — bytes for memory and storage,
  nano-CPUs for CPU — which is also what Docker's `HostConfig` takes, so no
  conversion is lost.
- **No `SELECT … FOR UPDATE`.** SQLite has no row locks. The allocation gate is
  an in-process semaphore (§5), which is correct for a single instance on both
  providers. The multi-host future needs a real row lock and therefore needs
  Postgres — that is a documented prerequisite of multi-host, not a surprise.
- **Append-only audit is enforced differently per provider** — `REVOKE UPDATE,
  DELETE` on Postgres, a `BEFORE UPDATE … RAISE(FAIL)` trigger on SQLite. Both
  live in provider-specific migrations, which is exactly where provider-specific
  DDL belongs.
- **SQLite runs in WAL mode**, with the file at `/var/lib/airside/data/airside.db`
  on a bind mount. It must not be on NFS or any network filesystem; the installer
  checks and refuses.

CI generates and applies migrations for both providers and fails on a pending
model difference in either. A schema change is not merged until it works twice.

---

## 4. Core abstractions

### `IContainerRuntime`

One interface for containers, images, volumes, networks, exec, and stats. Rules:

- No `Docker.DotNet` type appears in any signature. Parameters and returns are
  Airside record types.
- Every method is `async` and takes a `CancellationToken` — a remote-agent
  implementation is a network call, and the interface must already assume that.
- All identifiers passed to it are already-validated slugs or Docker IDs, never
  raw user input.
- Exec takes `string[] argv`, never a command line. Nothing routes through
  `sh -c`. This is what makes "never build shell commands from user input"
  structurally true rather than a review checklist item.

> **Exec stream demuxing.** Docker's exec attach stream without a TTY is
> multiplexed: 8-byte frame headers tagging each chunk as stdout or stderr. A
> naive read concatenates stderr bytes into the payload, which silently corrupts
> every `pg_dump`/`mysqldump`/`mongodump` backup taken while the engine emits a
> warning. The runtime demuxes properly and routes stderr to the job step log.
> This has a dedicated test.

### `IDatabaseEngine` — capability-driven, not enum-branched

```
Kind, DefaultPort, DefaultImage, SupportedVersions
SupportsDatabaseName        Postgres/MySQL/Mongo true, Redis false
SupportsUserAccounts        Redis false in MVP (requirepass), ACL later
SupportsLogicalBackup       Postgres/MySQL/Mongo true, Redis false
SupportsSnapshotBackup      Redis true
RequiresStopForRestore      Redis true, others false
QueryDialect                Sql | MongoShell | RedisCommand
EnvKeyPrefixDefault         DATABASE | DATABASE | MONGO | REDIS
```

Nothing outside the engine implementations switches on the engine kind. The
provisioning form, the backup scheduler, the restore flow, and the query console
all read capabilities. Redis is not a special case in the calling code — it just
answers the questions differently.

### Redis specifics

- No database name field. 16 numbered logical DBs, selected at connect time.
- `requirepass` against the `default` user for MVP. The entity model carries a
  `DatabaseUser` collection so Redis 6+ ACL users drop in without a migration to
  the shape.
- `maxmemory` is a **separate axis** from the container memory limit and is a
  required field. Default: **70%** of the container limit, not 80%.

  > **Correction to the brief's 80%.** `maxmemory` bounds the dataset only. During
  > `BGSAVE` or AOF rewrite, Redis forks; copy-on-write means the fork's resident
  > set grows toward the parent's size as writes land. At 80% with backups
  > enabled, a write-heavy instance gets OOM-killed by the cgroup mid-backup —
  > and the container restarts, so it looks like a mystery crash rather than a
  > backup problem. 70% for backup-enabled instances, 80% only when both RDB
  > snapshots and AOF are off (a pure cache). The API returns the computed
  > default and the reason; the admin can override with a warning.

- `maxmemory-policy` required. `noeviction` on a full instance means writes fail —
  surfaced as a warning in the API response the UI renders, per the UI contract.
- Backups are snapshots: `BGSAVE`, poll `INFO persistence` until
  `rdb_bgsave_in_progress:0`, copy `dump.rdb` out of the volume. Backups can be
  disabled entirely for cache-role instances.
- Restore stops the container, replaces `dump.rdb`, starts it.
- Query console is a **command console** with an allowlist (§7).

---

## 5. Resource allocation

Three numbers per resource, never conflated:

| | Source |
|---|---|
| **Capacity** | host `/proc/meminfo`, `/proc/cpuinfo`, `statvfs` on the volume root, read at startup and on a timer |
| **Allocated** | `SUM` of configured limits across managed workloads, from the control-plane store |
| **Used** | Docker stats API + `/proc`, sampled |

Provisioning or resizing is rejected when
`allocated + requested > capacity − reserve`. Reserve defaults: 1 GB RAM, 1 CPU,
10 GB disk, configurable. The rejection is a `ProblemDetails` with
`code = "resource.insufficient_memory"` and metadata carrying `requested`,
`available`, `capacity`, `allocated`, `reserved` — the UI renders the numbers, it
does not parse the message.

No overcommit. `IAllocationPolicy` is an interface with `StrictNoOvercommitPolicy`
as the only implementation, so a ratio-based policy is a registration change.

**Concurrency:** two simultaneous provision requests can both pass a naive check.
Single-instance means an in-process `SemaphoreSlim` around check-and-reserve is
sufficient and is what the MVP does — but the reservation is written to the store
inside the same transaction as the workload row, so the multi-host future
replaces the semaphore with a `SELECT … FOR UPDATE` on the host row without
restructuring anything.

**Enforcement:**

- Memory → `HostConfig.Memory`. Enforced.
- CPU → `HostConfig.NanoCPUs`. Enforced.
- Storage → **accounted, not enforced.**

  > **Correction to the brief.** Docker named volumes on the `local` driver over
  > ext4 or xfs-without-`pquota` — which is every default EC2 Ubuntu and Amazon
  > Linux image — support **no** per-volume size limit. `docker volume create
  > --opt o=size=` works only for tmpfs or a device-mapper/btrfs/zfs backend.
  > There is no flag that makes this work on a stock box.
  >
  > So for the MVP, "storage allocation" is an accounting and alerting number: it
  > counts against capacity for admission control, it is sampled per volume, and
  > it raises a notification at 80% and 95% of the allocation. It does not stop a
  > runaway database from filling the disk. The API exposes
  > `storageEnforcement: "accounting"` per host so the UI can say so plainly
  > rather than implying a guarantee.
  >
  > Real enforcement paths, both deferrable: XFS project quotas when the volume
  > root is on xfs with `pquota` (detected at startup, upgrades the field to
  > `"quota"`), or a loopback ext4 image per volume (works everywhere, costs an
  > I/O layer and a lot of complexity). I would not build either before there is
  > a user asking.

---

## 6. Jobs

No long-running operation blocks an HTTP request. Provision, build, deploy,
backup, restore, resize, and delete all enqueue and return `202` with a job ID.

**Job records live in the control-plane store, not only in memory.**

> **Correction to `BackgroundService` + `Channel<T>` alone.** An in-memory channel
> loses its queue when the API restarts — and the self-update flow restarts the
> API *by design*. A provision job in flight during an update vanishes silently,
> leaving an orphaned container, volume, and network that reconciliation then
> reports as drift with no explanation. The channel is the right dispatcher; it
> is not the right store.

The design: jobs are persisted rows; `Channel<T>` is the in-process wake-up
signal; on startup a recovery sweep finds jobs left in `Running` and either
resumes them (handlers are idempotent) or fails them with a compensating cleanup.
No Hangfire, no broker.

Job model: id, type, target workload, status, progress percent, append-only step
log, started/completed, error code and detail.

**Per-workload serialisation.** Two jobs must never touch the same container
concurrently — a resize during a backup corrupts both. Jobs take a per-workload
lease; a second job for the same workload queues behind the first rather than
failing.

**Compensation is part of the handler contract, not a convention.** `IJobHandler`
requires a `CompensateAsync`. Every resource a handler creates is registered on a
scope as it is created, and a failure — including at the health-check step —
unwinds it: container removed, volume removed unless it predates the job, network
removed, Caddy route withdrawn. Handlers are idempotent by workload ID: re-running
a provision finds the existing container by label and converges rather than
creating a second one.

Progress and step logs stream as server-sent events, as do container logs from
the Docker log follow API — with a bounded per-connection buffer so a chatty
container cannot exhaust server memory.

> **Not SignalR, though the brief specified it.** Every stream Airside has is
> server-to-client; the client's only input is which resource it wants, which is
> a URL. A hub buys bidirectional RPC and transport fallback that nothing here
> uses, and costs a client library plus a set of endpoints that cannot appear in
> the OpenAPI document. SSE also gives resumability for free: the browser returns
> `Last-Event-ID` on reconnect, so a client that drops mid-provision continues
> from the step it last saw. A hub reconnect restores the transport but not the
> missed application state — that had to be hand-written, and only worked on the
> initial connect. The one thing genuinely needing a socket is an interactive
> `exec` terminal, and that would be one WebSocket endpoint rather than a
> framework.

**Reconciliation** runs on a timer and on demand: list everything labelled
`airside.managed=true`, diff against the store, record drift. The MVP **reports**
drift and offers explicit per-item remediation. It does not auto-correct — an
auto-reconciler that deletes an unrecognised container is one bug away from
deleting a user's data.

---

## 7. Self-update

The API cannot replace itself, so it launches a short-lived detached updater
container (`AutoRemove`, Docker socket mounted) and returns `202` immediately.

Sequence: back up (control-plane database + Data Protection key ring) → pull and
verify target image → record current tag → swap → poll `/health` → restore
previous tag on timeout or failure. EF Core migrations run at API startup inside
the new container, before it serves traffic; a failed migration means the health
check never passes and the updater rolls back — which is why the backup is first
and mandatory. User workloads are untouched and keep running throughout.

Three corrections to the flow as written:

1. **The updater must run the *current* image, not the target.** Running the new
   image's updater means a broken target image breaks the very process meant to
   recover from it. The known-good binary drives the swap.

2. **Updater state must be on disk, not only in the updater's memory.** If the
   updater is killed — host reboot, OOM, someone's `docker system prune` — the
   API is down and nothing knows what the previous tag was. The updater writes
   each step to `/var/lib/airside/state.json` before performing it, so
   `airside rollback` from the host CLI can finish the job with no API and no
   updater. This is the whole reason the CLI exists.

3. **The system backup is provider-specific, and the dump never runs in the
   updater.** Under Postgres, `pg_dump` is exec'd *inside* `airside-db` and the
   output streamed out — shipping a client into the updater image would mean
   version-matching it to the server forever. Under SQLite it is a
   `VACUUM INTO` to a temp path plus a copy, which is atomic and safe against a
   live writer in a way that copying the file plus its WAL is not. Both are
   behind one `ISystemBackupProvider`; the updater and the CLI read the active
   provider from `state.json`.

Rules held: never `:latest` for the API — an explicit tag the updater rewrites.
The routine update path never changes the Postgres image version; a major upgrade
needs `pg_upgrade` or dump-and-restore and is a separate, explicitly triggered
operation.

CLI surface: `airside update [--version X.Y.Z]`, `airside rollback`,
`airside backup --system`, `airside restore --system <file>`. Boring names, no
aviation puns.

---

## 8. Applications, networking, secrets

**Deployment pipeline:** clone → build image tagged with commit SHA → create
container with limits → attach volumes → inject env → join network → register
Caddy route → provision TLS → health check → mark successful.

**Zero-downtime:** start the new container, poll its health check, update the
Caddy upstream, stop the old one. The previous image is retained, so rollback is
a proxy change plus a container start, not a rebuild. Every deployment records
commit SHA, branch, timings, status, and build log.

A health check is required configuration per application — HTTP path plus
expected status, or a command. Without one, "zero-downtime" means "we waited a
few seconds and hoped", which is worse than saying so.

**Sources:** Docker image, Git repo with a Dockerfile, raw Dockerfile. No
framework auto-detection, per the brief — if it is ever needed, integrate
Nixpacks rather than writing heuristics.

**Docker Compose is out of scope for the MVP** (decided). Compose is a
multi-container workload, and every single-container assumption in the system
breaks on it: one resource limit becomes N, one health check becomes N, one Caddy
upstream becomes a choice, rollback stops being a proxy flip, and `depends_on`
ordering lands in the job system. It is a phase of its own disguised as a bullet
in a list — the same scope-creep shape the brief already rejects for framework
auto-detection. The workload model stays strictly one-application-one-container,
and nothing is built to accommodate a future Compose that may never arrive.

**Caddy is driven over its admin API — but not at `localhost:2019`.**

> **Correction.** Caddy runs in `airside-proxy`; from the API container,
> `localhost` is the API. The address is `airside-proxy:2019` over the
> `airside-internal` network. More importantly, **Caddy's admin API is
> unauthenticated and can load arbitrary config, including directives that
> execute commands** — exposing it is equivalent to handing over the host. It is
> never published to the host, `admin.origins` is restricted, and only
> `airside-api` shares `airside-internal` with the proxy.

TLS via Let's Encrypt with automatic renewal; issuer, expiry, and auto-renew state
surfaced per domain, with a notification ahead of expiry.

> **First-run has no domain, so it has no public certificate.** Let's Encrypt does
> not issue for bare IPs. On a fresh box the dashboard is reachable at
> `https://<ip>` with Caddy's internal CA (browser warning) or over plain HTTP.
> Logging in over plain HTTP sends the admin password in clear text. The install
> path therefore: prints a **one-time setup token** on the console that the first
> request must present, binds the dashboard until a domain is set, and makes
> "attach a domain" the first prompt in the UI. This needs to be settled before
> the installer is written, because the installer is the product.

**Database ports default to `127.0.0.1`, not `0.0.0.0`.** The provisioning form's
port field publishes to loopback by default, reachable over an SSH tunnel.
Publishing a Postgres to the public internet is an explicit, separately confirmed
choice with a warning — defaulting to it would put unauthenticated-adjacent
databases on the internet within a week of launch.

**Env vars and secrets.** Per-application key/value, each flagged secret or not.
Secrets are encrypted at rest via ASP.NET Core Data Protection with the key ring
on a host bind mount, masked in every API response by default, revealed only via
an explicit elevated-permission call, and never logged — enforced by a `Secret`
wrapper type whose `ToString()` returns `***` plus a Serilog destructuring policy.
Every reveal and every change is audited.

> **Honest threat model.** Data Protection protects against exfiltration of the
> control-plane database — a stolen dump, a backup on S3, a SQL-injection read.
> It does **not** protect against host root, because the key ring is on the host
> and the process that decrypts it runs there. Anyone with the Docker socket
> already has root-equivalent reach. `SECURITY.md` says this in those words
> rather than implying secrets are safe from a compromised host.

**Database attachment** injects connection details automatically. Each attachment
carries an admin-editable **key prefix**, defaulted per engine and rejected on
collision, so two attached databases do not fight over `DATABASE_URL`:

| Engine | Default prefix | Injected keys |
|---|---|---|
| PostgreSQL | `DATABASE` | `_HOST _PORT _NAME _USER _PASSWORD _URL` |
| MySQL | `DATABASE` | `_HOST _PORT _NAME _USER _PASSWORD _URL` |
| MongoDB | `MONGO` | `_HOST _PORT _DATABASE _USER _PASSWORD _URI` |
| Redis | `REDIS` | `_HOST _PORT _PASSWORD _URL` — no `_NAME`, no `_USER` |

Attach joins the app to the database's network; detach removes it and the keys.

**Query console** is an optional module behind a permission independent of
infrastructure permission — restarting a database and reading its contents are
different rights. SQL engines get an editor, result grid, timing, rows affected,
history, save, and cancel. MongoDB gets shell-style input. Redis gets a command
console with an allowlist: `KEYS`, `FLUSHALL`, `FLUSHDB`, `CONFIG SET`, and
`SHUTDOWN` are blocked outright and gated behind a separate destructive-command
permission. Key browsing uses `SCAN`, never `KEYS *`.

---

## 9. Auth, RBAC, audit

Email and password, hashed with ASP.NET Core Identity's hasher, MFA-ready in the
schema from the start. Roles — Super Admin, Infrastructure Admin, Database Admin,
Application Admin, Developer, Read Only — are **bundles of permissions**;
authorisation checks a permission (`database.create`, `database.query`,
`secret.view`, `application.deploy`, `server.manage`, `user.manage`, …) via an
ASP.NET Core policy, never a role name.

The three system containers `airside-api`, `airside-db`, and `airside-proxy` carry
`airside.system=true`. They are visible but structurally undeletable,
unstoppable, and unresizable, rejected at the service layer — including for Super
Admin, so there is no permission that reaches them.

Audit records every privileged action with user, action, resource, timestamp, IP,
result, and metadata. Deletions, restores, credential rotation, secret access,
permission changes, deployments, and resizes are mandatory. Append-only is
enforced by having no update or delete path in code or API, plus a database-level
grant restriction on the audit table.

**Deleting a database does not delete its volume** unless the admin opts in via a
separate checkbox. Destructive operations require typed confirmation of the
resource name. Orphaned volumes remain counted against allocated storage and are
listed on a reclaim screen — otherwise a few delete cycles silently consume the
disk with nothing in the UI explaining why.

---

## 10. Multi-host readiness

A `Host` entity exists from the first migration with exactly one seeded row.
Workloads, networks, volumes, and resource accounting are all host-scoped. This
costs one foreign key now. Retrofitting it later means rewriting every allocation
query, every uniqueness constraint, and every route — which is the usual reason
"we'll add multi-host later" never happens.

Everything else about multi-host is deferred. `IContainerRuntime` being
async-and-serialisable is the only other accommodation.

---

## 11. Decisions taken

| Decision | Outcome |
|---|---|
| Control-plane store | **Both**, selected at install via `--store`. Postgres is the default. No migration path between them in the MVP (§3). |
| Licence | **Apache 2.0** — express patent grant and retaliation clause. Verbatim canonical text in `LICENSE`. |
| Docker Compose deployment source | **Out of scope for the MVP** (§8). |
| Repository | Standalone git repository at the project root, deliberately not part of any surrounding repo. |

### Still open

1. **First-run TLS and the setup token** (§8). The install path cannot obtain a
   public certificate before a domain exists, which means the first login is
   either over plain HTTP or behind a browser warning. The proposal is a one-time
   console token plus "attach a domain" as the first UI prompt. This needs a
   decision before the installer is written, and the installer is written early
   because it is the product.
2. **Storage enforcement posture** (§5). Confirm that accounting-plus-alerting is
   acceptable for the MVP and that the API advertising
   `storageEnforcement: "accounting"` is the right way to be honest about it.
