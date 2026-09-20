#!/usr/bin/env bash
# Document requirement 14 and protocol.md §6: stopping access to the host's local network,
# to localhost and to internal addresses, through the work browser's local proxy.
# Usage: ./check-egress-blocks.sh <proxy-port> [allowed-domain]
set -euo pipefail
PORT="${1:?usage: check-egress-blocks.sh <proxy-port> [allowed-domain]}"
ALLOWED="${2:-ifconfig.me}"
PROXY="http://127.0.0.1:$PORT"
fail=0

blocked() { # a label, a target, expected to be refused
  local label="$1" target="$2"
  local code
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 --proxy "$PROXY" "$target" 2>/dev/null || echo "000")
  if [ "$code" = "403" ] || [ "$code" = "000" ]; then
    echo "PASS  $label ($target) -> $code"
  else
    echo "FAIL  $label ($target) -> $code (it should have been refused)" >&2
    fail=1
  fi
}

echo "== addresses that must be refused =="
blocked "localhost"        "http://localhost/"
blocked "loopback v4"      "http://127.0.0.1/"
blocked "loopback v6"      "http://[::1]/"
blocked "local network"    "http://192.168.1.1/"
blocked "private 10/8"     "http://10.0.0.1/"
blocked "link-local"       "http://169.254.169.254/"
blocked "CGNAT"            "http://100.64.0.1/"

echo "== an allowed site that must get through =="
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 --proxy "$PROXY" "https://$ALLOWED" || echo "000")
if [ "$code" = "200" ]; then echo "PASS  $ALLOWED -> 200"; else echo "WARN  $ALLOWED -> $code (check the session is active and the domain is in the list)"; fi

exit $fail
