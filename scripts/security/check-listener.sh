#!/usr/bin/env bash
# Document requirement 14 and protocol.md §2: the host's listener accepts TLS only,
# stays silent until authentication succeeds, and closes any stranger's connection.
# Usage: ./check-listener.sh <host> <port>
set -euo pipefail
HOST="${1:?usage: check-listener.sh <host> <port>}"
PORT="${2:?usage: check-listener.sh <host> <port>}"
fail=0

echo "== 1. Port scan (TLS must appear, not a plaintext protocol) =="
if command -v nmap >/dev/null; then
  nmap -Pn -sV --version-light -p "$PORT" "$HOST" | sed -n '/PORT/,/^$/p'
else
  echo "skipped: nmap is not installed (brew install nmap)"
fi

echo "== 2. Random bytes with no TLS: the connection must close with no reply =="
resp=$(head -c 64 /dev/urandom | nc -w 5 "$HOST" "$PORT" 2>/dev/null | wc -c | tr -d ' ')
echo "bytes received: $resp"
[ "$resp" = "0" ] || { echo "FAIL: the listener answered unauthenticated junk" >&2; fail=1; }

echo "== 3. A TLS handshake then silence: the host must send nothing before AUTH1 =="
out=$(timeout_bin=$(command -v gtimeout || command -v timeout || true); \
      if [ -n "$timeout_bin" ]; then "$timeout_bin" 8 openssl s_client -connect "$HOST:$PORT" -servername josour -quiet 2>&1 </dev/null; \
      else openssl s_client -connect "$HOST:$PORT" -servername josour -quiet 2>&1 </dev/null & sleep 8; kill %1 2>/dev/null; fi || true)
echo "$out" | grep -iE 'protocol|cipher|verify|self.signed' | head -5
payload=$(echo "$out" | grep -vcE '^(CONNECTED|depth|verify|---|Protocol|Cipher|Server|No client|SSL|DONE|read|write|New,|Verification|Peer)' || true)
echo "payload lines after the handshake: $payload (expected 0)"

echo "== 4. The certificate is self-signed and temporary (no public CA) =="
echo | openssl s_client -connect "$HOST:$PORT" -servername josour 2>/dev/null | openssl x509 -noout -subject -issuer -dates 2>/dev/null || echo "could not fetch the certificate"

exit $fail
