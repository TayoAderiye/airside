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

`RegistryCredential` landed after Phase 6 — see **Private registries** below.

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

## Notification dispatch — **done**

Webhook, Slack, and email, each with its own severity threshold. Delivery is
tracked per notification per channel so one failing channel does not re-send to
one that succeeded, and dispatch runs apart from raising so a slow receiver
cannot hold up the sweep that noticed the problem.

Mostly an SSRF problem, and treated as one. See `docs/notifications.md`.

**Verified against real receivers** — a webhook whose HMAC signature was
recomputed independently in Python, SMTP through Mailpit, and a fan-out over six
channels showing per-channel isolation, severity thresholds, retry on 500, and
permanent failure on a refused address.

### Found by running it

- The escape hatch for private receivers was one switch for everything, so
  enabling "I have an internal receiver" also re-opened `169.254.169.254` and
  loopback. Neither is ever a legitimate webhook target; neither is behind the
  switch now.
- A refused destination was classified as a temporary network failure, because
  `HttpClient` wraps whatever `ConnectCallback` throws. It retried five times and
  muted the channel — a configuration mistake reported as an outage.
- Unrelated, exposed by the same run: the first state-file write in the update
  path sat outside its try block, so a read-only or full disk escaped as an
  unhandled 500 with the update record left `Pending`.

### Not built

- ~~No per-notification routing rules beyond severity.~~ Added: include and
  exclude lists over codes, resource kinds, and specific resources, with a
  preview endpoint and a warning when no channel would receive an error. Time-of-day
  routing followed: IANA-zoned windows that may wrap midnight, with `Defer` or
  `Suppress` outside them and an optional severity that ignores the schedule.
- SMTP is not covered by the outbound address rules. An internal relay on a
  private address is the normal arrangement, and an SMTP client cannot be turned
  into a useful request against the metadata service or the proxy's admin API.
  The reasoning is recorded rather than left implicit.

---

## Private registries — **done**

Deferred from Phase 4 and built last, since nothing before it needed a private
image.

Credentials are keyed by registry host rather than attached to a workload: one
token for `ghcr.io` covers every image on it, and per-application copies are how
one ends up stale and rotated separately. Stored encrypted, masked in every
response, revealed only through an audited endpoint.

They reach every pull — application deployments, database images (a Postgres
carrying pgvector usually lives somewhere private), the base image of a
Dockerfile build, and Airside's own image, since an organisation mirroring it
internally is normal and the control plane should not be the one thing that
cannot update from where it is published.

**Verified against a real private registry** — `registry:2` with htpasswd auth,
holding an image the daemon genuinely could not pull anonymously. Deploying
failed without a credential, succeeded with one, and the token appeared zero
times in the API log, in job steps, and anywhere in the database as plaintext.

### Found by running it

A pull refused for want of a credential reported **"The container runtime is
unreachable. Check that Docker is running."** Docker was fine. The dispatcher
mapped every `ContainerRuntimeException` to that message, so the one failure most
likely to be an authorisation problem sent the operator to check the daemon. It
now names the registry, says whether a credential was even tried, and — because
registries answer a private image and a typo identically — says both are
possible rather than picking one.

### Known limit

A multi-stage build pulling private base images from **two different** registries
gets a credential for the first only. Threading a list through the whole call
chain to serve that shape is not yet worth the surface; the single-registry case
is the one people have.

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
- ~~Notifications are raised and deduplicated but not delivered anywhere.~~
  Delivered as of the notifications work below — webhook, Slack, and email.

---

## Dashboard

Built as a separate Next.js app and shipped as a second image, `airside-ui`,
served on the dashboard hostname beside the API. Chosen over bundling a static
export into the API image so a UI fix can ship without a platform release.

Found by running it rather than by reasoning about it:

- The frontend had never been committed anywhere — it lived untracked inside an
  unrelated parent repository, one disk failure from gone.
- It rendered `@vercel/analytics` whenever `NODE_ENV` was production, so every
  self-hosted install would have beaconed to a third party from the console of
  somebody else's server.
- With the API unreachable the shell sat on "Loading session…" forever: `loading`
  went false, `user` stayed null, and no redirect fired. Separate containers make
  that state ordinary rather than impossible. Detecting it needs more than the
  gateway statuses — Next's proxy answers a dead upstream with a plain-text 500,
  and only the absence of Airside's `code`/`type` distinguishes it from a real
  fault.
- The rollback command had never worked. It interpolated a digest into the tag
  half of the reference, producing `repository:sha256:…`, which Docker rejects
  because a tag may not contain a colon.
- Both health checks called `wget`, which the chiselled runtime image does not
  contain — so the compose healthcheck could only fail, and the installer's
  readiness loop would have failed every install on a healthy host.

Not done: proxy reconciliation still does not restore the dashboard's own route,
because that hostname lives in `InstanceSettings` rather than in `Domains`.

---

## First install on a real host

An EC2 box, Ubuntu 24.04, 2 GB, x86-64. Everything below was found by running
it, and none of it was visible from macOS with Docker Desktop.

- **The installer never fetched the compose files it then ran.** It created
  `/opt/airside`, wrote `.env`, and ran `docker compose pull` in a directory
  with no compose file. Every install would have stopped there.
- **Both health checks called `wget`,** which the chiselled runtime image does
  not contain — so the compose healthcheck could only fail, and the installer's
  readiness loop ended every install with "the API did not become healthy" on a
  host where the API was fine.
- **The control plane did not own its own data directory.** Created by root, and
  the API runs as a non-root user. Nothing noticed until the first login, because
  Data Protection does not touch the key ring until the first thing is encrypted
  — which is the session cookie. Install, migrate, seed, accept the setup token,
  create the administrator, then `internal.unhandled` on a file permission.
- **A fresh install served nothing.** Routes are only created when a domain is
  bound, a new box has no dashboard domain, so Caddy listened on 80 and 443 and
  matched nothing. The address the installer prints returned a blank page.

Two of those had the same shape: the install *reported success* and the failure
surfaced later, somewhere that did not name the cause. That is worth more
attention than the individual bugs.

Still open from that run, none of them blocking:

- `Cannot load library libgssapi_krb5.so.2` at startup — Npgsql probing for
  Kerberos, which the chiselled image does not ship. Harmless, and printed as a
  bare `Error:` in the first lines an operator ever sees.
- EF logs the first `__EFMigrationsHistory` probe at `Error` on a first run,
  where the table is expected not to exist.
- Six `PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning`
  model warnings — a global query filter on the principal end of a required
  relationship can silently drop rows.

---

## Cross-cutting, held to in every phase

- Both migrations generated in the same pull request; CI fails if either lags.
- Every new endpoint has an authorisation policy and, where privileged, an audit
  record.
- Every job handler has a compensation path and an idempotency key.
- No shell strings, no host-path bind mounts, no secrets in logs.
- Nothing is reported as working that has not been built and run.
