#!/usr/bin/env bash
# Document requirements 6.8, 14 and 15: no browsing content, no passwords and no tokens are logged.
# It searches the server's and the client's logs for URLs, HTTP headers and secrets.
# Usage: ./check-logs-clean.sh [extra log path ...]
set -euo pipefail
fail=0
sources=()
if command -v docker >/dev/null && docker compose ls >/dev/null 2>&1; then sources+=("docker"); fi
for p in "$@"; do sources+=("$p"); done

scan() { # the label, the text
  local label="$1" text="$2"
  # patterns that may never appear
  local hits
  hits=$(printf '%s' "$text" | grep -inE 'Authorization: *Bearer|password"? *[:=]|refresh_token"? *[:=] *"[A-Za-z0-9_-]{20}|secret_b64"? *[:=] *"[A-Za-z0-9+/]{20}|Set-Cookie|CONNECT [a-z0-9.-]+:443|GET https?://' | head -10 || true)
  if [ -n "$hits" ]; then
    echo "FAIL  $label contains what may not be logged:" >&2
    printf '%s\n' "$hits" >&2
    fail=1
  else
    echo "PASS  $label is clean"
  fi
}

for s in "${sources[@]}"; do
  if [ "$s" = "docker" ]; then
    scan "api logs" "$(cd "$(dirname "$0")/../../deploy" 2>/dev/null && docker compose logs --no-color --tail 2000 api 2>/dev/null || true)"
  elif [ -e "$s" ]; then
    scan "$s" "$(cat "$s" 2>/dev/null | tail -5000)"
  fi
done
exit $fail
