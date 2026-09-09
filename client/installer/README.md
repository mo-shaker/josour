# المثبّت

- `Josour.iss`: سكربت Inno Setup. يضيف قاعدة Windows Firewall واردة للتنفيذي (المستمع المؤقت للنفق) ويحذفها عند الإزالة. يتطلب صلاحية المسؤول.
- خطوات الإصدار (على Windows):
  1. `dotnet publish src/Josour.App -c Release -r win-x64 --self-contained false -o publish/app`
  2. `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a publish\app\Josour.exe`
  3. `iscc /DAppVersion=<x.y.z> Josour.iss`
  4. `signtool sign ... Output\Josour-Setup-<x.y.z>.exe`
- شهادة التوقيع (OV/EV) تُطلب في اليوم الأول؛ بدونها يحذّر SmartScreen عند التثبيت.
- إشعارات Toast تسجّل CLSID تحت `HKCU\Software\Classes` عند أول استخدام؛ يجب أن يستدعي المثبّت عند الإزالة `Josour.exe --uninstall-notifications` (مفتاح يُضاف في الأسبوع 2 ويستدعي `ToastNotificationManagerCompat.Uninstall()`).
- الاسم `Josour Tunnel` لقاعدة الجدار الناري هو ما يفحصه `FirewallRuleChecker` في التطبيق؛ لا تغيّره في مكان دون الآخر.
