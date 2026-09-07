# عقد REST: `/api/v1`

الإصدار: 1 (مجمّد نهاية الأسبوع 1). المصدر التنفيذي هو OpenAPI المولّد على `/docs` (مقيّد للمسؤولين في الإنتاج). هذا الملف يثبّت الأشكال التي يعتمد عليها المسار C.

## قواعد عامة

- JSON في الطلب والرد. الأوقات ISO-8601 UTC بلاحقة `Z`. المعرّفات UUID.
- المصادقة: `Authorization: Bearer <access>`. access JWT صالح 15 دقيقة، refresh صالح 30 يومًا ويُستبدل عند كل تجديد (rotation؛ إعادة استخدام refresh قديم تلغي السلسلة كلها).
- الأخطاء: `{ "error": { "code": "…", "message": "…" } }` مع رمز HTTP مناسب. أكواد شائعة: `invalid_credentials`, `account_locked`, `account_disabled`, `device_revoked`, `unauthorized`, `forbidden`, `not_found`, `validation_error`, `rate_limited`, `conflict`.
- **تحديد المعدل** (أُعيدت معايرته في الأسبوع 6، [ADR-0008](decisions/0008-rate-limit-policy.md))؛ التجاوز يرد `429` بمظروف `rate_limited` وترويسة `Retry-After`:

| المسار | المفتاح | الحد |
|---|---|---|
| `POST /auth/login` | عنوان IP | 30/دقيقة |
| `POST /auth/login` | **البريد المُرسَل** (مجزَّأ) | 5 لكل 15 دقيقة، **تُعاد عند نجاح الدخول** |
| `POST /auth/refresh` | عنوان IP | 60/دقيقة |
| `POST /probe` | `user_id` | 10/دقيقة |

  الحد بمفتاح البريد هو حماية الحساب: الحد بالـ IP وحده تتجاوزه عشرون عنوانًا مختلفًا، ويعاقب في المقابل فريقًا كاملًا خلف NAT واحد. وسعته (5) **أقل من عتبة قفل الحساب** (10 إخفاقات) عمدًا، فلا تستطيع دفعة أن تقفل حسابًا: القفل حرمان من الخدمة يستطيع مهاجم إطلاقه. وإعادة الرصيد عند النجاح تجعل الإخفاقات وحدها تستهلك الحصة.
- قفل الحساب 15 دقيقة بعد 10 محاولات فاشلة → `423 account_locked`.

## المصادقة

### `POST /auth/login`
```json
{ "email": "a@b.c", "password": "…",
  "device": { "id": "uuid|null", "secret": "…|null", "name": "LAPTOP-01", "os_version": "Windows 11 Pro", "os_build": "22631" } }
```
- `device.id` و`device.secret` فارغان عند أول دخول من الجهاز → الخادم ينشئ الجهاز ويعيد `device_secret` **مرة واحدة**؛ العميل يحفظه بـ DPAPI.
- جهاز موجود بسر خاطئ → `401 unauthorized`. جهاز ملغى → `403 device_revoked`.

الرد `200`:
```json
{ "access_token": "…", "refresh_token": "…", "expires_in": 900,
  "user": { "id": "…", "email": "…", "display_name": "…", "role": "user" },
  "device": { "id": "…", "name": "…", "secret": "…|null" } }
```

### `POST /auth/refresh`  `{ "refresh_token": "…" }` → نفس شكل الرد أعلاه بلا `device.secret`.
### `POST /auth/logout`  `{ "refresh_token": "…" }` → `204`.

## المستخدم الحالي

- `GET /me` → `{ id, email, display_name, role }`
- `GET /me/devices` → `[{ id, name, os_version, status, last_seen_at, created_at }]`
- `DELETE /me/devices/{id}` → `204` (إلغاء تسجيل؛ يلغي refresh tokens الجهاز)
- `GET /hosts` → `[{ device_id, user_display_name, device_name, reachable }]` — نفس شكل `hosts.snapshot` ونفس التصفية بالضبط (متصل، مفعِّل «متاح»، بلا جلسة غير منتهية، **مع استثناء أجهزة المستخدم نفسه**؛ ثُبّت في الأسبوع 3)
- `GET /sessions/me?limit=50` → `[{ id, role, peer_display_name, peer_device_name, status, created_at, started_at, ended_at, end_reason, bytes_up, bytes_down }]`
- `GET /domains` → `{ "version": 3, "entries": ["example.com", "=exact.com", "portal.corp:8443"] }` مع `ETag: "3"`؛ يدعم `If-None-Match` → `304`، و`?version=N` يعيد إصدارًا محددًا أو `404`.

  **الاحتفاظ بالإصدارات (ثُبّت في الأسبوع 5):** إصدارات القائمة لقطات غير قابلة للتعديل ولا تُحذف أبدًا. الاعتماد عليها حقيقي: المضيف يستدعي `?version=N` ليعرض للطالب المواقع التي ستُمنح فعلًا قبل القبول (متطلب القسم 15 من وثيقة المنتج)، وحذف إصدار قديم يجعل هذا الإفصاح يفشل. أي تنظيف مستقبلي يجب أن يستثني الإصدارات المشار إليها من جلسات غير منتهية.

## النموذج التقني

- `POST /probe`  `{ "ip": "…", "port": 12345 }` → `{ "reachable": true, "latency_ms": 42 }` (عند الفشل: `{ "reachable": false, "latency_ms": null }`). يتطلب مصادقة. العناوين الخاصة وloopback تُرفض بـ `400 validation_error`. الخادم يجرب اتصال TCP بمهلة 3 ثوانٍ ويغلقه فورًا. يُستخدم في نموذج الأسبوع 1 وفي فحص قابلية الوصول.
- `POST /diagnostics`  `{ "session_id": "uuid|null", "role": "guest|host|null", "data": { … } }` → `201 { "id": "…" }`. يتطلب مصادقة (أي دور). يخزّن صفًا في `connect_diagnostics` لجهاز المرسِل (من التوكن). `data` كائن JSON حر بحد أقصى 64 KB بعد التسلسل، وإلا `422 validation_error`. عند تمرير `session_id` يجب أن تكون الجلسة موجودة (`404 not_found`) وأن يكون المستخدم أحد طرفيها (`403 forbidden`). تستخدمه أداة نموذج NAT في الأسبوع 2 لرفع نتائج الأزواج العشرة؛ الملخص عبر `GET /admin/diagnostics`.

  **مفاتيح محجوزة داخل `data` (ثُبّتت في الأسبوع 6):** إن حمل الكائن المفتاح `listener_unauthenticated` بقيمة عددية موجبة، يفسّره الخادم أيضًا كإشارة أمنية ويكتب صف `security_events` من النوع `listener_unauthenticated` إلى جانب صف التشخيص المعتاد. سياقه: مستمع المضيف أثناء نافذة الاتصال يغلق أي اتصال لا يجتاز `AUTH1` (بروتوكول القناة، القسم 2)، وهذه اللحظة الوحيدة التي يرى فيها النظام محاولة وصول غير مصرح بها إلى منفذ المضيف — والخادم لا يستطيع رصدها بنفسه. المفاتيح المرافقة الاختيارية: `listener_port` (عدد)، و`unauthenticated_peers` (مصفوفة عناوين IP، بحد 10)، و`unauthenticated_peers_distinct` (عدد المصادر المميزة قبل الاقتطاع — يميّز مسحًا من عشرات المصادر عن محاولات متكررة من مصدر واحد، وهو ما لا تكشفه القائمة المقتطعة وحدها). **لا تُسجَّل حمولات ولا أسماء نطاقات** — عدّاد وعناوين مصدر فقط.

  **العدّاد صفرًا ليس إشارة:** الخادم لا يكتب حدثًا أمنيًا إلا لقيمة موجبة. أما هل يُرسل الطلب أصلًا فقرار المرسِل: **التطبيق** لا يرسل عند الصفر لأن ليس لديه ما يقوله، بينما **أداة النموذج** ترسل تشخيصها كاملًا في كل تشغيل والصفر فيه بيان لا صمت. المقام (كم جلسة لم تر شيئًا) يُشتق من جدول الجلسات، فلا يضيع بأي من السلوكين.

- `GET /healthz` (بلا مصادقة، وخارج `/api/v1`) → `{ "status": "ok", "product":"josour", "version": "0.1.0" }`.

  البنية جزء من العقد رغم أنها خارج OpenAPI: شاشتا التشغيل الأول والإعدادات في العميل تقرران منه **هل هذا خادم Josour** قبل السماح للمستخدم بالمتابعة، ولولا علامة المنتج لعاد عنوان خاطئ لاحقًا في صورة «كلمة مرور خاطئة». وهي ليست ضابطًا أمنيًا — الرد بلا مصادقة ويسهل تقليده — بل وسيلة للفشل مبكرًا وبوضوح.

## الإدارة (role = admin)

- `POST /admin/users` `{ email, password, display_name, role }` → `201` المستخدم.
- `GET /admin/users` → قائمة. `PATCH /admin/users/{id}` `{ is_active?, password?, display_name?, unlock?: true }`.
- `GET /admin/devices?user_id=` ، `POST /admin/devices/{id}/revoke` → `204`.
- `GET /admin/domains` → `{ version, entries, updated_at }`.
- `PUT /admin/domains` `{ "entries": [...] }` → إصدار جديد؛ يتحقق من صحة كل مدخل؛ يبث `allowlist.updated`.
- `GET /admin/sessions?status=&limit=` ، `POST /admin/sessions/{id}/terminate` → `204`.
- `GET /admin/security-events?limit=` ، `GET /admin/diagnostics` → ملخص: عدد الجلسات، نسبة `connect_result=ok` (و`timeout` يُحسب فشلًا)، توزيع `winner_type`، توزيع `tls_version`، وتوزيع `end_reason` (أُضيف في الأسبوع 4).
- `GET /admin/settings` ، `PATCH /admin/settings` `{ max_session_minutes?, request_timeout_seconds?, log_domains?, allowed_ports? }`.

## القيم الافتراضية للإعدادات

| المفتاح | الافتراضي |
|---|---|
| `max_session_minutes` | 120 |
| `request_timeout_seconds` | 60 |
| `connect_timeout_seconds` | 30 |
| `log_domains` | false |
| `allowed_ports` | `[80, 443]` |
