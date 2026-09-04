using System.Net;
using Mono.Nat;
using RouteBridge.Core.Net;
using RouteBridge.Core.Tunnel;

namespace RouteBridge.Tunnel.Candidates;

public sealed record GatherOptions(
    int ListenPort,
    string? BackendPublicIp,
    bool SamePublicIp,
    TimeSpan MappingLifetime,
    bool EnableUpnp = true);

/// <summary>تشخيص الجمع لبوابة قرار Relay (ADR-0003).</summary>
public sealed record GatherDiagnostics(
    bool UpnpFound,
    bool MappingOk,
    string? UpnpExternalIp,
    bool CgnatSuspected,
    bool Ipv6Global,
    IReadOnlyList<string> Errors)
{
    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["upnp_found"] = UpnpFound,
        ["mapping_ok"] = MappingOk,
        ["upnp_external_ip"] = UpnpExternalIp,
        ["cgnat_suspected"] = CgnatSuspected,
        ["ipv6_global"] = Ipv6Global,
        ["errors"] = Errors.ToList(),
    };
}

public sealed record GatherResult(IReadOnlyList<CandidateEndpoint> Candidates, GatherDiagnostics Diagnostics);

/// <summary>
/// يجمع مرشحي docs/protocol.md القسم 2 الخطوة 2 لمنفذ استماع: lan (عند same_public_ip فقط)، v6 (عام غير مؤقت)،
/// upnp (Mono.Nat)، public (IP الذي يراه الخادم + المنفذ). الترتيب lan, v6, upnp, public بلا تكرار. لا يرمي أبدًا.
/// </summary>
public sealed class CandidateGatherer : ICandidateSource
{
    public const string MappingDescription = "RouteBridge";
    public static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DeviceCallTimeout = TimeSpan.FromSeconds(5);

    // اكتشاف Mono.Nat عام على مستوى العملية (NatUtility ساكن)؛ نسلسل عمليات الجمع المتزامنة.
    private static readonly SemaphoreSlim NatGate = new(1, 1);

    private readonly TimeSpan _discoveryTimeout;
    private INatDevice? _device;
    private Mapping? _mapping;

    public CandidateGatherer(TimeSpan? discoveryTimeout = null)
        => _discoveryTimeout = discoveryTimeout ?? DefaultDiscoveryTimeout;

    /// <summary>هل يوجد تعيين UPnP نشط أنشأه هذا الكائن؟</summary>
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
                        // REAL-NETWORK: الراوتر قد يعيد منفذًا عامًا مختلفًا (NAT-PMP)؛ نستخدم ما أعاده.
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

        // CGNAT/NAT مزدوج مشتبه به: IP الراوتر الخارجي ضمن 100.64.0.0/10 (أو أي نطاق خاص) أو يخالف ما يراه الخادم.
        var cgnat = externalUsable && (IpRangePolicy.IsBlockedRange(externalIp!) || (backendIp is not null && !backendIp.Equals(externalIp)));

        var candidates = lan.Concat(v6).Concat(upnp).Concat(pub)
            .DistinctBy(c => (c.Ip, c.Port))
            .ToList();

        var diagnostics = new GatherDiagnostics(upnpFound, mappingOk, externalUsable ? externalIp!.ToString() : null, cgnat, ipv6Global, errors);
        return new GatherResult(candidates, diagnostics);
    }

    /// <summary>يزيل التعيين الذي أنشأه GatherAsync (إن وُجد). لا يرمي؛ يعيد false عند الفشل.</summary>
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

    // REAL-NETWORK: لم يُختبر إلا على هذا الجهاز (بلا بوابة UPnP)؛ يحتاج راوترين أو ثلاثة فعليين (الخطة 12.3 الأسبوع 1).
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
                try { NatUtility.StopDiscovery(); } catch { /* تجاهل */ }
            }
        }
        finally
        {
            NatGate.Release();
        }
    }

    private static string Describe(Exception e) => $"{e.GetType().Name}: {e.Message}";
}
