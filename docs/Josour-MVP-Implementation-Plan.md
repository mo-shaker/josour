# خطة تنفيذ Josour — الإصدار الأول (MVP)

## 1. السياق

المشروع يبدأ من الصفر: مجلد `/Users/moshaker/project` لا يحتوي إلا على `Josour.pdf` (وثيقة تعريف المنتج وخطة الإصدارات، 16 صفحة). المطلوب خطة تنفيذ كاملة للإصدار الأول كما حددته الوثيقة في الأقسام 6 و10 إلى 17.

**هدف الإصدار الأول:** إثبات أن النظام يستطيع إنشاء اتصال آمن ومستقر بين جهاز المستخدم (Guest) وجهاز المضيف (Host)، وتمرير متصفح عمل مستقل عبر عنوان IP المضيف، بواجهة بسيطة، دون مرور أي بيانات تصفح عبر الخادم المركزي.

**القيود الحاكمة من الوثيقة:**
- الخادم الخلفي (FastAPI) للتحكم والتنسيق فقط. بيانات التصفح تمر مباشرة بين الجهازين عبر قناة مشفرة مستقلة.
- Relay Server مؤجل، ولا يُضاف إلا إذا أثبتت الاختبارات فشل الاتصال المباشر. QUIC مؤجل.
- لا فك تشفير HTTPS، لا تسجيل محتوى، منع الوصول للشبكة المحلية وLocalhost والعناوين الداخلية للمضيف.
- مضيف واحد يستضيف مستخدمًا واحدًا. جلسة فعالة واحدة لكل مستخدم. مدة محددة لكل جلسة.
- التقنيات: C# + .NET للعميل، Python + FastAPI + PostgreSQL + Native WebSockets للخادم، VPS Linux صغير مع Docker Compose.

---

## 2. القرارات التقنية (ما تركته الوثيقة مفتوحًا + ما كشفته المراجعة التقنية)

| القرار | الاعتماد | السبب |
|---|---|---|
| واجهة العميل | **WPF على .NET 8** + `WPF-UI` للمظهر الحديث، `CommunityToolkit.Mvvm`، `H.NotifyIcon.Wpf` للـ Tray، `Microsoft.Toolkit.Uwp.Notifications` للإشعارات | WinUI 3 بلا Tray رسمي، ويحتاج Windows App SDK Runtime، وقصة الإشعارات خارج MSIX أحدث. نموذج ليوم واحد في المرحلة 0 يثبّت القرار كما طلبت الوثيقة |
| المثبّت | **مثبّت موقّع غير مغلف (Inno Setup)**، لا MSIX في MVP | MSIX يورّث هوية الحزمة للمتصفح المُشغَّل ويغيّر مسار Profile. المثبّت المرتفع الصلاحية يضيف قاعدة Windows Firewall للتطبيق |
| إصدار TLS | `SslProtocols.None` (افتراضي النظام) مع **رفض أي تفاوض أقل من TLS 1.2** وتسجيل الإصدار المتفاوَض عليه | Schannel على Windows 10 لا يدعم TLS 1.3، وطلبه صراحة يرمي خطأ. TLS 1.3 يُستخدم تلقائيًا على Windows 11. **هذا تعديل على نص الوثيقة (10 و13) يحتاج اعتماد صاحب المنتج** |
| إنشاء الاتصال | **اتصال متماثل**: الطرفان يفتحان مستمعًا ويجمعان مرشحين ويتصلان بمرشحي الآخر بالتوازي، وأول اتصال يُصادَق عند المضيف يصبح النفق | يضاعف احتمال أن يكون أحد الطرفين قابلًا للوصول (مضيف في المكتب ومستخدم في المنزل مثلًا) بالكود نفسه مع علم للدور |
| عبور NAT | مرشحون: IP عام (يراه الخادم) + UPnP/NAT-PMP عبر **`Mono.Nat` 3.x** + IPv6 عام + عناوين LAN فقط إذا تطابق الـ IP العام للطرفين. **لا TCP hole punching في MVP**. بوابة قرار Relay بعد النموذج | Open.NAT مهجور. Hole punching يكلف أسبوعًا ويفشل تحديدًا على شبكات الشركات. Relay بسيط (يمرر بايتات معتمة) يُبنى في يومين إذا فشلت البوابة |
| Multiplexing | نموذج يومين لـ `Nerdbank.Streams.MultiplexingStream` أولًا؛ وإلا Framing يدوي (المواصفة في 7.3) | مكتبة ناضجة مع Backpressure مدمج توفر أسبوعًا من العمل إن نجحت |
| Proxy محلي | HTTP CONNECT فقط، على `127.0.0.1:0` (منفذ يعيّنه النظام)، **يقبل الاتصالات من عمليات المتصفح المُشغَّل فقط** (فحص PID المالك) | يمنع أي برنامج آخر على جهاز المستخدم من استغلال النفق (متطلب 6.5: اتصال هذا المتصفح فقط) |
| قرار قائمة المواقع | **الطرفان**: المستخدم يقرر التوجيه (نفق أم مباشر)، والمضيف يقرر التصريح ويرفض ما ليس في القائمة | المضيف هو صاحب المسؤولية عن IP الخاص به وشبكته؛ لا نثق بادعاء المستخدم |
| المواقع غير المسموح بها | تمر عبر اتصال المستخدم الطبيعي (قراءة الوثيقة 6.6: «تمرير بقية المواقع عبر اتصال المستخدم الطبيعي») | «منع النطاقات غير المسموح بها» يُفهم كمنعها من المرور عبر المضيف. يُضاف خيار «حظر بدل التمرير» في الإصدار الثاني |
| Workers الخادم | Uvicorn worker واحد | سجل اتصالات WS في الذاكرة. Redis Pub/Sub مسار الترقية |
| لوحة الإدارة | **إدارة المستخدمين داخل التطبيق** (لمن دوره `admin`) + CLI (`manage.py`) + OpenAPI التفاعلي | الوثيقة أجّلت واجهة الويب للإصدار الثاني؛ أُضيفت اللوحة داخل التطبيق بدلها فلا تعرّض سطحًا إداريًا على الإنترنت |
| تسجيل الأجهزة | الجهاز يُسجَّل عند أول تسجيل دخول بمعرّف + سر محفوظ بـ DPAPI، ويُرسل مع كل دخول. المسؤول يستطيع إلغاءه فيُمنع | يحقق «منع الاستخدام على أجهزة غير مصرح بها» |

**حدود ثقة موثقة (ADR):** الخادم يعرف سر الجلسة وبصمة الشهادة، فخادم مخترق يتحكم بالمسار يستطيع نظريًا اعتراض النفق. مقبول في MVP. التقوية في الإصدار الثاني: زوج مفاتيح طويل الأمد لكل جهاز يوقّع بصمة شهادة الجلسة.

**اعتمدها صاحب المنتج بتاريخ 2026-09-03:** TLS 1.3 حيث يتوفر مع حد أدنى 1.2 (تعديل على نص الوثيقة يُوثَّق في ADR)، بوابة قرار Relay بعد النموذج التقني، تمرير المواقع غير المسموح بها عبر اتصال المستخدم الطبيعي، والتنفيذ بفريق من أكثر من مطورَين (القسم 12 مبني على 3 مطورين + مسار DevOps/QA).

---

## 3. هيكل المستودع (Monorepo)

```
josour/
├── backend/                          # Python 3.12 + FastAPI
│   ├── app/
│   │   ├── main.py                   # إنشاء التطبيق، الـ routers، lifespan
│   │   ├── core/                     # config.py, security.py (argon2id + JWT), logging.py
│   │   ├── db/                       # session.py (async engine), base.py
│   │   ├── models/                   # SQLAlchemy 2.0 (القسم 4)
│   │   ├── schemas/                  # Pydantic v2
│   │   ├── api/routers/              # auth, me, devices, hosts, sessions, domains, admin_*
│   │   ├── ws/                       # router.py, connection_manager.py, protocol.py, handlers.py
│   │   ├── services/                 # presence, request, session, session_timer, reachability_probe, stats, audit
│   │   └── cli.py                    # Typer: create-admin, create-user, add-domain, list-sessions, end-session
│   ├── alembic/
│   ├── tests/
│   ├── Dockerfile
│   └── pyproject.toml
├── client/                           # حل .NET 8
│   ├── Josour.sln
│   ├── src/
│   │   ├── Josour.Core/         # النماذج، آلة حالة الجلسة، AllowlistMatcher، IpRangePolicy، رسائل البروتوكول (بلا تبعيات)
│   │   ├── Josour.Tunnel/       # TlsTunnelFactory، AuthHandshake، CandidateGatherer، CandidateDialer، TunnelListener، Mux
│   │   ├── Josour.Proxy/        # ConnectProxyServer (جهة المستخدم)، ProbePage، OwnerPidChecker
│   │   ├── Josour.Egress/       # EgressPolicy، OpenHandler (جهة المضيف)، ByteCounter، DomainCollector
│   │   ├── Josour.Browser/      # BrowserLocator، PolicyDetector، BrowserLauncher (Job Object)، GracefulCloser
│   │   ├── Josour.Infrastructure/ # ApiClient، ControlChannel (WS)، DpapiSecretStore، DeviceInfo، Serilog
│   │   └── Josour.App/          # WPF + MVVM + Tray + Toasts + DI (Generic Host)
│   ├── tests/                        # Josour.Core.Tests، Tunnel.Tests، Proxy.Tests، Egress.Tests، Browser.Tests
│   ├── tools/Josour.Spike/      # أدوات Console للنموذج التقني (المرحلة 0)
│   └── installer/                    # Inno Setup + قاعدة Firewall + سكربت التوقيع
├── deploy/                           # docker-compose.yml، Caddyfile، backup/، .env.example
├── docs/                             # protocol.md، ws-protocol.md، api.md، runbook.md، acceptance-checklist.md، decisions/ (ADR)
├── .github/workflows/                # ci-backend.yml، ci-client.yml
└── README.md
```

---

## 4. نموذج البيانات (PostgreSQL)

| الجدول | الأعمدة الأساسية | ملاحظات |
|---|---|---|
| `users` | id, email (unique), password_hash (argon2id), display_name, role (admin/user), is_active, failed_logins, locked_until, created_at | الإنشاء عبر المسؤول فقط |
| `devices` | id, user_id, name, os_version, os_build, device_secret_hash, status (active/revoked), last_seen_at, created_at | السر يولَّد على الجهاز ويُحفظ بـ DPAPI |
| `refresh_tokens` | id, user_id, device_id, token_hash, expires_at, revoked_at | Rotation عند كل تجديد |
| `presence` | device_id (pk), connected, is_available_host, public_ip, reachable (bool/null), updated_at | يُصفَّر عند إقلاع الخادم |
| `connection_requests` | id, guest_user_id, guest_device_id, host_user_id, host_device_id, requested_minutes, status (pending/accepted/rejected/expired/cancelled), created_at, responded_at, expires_at | ينتهي بعد 60 ثانية بلا رد |
| `sessions` | id, request_id, guest_*, host_*, status (connecting/active/ended), created_at, started_at, expires_at, ended_at, end_reason, bytes_up, bytes_down, connect_result, winner_type, tls_version, connect_ms | end_reason: guest_ended / host_ended / expired / guest_disconnected / host_disconnected / connect_failed / admin_terminated / browser_not_proxied / protocol_error |
| `session_keys` | session_id (pk), secret, guest_cert_fp, host_cert_fp, guest_candidates (jsonb), host_candidates (jsonb), created_at | **يُحذف الصف فور انتهاء الجلسة** |
| `allowed_domains` | id, entry, is_active, note, updated_at | `entry` بصيغة `example.com` (تشمل النطاقات الفرعية) أو `=exact.com` أو `host:port`. جدول `allowlist_versions` يحمل رقم الإصدار |
| `session_domains` | id, session_id, domain, hit_count | يُملأ عند نهاية الجلسة إذا كان إعداد `log_domains` مفعّلًا |
| `connect_diagnostics` | id, session_id, device_id, role, data (jsonb), created_at | بيانات النموذج وبوابة Relay: firewall_profile, firewall_rule_present, upnp_found, mapping_ok, upnp_external_ip, cgnat_suspected, ipv6_global, system_proxy_present, vpn_adapter, candidates_tried, per-candidate latency/error |
| `security_events` | id, type, user_id, device_id, ip, details (jsonb), created_at | محاولات الدخول، القفل، الإلغاء، الإنهاء المركزي، اتصالات غير مصادقة على المستمع |
| `app_settings` | key (pk), value | max_session_minutes, request_timeout_seconds, log_domains, allowed_ports |

**قيود:** فهرس جزئي فريد يضمن جلسة واحدة غير منتهية لكل مستخدم ولكل جهاز.

---

## 5. واجهات REST (`/api/v1`)

**عام (Bearer JWT):**
- `POST /auth/login` (email, password, device: {id?, secret?, name, os}) → access (15 دقيقة) + refresh (30 يومًا) + device_id (+ device_secret عند التسجيل الأول). الجهاز الملغى يُرفض.
- `POST /auth/refresh`، `POST /auth/logout`
- `GET /me`، `GET /me/devices`، `DELETE /me/devices/{id}`
- `GET /hosts` — المضيفون المتاحون مع شارة قابلية الوصول (من فحص الخادم)
- `GET /sessions/me`
- `GET /domains` → `{version, entries[]}` مع ETag. يدعم `?version=` لجلب إصدار محدد

**إدارة (role=admin):**
- `POST|GET /admin/users`، `PATCH /admin/users/{id}` (تفعيل/تعطيل/كلمة مرور/فك القفل)
- `GET /admin/devices`، `POST /admin/devices/{id}/revoke`
- `GET /admin/domains`، `PUT /admin/domains` (استبدال كامل → إصدار جديد → بث `allowlist.updated`)
- `GET /admin/sessions`، `POST /admin/sessions/{id}/terminate`
- `GET /admin/security-events`، `GET /admin/diagnostics` (ملخص بوابة Relay)
- `GET|PATCH /admin/settings`

**حماية:** rate limit على `/auth/login` (5/دقيقة/IP)، قفل الحساب 15 دقيقة بعد 10 محاولات فاشلة، تسجيل كل محاولة.

---

## 6. بروتوكول WebSocket (`/ws`، المصادقة برسالة `hello` تحمل access token)

| الاتجاه | النوع | المحتوى |
|---|---|---|
| عميل→خادم | `hello` | token, device_id, app_version, diagnostics (firewall_rule_present, vpn_adapter, system_proxy…) |
| عميل→خادم | `host.available` | available, listen_port (للفحص العكسي من الخادم) |
| عميل→خادم | `request.create` / `request.cancel` | host_device_id, duration_min / request_id |
| عميل→خادم | `request.accept` / `request.reject` | request_id |
| عميل→خادم | `session.endpoint` | session_id, cert_fp_sha256, candidates: [{type: lan/upnp/public/v6, ip, port}] **(من الطرفين)** |
| عميل→خادم | `session.connected` | session_id, winner_type, connect_ms, tls_version **(من المضيف فقط، المرجع)** |
| عميل→خادم | `session.connect_failed` | session_id, diagnostics |
| عميل→خادم | `session.stats` | session_id, bytes_up, bytes_down (كل 30 ثانية من المضيف) |
| عميل→خادم | `session.end` | session_id, reason, bytes_up, bytes_down, domains[] |
| خادم→عميل | `hello.ack` | server_time, public_ip, settings, allowlist_version |
| خادم→عميل | `hosts.snapshot` / `hosts.update` | المضيفون المتاحون: اسم المستخدم، اسم الجهاز، الحالة، reachable |
| خادم→عميل | `request.incoming` | request_id, guest_name, guest_device, duration_min, allowlist_version, expires_at (للمضيف) |
| خادم→عميل | `request.result` | request_id, accepted, session_id (للمستخدم) |
| خادم→عميل | `session.created` | session_id, role, secret_b64, expires_at, allowlist_version, peer_public_ip, same_public_ip **(للطرفين)** |
| خادم→عميل | `session.peer_endpoint` | cert_fp_sha256, candidates[] (كل طرف يستلم مرشحي الآخر) |
| خادم→عميل | `session.active` | session_id, expires_at |
| خادم→عميل | `session.terminate` | session_id, reason (إنهاء مركزي أو انقطاع الطرف الآخر) |
| خادم→عميل | `allowlist.updated` | version |
| كلاهما | `ping` / `pong` | نبض كل 20 ثانية، ضياع نبضتين = انقطاع |

**دورة حياة الجلسة (الخادم مرجع الحالة):**
```
request(pending, 60s) ─accept─▶ session(connecting): created→ endpoints من الطرفين → peer_endpoint
   connecting ─host: connected─▶ active            | مهلة 30 ثانية ─▶ ended(connect_failed)
   active ─مؤقت expires_at─▶ ended(expired)
   active ─انقطاع WS لطرف─▶ ended(*_disconnected) + terminate للطرف الآخر
   active ─session.end من طرف─▶ ended(guest_ended|host_ended) + terminate للآخر
   active ─admin─▶ ended(admin_terminated) + terminate للطرفين
```
عند `ended`: حذف `session_keys`، حفظ الإحصاءات والنطاقات، تحديث `presence`.

**فحص قابلية الوصول من الخادم:** عند `host.available` مع منفذ مستمع، الخادم يجرب اتصال TCP قصيرًا إلى IP المضيف العام:المنفذ ويحدّث `presence.reachable`. يظهر كشارة في قائمة المضيفين ويغذي بوابة Relay دون انتظار جلسات.

---

## 7. بروتوكول القناة بين الجهازين (`docs/protocol.md`)

### 7.1 إنشاء الاتصال (متماثل)
1. عند `session.created` كل طرف: يولّد شهادة ذاتية (ECDSA P-256، EKU serverAuth+clientAuth، صلاحية حتى انتهاء الجلسة + ساعة، تُعاد استيرادها من PFX بعلم `UserKeySet` لتعمل مع Schannel)، يفتح مستمعًا على `IPv6Any` بوضع DualMode ومنفذ 0، يطلب تعيين UPnP/NAT-PMP (Mono.Nat، عمر التعيين = مدة الجلسة + 5 دقائق، ويُسخَّن مسبقًا عند تفعيل «متاح»)، يجمع المرشحين (LAN فقط عند `same_public_ip`)، ويرسل `session.endpoint`.
2. عند `session.peer_endpoint`: كل طرف يتصل بكل مرشحي الآخر بالتوازي (مهلة 5 ثوانٍ لكل مرشح).
3. لكل اتصال TCP ناجح: مصافحة TLS (10 ثوانٍ) — المتصل يثبّت بصمة شهادة الطرف الآخر ويتجاهل أخطاء السلسلة، `TargetHost` ثابت `josour`، بلا فحص إبطال — ثم مصادقة داخل TLS (5 ثوانٍ). **المستخدم يتكلم أولًا دائمًا** أيًا كان من اتصل.
4. المضيف يبقي أول اتصال يُصادَق ويغلق الباقي ويرسل `session.connected`. الطرفان يغلقان المستمع ويزيلان تعيين UPnP ويلغيان بقية المحاولات.
5. إن لم يُصادَق شيء خلال 30 ثانية: `session.connect_failed` مع التشخيص.

**المستمع يعيش فقط في نافذة الاتصال:** حد 4 اتصالات غير مصادقة معلّقة، المضيف صامت حتى تنجح المصادقة، أي اتصال غريب يُغلق ويُسجَّل.

### 7.2 المصادقة داخل TLS
- `AUTH1` (مستخدم→مضيف): `v=1 ‖ session_id(16B) ‖ client_random(32B) ‖ HMAC-SHA256(secret, "rb-auth1" ‖ session_id ‖ client_random ‖ listener_cert_fp)`
- `AUTH2` (مضيف→مستخدم): `HMAC-SHA256(secret, "rb-auth2" ‖ session_id ‖ client_random ‖ listener_cert_fp)`
- مقارنة ثابتة الزمن. بعد النجاح يبدأ الـ Mux. بعد التفاوض يُرفض أي إصدار أدنى من TLS 1.2 ويُسجَّل الإصدار.
- عند الانتهاء: `Dispose` صريح للشهادات (يحذف حاوية المفتاح من Windows) ومسح `secret` من الذاكرة.

### 7.3 الإطارات والتحكم بالتدفق (إن لم يُعتمد Nerdbank)
```
رأس 8 بايت: [u8 type][u8 flags][u16 len][u32 stream_id]   الحمولة ≤ 16 KiB
```
الأنواع: `OPEN` ([u16 port][u8 hostlen][host]) / `OPEN_OK` / `OPEN_FAIL` (u8 reason: not_allowed, private_ip, port_not_allowed, dns_failed, connect_failed, limit) / `DATA` / `WINDOW_UPDATE` (u32) / `CLOSE` (إغلاق نصفي = Shutdown(Send) عند الطرف الآخر) / `RST` / `PING`/`PONG` (8 بايت) / `GOAWAY`.
- معرّفات الـ streams فردية متزايدة لا تُعاد. المستخدم فقط يفتح streams.
- نافذة استقبال لكل stream **1 MiB** (بلا نافذة على مستوى الاتصال). الرصيد يُعاد **بعد كتابة البايتات إلى المقبس الوجهة**، ويُرسل `WINDOW_UPDATE` عند استهلاك 25% من النافذة.
- كاتب واحد يفرّغ `Channel<Frame>` محدودًا (32)، وكل stream يضع إطارًا واحدًا (16 KiB) في المرة؛ يعطي عدالة Round-robin تلقائيًا.
- حدود على المضيف: 256 stream متزامن، 50 فتحًا/ثانية. حيوية النفق: `PING` كل 20 ثانية، ميت بعد 60 ثانية.

### 7.4 جانب المضيف عند `OPEN` (`EgressPolicy`)
1. ارفض أي IP literal (لا يمكن إدراجه في القائمة أصلًا). طبّع الاسم: أحرف صغيرة، حذف النقطة الأخيرة، مطابقة على Punycode.
2. طابق مع قائمة المواقع: `example.com` تشمل النطاقات الفرعية، `=exact.com` تامة فقط، `host:port` اختياري. المنفذ ضمن `allowed_ports` (80، 443).
3. حل DNS **مرة واحدة** بـ `Dns.GetHostAddressesAsync`. إن كان **أي** عنوان ناتج ضمن القائمة السوداء → `OPEN_FAIL(private_ip)`. الاتصال بـ `Socket.ConnectAsync(IPAddress[], port)` بالقائمة المفحوصة **وليس بالاسم** (يغلق DNS rebinding وHappy Eyeballs).
4. القائمة السوداء: `0/8, 10/8, 100.64/10, 127/8, 169.254/16, 172.16/12, 192.0.0/24, 192.0.2/24, 192.168/16, 198.18/15, 198.51.100/24, 203.0.113/24, 224/4, 240/4, 255.255.255.255`، وIPv6: `::, ::1, ::ffff:0:0/96 (فك التغليف وفحص v4), 64:ff9b::/96, 2002::/16, 2001::/32 (Teredo), fc00::/7, fe80::/10, ff00::/8`، **إضافة إلى كل عناوين واجهات المضيف وبواباته الافتراضية وIP العام الذي يراه الخادم** (يمنع الوصول لصفحة إدارة الراوتر عبر IP العام).
5. اتصال بمهلة 10 ثوانٍ، `OPEN_OK`، ضخ ثنائي الاتجاه مع عدّاد البايتات وجمع النطاقات المميزة.

### 7.5 جانب المستخدم (`ConnectProxyServer`)
- يستمع على `127.0.0.1:0` ويقرأ المنفذ المعيّن قبل بناء سطر أوامر المتصفح.
- **فحص المالك:** لكل اتصال وارد، استعلام `GetExtendedTcpTable` عن PID المالك؛ يُقبل فقط إن كان ضمن Job Object الخاص بالمتصفح.
- `CONNECT host:port` (يشمل `ws://` و`wss://` والصيغة `[IPv6]:port`): مسموح → `OPEN` عبر النفق و**`200 Connection Established` فقط بعد `OPEN_OK`** (وإلا 403/502/504 صادقة)؛ غير مسموح → اتصال TCP مباشر من جهاز المستخدم (يحترم Proxy النظام إن وُجد بإرسال CONNECT إليه). `OPEN_FAIL(not_allowed)` أثناء نافذة تباين القائمة → سقوط إلى المباشر.
- طلبات `http://` العادية: لنطاق مسموح → رد محلي `307` إلى `https://` (لا بايتات نصية عبر النفق). لغير المسموح → طلب واحد لكل اتصال: تحويل absolute-URI إلى origin-form، حذف `Proxy-Connection`، إضافة `Connection: close`، ضخ حتى يغلق الأصل. (Chromium يعيد استخدام اتصال الـ Proxy لأصول مختلفة، فالضخ الأعمى يخلط الوجهات.)
- **صفحة الفحص:** المتصفح يُفتح على `http://check.josour/`؛ الـ Proxy يعترضها ويرد بصفحة «النفق نشط. المواقع سترى: <IP المضيف العام>». إن لم يصل هذا الطلب خلال 10 ثوانٍ فالمتصفح لا يستخدم الـ Proxy → إنهاء الجلسة بسبب `browser_not_proxied`.
- أي CONNECT إلى عنوان خاص أو Localhost يُرفض محليًا أيضًا (دفاع في العمق).

### 7.6 ترتيب التنظيف عند الانتهاء
1. الـ Proxy يتوقف عن قبول CONNECT جديد. 2. إغلاق المتصفح المهذب ثم قتل الـ Job. 3. `GOAWAY` وإغلاق النفق. 4. إغلاق المستمع وإزالة تعيين UPnP. 5. `Dispose` للشهادات ومسح السر. 6. `session.end` بالإحصاءات والنطاقات.

### 7.7 بوابة قرار Relay
بعد نموذج المرحلة 0 على 10 أزواج حقيقية على الأقل من شبكات المستخدمين المستهدفين خلال أسبوعين: إن كانت نسبة الاتصال خلال 10 ثوانٍ أقل من 85% تُنفَّذ المرحلة 7 (Relay). التقدير المسبق للجمهور المذكور في الوثيقة (شركات وفرق موزعة) نحو 45% إلى 65% بالاتصال المتماثل، فاحتمال الحاجة للـ Relay مرتفع؛ لذلك يُبنى النقل خلف واجهة `ITunnelTransport` من البداية ليكون `RelayTransport` إضافة لا تغييرًا.

---

## 8. تطبيق Windows (WPF + .NET 8)

### 8.1 المكونات
| المشروع | المكونات |
|---|---|
| `Core` | `SessionStateMachine`، `Role`، `AllowlistMatcher` (دالة نقية مشتركة بين الطرفين)، `IpRangePolicy`، نماذج رسائل WS، واجهات `IControlChannel`, `ITunnelTransport`, `IBrowserSession`, `ISecretStore` |
| `Tunnel` | `TlsTunnelFactory` (توليد الشهادة، Pinning، `SslProtocols.None` + تحقق ≥1.2)، `AuthHandshake` (AUTH1/AUTH2)، `CandidateGatherer` (واجهات، Mono.Nat، IPv6، IP عام)، `TunnelListener`، `CandidateDialer` (متوازٍ، أول فائز)، `MuxAdapter` (Nerdbank أو `FrameCodec`+`MuxConnection`+`MuxStream`+`AsyncCredit`) |
| `Proxy` | `ConnectProxyServer`، `HttpRequestParser`، `ProbePage`، `OwnerPidChecker` (P/Invoke iphlpapi) |
| `Egress` | `EgressPolicy`، `OpenHandler`، `SafeConnector`، `ByteCounter`، `DomainCollector`، `StreamLimiter` |
| `Browser` | `BrowserLocator` (Registry App Paths لـ HKLM/HKCU/WOW6432Node ثم المسارات الافتراضية، والتحقق من توقيع Google/Microsoft)، `PolicyDetector` (مفاتيح `SOFTWARE\Policies\Google\Chrome` و`\Microsoft\Edge`: ProxySettings/ProxyMode/ProxyServer/UserDataDir؛ يفضّل المتصفح غير المُدار)، `BrowserLauncher` (Job Object مع `KILL_ON_JOB_CLOSE`، كشف Handoff إن خرجت العملية خلال 3 ثوانٍ، ضبط `exit_type=Normal` في Preferences)، `GracefulCloser` (WM_CLOSE لنوافذ الـ Job، انتظار 3 ثوانٍ، ثم إغلاق الـ Job) |
| `Infrastructure` | `ApiClient` (تجديد التوكن الشفاف)، `ControlChannel` (`ClientWebSocket` + إعادة اتصال بتراجع أسّي + نبض + إعادة إعلان «متاح» + احترام Proxy النظام)، `DpapiSecretStore` (التوكنات وسر الجهاز)، `DeviceInfoProvider`، `FirewallRuleChecker`، `VpnAdapterDetector`، Serilog إلى `%LOCALAPPDATA%\Josour\logs` (بلا أي حمولة إطارات) |
| `App` | Generic Host + DI، `LoginWindow`، `MainWindow` (تبويبا المضيف والمستخدم)، `IncomingRequestWindow` (Top-most + صوت، لأن Focus Assist يكتم الإشعارات)، `SessionPanel`، `SettingsView`، `TrayIcon`، Toast بأزرار قبول/رفض، نسخة واحدة عبر Mutex، تشغيل مع Windows (`HKCU\...\Run`) اختياري |

### 8.2 سطر أوامر المتصفح (Chrome وEdge سواء)
```
--user-data-dir="%LocalAppData%\Josour\BrowserProfile"
--proxy-server="http://127.0.0.1:<port>"
--no-first-run --no-default-browser-check --disable-sync
--disable-background-networking --disable-component-update
--disable-quic
--force-webrtc-ip-handling-policy=disable_non_proxied_udp
--hide-crash-restore-bubble
--new-window "http://check.josour/"
```
- **بلا** `--proxy-bypass-list`: الافتراضي يتجاوز loopback فقط وهو المطلوب؛ اسم صفحة الفحص غير loopback عمدًا ليمر عبر الـ Proxy.
- لا تغيير في Proxy النظام، فلا شيء يُستعاد؛ «إعادة المتصفح لوضعه الطبيعي» تتحقق بإغلاق الـ Profile المستقل وضبط `exit_type`.
- DNS للمواقع المسموح بها يُحل على المضيف (Chromium يرسل الاسم في CONNECT ولا يحلّه محليًا). WebRTC وQUIC معطّلان في متصفح العمل فقط.

### 8.3 تدفق المضيف
1. دخول → تسجيل الجهاز → WS → `hello.ack` (يحمل IP العام) و`hosts.snapshot`.
2. تفعيل «متاح» → فحص قاعدة Firewall ومحوّل VPN (تحذير إن وُجد) → تسخين UPnP → `host.available` مع منفذ الفحص.
3. `request.incoming` → Toast + نافذة Top-most تعرض: اسم المستخدم، اسم جهازه، المدة، المواقع المسموح بها (الإصدار الحالي)، تنبيه «المواقع سترى عنوان IP الخاص بك»، وأن القطع ممكن في أي وقت (متطلب 15) → قبول/رفض خلال 60 ثانية.
4. `session.created` → إنشاء الاتصال المتماثل (7.1) → `session.connected`.
5. `SessionPanel`: عدّاد تنازلي **محلي رتيب** من لحظة الاستلام (يعمل حتى لو انقطع WS)، البيانات التقريبية، زر «قطع». إن انقطع WS أكثر من 60 ثانية يُنهي المضيف الجلسة بنفسه (وإلا لا يمكن تنفيذ الإنهاء المركزي).
6. الإنهاء بأي سبب → التنظيف (7.6) → العودة إلى «متاح».

### 8.4 تدفق المستخدم
1. دخول → قائمة المضيفين المتاحين مع شارة قابلية الوصول.
2. اختيار مضيف ومدة (15/30/60/120 دقيقة، الأقصى من إعدادات الخادم) → `request.create` → شاشة انتظار مع إلغاء.
3. مرفوض → رسالة. مقبول → `session.created` → الاتصال المتماثل → تشغيل الـ Proxy → تشغيل المتصفح على صفحة الفحص.
4. `SessionPanel`: المتبقي، الحالة، زر «تشغيل متصفح العمل» (إن أُغلق يدويًا)، زر «إنهاء».
5. الإنهاء → التنظيف (7.6).

### 8.5 حالات الحافة
- إغلاق التطبيق أثناء جلسة → `session.end` وتنظيف كامل قبل الخروج. انهيار التطبيق → الـ Job Object يقتل المتصفح تلقائيًا (Fail-closed)، والخادم ينهي الجلسة بانقطاع WS.
- نوم الجهاز / تغيير الشبكة → انقطاع WS ينهي الجلسة (إعادة الاتصال بعد انقطاع قصير: الإصدار الثاني).
- انتهاء access token → تجديد شفاف وإعادة فتح WS مع إعادة إعلان «متاح».
- الوقت: `expires_at` من الخادم، العميل يحسب المتبقي نسبةً إلى `server_time` وبعدّاد رتيب.
- المستخدم على شبكة شركة بـ Proxy إجباري: WS يحترم `ClientWebSocketOptions.Proxy`، والمسار المباشر يمرر CONNECT إلى Proxy النظام. يُسجَّل `system_proxy_present` في التشخيص.

---

## 9. الخادم الخلفي (FastAPI)

- **المكدس:** Python 3.12، FastAPI، Uvicorn، SQLAlchemy 2.0 async + asyncpg، Alembic، Pydantic v2 + pydantic-settings، `argon2-cffi`، `PyJWT`، `slowapi`، Typer، pytest + pytest-asyncio + httpx + websockets.
- **الاتصالات:** `ConnectionManager` في الذاكرة (device_id → اتصال واحد؛ الجديد يغلق القديم). بث `hosts.update` عند تغير التوافر أو قابلية الوصول.
- **المؤقتات:** `SessionTimer` بمهام asyncio: انتهاء الطلب (60 ثانية)، مهلة الاتصال (30 ثانية)، `expires_at`. عند الإقلاع: إنهاء الجلسات المعلّقة وتصفير `presence`.
- **الأمان:** HTTPS/WSS عبر Caddy، JWT قصير، refresh rotation، argon2id، rate limit، قفل الحساب، تسجيل المحاولات، إلغاء الجهاز، إنهاء مركزي، لا تخزين لأي محتوى تصفح. `/docs` مقيّد للمسؤولين في الإنتاج.

---

## 10. النشر (VPS Linux صغير: 1 vCPU / 2 GB)

| الخدمة | الصورة | الدور |
|---|---|---|
| `caddy` | caddy:2 | TLS تلقائي، Reverse proxy لـ HTTP وWebSocket، HSTS |
| `api` | build من `backend/Dockerfile` | worker واحد، `alembic upgrade head` عند الإقلاع |
| `db` | postgres:16 | Volume دائم، شبكة داخلية فقط |
| `backup` | postgres:16 + cron | `pg_dump` يومي إلى `/backups`، احتفاظ 14 يومًا، سكربت استعادة موثق |

`docs/runbook.md`: تجهيز Ubuntu (ufw 80/443 فقط، fail2ban، تحديثات تلقائية)، النشر، الترقية، الاستعادة، السجلات. (إن أُضيف Relay لاحقًا يستمع على 443 بـ TLS خلف Caddy بـ SNI مستقل.)

---

## 11. استراتيجية الاختبار

| المستوى | ما يُختبر | الأداة |
|---|---|---|
| وحدات الخادم | آلة الحالة، انتقالات الطلب، «جلسة واحدة»، الأدوار، التجزئة، JWT، إصدارات القائمة | pytest |
| تكامل الخادم | REST كاملة على PostgreSQL في Docker؛ سيناريو WS كامل بعميلين وهميين: طلب → قبول → created → endpoints → connected → active → end/expired/disconnect/terminate؛ فحص قابلية الوصول | pytest + httpx + websockets |
| وحدات العميل | `AllowlistMatcher` (جدول حالات + Punycode)، `IpRangePolicy` (جدول IPv4/IPv6 مع التغليف)، `FrameCodec` (إطارات تالفة/ناقصة/Fuzz)، Mux على أزواج `Pipe`: مستهلك بطيء لا يعطل stream آخر، الرصيد لا يصبح سالبًا، الإغلاق النصفي، `RST` يحرر الموارد، 1000 متتالٍ و256 متزامن، 100 MB عبر TLS محلي؛ `AuthHandshake` (توقيع خاطئ، بصمة مختلفة، إعادة إرسال)؛ `HttpRequestParser` | xUnit |
| تكامل العميل | `ConnectProxyServer` + `OpenHandler` في العملية نفسها: CONNECT مسموح يمر بالنفق، غير مسموح يمر مباشرة، محلي/خاص يُرفض من الطرفين، `http://` مسموح يعود 307، صفحة الفحص، رفض اتصال من PID غريب | xUnit |
| E2E يدوي | جهازا Windows (10 و11) على شبكتين؛ `docs/acceptance-checklist.md` يغطي معايير النجاح الـ 18: ifconfig.me يعرض IP المضيف في متصفح العمل وIP المستخدم في متصفحه العادي، Teams/Outlook لا تتأثر، القطع من الطرفين، الانتهاء التلقائي، عجز الخادم عن قراءة بيانات التصفح وعدم مرورها بحاوية الـ API (`docker stats` على `api` و`relay` أثناء فيديو) | يدوي + Wireshark |
| أمان | nmap على المستمع يرى TLS يغلق بلا مصادقة؛ CONNECT إلى 192.168.x وlocalhost و[::1] وIP العام للمضيف مرفوض؛ `session_keys` فارغ بعد الإنهاء؛ `certutil -user -key` لا يظهر حاويات مفاتيح متراكمة؛ السجلات بلا URL أو محتوى؛ متصفح بسياسة Proxy مؤسسية يُنهي الجلسة بـ `browser_not_proxied` | يدوي + آلي |

---

## 12. خطة التنفيذ بالمسارات المتوازية (3 مطورين + مسار DevOps/QA، 9 أسابيع تقويمية)

### 12.1 المسارات وأصحابها
| المسار | المالك | النطاق |
|---|---|---|
| **A — الخادم** | مطور Python | `backend/` كاملًا: النماذج، REST، WS، آلة الحالة، فحص قابلية الوصول، التشخيص، CLI، اختبارات pytest، وRelay إن فُتحت البوابة |
| **B — الشبكات** | مطور .NET (الأقوى في الشبكات) | `Core` (Matcher, IpRangePolicy, آلة الحالة)، `Tunnel`، `Egress`، `Proxy`، `Browser`، أداة `Spike`، اختبارات xUnit للطبقات كلها |
| **C — تطبيق Windows** | مطور .NET (واجهات) | `Infrastructure` (ApiClient, ControlChannel, DPAPI, Serilog)، `App` كاملًا (WPF، MVVM، Tray، Toast، الشاشات)، الربط النهائي بين المسارين B وA |
| **D — DevOps/QA** | مطور رابع أو دور جزئي يتقاسمه الفريق | المستودع، CI، شهادة التوقيع، المثبّت، VPS/Caddy/النسخ الاحتياطي، runbook، قائمة القبول، سكربتات اختبارات الأمان، تشغيل E2E على المصفوفة |

### 12.2 العقود المجمّدة أولًا (شرط التوازي)
تُكتب وتُجمَّد بنهاية **الأسبوع 1** ويُراجعها المسارات الأربعة معًا:
- `docs/ws-protocol.md` (القسم 6) و`docs/api.md` (القسم 5) — يبني عليهما A الخادم وC العميل، وC يعمل على `MockControlChannel` حتى يجهز A.
- `docs/protocol.md` (القسم 7) — يبني عليه B، ويستهلكه C عبر واجهات `Core`.
- واجهات `Josour.Core`: `IControlChannel`, `ITunnelTransport`, `ITunnelSession`, `IBrowserSession`, `ISecretStore` + `SessionStateMachine` — تُنشر أولًا ليبني C الشاشات على `FakeTunnelSession` و`FakeBrowserSession`.
- أي تغيير لاحق في عقد يمر بمراجعة الطرفين المتأثرين وتحديث الوثيقة قبل الكود.

### 12.3 الجدول الأسبوعي

| الأسبوع | A — الخادم | B — الشبكات | C — التطبيق | D — DevOps/QA |
|---|---|---|---|---|
| **1** | هيكل `backend/`، الإعدادات، النماذج والترحيلات، argon2id + JWT، `auth`/`me`/`devices`؛ نقطة فحص قابلية الوصول المصغّرة لخدمة نموذج B | نموذج `SslStream` بشهادات مؤقتة على Win10/11 (TLS 1.2/1.3، حذف المفاتيح)؛ Mono.Nat على راوترين أو ثلاثة؛ أداة Console للاتصال المتماثل؛ نشر واجهات `Core` | نموذج WPF (Tray، Toast بأزرار، نسخة واحدة، تشغيل مع الدخول) → ADR الواجهة؛ هيكل `App` + DI + Serilog | المستودع والهيكل، CI للخادم والعميل، **طلب شهادة توقيع الكود**، VPS للاختبار (staging) بـ Compose أولي |
| **2** | `domains` بالإصدارات، `admin_*`، rate limit وقفل الحساب، CLI، Dockerfile، اختبارات التكامل على PostgreSQL | نموذج Nerdbank مقابل Framing يدوي → ADR؛ Proxy أولي مع صفحة الفحص؛ مصفوفة تشغيل المتصفح (Chrome/Edge × مُدار/غير مُدار × Win10/11)؛ **تشغيل 10 أزواج حقيقية وتجميع التشخيص** | `ApiClient` + `DpapiSecretStore` + `DeviceInfoProvider`؛ شاشة الدخول ضد خادم A على staging؛ `MockControlChannel` من `ws-protocol.md` | قاعدة Firewall عبر مثبّت Inno Setup أولي؛ نشر خادم A على staging؛ مسودة `acceptance-checklist.md` |
| | **معلم M0:** العقود مجمّدة، 4 قرارات ADR (UI، Mux، TLS، Relay)، **قرار بوابة Relay** على بيانات حقيقية، الدخول يعمل ضد staging | | | |
| **3** | WS router + `ConnectionManager` + النبض + `hello`؛ Presence وبث المضيفين؛ الطلبات (إنشاء/رد/إلغاء/انتهاء) | `Core`: `AllowlistMatcher`, `IpRangePolicy`, `SessionStateMachine` باختبارات كاملة؛ `Tunnel`: `TlsTunnelFactory`, `AuthHandshake` | `ControlChannel` الحقيقي (إعادة اتصال، نبض، Proxy النظام) ضد A؛ الشاشة الرئيسية وقائمة المضيفين | سكربتات اختبارات الأمان (nmap، CONNECT لعناوين خاصة، فحص `session_keys`)، مصفوفة أجهزة الاختبار |
| **4** | آلة حالة الجلسة كاملة: `created`/`endpoint`/`peer_endpoint`/`connected`/`active`/`terminate`، المؤقتات، الانقطاع، حذف `session_keys`، `connect_diagnostics`، فحص قابلية الوصول | `Tunnel`: `CandidateGatherer`, `TunnelListener`, `CandidateDialer`, `MuxAdapter` باختبارات الـ Pipe؛ بدء `Egress` | نافذة الطلب الوارد بالإفصاح الكامل + Toast + Top-most؛ شاشة الانتظار؛ `SessionPanel` على `FakeTunnelSession` | staging محدّث آليًا من CI؛ النسخ الاحتياطي والاستعادة موثقان ومجرّبان |
| | **معلم M1:** اختبار WS آلي بعميلين وهميين يمر كاملًا على A؛ وحدات `Core`/`Tunnel` خضراء؛ التطبيق يعرض المضيفين ويرسل طلبًا ويستقبله على staging | | | |
| **5** | الإحصاءات والنطاقات، `allowlist.updated`، `admin/sessions/terminate`، `admin/diagnostics`؛ تقوية وتنظيف؛ **إن فُتحت بوابة Relay: بناء خدمة Relay (asyncio، توكن موقّع، اقتران مقبسين)** | `Egress`: `EgressPolicy`, `OpenHandler`, `SafeConnector`, الحدود، العدّادات؛ `Proxy`: CONNECT، http 307، طلب/اتصال، `OwnerPidChecker` | الإعدادات، Tray كامل، العدّاد الرتيب، قاعدة انقطاع WS للمضيف، إغلاق التطبيق أثناء الجلسة؛ ربط `SessionStateMachine` بـ `IControlChannel` الحقيقي | تشغيل runbook كاملًا على VPS نظيف؛ فحص SmartScreen للمثبّت الموقّع |
| **6** | دعم Relay في البروتوكول إن لزم (`session.relay` بتوكن)؛ مراجعة أمنية للقسم 14 من جهة الخادم؛ تحميل WS (مئات الاتصالات) | `Browser`: `BrowserLocator`, `PolicyDetector`, `BrowserLauncher` بـ Job Object, `GracefulCloser`؛ `RelayTransport` إن لزم؛ **أداة Console تثبت `curl --proxy` بين جهازين مع IP المضيف وتشغيل Chrome على صفحة الفحص** | ربط مكتبات B الحقيقية في التطبيق (Tunnel/Proxy/Egress/Browser) بدل الـ Fakes؛ ترتيب التنظيف (7.6) | اختبارات الأمان الآلية تعمل في CI ضد staging؛ تجهيز جهازي اختبار (Win10 وWin11) على شبكتين |
| | **معلم M2:** أول جلسة كاملة حقيقية بين جهازين عبر التطبيق: طلب ← قبول ← اتصال ← صفحة الفحص تعرض IP المضيف ← قطع | | | |
| **7** | إصلاحات التكامل؛ ضبط المهلات؛ توثيق OpenAPI النهائي | إصلاحات التكامل؛ الأداء (صفحات ثقيلة، فيديو، 256 stream، RTT دولي)؛ تحذير VPN | حالات الحافة كلها (8.5)؛ صقل الواجهة والرسائل؛ زر إعادة تشغيل المتصفح | **E2E على المصفوفة** (Win10/11 × Chrome/Edge × شبكات منزل/مكتب/هاتف)؛ تسجيل الأخطاء وترتيبها |
| **8** | مراجعة القسم 14 بندًا بندًا (مشتركة)؛ إصلاحات | المراجعة الأمنية للنفق (مشتركة)؛ Fuzz للإطارات؛ إصلاحات | إصلاحات؛ تجربة المستخدم النهائية | تشغيل قائمة القبول الـ 18 كاملة على staging؛ `certutil` وWireshark و`docker stats` على `api` و`relay` |
| | **معلم M3:** قائمة القبول الـ 18 خضراء على staging وكل الاختبارات الآلية خضراء | | | |
| **9** | نشر الإنتاج، `alembic upgrade`، المسؤول الأول، المستخدمون الأوائل | دعم التشغيل الأول | دعم التشغيل الأول | المثبّت النهائي الموقّع، VPS الإنتاج، Caddy، النسخ الاحتياطي، runbook النهائي، **تشغيل القبول على الإنتاج** |
| | **معلم M4 (نهاية الإصدار الأول):** مثبّت موقّع + إنتاج يعمل + وثائق + تقرير `connect_diagnostics` | | | |

### 12.4 قواعد العمل المشترك
- كل مسار يدمج في الفرع الرئيسي يوميًا خلف CI أخضر؛ لا فرع يعيش أكثر من 3 أيام.
- مراجعة الكود متقاطعة: A يراجع تغييرات C على `ControlChannel`، وB يراجع تغييرات C على ربط النفق، وC يراجع واجهات `Core` من B.
- اجتماع تكامل قصير أسبوعيًا عند كل معلم؛ ما يفشل في المعلم يُصلَح قبل بدء الأسبوع التالي.
- Relay المشروط (بوابة 7.7) يُنفَّذ داخل الأسبوعين 5 و6 على مسارَي A وB دون تمديد الجدول.

**الإجمالي:** 9 أسابيع تقويمية بثلاثة مطورين ومسار DevOps/QA. بمطور واحد فقط تصبح المراحل متسلسلة (نحو 13 أسبوعًا)، والترتيب الطبيعي حينها: 0 (النماذج) → 1 و2 (الخادم) → 3 (الشبكات) → 4 (التطبيق) → 5 → 6.

---

## 13. المخاطر وخطط التخفيف

| الخطر | الأثر | التخفيف |
|---|---|---|
| فشل الاتصال المباشر (CGNAT، شبكات الشركات، لا UPnP) | يمنع الاستخدام | اتصال متماثل + فحص من الخادم + نموذج بـ 10 أزواج + بوابة Relay + `ITunnelTransport` جاهز للـ Relay |
| TLS 1.3 غير متاح على Windows 10 | خطأ عند الاتصال | `SslProtocols.None` مع حد أدنى 1.2 وتسجيل الإصدار؛ اعتماد التعديل من صاحب المنتج |
| Windows Firewall يحجب المستمع صامتًا (خاصة على الملف Public والأجهزة المُدارة) | يُشخَّص خطأً كفشل NAT | قاعدة من المثبّت المرتفع + فحص عند «متاح» + `firewall_rule_present` في التشخيص |
| سياسات Chrome/Edge المؤسسية تتجاوز `--proxy-server` أو `--user-data-dir` | جلسة تبدو ناجحة وتتصفح بـ IP المستخدم | `PolicyDetector` قبل التشغيل + صفحة الفحص بعده + إنهاء بـ `browser_not_proxied` |
| تأخر شهادة التوقيع | SmartScreen يمنع التثبيت | الطلب في اليوم الأول |
| برامج الحماية | بلاغات كاذبة | التوقيع، loopback فقط، توثيق السلوك |
| المضيف على VPN | IP الخروج هو IP الـ VPN | كشف المحوّل + تحذير + تسجيل |
| استنزاف موارد المضيف من المستخدم | تباطؤ جهاز المضيف | حدود الـ streams والفتح/ثانية |
| worker واحد | سقف للاتصالات المتزامنة | كافٍ للمئات؛ Redis Pub/Sub موثق للترقية |

---

## 14. خارج النطاق صراحة (القسم 9 من الوثيقة)
التحكم عن بُعد، مشاركة الشاشة، نقل الملفات، الوصول للشبكة المحلية، تمرير الجهاز بالكامل، الهواتف، macOS/Linux، أكثر من مستخدم لكل مضيف، الاشتراكات، لوحة الإدارة على الويب، فك تشفير HTTPS، تسجيل المحتوى، VPN دائم، إعادة الاتصال بعد الانقطاع القصير، TCP hole punching، QUIC.

---

## 15. التحقق النهائي

1. `docker compose up` على VPS؛ إنشاء مسؤول ومستخدمين عبر CLI؛ إضافة `ifconfig.me` و`whatismyipaddress.com` للقائمة.
2. تثبيت المثبّت الموقّع على جهازي Windows (أحدهما Windows 10) في شبكتين مختلفتين وتسجيل الدخول.
3. تنفيذ `docs/acceptance-checklist.md` (18 بندًا) وتوثيق النتيجة مع لقطات شاشة، بما فيها صفحة الفحص التي تعرض IP المضيف.
4. الفحوص التقنية: `session_keys` فارغ بعد الإنهاء؛ لا حاويات مفاتيح متبقية؛ السجلات بلا URL أو محتوى؛ حركة الخادم لا تزيد أثناء فيديو في متصفح العمل؛ nmap على منفذ المضيف لا يقبل غير TLS ويقطع بلا مصادقة؛ CONNECT إلى عناوين خاصة مرفوض.
5. اجتياز CI كاملًا (pytest + xUnit) على الفرع الرئيسي.
6. ملخص `connect_diagnostics` يوثق نسبة نجاح الاتصال المباشر وقرار بوابة Relay.
