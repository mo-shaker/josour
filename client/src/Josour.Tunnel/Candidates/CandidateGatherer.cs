using System.Net;
using Mono.Nat;
using Josour.Core.Net;
using Josour.Core.Tunnel;

namespace Josour.Tunnel.Candidates;

public sealed record GatherOptions(
    int ListenPort,
    string? BackendPublicIp,
    bool SamePublicIp,
    TimeSpan MappingLifetime,
    bool EnableUpnp = true);

/// <summary>
/// The gathering's diagnostics. It was for the relay decision gate (ADR-0003); after [ADR-0009] its job became explaining a connection failure after the fact.
/// </summary>
/// <param name="CgnatChecked">
/// Whether a judgement was possible at all. When it is <c>false</c>, <see cref="CgnatSuspected"/> means "we do not know", not "not present".
/// </param>
public sealed record GatherDiagnostics(
    bool UpnpFound,
    bool MappingOk,
    string? UpnpExternalIp,
    bool CgnatSuspected,
    bool Ipv6Global,
    IReadOnlyList<string> Errors,
    bool CgnatChecked = false,
    NatReachability Reachability = NatReachability.Unknown,
    string NatEvidence = "not_assessed")
{
    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["upnp_found"] = UpnpFound,
        ["mapping_ok"] = MappingOk,
        ["upnp_external_ip"] = UpnpExternalIp,
        ["cgnat_suspected"] = CgnatSuspected,
        // Without this key, cgnat_suspected=false carried two meanings that could not be told apart: "checked and not present" and "could not be checked".
        ["cgnat_checked"] = CgnatChecked,
        ["nat_reachability"] = Reachability.ToString().ToLowerInvariant(),
        ["nat_evidence"] = NatEvidence,
        ["ipv6_global"] = Ipv6Global,
        ["errors"] = Errors.ToList(),
    };
}

public sealed record GatherResult(IReadOnlyList<CandidateEndpoint> Candidates, GatherDiagnostics Diagnostics);

/// <summary>
/// It gathers the candidates of docs/protocol.md section 2 step 2 for a listening port: lan (only when same_public_ip), v6 (public and non-temporary),
/// upnp (Mono.Nat), public (the IP the server sees + the port). The order is lan, v6, upnp, public with no duplicates. It never throws.
/// </summary>
public sealed class CandidateGatherer : ICandidateSource
{
    public const string MappingDescription = "Josour";
    public static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DeviceCallTimeout = TimeSpan.FromSeconds(5);

    // Mono.Nat's discovery is global to the process (NatUtility is static); we serialise concurrent gatherings.
    private static readonly SemaphoreSlim NatGate = new(1, 1);

    private readonly TimeSpan _discoveryTimeout;
    private INatDevice? _device;
    private Mapping? _mapping;

    public CandidateGatherer(TimeSpan? discoveryTimeout = null)
        => _discoveryTimeout = discoveryTimeout ?? DefaultDiscoveryTimeout;

    /// <summary>Is there an active UPnP mapping this object created?</summary>
    public bool HasMapping => _mapping is not null;

    public async Task<GatherResult> GatherAsync(GatherOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var lan = new List<CandidateEndpoint>();
        var v6 = new List<CandidateEndpoint>();
        var upnp = new List<CandidateEndpoint>();
        var pub = new List<CandidateEndpoint>();
        var ipv6Global = false;

        try
        {
            foreach (var la in LocalNetwork.GetUnicastAddresses())
            {
                if (LocalNetwork.IsLanIPv4(la.Address))
                {
                    if (options.SamePublicIp) lan.Add(new CandidateEndpoint(CandidateType.Lan, la.Address.ToString(), options.ListenPort));
                }
                else if (LocalNetwork.IsGlobalIPv6(la.Address) && LocalNetwork.IsStableAddress(la.Info))
                {
                    ipv6Global = true;
                    v6.Add(new CandidateEndpoint(CandidateType.V6, la.Address.ToString(), options.ListenPort));
                }
            }
        }
        catch (Exception e)
        {
            errors.Add($"interfaces: {Describe(e)}");
        }

        var upnpFound = false;
        var mappingOk = false;
        IPAddress? externalIp = null;
        if (options.EnableUpnp)
        {
            try
            {
                var device = await DiscoverAsync(ct).ConfigureAwait(false);
                if (device is null)
                {
                    errors.Add("upnp: no UPnP/NAT-PMP gateway found");
                }
                else
                {
                    upnpFound = true;
                    _device = device;
                    try
                    {
                        externalIp = await device.GetExternalIPAsync().WaitAsync(DeviceCallTimeout, ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        errors.Add($"upnp external ip: {Describe(e)}");
                    }

                    try
                    {
                        var lifetime = (int)Math.Clamp(options.MappingLifetime.TotalSeconds, 0, int.MaxValue);
                        var requested = new Mapping(Protocol.Tcp, options.ListenPort, options.ListenPort, lifetime, MappingDescription);
                        // REAL-NETWORK: the router may return a different external port (NAT-PMP); we use what it returned.
                        var created = await device.CreatePortMapAsync(requested).WaitAsync(DeviceCallTimeout, ct).ConfigureAwait(false);
                        _mapping = created ?? requested;
                        mappingOk = true;
                    }
                    catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        errors.Add($"upnp mapping: {Describe(e)}");
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"upnp discovery: {Describe(e)}");
            }
        }

        var externalUsable = externalIp is not null && !externalIp.Equals(IPAddress.Any) && !externalIp.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(externalIp);
        if (mappingOk && externalUsable && _mapping is not null)
            upnp.Add(new CandidateEndpoint(CandidateType.Upnp, externalIp!.ToString(), _mapping.PublicPort));

        IPAddress? backendIp = null;
        if (!string.IsNullOrWhiteSpace(options.BackendPublicIp))
        {
            if (IPAddress.TryParse(options.BackendPublicIp, out var parsed))
            {
                backendIp = parsed;
                pub.Add(new CandidateEndpoint(CandidateType.Public, parsed.ToString(), options.ListenPort));
            }
            else
            {
                errors.Add("public: backend public ip is not a valid IP address");
            }
        }

        // Judging the NAT weighs every source that speaks, not UPnP alone: the machine's own addresses, what the server sees,
        // and the gateway if it answers. Behind a phone hotspot no gateway answers — and that is precisely where CGNAT lives.
        IReadOnlyList<IPAddress> localAddresses;
        try
        {
            localAddresses = LocalNetwork.GetUnicastAddresses().Select(a => a.Address).ToList();
        }
        catch (Exception e)
        {
            errors.Add($"local addresses: {Describe(e)}");
            localAddresses = Array.Empty<IPAddress>();
        }

        var nat = NatAssessment.Assess(localAddresses, backendIp, upnpFound, mappingOk, externalUsable ? externalIp : null);

        var candidates = lan.Concat(v6).Concat(upnp).Concat(pub)
            .DistinctBy(c => (c.Ip, c.Port))
            .ToList();

        var diagnostics = new GatherDiagnostics(
            upnpFound, mappingOk, externalUsable ? externalIp!.ToString() : null,
            nat.CgnatSuspected, ipv6Global, errors,
            nat.Checked, nat.Reachability, nat.Evidence);
        return new GatherResult(candidates, diagnostics);
    }

    /// <summary>Removes the mapping GatherAsync created (if there is one). It does not throw; it returns false on failure.</summary>
    public async Task<bool> RemoveMappingAsync(CancellationToken ct)
    {
        var device = _device;
        var mapping = Interlocked.Exchange(ref _mapping, null);
        if (device is null || mapping is null) return false;
        try
        {
            await device.DeletePortMapAsync(mapping).WaitAsync(DeviceCallTimeout, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RemoveMappingAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // REAL-NETWORK: only exercised on this machine (with no UPnP gateway); it needs two or three actual routers (plan 12.3, week 1).
    private async Task<INatDevice?> DiscoverAsync(CancellationToken ct)
    {
        await NatGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var found = new TaskCompletionSource<INatDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<DeviceEventArgs> handler = (_, e) => found.TrySetResult(e.Device);
            NatUtility.DeviceFound += handler;
            try
            {
                NatUtility.StartDiscovery(NatProtocol.Upnp, NatProtocol.Pmp);
                try
                {
                    return await found.Task.WaitAsync(_discoveryTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }
            finally
            {
                NatUtility.DeviceFound -= handler;
                try { NatUtility.StopDiscovery(); } catch { /* ignore */ }
            }
        }
        finally
        {
            NatGate.Release();
        }
    }

    private static string Describe(Exception e) => $"{e.GetType().Name}: {e.Message}";
}
