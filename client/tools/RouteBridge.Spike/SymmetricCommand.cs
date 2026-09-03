using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteBridge.Core.Control;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel;
using RouteBridge.Tunnel.Candidates;
using RouteBridge.Tunnel.Certificates;
using RouteBridge.Tunnel.Transport;

namespace RouteBridge.Spike;

/// <summary>الشكل نفسه الذي يحمله session.endpoint / session.peer_endpoint في docs/ws-protocol.md.</summary>
public sealed record EndpointJson(
    [property: JsonPropertyName("cert_fp_sha256")] string CertFpSha256,
    [property: JsonPropertyName("candidates")] List<CandidateDto> Candidates);

public static class SymmetricCommand
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(Args args, CancellationToken ct)
    {
        var roleText = args.Require("role").ToLowerInvariant();
        var role = roleText switch
        {
            "host" => TunnelRole.Host,
            "guest" => TunnelRole.Guest,
            _ => throw new UsageException("--role must be host or guest"),
        };
        if (!Guid.TryParse(args.Require("session"), out var sessionId)) throw new UsageException("--session must be a GUID");
        byte[] secret;
        try { secret = Convert.FromBase64String(args.Require("secret-b64")); }
        catch (FormatException) { throw new UsageException("--secret-b64 is not valid base64"); }
        if (secret.Length != 32) throw new UsageException("--secret-b64 must decode to exactly 32 bytes");
        var publicIp = args.Require("public-ip");
        var peerFile = args.Get("peer-file");
        var port = args.GetInt("port", 0);
        var samePublicIp = args.Has("same-public-ip");
        var enableUpnp = !args.Has("no-upnp");
        var timeout = TimeSpan.FromSeconds(args.GetInt("timeout-s", 30));
        var sessionMinutes = 30;

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(sessionMinutes);
        var material = new SessionMaterial(sessionId, role, secret, expiresAt, samePublicIp, args.Get("peer-public-ip") ?? string.Empty);

        using var cert = SessionCertificate.Create(expiresAt);
        await using var listener = new TunnelListener(port);
        await using var gatherer = new CandidateGatherer();
        Console.Error.WriteLine($"[{roleText}] listening on {listener.LocalEndPoint}; gathering candidates (upnp={(enableUpnp ? "on" : "off")}) ...");
        var gather = await gatherer.GatherAsync(new GatherOptions(listener.Port, publicIp, samePublicIp, TimeSpan.FromMinutes(sessionMinutes + 5), enableUpnp), ct);
        foreach (var error in gather.Diagnostics.Errors) Console.Error.WriteLine($"[{roleText}] gather: {error}");

        var ours = new EndpointJson(cert.FingerprintHex, gather.Candidates.Select(GatherCommand.ToDto).ToList());
        var oursJson = JsonSerializer.Serialize(ours, Json.Compact);
        var outFile = args.Get("out") ?? $"endpoint-{roleText}.json";
        await File.WriteAllTextAsync(outFile, oursJson + Environment.NewLine, ct);

        Console.Error.WriteLine();
        Console.Error.WriteLine($"=== OUR ENDPOINT ({roleText}) — send this line to the peer (also saved to {Path.GetFullPath(outFile)}) ===");
        Console.WriteLine(oursJson);
        Console.Error.WriteLine("=== END ===");
        Console.Error.WriteLine();

        EndpointJson peer;
        try
        {
            peer = await WaitForPeerAsync(peerFile, roleText, ct);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"[{roleText}] {e.Message}");
            return 1;
        }
        var peerInfo = new PeerEndpointInfo(peer.CertFpSha256, peer.Candidates.Select(c => new CandidateEndpoint(CandidateTypeNames.Parse(c.Type), c.Ip, c.Port)).ToList());
        Console.Error.WriteLine($"[{roleText}] peer fp {peer.CertFpSha256[..16]}…, {peerInfo.Candidates.Count} candidate(s): {string.Join(", ", peerInfo.Candidates.Select(c => $"{CandidateTypeNames.ToWire(c.Type)} {c.Ip}:{c.Port}"))}");
        Console.Error.WriteLine($"[{roleText}] connecting (timeout {timeout.TotalSeconds:F0} s) ...");

        var connector = new SymmetricConnector(material, cert, listener, new DirectTransport(), gather.Candidates);
        var outcome = await connector.ConnectAsync(peerInfo, timeout, ct);

        var report = new Dictionary<string, object?>
        {
            ["role"] = roleText,
            ["result"] = new Dictionary<string, object?>
            {
                ["connected"] = outcome.Result.Connected,
                ["winner_type"] = outcome.Result.WinnerType is null ? null : CandidateTypeNames.ToWire(outcome.Result.WinnerType.Value),
                ["connect_ms"] = outcome.Result.ConnectMs,
                ["tls_version"] = outcome.Result.TlsVersion,
                ["failure_reason"] = outcome.Result.FailureReason,
            },
            ["diagnostics"] = outcome.Diagnostics,
            ["gather_diagnostics"] = gather.Diagnostics.ToDictionary(),
        };
        Console.Error.WriteLine($"=== RESULT ({roleText}) ===");
        Console.WriteLine(JsonSerializer.Serialize(report, Json.Pretty));
        Console.Error.WriteLine("=== END ===");

        var exit = 2;
        if (outcome.Connection is { } connection)
        {
            await using (connection)
            {
                exit = await HelloExchangeAsync(connection.Stream, role, roleText, ct) ? 0 : 3;
            }
        }
        else
        {
            Console.Error.WriteLine($"[{roleText}] not connected: {outcome.Result.FailureReason}");
        }

        if (gatherer.HasMapping)
        {
            var removed = await gatherer.RemoveMappingAsync(CancellationToken.None);
            Console.Error.WriteLine($"[{roleText}] upnp mapping removed: {removed}");
        }
        CryptographicOperations.ZeroMemory(secret);
        return exit;
    }

    private static async Task<EndpointJson> WaitForPeerAsync(string? peerFile, string roleText, CancellationToken ct)
    {
        if (peerFile is not null)
        {
            Console.Error.WriteLine($"[{roleText}] waiting for peer endpoint file {Path.GetFullPath(peerFile)} (polling every 1 s, Ctrl+C to abort) ...");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(peerFile))
                {
                    try
                    {
                        var text = await File.ReadAllTextAsync(peerFile, ct);
                        var parsed = TryParse(text);
                        if (parsed is not null) return parsed;
                    }
                    catch (IOException) { /* الملف قيد الكتابة */ }
                }
                await Task.Delay(1000, ct);
            }
        }

        Console.Error.WriteLine($"[{roleText}] paste the PEER's endpoint JSON line and press Enter:");
        var buffer = new StringBuilder();
        while (true)
        {
            var line = await Console.In.ReadLineAsync(ct);
            if (line is null) throw new UsageException("stdin closed before a peer endpoint JSON was received");
            buffer.AppendLine(line);
            var parsed = TryParse(buffer.ToString());
            if (parsed is not null) return parsed;
        }
    }

    private static EndpointJson? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<EndpointJson>(text);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.CertFpSha256) || parsed.Candidates is null) return null;
            if (parsed.CertFpSha256.Length != 64) return null;
            foreach (var c in parsed.Candidates)
            {
                if (!CandidateTypeNames.TryParse(c.Type, out _)) return null;
                if (!IPAddress.TryParse(c.Ip, out _)) return null;
                if (c.Port is < 1 or > 65535) return null;
            }
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Guest يرسل "hello"، Host يرد "hello-ack". يثبت أن الـ stream المصادَق يعمل في الاتجاهين.
    private static async Task<bool> HelloExchangeAsync(Stream stream, TunnelRole role, string roleText, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HelloTimeout);
        try
        {
            if (role == TunnelRole.Guest)
            {
                await WriteLineAsync(stream, "hello", cts.Token);
                var reply = await ReadLineAsync(stream, cts.Token);
                Console.Error.WriteLine($"[{roleText}] sent hello, received '{reply}'");
                var ok = reply == "hello-ack";
                Console.Error.WriteLine(ok ? $"[{roleText}] hello exchange: ok" : $"[{roleText}] hello exchange: FAILED");
                return ok;
            }
            else
            {
                var greeting = await ReadLineAsync(stream, cts.Token);
                Console.Error.WriteLine($"[{roleText}] received '{greeting}'");
                if (greeting != "hello")
                {
                    Console.Error.WriteLine($"[{roleText}] hello exchange: FAILED");
                    return false;
                }
                await WriteLineAsync(stream, "hello-ack", cts.Token);
                Console.Error.WriteLine($"[{roleText}] sent hello-ack; hello exchange: ok");
                return true;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[{roleText}] hello exchange: FAILED {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 64)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) throw new EndOfStreamException("peer closed the stream");
            if (one[0] == (byte)'\n') break;
            bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
    }
}
