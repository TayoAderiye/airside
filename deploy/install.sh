#!/usr/bin/env sh
# Airside installer.
#
#   curl -fsSL https://airside.dev/install | sh
#
# The install path is the product: on a fresh box this must produce a working
# dashboard with no further steps. It is written in POSIX sh so it runs on a
# minimal image without bash, and it is idempotent — re-running it upgrades
# configuration in place rather than starting over.

set -eu

AIRSIDE_ROOT="${AIRSIDE_ROOT:-/opt/airside}"
AIRSIDE_DATA="${AIRSIDE_DATA:-/var/lib/airside}"
# Keep in step with VersionPrefix in Directory.Build.props.
AIRSIDE_VERSION="${AIRSIDE_VERSION:-0.1.2}"
AIRSIDE_STORE_PROVIDER="${AIRSIDE_STORE_PROVIDER:-Postgres}"

log()  { printf '  %s\n' "$*"; }
warn() { printf '  ! %s\n' "$*" >&2; }
die()  { printf '\n  Install failed: %s\n\n' "$*" >&2; exit 1; }

# --- preflight ---------------------------------------------------------------

[ "$(id -u)" -eq 0 ] || die "run as root (this configures the Docker daemon and writes to $AIRSIDE_DATA)"

case "$(uname -s)" in
  Linux) ;;
  *) die "Airside manages a Linux host; this is $(uname -s)" ;;
esac

command -v curl >/dev/null 2>&1 || die "curl is required (this fetches the compose file and, if needed, Docker)"

if ! command -v docker >/dev/null 2>&1; then
  log "Docker is not installed. Installing it from get.docker.com..."
  curl -fsSL https://get.docker.com | sh || die "Docker installation failed"
fi

docker info >/dev/null 2>&1 || die "the Docker daemon is not running"

# --- Docker address pool -----------------------------------------------------
#
# Docker's default default-address-pools allocates a /16 per bridge network from
# 172.17.0.0/16 through 172.31.0.0/16 — about 15 networks in total. Airside gives
# every workload its own network, so without this the fifteenth workload fails
# with "could not find an available, non-overlapping IPv4 address pool", which
# reads as a bug in Airside rather than a daemon default.
#
# This must happen before anything starts, and it restarts the daemon.

DAEMON_JSON=/etc/docker/daemon.json

if ! grep -q 'default-address-pools' "$DAEMON_JSON" 2>/dev/null; then
  log "Configuring Docker's address pool (4096 networks instead of ~15)"
  mkdir -p /etc/docker

  if [ -s "$DAEMON_JSON" ]; then
    cp "$DAEMON_JSON" "$DAEMON_JSON.airside.bak"
    warn "existing $DAEMON_JSON backed up to $DAEMON_JSON.airside.bak"

    if command -v python3 >/dev/null 2>&1; then
      python3 - "$DAEMON_JSON" <<'PY'
import json, sys
path = sys.argv[1]
with open(path) as f:
    config = json.load(f)
config["default-address-pools"] = [{"base": "172.16.0.0/12", "size": 24}]
with open(path, "w") as f:
    json.dump(config, f, indent=2)
PY
    else
      die "$DAEMON_JSON exists and python3 is unavailable to merge it safely; add \
'\"default-address-pools\": [{\"base\": \"172.16.0.0/12\", \"size\": 24}]' by hand and re-run"
    fi
  else
    cat > "$DAEMON_JSON" <<'JSON'
{
  "default-address-pools": [
    { "base": "172.16.0.0/12", "size": 24 }
  ]
}
JSON
  fi

  log "Restarting Docker to apply it"
  systemctl restart docker || die "could not restart Docker"
  sleep 3
  docker info >/dev/null 2>&1 || die "Docker did not come back after the restart"
fi

# --- directories -------------------------------------------------------------

log "Creating $AIRSIDE_DATA"
mkdir -p "$AIRSIDE_DATA/keys" "$AIRSIDE_DATA/data" "$AIRSIDE_DATA/volumes" "$AIRSIDE_DATA/backups"

# The Data Protection key ring decrypts every stored secret. Nothing but the
# control plane has any business reading it.
chmod 700 "$AIRSIDE_DATA/keys"

mkdir -p "$AIRSIDE_ROOT"

# --- compose files -----------------------------------------------------------
#
# Fetched at the tag being installed rather than from main. The compose file
# names the images, their mounts and their environment, so pairing one commit's
# compose file with another commit's images is how a container comes up missing
# a volume it now depends on.
#
# Without this the directory has no compose file at all and the next step fails
# with "no configuration file provided: not found" — which reads as a broken
# Docker install rather than a missing download.

REPO_RAW="${AIRSIDE_REPO_RAW:-https://raw.githubusercontent.com/TayoAderiye/airside}"

fetch_deploy_file() {
  name="$1"
  dest="$AIRSIDE_ROOT/$name"

  curl -fsSL "$REPO_RAW/v$AIRSIDE_VERSION/deploy/$name" -o "$dest.new" \
    || die "could not fetch deploy/$name for v$AIRSIDE_VERSION from $REPO_RAW"

  # A local edit is kept rather than silently discarded. An operator who changed
  # a memory limit or added a mount should find it next to the new file, not
  # discover on the next restart that it is gone.
  if [ -f "$dest" ] && ! cmp -s "$dest.new" "$dest"; then
    cp "$dest" "$dest.airside.bak"
    warn "$name differed from the published v$AIRSIDE_VERSION; your copy is at $name.airside.bak"
  fi

  mv "$dest.new" "$dest"
}

log "Fetching the compose files for $AIRSIDE_VERSION"
fetch_deploy_file docker-compose.yml
fetch_deploy_file caddy.json

# --- configuration -----------------------------------------------------------

ENV_FILE="$AIRSIDE_ROOT/.env"

if [ ! -f "$ENV_FILE" ]; then
  log "Generating credentials"

  DB_PASSWORD="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')"
  DOCKER_GID="$(getent group docker | cut -d: -f3)"
  DOCKER_GID="${DOCKER_GID:-999}"

  if [ "$AIRSIDE_STORE_PROVIDER" = "Sqlite" ]; then
    STORE_CONNECTION="Data Source=$AIRSIDE_DATA/data/airside.db"
  else
    STORE_CONNECTION="Host=airside-db;Database=airside;Username=airside;Password=$DB_PASSWORD"
  fi

  umask 077
  cat > "$ENV_FILE" <<EOF
AIRSIDE_VERSION=$AIRSIDE_VERSION
AIRSIDE_DB_PASSWORD=$DB_PASSWORD
AIRSIDE_DOCKER_GID=$DOCKER_GID
AIRSIDE_STORE_PROVIDER=$AIRSIDE_STORE_PROVIDER
AIRSIDE_STORE_CONNECTION=$STORE_CONNECTION
EOF
  chmod 600 "$ENV_FILE"
else
  log "Keeping existing configuration in $ENV_FILE"

  # Only the version moves on an upgrade. Regenerating the database password
  # would lock the API out of its own store.
  sed -i "s/^AIRSIDE_VERSION=.*/AIRSIDE_VERSION=$AIRSIDE_VERSION/" "$ENV_FILE"
fi

# --- start -------------------------------------------------------------------

log "Starting Airside $AIRSIDE_VERSION"
cd "$AIRSIDE_ROOT"
docker compose pull --quiet
docker compose up -d

log "Waiting for the control plane to become healthy"
i=0
while [ "$i" -lt 60 ]; do
  # The API binary's own probe. The runtime image is chiselled and contains no
  # shell, wget or curl, so anything else here fails not because the API is
  # unhealthy but because the command does not exist — and the installer then
  # reports a healthy install as a failed one, every time, on every host.
  if docker exec airside-api dotnet Airside.Api.dll --health >/dev/null 2>&1; then
    break
  fi
  i=$((i + 1))
  sleep 2
done

[ "$i" -lt 60 ] || die "the API did not become healthy — check 'docker logs airside-api'"

log "Waiting for the dashboard"
i=0
while [ "$i" -lt 30 ]; do
  # 127.0.0.1 rather than localhost: that name also resolves to ::1, which
  # busybox wget tries first and Next does not listen on.
  if docker exec airside-ui wget -qO- http://127.0.0.1:3000/login >/dev/null 2>&1; then
    break
  fi
  i=$((i + 1))
  sleep 2
done

# Not fatal. The API is up and the CLI works, so an operator can still act; a
# dashboard that is slow to start is not a reason to fail an otherwise good
# install and leave them thinking nothing came up.
[ "$i" -lt 30 ] || log "the dashboard has not answered yet — check 'docker logs airside-ui'"

IP="$(hostname -I 2>/dev/null | awk '{print $1}')"

printf '\n'
printf '  Airside is running.\n\n'
printf '    Dashboard:  http://%s\n' "${IP:-<this host>}"
printf '\n'
printf '  The one-time setup token is in the API log:\n\n'
printf '    docker logs airside-api | head -40\n'
printf '\n'
# Said plainly rather than buried: Let'\''s Encrypt does not issue certificates for
# bare IP addresses, so there is no publicly trusted certificate until a domain
# is attached, and the first login crosses the network in the clear.
printf '  Until you attach a domain, the dashboard has no TLS certificate and\n'
printf '  your password will cross the network unencrypted. Attach a domain as\n'
printf '  the first thing you do, or reach the box over an SSH tunnel:\n\n'
printf '    ssh -L 8080:localhost:80 %s\n' "${IP:-<this host>}"
printf '\n'
