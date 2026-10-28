using System.Net;
using System.Net.Sockets;
using Josour.Core.Allowlist;
using Josour.Core.Net;
using Josour.Tunnel.Mux;

namespace Josour.Egress;

/// <summary>The egress policy's result: a stream to the site (owning the socket, supporting the half-close) or an OPEN_FAIL reason.</summary>
public sealed class EgressResult
{
    private EgressResult(Stream? stream, OpenFailReason? reason, string? normalizedHost, IPAddress? connectedTo)
    {
        Stream = stream;
        Reason = reason;
        NormalizedHost = normalizedHost;
        ConnectedTo = connectedTo;
    }

    public Stream? Stream { get; }
    public OpenFailReason? Reason { get; }
    /// <summary>The name after normalisation (available from step 2 onwards, even on a refusal).</summary>
    public string? NormalizedHost { get; }
    public IPAddress? ConnectedTo { get; }
    public bool IsOk => Stream is not null;

    public static EgressResult Ok(Stream stream, string normalizedHost, IPAddress connectedTo) => new(stream, null, normalizedHost, connectedTo);
    public static EgressResult Fail(OpenFailReason reason, string? normalizedHost = null) => new(null, reason, normalizedHost, null);
}

public sealed class EgressPolicyOptions
{
    public static readonly IReadOnlyList<int> DefaultAllowedPorts = new[] { 80, 443 };

    public IReadOnlyList<int> AllowedPorts { get; init; } = DefaultAllowedPorts;
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public IHostResolver Resolver { get; init; } = DnsHostResolver.Instance;

    /// <summary>The host's own addresses (its interfaces and gateways) and its public address as the server sees it; all of them blocked.</summary>
    public IReadOnlyList<IPAddress> LocalAddresses { get; init; } = Array.Empty<IPAddress>();

    /// <summary>
    /// The host's on-link networks (its global IPv6 prefixes, IPv4 subnets narrower than /16), blocked whole so the guest cannot reach the
    /// host's LAN neighbours by their global addresses. Empty = only the exact addresses above.
    /// </summary>
    public IReadOnlyList<IPNetwork> LocalNetworks { get; init; } = Array.Empty<IPNetwork>();

    /// <summary>
    /// The blocked check for one address. The default is IpRangePolicy.IsBlocked(address, LocalAddresses, LocalNetworks).
    /// Replaced in the E2E tests only (to permit 127.0.0.1 inside the process).
    /// </summary>
    public Func<IPAddress, bool>? AddressBlocker { get; init; }
}

/// <summary>
/// The egress policy on the host per docs/protocol.md section 6, in order: (1) normalisation, (2) an address literal (before or after it) -> ip_literal, (3) the list -> not_allowed,
/// (4) the port -> port_not_allowed, (5) DNS once with a 5 s timeout -> dns_failed, (6) any blocked address -> private_ip,
/// (7) Socket.ConnectAsync(IPAddress[], port) using the checked list only, with a 10 s timeout -> connect_failed.
/// </summary>
public sealed class EgressPolicy
{
    private readonly IAllowlist _allowlist;
    private readonly EgressPolicyOptions _options;
    private readonly Func<IPAddress, bool> _isBlocked;

    public EgressPolicy(IAllowlist allowlist, EgressPolicyOptions? options = null)
    {
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _options = options ?? new EgressPolicyOptions();
        var locals = _options.LocalAddresses;
        var networks = _options.LocalNetworks;
        _isBlocked = _options.AddressBlocker ?? (address => IpRangePolicy.IsBlocked(address, locals, networks));
    }

    public IAllowlist Allowlist => _allowlist;

    /// <summary>Steps 1-4 only (with no I/O). It returns null on acceptance, with the normalised name.</summary>
    public OpenFailReason? Evaluate(string host, int port, out string? normalizedHost)
    {
        normalizedHost = null;
        if (string.IsNullOrWhiteSpace(host)) return OpenFailReason.NotAllowed;
        if (IpRangePolicy.IsIpLiteral(host)) return OpenFailReason.IpLiteral;

        string normalized;
        try { normalized = AllowlistMatcher.NormalizeHost(host); }
        catch (ArgumentException) { return OpenFailReason.NotAllowed; }
        normalizedHost = normalized;
        // Normalisation can turn a name into a literal: "8.8.8.8." (the trailing dot), fullwidth digits, "0x7f.0.0.1.".
        if (IpRangePolicy.IsIpLiteral(normalized)) return OpenFailReason.IpLiteral;
        if (port is < 1 or > 65535) return OpenFailReason.PortNotAllowed;

        if (_allowlist.IsAllowed(normalized, port, _options.AllowedPorts)) return null;
        // The name matched but the port is neither within allowed_ports nor the entry's restriction -> port_not_allowed; otherwise not_allowed.
        var hostMatched = _allowlist.HostMatches(normalized);
        return hostMatched ? OpenFailReason.PortNotAllowed : OpenFailReason.NotAllowed;
    }

    /// <summary>All of steps 1-7. It throws only on an external cancellation.</summary>
    public async Task<EgressResult> OpenAsync(string host, int port, CancellationToken ct)
    {
        var early = Evaluate(host, port, out var normalized);
        if (early is { } reason) return EgressResult.Fail(reason, normalized);
        var target = normalized!;

        IPAddress[] addresses;
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(_options.DnsTimeout);
            addresses = await _options.Resolver.ResolveAsync(target, dnsCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EgressResult.Fail(OpenFailReason.DnsFailed, target);
        }

        addresses = addresses.Where(a => a is not null && a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToArray();
        if (addresses.Length == 0) return EgressResult.Fail(OpenFailReason.DnsFailed, target);
        if (addresses.Any(_isBlocked)) return EgressResult.Fail(OpenFailReason.PrivateIp, target);

        var socket = CreateSocket(addresses);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);
            // Using the checked list only, not the name (which closes DNS rebinding and Happy Eyeballs on unchecked names).
            await socket.ConnectAsync(addresses, port, connectCts.Token).ConfigureAwait(false);
            var remote = socket.RemoteEndPoint as IPEndPoint;
            var connectedTo = remote?.Address ?? addresses[0];
            if (connectedTo.IsIPv4MappedToIPv6) connectedTo = connectedTo.MapToIPv4();
            return EgressResult.Ok(new SocketStream(socket), target, connectedTo);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception)
        {
            socket.Dispose();
            return EgressResult.Fail(OpenFailReason.ConnectFailed, target);
        }
    }

    private static Socket CreateSocket(IPAddress[] addresses)
    {
        var needV6 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        var needV4 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (needV6 || !needV4)
        {
            try
            {
                return new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = needV4 };
            }
            catch (SocketException) when (needV4)
            {
                // No IPv6 on the machine; we fall back to v4
            }
        }
        return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }
}
