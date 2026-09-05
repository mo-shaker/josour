# RouteBridge

تطبيق Windows يسمح لمستخدم (Guest) باستخدام اتصال الإنترنت الخاص بجهاز مستخدم آخر (Host) مؤقتًا وبموافقته الصريحة. المواقع المسموح بها فقط تمر عبر جهاز المضيف؛ الخادم المركزي للتحكم والتنسيق ولا يمرر أي بيانات تصفح.

## البنية

| المجلد | المحتوى |
|---|---|
| `backend/` | خادم التحكم: Python 3.12 + FastAPI + PostgreSQL + WebSockets |
| `client/` | حل .NET 8: مكتبات النفق والـ Proxy والمنفذ والمتصفح + تطبيق WPF |
| `deploy/` | Docker Compose للإنتاج (Caddy + API + PostgreSQL + نسخ احتياطي) |
| `docs/` | خطة التنفيذ، العقود (REST / WebSocket / بروتوكول القناة)، قرارات ADR، runbook |

**للبدء بتجربة حقيقية:** [docs/trial-setup-guide.md](docs/trial-setup-guide.md) — دليل من الصفر لنشر الخادم وتثبيت التطبيق على Windows 11.

الخطة الكاملة: [docs/RouteBridge-MVP-Implementation-Plan.md](docs/RouteBridge-MVP-Implementation-Plan.md)

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
python manage.py add-domain ifconfig.me
uvicorn app.main:app --reload
```

الاختبارات: `pytest` يعمل على SQLite تلقائيًا؛ ومع `DATABASE_URL_TEST` يعمل على PostgreSQL ويشمل اختبار الترحيلات. `ruff check . && ruff format --check .` قبل الدمج.

### العميل

```bash
cd client
dotnet build RouteBridge.sln
dotnet test RouteBridge.sln
```

تطبيق WPF (`RouteBridge.App`) يعمل على Windows فقط، لكنه يُبنى على macOS/Linux عبر `EnableWindowsTargeting`.

## المسارات

| المسار | النطاق |
|---|---|
| A: الخادم | `backend/` |
| B: الشبكات | `client/src/RouteBridge.{Core,Tunnel,Proxy,Egress,Browser}` + `client/tools/` |
| C: التطبيق | `client/src/RouteBridge.{Infrastructure,App}` |
| D: DevOps/QA | `deploy/`, `.github/`, `client/installer/`, `docs/runbook.md`, `docs/acceptance-checklist.md` |
