# deploy/

What the installer writes to `/opt/airside`.

## `caddy.json`

Caddy's bootstrap configuration. **It has no comments, and cannot have any** —
Caddy parses its JSON config strictly and rejects unknown fields, so the usual
`"//": "…"` convention makes the proxy fail to start with
`json: unknown field "//"`. The reasoning therefore lives here.

**`admin.listen` is `0.0.0.0:2019`, and that is deliberate.** Caddy runs in its
own container, so binding to `localhost` would mean the proxy's own loopback,
which `airside-api` cannot reach. The port is **never published to the host** —
see `docker-compose.yml`, which lists 80 and 443 and nothing else. Caddy's admin
API is unauthenticated and can load configuration that executes commands, so a
published 2019 is equivalent to handing over the machine. `origins` restricts
which Host headers it will answer to as a second layer.

**The server is named `airside`, not Caddy's generated `srv0`.** Airside adds a
route per domain through the admin API, and the path those calls use
(`/config/apps/http/servers/airside/routes`) depends on that name being stable.

**`routes` starts empty and stays empty in this file.** Airside owns routing, and
the database is the source of truth: routes added through the admin API do not
survive the container being replaced, so `ProxyReconciliationService` reasserts
every domain at startup and every two minutes. That is also what brings routing
back after a proxy update rather than leaving a silently empty config.

**Listening on `:443` is what enables automatic HTTPS.** Caddy obtains a
certificate for each host matcher in a route, so adding a domain is what triggers
issuance. It cannot succeed until that hostname's DNS resolves to this host.
