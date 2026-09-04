#!/usr/bin/env bash
# متطلبات الوثيقة 14 وprotocol.md §6: منع الوصول إلى الشبكة المحلية للمضيف
# وLocalhost والعناوين الداخلية، عبر الـ Proxy المحلي لمتصفح العمل.
# الاستخدام: ./check-egress-blocks.sh <proxy-port> [allowed-domain]
set -euo pipefail
PORT="${1:?usage: check-egress-blocks.sh <proxy-port> [allowed-domain]}"
ALLOWED="${2:-ifconfig.me}"
PROXY="http://127.0.0.1:$PORT"
fail=0

blocked() { # اسم، عنوان، المتوقع رفضه
  local label="$1" target="$2"
  local code
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 --proxy "$PROXY" "$target" 2>/dev/null || echo "000")
  if [ "$code" = "403" ] || [ "$code" = "000" ]; then
    echo "PASS  $label ($target) -> $code"
  else
    echo "FAIL  $label ($target) -> $code (كان يجب الرفض)" >&2
    fail=1
  fi
}

echo "== عناوين يجب أن تُرفض =="
blocked "localhost"        "http://localhost/"
blocked "loopback v4"      "http://127.0.0.1/"
blocked "loopback v6"      "http://[::1]/"
blocked "شبكة محلية"        "http://192.168.1.1/"
blocked "شبكة خاصة 10/8"    "http://10.0.0.1/"
blocked "link-local"       "http://169.254.169.254/"
blocked "CGNAT"            "http://100.64.0.1/"

echo "== موقع مسموح يجب أن يمر =="
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 --proxy "$PROXY" "https://$ALLOWED" || echo "000")
if [ "$code" = "200" ]; then echo "PASS  $ALLOWED -> 200"; else echo "WARN  $ALLOWED -> $code (تحقق أن الجلسة نشطة والنطاق في القائمة)"; fi

exit $fail
