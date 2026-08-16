# Frontend brief

Everything needed to build the Airside dashboard against the API as it actually
exists. Hand this to whoever (or whatever) is writing the UI.

---

## What you are building

A web dashboard for a control plane that manages **one Linux server**. The
operator uses it to deploy applications, run databases, attach domains, and see
what the machine is doing.

The audience is one to five technical people who administer their own server.
Not a consumer product, and not a multi-tenant SaaS console — there is no billing,
no organisations, no tenant switcher.

## Getting the contract

**Do not hand-write the client.** The API publishes an OpenAPI document; generate
types and a client from it, and regenerate when the API changes.

```
GET /openapi/v1.json      (requires an authenticated session)
```

Everything below is the part a schema cannot tell you.

## The shape of the API

**Base path** is `/api/v1`. **Errors** are RFC 9457 ProblemDetails:

```json
{
  "type": "https://airside.dev/errors/domain.certificate_expiring",
  "title": "Conflict",
  "status": 409,
  "detail": "A human-readable sentence you can show directly.",
  "code": "domain.certificate_expiring",
  "metadata": { "found": "203.0.113.9", "expected": "198.51.100.4" }
}
```

Key `code` for logic; show `detail` to the user — it is written to be read, not
summarised. `metadata` carries structured extras: `found`, `expected`, `remedy`,
`retryAfter`, `confirmField`, `checks`.

**Auth** is a session cookie. `POST /api/v1/auth/login`, then the cookie carries
everything. `GET /api/v1/auth/me` for the current user and permissions. A 401
means show the login screen; a 403 means the user is signed in and lacks the
permission — those are different screens.

## Six rules the UI must honour

These are not preferences. Each one exists because the API is shaped around it,
and a UI that ignores them will produce a confusing or dangerous product.

### 1. Long operations return a job, not a result

Anything that touches Docker returns **202** with a job:

```json
{ "jobId": "01a0…", "jobType": "application.deploy",
  "statusUrl": "/api/v1/jobs/01a0…", "eventsUrl": "/api/v1/jobs/01a0…/events" }
```

Subscribe to `eventsUrl` (Server-Sent Events) and show **the steps as they
happen** — "Pulling image", "Building", "Health check 2/3", "Swapping traffic".
Never a bare spinner. A deploy takes minutes and the user needs to know which
part is slow, and which part failed.

The stream supports `Last-Event-ID`, so a reconnect resumes rather than
restarting.

### 2. TLS mode is a required, explicit choice

When adding a domain there is **no default and no pre-selected option**. Fetch
`GET /api/v1/tls-modes` — it returns each mode with a one-line summary written
for the UI, and an `available` flag. Show the summary next to each choice.

This matters more than it looks. A wrong default here means someone terminating
TLS at CloudFront silently gets automatic issuance, and spends days watching an
ACME challenge fail with nothing explaining why.

### 3. Pre-flight results are the main event, not a validation error

`POST /api/v1/domains/preflight` returns a list of checks, each with `severity`
(`passed` / `unknown` / `warning` / `blocking`), `summary`, `found`, `expected`,
and `remedy`.

Render these as a checklist with the real values:

> ✕ **app.example.com resolves to a different server**
> found `203.0.113.9` · expected `198.51.100.4`
> *Change the A record for app.example.com to 198.51.100.4. If you changed it
> recently, propagation can take up to the record's previous TTL.*

Do not collapse this into "validation failed". The whole feature exists to
replace an opaque failure with a specific one.

`unknown` is a real state and must look different from `passed` — it means the
check could not be carried out, not that it succeeded.

### 4. Destructive actions need the name typed back

Deleting an application, changing the dashboard domain, enabling HSTS preload,
and moving a domain all refuse until the user types the resource name. The API
returns `metadata.confirmField` and `metadata.expected` — use those to drive the
input rather than hard-coding.

Two of these deserve extra weight in the UI:

- **Deleting an application with domains attached** returns
  `application.domains_attached` with the hostname list. Ask explicitly whether
  to release them; do not decide for the user.
- **HSTS preload** is effectively irreversible. Say so in the dialog, in plain
  words, not in a tooltip.

### 5. Secrets are masked; revealing is a separate, audited act

Any secret comes back as `***`. There is a distinct `POST …/reveal` endpoint that
requires an elevated permission and writes an audit entry. Design for that:
a "reveal" affordance that visibly costs something, not a toggle that silently
fetches the value on render.

Never put a secret in a URL, in `localStorage`, or in a log.

### 6. `warnings[]` is advisory and must be shown

Many DTOs carry a `warnings` array of `{ code, message, metadata }`. These are
things that are true but not errors — a certificate expiring in nine days, a
staging certificate that no browser trusts, TLS terminated upstream so Airside
cannot report on it. Surface them inline on the resource, not in a toast that
disappears.

## Live data

Three Server-Sent Event streams besides job events:

```
GET /api/v1/notifications/stream
GET /api/v1/databases/{id}/logs/stream
GET /api/v1/databases/{id}/metrics/stream
```

Use `EventSource`. Handle reconnection — the server sets ids and honours
`Last-Event-ID`.

Metrics for charts come from `GET /api/v1/workloads/{id}/metrics?hours=24`, which
returns hourly rollups. **CPU is in nanoseconds per second, not a percentage** —
divide by the workload's `cpuNanos` limit to show usage against its allocation,
which is the number that tells an operator whether to resize. Memory comes with
`memoryLimitBytes` for the same reason.

## Screens

| Screen | Notes |
|---|---|
| **Setup** | First run only. Consumes the one-time token printed on the console. |
| **Login** | Plus MFA challenge if enrolled. |
| **Overview** | Host capacity vs allocated, workload states, unresolved notifications. |
| **Applications** | List, create, deploy, deployment history with rollback, environment variables, database attachments, domains. |
| **Databases** | List, provision, backups, restore, credential rotation, query console. |
| **Domains** | Per application. TLS mode, pre-flight, certificate detail, HSTS, redirects. |
| **Notifications** | Feed, acknowledge, channel configuration with routing rules and hours. |
| **Settings** | Users and roles, registry credentials, dashboard domain, system backup, updates, audit log. |

### Two screens with specific traps

**Query console.** Redis and SQL consoles exist, and the API refuses dangerous
commands by permission level. Show refusals as what they are — a policy decision
with a reason — not as a syntax error.

**Notification channels.** Routing rules and schedules can silently match
nothing. The API provides `POST /api/v1/notification-channels/preview`, which
runs a rule against real history and returns a warning when it matches none. Wire
that into the editor as a live preview; it is the difference between a channel
that works and one that is quietly dead.

## What does not exist

Do not design for these — there is no API behind them:

- Multi-server, clustering, or any host switcher
- Organisations, teams, or billing
- Container shell / exec from the browser
- Editing files in a repository
- Image vulnerability scanning

## Hosting the UI

The API serves JSON only. Two workable arrangements:

1. **Static build served by Caddy** on the dashboard domain, with `/api/*` proxied
   to the API container. Simplest, no CORS.
2. **Separate origin.** Then you must configure CORS on the API — it is not
   enabled by default, deliberately, because the session cookie is the credential.

Prefer the first.

## Getting a real instance to build against

```bash
docker compose -f deploy/docker-compose.yml up -d
```

The setup token is printed to the API container's console on first run
(`docker logs airside-api`). Store provider can be SQLite for local work — set
`Airside__Store__Provider=Sqlite`.
