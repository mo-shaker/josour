# دليل النموذج التقني: قياس الاتصال المباشر على 10 أزواج حقيقية

الهدف: تغذية بوابة قرار Relay (ADR-0003) ببيانات حقيقية بنهاية الأسبوع 2. الأداة: `client/tools/RouteBridge.Spike`.

## المتطلبات على كل جهاز
- Windows 10 أو 11 مع .NET 8 SDK (أو نشر الأداة كـ self-contained من CI).
- السماح للتنفيذي في Windows Firewall عند أول تشغيل (أو تثبيت المثبّت الذي يضيف القاعدة).
- معرفة IP العام للجهاز كما يراه الإنترنت (`curl ifconfig.me`).

## 1. فحص الشهادة وTLS (كل جهاز على حدة)
```
dotnet run --project tools/RouteBridge.Spike -c Release -- certtest
```
المتوقع: `tls_loopback: ok (server 1.3 …)` على Windows 11 و`1.2` على Windows 10، و`disposed: True`. سجّل الإصدار.

## 2. المرشحون وUPnP (كل جهاز على حدة)
```
dotnet run --project tools/RouteBridge.Spike -c Release -- gather --port 40000 --public-ip <IP العام>
```
سجّل من JSON: `upnp_found`, `mapping_ok`, `upnp_external_ip`, `cgnat_suspected`, `ipv6_global`, `firewall_rule_present`, `vpn_adapter`, `system_proxy_present`.

## 3. الاتصال المتماثل (زوج من جهازين)
ولّد معرّف جلسة وسرًا مشتركين مرة واحدة وشاركهما مع الطرف الآخر:
- PowerShell: `[guid]::NewGuid()` و`[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`
- macOS/Linux: `uuidgen` و`head -c 32 /dev/urandom | base64`

الجهاز A (المضيف):
```
dotnet run --project tools/RouteBridge.Spike -c Release -- symmetric --role host --session <GUID> --secret-b64 <SECRET> --public-ip <IP A> --peer-file endpoint-guest.json
```
الجهاز B (المستخدم):
```
dotnet run --project tools/RouteBridge.Spike -c Release -- symmetric --role guest --session <GUID> --secret-b64 <SECRET> --public-ip <IP B> --peer-file endpoint-host.json
```
كل طرف يطبع سطر JSON بنقطة نهايته ويكتب `endpoint-<role>.json`. انقل ملف A إلى مجلد B والعكس (أو الصق سطر JSON على stdin بدون `--peer-file`). خيارات: `--same-public-ip` على الطرفين إن كانا خلف الـ IP العام نفسه، `--no-upnp` لتخطي الاكتشاف، `--timeout-s N` (افتراضي 30).

أكواد الخروج: 0 اتصال + hello ناجح، 2 لا اتصال، 3 اتصال لكن hello فشل.

## 4. ما يُسجَّل لكل زوج (جدول القياس)
| الحقل | المصدر |
|---|---|
| نوع شبكة كل طرف (منزل / مكتب / هاتف / CGNAT) | يدوي |
| `winner` و`tls_version` و`connect_ms` | JSON الناتج (اعتمد تسمية المضيف لـ `winner_type`) |
| المحاولات الفاشلة: `{direction,type,ip,port,ms,stage,error}` | JSON الناتج |
| `inbound_attempts` | JSON الناتج |
| تشخيص `gather` للطرفين | الخطوة 2 |

**عتبة البوابة:** 10 أزواج على الأقل خلال أسبوعين؛ إن كانت نسبة الاتصال خلال 10 ثوانٍ أقل من 85% تُنفَّذ خدمة Relay في الأسبوعين 5 و6.

## 5. ما يحتاج تحققًا على Windows وراوترات حقيقية (من تقرير المسار B)
1. مسار Schannel للمفتاح: إعادة استيراد PFX بـ `UserKeySet` وقبول `SslStream` له على Win10 و11، واختفاء حاوية المفتاح بعد `Dispose` (`certutil -user -key`).
2. تفاوض TLS 1.3 على Win11 وسلوك `SslProtocols.None` على Win10 (ADR-0002).
3. Mono.Nat على راوترين أو ثلاثة: الاكتشاف خلال 4 ثوانٍ، نجاح التعيين، هل يحترم الراوتر المنفذ المطلوب أم يعيد آخر، الإبلاغ عن IP الخارجي، إزالة التعيين.
4. صحة `cgnat_suspected` على شبكة CGNAT واحدة وشبكة NAT مزدوج واحدة على الأقل.
5. تصفية مرشحي IPv6 على Windows (العناوين المؤقتة وحالة DAD).
6. `HostDiagnostics` عبر `netsh`: كشف قاعدة «RouteBridge Tunnel» ومحلل `currentprofile` على Windows غير الإنجليزي، وقراءة `ProxyEnable`.
7. المستمع والمتصل بوضع DualMode على Windows، بما فيها أجهزة IPv6 معطّل (يسقط إلى IPv4).
8. تفاعل Windows Firewall: القبول الوارد على المنفذ المؤقت مع القاعدة وبدونها.
9. سلوك `Socket.Close(0)` للاتصالات الزائدة عن السعة على Windows.

---

# مصفوفة تشغيل المتصفح (الأسبوع 2)

الهدف: إثبات أن متصفح العمل يمر فعلًا عبر الـ Proxy المحلي، وكشف الحالات التي تتجاوز فيها سياسات الشركة سطر الأوامر. الأداة: `dotnet run --project tools/RouteBridge.Spike -c Release -- browser --self-hosted [--browser chrome|edge]`.

مع `--self-hosted` تشغّل الأداة `ConnectProxyServer` بقائمة مواقع فارغة (كل شيء مباشر) وتفتح المتصفح على `http://check.routebridge/`، ثم تطبع JSON يشمل: مسار المتصفح، التحقق من ناشره، حالة السياسات، نتيجة التشغيل، هل حدث Handoff، زمن وصول صفحة الفحص، وعدّادات الـ Proxy.

## الخانات الثماني المطلوب تعبئتها

| # | النظام | المتصفح | مُدار بسياسة | المتوقع |
|---|---|---|---|---|
| 1 | Windows 10 | Chrome | لا | `probe_hit_ms` < 10000، `handoff` = false |
| 2 | Windows 10 | Edge | لا | نفسه |
| 3 | Windows 11 | Chrome | لا | نفسه |
| 4 | Windows 11 | Edge | لا | نفسه |
| 5 | Windows 11 | Chrome | نعم (`ProxySettings`) | `policy.proxy_managed` = true، ولا تصل صفحة الفحص |
| 6 | Windows 11 | Edge | نعم (`ProxySettings`) | نفسه |
| 7 | Windows 11 | Chrome | نعم (`UserDataDir`) | `policy.user_data_dir_managed` = true |
| 8 | Windows 11 | Chrome | نسخة تعمل بالفعل بنفس الـ Profile | `handoff` = true |

لمحاكاة الإدارة في الخانات 5 إلى 7 (على جهاز اختبار فقط): أضف القيم تحت `HKLM\SOFTWARE\Policies\Google\Chrome` أو `\Microsoft\Edge` ثم احذفها بعد الاختبار.

## ما يُسجَّل لكل خانة
`browser`, `path`, `publisher_verified`, `policy.*`, `launch.success`, `launch.failure`, `handoff`, `exit_type_set`, `probe_hit_ms`, `close_ms`, و`proxy_counters`.

**معيار القبول:** الخانات 1 إلى 4 تعطي صفحة فحص خلال 10 ثوانٍ وإغلاقًا نظيفًا؛ الخانات 5 إلى 8 تُكتشف قبل التشغيل أو خلال 10 ثوانٍ فينتهي المسار بـ `browser_not_proxied` بدل جلسة تتصفح بعنوان المستخدم دون أن يدري.

---

# جلسة كاملة بلا واجهة: أمر `session` (الأسبوع 4)

الهدف: تشغيل **جلسة حقيقية بين جهازي Windows** بالمكدّس الإنتاجي كاملًا — تسجيل دخول عبر `ApiClient`/`AuthSession`، قناة تحكم حقيقية على WSS، `TunnelSession` بالدورين، سياسة الخروج على المضيف، والـ Proxy المحلي على المستخدم — قبل أن يجهز تطبيق WPF. الأداة هي نفسها `client/tools/RouteBridge.Spike`، والأمر `session`.

ما يُثبته هذا الأمر ولا يُثبته `symmetric`: أن الخادم والعميل يتفقان على دورة الجلسة كاملة (`session.created` ← `endpoint` ← `peer_endpoint` ← `connected` ← `active` ← `stats` ← `end`)، وأن حركة المرور تخرج فعلًا من عنوان المضيف.

## 0. المتطلبات

- جهازان (أو جهاز واحد بمجلدَي حالة مختلفين للتجربة الجافة)، على كل منهما .NET 8 SDK ونسخة من المستودع.
- حسابان مختلفان على الخادم (`POST /admin/users`). **جهاز المستخدم لا يستطيع طلب جهاز من أجهزة المستخدم نفسه** (`bad_request`).
- قائمة مواقع فيها نطاق واحد على الأقل لاختباره (`PUT /admin/domains`)، مثل `api.ipify.org`.
- السماح للتنفيذي في Windows Firewall عند أول تشغيل (نافذة الاتصال تفتح مستمعًا على منفذ مؤقت).

## 1. الأوامر بالضبط

على **جهاز المضيف** (الجهاز الذي ستخرج منه حركة المرور)، شغّل أولًا وأبقه يعمل:

```
dotnet run --project tools/RouteBridge.Spike -c Release -- session ^
  --api https://<الخادم> --email host@example.com --password '<كلمة السر>' ^
  --role host --available > host.jsonl
```

على **جهاز المستخدم**، تعرّف على المضيفين المتاحين ثم اطلب أحدهم:

```
dotnet run --project tools/RouteBridge.Spike -c Release -- session ^
  --api https://<الخادم> --email guest@example.com --password '<كلمة السر>' ^
  --role guest --list-hosts

dotnet run --project tools/RouteBridge.Spike -c Release -- session ^
  --api https://<الخادم> --email guest@example.com --password '<كلمة السر>' ^
  --role guest --host-device <معرّف الجهاز أو اسمه> --minutes 30 ^
  --curl-test https://api.ipify.org > guest.jsonl
```

المضيف يقبل الطلب تلقائيًا، فيتصل الطرفان، ثم يطبع المستخدم منفذ الـ Proxy المحلي وينفّذ طلب HTTP GET عبر الـ Proxy نفسه بلا متصفح. **الدليل المطلوب:** جسم الرد في حدث `curl.result` يساوي عنوان المضيف العام (`public_ip` في `hello.ack` على جهاز المضيف)، لا عنوان جهاز المستخدم.

خيارات إضافية:

| الخيار | الدور | المعنى |
|---|---|---|
| `--available` / `--no-available` | مضيف | الإعلان هو الافتراضي؛ `--no-available` يبقي الجهاز متصلًا وغير ظاهر لأي مستخدم (لاختبار القناة وحدها) |
| `--auto-reject` | مضيف | يرفض أول طلب وارد ثم يخرج بالكود 0 (لاختبار مسار الرفض) |
| `--listen-port N` | كلاهما | يربط مستمع النفق على منفذ ثابت؛ وعلى المضيف يُرسل أيضًا في `host.available.listen_port` فيصير فحص قابلية الوصول ذا معنى |
| `--browser chrome\|edge` | مستخدم | يشغّل متصفح العمل الحقيقي على صفحة الفحص بدل `--curl-test` (لا يجتمعان: مع متصفح يُفعَّل فحص PID المالك فيُرفض طلب الأداة نفسها) |
| `--profile <path>` | مستخدم | مجلد ملف تعريف المتصفح |
| `--minutes N` | مستخدم | مدة الجلسة المطلوبة (1..`max_session_minutes`) |
| `--no-upnp` | كلاهما | تخطي اكتشاف UPnP (أسرع على شبكة بلا راوتر متعاون) |
| `--connect-timeout-s N` | كلاهما | مهلة الاتصال المتماثل (الافتراضي 30، وهي مهلة الخادم نفسها) |
| `--stats-interval-s N` | مضيف | دورية `session.stats` (الافتراضي 30) |
| `--state-dir <path>` | كلاهما | مجلد الحالة بجوار التنفيذي (الافتراضي `spike-state/`): `settings.json` وسر الجهاز والـ refresh token. **هويتان على الجهاز نفسه = مجلدان مختلفان** |
| `--reset-device` | كلاهما | يمسح هوية الجهاز والتوكنات فيسجّل الدخول التالي جهازًا جديدًا (بعد إلغاء الجهاز من الخادم) |
| `--quiet` | كلاهما | يوقف الأسطر البشرية على stderr ويُبقي JSON فقط |

سر الجهاز يُحفظ في `<state-dir>/secrets/` بـ DPAPI على Windows، وبنص صريح موسوم على غير Windows (تطوير فقط). لا تنقل هذا المجلد بين الأجهزة.

`Ctrl+C` في أي لحظة ينفّذ ترتيب التنظيف في `docs/protocol.md` القسم 7 كاملًا ويرسل `session.end` بسبب الدور (`host_ended` أو `guest_ended`) بدل قتل العملية.

## 2. مجرى الأحداث: JSON على stdout وملخص بشري على stderr

كل حدث سطر JSON واحد. المظروف ثابت:

```json
{"ts":"2026-09-05T09:12:33.4210000Z","seq":14,"role":"guest","event":"tunnel.connected","winner_type":"upnp","connect_ms":812,"tls_version":"1.3","proxy_port":51544,"probe_url":"http://check.routebridge/"}
```

`ts` بـ ISO-8601 UTC، و`seq` عدّاد يبدأ من 1، و`role` هو `host` أو `guest`. الأسطر البشرية تذهب إلى stderr، فـ `> run.jsonl` يعطي ملفًا صالحًا للتحليل بينما تبقى المتابعة على الشاشة.

**الإعداد والدخول**

| الحدث | الحقول | المعنى |
|---|---|---|
| `tool.start` | `api`, `state_dir`, `os`, `reset_device` | بداية التشغيل |
| `auth.signed_in` | `user_id`, `email`, `display_name`, `device_id`, `device_name`, `app_version` | نجح الدخول؛ `device_id` هو ما يُكتب في `--host-device` على الطرف الآخر |
| `auth.failed` | `code`, `status`, `message` | فشل الدخول (`invalid_credentials`, `account_locked`, `device_revoked`, `unavailable`) |
| `channel.state` | `state` | `Connecting` / `Connected` / `Reconnecting` / `Disconnected` |
| `channel.closed` | `reason`, `close_code`, `description` | إغلاق نهائي لا إعادة اتصال بعده (4401 / 4403 / 4409) |
| `channel.connect_failed` | `error` | لم تُفتح القناة أصلًا |
| `hello.ack` | `server_time`, `public_ip`, `allowed_ports`, `allowlist_version`, `max_session_minutes`, `request_timeout_seconds`, `log_domains` | **`public_ip` هنا على جهاز المضيف هو العنوان الذي يجب أن تراه المواقع** |
| `allowlist.loaded` | `version`, `entries`, `skipped` | قائمة المواقع من `GET /domains`؛ `skipped` مدخلات رفضها المحلل |
| `frame.in` | `type` + ملخص | كل إطار وارد من الخادم (يشمل `ping`/`pong`) |
| `frame.out` | `type` | كل إطار صادر من دورة الجلسة |

**دورة الطلب**

| الحدث | الحقول | المعنى |
|---|---|---|
| `host.available` | `available`, `listen_port` | المضيف أعلن نفسه (أو لم يفعل مع `--no-available`) |
| `hosts` | `count`, `hosts[]` | لقطة المضيفين (`device_id`, `user_display_name`, `device_name`, `reachable`) |
| `host.selected` | `device_id`, `device_name`, `reachable` | المضيف الذي طابق `--host-device` |
| `host.not_found` | `wanted`, `available` | لا مضيف يطابق، أو أكثر من واحد يطابق (الغموض = لا اختيار) |
| `request.created` | `request_id`, `expires_at` | الخادم قَبِل الطلب وهو معلّق |
| `request.incoming` | `request_id`, `guest_name`, `guest_device`, `duration_min`, `expires_at` | وصل الطلب إلى المضيف |
| `request.accepted` / `request.rejected` | `request_id` | قرار المضيف التلقائي |
| `request.result` | `request_id`, `accepted`, `reason` | نتيجة الطلب عند المستخدم (`rejected`/`expired`/`cancelled`/`host_unavailable`) |
| `request.rejected_by_server` | `code`, `message` | `request.create` رُفض (`host_unavailable`, `session_exists`, `request_pending`, `bad_request`…) |
| `session.created` | `session_id`, `peer_display_name`, `peer_device_name`, `peer_public_ip`, `same_public_ip`, `expires_at` | بدأت الجلسة؛ من هنا يتولى `SessionCoordinator` (نفس منسّق الجلسة الذي يقوده تطبيق WPF) |

**النفق**

| الحدث | الحقول | المعنى |
|---|---|---|
| `tunnel.state` | `state` | `Listening` → `Connecting` → `Authenticating` → `Connected` → `Ended` |
| `tunnel.prepared` | `listen_port`, `cert_fp_sha256`, `candidates[]` | ما أُرسل في `session.endpoint`: بصمة الشهادة والمرشحون (`lan`/`v6`/`upnp`/`public`) |
| `tunnel.peer_endpoint` | `cert_fp_sha256`, `candidates[]` | مرشحو الطرف الآخر؛ بدأ الاتصال المتوازي |
| `tunnel.connected` | `winner_type`, `connect_ms`, `tls_version`, `proxy_port`, `probe_url` | **نجح النفق.** `winner_type` عند المضيف هو المعتمد، و`tls_version` يجب أن يكون `1.3` على Win11 و`1.2` على Win10 |
| `tunnel.connect_failed` | `reason`, `role`, `candidates[]` بزمن وخطأ كل مرشح | فشل الاتصال؛ هذا هو نفس التشخيص المرفوع في `session.connect_failed` وهو ما تغذّى به بوابة Relay |
| `tunnel.died` | `suggested_reason` | النفق مات بعد أن عمل (اختفى الطرف الآخر أو انتهت مهلة PONG) |
| `proxy.ready` | `port`, `probe_url` | منفذ الـ Proxy المحلي على 127.0.0.1 لدى المستخدم |
| `curl.result` | `url`, `status`, `elapsed_ms`, `bytes`, `body_prefix` | **الدليل:** طلب HTTP GET عبر الـ Proxy نجح، و`body_prefix` أول 200 حرف من الرد |
| `curl.failed` | `url`, `error` | الطلب عبر الـ Proxy فشل |
| `browser.launch` | `browser`, `success`, `failure`, `detail`, `handoff`, `pid` | نتيجة تشغيل متصفح العمل مع `--browser` |
| `session.active` | `expires_at` | الخادم حوّل الجلسة إلى نشطة |
| `session.stats` | `bytes_up`, `bytes_down`, `open_streams` | من المضيف كل 30 ثانية (نفس ما يُرسل في `session.stats`) |
| `session.terminate` | `reason` | الخادم أنهى الجلسة |
| `session.ending` / `session.ended` | `kind`, `reason`, `bytes_up`, `bytes_down`, `domains`, `server_already_ended` | ترتيب التنظيف بدأ / اكتمل |
| `tool.exit` | `code`, `kind`, `end_reason`, `bytes_up`, `bytes_down`, `domains` | آخر سطر دائمًا؛ `code` هو رمز خروج العملية |

قيم `kind` في `session.ended` و`tool.exit`: `Terminated` (الخادم أنهى)، `Expired` (انتهت المدة)، `LocalEnd` (Ctrl+C أو إنهاء مقصود)، `ConnectFailed`، `TunnelDied`، `ChannelLost` (سقطت قناة التحكم أثناء الجلسة)، `ProtocolError` (خرق للعقد، أو `browser_not_proxied` مع `--browser`).

بلا `--browser` لا متصفح عمل أصلًا، فلا انتظار لصفحة الفحص ولا `browser_not_proxied`: الـ Proxy يقوم ويبقى، ويثبته `--curl-test` بنفسه. مع `--browser` يبقى السلوك كاملًا — يُشغَّل المتصفح عند `session.active`، وغياب صفحة الفحص خلال 10 ثوانٍ ينهي الجلسة بـ `browser_not_proxied`.

## 3. أكواد الخروج

| الكود | المعنى | ماذا يعني لـ QA |
|---|---|---|
| `0` | الجلسة عملت وانتهت نهاية متوقَّعة، أو `--list-hosts` نجح، أو `--auto-reject` رفض | نجاح |
| `1` | سوء استعمال المعاملات، أو لا مضيف يطابق `--host-device` | خطأ في الأمر لا في المنتج |
| `2` | لم يقم نفق: فشل الاتصال المتماثل، أو رُفض الطلب أو انتهت مهلته، أو المضيف غير متاح | يُسجَّل في جدول بوابة Relay |
| `3` | النفق قام ثم مات، أو سقطت قناة التحكم أثناء الجلسة | عطل شبكة أو استقرار |
| `4` | فشل مصادقة أو خرق للعقد من الخادم | يُرفع للمسار A أو C |
| `130` | Ctrl+C قبل قيام الجلسة | ليس فشلًا |

## 4. ماذا يسجّل QA لكل تشغيل

احفظ `host.jsonl` و`guest.jsonl` كاملين، واملأ من الحدثين `tunnel.connected` و`tool.exit`:

| الحقل | من أين |
|---|---|
| نوع شبكة كل طرف (منزل / مكتب / هاتف / CGNAT) | يدوي |
| `winner_type`, `connect_ms`, `tls_version` | `tunnel.connected` على جهاز المضيف (تسميته هي المعتمدة) |
| المحاولات الفاشلة `{direction,type,ip,port,ms,stage,error}` | `tunnel.connect_failed.candidates` |
| `public_ip` لكل طرف | `hello.ack` على كل جهاز |
| ‏IP الذي رآه الموقع | `curl.result.body_prefix` على جهاز المستخدم |
| `bytes_up` / `bytes_down` / `domains` | `tool.exit` على جهاز المضيف |
| رمز الخروج لكل طرف | `tool.exit.code` |

**معيار القبول لتشغيل ناجح:** رمز خروج `0` على الطرفين، و`tunnel.connected` خلال 10 ثوانٍ من `session.created`، و`curl.result.status` يساوي 200، و`body_prefix` يطابق `public_ip` الخاص بالمضيف، و`session.ended.kind` ليس `TunnelDied`.

## 5. حالات فشل شائعة وتفسيرها

| ما يظهر | التفسير |
|---|---|
| `request.rejected_by_server` بـ `host_unavailable` | المضيف غير متصل، أو لم يُعلن `--available`، أو عنده جلسة غير منتهية، أو يردّ على طلب آخر |
| `request.rejected_by_server` بـ `bad_request` | الجهازان لنفس المستخدم — استخدم حسابين مختلفين |
| `request.rejected_by_server` بـ `session_exists` | جلسة سابقة لم تُنهَ؛ أنهها من `POST /admin/sessions/{id}/terminate` |
| `host.not_found` مع `available > 0` | الاسم يطابق أكثر من مضيف؛ استخدم `device_id` من `--list-hosts` |
| `tunnel.connect_failed` بـ `peer_endpoint_timeout` | الطرف الآخر لم يرسل `session.endpoint` خلال المهلة (أو الخادم لم يمرره) |
| `tunnel.connect_failed` وكل المرشحين `stage=tcp` | الوارد محجوب على الطرفين — هذه هي الحالة التي تفتح بوابة Relay |
| `curl.failed` بـ 403 والنطاق في القائمة | المضيف رفض العنوان بعد الحل (`private_ip`): الاسم يحل إلى عنوان داخلي على شبكة المضيف |
| `session.ended.kind = ChannelLost` | انقطعت قناة التحكم فأنهى الخادم الجلسة بـ `*_disconnected` |
| `session.ended.kind = TunnelDied` و`session.end` بـ `guest_disconnected`/`host_disconnected` | مات النفق والقناتان حيتان: **الاسم اسم النظير** لا اسم المُرسِل (ws-protocol القسم 9) |
| `session.ended.kind = ProtocolError` بـ `browser_not_proxied` (مع `--browser` فقط) | متصفح العمل قام لكنه لم يمر بالـ Proxy (سياسة مؤسسية أو تسليم لنسخة قائمة) |
| `channel.closed` بالكود `4409` | نسخة أخرى من الأداة تعمل بنفس `--state-dir` على جهاز آخر |

## 6. ما لم يُتحقق منه بعد على Windows (من تقرير المسار B للأسبوع 4)

يضاف إلى قائمة القسم 5 أعلاه:

10. الجلسة كاملة بين جهازي Windows حقيقيين: `session --role host` و`session --role guest` حتى `tool.exit` بالكود 0 على الطرفين.
11. `curl.result.body_prefix` يساوي `public_ip` الخاص بالمضيف (إثبات الخروج من عنوانه).
12. `--browser` على Win10 وWin11: تشغيل متصفح العمل من داخل الجلسة، وصول صفحة الفحص، ثم إغلاقه في الخطوة 2 من ترتيب التنظيف عند `Ctrl+C`.
13. `Ctrl+C` على الطرفين: `session.end` يصل بالسبب الصحيح، والمستمع يُغلق وتُزال تعيينات UPnP (`netsh` و`certutil -user -key` بعدها).
14. `--listen-port N` على المضيف: يظهر `reachable=true` في `hosts` لدى المستخدم.
15. سر الجهاز بـ DPAPI في `<state-dir>/secrets/` على Windows، و`--reset-device` يعيد التسجيل بعد إلغاء الجهاز.
