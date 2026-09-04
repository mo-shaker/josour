using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RouteBridge.Spike;
using RouteBridge.Tunnel.Certificates;
using RouteBridge.Tunnel.Tls;

// أداة الأسبوع 1 لمسار B: اختبار الشهادة، جمع المرشحين، الاتصال المتماثل بين جهازين، وفحص قابلية الوصول.
// الأسطر البشرية على stderr، وJSON على stdout ليسهل النسخ واللصق أو إعادة التوجيه إلى ملف.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Usage();
    return 1;
}

try
{
    var options = new Args(args, 1);
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

    return args[0].ToLowerInvariant() switch
    {
        "certtest" => await CertTestAsync(cancel.Token),
        "gather" => await GatherCommand.RunAsync(options, cancel.Token),
        "symmetric" => await SymmetricCommand.RunAsync(options, cancel.Token),
        "probe" => await ProbeCommand.RunAsync(options, cancel.Token),
        "browser" => await BrowserCommand.RunAsync(options, cancel.Token),
        _ => Unknown(args[0]),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Usage();
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command '{command}'");
    Usage();
    return 1;
}

static void Usage()
{
    Console.Error.WriteLine("""
        RouteBridge.Spike — Track B week-1 networking spike

          certtest
              Create a SessionCertificate, run a TLS loopback handshake with it, dispose, print "ok".

          gather --port N [--public-ip X] [--same-public-ip] [--no-upnp] [--lifetime-min 10]
              Gather candidates (lan/v6/upnp/public) + host diagnostics, print JSON, remove the UPnP mapping.

          symmetric --role host|guest --session <guid> --secret-b64 <s> --public-ip X
                    [--peer-file path] [--port N] [--same-public-ip] [--no-upnp] [--timeout-s 30]
                    [--post-to <api base url> --token <jwt>]
              Print OUR endpoint JSON (stdout + endpoint-<role>.json), wait for the peer's endpoint JSON
              (from --peer-file when it appears, or pasted on stdin), run the symmetric connect, print the
              result + diagnostics JSON, then exchange hello/hello-ack over the authenticated stream.
              With --post-to, POST the final result JSON to <base>/api/v1/diagnostics as {session_id, role, data}.

          browser --proxy-port N | --self-hosted [--browser chrome|edge] [--profile path]
              Locate the browser (App Paths + Authenticode publisher), detect enterprise proxy/profile policies,
              launch it inside a Job Object on http://check.routebridge/ (with --self-hosted: a local
              ConnectProxyServer with an empty allowlist serves the probe page and everything goes direct),
              report probe_hit_ms (10 s timeout), close it after 5 s and report close_ms. JSON on stdout.
              This is the Windows browser-matrix tool (Chrome/Edge x managed/unmanaged x Win10/11).

          probe --api https://host --token T --ip X --port N
              POST /api/v1/probe on the backend (best effort).
        """);
}

static async Task<int> CertTestAsync(CancellationToken ct)
{
    var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
    var cert = SessionCertificate.Create(expiresAt);
    Console.WriteLine($"subject:            {cert.Certificate.Subject}");
    Console.WriteLine($"fingerprint_sha256: {cert.FingerprintHex}");
    Console.WriteLine($"not_before:         {cert.NotBefore:O}");
    Console.WriteLine($"not_after:          {cert.NotAfter:O}");
    Console.WriteLine($"has_private_key:    {cert.Certificate.HasPrivateKey}");
    Console.WriteLine($"key:                {cert.Certificate.GetECDsaPublicKey()?.KeySize ?? 0}-bit ECDSA");
    Console.WriteLine($"os:                 {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

    // مصافحة TLS على loopback بالشهادة نفسها: هذا ما يثبت أن Schannel يقبل المفتاح على Windows.
    try
    {
        var (server, client) = await Loopback.CreatePairAsync(ct);
        var serverTask = TlsChannel.AuthenticateAsServerAsync(server, cert.Certificate, TimeSpan.FromSeconds(10), ct);
        var clientTask = TlsChannel.AuthenticateAsClientAsync(client, cert.FingerprintSha256, TimeSpan.FromSeconds(10), ct);
        var serverResult = await serverTask;
        var clientResult = await clientTask;
        Console.WriteLine($"tls_loopback:       ok (server {serverResult.TlsVersion}, client {clientResult.TlsVersion})");
        await serverResult.Stream.DisposeAsync();
        await clientResult.Stream.DisposeAsync();
    }
    catch (Exception e)
    {
        Console.WriteLine($"tls_loopback:       FAILED {e.GetType().Name}: {e.Message}");
        cert.Dispose();
        return 2;
    }

    cert.Dispose();
    bool disposed;
    try { _ = cert.Certificate.RawData; disposed = false; }
    catch (CryptographicException) { disposed = true; }
    catch (ObjectDisposedException) { disposed = true; }
    Console.WriteLine($"disposed:           {disposed}");
    Console.WriteLine("ok");
    return 0;
}
