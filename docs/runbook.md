# Runbook: تشغيل خادم RouteBridge على VPS

الهدف: VPS Linux صغير (1 vCPU / 2 GB) يشغّل Caddy + FastAPI + PostgreSQL + نسخ احتياطي يومي بـ Docker Compose.

## 1. تجهيز الخادم (مرة واحدة)

```bash
# Ubuntu 24.04 LTS
apt update && apt -y upgrade
apt -y install ufw fail2ban unattended-upgrades ca-certificates curl
ufw default deny incoming && ufw default allow outgoing
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw --force enable
dpkg-reconfigure -plow unattended-upgrades
curl -fsSL https://get.docker.com | sh
```

- أنشئ سجل DNS من نوع A لـ `rb.example.com` يشير إلى IP الخادم قبل التشغيل الأول (Caddy يحتاجه لإصدار الشهادة).
- SSH بمفتاح فقط (`PasswordAuthentication no` في `sshd_config`).

## 2. النشر الأول

```bash
git clone <repo> /opt/routebridge && cd /opt/routebridge/deploy
cp .env.example .env
# عدّل DOMAIN وACME_EMAIL وكلمة مرور PostgreSQL وJWT_SECRET (openssl rand -base64 48)
docker compose up -d --build
docker compose logs -f api   # انتظر: "Application startup complete"
# المسؤول الأول
docker compose exec api python manage.py create-admin --email admin@example.com --password '…' --display-name "Admin"
curl -s https://rb.example.com/healthz     # فحص الحياة خارج /api/v1، بلا مصادقة
```

## 3. الترقية

```bash
cd /opt/routebridge && git pull
cd deploy && docker compose build api && docker compose up -d api
docker compose logs --tail=50 api
```
الترحيلات تُطبَّق تلقائيًا عند إقلاع `api` (`alembic upgrade head`). عند ترحيل خطير: خذ نسخة يدوية أولًا (`docker compose exec backup /usr/local/bin/backup.sh`).

## 4. النسخ الاحتياطي والاستعادة

- تلقائي: خدمة `backup` تكتب `deploy/backups/routebridge-<UTC>.sql.gz` يوميًا وتحذف ما يتجاوز `BACKUP_RETENTION_DAYS`.
- انسخ المجلد خارج الخادم (rsync أو S3) يوميًا؛ النسخة على القرص نفسه ليست خطة كوارث.
- الاستعادة: `./backup/restore.sh backups/routebridge-….sql.gz` (يوقف `api`، يعيد إنشاء القاعدة، يستورد، يشغّل `api`).
- اختبر الاستعادة على خادم staging مرة شهريًا.

## 5. المراقبة والسجلات

```bash
docker compose ps
docker compose logs --since 1h api
docker compose exec db psql -U routebridge -c "select status, count(*) from sessions group by 1;"
```
- `api` يعمل بـ worker واحد عمدًا (سجل اتصالات WebSocket في الذاكرة). لا ترفع `--workers` قبل إضافة Redis Pub/Sub.
- إعادة تشغيل `api` تقطع كل اتصالات WebSocket؛ العملاء يعيدون الاتصال بتراجع أسّي والجلسات النشطة تُنهى (بحسب العقد).

## 6. الأمان

- `/docs` معطّل في `ENV=prod`.
- لا تفتح منفذ PostgreSQL للخارج؛ الوصول عبر `docker compose exec db psql` فقط.
- دوّر `JWT_SECRET` فقط مع تسجيل خروج جماعي مخطط (يبطل كل access tokens فورًا).
- إن أُضيف Relay لاحقًا: خدمة منفصلة خلف Caddy بـ SNI مستقل على 443.
