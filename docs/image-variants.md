# Image variants

Airside runs each database from an official upstream image. Where upstream
publishes more than one base image, you choose which one at creation.

| Engine | Variants | Default |
|---|---|---|
| PostgreSQL | Alpine, Debian | **Alpine** |
| Redis | Alpine, Debian | **Alpine** |
| MySQL | Debian only | Debian |
| MongoDB | Debian only | Debian |

MySQL discontinued its Alpine images upstream, and MongoDB has never published
one. For those two there is no choice to make, so Airside shows no control —
rather than offering an option that would resolve to a tag that does not exist.

## What Alpine changes

**A different C library.** Alpine builds against musl rather than glibc. For the
database process itself this is unremarkable — Postgres and Redis are both built
and tested against musl upstream — but anything that loads a native shared object
into the engine has to have been compiled for musl too. That is the practical
limit, and it is the reason to pick Debian.

**A different userland.** Coreutils, `sh`, and friends come from BusyBox. The
commands are the same ones you know with fewer options. It matters when you exec
into a container to poke at something; it does not matter to the engine.

**A much smaller image.** Typically a third to a fifth of the Debian equivalent.
On a single server pulling several databases, that is real disk and real time on
first provision.

## What Alpine does not change

**Backups stay portable.** A logical backup is the engine's own dump format —
`pg_dump` output is `pg_dump` output regardless of the base image. A backup taken
from an Alpine Postgres restores into a Debian Postgres of the same major version
and the reverse, because the format is defined by the engine, not by the
distribution underneath it. The same is true of a Redis RDB snapshot.

**Data files stay compatible for the same major version.** The on-disk format is
the engine's.

**Configuration, tuning, and the wire protocol are identical.** Nothing your
application connects to changes.

## When to choose Debian

Pick Debian when you need an extension that ships as a compiled shared object and
is not built for musl. In practice that means:

- **PostGIS** — heavy native dependencies, and the packaging assumes glibc.
- **pgvector** — available for Alpine, but often easier to obtain prebuilt for
  Debian.
- Anything installed through `apt` from a PostgreSQL APT repository.
- Tooling that expects a full GNU userland inside the container.

If you are not sure, the honest answer is that most people never hit the
difference — which is why Alpine is the default.

## When to use a custom image instead

Neither variant carries extensions preinstalled. If you need pgvector or PostGIS,
the usual route is an image that already has it:

```
pgvector/pgvector:pg16
postgis/postgis:16-3.4
```

Supply that as the custom image when creating the database. Doing so bypasses
variant selection entirely, and the database is flagged as using a custom image.
Airside cannot see inside it, so from that point on it will not offer version or
variant guidance for that workload — it runs what you asked for.

## The variant cannot be changed afterwards

Fixed at creation, and Airside rejects any attempt to change it.

This is not caution for its own sake. The two builds differ in libc and in the
layout the engine initialises into its data volume the first time it starts.
Pointing a Debian build at a volume an Alpine build created is not a
configuration change; it is a migration, and not one either engine supports.

To move an existing database to the other variant:

1. Create a new database on the variant you want.
2. Back up the old one.
3. Restore that backup into the new one — the dump is portable, which is exactly
   why this works.
4. Repoint your applications and delete the old database when you are satisfied.

## How the image is pinned

The tag chosen from version and variant is only used once, on first provision.
Airside records the resolved digest — `RepoDigests[0]` — and everything
afterwards resolves through that digest rather than the tag.

That matters because a tag moves. `postgres:16-alpine` today and the same tag in
six months are different builds. Without the digest, a container recreated after
a restart or a resize would silently land on a newer build than the one that
initialised the data. Restore, rollback, and drift detection all compare digests
for the same reason.
