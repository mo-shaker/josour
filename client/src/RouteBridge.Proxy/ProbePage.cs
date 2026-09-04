using System.Net;
using System.Text;

namespace RouteBridge.Proxy;

/// <summary>صفحة الفحص التي يُفتح عليها المتصفح (http://check.routebridge/). وصولها يثبت أن المتصفح يستخدم الـ Proxy.</summary>
public static class ProbePage
{
    public const string Host = "check.routebridge";
    public const string TitleEnglish = "Tunnel active. Sites will see: ";

    public static string Html(string peerPublicIp)
    {
        var ip = WebUtility.HtmlEncode(peerPublicIp ?? string.Empty);
        return $$"""
            <!doctype html>
            <html lang="ar" dir="rtl">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>RouteBridge</title>
            <style>
            body{font-family:Segoe UI,Tahoma,Arial,sans-serif;background:#f4f6f9;color:#1b2430;margin:0;display:flex;min-height:100vh;align-items:center;justify-content:center}
            main{background:#fff;border-radius:12px;padding:32px 40px;box-shadow:0 8px 24px rgba(0,0,0,.08);max-width:560px;text-align:center}
            h1{font-size:1.5rem;margin:0 0 12px}
            .ip{font-family:Consolas,monospace;font-size:1.25rem;background:#eef2f7;padding:6px 12px;border-radius:6px;display:inline-block;direction:ltr}
            p.en{direction:ltr;color:#4a5568;margin-top:20px;font-size:.95rem}
            </style>
            </head>
            <body>
            <main>
            <h1>النفق نشط</h1>
            <p>المواقع سترى العنوان: <span class="ip">{{ip}}</span></p>
            <p class="en">{{TitleEnglish}}{{ip}}</p>
            </main>
            </body>
            </html>
            """;
    }

    public static byte[] HtmlBytes(string peerPublicIp) => Encoding.UTF8.GetBytes(Html(peerPublicIp));
}
