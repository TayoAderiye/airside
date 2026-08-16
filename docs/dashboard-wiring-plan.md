# Wiring the dashboard to the API

The dashboard was built against an assumed contract before the API existed.
`lib/api/mock.ts` says so in its own header, and suggests swapping itself out
"once the v0.1 backend contract is available". That swap was done for about half
the screens and never finished.

This is the map and the order to finish it in.

## How it fails today

Not as a blank screen — as a screen that looks right. The two create flows are
the worst of it:

```
components/databases/create-form.tsx:72
  router.push(`/databases/new/provisioning?name=…&engine=…`)

components/applications/create-form.tsx:61
  setTimeout(() => router.push(`/applications/new/deploying?…`), 400)
```

Neither calls the API. Both navigate to a screen that renders a canned step list
and a fake log, so the operator watches a convincing progress animation and ends
with nothing provisioned, an empty list, and a detail page that 404s on a mock id.

## What is already real

Worth knowing before estimating any of this, because it is the hard part and it
is done:

- **`lib/api/client.ts`** — generated from the OpenAPI document, same-origin,
  throws typed `ApiError` carrying ProblemDetails.
- **`lib/api/jobs.ts` and `components/job-watcher.tsx`** — a correct SSE
  subscription to a job's `eventsUrl`, typed from the schema, resuming on
  `Last-Event-ID`. This is exactly what the create flows need and do not use.
- **`components/preflight-list.tsx`, `problem-banner.tsx`, `confirm-dialog.tsx`** —
  the three components the API's contract actually demands.

So most of this work is calling things that exist, not building new machinery.

## Screen by screen

| Screen | State | Endpoints it needs |
|---|---|---|
| Login, Setup | real | — |
| Overview | real | — |
| Applications list | real | — |
| Databases list | real | — |
| Query console | real | — |
| Domains, Notifications, Settings | real | — |
| **Database create + provisioning** | **fake** | `POST /api/v1/databases` → job SSE |
| **Application create + deploying** | **fake** | `POST /api/v1/applications`, `POST /{id}/deployments` → job SSE |
| Application detail | mock | `GET /api/v1/applications/{id}`, `/deployments`, `/environment`, `/databases`, `/domains` |
| Deployments | mock | `GET /api/v1/applications/{id}/deployments`, `POST /api/v1/deployments/{id}/rollback` |
| Backups | mock | `GET /api/v1/databases/{id}/backups`, `POST /api/v1/system/backups` |
| Audit | mock | `GET /api/v1/audit` |
| Access | mock | `GET /api/v1/users`, `/roles`, `/permissions` |
| Storage | mock | `GET /api/v1/volumes` |
| Servers | mock | `GET /api/v1/host`, `GET /api/v1/system/info` |
| Monitoring | mock | `GET /api/v1/workloads/{id}/metrics` |
| **Networks** | mock | **no API exists** |
| **Secrets** | mock | **no API exists as a standalone concept** |
| Log streaming | mock | `GET /api/v1/databases/{id}/logs/stream` (databases only) |

## The four gaps

Everything above the line is rewiring. These four are decisions.

**1. Networks has no API.** There is no `/api/v1/networks`. Airside creates a
network per workload and the isolation model is the most important thing it does,
so a read-only view of it is defensible — but it has to be built, endpoint first.

**2. Secrets has no API as a screen.** Secrets are not a flat list in Airside:
they are application environment variables (`/applications/{id}/environment`,
with a separate audited `/reveal`) and database credentials
(`/databases/{id}/credentials`). The mock screen invents a concept the product
does not have.

**3. Applications have no live log stream.** Databases do
(`/databases/{id}/logs/stream`); applications only have
`GET /api/v1/deployments/{id}/log`, which is a fetch, not a stream. Either the
application log view is fetch-and-poll, or the API grows a stream to match.

**4. System containers are not exposed.** `deploy/docker-compose.yml` claims they
"are visible in the UI". Nothing implements that — no endpoint lists them and no
screen shows them. Either build it or correct the comment; the comment is wrong
today either way.

## Order of work

Sequenced by how badly each failure misleads, not by size.

### Phase 1 — the flows that silently do nothing

The create flows, both of them. An operator who clicks "New database", watches a
progress bar, and gets nothing has been actively misled; every other mocked
screen is merely wrong.

- `create-form.tsx` (both) calls the API and receives `202 + JobAccepted`.
- The provisioning and deploying screens swap static `JobProgress` for live
  `JobWatcher`, which already exists and already works.
- Delete the query-string handoff — the job id travels instead.
- Failure path: the job's terminal event carries the error; show it rather than
  leaving the animation running.

This is the phase that turns the dashboard from a demonstration into a tool.

### Phase 2 — detail pages

Application detail, and deployments with rollback. Rollback is destructive and
needs the typed-confirmation treatment the API already returns metadata for.

### Phase 3 — read-only screens against endpoints that exist

Audit, Access, Storage, Servers, Backups, Monitoring. Mechanical: each is a
`client.GET` and a shape change. Monitoring needs the units note from the
frontend brief — CPU is nanoseconds per second, not a percentage.

### Phase 4 — the gaps

Take the four decisions above. Networks and Secrets should probably be resolved
by **removing the screens** in this release and adding them back when the API
supports them, rather than shipping two more pages of invented data.

### Phase 5 — make it impossible to regress

- **Delete `lib/api/mock.ts`.** Once nothing imports it, remove the file. While it
  exists, the next screen written in a hurry will import it.
- Sidebar footer reads a hardcoded `v0.2`; it should read the real version, which
  the UI already knows because the version gate baked it in.
- Correct the compose comment about system containers.

## Audit: what the API offers and the dashboard never asks for

Phases 1 to 5 replaced every mocked screen. That is not the same as covering the
API. Of 79 endpoints, **31 are never called**, and some of them are features the
README advertises on its front page.

Ordered by how badly the absence hurts.

> Counted by searching for each path in the source, including template-literal
> forms. An earlier pass reported 37 by looking only inside `client.GET(...)`
> calls, which missed paths passed as variables — database start, stop and
> restart are wired that way and were wrongly listed as absent.

### A database you create cannot be used

Nothing calls `/api/v1/databases/{id}/credentials`. Provision Postgres and there
is no way to obtain the password, the username or a connection string — the
detail screen fetches the database and offers to delete it, and that is all. The
database works; it is simply unreachable by anything you would want to connect
to it.

Rotation, reveal and revoke (`/credentials/rotate`, `/{credentialId}/reveal`,
`/{credentialId}/revoke`) are unreachable with it. "Credential rotation" is
listed on the README's feature list.

### An application cannot be attached to a database

`POST /api/v1/applications/{id}/databases` is never called, nor the detach
alongside it. Pairwise network isolation — the thing the README calls the most
important test in the suite — is configured through that one endpoint, so
through the dashboard an application can never reach a database at all.

### The second factor cannot be turned on

The login screen accepts a TOTP code and the API implements enrolment
(`/account/mfa`, `/enrol`, `/confirm`, `/disable`). No screen calls any of them,
so there is no way to enrol an authenticator. A security feature that exists on
the server and cannot be switched on.

### Self-update is unreachable

`/api/v1/system/updates` — the path that takes a backup, records each step to
disk and can roll back by digest — is never called. "Self-update with rollback"
is on the README's feature list. Upgrading means re-running `install.sh`, which
does none of those things.

### Databases cannot be resized

Start, stop, restart and delete are all wired on the detail screen. `/resize` is
not, so the only way to change a database's limits is to destroy it and provision
another.

### Domains are half wired

The screen binds a domain and runs pre-flight. It cannot upload a certificate,
delete a domain, set HSTS, move a domain between applications, re-run a check,
or create the apex-and-www pair — six endpoints, all present, none called.

### Private registries are unreachable

Four endpoints for storing and verifying registry credentials, none called, so
an image can only be pulled from somewhere anonymous.

### Smaller, and honestly optional

- Resource charts. `/workloads/{id}/metrics` and `/databases/{id}/metrics/stream`
  are unused, so Monitoring shows logs and no graphs.
- Deployment detail and build log (`/deployments/{id}`, `/{id}/log`).
- The jobs list and cancelling a running job.
- `/notifications/stream` — the screen fetches rather than subscribing.
- Query history, restore preview, notification channel test.

## What this does not cover

Nothing here changes the API except the four gaps, and three of those may be
resolved by removing a screen rather than adding an endpoint. The install path,
the container split and the version handshake are all done and are not affected.
