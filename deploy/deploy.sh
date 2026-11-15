#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<'EOF'
Usage: deploy/deploy.sh [options] [up|check|status|logs|stop|seed]

up      Build and start the Compose services, then check API and web.
check   Check the published API and web endpoints.
status  Show all containers for the selected Compose project.
logs    Show the last 100 log lines for each service.
stop    Stop the containers and keep the database and report volumes.
seed    Load the 1,000-asset fixture into the selected local test stack.

Options:
  --skip-build          Use existing images (up only).
  --project-name NAME   Select the Compose project.
  --env-file PATH       Read Compose variables from PATH.
  --override PATH       Apply an additional Compose file. Repeatable.
  -h, --help            Show this help.
EOF
}

fail() { printf '%s\n' "$*" >&2; exit 1; }
usage_error() { printf '%s\n' "$*" >&2; usage >&2; exit 2; }

compose=(docker compose -f "$SCRIPT_DIR/docker-compose.yml")
build=--build
env_file=""
while (( $# )); do
  case "$1" in
    --project-name|--env-file|--override)
      [[ -n "${2:-}" && "$2" != -* ]] || usage_error "Missing value for $1."
      case "$1" in
        --project-name) compose+=(--project-name "$2") ;;
        --env-file) env_file="$2" ;;
        --override) compose+=(-f "$2") ;;
      esac
      shift 2
      ;;
    --skip-build) build=--no-build; shift ;;
    -h|--help) usage; exit 0 ;;
    -*) usage_error "Unknown option: $1" ;;
    *) break ;;
  esac
done

action="${1:-up}"
(( $# <= 1 )) || usage_error "Unexpected argument: $2"
case "$action" in
  up|check|status|logs|stop|seed) ;;
  *) usage_error "Unknown command: $action" ;;
esac
[[ "$build" == --build || "$action" == up ]] || usage_error '--skip-build requires up.'
if [[ -z "$env_file" && -f "$SCRIPT_DIR/.env" ]]; then
  env_file="$SCRIPT_DIR/.env"
fi
if [[ -n "$env_file" ]]; then
  [[ -f "$env_file" ]] || fail "Compose environment file not found: $env_file"
  compose+=(--env-file "$env_file")
fi

command -v docker >/dev/null 2>&1 || fail 'Docker is required.'
if [[ "$action" == up || "$action" == check ]]; then
  command -v curl >/dev/null 2>&1 || fail 'curl is required for HTTP checks.'
fi

check_endpoint() {
  local service="$1" target="$2" path="$3" binding url
  binding="$("${compose[@]}" port "$service" "$target")"
  [[ -n "$binding" ]] || fail "No published port for $service."
  case "$binding" in
    0.0.0.0:*) binding="127.0.0.1:${binding##*:}" ;;
    \[::\]:*) binding="[::1]:${binding##*:}" ;;
  esac
  url="http://$binding$path"
  curl --noproxy '*' --fail --silent --show-error --max-time 5 --output /dev/null "$url"
  printf 'OK: %s %s\n' "$service" "$url"
}

seed_test_data() {
  local file
  for file in repair-test-data.sql init-test-data.sql; do
    printf 'Applying %s...\n' "$file"
    "${compose[@]}" exec -T postgres sh -ec '
      export PGCLIENTENCODING=UTF8
      exec psql -X --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --file=-
    ' < "$SCRIPT_DIR/$file"
  done
  printf '%s\n' 'Test data loaded. Cached catalogs can take up to 30 seconds to refresh.'
}

case "$action" in
  up)
    if ! "${compose[@]}" up --detach "$build" --wait --wait-timeout 120; then
      "${compose[@]}" ps --all >&2 || true
      exit 1
    fi
    check_endpoint api 8080 /health/ready
    check_endpoint web 80 /
    ;;
  check)
    check_endpoint api 8080 /health/ready
    check_endpoint web 80 /
    ;;
  status) "${compose[@]}" ps --all ;;
  logs) "${compose[@]}" logs --tail 100 ;;
  stop) "${compose[@]}" stop ;;
  seed) seed_test_data ;;
esac
