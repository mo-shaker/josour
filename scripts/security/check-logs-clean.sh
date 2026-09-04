#!/usr/bin/env bash
# متطلبات الوثيقة 6.8 و14 و15: لا يُسجَّل محتوى تصفح ولا كلمات مرور ولا رموز.
# يفحص سجلات الخادم وسجلات العميل عن عناوين URL ورؤوس HTTP وأسرار.
# الاستخدام: ./check-logs-clean.sh [مسار سجلات إضافي ...]
set -euo pipefail
fail=0
sources=()
if command -v docker >/dev/null && docker compose ls >/dev/null 2>&1; then sources+=("docker"); fi
for p in "$@"; do sources+=("$p"); done

scan() { # الاسم، النص
  local label="$1" text="$2"
  # أنماط لا يجوز ظهورها إطلاقًا
  local hits
  hits=$(printf '%s' "$text" | grep -inE 'Authorization: *Bearer|password"? *[:=]|refresh_token"? *[:=] *"[A-Za-z0-9_-]{20}|secret_b64"? *[:=] *"[A-Za-z0-9+/]{20}|Set-Cookie|CONNECT [a-z0-9.-]+:443|GET https?://' | head -10 || true)
  if [ -n "$hits" ]; then
    echo "FAIL  $label يحتوي ما لا يجوز تسجيله:" >&2
    printf '%s\n' "$hits" >&2
    fail=1
  else
    echo "PASS  $label نظيف"
  fi
}

for s in "${sources[@]}"; do
  if [ "$s" = "docker" ]; then
    scan "سجلات api" "$(cd "$(dirname "$0")/../../deploy" 2>/dev/null && docker compose logs --no-color --tail 2000 api 2>/dev/null || true)"
  elif [ -e "$s" ]; then
    scan "$s" "$(cat "$s" 2>/dev/null | tail -5000)"
  fi
done
exit $fail
