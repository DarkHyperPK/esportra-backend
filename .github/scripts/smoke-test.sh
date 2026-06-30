#!/usr/bin/env bash
# Smoke tests for deployed API — validates critical paths are responding.
# Usage: bash smoke-test.sh <API_URL>
set -euo pipefail

API_URL="${1:?Usage: smoke-test.sh <API_URL>}"
API_URL="${API_URL%/}"

FAIL=0
PASS=0
TOTAL=0

smoke() {
  local name="$1"
  local method="${2:-GET}"
  local path="$3"
  local expected_status="${4:-200}"

  TOTAL=$((TOTAL + 1))
  local status
  status=$(curl --silent --output /dev/null --write-out "%{http_code}" \
    --max-time 15 -X "$method" "${API_URL}${path}") || status="000"

  if [[ "$status" == "$expected_status" ]]; then
    echo "  ✓ ${name} (${method} ${path}) → ${status}"
    PASS=$((PASS + 1))
  else
    echo "  ✗ ${name} (${method} ${path}) → ${status} (expected ${expected_status})"
    FAIL=$((FAIL + 1))
  fi
}

echo "Smoke testing: ${API_URL}"
echo ""

echo "── Health ──"
smoke "Liveness probe"       GET "/health/live"
smoke "Readiness probe"      GET "/health/ready"

echo ""
echo "── Public API ──"
smoke "Game catalog"         GET "/api/games"
smoke "Public tournaments"   GET "/api/tournaments?page=1&pageSize=1"
smoke "Sitemap index"        GET "/api/sitemap/index"

echo ""
echo "── Auth boundary ──"
smoke "Profile (unauthed)"   GET "/api/profiles/me" "401"
smoke "Admin (unauthed)"     GET "/api/admin/users" "401"

echo ""
echo "── SignalR negotiate ──"
smoke "Notifications hub"    POST "/hubs/notifications/negotiate?negotiateVersion=1" "401"
smoke "Bracket hub"          POST "/hubs/bracket/negotiate?negotiateVersion=1" "401"

echo ""
echo "══════════════════════════════"
echo "Results: ${PASS}/${TOTAL} passed, ${FAIL} failed"

if [[ $FAIL -gt 0 ]]; then
  echo "::error title=Smoke tests failed::${FAIL} of ${TOTAL} smoke tests failed."
  exit 1
fi

echo "All smoke tests passed."
