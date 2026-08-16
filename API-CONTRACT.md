# Airside — API contract v1

Status: **proposed, awaiting approval.** This is the contract the web UI (v0.2)
builds against, so it is meant to be complete and stable before endpoint code is
written.

Once Phase 1 lands, the generated OpenAPI document at `/openapi/v1.json` is the
machine-readable source of truth and is checked in CI against this file. This
document is the human one: it explains *why* a shape is what it is, which
OpenAPI cannot.

---

## 1. Conventions

**Base path** `/api/v1`. Every route is versioned. A breaking change to any shape
below means `/api/v2`, not a silent edit.

**Encoding** `application/json`, UTF-8, `camelCase` property names, enums as
strings (never ordinals — an enum that reorders silently corrupts every stored
client).

**Timestamps** ISO 8601 with offset, always UTC: `2026-03-14T09:21:03.482Z`.

**Numbers** Memory and storage are bytes; CPU is nano-CPUs (`1_000_000_000` = one
core). All are JSON numbers, and all stay well under JavaScript's
`Number.MAX_SAFE_INTEGER` (2^53) — a 9 PB disk is 9×10^15, one core is 10^9. No
string-encoded integers are needed, and none are used.

### Authentication

Cookie-based. `POST /api/v1/auth/login` sets an `HttpOnly`, `Secure`,
`SameSite=Strict` session cookie; every subsequent request carries it
automatically, including on `EventSource` connections. There is no bearer token
and no token in JavaScript's reach — which is also why the live streams need no
authentication scheme of their own.

Unauthenticated requests get `401`. Authenticated requests missing a permission
get `403`. A resource that exists but is not visible to the caller gets `404`,
deliberately — `403` would confirm its existence to someone who should not know.

### Errors

RFC 9457 `ProblemDetails`, with `code` and `metadata` as extensions:

```json
{
  "type": "https://airside.dev/errors/resource.insufficient_memory",
  "title": "Not enough memory available",
  "status": 409,
  "detail": "Not enough memory available to provision this database.",
  "code": "resource.insufficient_memory",
  "traceId": "0HN7GK2LJ8P4M",
  "metadata": {
    "requestedBytes": 4294967296,
    "availableBytes": 2147483648,
    "capacityBytes": 8589934592,
    "allocatedBytes": 5368709120,
    "reservedBytes": 1073741824
  }
}
```

`code` is the stable contract — the UI switches on it. `detail` is for humans and
never contains a secret or a raw message from an external system. **Clients must
render numbers from `metadata` and never parse `detail`.**

Validation failures add `errors: { "fieldName": ["message", …] }` alongside.

The full code list is `src/Airside.Core/Common/ErrorCodes.cs`; it is generated
into the OpenAPI document so clients can exhaustively switch.

**Successes are never wrapped in an envelope.** `200` returns the resource
directly.

### Status codes

| | |
|---|---|
| `200` | Read, or a synchronous write that returns the resource |
| `201` | Created synchronously, with `Location` |
| `202` | Long-running operation enqueued — see below |
| `204` | Succeeded, nothing to return |
| `400` | Validation failure |
| `401` / `403` | Unauthenticated / permission missing |
| `404` | Not found, or not visible to this caller |
| `409` | Conflict: slug taken, workload busy, illegal transition, allocation rejected |
| `412` | `If-Match` precondition failed |
| `429` | Rate limited (`Retry-After` set) |

### Long-running operations

Provisioning, resizing, deploying, backing up, restoring, deleting, attaching,
and updating all enqueue a job and return immediately. **No infrastructure
operation blocks an HTTP request.**

```
202 Accepted
Location: /api/v1/jobs/019559e2-...
```
```json
{
  "jobId": "019559e2-7c31-7a44-b2f1-6c9e0a3d5e10",
  "jobType": "database.provision",
  "workloadId": "019559e2-7c31-7a44-b2f1-6c9e0a3d5e0f",
  "statusUrl": "/api/v1/jobs/019559e2-7c31-7a44-b2f1-6c9e0a3d5e10",
  "eventsUrl": "/api/v1/jobs/019559e2-7c31-7a44-b2f1-6c9e0a3d5e10/events"
}
```

The client opens an `EventSource` on `eventsUrl` for live progress and falls back
to polling `statusUrl` if streaming is unavailable. Both are always valid.

**Idempotency.** Requests that enqueue a job accept an `Idempotency-Key` header.
Re-sending the same key while a job is in flight returns the **same** `202` body
rather than starting a second job. Without the header the server derives a key
from the operation and target, so a double-clicked provision button still cannot
create two containers.

### Concurrency

Mutable resources return `ETag`. `PATCH` accepts `If-Match`; a mismatch is `412`.
This is what stops two admins in two tabs silently overwriting each other's
resource limits.

### Pagination

Two shapes, chosen by whether the collection is stable:

```ts
// Stable collections: databases, applications, users, volumes
PagedResult<T> = { items: T[]; page: number; pageSize: number; totalCount: number }
// ?page=1&pageSize=25   (pageSize max 200)

// Append-only streams: audit, jobs, query history, deployments
CursorResult<T> = { items: T[]; nextCursor: string | null }
// ?cursor=<opaque>&limit=50
```

Offset paging over an append-only log is wrong: rows arrive while the user
pages, every page boundary shifts, and entries are silently skipped. Audit is
exactly the place where a silently skipped row matters most.

### Secrets in responses

Every secret-bearing field is masked by default and carries the means to reveal
it deliberately:

```ts
SecretField = {
  isSecret: true
  value: "***"           // always, in every list and detail response
  revealUrl: string      // POST here with secret.read to obtain the value
  lastChangedAt: string
}
```

Reveal is **`POST`**, never `GET` — a `GET` with a secret in the response lands in
browser history, proxy logs, and referrer headers. Every reveal writes an audit
record.

### Warnings

Some requests succeed but deserve a caveat the UI must surface. These are not
errors and do not change the status code:

```ts
Warning = { code: string; message: string; metadata?: Record<string, unknown> }
```

Returned as a `warnings: Warning[]` array on the relevant response. Defined
warning codes:

| Code | Raised when |
|---|---|
| `redis.noeviction_write_failure_risk` | `maxmemory-policy` is `noeviction`; a full instance starts failing writes rather than evicting |
| `redis.maxmemory_headroom_low` | `maxmemory` exceeds 70% of the container limit while persistence is on — BGSAVE forks, and copy-on-write can push the instance into the cgroup OOM killer mid-backup |
| `database.published_publicly` | `publishBindAddress` is `0.0.0.0`; the database is reachable from the internet |
| `container.runs_as_root` | The image declares no non-root `USER`. Airside cannot override this without breaking images that legitimately write to root-owned paths, so it reports rather than silently failing to deliver a guarantee |
| `storage.enforcement_unavailable` | The host filesystem cannot enforce per-volume quotas; storage allocation is accounting only |
| `backup.local_only` | The backup is stored on the same instance as the database and will not survive its loss |
| `deployment.no_previous_image` | Nothing to roll back to |

### Rate limits

| Scope | Limit |
|---|---|
| `POST /auth/login`, `/setup/complete` | 5 per minute per IP, then `429` |
| Secret reveal | 20 per minute per user |
| Query console execution | 30 per minute per user |
| Destructive operations (delete, restore, rollback) | 10 per minute per user |

### Destructive confirmation

Deleting a workload or a volume requires the caller to type the resource's slug,
submitted as `confirmSlug`. A mismatch is `409 workload.confirmation_mismatch`.

Deletes are `POST .../delete` rather than `DELETE`, because they carry a body
(`confirmSlug`, `deleteVolume`) and bodies on `DELETE` are unreliable through
intermediaries. The volume decision is a required explicit boolean — there is no
default, so nobody deletes data by omission.

---

## 2. Live streams (Server-Sent Events)

Four streams, each an ordinary authenticated `GET` returning `text/event-stream`.
Not WebSockets and not a hub: every stream in Airside is server-to-client, and
the client's only input is which resource it wants — which is a URL.

| Stream | Permission | Events |
|---|---|---|
| `GET /api/v1/jobs/{id}/events` | authenticated | `job.updated`, `job.step`, `job.completed` |
| `GET /api/v1/databases/{id}/logs/stream?tail=200` | `logs.read` | `log.line` |
| `GET /api/v1/databases/{id}/metrics/stream?intervalSeconds=5` | `metrics.read` | `metric.sample` |
| `GET /api/v1/notifications/stream` | authenticated | `notification.raised` |

```
id: 3
event: job.step
data: {"sequence":3,"name":"health","message":"Health check passed.","occurredAt":"…"}

: keep-alive
```

**Resume is the point.** Every frame that can be resumed from carries an `id`.
A browser sends the last one back as `Last-Event-ID` on reconnect automatically,
and the server continues from there rather than replaying what the client already
saw or — worse — skipping what arrived while it was disconnected. Job streams
resume on the step sequence; log streams resume on the timestamp, so a reconnect
asks Docker for lines `since` that point.

Clients that cannot set headers may pass `?lastEventId=` instead, which is what
makes `curl -N` a first-class way to watch any stream in the product.

**`stream.closing`** ends a stream with a reason rather than dropping it silently:

| Reason | Meaning |
|---|---|
| `job-complete` | The job reached a terminal state; nothing further will arrive |
| `rate-limited` | The container out-ran the reader; reconnect with `Last-Event-ID` to continue |
| `container-gone` | The container no longer exists |

**Backpressure.** A subscriber that stops reading fills a bounded per-connection
buffer, and further events are dropped rather than queued — one browser tab must
not be able to grow the control plane's heap. Log streams cap at 2000 lines per
second and then close with `rate-limited`, because reconnect-and-resume recovers
the gap correctly whereas silently dropping lines does not.

**Live metrics are never persisted.** Historical data is the hourly rollup from
`GET .../metrics`. `metric.sample.cpuNanos` is `null` on the first sample for a
container: Docker's one-shot stats call carries no previous CPU reading, and a
plausible `0` would be a lie.

A heartbeat comment is sent every 15 seconds. Without it, proxies close an idle
stream and the client sees an unexplained disconnect.

---

## 3. Setup and health

| Method | Path | Permission | Notes |
|---|---|---|---|
| `GET` | `/health` | none | Liveness. **No `/api/v1` prefix** — the self-updater polls this, and it must not move between versions |
| `GET` | `/api/v1/setup/status` | none | Whether first-run is complete |
| `POST` | `/api/v1/setup/complete` | setup token | Creates the first Super Admin |

```ts
SetupStatus = {
  setupCompleted: boolean
  storeProvider: "postgres" | "sqlite"
  version: string
  // True until a domain is attached. While true the dashboard has no publicly
  // trusted certificate — Let's Encrypt does not issue for bare IPs — so the UI
  // must warn that credentials are crossing the wire unprotected.
  awaitingDomain: boolean
}

SetupCompleteRequest = {
  setupToken: string     // printed on the console by the installer; only its hash is stored
  email: string
  password: string
  displayName: string
  instanceName: string
}
```

There is no default account at any point, not even briefly. The first Super Admin
is created here or not at all.

---

## 4. Authentication and the current user

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/v1/auth/login` | none |
| `POST` | `/api/v1/auth/logout` | authenticated |
| `GET` | `/api/v1/auth/me` | authenticated |
| `POST` | `/api/v1/auth/password` | authenticated (own) |
| `GET` | `/api/v1/auth/sessions` | authenticated (own) |
| `DELETE` | `/api/v1/auth/sessions/{id}` | authenticated (own) |
| `POST` | `/api/v1/auth/mfa/enrol` | authenticated (own) — *Phase 6* |
| `POST` | `/api/v1/auth/mfa/confirm` | authenticated (own) — *Phase 6* |
| `DELETE` | `/api/v1/auth/mfa` | authenticated (own) — *Phase 6* |

```ts
LoginRequest = { email: string; password: string; totpCode?: string }

CurrentUser = {
  id: string
  email: string
  displayName: string
  roles: string[]            // slugs
  permissions: string[]      // the resolved union — the UI gates on this, not on roles
  mfaEnabled: boolean
  mustChangePassword: boolean
}
```

`permissions` is the resolved union across roles. **The UI must gate on
permissions, never on role names** — that is the entire point of roles being
bundles, and it is what lets an operator create a role that can restart a
database but not read it.

---

## 5. Host and system

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/v1/host` | `metrics.read` |
| `PATCH` | `/api/v1/host/reserve` | `server.manage` |
| `GET` | `/api/v1/host/metrics?from=&to=` | `metrics.read` |
| `GET` | `/api/v1/system/info` | authenticated |
| `GET` | `/api/v1/system/updates` | `server.update` |
| `POST` | `/api/v1/system/update` | `server.update` → `202` |
| `POST` | `/api/v1/system/rollback` | `server.update` → `202` |
| `GET` | `/api/v1/system/updates/history` | `server.update` |
| `GET` | `/api/v1/system/reconciliation` | `server.manage` |
| `POST` | `/api/v1/system/reconciliation/scan` | `server.manage` → `202` |
| `POST` | `/api/v1/system/reconciliation/{itemId}/resolve` | `server.manage` → `202` |

```ts
HostDto = {
  id: string
  name: string
  capacity:  { cpuNanos: number; memoryBytes: number; storageBytes: number }
  reserve:   { cpuNanos: number; memoryBytes: number; storageBytes: number }
  allocated: { cpuNanos: number; memoryBytes: number; storageBytes: number }
  used:      { cpuNanos: number; memoryBytes: number; storageBytes: number } | null
  available: { cpuNanos: number; memoryBytes: number; storageBytes: number }
  storageEnforcement: "accounting" | "quota"
  dockerApiVersion: string
  kernelVersion: string
  operatingSystem: string
  lastDiscoveredAt: string
  warnings: Warning[]
}
```

Capacity, allocated, and used are three separate objects and must be rendered as
three separate numbers. Conflating them is how a dashboard ends up showing "50%
used" for a host that cannot accept another workload.

`used` is `null` until sampled — render "—", not zero.

`storageEnforcement: "accounting"` means storage limits are **counted and alerted
on, but not enforced by the kernel**. Docker's `local` volume driver over ext4,
or xfs without `pquota`, supports no per-volume limit at all. The UI must not
present storage allocation as a guarantee on such a host.

```ts
ReconciliationReport = {
  scannedAt: string
  items: {
    id: string
    kind: "container" | "volume" | "network"
    reference: string
    drift: "missing" | "unexpected" | "mismatched"
    workloadId: string | null
    workloadSlug: string | null
    detail: string
    availableActions: ("recreate" | "adopt" | "forget" | "remove")[]
  }[]
}
```

Reconciliation **reports** drift and offers per-item remediation. It never
auto-corrects: an auto-reconciler that deletes an unrecognised container is one
bug away from deleting a user's data.

---

## 6. Database engine catalogue

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/v1/database-engines` | `database.read` |

```ts
DatabaseEngineDto = {
  kind: "postgres" | "mysql" | "mongodb" | "redis"
  displayName: string
  supportedVersions: string[]        // newest first
  defaultVersion: string
  defaultPort: number
  capabilities: {
    supportsDatabaseName: boolean
    supportsUserAccounts: boolean
    supportsLogicalBackup: boolean
    supportsSnapshotBackup: boolean
    requiresStopForRestore: boolean
    requiresMaxMemory: boolean
    queryDialect: "sql" | "mongoShell" | "redisCommand"
    defaultEnvKeyPrefix: string
  }
  maxMemoryPolicies?: string[]       // Redis only
  injectedEnvKeys: string[]          // e.g. ["_HOST","_PORT","_PASSWORD","_URL"]
  variants: {
    value: "alpine" | "debian"
    displayName: string
    isDefault: boolean
    note: string | null              // present only on the non-default option
  }[]
}
```

### Image variants

| Engine | Variants | Default |
|---|---|---|
| PostgreSQL | `alpine`, `debian` | **`alpine`** |
| Redis | `alpine`, `debian` | **`alpine`** |
| MySQL | `debian` only | `debian` |
| MongoDB | `debian` only | `debian` |

**A single entry means the UI renders no variant control at all.** MySQL
discontinued its Alpine images upstream and MongoDB never published one, so there
is no choice to present — and offering one would resolve to a tag that does not
exist.

**`note` is null on the default, and that is deliberate.** A warning attached to
the path most users take is noise, and it teaches people that these messages can
be dismissed unread — exactly the habit you do not want when a real one appears.
Since Alpine is the default, the note lives on the Debian option and explains
what it buys (broader extension availability, standard glibc tooling) and what it
costs (a larger image), along with the fact that the variant is fixed at creation.

`imageVariant` on `CreateDatabaseRequest` is optional; omitting it takes the
engine's own default. It is **fixed once the database exists** — Alpine and Debian
differ in libc and in the layout an engine initialises into its volume, so
changing it is a migration rather than a setting. Any attempt returns
`400 validation.field_not_applicable` with `current` and `requested` in metadata.

`customImage` bypasses variant resolution entirely and sets `usesCustomImage` on
the workload; supplying both is rejected. See `docs/image-variants.md`.

> **This endpoint is load-bearing for the UI.** The provisioning form must be
> rendered from these capabilities, not from hardcoded engine knowledge. When
> `supportsDatabaseName` is false the database-name field is not shown; when
> `requiresMaxMemory` is true the maxmemory fields appear and are required. That
> is what stops Redis becoming a pile of `if (engine === 'redis')` branches in the
> UI — the same mistake the backend is explicitly avoiding, and the one the brief
> warns produces a broken abstraction.

---

## 7. Databases

| Method | Path | Permission | Result |
|---|---|---|---|
| `GET` | `/api/v1/databases` | `database.read` | `PagedResult<DatabaseSummary>` |
| `POST` | `/api/v1/databases` | `database.create` | `202` |
| `GET` | `/api/v1/databases/{id}` | `database.read` | `DatabaseDetail` |
| `PATCH` | `/api/v1/databases/{id}` | `database.update` | `200` |
| `POST` | `/api/v1/databases/{id}/resize` | `database.update` | `202` |
| `POST` | `/api/v1/databases/{id}/start` | `database.lifecycle` | `202` |
| `POST` | `/api/v1/databases/{id}/stop` | `database.lifecycle` | `202` |
| `POST` | `/api/v1/databases/{id}/restart` | `database.lifecycle` | `202` |
| `POST` | `/api/v1/databases/{id}/delete` | `database.delete` | `202` |
| `GET` | `/api/v1/databases/{id}/logs` | `logs.read` | snapshot |
| `GET` | `/api/v1/databases/{id}/metrics` | `metrics.read` | rollups |

```ts
CreateDatabaseRequest = {
  slug: string                  // ^[a-z][a-z0-9-]{1,30}[a-z0-9]$, no consecutive hyphens
  displayName: string
  engine: "postgres" | "mysql" | "mongodb" | "redis"
  version: string
  cpuNanos: number
  memoryBytes: number
  storageBytes: number
  autoRestart: boolean

  publishedPort?: number | null           // null = not published to the host
  publishBindAddress?: "127.0.0.1" | "0.0.0.0"   // defaults to loopback

  databaseName?: string        // required except Redis; rejected for Redis
  username?: string            // required except Redis; rejected for Redis
  password?: string            // omit to generate

  maxMemoryBytes?: number      // Redis: required
  maxMemoryPolicy?: string     // Redis: required
  aofEnabled?: boolean         // Redis

  backupEnabled: boolean
  backupCron?: string
  backupRetentionCount?: number
  backupRetentionDays?: number
}
```

Fields are **rejected, not ignored**, when they do not apply:
`databaseName` on a Redis request is `400 validation.field_not_applicable`, not a
silently dropped value. A client that sends it has misunderstood something, and
saying so is more useful than pretending.

`publishBindAddress` defaults to `127.0.0.1`. Sending `0.0.0.0` succeeds but
returns `database.published_publicly`, and the UI must require a separate,
explicit confirmation for it. A default that put Postgres on the public internet
would be a launch-week incident.

```ts
DatabaseSummary = {
  id: string
  slug: string
  displayName: string
  engine: string
  version: string
  state: "provisioning" | "running" | "stopped" | "restarting"
       | "backingUp" | "restoring" | "failed" | "deleting" | "deleted"
  stateChangedAt: string
  cpuNanos: number
  memoryBytes: number
  storageBytes: number
  storageUsedBytes: number | null
  activeJobId: string | null
  driftState: "none" | "missing" | "unexpected" | "mismatched"
  isSystem: false
}

DatabaseDetail = DatabaseSummary & {
  imageRef: string
  imageDigest: string
  databaseName: string | null       // null for Redis
  publishedPort: number | null
  publishBindAddress: string | null
  maxMemoryBytes: number | null     // Redis only
  maxMemoryPolicy: string | null
  aofEnabled: boolean | null
  backup: { enabled: boolean; cron: string | null; retentionCount: number | null;
            retentionDays: number | null; lastBackupAt: string | null }
  attachedApplications: { id: string; slug: string; envKeyPrefix: string }[]
  volumes: VolumeSummary[]
  warnings: Warning[]
}

DeleteDatabaseRequest = {
  confirmSlug: string
  deleteVolume: boolean    // required; no default
}
```

`deleteVolume` has no default and no implicit value. The brief's requirement is
that deleting a database must not delete its data unless the admin says so, and
an optional boolean defaulting to `false` is a weaker guarantee than a required
one — an omitted field is ambiguous, a required one is a decision.

Kept volumes become orphaned, remain counted against allocated storage, and
appear under `/api/v1/volumes`.

### Credentials

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/v1/databases/{id}/credentials` | `database.read` |
| `POST` | `/api/v1/databases/{id}/credentials/rotate` | `database.rotate_credentials` → `202` |
| `POST` | `/api/v1/databases/{id}/credentials/{credentialId}/reveal` | `secret.read` |
| `POST` | `/api/v1/databases/{id}/credentials/{credentialId}/revoke` | `database.rotate_credentials` |

```ts
DatabaseCredentialDto = {
  id: string
  username: string | null       // null for Redis: requirepass, implicit `default` user
  password: SecretField
  connectionString: SecretField
  isPrimary: boolean
  state: "active" | "retired" | "revoked"
  createdAt: string
}
```

Rotation issues a **second active credential** rather than replacing the first.
The flow is: rotate → redeploy attached applications → revoke the old one.
Replacing in place would break every attached application at the instant of
rotation, which is why `revoke` is a separate, explicit call.

### Backups and restore

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/v1/databases/{id}/backups` | `database.read` |
| `POST` | `/api/v1/databases/{id}/backups` | `database.backup` → `202` |
| `GET` | `/api/v1/backups/{id}` | `database.read` |
| `PATCH` | `/api/v1/backups/{id}` | `database.backup` — `{ isRetained }` |
| `POST` | `/api/v1/backups/{id}/delete` | `database.backup` |
| `GET` | `/api/v1/backups/{id}/download` | `database.backup` — streams |
| `POST` | `/api/v1/backups/{id}/restore` | `database.restore` → `202` |

```ts
BackupDto = {
  id: string
  databaseId: string
  kind: "logical" | "snapshot"
  triggerKind: "scheduled" | "manual" | "preRestore" | "preUpdate"
  status: "running" | "succeeded" | "failed"
  sizeBytes: number | null
  sha256: string | null
  engineSnapshot: string          // e.g. "postgres:16.4"
  startedAt: string
  completedAt: string | null
  expiresAt: string | null
  isRetained: boolean
  warnings: Warning[]             // includes backup.local_only
}

RestoreRequest = { confirmSlug: string }

RestorePreview = {                // GET /api/v1/backups/{id}/restore-preview
  requiresStop: boolean           // true for Redis
  estimatedDowntimeSeconds: number | null
  engineVersionMatches: boolean
  preRestoreBackupWillBeTaken: true
}
```

Restore always takes a pre-restore backup first and links it, so "we restored the
wrong backup" has an answer rather than a conversation.

Restore refuses on an engine version mismatch (`backup.engine_version_mismatch`)
before touching anything. A pg_dump from 16 does not restore into 15, and
discovering that halfway through means the database is already stopped.

The checksum is verified before restore begins. A truncated backup that restores
as an empty database is the worst possible failure mode for this feature.

`requiresStop` is `true` for Redis and the UI must say so before the user
commits: an RDB cannot be loaded into a running instance, so the flow is stop →
replace `dump.rdb` → start, and that is real downtime.

### Query console

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/v1/databases/{id}/query` | `database.query` |
| `POST` | `/api/v1/databases/{id}/query/{executionId}/cancel` | `database.query` |
| `GET` | `/api/v1/databases/{id}/query/history` | `database.query` (own only) |
| `GET`/`POST` | `/api/v1/saved-queries` | `database.query` |
| `PATCH`/`DELETE` | `/api/v1/saved-queries/{id}` | `database.query` |

```ts
QueryRequest = { statement: string; maxRows?: number; timeoutSeconds?: number }

QueryResponse = {
  executionId: string
  columns: string[]
  rows: unknown[][]
  rowsAffected: number
  truncated: boolean
  durationMs: number
}
```

`database.query` is independent of every infrastructure permission. A user may
legitimately restart a database without being allowed to read it.

**Redis is a command console, not a query editor.** `KEYS`, `FLUSHALL`,
`FLUSHDB`, `CONFIG SET`, and `SHUTDOWN` return `409 query.command_blocked`, or
`403 query.command_requires_elevation` when the caller lacks
`database.query_destructive`. `KEYS *` on a large production instance blocks the
server for the duration of the scan; any key-browsing UI must use `SCAN`.

Query history is **strictly per-user** and not listable by anyone else regardless
of permission. A statement like `INSERT INTO users (password) VALUES (…)` lands
in history as plain text, so history is a secret-bearing surface and is treated
as one. It is capped per user per database and excluded from support bundles.

---

## 8. Applications

| Method | Path | Permission | Result |
|---|---|---|---|
| `GET` | `/api/v1/applications` | `application.read` | `PagedResult<ApplicationSummary>` |
| `POST` | `/api/v1/applications` | `application.create` | `201` — metadata only, no deploy |
| `GET` | `/api/v1/applications/{id}` | `application.read` | `ApplicationDetail` |
| `PATCH` | `/api/v1/applications/{id}` | `application.update` | `200` |
| `POST` | `/api/v1/applications/{id}/resize` | `application.update` | `202` |
| `POST` | `/api/v1/applications/{id}/start\|stop\|restart` | `application.lifecycle` | `202` |
| `POST` | `/api/v1/applications/{id}/delete` | `application.delete` | `202` |
| `GET` | `/api/v1/applications/{id}/logs` | `logs.read` | snapshot |
| `GET` | `/api/v1/applications/{id}/metrics` | `metrics.read` | rollups |

Creation is synchronous and returns `201`: it writes a record and allocates
nothing. Deploying is a separate, explicit act. Conflating them would mean a
typo in a Dockerfile path leaves you with no way to correct it without deleting
the application.

```ts
CreateApplicationRequest = {
  slug: string
  displayName: string
  cpuNanos: number
  memoryBytes: number
  storageBytes: number
  autoRestart: boolean
  containerPort: number                    // what the app listens on; the proxy upstream

  source:
    | { kind: "image"; imageRef: string; registryCredentialId?: string }
    | { kind: "git"; repositoryUrl: string; branch: string;
        dockerfilePath: string; buildContextPath?: string; gitCredentialId?: string }
    | { kind: "dockerfile"; dockerfileContent: string }

  healthCheck:
    | { kind: "http"; path: string; expectedStatus: number;
        intervalSeconds: number; timeoutSeconds: number; retries: number }
    | { kind: "command"; command: string[];
        intervalSeconds: number; timeoutSeconds: number; retries: number }
}
```

**`healthCheck` is required and has no "none" variant.** Zero-downtime deployment
is defined as start-new, poll-health, swap-upstream, stop-old. Without a health
check that degrades to waiting a few seconds and hoping, and the API should not
make it possible to ask for something Airside cannot deliver.

`command` is an argument vector, never a command line. There is no shell.

**Docker Compose is not a source kind.** Out of MVP scope — a Compose file is a
multi-container workload and breaks the one-app-one-container assumption behind
resource limits, health checks, rollback, and proxy routing.

`repositoryUrl` and `dockerfilePath` are validated on submission: the path is
rejected if it escapes the build context, and the URL scheme is restricted.

### Deployments

| Method | Path | Permission |
|---|---|---|
| `POST` | `/api/v1/applications/{id}/deployments` | `application.deploy` → `202` |
| `GET` | `/api/v1/applications/{id}/deployments` | `application.read` — `CursorResult` |
| `GET` | `/api/v1/deployments/{id}` | `application.read` |
| `GET` | `/api/v1/deployments/{id}/log` | `application.read` — plain text |
| `POST` | `/api/v1/deployments/{id}/rollback` | `application.rollback` → `202` |

```ts
DeployRequest = { branch?: string; commitSha?: string; imageRef?: string }

DeploymentDto = {
  id: string
  applicationId: string
  number: number                  // humans say "deployment 14"
  status: "queued" | "building" | "deploying" | "succeeded" | "failed" | "rolledBack"
  triggerKind: "manual" | "rollback" | "api"
  commitSha: string | null
  commitMessage: string | null
  branch: string | null
  imageRef: string | null
  imageDigest: string | null
  startedAt: string
  completedAt: string | null
  durationMs: number | null
  isCurrent: boolean
  rolledBackFromDeploymentId: string | null
  errorCode: string | null
  warnings: Warning[]
}
```

Rollback targets any previous **successful** deployment and is a proxy change
plus a container start, not a rebuild — which works because `imageDigest` is
retained. If the image has been pruned, rollback returns
`409 deployment.image_pruned` rather than silently rebuilding something that may
no longer produce the same artefact.

### Environment variables

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/v1/applications/{id}/environment` | `secret.view` |
| `PUT` | `/api/v1/applications/{id}/environment/{key}` | `secret.write` |
| `POST` | `/api/v1/applications/{id}/environment/{key}/reveal` | `secret.read` |
| `DELETE` | `/api/v1/applications/{id}/environment/{key}` | `secret.write` |

```ts
EnvironmentResponse = {
  entries: {
    key: string
    value: string          // "***" when isSecret
    isSecret: boolean
    source: "manual" | "attachment"
    sourceAttachmentId: string | null
    editable: boolean      // false for attachment-injected entries
    revealUrl: string | null
    updatedAt: string
  }[]
}
```

Attachment-injected entries appear here but are **not stored** — they are rendered
from the attachment and the live credential at read time and at deploy time. That
is why they are `editable: false`: editing one would be edited away at the next
deploy, and storing them would mean a credential rotation leaves the running
container holding a password the UI no longer shows.

`secret.view` lists keys. `secret.read` reveals values. They are separate
permissions because knowing that `STRIPE_SECRET_KEY` exists is not the same as
knowing what it is.

### Database attachment

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/v1/applications/{id}/databases` | `application.read` |
| `POST` | `/api/v1/applications/{id}/databases` | `application.attach_database` → `202` |
| `DELETE` | `/api/v1/applications/{id}/databases/{attachmentId}` | `application.attach_database` → `202` |

```ts
AttachDatabaseRequest = {
  databaseId: string
  envKeyPrefix?: string      // defaults to the engine's; must not collide
}

AttachmentDto = {
  id: string
  databaseId: string
  databaseSlug: string
  engine: string
  envKeyPrefix: string
  injectedKeys: string[]     // e.g. ["REDIS_HOST","REDIS_PORT","REDIS_PASSWORD","REDIS_URL"]
  attachedAt: string
  attachedBy: string
}
```

Attaching joins the application's container to the database's network; detaching
removes it. **This record is the network authorisation** — an application can
reach only the databases attached to it, never every database on the host.

`envKeyPrefix` collisions are `409 database.env_prefix_conflict`. Two attached
Postgres databases cannot both claim `DATABASE_URL`.

Redis injects `REDIS_HOST`, `REDIS_PORT`, `REDIS_PASSWORD`, `REDIS_URL` — no
`_NAME`, no `_USER`, because Redis has neither.

### Domains and TLS

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/v1/applications/{id}/domains` | `domain.read` |
| `POST` | `/api/v1/applications/{id}/domains` | `domain.manage` → `202` |
| `POST` | `/api/v1/domains/{id}/delete` | `domain.manage` → `202` |
| `GET` | `/api/v1/domains/{id}/certificate` | `domain.read` |

```ts
DomainDto = {
  id: string
  hostname: string
  isPrimary: boolean
  state: "pending" | "active" | "failed"
  certificate: {
    issuer: string
    notBefore: string
    notAfter: string
    autoRenew: boolean
    lastCheckedAt: string
  } | null
  errorCode: string | null
}
```

`state: "pending"` covers the ACME challenge. The UI should say DNS must already
point at the host — the most common failure here is a certificate request for a
domain whose A record has not propagated, and the resulting error is otherwise
opaque.

---

## 9. Volumes, jobs, users, audit, notifications

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/v1/volumes?orphaned=` | `server.manage` |
| `POST` | `/api/v1/volumes/{id}/delete` | `server.manage` → `202` |
| `GET` | `/api/v1/jobs?status=&workloadId=&type=` | authenticated — `CursorResult` |
| `GET` | `/api/v1/jobs/{id}` | authenticated |
| `POST` | `/api/v1/jobs/{id}/cancel` | matching operation permission |
| `GET`/`POST` | `/api/v1/users` | `user.manage` |
| `GET`/`PATCH` | `/api/v1/users/{id}` | `user.manage` |
| `POST` | `/api/v1/users/{id}/deactivate` | `user.manage` |
| `PUT` | `/api/v1/users/{id}/roles` | `user.manage` |
| `GET`/`POST` | `/api/v1/roles` | `role.manage` |
| `PATCH`/`DELETE` | `/api/v1/roles/{id}` | `role.manage` |
| `GET` | `/api/v1/permissions` | `role.manage` |
| `GET` | `/api/v1/audit?…` | `audit.read` — `CursorResult` |
| `GET` | `/api/v1/notifications` | authenticated |
| `POST` | `/api/v1/notifications/{id}/read` | authenticated |

```ts
JobDto = {
  id: string
  type: string
  status: "queued" | "running" | "succeeded" | "failed"
        | "cancelled" | "compensating" | "compensated"
  progressPercent: number
  currentStep: string | null
  workloadId: string | null
  workloadSlug: string | null
  queuedAt: string
  startedAt: string | null
  completedAt: string | null
  errorCode: string | null
  errorMessage: string | null
  steps: { sequence: number; name: string; status: string;
           message: string | null; startedAt: string; completedAt: string | null }[]
}

VolumeDto = {
  id: string
  name: string
  workloadId: string
  workloadSlug: string          // retained after deletion: "formerly the orders Postgres"
  purpose: "data" | "backup" | "config"
  sizeAllocationBytes: number
  lastMeasuredBytes: number | null
  measuredAt: string | null
  orphanedAt: string | null
}

AuditEventDto = {
  id: string
  occurredAt: string
  userId: string | null
  userEmail: string | null      // snapshot; survives user deletion
  action: string
  resourceKind: string | null
  resourceId: string | null
  resourceSlug: string | null   // snapshot; survives resource deletion
  result: "success" | "failure" | "denied"
  ipAddress: string | null
  correlationId: string
  metadata: Record<string, unknown>
}
```

Audit is read-only. There is no create, update, or delete endpoint, by design.

`compensating` and `compensated` are visible job statuses, not internal ones: the
UI should show that a failed provision is cleaning up after itself, because
otherwise a failure looks like it left debris behind even when it did not.

The three system containers (`airside-api`, `airside-db`, `airside-proxy`) appear
in listings with `isSystem: true`. Every mutating endpoint rejects them with
`403 workload.system_protected` — **including for Super Admin**. There is no
permission that reaches them.

Deactivating the last active Super Admin returns `409 auth.last_super_admin`.
Locking yourself out of a control plane with root-equivalent host reach is
unrecoverable without SSH.

---

## 10. What this contract deliberately does not have

- **No API tokens.** Cookie sessions only. CI/CD deploys need a second
  authentication scheme and belong with a phase that has deployment in it.
- **No resource-scoped permissions.** `database.query` grants query access to
  every database. Documented in `DATA-MODEL.md` §4 with the retrofit shape.
- **No Compose deployments.**
- **No off-host backup targets.** Backups live on the same instance as the
  database, which is why `backup.local_only` is a warning rather than silence.
- **No minute-resolution historical metrics.** Live stream plus hourly rollups.
- **No bulk endpoints.** Deleting five databases is five calls, each with its own
  typed confirmation. Bulk destructive operations are how people destroy the
  wrong thing.
