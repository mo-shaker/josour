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
| `hello` | `token` (access JWT), `device_id`, `app_version`, `diagnostics` (كائن اختياري: `firewall_rule_present`, `firewall_profile`, `vpn_adapter`, `system_proxy_present`, `os_build`, `ipv6_global`) | الرد `hello.ack` أو إغلاق `4401` |
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
| `hello.ack` | `server_time`, `public_ip` (كما يراه الخادم), `settings` (`max_session_minutes`, `request_timeout_seconds`, `allowed_ports`, `log_domains`), `allowlist_version` | العميل بعد `hello` |
| `hosts.snapshot` | `hosts`: مصفوفة `{device_id, user_display_name, device_name, reachable (true/false/null)}` | بعد `hello.ack` |
| `hosts.update` | `hosts` (المصفوفة الكاملة الجديدة) | كل العملاء عند أي تغير |
| `request.created` | `ref`, `request_id`, `expires_at` | المستخدم |
| `request.incoming` | `request_id`, `guest_name`, `guest_device`, `duration_min`, `allowlist_version`, `expires_at` | المضيف |
| `request.result` | `request_id`, `accepted` (bool), `reason` (`rejected`/`expired`/`cancelled`/`host_unavailable` عند `false`), `session_id` (عند `true`) | المستخدم |
| `request.expired` | `request_id` | المضيف: إطار «أغلق نافذة الطلب» عمومًا، يُرسل عند انتهاء المهلة وعند إلغاء المستخدم وعند انقطاعه |
| `session.created` | `session_id`, `role` (`guest`/`host`), `secret_b64` (32 بايت Base64), `expires_at`, `allowlist_version`, `peer_public_ip`, `same_public_ip` (bool), `peer` (`{user_display_name, device_name}`) | الطرفان |
| `session.peer_endpoint` | `session_id`, `cert_fp_sha256`, `candidates` | الطرف الآخر لمن أرسل `session.endpoint` |
| `session.active` | `session_id`, `expires_at` | الطرفان |
| `session.terminate` | `session_id`, `reason` | الطرفان (أو الطرف الباقي) |
| `allowlist.updated` | `version` | كل العملاء |
| `error` | `ref` (اختياري), `code`, `message` | العميل المعني |
| `ping` / `pong` | لا حقول | |

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
- **`hello.diagnostics`** يُحفظ في `connect_diagnostics` بـ `session_id` فارغ؛ ما يتجاوز 64 KB يُهمَل مع تحذير ولا يقطع الاتصال.
- **`X-Forwarded-For`** يُحترم فقط إذا كان النظير عنوانًا خاصًا أو loopback (أي الـ Proxy أمامنا)، فلا يستطيع عميل مباشر تزوير `public_ip` وهو هدف فحص قابلية الوصول.
- **فحص قابلية الوصول** لا يُجرى إلا على عنوان عام؛ غير ذلك يبقى `reachable = null` (غير معروف).
- **الطلبات التي تُسوّى بانقطاع** تُخزَّن بحالة `cancelled` مع `responded_at`، بينما السبب على السلك `host_unavailable` للمستخدم و`request.expired` للمضيف.
- **إلغاء تسجيل الجهاز** يُغلق قناته فورًا بالكود `4403` ويصفّر حضوره.
- **رد `error` على `RequestAsync`:** قناة التحكم الحقيقية في العميل ترفع استثناءً يحمل الكود، بينما القناة المحاكية تعيد رسالة `error`. المستهلك يتعامل مع الحالتين حتى تُسحب المحاكية.
