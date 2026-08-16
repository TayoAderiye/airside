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

### Then using it

Installing successfully is not the same as working. Provisioning a database and
deploying an application on that host found three more:

- **The allocation gate refused everything.** The host reserve was a fixed one
  core, one GiB and ten GiB, which does not scale down. On the 1 vCPU, 2 GB
  instance the README recommends as a starting point, the CPU reserve took the
  whole core and the gate answered *"Not enough cpu available, available 0"* on
  an idle machine. The storage reserve exceeded the entire filesystem. It is now
  a bounded share of capacity, and the memory share is set from a measurement —
  a fresh install uses 0.7 GiB with nothing deployed, so a quarter would have
  reserved less than the control plane itself occupies.
- **Storage accounting ignored everything Airside had not allocated.** The gate
  believed it had 4.7 GiB to hand out on a disk with 3.0 GiB free, because the
  operating system and the images were in neither the reserve nor the allocated
  total. Foreign usage now counts against the reserve, with Airside's own
  volumes subtracted back out so their bytes are not charged twice.
- **The database create form sent neither `databaseName` nor `username`.** Both
  are required by every engine that has the concept and rejected outright by
  those that do not, so the fix is per-engine rather than "send them always".

### Then attaching a domain

Five more, and together they made setting a dashboard domain a way to lock
yourself out of the machine.

- **Reconciliation deleted the dashboard's own route every two minutes.**
  `RemoveOrphansAsync` withdraws routes Airside created that match no domain in
  `Domains`. The dashboard's hostname is in `InstanceSettings`, so it matched
  nothing and looked exactly like a leftover from a deleted domain. The
  dashboard worked, then stopped, then worked again when the route was added by
  hand, then stopped again — on a timer.
- **The route was never reasserted** after the proxy container was replaced, for
  the same reason: the reconciliation loop walks `Domains` and never sees it.
- **`/var/lib/airside` was not mounted** — only four directories inside it. Two
  files live at its root: `state.json`, which the updater writes *specifically*
  so it survives the container being replaced, and `domain-reset`, the escape
  hatch for exactly this lockout. Neither was visible to the container, so the
  rollback state was written to a filesystem the update destroys, and touching
  the reset file on the host did nothing at all.
- **Inserting a route failed against an empty list.** Keeping the catch-all last
  meant inserting real routes at index 0, and Caddy answers an empty array with
  `array index out of bounds: 0` — the state a replaced proxy boots into. Self
  inflicted, and it defeated the fix above until it was caught.
- **The `airside` CLI is never installed on the host.** The documented recovery
  path did not exist.

Worth stating plainly: the first four were all found by one operator setting a
domain and watching it break intermittently. None of them would have shown up in
a test suite, because each depends on the passage of time or the replacement of
a container.

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

## Two-factor authentication — **done**

Asked for as "the MFA enrolment", meaning the screen. The screen was the smaller
half.

**Login never checked the code.** `LoginAsync` accepted a `totpCode` field and
ignored it; nothing in the auth path read `UserMfa` at all. Enrolment worked,
`GET /account/mfa` reported the factor as confirmed, and the password alone still
signed you in. Shipping only the screen would have handed an operator the belief
that their control plane had two factors on it — worse than knowing it has one,
because a password gets guarded when it is visibly the only thing standing there.
That is the whole reason this section is longer than "added a panel".

What the enforcement does:

- Only a **confirmed** enrolment gates login. An unconfirmed one may be a secret
  nobody successfully scanned, and enforcing it would lock someone out of the one
  account that could fix it.
- The accepted time step is **advanced on success**, so a code captured in transit
  cannot be replayed inside its own thirty-second window.
- **Recovery codes work in the same field** and are burned on use. The login form
  used to cap that input at eight characters, which silently truncated an
  eleven-character recovery code — on the exact path taken by someone whose phone
  is gone.
- A wrong code **feeds the lockout counter**. Six digits is a million guesses
  against an endpoint that has already accepted the password.
- An **undecryptable secret refuses the login** rather than falling back to the
  password, which would silently drop the second factor for whoever turned up
  after the key ring was replaced.

The decision lives in `MfaChallenge.Evaluate` rather than inline in the endpoint,
so it can be tested without an HTTP context. Fourteen tests, mostly about the
*second* attempt — a code offered twice, a recovery code spent twice — because
nothing in a single successful login distinguishes a working implementation from
the one that ignored the field entirely.

### The QR code, and why it is not a dependency

The payload is an `otpauth://` URI containing the shared secret. That rules out a
hosted chart API, and makes a transitive dependency on the page that renders it a
supply chain terminating at the second factor. So `frontend/lib/qr.ts` is a QR
encoder written for this: byte mode, error correction M, versions 1 to 10, and
nothing else — modes that will never be reached are absent rather than untested.

Verification is the part worth recording, because a wrong QR still renders as a
plausible square of noise:

- The first approach diffed the matrix against **segno**. Almost every payload
  differed, which turned out to be segno appending a spurious zero codeword
  whenever the data stream already ends on a codeword boundary — in byte mode,
  nearly always, since the header is a fixed twelve bits. Both symbols decode
  identically, but matching it would have meant reproducing the bug. ISO 18004
  §7.4.10 adds padding bits only when the stream does *not* already end at a
  boundary.
- The comparison did earn its keep first: it caught a genuine bug in the mask
  penalty scoring, where rule 3 was counting overlapping occurrences and demanding
  four in-bounds light modules at the symbol edge.
- The real check **decodes**. Every payload length from 1 to 213 bytes, plus real
  provisioning URIs from the server's own `BuildProvisioningUri`, rendered and
  read back — 217 symbols across all ten versions.
- Two decoders, passing if either reads it. Not a lowered bar: each intermittently
  fails to *locate* a valid symbol depending on render scale. OpenCV's base
  detector missed seven that the WeChat detector read exactly, and missed one the
  base detector read exactly. The seven looked like encoder bugs until the same
  matrix decoded at a different scale.

End to end, once: the server generated twenty secrets, the QR encoded each URI,
an independent decoder recovered the secret byte for byte, and codes computed the
way a phone computes them were accepted by the server's validator.

---

## Monitoring that monitors — **done**

Reported as three separate complaints, which turned out to be one decision.

`SystemWorkloadReader` synthesises ids for Airside's own four containers, and its
own comment explained why that was good: *"their ids exist nowhere in the
database, so every action endpoint looks them up, finds nothing, and returns 404
without a single guard having to be remembered."*

That is right for **stopping the API through the dashboard the API is serving**.
It was applied to reads as well, and reads are not control. The result was four
containers listed on three screens that could not be clicked, opened, or read.

Compounding it, applications had no live log stream at all — only databases did.
So Monitoring, whose entire purpose is watching what the host is doing, refused
for every application *and* every control-plane container. On a host with no
database provisioned, it refused for everything on the page and suggested ssh.

- **Applications now have `/logs/stream`**, sharing the database handler. One
  resolver looks a workload id up in `Workloads`, so soft-deleted rows stay
  excluded by the existing query filter.
- **System ids resolve to their container** through an allowlist of exactly four
  names, used by log streaming and nothing else. An id an attacker constructs
  matches none of them, and there is a test that says so.
- **The lifecycle endpoints are untouched.** They still look ids up in the
  database and still find nothing, so nothing destructive became reachable.
- The list screens link system rows to their log rather than rendering a row that
  looks clickable and is not.
- The Query console said "provision a database first" while the Databases screen
  showed one running. It now says the control-plane store exists, is deliberately
  not queryable, and why.

The general lesson, recorded because it will recur: *unreachable by construction*
is a fine security property and a poor UI default. It is worth stating which
operations it is protecting, because "no row matches this id" silently covers
reads that were never dangerous.

---

## Forms that could not succeed — **done**

Reported three times across two releases as "still can't create db" and
"couldn't deploy any application", and misfiled by me each time as a server
error. It was a 409, returned correctly, to a form that could not send anything
else.

The database form clamped its defaults to the host, with the floor applied after
the minimum:

```ts
setStorage((s) => Math.max(1, Math.min(s, available)))
```

On the operator's host that evaluates to 1 GiB when 0.84 GiB is free. The
slider's maximum computes to `Math.max(1, Math.min(500, 0.84))` — also 1 GiB. So
the control sat at its own minimum, which was above the ceiling, and every
submission was refused with nothing on screen to act on.

Worked through with the host's real numbers rather than assumed:

| | |
|---|---|
| capacity | 6.25 GiB |
| used | 3.41 GiB |
| base reserve (20%, floored at 2 GiB) | 2.00 GiB |
| foreign usage absorbed by `HostAllocationReader` | 3.41 GiB |
| **available** | **0.84 GiB** |
| form minimum | 1.00 GiB |

The application form reached the same dead end by the opposite route: it fetched
`/host` only to draw the allocation rails, and its sliders ran to 4 cores and
8 GiB on any machine, so the defaults overshot a small host and the overshoot was
visible only to someone who read the rail beside them.

Both now clamp against real headroom, name the minimums once so a floor cannot
outrun a ceiling again, and refuse up front naming the resource that is short and
by how much. Refusing up front is the point: the remedy is a bigger disk, and
that is not on this screen.

Two things worth keeping:

- **The admission gate was right every time.** The bug was entirely in what the
  form could express, which is why five identical failures produced no server
  error to find. Reading the 409 as a server fault cost two releases.
- **The reserve is severe on small hosts by design** — it absorbs disk used by
  things Airside does not manage, so an 8 GB root volume with Docker images on it
  has almost nothing left to give. That is correct, and it is now stated in the
  README's requirements table instead of being discovered.

---

## Querying the control plane, and a wall of red — **done**

Two things, both reported by looking at a working screen and finding it useless.

### The store was excluded from the query console

Refused originally with what reads like a good reason: it holds every
credential, session and audit row on the host, so a console pointed at it is not
a feature. The operator's answer was one line — *"i should be able to query the
airside db"* — and they were right.

The reasoning does not survive contact with the product:

- **An Airside login is already a root login.** The README says so in its own
  security section. Anyone who can reach this screen can start a container with
  the Docker socket mounted.
- **The documented recovery path is `docker exec airside-db psql`.** It is in
  the README, and I handed it to the same operator earlier in the session as the
  way to undo an MFA lockout. The access was never withheld — only the good
  interface to it was.

So refusing protected nothing and removed the tool most likely to answer the
question someone came to that screen with. What is kept is the product's own
guard rather than one invented here: writes need
`database.query_destructive`, exactly as for any other database. Reads are
audited under a distinct action, so *"who read the table holding every
credential on this host"* is answerable without joining on workload ids.

Credentials come from the connection string the API already holds, because there
is nowhere else — the store is created by compose and has no credential row.
Under the SQLite provider it refuses and says why: there is no container to exec
into.

### Every stderr line was painted as a failure

The log viewer coloured by stream. Postgres, nginx, Redis and most server images
write their *entire normal log* to stderr, so a healthy store rendered as an
unbroken wall of red checkpoint messages.

That is worse than no colour at all, in both directions: alarming when nothing
is wrong, and unremarkable when something finally is. Severity now comes from the
text. Crude, and it will miss things — but a missed highlight costs a reader
nothing, while a screen of false red costs them the ability to spot the real one.

Checked against output from both sides rather than assumed: Postgres checkpoint
and startup lines, an nginx access line, a Redis ready line and ASP.NET's `info:`
prefix stay plain; Postgres `FATAL`, an nginx `[error]`, a .NET unhandled
exception, a Go `panic:` and ASP.NET's `fail:` prefix all light up.

### The pattern worth naming

Both of these, and the create-form failure before them, are the same mistake in
different clothes: **a defensible-sounding rule applied one step wider than the
thing it protects.** Ids that match no row made destructive actions unreachable,
and also made logs unreadable. A store full of secrets should not be casually
writable, and was made unreadable. stderr carries errors, so everything on stderr
was an error.

None of them are visible from the code. Each was found by someone looking at the
screen and asking why it would not do the obvious thing.

---

## A proxy log full of red — **done**

Reported as a question — *"whys proxy showing red?"* — with a screenshot of
Caddy's log. Three separate causes, and only one of them a defect.

**Airside asked Caddy to delete a route that was already gone, every two
minutes, forever.** Once a dashboard domain exists, reconciliation calls
`RemoveFallbackRouteAsync` unconditionally on every pass. The first call
succeeds; every subsequent one gets `unknown object ID 'airside-route-fallback'`
— a 404 that Caddy logs at `error` level. About seven hundred error lines a day,
each reporting that something Airside wanted gone was already gone, which is the
desired state.

Nothing broke, which is exactly why it survived: the only damage is that the
operator learns the proxy log is always red, and stops reading it. The presence
of the route is now checked against the routes array first — not with
`GET /id/…`, which 404s and logs identically, trading one spurious error for
another. An unreadable configuration still attempts the delete, because failing
to read is not evidence of absence and a fallback left in place serves the
dashboard on every hostname pointed at the host.

**The `context canceled` lines are normal.** Caddy logs a client disconnect on a
streaming response at `error` level. Every time the Monitoring screen switches
workloads, the browser closes an `EventSource` and Caddy records an error. Live
logs made this visible; they did not make it wrong.

**The 502s coincided with an upgrade.** A one-millisecond failure to reach
`airside-api:8080` is the API container being replaced. Not investigated further,
and not claimed to be understood.

Worth recording: two of the three were **the log being honest about things that
did not matter**, and the fix for those is to stop generating them, not to
recolour them. The severity heuristic added alongside live logs was working
correctly here — Caddy really did log these at `error`.

---

## Cross-cutting, held to in every phase

- Both migrations generated in the same pull request; CI fails if either lags.
- Every new endpoint has an authorisation policy and, where privileged, an audit
  record.
- Every job handler has a compensation path and an idempotency key.
- No shell strings, no host-path bind mounts, no secrets in logs.
- Nothing is reported as working that has not been built and run.
