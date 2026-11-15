#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

usage() {
  cat <<'EOF'
Usage: ./deploy/check-local.sh [--health-only]

Checks local service availability. Without --health-only, also runs the
project's backend, web, and mobile verification commands.
EOF
}

HEALTH_ONLY=false
case "${1:-}" in
  "") ;;
  --health-only) HEALTH_ONLY=true ;;
  -h|--help) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac

for command in curl; do
  command -v "$command" >/dev/null 2>&1 || {
    printf 'Required command not found: %s\n' "$command" >&2
    exit 1
  }
done

check_url() {
  local url="$1"
  curl --fail --silent --show-error --max-time 5 "$url" >/dev/null || {
    printf 'Unavailable: %s\n' "$url" >&2
    exit 1
  }
  printf 'OK: %s\n' "$url"
}

check_url 'http://localhost:5147/health/ready'
check_url 'http://localhost:5173/'
check_url 'http://127.0.0.1:5174/'

if [[ "$HEALTH_ONLY" == true ]]; then
  exit 0
fi

run_check() {
  local directory="$1"
  shift
  printf '\n==> (%s) %s\n' "$directory" "$*"
  (cd "$PROJECT_ROOT/$directory" && "$@")
}

run_check backend dotnet test AssetInventory.sln --no-restore
run_check web npm test
run_check web npm run build
run_check web npm run typecheck:live
run_check web npm run test:e2e
run_check web npm run test:e2e:mobile-reports
run_check mobile npm test -- --runInBand
run_check mobile npm run typecheck
run_check mobile npm run lint
run_check mobile npm run build:web

echo 'All local checks passed. Real business data were not changed. Native Android was not launched.'
