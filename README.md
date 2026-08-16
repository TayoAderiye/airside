# Airside

A self-hosted control plane for **one Linux server**. Deploy applications, run
databases, get TLS certificates, and see what the machine is doing — without
learning Kubernetes and without a monthly bill.

Airside manages Docker on the host it runs on. It is not a cluster orchestrator,
and it is not trying to become one.

> **Status: 0.1.0, pre-release.** The code is complete through the roadmap and
> heavily tested — but the installer has not yet been run end to end on a fresh
> Linux host. See [Status](#status) before putting anything real on it.

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

## Install

On a fresh Ubuntu or Debian host, as root:

```bash
curl -fsSL https://raw.githubusercontent.com/TayoAderiye/airside/main/deploy/install.sh | sh
```

The installer configures the Docker daemon, writes `/opt/airside`, and starts the
control plane. It prints a one-time setup token on the console — that is how you
create the first administrator.

**Requirements**

| | |
|---|---|
| OS | Linux (x86-64). Airside manages a Linux host and will refuse to run elsewhere. |
| Memory | 1 GB before any workload; 2 GB is a comfortable starting point. |
| Ports | **80 and 443 open to the internet.** Port 80 is not optional — the ACME challenge uses it even for a site that only serves HTTPS. |
| DNS | A domain with an A record pointing at the host, if you want real certificates. |

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
| [docs/domains-and-tls.md](docs/domains-and-tls.md) | TLS modes, why issuance fails, cloud provider specifics |
| [docs/notifications.md](docs/notifications.md) | Channels, routing rules, hours, webhook signatures |
| [docs/image-variants.md](docs/image-variants.md) | Alpine vs Debian database images |
| [CONVENTIONS.md](CONVENTIONS.md) | Code conventions, including the container security rules |
| [ROADMAP.md](ROADMAP.md) | What was built, and what was found by running it |
| [API-CONTRACT.md](API-CONTRACT.md) | The HTTP contract |

## Status

Everything on the roadmap is built, with 442 tests passing. Much of it has been
verified against real infrastructure rather than mocks — real Docker containers,
a real Caddy, a real private registry, a real SMTP server, real DNS.

**What has not happened yet:** the installer has never been run on a fresh Linux
host. All verification so far has been on macOS with Docker Desktop, driving the
API directly. `install.sh` writes `/etc/docker/daemon.json`, creates
`/var/lib/airside`, and detects the host's docker group — none of which has
executed once.

So: expect the first install to break. If you are trying this early, that is the
part worth reporting.

Known gaps, recorded rather than hidden:

- The job dispatcher is a single reader loop, so one slow operation delays the
  others.
- A multi-stage Docker build pulling private base images from two different
  registries gets a credential for the first only.
- Notification schedules have no holiday calendar — "weekdays" includes
  Christmas Day.
- Metric retention is hourly rollups for 90 days, with no configuration.

## Building from source

```bash
git clone https://github.com/TayoAderiye/airside.git
cd airside
dotnet test
```

Needs the .NET SDK named in `global.json`. The Docker-backed integration tests
skip when no daemon is available; set `AIRSIDE_REQUIRE_DOCKER=1` to make that a
failure instead, which is what CI does.

## Licence

[Apache 2.0](LICENSE). Bundled third-party data is attributed in [NOTICE](NOTICE).
