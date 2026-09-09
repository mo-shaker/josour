#!/usr/bin/env bash
# متطلبات الوثيقة 14 وprotocol.md §2: مستمع المضيف يقبل TLS فقط،
# يصمت حتى تنجح المصادقة، ويغلق أي اتصال غريب.
# الاستخدام: ./check-listener.sh <host> <port>
set -euo pipefail
HOST="${1:?usage: check-listener.sh <host> <port>}"
PORT="${2:?usage: check-listener.sh <host> <port>}"
fail=0

echo "== 1. مسح المنفذ (يجب أن يظهر TLS لا بروتوكولًا مكشوفًا) =="
if command -v nmap >/dev/null; then
  nmap -Pn -sV --version-light -p "$PORT" "$HOST" | sed -n '/PORT/,/^$/p'
else
  echo "تخطٍّ: nmap غير مثبّت (brew install nmap)"
fi

echo "== 2. بايتات عشوائية بلا TLS: يجب أن يُغلق الاتصال بلا رد =="
resp=$(head -c 64 /dev/urandom | nc -w 5 "$HOST" "$PORT" 2>/dev/null | wc -c | tr -d ' ')
echo "بايتات مستلمة: $resp"
[ "$resp" = "0" ] || { echo "FAIL: المستمع ردّ على حشو غير مصادَق" >&2; fail=1; }

echo "== 3. مصافحة TLS ثم صمت: المضيف يجب ألا يرسل شيئًا قبل AUTH1 =="
out=$(timeout_bin=$(command -v gtimeout || command -v timeout || true); \
      if [ -n "$timeout_bin" ]; then "$timeout_bin" 8 openssl s_client -connect "$HOST:$PORT" -servername josour -quiet 2>&1 </dev/null; \
      else openssl s_client -connect "$HOST:$PORT" -servername josour -quiet 2>&1 </dev/null & sleep 8; kill %1 2>/dev/null; fi || true)
echo "$out" | grep -iE 'protocol|cipher|verify|self.signed' | head -5
payload=$(echo "$out" | grep -vcE '^(CONNECTED|depth|verify|---|Protocol|Cipher|Server|No client|SSL|DONE|read|write|New,|Verification|Peer)' || true)
echo "أسطر حمولة بعد المصافحة: $payload (المتوقع 0)"

echo "== 4. الشهادة ذاتية التوقيع ومؤقتة (لا CA عامة) =="
echo | openssl s_client -connect "$HOST:$PORT" -servername josour 2>/dev/null | openssl x509 -noout -subject -issuer -dates 2>/dev/null || echo "تعذّر جلب الشهادة"

exit $fail
