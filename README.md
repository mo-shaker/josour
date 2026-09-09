# Josour

تطبيق Windows يسمح لمستخدم (Guest) باستخدام اتصال الإنترنت الخاص بجهاز مستخدم آخر (Host) مؤقتًا وبموافقته الصريحة، فتصل المواقع التي لا تعمل إلا من داخل دولة المضيف. كل ما يطلبه متصفح العمل يخرج من عنوان المضيف ([ADR-0010](docs/decisions/0010-route-all-through-host.md))، وبقية تطبيقات الجهاز لا تتأثر.

بيانات التصفح مشفّرة بين الجهازين طرفًا لطرف: **الخادم لا يستطيع قراءتها**. وحين يتعذّر الاتصال المباشر — وهو الحال بين شبكتَي محمول — تمر البايتات المعتمة عبر Relay لا يفهمها ([ADR-0009](docs/decisions/0009-relay-default.md))، فتقوم الجلسة بلا أي إعداد على الراوتر أو جدار الحماية.

## البنية

| المجلد | المحتوى |
|---|---|
| `backend/` | خادم التحكم: Python 3.12 + FastAPI + PostgreSQL + WebSockets |
| `client/` | حل .NET 8: مكتبات النفق والـ Proxy والمنفذ والمتصفح + تطبيق WPF |
| `relay/` | خدمة الـ Relay: تزاوج طرفَي الجلسة وتضخّ بايتات معتمة ([ADR-0009](docs/decisions/0009-relay-default.md)) |
| `deploy/` | Docker Compose للإنتاج (Caddy + API + PostgreSQL + نسخ احتياطي) و**Relay مستقل** |
| `docs/` | خطة التنفيذ، العقود (REST / WebSocket / بروتوكول القناة)، قرارات ADR، runbook |

**للبدء بتجربة حقيقية:** [docs/trial-setup-guide.md](docs/trial-setup-guide.md) — دليل من الصفر لنشر الخادم وتثبيت التطبيق على Windows 11.

**الحالة:** جلسة كاملة بين جهازين على شبكتين مختلفتين تعمل منذ 2026-09-09 (معلم M2). آخر تقرير: [docs/status-week7.md](docs/status-week7.md).

الخطة الكاملة: [docs/Josour-MVP-Implementation-Plan.md](docs/Josour-MVP-Implementation-Plan.md)

## العقود المجمّدة

- [docs/api.md](docs/api.md): واجهات REST
- [docs/ws-protocol.md](docs/ws-protocol.md): رسائل WebSocket ودورة حياة الجلسة
- [docs/protocol.md](docs/protocol.md): القناة بين الجهازين (TLS، المصادقة، الإطارات)
- [docs/decisions/](docs/decisions/): قرارات ADR

أي تغيير في عقد يمر بمراجعة الطرفين المتأثرين وتحديث الوثيقة قبل الكود.

## التشغيل المحلي

### الخادم

```bash
cd backend
python3.12 -m venv .venv && source .venv/bin/activate
pip install -e ".[dev]"
cp .env.example .env            # عدّل JWT_SECRET (32 بايت على الأقل) وDATABASE_URL
docker compose -f docker-compose.dev.yml up -d db     # PostgreSQL 16 على المنفذ 5433
alembic upgrade head
python manage.py create-admin --email admin@example.com --password '…' --display-name Admin
uvicorn app.main:app --reload
```

الاختبارات: `pytest` يعمل على SQLite تلقائيًا؛ ومع `DATABASE_URL_TEST` يعمل على PostgreSQL ويشمل اختبار الترحيلات. `ruff check . && ruff format --check .` قبل الدمج.

### العميل

```bash
cd client
dotnet build Josour.sln
dotnet test Josour.sln
```

تطبيق WPF (`Josour.App`) يعمل على Windows فقط، لكنه يُبنى على macOS/Linux عبر `EnableWindowsTargeting`.

### الـ Relay

```bash
cd relay
python3.12 -m venv .venv && source .venv/bin/activate
pip install -e ".[dev]"
pytest -q && ruff check . && ruff format --check .
RELAY_SECRET="$(openssl rand -base64 48)" RELAY_PORT=8443 python -m relay
```

القياس: `pip install -e ".[bench]" && python -m bench` — التفصيل في [docs/performance-relay.md](docs/performance-relay.md).

## المسارات

| المسار | النطاق |
|---|---|
| A: الخادم | `backend/` |
| A: الخادم والـ Relay | `backend/`, `relay/` |
| B: الشبكات | `client/src/Josour.{Core,Tunnel,Proxy,Egress,Browser}` + `client/tools/` |
| C: التطبيق | `client/src/Josour.{Infrastructure,App}` |
| D: DevOps/QA | `deploy/`, `.github/`, `client/installer/`, `docs/runbook.md`, `docs/acceptance-checklist.md` |
