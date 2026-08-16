# Notifications

Airside raises a notification when something needs an operator's attention — a
certificate expiring, an update that rolled back, a backup that failed. Channels
are how those leave the dashboard.

## Channels

| Kind | Destination | Secret |
|---|---|---|
| **Webhook** | Any HTTPS URL | An optional signing key |
| **Slack** | An incoming-webhook URL | The URL itself |
| **Email** | A recipient address | The SMTP password |

Each channel has its own **minimum severity**. The arrangement that works is a
chat channel that gets everything and an email address that only gets what would
justify waking somebody up; a single global threshold forces a choice between
noise and missing things.

Use **Test** after configuring one. It sends a real message rather than checking
connectivity, because everything worth getting wrong is downstream of connecting:
a Slack URL whose token was revoked, an SMTP account that authenticates but
cannot send as the configured from-address, a receiver that returns 200 for
anything.

## Routing rules

Beyond severity, a channel can be narrowed by what the notification is about.

```json
{
  "includeCodes": ["domain", "backup"],
  "excludeCodes": ["domain.awaiting_certificate"],
  "includeResourceKinds": ["domain"],
  "excludeResourceIds": ["01a0..."]
}
```

- **Codes match on segment boundaries.** `domain` matches
  `domain.certificate_expiring` but not `domainless.thing`. Trailing `.` or `*`
  are accepted and mean the same thing.
- **Empty means everything.** A channel with no rules sends everything it is
  offered, which is what every channel created before routing existed was already
  doing.
- **Exclude beats include.** "Everything under `domain`, except the expiry
  warnings" is a sentence people say; the reverse is not. So the two lists never
  have to be read in order.

### Check a rule before relying on it

`POST /api/v1/notification-channels/preview` runs a rule against the last hundred
notifications and shows which would have been sent, and why each of the rest
would not:

```json
{ "considered": 40, "wouldSend": 0,
  "warning": "This rule matches none of the last 40 notifications…" }
```

That warning is the point. A rule that accidentally matches nothing leaves a
channel quiet, and it looks identical to a channel that is simply waiting for its
first matching event — until the incident it was meant to report.

The channel list carries the same idea at a larger scale: if **no enabled channel
would receive an error**, it says so. Every channel can be individually sensible
and still leave no path for the thing that matters.

## Hours

A channel can also be given hours. This is the on-call case: one channel for the
working day, another for nights and weekends.

```json
{
  "timeZone": "Europe/London",
  "windows": [
    { "days": ["Monday","Tuesday","Wednesday","Thursday","Friday"], "start": "18:00", "end": "09:00" },
    { "days": ["Saturday","Sunday"], "start": "00:00", "end": "23:59" }
  ],
  "outside": "Defer",
  "alwaysDeliverAtOrAbove": "Error"
}
```

**The zone is an IANA identifier, not an offset.** An offset is right for half the
year and an hour wrong for the other half, and the wrong half is the one nobody
checks. Days and times are always the local ones: at 23:00 UTC on a Friday it is
already Saturday in Sydney, and a schedule written there means Sydney's Saturday.

**Windows may wrap midnight.** `18:00`–`09:00` is an overnight shift, and the day
it is checked against is the day it *started* — so at 02:00 on Saturday, a Friday
window still applies.

### What happens outside the window

- **`Defer`** — hold and send when the window opens. What "do not wake me" usually
  means: the alert still arrives, just at a civilised hour.
- **`Suppress`** — do not send at all. For a channel that is one half of a pair,
  where deferring would deliver everything twice.

A deferred notification that **resolves before its window opens is dropped**, not
delivered. An alert arriving at nine to announce a problem fixed at four in the
morning is worse than no alert.

`alwaysDeliverAtOrAbove` lets a severity ignore the schedule entirely. It is a
setting rather than a built-in rule, because a quiet-hours channel that pages
anyway is a surprise, and one that silently holds a production outage until
Monday is worse — whichever you meant, say so.

The preview endpoint accepts a schedule too, and answers not just whether a
notification would be sent but **when** it would arrive.

### Why a notification did not arrive

Filtered notifications are recorded, not dropped. Each carries the reason —
`'update.prepare_failed' does not match any code this channel sends` — so a
notification that was deliberately filtered is distinguishable from a channel
that is silently broken. Without that, the two look the same from the outside.

## Where Airside will not send

A webhook is a way to make **this server** issue an HTTP request, and this server
is a bad one to have that power over — it holds the Docker socket and shares a
network with Caddy's admin API, which is unauthenticated and can load
configuration that executes commands.

So these destinations are refused:

- **Loopback** — reaches Airside's own API and anything else deliberately unexposed.
- **Link-local (`169.254.0.0/16`)** — on a cloud instance this is the metadata
  service, which hands out IAM credentials to anything that can reach it.
- **Private ranges** (`10/8`, `172.16/12`, `192.168/16`, IPv6 unique-local) — from
  inside this server these are the container networks.
- **Reserved and carrier-grade NAT ranges.**

The check runs against the **resolved address at the moment the connection is
made**, not against the URL when it is saved. A hostname is not a destination:
`hooks.example.com` can resolve to `127.0.0.1`, and a URL check would pass it.
Redirects are not followed for the same reason — a `302` to the metadata service
is a destination chosen by the remote server, and it has not been checked.

### If your receiver is on your own network

Set `Airside:Notifications:AllowPrivateDestinations` to `true`. That opens the
private ranges only. **Loopback and link-local stay refused**, because neither is
ever a legitimate webhook target and both are catastrophic to reach — the switch
is deliberately not one setting for all of it.

## Webhook payloads

`POST` with a JSON body:

```json
{
  "id": "01a0...",
  "level": "error",
  "title": "Certificate expiring",
  "body": "app.example.com expires in 3 days.",
  "code": "domain.certificate_expiring",
  "resource": { "kind": "domain", "id": "01a0..." },
  "occurrences": 3,
  "firstSeenAt": "2026-08-14T04:00:00+00:00",
  "lastSeenAt": "2026-08-16T04:00:00+00:00",
  "instance": "prod-1",
  "url": "https://airside.example.com/notifications"
}
```

`occurrences` matters: notifications are deduplicated, so one message stands for
every time the condition was observed. Three is a blip; forty is a pattern.

### Verifying the signature

With a signing secret set, each request carries:

```
X-Airside-Timestamp: 1786852843
X-Airside-Signature: sha256=<hex>
```

The signature is `HMAC-SHA256(secret, "<timestamp>.<raw body>")`. Verify against
the **raw** body, before any JSON parsing — re-serialising changes the bytes.

The timestamp is inside the signed material rather than beside it, so it cannot
be edited without breaking the signature. Reject anything older than a few
minutes; that is what stops a captured request being replayed.

```python
expected = "sha256=" + hmac.new(
    secret.encode(), f"{timestamp}.{raw_body}".encode(), hashlib.sha256
).hexdigest()
hmac.compare_digest(expected, received)
```

## Slack

Create an incoming webhook in Slack and paste the URL into the **secret** field,
not the endpoint field. That URL *is* the credential — anyone holding it can post
to your channel — so Airside stores it encrypted and shows only the host.

## Email

Needs an SMTP host in settings, and usually a port, username, and from address:

```json
{ "host": "smtp.example.com", "port": "587", "from": "airside@example.com", "username": "apikey" }
```

TLS is negotiated automatically — 465 is implicit, 587 is STARTTLS. Setting
`"insecure": "true"` sends over plaintext, including the password, and exists only
for internal relays that genuinely do not speak TLS.

Outbound SMTP is **not** subject to the address rules above. An internal relay on
a private address is the normal arrangement, and an SMTP client cannot be turned
into a useful request against the metadata service or the proxy's admin API — the
protocols do not line up. The webhook rules exist because HTTP does.

## Retries

A failure that might change is retried with backoff — roughly 30s, 1m, 2m, 4m, up
to five attempts. A failure that will not change is not: a `401`, a bad URL, or a
refused destination is a configuration answer, and retrying spends attempts
reaching the same conclusion.

After ten consecutive failures a channel is muted for thirty minutes. A receiver
that has been down all day should not have a day's alerts delivered the moment it
returns, most of them about things long since fixed. **Notifications are still
recorded and visible in Airside while a channel is muted** — muting stops
delivery, not the notification.
