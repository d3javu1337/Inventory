#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
MOBILE_ROOT="$PROJECT_ROOT/mobile"
LOG_DIR="$PROJECT_ROOT/tmp/local-mvp"
MOBILE_PID_FILE="$LOG_DIR/mobile-web.pid"

usage() {
  cat <<'EOF'
Usage: ./deploy/start-local.sh [--skip-build]

Starts the local API, admin web, PostgreSQL, Redis, and mobile web.
Existing database and document volumes are preserved.
EOF
}

SKIP_BUILD=false
case "${1:-}" in
  "") ;;
  --skip-build) SKIP_BUILD=true ;;
  -h|--help) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac

for command in docker node npm curl; do
  command -v "$command" >/dev/null 2>&1 || {
    printf 'Required command not found: %s\n' "$command" >&2
    exit 1
  }
done

docker info --format '{{.ServerVersion}}' >/dev/null || {
  echo 'Start Docker Desktop / Docker Engine before running this script.' >&2
  exit 1
}

deploy_options=()
if [[ "$SKIP_BUILD" == true ]]; then
  deploy_options+=(--skip-build)
fi
bash "$SCRIPT_DIR/deploy.sh" "${deploy_options[@]}" up

wait_for_url() {
  local url="$1" timeout="$2" expected_text="${3:-}"
  local deadline=$((SECONDS + timeout)) body
  while (( SECONDS < deadline )); do
    if body="$(curl --fail --silent --show-error --max-time 3 "$url" 2>/dev/null)"; then
      if [[ -z "$expected_text" || "$body" == *"$expected_text"* ]]; then
        return 0
      fi
    fi
    sleep 1
  done
  return 1
}

VITE="$MOBILE_ROOT/node_modules/vite/bin/vite.js"
[[ -f "$VITE" ]] || {
  echo 'Install mobile dependencies first: cd mobile && npm ci' >&2
  exit 1
}

if curl --fail --silent --max-time 3 'http://127.0.0.1:5174/' >/dev/null 2>&1; then
  wait_for_url 'http://127.0.0.1:5174/' 3 'Asset Inventory' || {
    echo 'Port 5174 is serving another application; it was left untouched.' >&2
    exit 1
  }
else
  mkdir -p "$LOG_DIR"
  (
    cd "$MOBILE_ROOT"
    nohup node "$VITE" --config vite.config.ts --host 127.0.0.1 --port 5174 --strictPort \
      >"$LOG_DIR/mobile.stdout.log" 2>"$LOG_DIR/mobile.stderr.log" < /dev/null &
    echo "$!" > "$MOBILE_PID_FILE"
  )
  wait_for_url 'http://127.0.0.1:5174/' 30 'Asset Inventory' || {
    echo 'Mobile web did not become ready; inspect tmp/local-mvp/mobile.stderr.log.' >&2
    exit 1
  }
fi

cat <<'EOF'
Ready:
  API:       http://localhost:5147
  Admin web: http://localhost:5173
  Mobile web: http://127.0.0.1:5174

Existing database and report volumes were preserved. No seed or password reset was run.
EOF
