# Airside — Implementation plan

Status: **proposed, awaiting approval.**

Files created or modified per phase. Each phase ends with: solution builds,
tests pass, security requirements reviewed against `CONVENTIONS.md` §9, summary
written. Work stops at every phase boundary for review.

Estimates are omitted deliberately — the phases are ordered by dependency, and
the ordering is the useful part.

---

## Phase 0 — Foundation scaffold ✅ *complete (commit `91456b3`)*

Eight projects, build infrastructure, and the whole of `Airside.Core`. 31 tests
passing.

---

## Phase 1 — Auth, RBAC, host, runtime, jobs, audit ✅ *complete (commit `25db138`)*

The largest phase, because almost everything downstream depends on it. It ends
with **a working `curl | sh` install**, not with a library — the install path is
the product, and finding out in Phase 6 that it does not work is how this kind
of project dies.

**`Airside.Data`** — `AirsideDbContext : IdentityUserContext<AirsideUser, Guid>`;
entities `Host`, `AirsideUser`, `Role`, `Permission`, `RolePermission`,
`UserRole`, `UserSession`, `Job`, `JobStep`, `JobResource`, `AuditEvent`,
`InstanceSettings`; one `IEntityTypeConfiguration` each; `DbSeeder`;
`AddAirsideData(IServiceCollection, IConfiguration)` with provider selection.

**`Airside.Data.Migrations.{Postgres,Sqlite}`** — `0001_Foundation` in each,
generated separately. The Postgres one carries `REVOKE UPDATE, DELETE` on the
audit table; the SQLite one carries the equivalent `BEFORE UPDATE … RAISE(FAIL)`
trigger.

**`Airside.Runtime`** — `DockerContainerRuntime` and the four operation groups;
`ExecStreamDemultiplexer`; `HostResourceReader` (reads `/proc` and `/sys` from
the read-only host bind mounts, plus `statvfs` on the volume root, plus XFS quota
detection); `Reconciler`; `DataProtectionSecretProtector`; `SecretGenerator`;
`AddAirsideRuntime`.

**`Airside.Api`** — `Program.cs` composition root; Serilog with the secret
destructuring policy; cookie auth and permission policies; `PermissionRequirement`
+ handler; global `IExceptionHandler`; `ProblemDetails` mapping from `Error`;
FluentValidation endpoint filter; `JobDispatcherService` (`BackgroundService` +
`Channel<Guid>` + startup recovery sweep + per-workload lease);
`ReconciliationService`; the server-sent-event endpoints; endpoint groups for setup, auth, users, roles, host, system,
jobs, audit; OpenAPI generation.

**`deploy/`** — `install.sh`; `docker-compose.yml`; `Dockerfile` for the API;
`daemon.json` fragment writing `default-address-pools` (`172.16.0.0/12`, size 24)
**before** anything starts, or every deployment wedges at ~15 networks; Caddy
bootstrap config with the admin API bound to the internal network only.

**`.github/workflows/ci.yml`** — build, `dotnet format --verify-no-changes`,
unit tests, integration tests, the `Airside.Core` dependency guard, and
`migrations has-pending-model-changes` against **both** providers.

**Tests** — allocation admission at and across the reserve boundary; job
compensation with a failure injected at each step; job idempotency; exec stream
demuxing with interleaved stderr; permission policy evaluation; audit
append-only rejection; system-container protection including as Super Admin.

**Exit criteria:** `curl -fsSL … | sh` on a fresh Ubuntu VM produces a reachable
dashboard, a first-run setup token on the console, and a created Super Admin.

---

## Phase 2 — Database provisioning ✅ *complete*

**`Airside.Core`** — `DatabaseProvisionSpec` validators.

**`Airside.Data`** — `Workload` (TPH) with `DatabaseInstance` and `Application`
subtypes, `Volume`, `DatabaseCredential`; migration `0002_Databases` ×2.

**`Airside.Runtime`** — `PostgresEngine`, `MySqlEngine`, `MongoDbEngine`,
`RedisEngine`; `DatabaseEngineRegistry`; job handlers `database.provision`,
`database.start|stop|restart`, `database.resize`, `database.delete`.

**`Airside.Api`** — `DatabaseEndpoints`, `DatabaseEngineEndpoints`,
`VolumeEndpoints`; `StrictNoOvercommitPolicy` wired with the allocation gate.

**Tests** — engine capability dispatch; Redis rejects a database name and
requires maxmemory; the 70% recommendation with persistence on; provision
compensation leaves no container, volume, or network; delete without the volume
opt-in leaves the volume; ports bind to loopback by default.

---

## Phase 3 — Database operations ✅ *complete*

**`Airside.Data`** — `Backup`, `Restore`, `SavedQuery`, `QueryHistoryEntry`;
migration `0003_DatabaseOps` ×2.

**`Airside.Runtime`** — per-engine backup and restore (pg_dump/mysqldump/
mongodump via exec; Redis BGSAVE → poll `INFO persistence` → copy `dump.rdb`);
checksum and engine-version verification; retention pruner; `SqlQueryConsole`,
`MongoQueryConsole`, `RedisCommandConsole`; `RedisCommandPolicy` (a parser, not a
`StartsWith` denylist); credential rotation.

**`Airside.Api`** — backup, restore, credential, and query endpoints; live log
and metric streaming.

**Tests** — backup round-trip per engine (integration); truncated backup is
refused; version mismatch is refused before stopping anything; Redis restore
stop/replace/start; command policy against casing, leading whitespace, inline
comments, and `KEYS` in every form.

---

## Phase 4 — Application deployment ✅ *complete*

**`Airside.Data`** — `Deployment`, `DeploymentLog`, `EnvironmentVariable`,
`DatabaseAttachment`, `GitCredential`, `RegistryCredential`; migration
`0004_Applications` ×2.

**`Airside.Runtime`** — `GitCloner`; `ImageBuilder`; `DeploymentOrchestrator`
(build → create → health-check → swap → stop old); `EnvironmentRenderer` (merges
manual entries with attachment-injected ones at deploy time); job handlers
`application.deploy`, `application.rollback`, `application.attach_database`.

**`Airside.Api`** — application, deployment, environment, and attachment
endpoints.

**Tests** — health-check failure leaves no orphan and does not move the proxy
upstream; rollback with a pruned image fails cleanly; env prefix collision;
injected values reflect a rotated credential without an env write; Dockerfile
path traversal rejected.

---

## Phase 5 — Networking and TLS — **done**

**`Airside.Data`** — `Domain`; migration `0005_Networking` ×2.

**`Airside.Runtime`** — `CaddyProxyManager` against the admin API on
`airside-proxy:2019`; certificate polling; per-workload network creation and the
proxy's attach/detach to each application network.

**`Airside.Api`** — domain endpoints; certificate expiry notifications.

**Tests** — an application cannot reach an unattached database (integration, and
the single most important test in the suite); route upsert is idempotent; the
proxy admin API is unreachable from a workload network.

All three are in place. The first two run against a real daemon
(`tests/Airside.Tests/Integration/`) and skip without one, or fail if
`AIRSIDE_REQUIRE_DOCKER=1` — which CI sets, so a broken daemon cannot make the
isolation test quietly disappear. Both include a positive control: the isolation
test re-attaches the network mid-way and shows reach returning, because a "cannot
connect" result proves nothing on a rig that never connected.

### Found by running it

Four defects that unit tests and review had both missed, three of them in code
from earlier phases:

- **Any stock image failed to deploy.** Applications ran with `CapDrop=ALL` and
  nothing restored, so `nginx` died on `chown … Operation not permitted` — as
  would Apache, PHP-FPM, and most official web images. Phase 4 was verified with a
  hand-written Dockerfile running as root on a high port, the one shape that
  survives. Fixed with `ContainerSecurity.Application`, pinned by
  `StockImageStartupTests`, which includes a negative control that fails if the
  old profile ever starts working.
- **A job left `Compensating` blocked its workload for ever.** Recovery moved
  orphans to `Compensating`, but the claim query only looked for `Queued`, so they
  were never picked up — and a `Compensating` row marks its workload busy, so
  every later job for it waited behind a job that could never finish. Covered by
  `JobRecoveryTests`, confirmed to fail without the fix.
- **Unbinding a domain did not withdraw its route.** The endpoint soft-deletes the
  row before enqueuing the job, so the handler read back nothing, treated it as
  already gone, and reported success — leaving the hostname serving until
  reconciliation caught it up to two minutes later. The hostname now travels in
  the payload.
- **`deploy/caddy.json` stopped Caddy from starting.** Its `"//"` comment keys are
  rejected by Caddy's strict config parsing (`json: unknown field "//"`). The
  reasoning moved to `deploy/README.md`.

Also: `Secret` construction on the unauthenticated setup endpoint turned a
malformed body into a 500 rather than a validation error, and the setup-token box
had one row a character too wide.

---

## Domains and TLS — **done**

Built to a separate specification after Phase 5, in its stated build order.

**Pre-flight** (`Airside.Runtime/Domains`) — DNS through a public resolver rather
than the host's, CAA, the IPv6-preference trap, proxied-DNS detection, port
conflicts, hostname conflicts naming the holder, and ACME rate-limit accounting
against an embedded Public Suffix List. Every check reports what it found beside
what it expected.

**TLS modes** — `Automatic`, `Manual`, `External`, and `Internal` are built;
`AutomaticDns` and `OnDemand` are modelled and rejected at the service layer. The
mode is required with no default.

**Certificates** — upload validation catches a mismatched key, an incomplete or
misordered chain, an expired intermediate under a valid leaf, and a wildcard that
does not cover its own apex. Keys are encrypted into their own table. Expiry is
swept every six hours and warned at 30/14/7/3/1/0 days.

**Lifecycle** — redirects (308), HSTS with typed confirmation for preload, a
maintenance page for stopped applications, a grace period on detach, and the
dashboard domain guarded by DNS verification before the switch plus
`airside domain reset` as the way back.

### Found by running it

- Caddy's `skip` and `skip_certificates` are not interchangeable. `skip` removes
  the TLS listener entirely, so Manual mode loaded an uploaded certificate
  perfectly and served nothing on 443. One flat skip list became three.
- Reconciliation restored routes but not certificates or network attachments, so
  a replaced proxy came back told not to obtain a certificate, holding none, and
  with no path to any application. Every reasserted route returned 502.
- `deploy/caddy.json` comment keys stopped Caddy booting; a wildcard hostname was
  rejected as invalid syntax before reaching the wildcard-specific advice.

### Not built

- Moving a domain between applications is detach-then-attach rather than one
  atomic operation. The conflict check blocks the overlap, so it is safe, but it
  is two steps.
- No apex/www convenience: the redirect field exists and has to be set by hand.
- Deleting an application with domains attached is unguarded because
  `application.delete` does not exist yet. It must block or release the domains
  when that endpoint lands, or it will orphan routes.

---

## Phase 6 — Operations — **done**

**`Airside.Data`** — `MetricRollup`, `Notification`, `UpdateRecord`; migration
`0006_Operations` ×2.

**`Airside.Runtime`** — metric sampler and hourly roller; notification
dispatcher with dedupe; `SystemBackupProvider` (Postgres via exec `pg_dump`;
SQLite via `VACUUM INTO`); the updater running from the **current** image and
writing `state.json` before each step.

**`Airside.Cli`** — `update`, `rollback`, `backup --system`,
`restore --system`, `status`, published NativeAOT.

**`Airside.Api`** — metrics, notification, and system-update endpoints; MFA
enrolment.

**Tests** — update rollback on a failed health check; `state.json` lets the CLI
finish an update whose updater died; dedupe suppresses repeat expiry
notifications.

All three are in place, plus TOTP against RFC 6238's published vectors — a TOTP
implementation that is subtly wrong still produces six plausible digits, and the
only symptom is users unable to log in with a code their phone says is correct.

### Verified by running it

- **Metrics** against a live Redis container: two samples folded into one hourly
  row, average exactly between them, max the higher of the two. Stored as
  nanoseconds of CPU per second rather than a percentage, so a chart compares
  usage against the workload's own limit rather than against the host.
- **System backup** through the API: the archive contains the store dump, the
  manifest, and the real Data Protection key ring. A backup missing the key ring
  is reported as usable-but-undecryptable rather than silently restored.
- **MFA** end to end, confirming with a code computed independently in Python
  from the returned secret — a cross-implementation check rather than the
  implementation agreeing with itself.
- **The CLI** published NativeAOT and run on Linux against a real
  `state.json` left at `Swapping`: it produces the recovery steps, the migration
  caveat, and a rollback pinned to the previous **digest** rather than the tag.

### Deliberate limits

- `airside update` and `airside rollback` print the exact commands rather than
  performing the swap. Doing it properly needs the compose file, its environment,
  and the socket, and a CLI reimplementing all three would drift out of step with
  the compose file the installer wrote — silently, and only noticeably during an
  update. The API drives updates; the CLI exists for when the API cannot.
- The swap itself is prepared by the API and carried out by Docker Compose. The
  API cannot stop its own container mid-request and survive to report the result,
  so the outcome is reconciled from `state.json` at the next startup.
- Notifications are raised and deduplicated but not delivered anywhere outside
  the API. Email and webhook dispatch are not built.

---

## Cross-cutting, held to in every phase

- Both migrations generated in the same pull request; CI fails if either lags.
- Every new endpoint has an authorisation policy and, where privileged, an audit
  record.
- Every job handler has a compensation path and an idempotency key.
- No shell strings, no host-path bind mounts, no secrets in logs.
- Nothing is reported as working that has not been built and run.
