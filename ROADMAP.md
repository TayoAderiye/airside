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

## Phase 5 — Networking and TLS

**`Airside.Data`** — `Domain`; migration `0005_Networking` ×2.

**`Airside.Runtime`** — `CaddyProxyManager` against the admin API on
`airside-proxy:2019`; certificate polling; per-workload network creation and the
proxy's attach/detach to each application network.

**`Airside.Api`** — domain endpoints; certificate expiry notifications.

**Tests** — an application cannot reach an unattached database (integration, and
the single most important test in the suite); route upsert is idempotent; the
proxy admin API is unreachable from a workload network.

---

## Phase 6 — Operations

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

---

## Cross-cutting, held to in every phase

- Both migrations generated in the same pull request; CI fails if either lags.
- Every new endpoint has an authorisation policy and, where privileged, an audit
  record.
- Every job handler has a compensation path and an idempotency key.
- No shell strings, no host-path bind mounts, no secrets in logs.
- Nothing is reported as working that has not been built and run.
