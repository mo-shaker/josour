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
        "session" => await SessionCommand.RunAsync(options, cancel.Token),
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

          session --api https://host --email E --password P --role host [--available|--no-available] [--auto-reject]
          session --api https://host --email E --password P --role guest --host-device <id|name>
                  [--minutes 30] [--list-hosts] [--curl-test <url>] [--browser chrome|edge] [--profile path]
                  common: [--state-dir path] [--reset-device] [--listen-port N] [--no-upnp]
                          [--connect-timeout-s 30] [--stats-interval-s 30] [--quiet] [--no-diagnostics]
              The headless equivalent of the WPF app: sign in through ApiClient/AuthSession, open the real
              ControlChannel over WSS, run the full TunnelSession for the role, and print one JSON event per
              line on stdout (machine-readable) with a human summary on stderr.
              Host: announces host.available (the default; --available is the explicit form, --no-available stays
                    connected but hidden), waits for request.incoming, auto-accepts (--auto-reject to refuse),
                    runs the host side and reports session.connected, session.stats every 30 s and session.end.
                    With --listen-port N that port is announced in host.available so the server's reachability
                    check has something to probe, and the tunnel listener binds it.
              Guest: --list-hosts prints the available hosts and exits; otherwise sends request.create, runs the
                    guest side, prints the local proxy port, and with --curl-test performs an HTTP GET through
                    the proxy itself (no browser) so the response body proves the traffic left via the host's IP.
                    --browser launches the real work browser instead (cannot be combined with --curl-test).
              Both honour session.terminate, expires_at and Ctrl+C with the protocol section-7 cleanup, and both
              POST the tunnel listener's unauthenticated-connection count to /api/v1/diagnostics at session end
              (docs/api.md reserved keys: listener_unauthenticated, listener_port, unauthenticated_peers).
              The listener is the only place the system sees an unauthorised probe of this device's port, and
              the server cannot observe it. --no-diagnostics suppresses that report.
              Exit codes: 0 success, 1 usage, 2 connect failed, 3 tunnel died, 4 protocol/auth error.
              See docs/spike-runbook.md for the two-machine procedure and the event schema.
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
