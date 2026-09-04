# ADR-0006: مكتبة Multiplexing للنفق

**الحالة:** معتمد بتاريخ 2026-09-04 بعد نموذج الأسبوع 2 (يومان).

## السياق
النفق بين الجهازين stream واحد مصادَق (`SslStream` من `SymmetricConnector`) يجب أن يحمل مئات الـ streams المتزامنة (اتصال TCP لكل `CONNECT` من المتصفح) مع Backpressure لكل stream على حدة، ورفض فتح الـ stream بسبب مرمّز (`OPEN_FAIL`)، وإغلاق نصفي نظيف. الخياران: `Nerdbank.Streams.MultiplexingStream` أو Framing يدوي وفق `docs/protocol.md` القسم 5.

## القرار
**`Nerdbank.Streams.MultiplexingStream` (الإصدار 2.13.31، بروتوكول 3)** خلف واجهتي `IMuxConnection` (جانب Guest) و`IMuxAcceptor` (جانب Host) في `RouteBridge.Tunnel.Mux`. الفئة `NerdbankMux` تنفذ الواجهتين معًا وتُنشأ من الـ stream المصادَق بـ `NerdbankMux.Create(stream, role)`.

كيفية تحقيق عقد القسم 5 فوق Nerdbank:
- **OPEN:** Guest يعرض قناة باسم `host:port`. المضيف يقبل القناة دائمًا ثم يكتب **بايت حالة واحدًا** كأول بايت فيها: `0` = `OPEN_OK` ويبدأ الضخ، وإلا رمز `OPEN_FAIL` نفسه (1 `not_allowed` … 7 `ip_literal`) ثم يكمل الكتابة. الرفض داخل القناة نفسها يضمن الترتيب ولا يحتاج قناة تحكم لكل فتح ولا الاعتماد على دلالات رفض Nerdbank (التي لا تحمل سببًا).
- **PING/PONG/GOAWAY:** قناة مزروعة (seeded, id 0) لا تحتاج مصافحة، بإطارات ثابتة 9 بايت: `u8 type | 8 بايت حمولة`. `PING` كل 20 ثانية من الطرفين، ولا `PONG` خلال 60 ثانية = نفق ميت (`Completion` يفشل بـ `MuxClosedException`). `GOAWAY(reason)` يُرسل قبل الإغلاق ويظهر عند الطرف الآخر في `RemoteGoAway`.
- **النافذة:** `DefaultChannelReceivingWindowSize = 1 MiB` لكل قناة (نفس القسم 5)؛ Backpressure من `System.IO.Pipelines`: الكاتب يتوقف عند امتلاء نافذة الطرف الآخر.
- **الإغلاق النصفي:** `Output.Complete()` على القناة يظهر عند الطرف الآخر كـ EOF، و`StreamPump` يحوّله إلى `Shutdown(Send)` على المقبس الوجهة (`SocketStream`)، والعكس. القناة تُغلق ذاتيًا عندما يكمل الطرفان الكتابة.
- **الحدود (256 stream، 50 OPEN/ث)** تُنفَّذ في `RouteBridge.Egress.StreamLimiter` وترد `OPEN_FAIL(limit)`.
- **التتبع:** `MuxOptions.Trace` يمرر `TraceSource` إلى Nerdbank لتسجيل الإطارات عند الأعطال؛ `MuxStats` تعطي بايتات النقل في الاتجاهين وعدد الـ streams المفتوحة.

## أرقام النموذج (macOS، TLS 1.2 على loopback، `tests/RouteBridge.Tunnel.Tests/Mux/MuxBenchmarks.cs` بوسم `Category=Benchmark`)

| المعيار | النتيجة |
|---|---|
| (a) مستهلك بطيء على القناة A متوقف 3 ثوانٍ | القناة B نقلت **3005 MB في 3000 ms = 1002 MB/s** أثناء التوقف؛ A قبلت **1.0 MiB** فقط قبل أن تتوقف (النافذة) والمستهلك لم يستلم شيئًا حتى فُتحت البوابة |
| (b) رفض الفتح بسبب مرمّز | الأسباب السبعة كلها تصل إلى Guest كما أُرسلت (`Open_Rejected_CarriesEncodedReason`) |
| (c) 100 MB على قناة واحدة | Guest→Host **120 ms = 834 MB/s**؛ Host→Guest **116 ms = 861 MB/s**؛ حمل النقل 100,085,685 بايت لـ 100,000,000 بايت حمولة (زيادة 0.09%) |
| (d) 256 قناة متزامنة × 1 MB في كل اتجاه | فتح 256 قناة في **26 ms**؛ النقل كاملًا في **688 ms = 744 MB/s** إجمالًا؛ عدّاد الـ streams يعود إلى 0 |
| (e) الإغلاق النصفي | `CompleteWriting` على Guest → الطرف البعيد للمقبس يقرأ 0 (FIN) ويستطيع الرد بعدها؛ إغلاق الطرف البعيد → EOF عند Guest؛ التخلص عند Guest يغلق المقبس البعيد |
| إضافي: 1000 فتح/إغلاق متتالٍ | **142 ms = 0.14 ms** لكل واحد، بلا تسريب |
| إضافي: كشف النفق الميت | ابتلاع حركة المرور بصمت → `MuxClosedException` خلال `DeadAfter` |

الحد الأدنى المقبول كان 30 MB في 3 ثوانٍ للمعيار (a) و60 ثانية للمعيارين (c) و(d)؛ النتائج أعلى بمرتبتين، فالمكتبة ليست عنق الزجاجة أمام أي وصلة إنترنت واقعية.

## الأسباب
- تحقق معايير القرار الأربعة كلها بلا Framing يدوي (نحو 600 سطر + اختبارات Fuzz) ولا إدارة نوافذ يدوية.
- الاعتماد صغير ومعروف: `Nerdbank.Streams` 2.13.31 (MIT) + `Microsoft.VisualStudio.Threading.Only` 17.13.61 + `Microsoft.VisualStudio.Validation` 17.8.8 + `System.IO.Pipelines` 8.0.0.
- ما يخص الأمن (السياسة، الحدود، DNS، الحظر) يبقى في كودنا (`Egress`) لا في المكتبة.

## النتائج
- `docs/protocol.md` القسم 5 يبقى مرجعًا للدلالات (النافذة 1 MiB، الحدود، PING/PONG، أسباب OPEN_FAIL/GOAWAY) وللبديل اليدوي إن احتجناه؛ صيغة الإطارات على السلك هي صيغة Nerdbank v3 لا الرأس اليدوي ذا 8 بايت.
- Guest و Host يستخدمان `NerdbankMux.Create` من الـ stream نفسه الذي يعيده `SymmetricConnector`؛ المسار C لا يرى سوى `IMuxConnection`/`IMuxAcceptor`.
- يحتاج تحققًا على Windows: القياس نفسه فوق Schannel على Win10 (TLS 1.2) وWin11 (TLS 1.3)، وسلوك الحيوية على RTT دولي حقيقي (الأسبوع 7).
