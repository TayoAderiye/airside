# Domains and TLS

How Airside gets a certificate onto a hostname, and what to do when the platform
in front of it has opinions of its own.

## Choosing a mode

The mode is a required choice with no default. That is deliberate: the wrong
default here produces the most opaque failure this kind of tool has. Someone who
meant to terminate TLS at CloudFront gets automatic issuance, Caddy spends days
failing a challenge that can never succeed, and nothing in the interface says
why.

| Mode | Who issues | Who renews | Use it when |
|---|---|---|---|
| **Automatic** | Let's Encrypt, over HTTP-01 | Airside | DNS points at this server and port 80 is open. The normal case. |
| **Manual** | You, elsewhere | **You** | You have a certificate from your own CA, an internal PKI, or a provider Airside cannot talk to. |
| **External** | A load balancer or CDN | That thing | TLS ends before traffic reaches this host. |
| **Internal** | Airside's own CA | Airside | Development, air-gapped installs, or a hostname that could never pass a public challenge. Browsers will warn. |
| Automatic over DNS | — | — | Not implemented. It is the only way to cover a wildcard. |
| On demand | — | — | Not implemented. |

## Why a domain fails to get a certificate

Pre-flight runs before Airside lets Caddy attempt anything, and every check
reports what it found alongside what it expected. Run it any time from the
domain's **Re-check** action.

The failures worth knowing about in advance:

**The name does not resolve here.** The commonest problem by a wide margin. DNS
is resolved through a public resolver rather than this host's, on purpose — a
server with split-horizon DNS, an internal resolver, or a stale `/etc/hosts`
entry will report a hostname resolving perfectly while the rest of the internet,
including the certificate authority, sees something else.

**Port 80 is closed.** The HTTP-01 challenge is delivered over port 80 even for a
site that only ever serves HTTPS. A cloud firewall or security group is invisible
from inside the host, so if the challenge fails while everything else looks
correct, check the provider console as well as `ufw` or `iptables`.

**An AAAA record that does not work.** This one costs people hours. Certificate
authorities *prefer* IPv6 when an AAAA record exists, so a host with a perfect A
record and a stale or unroutable AAAA record fails validation every time, and the
error mentions nothing about IPv6. Airside probes the AAAA address and blocks
with an explanation rather than letting the challenge fail silently.

**A CAA record that excludes Let's Encrypt.** If the domain publishes a CAA
record, issuance fails at the authority no matter how correct DNS and the
firewall are.

**Proxied DNS.** An orange-clouded Cloudflare record points at a CDN edge, so the
challenge is answered there and never reaches this host. Either grey-cloud the
record until the certificate is issued, or use **External** mode.

## Rate limits

Let's Encrypt limits are easy for a provisioning tool to trip, and the lockout
looks exactly like the original problem — so it reads as "still broken" rather
than "stop and wait". Airside keeps its own ledger, because ACME offers no way to
ask how much headroom is left; the server tells you only by refusing.

The limits that matter: 50 certificates per registered domain per week, 5
duplicates of the same hostname per week, 5 failed validations per hostname per
hour, and 300 new orders per account per three hours.

**If you are debugging a stubborn domain, turn on staging mode** in settings.
Staging has limits high enough to iterate against. Certificates issued from it
are trusted by no browser, so Airside marks those domains explicitly untrusted
and they must be re-issued against production before they count as healthy.

## Uploading your own certificate

Manual mode takes a PEM chain and an unencrypted private key. The key is
encrypted at rest with the same Data Protection key ring that protects database
credentials, is never returned by any endpoint, and never appears in a log.

Airside checks the upload before storing it, because every one of these otherwise
arrives as an identical, uninformative TLS handshake failure in a browser:

- the key actually belongs to the certificate
- the chain is complete, in order, and free of expired intermediates
- the hostname is in the SAN list — note that `*.example.com` does **not** cover
  `example.com`, which is a genuinely common surprise
- the key is strong enough for current browsers

**Nothing renews an uploaded certificate.** Airside warns at 30, 14, 7, 3, and 1
days, and on the day itself, and shows the countdown on the domain list rather
than hiding it in a detail panel. Replacing one is a hot reload — upload, and the
new certificate is served on the next handshake with no restart and no dropped
connection.

## Behind Cloudflare

Cloudflare's SSL mode decides what Airside should be set to.

| Cloudflare mode | What it does | Set Airside to |
|---|---|---|
| **Flexible** | Encrypts browser→Cloudflare only. The hop to your server is plain HTTP. | **External** — but be aware this leg is unencrypted. |
| **Full** | Encrypts to your server without validating the certificate. | **Internal**. A self-signed certificate is all Full requires. |
| **Full (Strict)** | Encrypts and validates. | **Automatic** with the record grey-clouded during issuance, or **Manual** with a Cloudflare Origin Certificate. |

**Redirect loops** are the classic Cloudflare failure: Flexible mode plus an
application that redirects HTTP to HTTPS. Cloudflare sends plain HTTP to the
origin, the origin redirects to HTTPS, Cloudflare serves that redirect, and round
it goes. Either switch Cloudflare to Full, or stop the application redirecting.

**Protect the origin.** If your server is still reachable directly on port 80 from
the internet, traffic can bypass Cloudflare's WAF entirely. Restrict the firewall
to Cloudflare's ranges, or use an Origin Certificate with authenticated origin
pull.

## AWS

**A public ACM certificate cannot be exported.** AWS keeps the private key;
`get-certificate` returns the chain and not the key. There is no way to bring one
onto this host, and no amount of configuration changes that.

So the options are:

- Keep the ALB or CloudFront in front and use **External**.
- Point DNS at an Elastic IP and use **Automatic**. You lose the ACM certificate
  and gain automatic renewal.
- **AWS Private CA** certificates *can* be exported, with
  `acm export-certificate --passphrase`, and used in **Manual** mode. Only clients
  with the private root installed will trust them, which suits internal tooling
  behind a VPN and not a public endpoint.

Two more AWS specifics: a certificate used with CloudFront must live in
`us-east-1` regardless of where the instance is, and **without an Elastic IP a
stop/start changes the public address** and breaks every A record pointing at it.

## Other providers

- **Azure** — App Service Managed Certificates cannot be exported. Key Vault
  certificates can, as PFX; convert to PEM and use Manual.
- **GCP** — Google-managed certificates cannot be exported. Self-managed uploads
  are yours and can be reused.
- **Hetzner, DigitalOcean, Vultr** — no managed certificate layer, so Automatic is
  the normal path. Check the provider firewall is not blocking port 80.

## HSTS

Off by default, and available per domain.

**Think hard before enabling preload.** Submitting a domain to the browser preload
list is effectively irreversible: removal takes months and requires the domain to
keep serving valid HTTPS throughout. If you may ever need plain HTTP on that
hostname — or on any subdomain of it, including ones Airside does not manage —
preload will prevent it in every major browser. Airside requires you to type the
hostname to confirm, and refuses preload without `includeSubDomains`, because
browsers ignore the directive otherwise.

## Backups

Caddy keeps its certificates and its ACME account key in the `airside-caddy-data`
volume. **Include it in your backups**, alongside the database and the Data
Protection key ring.

If that volume is lost, Caddy comes back believing it has never issued anything
and requests every certificate again at once. On a host with more than a handful
of domains that trips the weekly limit part-way through, leaving some hostnames
without a certificate for a week — and the symptom looks like a broken install
rather than a missing volume. Airside warns at startup when the volume is missing
or empty while domains expect automatic certificates.

## The proxy admin API

Caddy's admin API is unauthenticated and can load configuration that executes
commands. Anyone who reaches it controls every route on the machine.

It binds `0.0.0.0:2019` **inside the proxy container** and is never published to
the host. That is not the same as binding loopback, and it is not a weaker
position: Caddy runs in its own container, so binding its loopback would put the
API out of reach of the control plane that has to drive it. What matters is that
only `airside-internal` can reach the port, and an integration test asserts that a
workload network cannot.

Do not publish port 2019.
