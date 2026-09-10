# دليل التجربة: نشر الخادم وتثبيت التطبيق على Windows 11

دليل تنفيذي من الصفر لتشغيل تجربة حقيقية لـ Josour. يفترض أنك لم تجهّز شيئًا بعد.

**ما ستحصل عليه في النهاية:** خادم يعمل على الإنترنت، وجهازا Windows 11 عليهما التطبيق، وجلسة تثبت أن المواقع ترى عنوان المضيف لا عنوانك.

> **قبل أن تبدأ، اعرف حدود التجربة اليوم:**
> - **لا شهادة توقيع كود بعد**، فسيحذّر SmartScreen عند التثبيت. الدليل يشرح كيف تتجاوز التحذير بوعي في جهاز اختبار.
> - **جلسة كاملة بين جهازين على شبكتين مختلفتين جرت فعلًا بتاريخ 2026-09-09**: النفق قام عبر الـ Relay بـ TLS 1.3، والمواقع رأت عنوان المضيف. ما تقرؤه هنا مسار مُجرَّب لا مُقترَح.
> - **أداة `session` بلا واجهة** تبقى أسرع طريق لعزل عطل في النفق عن عطل في الواجهة، لكنها لم تعد شرطًا للبدء.

---

## الجزء الأول: الخادم

### 1.1 ما تحتاجه

| العنصر | المواصفة | التكلفة التقريبية |
|---|---|---|
| VPS | نواة واحدة، ذاكرة 2 غيغابايت، Ubuntu 24.04 LTS | 4 إلى 6 دولارات شهريًا |
| اسم نطاق | نطاق فرعي مثل `rb.example.com` | من نطاق تملكه |

المزوّدون المذكورون في وثيقة المنتج: Hetzner أو DigitalOcean أو Contabo أو Hostinger (**خطة VPS لا الاستضافة المشتركة**؛ التطبيق يحتاج عملية Python مستمرة واتصالات WebSocket طويلة).

سعة هذه المواصفة مقيسة لا مقدّرة: **500 قناة تحكم متزامنة** (`docs/load-test-week5.md`).

### 1.2 سجل DNS (قبل كل شيء)

أنشئ سجل `A` يشير من `rb.example.com` إلى عنوان الخادم، وانتظر انتشاره:

```bash
dig +short rb.example.com
```

يجب أن يظهر عنوان الخادم. **لا تكمل قبل ذلك**: Caddy يطلب شهادة TLS من Let's Encrypt عند أول تشغيل، وسيفشل إن لم يجد السجل.

### 1.3 تجهيز الخادم (مرة واحدة)

اتصل بالخادم عبر SSH ونفّذ:

```bash
apt update && apt -y upgrade
apt -y install ufw fail2ban unattended-upgrades ca-certificates curl git
ufw default deny incoming && ufw default allow outgoing
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw --force enable
dpkg-reconfigure -plow unattended-upgrades
curl -fsSL https://get.docker.com | sh
```

`ufw` يفتح ثلاثة منافذ فقط. قاعدة البيانات لا تُعرَّض للإنترنت إطلاقًا.

### 1.4 جلب الكود

```bash
git clone https://github.com/<حسابك>/routebridge.git /opt/routebridge
cd /opt/routebridge/deploy
```

المستودع خاص، فسيطلب منك git بيانات الدخول. استخدم رمز وصول شخصي (Personal Access Token) بصلاحية `repo` من [github.com/settings/tokens](https://github.com/settings/tokens).

### 1.5 الإعدادات

```bash
cp .env.example .env
nano .env
```

عدّل هذه القيم **بالضرورة**:

| المتغير | القيمة |
|---|---|
| `DOMAIN` | `rb.example.com` |
| `ACME_EMAIL` | بريدك (لتنبيهات انتهاء الشهادة) |
| `POSTGRES_PASSWORD` | كلمة عشوائية طويلة: `openssl rand -base64 32` |
| `JWT_SECRET` | **48 بايت عشوائية**: `openssl rand -base64 48` |

باقي القيم اتركها. **لا تودع `.env` في git** (مستثنى في `.gitignore`).

> `JWT_SECRET` لا يقل عن 32 بايت وإلا رفض الخادم الإقلاع. وتغييره لاحقًا يبطل كل رموز الدخول فورًا ويجبر الجميع على تسجيل دخول جديد.

### 1.6 التشغيل

```bash
docker compose up -d --build
docker compose logs -f api
```

انتظر `Application startup complete`، ثم اضغط `Ctrl+C` للخروج من السجل (الخدمة تبقى تعمل).

تحقق:

```bash
curl -s https://rb.example.com/healthz
```

المتوقع: `{"status":"ok","product":"josour","version":"0.1.0"}`

> إن فشل: `docker compose logs caddy` غالبًا يشرح فشل الشهادة. السببان الأشيع: سجل DNS لم ينتشر، أو المنفذ 80 مغلق (Let's Encrypt يحتاجه للتحقق).

### 1.7 إنشاء الحسابات

```bash
docker compose exec api python manage.py create-admin \
  --email admin@example.com --password 'كلمة-مرور-قوية' --display-name "المسؤول"

docker compose exec api python manage.py create-user \
  --email host@example.com --password 'Host-pass-1234' --display-name "جهاز المضيف"

docker compose exec api python manage.py create-user \
  --email guest@example.com --password 'Guest-pass-1234' --display-name "جهاز المستخدم"
```

> لا يوجد تسجيل ذاتي: كل المستخدمين يُنشَئون من المسؤول، وهذا مقصود في الإصدار الأول.

### 1.8 قائمة المواقع — اختيارية الآن

**تخطَّ هذه الخطوة.** منذ [ADR-0010](decisions/0010-route-all-through-host.md) يمر **كل** ما يطلبه متصفح العمل عبر المضيف، والإعداد `enforce_allowlist` معطّل افتراضيًا. لا حاجة لإضافة نطاق واحد قبل التجربة.

القائمة تبقى ضابطًا تستطيع تفعيله لاحقًا إن أردت تقييد نشرك بوجهات محددة:

```bash
docker compose exec api python manage.py add-domain api.ipify.org
docker compose exec api python manage.py list-domains
```

ثم تفعيل التقييد عبر `PATCH /admin/settings` بـ `{"enforce_allowlist": true}`.

> ما لا يتغير بتعطيل القائمة: حظر العناوين الداخلية على المضيف (شبكته المحلية، صفحة راوتره، `localhost`، عنوانه العام) وحدّ المنافذ 80 و443. «كل المواقع» ترفع شرط الاسم وحده.

### 1.9 خدمة الـ Relay — وهي ما يجعل الجلسة تقوم بلا إعداد راوتر

[ADR-0009](decisions/0009-relay-default.md): الـ Relay هو النقل الافتراضي، والمباشر ترقية تُجرَّب بالتوازي وتفوز حين تنجح. بدونه تعمل الجلسة **فقط** حين يكون أحد الطرفين قابلًا للوصول من الإنترنت — وهو ما لا يتحقق بين شبكتَي محمول، وهي الحالة التي فشلت مرتين قبل بنائه.

**السرّ نفسه في مكانين.** ولّده مرة واحدة:

```bash
openssl rand -base64 48
```

شغّل الخدمة:

```bash
cd /opt/josour/deploy && cp .env.relay.example .env.relay && nano .env.relay
```

اضبط `RELAY_SECRET` بالسرّ، و`RELAY_PUBLIC_PORT=8443` **إن كنت تشغّله على خادم الـ API نفسه** (Caddy يحتجز 443). على خادم مستقل اتركه 443.

```bash
docker compose --env-file .env.relay -f docker-compose.relay.yml up -d --build
ufw allow 8443/tcp
```

ثم عرّف الخادم الخلفي به — أضف إلى `deploy/.env`:

```bash
RELAY_HOST=rb.example.com
RELAY_PORT=8443
RELAY_SECRET=<نفس السرّ>
```

وأعد نشره: `docker compose up -d --build`.

> **نصف إعداد يوقف الخادم عند الإقلاع عمدًا:** عنوان بلا سرّ لا يصدر توكنًا، وسرّ بلا عنوان لا يُرسل، وكلاهما يجعل كل جلسة تسقط صامتة إلى المباشر — وهو العطل الذي وُجد ADR-0009 لإزالته.

> **أين تضعه يهم أكثر من سعته:** الـ Relay يقع على المسار بين الطرفين. مصر ↔ السعودية عبر جدة نحو 40 مللي ثانية، وعبر أوروبا نحو 150. تكلفته البرمجية نفسها **دون مللي ثانية** ([قياس](performance-relay.md))، فالجغرافيا وحدها ما يشعر به المستخدم.

**تحقق من الخارج:**

```bash
curl -s https://rb.example.com/healthz          # {"status":"ok","product":"josour",...}
timeout 5 bash -c "</dev/tcp/rb.example.com/8443" && echo "المنفذ مفتوح"
```

### 1.10 نسخة احتياطية أولى

```bash
docker compose exec backup /usr/local/bin/backup.sh
ls -la backups/
```

النسخ تلقائية يوميًا بعد ذلك. **انسخ مجلد `backups/` خارج الخادم دوريًا**؛ نسخة على القرص نفسه ليست خطة كوارث. سكربت الاستعادة `./backup/restore.sh` مُجرَّب فعليًا وموثق في `docs/runbook.md`.

---

## الجزء الثاني: جهازا Windows 11

تحتاج **جهازين**، ويفضَّل أن يكونا على **شبكتين مختلفتين** (أحدهما على شبكة الجوال مثلًا). جهاز واحد يعمل لكنه لا يثبت الشيء المهم: أن الحركة تخرج من عنوان الجهاز الآخر.

### 2.1 الحصول على البرنامج

بما أن المستودع صار على GitHub، فأسهل طريق هو **بناء CI**:

1. افتح مستودعك ← تبويب **Actions** ← آخر تشغيل ناجح لـ `ci-client`.
2. نزّل الملف المرفق `josour-app-unsigned`.
3. فك الضغط في مجلد دائم، مثل `C:\Josour`.

> إن لم يظهر تشغيل، ادفع أي تغيير إلى `main` أو شغّل الـ workflow يدويًا من نفس التبويب.

**البديل: البناء محليًا** (يحتاج [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
git clone https://github.com/<حسابك>/routebridge.git
cd routebridge
powershell -ExecutionPolicy Bypass -File scripts\publish-exe.ps1
```

يخرج **ملف واحد** في `client\publish\exe\Josour.exe`، ويطبع بصمته. ولا تبنِ بأمر `dotnet publish` مباشرة: مكتبات WPF الأصلية لا تُدمج افتراضيًا، فيخرج ملف يعمل على جهاز البناء **ويموت صامتًا** على أي جهاز آخر. السكربت يفحص ذلك ويرفض الناتج الناقص.

### 2.2 التحقق من الملف، ثم تجاوز SmartScreen

الملف **غير موقّع، وهذا قرار لا نقص** ([ADR-0011](decisions/0011-no-code-signing-certificate.md)): المستخدمون من ثلاثة إلى خمسة يعرفون من أعطاهم الملف، وشهادة OV لا تلغي تحذير SmartScreen عند هذا العدد أصلًا — إنما تبني سمعة بعدد التنزيلات.

**البديل ليس أضعف من التوقيع.** التوقيع يثبت أن الملف من جهة اشترت شهادة؛ **البصمة تثبت أنه هذا الملف بعينه**. لذلك، قبل التشغيل على أي جهاز:

```powershell
Get-FileHash C:\Josour\Josour.exe -Algorithm SHA256
```

طابق الناتج مع البصمة التي طبعها `publish-exe.ps1` عند البناء — **بمكالمة أو رسالة مباشرة، لا بالقناة نفسها التي وصل بها الملف**. غير مطابقة تعني ملفًا آخر: احذفه.

بعد المطابقة سيظهر «Windows protected your PC» مرة واحدة على كل جهاز: **More info** ثم **Run anyway**.

> **متى يتغيّر هذا؟** حين يصير الجواب على «هل يعرف كل مستخدم من أعطاه الملف؟» هو «لا». عندها تعود الشهادة إلى الطاولة، ومعها مثبّت Inno Setup الجاهز في `client/installer/`.

### 2.3 قاعدة جدار الحماية (اختيارية الآن)

**لم تعد شرطًا.** مع الـ Relay لا يفتح أي طرف مستمعًا: الطرفان يتصلان **خارجًا**، والاتصال الخارج لا يمر بقاعدة واردة. الجلسة تقوم بدونها.

أضفها فقط لتسريع المسار المباشر حين يكون الجهازان على شبكة واحدة أو خلف راوتر بـ UPnP — يفوز حينها المباشر على الـ Relay ويكون أسرع. من PowerShell **كمسؤول**:

```powershell
netsh advfirewall firewall add rule name="Josour Tunnel" dir=in action=allow program="C:\Josour\Josour.exe" enable=yes profile=domain,private,public protocol=TCP
```

> بدونها يحجب Windows اتصال المستخدم الوارد **بصمت**، ويبدو الأمر وكأنه فشل شبكة. الاسم `Josour Tunnel` بالضبط: التطبيق يفحص وجود القاعدة بهذا الاسم ويحذّرك إن غابت.

للحذف بعد التجربة:

```powershell
netsh advfirewall firewall delete rule name="Josour Tunnel"
```

### 2.4 التشغيل الأول

شغّل `Josour.exe`. الواجهة **بالعربية** مع اتجاه من اليمين إلى اليسار (`--lang en` للإنجليزية).

سيطلب منك:
1. **عنوان الخادم**: `https://rb.example.com` — يفحصه فعليًا قبل السماح بالمتابعة.
2. **تسجيل الدخول**: `host@example.com` على الجهاز الأول و`guest@example.com` على الثاني.
3. **ملخص الجاهزية**: حالة قاعدة جدار الحماية، وتحذير VPN إن كان محوّل VPN يملك مسار الخروج.

> **إن كنت تستخدم VPN على جهاز المضيف، أغلقه.** المواقع سترى عنوان الـ VPN لا عنوان الجهاز، وهذا يفسد معنى التجربة. التطبيق يحذّرك لكنه لا يمنعك.

### 2.5 الجلسة

**على جهاز المضيف:** فعّل «متاح لاستقبال الطلبات».

**على جهاز المستخدم:** يظهر المضيف في القائمة. اختره، اختر مدة (15 دقيقة تكفي)، ثم اطلب الاتصال.

**على جهاز المضيف:** يظهر إشعار ونافذة تعرض اسم الطالب وجهازه والمدة **وقائمة المواقع الفعلية** والتنبيه بأن المواقع سترى عنوان IP الخاص بك. اقبل.

**على جهاز المستخدم:** يُفتح متصفح عمل مستقل على صفحة الفحص. اذهب إلى `https://api.ipify.org`.

**هذا هو الاختبار كله:** يجب أن يظهر **عنوان IP جهاز المضيف**، لا عنوانك. افتح المتصفح العادي على نفس العنوان في الوقت نفسه — يجب أن يظهر عنوانك أنت. اختلاف الرقمين هو المنتج.

---

## الجزء الثالث: الطريق الأضمن — أداة `session`

إن تعثّرت الواجهة (وهي لم تُشغَّل على Windows قط)، فهذه الأداة تثبت أن النفق نفسه يعمل، وقد **جُرِّبت فعليًا** من طرف إلى طرف.

تحتاج [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) على الجهازين، ومستودعًا مستنسخًا.

**على جهاز المضيف:**

```powershell
cd routebridge\client
dotnet run --project tools\Josour.Spike -c Release -- session `
  --api https://rb.example.com --email host@example.com --password "Host-pass-1234" `
  --role host --available --state-dir C:\rb-host
```

انتظر حتى يطبع أنه ينتظر طلبًا، وسجّل `device_id` الذي يعرضه.

**على جهاز المستخدم:**

```powershell
cd routebridge\client
dotnet run --project tools\Josour.Spike -c Release -- session `
  --api https://rb.example.com --email guest@example.com --password "Guest-pass-1234" `
  --role guest --list-hosts --state-dir C:\rb-guest
```

انسخ `device_id` المضيف من القائمة، ثم:

```powershell
dotnet run --project tools\Josour.Spike -c Release -- session `
  --api https://rb.example.com --email guest@example.com --password "Guest-pass-1234" `
  --role guest --host-device <device_id> --minutes 15 `
  --curl-test https://api.ipify.org --state-dir C:\rb-guest
```

**النتيجة المطلوبة** سطر JSON فيه:

```json
{"event":"curl.result","status":200,"body_prefix":"<عنوان IP المضيف>"}
```

إن كان `body_prefix` هو عنوان **جهاز المضيف** فالمنتج يعمل. أكواد الخروج: `0` نجاح، `2` فشل الاتصال، `3` مات النفق، `4` خطأ مصادقة.

> `--host-device` يقبل معرّف الجهاز أو اسمه، **لا البريد الإلكتروني**.

---

## الجزء الرابع: إن لم ينجح الاتصال

بوابة القرار التي كان هذا القسم يخدمها **أُغلقت** في 2026-09-07 ([ADR-0009](decisions/0009-relay-default.md)): القياس على زوج حقيقي أثبت استحالة المسار المباشر بين طرفين خلف CGNAT، فصار الـ Relay هو النقل الافتراضي. إن كان الـ Relay مضبوطًا فالجلسة يجب أن تقوم؛ وإن لم تقم فاقرأ السبب بدل التخمين.

### السجل يسمّي العطل

```powershell
Get-Content "$env:LOCALAPPDATA\Josour\logs\app-*.log" | Select-String "Tunnel connected|connect_failed|No probe page" | Select-Object -Last 5
```

| ما تقرؤه | المعنى | الخطوة |
|---|---|---|
| `winner="Relay"` | النفق قام عبر الـ Relay | سليم |
| `winner="Lan"` أو `"Upnp"` | فاز المباشر — أسرع، لكن الشبكتين غير مختلفتين بما يكفي لإثبات تبديل العنوان | ضع الجهازين على شبكتين |
| `connect_failed` مع `Relay available` في السطور السابقة | الـ Relay مُعلَن ولم ينجح | تحقق أن منفذه مفتوح من الخارج، وأن `RELAY_SECRET` **متطابق** في الملفين |
| `connect_failed` بلا `Relay available` | الخادم لا يرسل كائن `relay` أصلًا | `RELAY_HOST` أو `RELAY_SECRET` ناقص في `deploy/.env` |
| `No probe page ... (accepted=N rejected_by_owner=N)` | متصفح العمل يصل الوكيل والوكيل يرفضه | عطل في التطبيق — أرسل السطر |
| `No probe page ... (nothing ever connected)` | المتصفح لا يستخدم الوكيل | [`scripts/diagnose-work-browser.ps1`](../scripts/diagnose-work-browser.ps1) يحسمها |

### تشخيص المتصفح

```powershell
powershell -ExecutionPolicy Bypass -File scripts\diagnose-work-browser.ps1
```

يشغّل المتصفح بأوامر جسور نفسها على مستمع فارغ، ويطبع حكمًا: لم يصل أبدًا (شيء يتجاوز `--proxy-server` على ذلك الجهاز)، أو وصل متأخرًا (المهلة قصيرة على ذلك الجهاز)، أو وصل فورًا (المتصفح سليم والفرق في وكيل جسور).

### حالة المسار المباشر وحده

لجمع بيانات NAT دون Relay:

```powershell
dotnet run --project tools\Josour.Spike -c Release -- gather --port 40000 --public-ip <عنوانك العام>
```

`upnp_found: false` مع `ipv6_global: false` ومرشّح `public` وحيد يعني أن المباشر مستحيل من هذه الشبكة — وهو بالضبط ما يحمله الـ Relay.

---

## ملخص التحقق

| # | الفحص | المتوقع |
|---|---|---|
| 1 | `curl https://rb.example.com/healthz` | `{"status":"ok","product":"josour",...}` |
| 2 | تسجيل الدخول من الجهازين | ينجح |
| 3 | ظهور المضيف في قائمة المستخدم | يظهر خلال ثوانٍ |
| 4 | نافذة الطلب على المضيف | تعرض الاسم والجهاز والمدة، وتحت عنوان **«نطاق التصفح»** جملة **«سيتمكّن من تصفّح أي موقع عبر اتصالك»**، والتنبيه بأن المواقع سترى عنوانك. **ولا تظهر أي جملة عن «قائمة الشركة»** |
| 5 | `api.ipify.org` في متصفح العمل | **عنوان المضيف** |
| 6 | نفس العنوان في المتصفح العادي | **عنوانك أنت** |
| 7 | Teams وOutlook أثناء الجلسة | تعمل بعنوانك، لا تتأثر |
| 8 | القطع من أي طرف | يغلق متصفح العمل خلال ثوانٍ |
| 9 | انتهاء المدة | تنتهي الجلسة تلقائيًا |

القائمة الكاملة (18 بندًا) في `docs/acceptance-checklist.md`.

---

## مراجع

| الملف | المحتوى |
|---|---|
| `docs/runbook.md` | تشغيل الخادم: الترقية، النسخ الاحتياطي والاستعادة، المراقبة، النشر الآلي |
| `docs/spike-runbook.md` | أداة النموذج بتفصيل: كل الأوامر وكل حدث JSON |
| `docs/test-matrix.md` | مصفوفة الأجهزة والشبكات وحالات الحافة |
| `docs/acceptance-checklist.md` | معايير النجاح الـ 18 والفحوص الأمنية |
| `docs/load-test-week5.md` | سعة الخادم المقيسة |
| `client/installer/README.md` | بناء المثبّت الموقّع بعد وصول الشهادة |
