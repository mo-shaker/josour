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
