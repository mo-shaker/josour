# دليل التجربة: نشر الخادم وتثبيت التطبيق على Windows 11

دليل تنفيذي من الصفر لتشغيل تجربة حقيقية لـ RouteBridge. يفترض أنك لم تجهّز شيئًا بعد.

**ما ستحصل عليه في النهاية:** خادم يعمل على الإنترنت، وجهازا Windows 11 عليهما التطبيق، وجلسة تثبت أن المواقع ترى عنوان المضيف لا عنوانك.

> **قبل أن تبدأ، اعرف حدود التجربة اليوم:**
> - **لا شهادة توقيع كود بعد**، فسيحذّر SmartScreen عند التثبيت. الدليل يشرح كيف تتجاوز التحذير بوعي في جهاز اختبار.
> - **التطبيق لم يُشغَّل على Windows قط** — بُني واختُبر على macOS. أول تشغيل فعلي هو تجربتك، ومن المتوقع أن تجد مشكلات في الواجهة.
> - **أداة `session` بلا واجهة هي الطريق الأضمن** لإثبات عمل النفق، وهي مُجرَّبة فعليًا. ابدأ بها قبل الواجهة الرسومية.

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

المتوقع: `{"status":"ok","product":"routebridge","version":"0.1.0"}`

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

### 1.8 قائمة المواقع المسموح بها

هذه هي التي ستمر عبر المضيف. للتجربة أضف مواقع تكشف عنوان IP:

```bash
docker compose exec api python manage.py add-domain api.ipify.org
docker compose exec api python manage.py add-domain ifconfig.me
docker compose exec api python manage.py list-domains
```

> كل إضافة تنشئ **إصدارًا جديدًا** من القائمة. الإصدارات لا تُحذف أبدًا لأن شاشة إفصاح المضيف تعرض قائمة الإصدار الذي طُلب به تحديدًا.

### 1.9 نسخة احتياطية أولى

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
2. نزّل الملف المرفق `routebridge-app-unsigned`.
3. فك الضغط في مجلد دائم، مثل `C:\RouteBridge`.

> إن لم يظهر تشغيل، ادفع أي تغيير إلى `main` أو شغّل الـ workflow يدويًا من نفس التبويب.

**البديل: البناء محليًا** (يحتاج [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
git clone https://github.com/<حسابك>/routebridge.git
cd routebridge\client
dotnet publish src\RouteBridge.App -c Release -r win-x64 --self-contained false -o publish\app
```

### 2.2 تجاوز SmartScreen بوعي

الملف **غير موقّع** لأن شهادة توقيع الكود لم تُطلب بعد. عند التشغيل سيظهر «Windows protected your PC».

على **جهاز اختبار فقط**، وبعد أن تكون واثقًا أن الملف من بنائك أنت أو من CI مستودعك: اضغط **More info** ثم **Run anyway**.

> لا توزّع هذا الملف على مستخدمين حقيقيين. الحل الدائم شهادة توقيع كود (OV تكفي؛ EV لم تعد تمنح سمعة فورية منذ أغسطس 2024)، وبها يُبنى مثبّت Inno Setup الجاهز في `client/installer/`.

### 2.3 قاعدة جدار الحماية (على جهاز المضيف)

المثبّت الموقّع يضيفها تلقائيًا، لكن مع النسخة المنشورة يدويًا **أضفها بنفسك**. افتح PowerShell **كمسؤول**:

```powershell
netsh advfirewall firewall add rule name="RouteBridge Tunnel" dir=in action=allow program="C:\RouteBridge\RouteBridge.exe" enable=yes profile=domain,private,public protocol=TCP
```

> بدونها يحجب Windows اتصال المستخدم الوارد **بصمت**، ويبدو الأمر وكأنه فشل شبكة. الاسم `RouteBridge Tunnel` بالضبط: التطبيق يفحص وجود القاعدة بهذا الاسم ويحذّرك إن غابت.

للحذف بعد التجربة:

```powershell
netsh advfirewall firewall delete rule name="RouteBridge Tunnel"
```

### 2.4 التشغيل الأول

شغّل `RouteBridge.exe`. الواجهة **بالعربية** مع اتجاه من اليمين إلى اليسار (`--lang en` للإنجليزية).

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
dotnet run --project tools\RouteBridge.Spike -c Release -- session `
  --api https://rb.example.com --email host@example.com --password "Host-pass-1234" `
  --role host --available --state-dir C:\rb-host
```

انتظر حتى يطبع أنه ينتظر طلبًا، وسجّل `device_id` الذي يعرضه.

**على جهاز المستخدم:**

```powershell
cd routebridge\client
dotnet run --project tools\RouteBridge.Spike -c Release -- session `
  --api https://rb.example.com --email guest@example.com --password "Guest-pass-1234" `
  --role guest --list-hosts --state-dir C:\rb-guest
```

انسخ `device_id` المضيف من القائمة، ثم:

```powershell
dotnet run --project tools\RouteBridge.Spike -c Release -- session `
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

النفق يحاول اتصالًا مباشرًا بين الجهازين. هذا يفشل على بعض الشبكات، وهو **متوقع ومقيس**: التقدير 45% إلى 65% نجاحًا للشبكات المؤسسية.

عند فشل الاتصال (كود خروج `2`)، احفظ مخرجات JSON كاملة — فيها المرشحون المجرَّبون وسبب فشل كل واحد. وشغّل على كل جهاز:

```powershell
dotnet run --project tools\RouteBridge.Spike -c Release -- gather --port 40000 --public-ip <عنوانك العام>
```

**هذه البيانات تحكم قرارًا معلّقًا منذ الأسبوع الأول:** إن كانت نسبة النجاح على عشرة أزواج حقيقية أقل من 85%، تُبنى خدمة Relay (الجانب العميل جاهز، والخادم يُبنى في أسبوع). التفصيل في `docs/spike-runbook.md` و`docs/test-matrix.md`.

---

## ملخص التحقق

| # | الفحص | المتوقع |
|---|---|---|
| 1 | `curl https://rb.example.com/healthz` | `{"status":"ok","product":"routebridge",...}` |
| 2 | تسجيل الدخول من الجهازين | ينجح |
| 3 | ظهور المضيف في قائمة المستخدم | يظهر خلال ثوانٍ |
| 4 | نافذة الطلب على المضيف | تعرض الاسم والجهاز والمدة وقائمة المواقع والتنبيه |
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
