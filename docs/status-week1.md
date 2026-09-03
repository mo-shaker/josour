# حالة الأسبوع 1 (بتاريخ 2026-09-04)

## ما اكتمل وتحقق منه

| المسار | المخرج | التحقق |
|---|---|---|
| D | المستودع (git مهيأ بلا commits)، README، `.gitignore`، `.editorconfig`، CI للخادم (Postgres + ruff + pytest + docker build) وللعميل (Windows + Linux)، `deploy/` (Compose + Caddy + نسخ احتياطي واستعادة)، `docs/runbook.md`، `docs/acceptance-checklist.md`، `client/installer/RouteBridge.iss` مع قاعدة Firewall | `docker compose config` وCaddy validate ناجحان |
| العقود | `docs/api.md`، `docs/ws-protocol.md`، `docs/protocol.md`، ست وثائق ADR، واجهات `RouteBridge.Core` (IControlChannel، ITunnelSession، ITunnelTransport، IBrowserSession، ISecretStore، رسائل WS) | مجمّدة؛ التغيير عبر مراجعة الطرفين |
| A | نواة الخادم: النماذج كاملة (13 جدولًا) + ترحيل Alembic، argon2id + JWT + refresh rotation مع كشف إعادة الاستخدام، auth/me/hosts/domains/probe/healthz، قفل الحساب، `security_events`، CLI (create-admin, create-user, add-domain, list-domains)، Dockerfile | ruff نظيف؛ 77 اختبارًا + 1 متخطى على SQLite؛ 78 على PostgreSQL 16 شاملة الترحيل و`alembic check`؛ الصورة تُبنى |
| B | `Core`: AllowlistMatcher، IpRangePolicy. `Tunnel`: SessionCertificate، TlsChannel (Pinning + حد أدنى 1.2)، AuthHandshake (AUTH1/AUTH2)، CandidateGatherer (Mono.Nat 3.0.4)، DirectTransport، TunnelListener، SymmetricConnector، HostDiagnostics. `Spike`: certtest / gather / symmetric / probe | 176 اختبارًا Core + 35 Tunnel؛ `certtest` يعمل؛ اتصال متماثل حقيقي بين عمليتين خلال ~50 ms |
| C | `App`: نسخة واحدة، `--minimized`، Generic Host + Serilog، نافذة رئيسية بصفحتي المضيف والمستخدم، Tray بقائمة، Toast بأزرار قبول/رفض، نافذة الطلب الوارد Top-most بعدّاد 60 ثانية، تشغيل مع Windows، app.manifest PerMonitorV2. `Infrastructure`: DpapiSecretStore، DeviceInfoProvider، LoggingSetup | بناء Release للحل كاملًا بلا أخطاء ولا تحذيرات؛ 26 اختبارًا Infrastructure |

## قرارات ADR
- 0002 (TLS) و0003 (NAT/Relay) و0004 (التوجيه) و0005 (المثبّت): معتمدة.
- 0001 (WPF): النموذج مبني ويُعتمد بعد فحص QA على Windows حقيقي.
- 0006 (Mux): يُقرر في الأسبوع 2.

## ما يحتاج تدخلًا بشريًا الآن
1. **طلب شهادة توقيع الكود (OV/EV)** لأن الاستخراج يستغرق أيامًا.
2. **تجهيز VPS للاختبار (staging)** وفق `docs/runbook.md` ونشر الخادم عليه.
3. **تشغيل النموذج التقني على Windows** وفق `docs/spike-runbook.md`: `certtest` على Win10 وWin11، `gather` على راوترين أو ثلاثة، ثم بدء الأزواج العشرة.
4. **فحص QA لتطبيق WPF على Windows** (Tray، Toast، النسخة الواحدة، التشغيل مع الدخول، الإخفاء إلى Tray، DPI) لاعتماد ADR-0001.

## الأسبوع 2 (من الخطة)
- A: `admin_*`، rate limit، `GET /sessions/me`، اختبارات التكامل الإضافية. (ملاحظة: `/docs` حاليًا في `ENV=dev` فقط؛ تقييده للمسؤولين في الإنتاج ضمن أسبوع 2.)
- B: نموذج Nerdbank مقابل Framing يدوي ← ADR-0006؛ Proxy أولي مع صفحة الفحص؛ مصفوفة تشغيل المتصفح؛ تجميع بيانات الأزواج العشرة.
- C: `ApiClient` + شاشة الدخول ضد staging؛ `MockControlChannel` من `ws-protocol.md`؛ مفتاح `--uninstall-notifications`.
- D: نشر staging، فحص SmartScreen للمثبّت الموقّع، تحديث قائمة القبول.
