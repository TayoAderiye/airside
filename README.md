# Airside

A self-hosted control plane for **one Linux server**. Deploy applications, run
databases, get TLS certificates, and see what the machine is doing — without
learning Kubernetes and without a monthly bill.

Airside manages Docker on the host it runs on. It is not a cluster orchestrator,
and it is not trying to become one.

> **Status: 0.1.4, pre-release.** Complete through the roadmap, heavily tested,
> and installed successfully on a fresh Linux host — which found four bugs that
> are now fixed. Read [Status](#status) before putting anything real on it.

---

## What it does

- **Applications** — deploy from an image, a Dockerfile, or a Git repository.
  Zero-downtime cutover: the new container has to pass a health check before the
  proxy moves and the old one stops.
- **Databases** — PostgreSQL, MySQL, MongoDB, and Redis, with backups, restores,
  credential rotation, and a query console.
- **Domains and TLS** — automatic certificates, or bring your own, or terminate
  upstream. Pre-flight checks tell you *why* a certificate cannot be issued
  before Let's Encrypt starts refusing.
- **Networking** — every workload gets its own network. An application can only
  reach a database it has been explicitly attached to.
- **Operations** — resource metrics, notifications by webhook, Slack, or email,
  self-update with rollback, and a CLI that works when the API does not.
- **A dashboard** — a Next.js UI in [`frontend/`](frontend), shipped as its own
  image beside the API and served on the same hostname. It checks its version
  against the API before rendering, and refuses rather than show you screens it
  may be reading wrongly.

## Install

### What you need first

| | |
|---|---|
| OS | Linux, x86-64 or arm64. Ubuntu 24.04 is what it is tested on. Airside manages a Linux host and refuses to run elsewhere. |
| Memory | 2 GB. It runs in 1 GB with nothing deployed, but Postgres, the API, the dashboard and the proxy all have to fit. |
| Disk | 20 GB. The four images plus a database volume do not fit comfortably in the 8 GB a cloud image usually defaults to. |
| Ports | **80 and 443 reachable from the internet.** Port 80 is not optional — the certificate challenge uses it even for a site that only ever serves HTTPS. |
| Access | Root, or a user with `sudo`. |

If you are on EC2, put the instance in a **public** subnet with auto-assign public
IP enabled. A private subnet gives you a box that cannot download Docker and that
you cannot reach.

### 1. Install

As root, on the host:

```bash
curl -fsSL https://raw.githubusercontent.com/TayoAderiye/airside/main/deploy/install.sh | sh
```

It installs Docker if absent, widens Docker's address pool (the default allows
about fifteen networks, and Airside gives every workload its own), writes
`/opt/airside`, creates `/var/lib/airside`, and starts four containers. Two to
three minutes on a small instance, most of it pulling images.

### 2. Take the setup token

The installer prints it. If it scrolled past:

```bash
sudo docker logs airside-api | grep -A4 "setup token"
```

It works once and expires in 24 hours.

### 3. Reach the dashboard

**Not over the public IP.** There is no certificate for a bare address — Let's
Encrypt does not issue for IPs — so a password typed there crosses the network in
clear text. Tunnel from your own machine instead:

```bash
ssh -i <your-key.pem> -L 8080:localhost:80 ubuntu@<host>
```

Leave it running and open `http://localhost:8080`.

### 4. Create the administrator

Paste the token, choose an email and password, name the instance. There is no
default account: the first administrator is created here or not at all.

### 5. Attach a domain

This is the step that makes the tunnel unnecessary, and it is worth doing
immediately.

Point an `A` record at the host, then in **Settings → dashboard domain** type the
hostname to confirm it. On AWS, allocate an **Elastic IP** first and point the
record at that — an instance's default public address is released on stop/start,
which breaks the record and the certificate renewal with it.
[Route 53, step by step](docs/domains-and-tls.md#pointing-a-route-53-domain-at-airside). Airside runs pre-flight checks *before* switching — that
the name resolves to this host, and that CAA records permit issuance — and
refuses with the specific reason rather than letting the certificate request fail
silently afterwards.

Caddy then obtains the certificate over HTTP-01, which is why port 80 has to be
open. Once a dashboard domain exists, the bare IP stops serving the dashboard.

After that it is `https://your-domain`, no tunnel. The dashboard and the API
share one hostname — Caddy routes `/api` to the API and everything else to the
dashboard — so there is nothing separate to expose.

### If it goes wrong

```bash
sudo docker compose -f /opt/airside/docker-compose.yml ps
```

```bash
sudo docker logs airside-api --tail 50
```

The `airside` CLI on the host keeps working when the API does not — `airside
status` reads the update state file directly.

## Security model, in plain terms

Read this before installing. It is short and it matters.

**Airside holds the Docker socket, which is root-equivalent on the host.** There
is no way to manage containers without it. Anyone who can administer Airside can
run arbitrary containers, and therefore do anything on the machine. Treat an
Airside login as a root login.

What Airside does with that:

- Workloads are **isolated pairwise**. Each gets its own Docker network, and an
  application can reach a database only through an explicit attachment. There is
  an integration test for this, and it is the most important test in the suite.
- **Nothing builds a shell command from user input.** Container exec takes an
  argument vector; there is no string-formatting helper for commands, because the
  way to keep a rule is to remove the tool that breaks it.
- **Arbitrary host bind mounts are inexpressible.** The type that describes a
  mount has no host-path variant, so "mount `/` into a container" cannot be
  written, not merely rejected.
- **Secrets are encrypted at rest** with ASP.NET Data Protection — database
  passwords, certificate private keys, registry logins, SMTP passwords, TOTP
  seeds. They are masked in every response, never logged, and revealing one is an
  audited action.
- **Outbound webhooks are checked at connect time** against the resolved address,
  and cannot reach loopback, private ranges, or the cloud metadata service. A
  webhook is a way to make the server issue a request, and this server is a bad
  one to have that power over.
- **The proxy's admin API is never published.** Caddy's admin port is
  unauthenticated and can load configuration that executes commands; only the
  API container shares a network with it.

To report a vulnerability, see [SECURITY.md](SECURITY.md).

## Documentation

| | |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | How it fits together, and why |
| [docs/frontend-brief.md](docs/frontend-brief.md) | Building a UI against this API |
| [docs/dashboard-wiring-plan.md](docs/dashboard-wiring-plan.md) | How the dashboard was connected, and what is left |
| [docs/domains-and-tls.md](docs/domains-and-tls.md) | TLS modes, why issuance fails, cloud provider specifics |
| [docs/notifications.md](docs/notifications.md) | Channels, routing rules, hours, webhook signatures |
| [docs/image-variants.md](docs/image-variants.md) | Alpine vs Debian database images |
| [CONVENTIONS.md](CONVENTIONS.md) | Code conventions, including the container security rules |
| [ROADMAP.md](ROADMAP.md) | What was built, and what was found by running it |
| [API-CONTRACT.md](API-CONTRACT.md) | The HTTP contract |

## Status

Everything on the roadmap is built, with 459 tests passing. Much of it has been
verified against real infrastructure rather than mocks — real Docker containers,
a real Caddy, a real private registry, a real SMTP server, real DNS.

**The installer has now been run on a fresh Linux host** — Ubuntu 24.04 on EC2,
2 GB, x86-64. It found four bugs, all fixed:

- It never downloaded the compose file it then ran, so every install stopped at
  `docker compose pull`.
- Both health checks called `wget`, which the chiselled runtime image does not
  contain — so the installer ended every install with *"the API did not become
  healthy"* on a host where the API was fine.
- The control plane did not own its own data directory, which surfaced as a 500
  on the first login and nowhere earlier.
- A fresh install served a blank page, because routes are only created when a
  domain is bound and a new box has none.

Two of those reported success and failed later, somewhere that did not name the
cause. [ROADMAP.md](ROADMAP.md) has the detail.

So the install path works, but 0.1.3 has not yet been run end to end on a clean
box — only its parts have. Expect rough edges, and the first install is still
the most useful thing to report on.

Known gaps, recorded rather than hidden:

- The job dispatcher is a single reader loop, so one slow operation delays the
  others.
- A multi-stage Docker build pulling private base images from two different
  registries gets a credential for the first only.
- Notification schedules have no holiday calendar — "weekdays" includes
  Christmas Day.
- Metric retention is hourly rollups for 90 days, with no configuration.
- Proxy reconciliation restores application routes but not the dashboard's own,
  so a replaced proxy container needs the dashboard domain set again by hand.

## Building from source

```bash
git clone https://github.com/TayoAderiye/airside.git
cd airside
dotnet test
```

Needs the .NET SDK named in `global.json`. The Docker-backed integration tests
skip when no daemon is available; set `AIRSIDE_REQUIRE_DOCKER=1` to make that a
failure instead, which is what CI does.

The dashboard is separate, and needs Node 24 and pnpm:

```bash
cd frontend
pnpm install
pnpm build
```

`pnpm dev` wants an API to talk to — copy `.env.example` to `.env.local` and
point `AIRSIDE_API_URL` at one. It is read at build time, not run time.

## Licence

[Apache 2.0](LICENSE). Bundled third-party data is attributed in [NOTICE](NOTICE).
