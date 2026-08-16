# Security

## Reporting a vulnerability

Please report privately, not as a public issue.

**Use [GitHub's private vulnerability reporting](https://github.com/TayoAderiye/airside/security/advisories/new)**
on this repository. It is enabled, so the form above is the whole process — the
report stays private between you and the maintainers until there is a fix to
publish, and it keeps the discussion attached to the code rather than in
somebody's inbox.

Useful to include: what you did, what happened, and what you expected. A minimal
reproduction is worth more than a scanner report. If you have a suggested fix,
say so — but please do not open a public pull request for a security issue, since
the diff discloses the problem before there is anything to upgrade to.

This is a small project with no security team and no bounty programme. Expect an
acknowledgement within a few days, and please allow reasonable time for a fix
before disclosing. If you do not hear back within two weeks, chase by commenting
on your own advisory — an overlooked notification is far more likely than a
decision to ignore you.

## What is in scope

Airside is a control plane for a single machine. The interesting boundaries are:

- Privilege escalation from a lower-privileged Airside role to a higher one, or
  to the host.
- Escaping the workload isolation — reaching a database from an application that
  has no attachment to it.
- Reading a stored secret without the permission and audit trail that is supposed
  to gate it.
- Making the server issue requests it should refuse (webhook destinations,
  registry endpoints, DNS).
- Anything that reaches Caddy's admin API from outside the control plane.
- Authentication and session handling, including the second factor.

## What is not a vulnerability

**Airside holds the Docker socket, and that is root-equivalent on the host.**
There is no way to manage containers without it. It follows that:

- An administrator can run arbitrary containers, and therefore run arbitrary code
  on the host. This is what the product does. An Airside administrator login is a
  root login, and the documentation says so plainly.
- A user with permission to deploy can deploy a malicious image. Airside does not
  scan images and does not claim to.
- Setting `Airside:Notifications:AllowPrivateDestinations` permits webhooks to
  private networks, by explicit configuration. Loopback and link-local stay
  refused even then.
- Running the API container as root, or publishing Caddy's admin port, or
  bind-mounting the socket into a workload, are all things an operator can do to
  themselves. Airside's defaults do none of them.

If you are unsure which side of that line something falls on, report it. A
question costs less than a missed issue.

## Design decisions worth knowing about

These are deliberate, and each one is a place where the safe-looking choice is
the wrong one.

**The proxy admin API binds `0.0.0.0` inside its container.** It is never
published to the host. Caddy runs in its own container, so binding loopback would
put the API out of reach of the control plane that has to drive it — the
protection is network isolation plus an unpublished port, and there is an
integration test asserting a workload network cannot reach it.

**Outbound webhook destinations are checked when the socket opens**, against the
resolved address, and the connection is then made to the address that was checked.
Validating a URL when it is saved is not a check: DNS can answer publicly during
configuration and `127.0.0.1` when the webhook fires. Redirects are not followed.

**Forwarded headers are trusted only from the proxy's own network** unless an
operator names more. Blanket trust is the tempting shortcut and it lets anyone
who can reach the origin forge a client address into the audit log — which is
worse than having no audit log, because it looks authoritative.

**Secrets are encrypted with a key ring on a host volume.** Lose
`/var/lib/airside/keys` and every stored credential becomes undecryptable. Back it
up with the database, in the same archive — Airside's system backup does exactly
that, and refuses to pretend a backup without it is complete.

## Supported versions

Pre-1.0. Only the latest release gets fixes. There are no backports yet.
