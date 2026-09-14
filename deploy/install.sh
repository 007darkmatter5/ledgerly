#!/usr/bin/env bash
# Install or update Ledgerly on a Linux server with Docker.
#
#   curl -fsSL https://raw.githubusercontent.com/007darkmatter5/ledgerly/main/deploy/install.sh | sudo bash -s -- beta
#   curl -fsSL https://raw.githubusercontent.com/007darkmatter5/ledgerly/main/deploy/install.sh | sudo bash -s -- production
#
# Beta and Production are separate installs (own folder, port, database and keys), so testing Beta never
# touches Production data. Each run pulls the newest image for the channel, backs up the database, restarts
# the container and waits until the app reports healthy.

set -euo pipefail

IMAGE="ghcr.io/007darkmatter5/ledgerly"
APP_UID=1654          # the non-root user inside the image
KEEP_BACKUPS=10
HEALTH_TIMEOUT=90

usage() {
    cat <<'EOF'
Usage: install.sh <beta|production> [options]

Options:
  --port <port>        Host port (default: 5006 for beta, 5005 for production; remembered)
  --bind <address>     Host address to listen on, e.g. 127.0.0.1 behind a reverse proxy (default: all; remembered)
  --version <version>  Install a specific version, e.g. 1.0.42 or 1.0.43-beta (default: newest for the channel)
  --dir <path>         Base folder (default: /opt/ledgerly)
  -h, --help           Show this help

Examples:
  install.sh beta                       # install or update Beta on port 5006
  install.sh production                 # install or update Production on port 5005
  install.sh production --version 1.0.41  # roll back Production to an earlier version
EOF
}

say()  { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mError:\033[0m %s\n' "$*" >&2; exit 1; }

# --- Arguments ----------------------------------------------------------------------------------------

case "${1:-}" in
    beta|Beta)                   channel=beta;       label=Beta;       default_port=5006 ;;
    production|Production|prod)  channel=production; label=Production; default_port=5005 ;;
    -h|--help)                   usage; exit 0 ;;
    *)                           usage >&2; exit 1 ;;
esac
shift

port=""; bind=""; version=""; base_dir="/opt/ledgerly"
while [ $# -gt 0 ]; do
    case "$1" in
        --port)    [ $# -ge 2 ] || die "--port needs a value";    port="$2";     shift 2 ;;
        --bind)    [ $# -ge 2 ] || die "--bind needs a value";    bind="$2";     shift 2 ;;
        --version) [ $# -ge 2 ] || die "--version needs a value"; version="$2";  shift 2 ;;
        --dir)     [ $# -ge 2 ] || die "--dir needs a value";     base_dir="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *)         die "Unknown option: $1 (see --help)" ;;
    esac
done

[ -z "$port" ] || [[ "$port" =~ ^[0-9]+$ && "$port" -ge 1 && "$port" -le 65535 ]] || die "Port must be a number between 1 and 65535."

# --- Requirements -------------------------------------------------------------------------------------

command -v docker >/dev/null 2>&1 || die "Docker isn't installed. See https://docs.docker.com/engine/install/"
docker compose version >/dev/null 2>&1 || die "The Docker Compose plugin isn't installed. See https://docs.docker.com/compose/install/linux/"
command -v curl >/dev/null 2>&1 || die "curl isn't installed."
docker info >/dev/null 2>&1 || die "Can't reach Docker. Run with sudo, or add your user to the docker group."

dir="$base_dir/$channel"
container="ledgerly-$channel"

# --- Folders and settings -----------------------------------------------------------------------------

say "Installing Ledgerly $label into $dir"
mkdir -p "$dir/data" "$dir/keys" "$dir/backups"

# The container runs as UID $APP_UID and must be able to write its database and keys.
if [ "$(id -u)" -eq 0 ]; then
    chown "$APP_UID:$APP_UID" "$dir/data" "$dir/keys"
    chmod 700 "$dir/keys"
elif [ "$(stat -c %u "$dir/data")" != "$APP_UID" ] || [ "$(stat -c %u "$dir/keys")" != "$APP_UID" ]; then
    die "$dir/data and $dir/keys must be owned by UID $APP_UID. Run this script with sudo."
fi

# Remember port and bind address between runs; a new --port/--bind replaces them.
if [ -f "$dir/.env" ]; then
    saved_port=$(sed -n 's/^LEDGERLY_PORT=//p' "$dir/.env")
    saved_bind=$(sed -n 's/^LEDGERLY_BIND=//p' "$dir/.env")
fi
port="${port:-${saved_port:-$default_port}}"
bind="${bind:-${saved_bind:-0.0.0.0}}"
tag="${version:-$channel}"

cat > "$dir/.env" <<EOF
# Managed by install.sh; changes here are overwritten. Put your own settings in ledgerly.env.
LEDGERLY_ID=$channel
LEDGERLY_CHANNEL=$label
LEDGERLY_TAG=$tag
LEDGERLY_PORT=$port
LEDGERLY_BIND=$bind
EOF

# Your own settings: created once and never overwritten.
if [ ! -f "$dir/ledgerly.env" ]; then
    cat > "$dir/ledgerly.env" <<'EOF'
# Extra settings for this Ledgerly install (KEY=value). Restart after changing:
#   docker compose up -d
#
# Behind a reverse proxy (nginx, Caddy, Traefik) that handles HTTPS:
# ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
#
# Email is normally set up by an admin in the app (Account settings > Email server).
# Settings here override the app's, e.g.:
# Email__Host=smtp.example.com
# Email__Port=587
# Email__Security=StartTls
# Email__Username=you@example.com
# Email__Password=your-app-password
# Email__FromAddress=you@example.com
# Email__PublicBaseUrl=https://ledgerly.example.com
EOF
    chmod 600 "$dir/ledgerly.env"
fi

cat > "$dir/docker-compose.yml" <<EOF
# Managed by install.sh; changes here are overwritten. Put your own settings in ledgerly.env.
name: ledgerly-\${LEDGERLY_ID}

services:
  ledgerly:
    image: $IMAGE:\${LEDGERLY_TAG}
    container_name: ledgerly-\${LEDGERLY_ID}
    restart: unless-stopped
    ports:
      - "\${LEDGERLY_BIND}:\${LEDGERLY_PORT}:8080"
    environment:
      Ledgerly__Channel: \${LEDGERLY_CHANNEL}
    env_file:
      - ledgerly.env
    volumes:
      - ./data:/data
      - ./keys:/keys
EOF

cd "$dir"

# --- Update -------------------------------------------------------------------------------------------

say "Downloading $IMAGE:$tag"
if ! docker compose pull; then
    die "Couldn't download $IMAGE:$tag. Check the version exists (https://github.com/007darkmatter5/ledgerly/releases) and that the server can reach ghcr.io."
fi

previous_version=""
if [ -n "$(docker ps -aq --filter "name=^${container}$")" ]; then
    previous_version=$(docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' "$container" 2>/dev/null || true)
    say "Stopping the running version${previous_version:+ ($previous_version)}"
    docker compose stop
fi

if [ -f data/ledgerly.db ]; then
    backup="backups/$(date +%Y%m%d-%H%M%S)${previous_version:+-$previous_version}"
    say "Backing up the database to $dir/$backup"
    mkdir -p "$backup"
    cp -p data/ledgerly.db* "$backup/"

    # Keep only the newest backups.
    ls -1dt backups/*/ 2>/dev/null | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm -rf
fi

say "Starting Ledgerly $label"
docker compose up -d --remove-orphans

# --- Health check -------------------------------------------------------------------------------------

say "Waiting for Ledgerly to start (database updates run on first start)"
health_host="$bind"
[ "$health_host" = "0.0.0.0" ] && health_host="127.0.0.1"
for _ in $(seq 1 "$HEALTH_TIMEOUT"); do
    if curl -fsS "http://$health_host:$port/healthz" >/dev/null 2>&1; then
        installed_version=$(docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' "$container" 2>/dev/null || true)
        say "Ledgerly $label ${installed_version:-} is running at http://$(hostname -f 2>/dev/null || hostname):$port"
        [ -z "$previous_version" ] || [ "$previous_version" = "$installed_version" ] || echo "    Updated from $previous_version."
        exit 0
    fi
    sleep 1
done

warn "Ledgerly didn't report healthy within ${HEALTH_TIMEOUT}s. Recent logs:"
docker compose logs --tail 60 ledgerly || true
cat >&2 <<EOF

To roll back:
  1. Install the previous version:  install.sh $channel --version ${previous_version:-<version>}
  2. If the database was already upgraded, restore the backup while stopped:
       cd $dir && docker compose stop && cp -p ${backup:-backups/<latest>}/ledgerly.db* data/ && docker compose up -d
EOF
exit 1
