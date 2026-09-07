# سكربتات اختبارات الأمان

تُشغَّل يدويًا في الأسبوع 8 ضمن قائمة القبول، وبعضها آليًا في CI مقابل staging.

| السكربت | يتحقق من | متطلب الوثيقة |
|---|---|---|
| `check-session-keys.sh` | لا مفاتيح باقية لجلسات منتهية | 6.7، 14 |
| `check-listener.sh <host> <port>` | مستمع المضيف: TLS فقط، صامت قبل المصادقة، يغلق الغرباء، شهادة ذاتية مؤقتة | 14، protocol.md §2 |
| `check-egress-blocks.sh <proxy-port>` | رفض localhost والشبكة المحلية والعناوين الداخلية عبر الـ Proxy، ومرور المسموح | 6.6، 14، protocol.md §6 |
| `check-logs-clean.sh [مسار...]` | لا URL ولا رؤوس ولا أسرار في السجلات | 6.8، 14، 15 |

كل سكربت يعيد 0 عند النجاح وغير ذلك عند الفشل، فيصلح للتشغيل في CI.

## أمثلة

```bash
scripts/security/check-session-keys.sh
scripts/security/check-listener.sh 203.0.113.10 51234
scripts/security/check-egress-blocks.sh 49152
scripts/security/check-logs-clean.sh ~/Library/Logs/Josour/app.log
```

على Windows تُشغَّل من Git Bash أو WSL؛ يحتاج `check-listener.sh` إلى `openssl` و`nc`، ويستفيد من `nmap` إن وُجد.
