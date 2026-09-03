# عقد القناة بين الجهازين (Tunnel Protocol)

الإصدار: 1 (مجمّد نهاية الأسبوع 1). المالك: المسار B. المستهلك: المسار C عبر واجهات `RouteBridge.Core`.

## 1. الأدوار

- **Guest (المستخدم):** يشغّل `ConnectProxyServer` محليًا ويفتح streams عبر النفق. يتكلم أولًا في المصادقة دائمًا.
- **Host (المضيف):** يشغّل `OpenHandler` وينفّذ `EgressPolicy`. هو المرجع الوحيد في قبول الاتصال (`session.connected`).
- أي طرف قد يكون **المتصل** أو **المستمع** على مستوى TCP (اتصال متماثل). الدور المنطقي لا يتغير.

## 2. إنشاء الاتصال

1. عند `session.created` كل طرف:
   - يولّد شهادة ذاتية: ECDSA P-256، الموضوع `CN=routebridge`, EKU `serverAuth` + `clientAuth`, الصلاحية من الآن − 5 دقائق إلى `expires_at` + ساعة. تُصدَّر PFX وتُعاد استيرادها بـ `X509KeyStorageFlags.UserKeySet` (بلا `PersistKeySet`) لتعمل مع Schannel وتُحذف حاوية المفتاح عند `Dispose`.
   - يفتح مستمع TCP على `[::]:0` بـ `DualMode=true` ويقرأ المنفذ.
   - يطلب تعيين UPnP/NAT-PMP بـ Mono.Nat لهذا المنفذ (عمر التعيين = المدة المتبقية + 5 دقائق).
   - يجمع المرشحين ويرسل `session.endpoint` مع بصمة الشهادة (SHA-256 على `RawData`, hex صغير).
2. أنواع المرشحين وترتيب الأولوية: `lan` (فقط إذا `same_public_ip=true`), `v6` (عناوين IPv6 عامة غير مؤقتة), `upnp` (IP:Port الخارجي من الراوتر), `public` (IP الذي يراه الخادم + المنفذ المحلي؛ يعمل إن كان المنفذ مفتوحًا أو الجهاز بعنوان عام).
3. عند `session.peer_endpoint` يتصل كل طرف بكل مرشحي الآخر بالتوازي بمهلة 5 ثوانٍ لكل مرشح، ويستمر في قبول الاتصالات الواردة.
4. لكل اتصال TCP: مصافحة TLS بمهلة 10 ثوانٍ ثم مصادقة بمهلة 5 ثوانٍ.
5. المضيف يبقي **أول** اتصال يجتاز `AUTH1`، ويرسل `AUTH2` عليه **فقط**، ويغلق أي اتصال آخر بلا رد. بذلك لا يرى المستخدم إلا `AUTH2` واحدًا، فاختياره لـ «أول اتصال مصادَق» هو بالضرورة اختيار المضيف. ثم يرسل المضيف `session.connected`. الطرفان يغلقان المستمع ويزيلان تعيين UPnP ويلغيان المحاولات الأخرى. في التشخيص قد تختلف تسمية `winner_type` بين الطرفين للاتصال نفسه؛ تسمية المضيف هي المعتمدة.
6. إن لم يُصادَق شيء خلال 30 ثانية من `session.created`: `session.connect_failed` مع التشخيص.

قيود المستمع: 4 اتصالات غير مصادقة معلّقة كحد أقصى؛ أي اتصال زائد يُغلق فورًا. المستمع لا يُفتح إلا بين `session.created` و`session.connected`.

## 3. TLS

- `SslStream` مع `SslProtocols.None` (افتراضي النظام). بعد المصافحة: إن كان `SslProtocol < Tls12` يُغلق الاتصال ويُسجَّل `tls_too_old`. الإصدار المتفاوَض عليه يُرسل في `session.connected`.
- الطرف الذي يتصل (Client في TLS) يقدّم `TargetHost="routebridge"` ويقبل الشهادة فقط إذا طابقت `SHA256(RawData)` بصمة الطرف الآخر من `session.peer_endpoint`؛ يتجاهل أخطاء السلسلة والاسم؛ `CertificateRevocationCheckMode=NoCheck`.
- الطرف الذي يستمع (Server في TLS) يقدّم شهادته الذاتية ولا يطلب شهادة عميل.
- لا يُكتب أي بايت خارج TLS.

## 4. المصادقة داخل TLS

بعد المصافحة، Guest يرسل `AUTH1` ثم ينتظر `AUTH2`. Host صامت حتى يتحقق من `AUTH1`.

```
AUTH1 (Guest → Host), 81 بايت:
  u8   version = 1
  16B  session_id (UUID bytes, big-endian كما في RFC 4122)
  32B  client_random
  32B  mac1 = HMAC-SHA256(secret, "rb-auth1" || session_id || client_random || listener_cert_fp)

AUTH2 (Host → Guest), 32 بايت:
  32B  mac2 = HMAC-SHA256(secret, "rb-auth2" || session_id || client_random || listener_cert_fp)
```

- `secret` = 32 بايت من `secret_b64` في `session.created`.
- `listener_cert_fp` = 32 بايت SHA-256 لشهادة **الطرف الذي يعمل كـ TLS Server** في هذا الاتصال (أيًا كان دوره المنطقي). كلا الطرفين يعرفانها: المستمع من شهادته، والمتصل من `session.peer_endpoint`.
- النصوص `"rb-auth1"` و`"rb-auth2"` ASCII بلا فاصل.
- المقارنة بـ `CryptographicOperations.FixedTimeEquals`.
- الفشل: إغلاق الاتصال بلا رد. عند النجاح يبدأ الـ Mux فورًا.
- عند انتهاء الجلسة: مسح `secret` من الذاكرة (`CryptographicOperations.ZeroMemory`) و`Dispose` للشهادات.

## 5. الإطارات (يُستخدم إن لم يُعتمد Nerdbank في ADR-0006)

رأس ثابت 8 بايت، Big-endian:

```
u8  type | u8 flags | u16 length | u32 stream_id
```

| type | الاسم | الحمولة | ملاحظات |
|---|---|---|---|
| 0x01 | `OPEN` | `u16 port` + `u8 hostlen` + `host` (UTF-8, ASCII/Punycode) | من Guest فقط. `stream_id` فردي متزايد |
| 0x02 | `OPEN_OK` | لا شيء | |
| 0x03 | `OPEN_FAIL` | `u8 reason` | 1 `not_allowed`, 2 `private_ip`, 3 `port_not_allowed`, 4 `dns_failed`, 5 `connect_failed`, 6 `limit`, 7 `ip_literal` |
| 0x04 | `DATA` | بايتات ≤ 16384 | |
| 0x05 | `WINDOW_UPDATE` | `u32 increment` | |
| 0x06 | `CLOSE` | لا شيء | إغلاق نصفي (لن يرسل هذا الطرف DATA بعده) |
| 0x07 | `RST` | لا شيء | إنهاء فوري للـ stream في الاتجاهين |
| 0x08 | `PING` | 8 بايت معتمة | `stream_id = 0` |
| 0x09 | `PONG` | نفس الـ 8 بايت | `stream_id = 0` |
| 0x0A | `GOAWAY` | `u8 reason` | `stream_id = 0`؛ 1 `session_end`, 2 `protocol_error`, 3 `expired` |

قواعد:
- `length` ≤ 16384 لكل الأنواع؛ تجاوزه = `GOAWAY(protocol_error)` وإغلاق الاتصال.
- `stream_id` غير معروف في `DATA`/`CLOSE`/`WINDOW_UPDATE` → `RST` لذلك المعرّف.
- نافذة استقبال لكل stream = 1 MiB (1048576) عند الفتح. المرسل لا يتجاوز رصيده. المستقبل يعيد الرصيد **بعد** كتابة البايتات إلى المقبس الوجهة، ويرسل `WINDOW_UPDATE` عندما يبلغ المستهلَك 25% من النافذة.
- كاتب واحد لكل اتصال يفرّغ قناة محدودة (32 إطارًا)؛ كل stream يملك إطار DATA واحدًا معلّقًا في القناة كحد أقصى.
- حدود Host: 256 stream متزامن، 50 `OPEN` في الثانية؛ التجاوز يرد `OPEN_FAIL(limit)`.
- حيوية: `PING` كل 20 ثانية من الطرفين؛ لا `PONG` خلال 60 ثانية = النفق ميت = إنهاء الجلسة.

## 6. سياسة الخروج على المضيف (`EgressPolicy`)

عند `OPEN(host, port)` بالترتيب:

1. إن كان `host` عنوان IP (v4 أو v6، ولو بين أقواس) → `OPEN_FAIL(ip_literal)`.
2. تطبيع: أحرف صغيرة، حذف النقطة الأخيرة، تحويل IDN إلى Punycode.
3. مطابقة القائمة (`AllowlistMatcher`):
   - `example.com` يطابق `example.com` وكل نطاق فرعي.
   - `=exact.com` يطابق `exact.com` فقط.
   - لاحقة `:port` اختيارية تقيّد المنفذ؛ بدونها يُسمح بمنافذ `allowed_ports`.
   - مدخلات مرفوضة عند التحميل: فارغة، `*`، تحتوي `/` أو مسافات.
   - لا تطابق → `OPEN_FAIL(not_allowed)`.
4. المنفذ ليس ضمن `allowed_ports` (افتراضيًا 80 و443) ولا يطابق قيد المدخل → `OPEN_FAIL(port_not_allowed)`.
5. حل DNS مرة واحدة (`Dns.GetHostAddressesAsync`) بمهلة 5 ثوانٍ؛ فشل → `OPEN_FAIL(dns_failed)`.
6. إن كان **أي** عنوان ناتج محظورًا (`IpRangePolicy`) → `OPEN_FAIL(private_ip)`. العناوين المحظورة:
   - IPv4: `0.0.0.0/8`, `10.0.0.0/8`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16`, `172.16.0.0/12`, `192.0.0.0/24`, `192.0.2.0/24`, `192.168.0.0/16`, `198.18.0.0/15`, `198.51.100.0/24`, `203.0.113.0/24`, `224.0.0.0/4`, `240.0.0.0/4`, `255.255.255.255/32`.
   - IPv6: `::/128`, `::1/128`, `::ffff:0:0/96` و`64:ff9b::/96` و`2002::/16` و`2001::/32` (تُفك ويُفحص v4 المضمَّن), `fc00::/7`, `fe80::/10`, `ff00::/8`.
   - كل عناوين واجهات المضيف نفسه، بواباته الافتراضية، وعنوانه العام كما يراه الخادم.
7. الاتصال بـ `Socket.ConnectAsync(IPAddress[], port)` بالقائمة المفحوصة فقط، بمهلة 10 ثوانٍ؛ فشل → `OPEN_FAIL(connect_failed)`.
8. `OPEN_OK` ثم ضخ ثنائي الاتجاه؛ عدّ البايتات في الاتجاهين؛ إضافة `host` لمجموعة النطاقات المميزة.

## 7. ترتيب التنظيف عند انتهاء الجلسة

1. الـ Proxy يتوقف عن قبول اتصالات جديدة.
2. إغلاق المتصفح المهذب (WM_CLOSE) ثم إغلاق الـ Job Object بعد 3 ثوانٍ.
3. `GOAWAY(session_end)` وإغلاق النفق.
4. إغلاق المستمع وإزالة تعيين UPnP.
5. `Dispose` للشهادات ومسح السر.
6. `session.end` بالإحصاءات والنطاقات.

## 8. واجهات `RouteBridge.Core` (ما يستهلكه المسار C)

```csharp
public interface ITunnelSession : IAsyncDisposable
{
    string SessionId { get; }
    TunnelRole Role { get; }
    TunnelState State { get; }
    event Action<TunnelState> StateChanged;
    Task<TunnelConnectResult> ConnectAsync(PeerEndpointInfo peer, CancellationToken ct);
    TunnelStats Stats { get; }
    IReadOnlyCollection<string> DomainsSeen { get; }   // Host only
    Task EndAsync(TunnelEndReason reason);
}

public interface ITunnelTransport { Task<Stream> ConnectAsync(...); }   // Direct today, Relay later
```

التعريف الملزم في `client/src/RouteBridge.Core/Tunnel/*.cs`.
