# عقد WebSocket: `/ws`

الإصدار: 1 (مجمّد نهاية الأسبوع 1). أي تغيير يمر بمراجعة المسارين A وC وتحديث هذا الملف قبل الكود.

## 1. الاتصال والمصادقة

- المسار: `wss://<host>/ws` (بلا معاملات في URL؛ التوكن لا يُوضع في Query String).
- الرسائل نص JSON، كائن واحد لكل إطار WebSocket. الترميز UTF-8.
- أول رسالة من العميل **يجب** أن تكون `hello` خلال 5 ثوانٍ من فتح الاتصال، وإلا يغلق الخادم بالكود `4401`.
- اتصال واحد لكل جهاز: اتصال جديد بنفس `device_id` يغلق القديم بالكود `4409`.
- النبض: الخادم يرسل `ping` كل 20 ثانية؛ العميل يرد `pong` فورًا. العميل أيضًا قد يرسل `ping` ويرد الخادم `pong`. ضياع نبضتين متتاليتين (40 ثانية) = انقطاع.
- الانقطاع يُعامل فورًا: `presence.connected=false`، وأي جلسة غير منتهية لهذا الجهاز تُنهى بـ `guest_disconnected` أو `host_disconnected` ويُرسل `session.terminate` للطرف الآخر.

## 2. المظروف

كل رسالة كائن JSON بحقل `type` إلزامي. رسائل العميل التي تتوقع ردًا تحمل `ref` (نص يولّده العميل، فريد داخل الاتصال). الخادم يرد إما بالرسالة المتوقعة أو بـ `error` يحمل نفس `ref`.

```json
{ "type": "request.create", "ref": "c1", "host_device_id": "…", "duration_min": 30 }
{ "type": "error", "ref": "c1", "code": "host_unavailable", "message": "…" }
```

أكواد `error`: `unauthorized`, `bad_request`, `not_found`, `host_unavailable`, `session_exists`, `request_pending`, `forbidden`, `rate_limited`, `internal`.

`rate_limited` يُستخدم كذلك على القناة نفسها (ثُبّت في الأسبوع 5): لكل اتصال ميزانية إطارات (100 إطار في 10 ثوانٍ)؛ تجاوزها يرد `rate_limited` **بلا قطع الاتصال**، حمايةً للعامل الواحد من جهاز مصادَق يغرق حلقة الأحداث.

متى يُستخدم كل كود في `request.create` (ثُبّت في الأسبوع 3):

| الحالة | الكود |
|---|---|
| للمستخدم جلسة غير منتهية | `session_exists` |
| للمستخدم طلب `pending` بالفعل | `request_pending` |
| المضيف غير متصل، أو غير مفعّل «متاح»، أو مشغول بجلسة، أو يردّ على طلب آخر | `host_unavailable` |
| `host_device_id` لا يقابل جهازًا | `not_found` |
| الجهاز المطلوب من أجهزة المستخدم نفسه | `bad_request` |
| غير المضيف المخاطَب يحاول القبول أو الرفض | `forbidden` |

الأوقات كلها ISO-8601 بتوقيت UTC مع اللاحقة `Z`. المعرّفات UUID نصية.

## 3. رسائل العميل إلى الخادم

| النوع | الحقول | ملاحظات |
|---|---|---|
| `hello` | `token` (access JWT), `device_id`, `app_version`, `diagnostics` (كائن اختياري: `firewall_rule_present`, `firewall_profile`, `vpn_adapter`, `vpn_holds_default_route`, `system_proxy_present`, `os_build`, `ipv6_global`) | الرد `hello.ack` أو إغلاق `4401` |
| `host.available` | `available` (bool), `listen_port` (int اختياري عند `true`) | يحدّث `presence.is_available_host`. مع `listen_port` يشغّل الخادم فحص قابلية الوصول |
| `request.create` | `ref`, `host_device_id`, `duration_min` (1..max_session_minutes) | يرد `request.created {ref, request_id, expires_at}` ثم لاحقًا `request.result` |
| `request.cancel` | `ref`, `request_id` | فقط بحالة `pending` |
| `request.accept` | `ref`, `request_id` | من المضيف فقط |
| `request.reject` | `ref`, `request_id` | من المضيف فقط |
| `session.endpoint` | `session_id`, `cert_fp_sha256` (hex صغير، 64 حرفًا), `candidates` (مصفوفة `{type, ip, port}`؛ `type` من `lan`/`upnp`/`public`/`v6`) | من الطرفين بعد `session.created` |
| `session.connected` | `session_id`, `winner_type`, `connect_ms`, `tls_version` (`1.2`/`1.3`) | **من المضيف فقط**؛ يحوّل الجلسة إلى `active` |
| `session.connect_failed` | `session_id`, `diagnostics` (كائن حر: المرشحون المجرَّبون، زمن وخطأ كل مرشح) | من أي طرف؛ الخادم ينهي بـ `connect_failed` |
| `session.stats` | `session_id`, `bytes_up`, `bytes_down` | من المضيف كل 30 ثانية |
| `session.end` | `session_id`, `reason` (`guest_ended`/`host_ended`), `bytes_up`, `bytes_down`, `domains` (مصفوفة نصوص، قد تكون فارغة) | يُقبل من أي طرف في الجلسة |
| `ping` / `pong` | لا حقول | |

## 4. رسائل الخادم إلى العميل

| النوع | الحقول | إلى من |
|---|---|---|
| `hello.ack` | `server_time`, `public_ip` (كما يراه الخادم), `settings` (`max_session_minutes`, `request_timeout_seconds`, `allowed_ports`, `log_domains`, `enforce_allowlist`), `allowlist_version` | العميل بعد `hello` |
| `hosts.snapshot` | `hosts`: مصفوفة `{device_id, user_display_name, device_name, reachable (true/false/null)}` | بعد `hello.ack` |
| `hosts.update` | `hosts` (المصفوفة الكاملة الجديدة) | كل العملاء عند أي تغير |
| `request.created` | `ref`, `request_id`, `expires_at` | المستخدم |
| `request.incoming` | `request_id`, `guest_name`, `guest_device`, `duration_min`, `allowlist_version`, `expires_at` | المضيف |
| `request.result` | `request_id`, `accepted` (bool), `reason` (`rejected`/`expired`/`cancelled`/`host_unavailable` عند `false`), `session_id` (عند `true`) | المستخدم |
| `request.expired` | `request_id` | المضيف: إطار «أغلق نافذة الطلب» عمومًا، يُرسل عند انتهاء المهلة وعند إلغاء المستخدم وعند انقطاعه |
| `session.created` | `session_id`, `role` (`guest`/`host`), `secret_b64` (32 بايت Base64), `expires_at`, `allowlist_version`, `peer_public_ip`, `same_public_ip` (bool), `peer` (`{user_display_name, device_name}`), `relay` (`{address, port, token}` أو `null`) | الطرفان |
| `session.peer_endpoint` | `session_id`, `cert_fp_sha256`, `candidates` | الطرف الآخر لمن أرسل `session.endpoint` |
| `session.active` | `session_id`, `expires_at` | الطرفان |
| `session.terminate` | `session_id`, `reason` | الطرفان (أو الطرف الباقي) |
| `allowlist.updated` | `version` | كل العملاء |
| `error` | `ref` (اختياري), `code`, `message` | العميل المعني |
| `ping` / `pong` | لا حقول | |

**كائن `relay` في `session.created`** (أُضيف في 2026-09-07 بـ [ADR-0009](decisions/0009-relay-default.md)): `{address, port, token}`، أو `null` إن لم يُضبط Relay في النشر — وهو نشر مدعوم يعني «المباشر وحده» كما كان قبل القرار.

- `token` موقَّع بـ HS256 ومربوط **بالجلسة وبالدور معًا** وقصير العمر. بدون ربط الدور يستطيع حامل توكن واحد فتح طرفَي الـ Relay والاقتران بنفسه، فيحتل الجلسة ويحرم نظيره منها.
- `token` **بيان حامل** حتى انتهاء صلاحيته: لا يُسجَّل في أي سجل، ولا يظهر في تشخيص، ولا يُخزَّن على الخادم (الـ Relay يتحقق من التوقيع بدل أن يبحث عن الجلسة، فلا شيء يحتاج البقاء).
- كل طرف يتلقى **توكنه هو**؛ توكنان مختلفان في الجلسة الواحدة.

## 5. دورة حياة الجلسة (الخادم مرجع الحالة)

```
request(pending, 60s)
  ├─ reject / cancel / timeout ──▶ request(rejected|cancelled|expired) + request.result(false)
  └─ accept ──▶ session(connecting) + session.created للطرفين
session(connecting)
  ├─ الطرفان يرسلان session.endpoint → الخادم يمرر كل واحد للآخر كـ session.peer_endpoint
  ├─ المضيف يرسل session.connected ──▶ session(active) + session.active للطرفين
  ├─ أي طرف يرسل session.connect_failed ──▶ ended(connect_failed)
  └─ مهلة 30 ثانية من created ──▶ ended(connect_failed)
session(active)
  ├─ مؤقت expires_at ──▶ ended(expired)
  ├─ session.end من طرف ──▶ ended(guest_ended|host_ended)
  ├─ انقطاع WS لطرف ──▶ ended(guest_disconnected|host_disconnected)
  └─ إنهاء المسؤول ──▶ ended(admin_terminated)
ended: حذف session_keys، حفظ الإحصاءات والنطاقات، إرسال session.terminate للطرف/الطرفين، تحديث presence
```

أسباب الإنهاء: `guest_ended`, `host_ended`, `expired`, `guest_disconnected`, `host_disconnected`, `connect_failed`, `admin_terminated`, `browser_not_proxied`, `protocol_error`.

قواعد:
- جلسة واحدة غير منتهية لكل مستخدم ولكل جهاز؛ أكواد فشل `request.create` مفصّلة في جدول القسم 2.
- المضيف الذي لديه جلسة غير منتهية لا يظهر في `hosts.*` حتى تنتهي.
- `session.connected` من المستخدم يُتجاهل مع `error(forbidden)`.

## 6. فحص قابلية الوصول

عند `host.available {available:true, listen_port}`: الخادم ينفذ اتصال TCP إلى `presence.public_ip:listen_port` بمهلة 3 ثوانٍ ويحدّث `presence.reachable` ويبث `hosts.update`. لا يُرسل بايت واحد؛ الاتصال يُغلق فور نجاح المصافحة TCP. يستخدم النموذج التقني الطريقة نفسها عبر `POST /api/v1/probe`.

## 7. أكواد الإغلاق

| الكود | المعنى |
|---|---|
| `4401` | لم تصل `hello` صالحة في الوقت |
| `4403` | الجهاز ملغى أو المستخدم معطّل |
| `4409` | اتصال أحدث من الجهاز نفسه |
| `1012` | إعادة تشغيل الخادم؛ أعد الاتصال بتراجع أسّي (1، 2، 4… حتى 30 ثانية) |
| `1001` | انتهت مهلة النبض (لا `pong` خلال 40 ثانية)؛ عابر، أعد الاتصال |
| `1011` | عطل غير متوقع أثناء المصافحة؛ عابر، أعد الاتصال |

## 8. تفاصيل ثُبّتت في الأسبوع 3

- **`hosts.update` لكل مستلم على حدة** لا إطار واحد مشترك، لأن القائمة تستثني أجهزة المستلم نفسه. `GET /hosts` يستثنيها كذلك ليطابق `hosts.snapshot` تمامًا (تغيّر سلوك عن الأسبوع 2).
- **`ref` في `request.cancel` و`request.accept` و`request.reject`** لا يقابله رد إيجابي؛ يُستخدم لربط رسالة `error` فقط.
- **`peer_public_ip`** يكون سلسلة فارغة عند تعذّر معرفته، لا `null`.
- **`hello.app_version`** اختياري: حقل تجميلي لا يجوز أن يمنع عميلًا من الاتصال.
- **`hello.diagnostics`** يُحفظ في `connect_diagnostics` بـ `session_id` فارغ؛ ما يتجاوز 64 KB يُهمَل مع تحذير ولا يقطع الاتصال. الكائن مفتوح: المفاتيح المذكورة هي المتوقعة، وأي مفتاح إضافي يُخزَّن كما هو.

`cgnat_suspected` كان يُحسب من تقرير بوابة UPnP وحدها، فحين لا توجد بوابة تُسأل — وهو حال **نقطة اتصال الهاتف، حيث يعيش الـ CGNAT فعلًا** — كان يعود `false`: القيمة نفسها التي تعني «فُحص ولا يوجد». أُضيف في الأسبوع 8 مفتاحان يفصلان الملاحظة عن الصمت، على منوال `vpn_adapter`/`vpn_holds_default_route`:

- **`cgnat_checked`**: هل أمكن الحكم أصلًا. حين يكون `false` فإن `cgnat_suspected` تعني **«لا نعرف»** لا «لا يوجد».
- **`nat_reachability`**: `open` (الجهاز يحمل العنوان العام) · `mapped` (NAT واحد ردّ وفتح المنفذ) · `blocked` (لن يعمل المباشر) · `unknown`. وهذا يُجاب غالبًا **حتى حين يتعذّر الحكم على الـ CGNAT**: إن رأى الخادم عنوانًا لا يحمله الجهاز، فالوارد محجوب أيًّا كان شكل الـ NAT فوقه.
- **`nat_evidence`**: الملاحظة التي قام عليها الحكم، نصًّا، ليُقرأ الصف بعد شهور.

`cgnat_suspected` صار يُرفع أيضًا بلا بوابة: **عنوان محلي داخل `100.64.0.0/10`** هو CGNAT منطوقًا به.

`vpn_adapter` يقول إن محوّل VPN **موجود**، و`vpn_holds_default_route` (أُضيف في الأسبوع 6) يقول إنه **يملك مسار الخروج فعلًا** — وهذه وحدها هي التي تتنبأ بأن المواقع سترى عنوانًا غير المتوقع. العميل يحذّر المضيف على الثانية لا الأولى، والفصل بينهما يجعل الخادم قادرًا على تفسير جلسة بعد وقوعها.
- **`X-Forwarded-For`** يُحترم فقط إذا كان النظير عنوانًا خاصًا أو loopback (أي الـ Proxy أمامنا)، فلا يستطيع عميل مباشر تزوير `public_ip` وهو هدف فحص قابلية الوصول.
- **فحص قابلية الوصول** لا يُجرى إلا على عنوان عام؛ غير ذلك يبقى `reachable = null` (غير معروف).
- **الطلبات التي تُسوّى بانقطاع** تُخزَّن بحالة `cancelled` مع `responded_at`، بينما السبب على السلك `host_unavailable` للمستخدم و`request.expired` للمضيف.
- **إلغاء تسجيل الجهاز** يُغلق قناته فورًا بالكود `4403` ويصفّر حضوره.
- **رد `error` على `RequestAsync`:** قناة التحكم الحقيقية في العميل ترفع استثناءً يحمل الكود، بينما القناة المحاكية تعيد رسالة `error`. المستهلك يتعامل مع الحالتين حتى تُسحب المحاكية.

## 9. تفاصيل ثُبّتت في الأسبوع 4 (رسائل الجلسة)

**أكواد الخطأ لرسائل الجلسة**، بالترتيب الذي تُفحص به:

| الحالة | الكود |
|---|---|
| `session_id` غير معروف، أو الجلسة منتهية | `not_found` |
| المرسل ليس طرفًا في الجلسة | `forbidden` |
| الدور لا يسمح بالرسالة (مستخدم يرسل `session.connected` أو `session.stats`) | `forbidden` |
| الجلسة حية لكن الحالة لا تناسب الرسالة | `bad_request` |

**التحقق من الحقول:**
- `candidates[].ip` عنوان IP حرفي (v4 أو v6) لا اسم مضيف، ويُخزَّن بصيغته المعيارية. الحد 16 مرشحًا، والمكرر هو تطابق `(type, ip, port)` الثلاثي؛ نفس `ip:port` بنوعين مختلفين (`upnp` و`public`) مسموح.
- `winner_type`: `lan|upnp|public|v6|relay` — **وُسِّعت في 2026-09-07 بـ [ADR-0009](decisions/0009-relay-default.md)**. وهي مفردات أوسع من `candidates[].type` عمدًا: `relay` ليس مرشحًا يُطلب الاتصال به (لا يظهر في `session.endpoint` أبدًا، ويُرفض فيها بـ `bad_request`)، لكنه جواب صحيح على «ما الذي حمل الجلسة»، وبدونه يعمى تقرير `admin/diagnostics` عن النقل الذي يعمل فعلًا.
- `connect_ms` ≤ 2³¹−1، والبايتات ≤ 2⁶³−1 (مطابقة لأنواع الأعمدة)؛ التجاوز `bad_request`.
- `session.end.domains` اختياري ويُعامل كقائمة فارغة عند غيابه: لا يُرفض إنهاء بسبب تفصيل ثانوي. `session.connect_failed.diagnostics` إلزامي.
- `session.endpoint` مقبولة في حالة `connecting` فقط؛ بعد `active` تُرد `bad_request`.

**سلوك التبادل:** الطرف الثاني في تبادل نقاط النهاية يستلم نقطة نظيره مرتين (التمرير الأول ثم الرد المستقل عن الترتيب). العملية عديمة الأثر عند العميل، وكبتها يعيد ثغرة المنضم المتأخر.

**المؤقتات:** كلاهما يُجدوَل عند القبول: مهلة الاتصال (`connect_timeout_seconds`) ومؤقت `expires_at`. `session.connected` يعيد تسليح مؤقت الانتهاء بلا أثر مزدوج، فيغطي المؤقت أيضًا جلسة عالقة في `connecting`. الإلغاء يقع داخل مسار الإنهاء الوحيد فلا يتفرّق عن حذف `session_keys` وبث `session.terminate`.

**`session.active.expires_at`** يكرر القيمة نفسها من `session.created`: التفعيل لا يحرّك الموعد.

**أسباب `session.end` التي يجوز لعميل إرسالها** (ثُبّت بعد اكتشاف تعارض بين المسارين في الأسبوع 4):

| السبب | من يرسله | لماذا |
|---|---|---|
| `guest_ended` / `host_ended` | صاحب الاسم نفسه | المستخدم أوقف الجلسة بنفسه |
| `guest_disconnected` / `host_disconnected` | **النظير** لا صاحب الاسم | مات النفق بينما بقيت قناتا التحكم حيتين، وهذه اللحظة الوحيدة التي لا يستطيع الخادم رصدها بنفسه |
| `browser_not_proxied` | أي طرف | متصفح العمل لم يمر عبر الـ Proxy |
| `protocol_error` | أي طرف | خلل في البروتوكول |

و`expired` و`connect_failed` و`admin_terminated` **أحكام الخادم وحده**: له مؤقتاته ومهلته وأمر مسؤوله، ويرد `bad_request` على أي عميل يدّعيها. لذلك عند انتهاء المدة يفكك العميل جلسته محليًا فورًا بلا `session.end`، وينتظر `session.terminate` الذي يصدر خلال فارق الساعتين لأن المؤقتين مشتقان من `expires_at` نفسه.

**سجل النطاقات:** تُخفَّض الأحرف، وتُحذف النقطة الأخيرة، وتُتجاهل المدخلات الفارغة أو ذات المسافات أو المحارف الضابطة أو التي تتجاوز 255 حرفًا، وبحد أقصى 200 نطاق مميز لكل جلسة.

**`connect_result`** يأخذ `ok` أو `failed` أو `timeout`، والأخير يُحسب ضمن حالات الفشل في ملخص `GET /admin/diagnostics`.
