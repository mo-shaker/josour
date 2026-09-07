using Josour.Core.Diagnostics;
using Josour.Core.Net;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>An <see cref="IProcessRunner"/> that answers from a script and records what it was asked to run.</summary>
public sealed class ScriptedProcessRunner : IProcessRunner
{
    private readonly Func<string, ProcessRunResult> _answer;

    public ScriptedProcessRunner(Func<string, ProcessRunResult> answer) => _answer = answer;

    /// <summary>Every command line, as <c>"&lt;file&gt; &lt;arguments&gt;"</c>.</summary>
    public List<string> Commands { get; } = new();

    public Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Commands.Add(fileName + " " + arguments);
        return Task.FromResult(_answer(arguments));
    }
}

/// <summary>An <see cref="IFirewallDiagnostics"/> that answers with whatever the test set.</summary>
public sealed class FakeFirewallDiagnostics : IFirewallDiagnostics
{
    public FakeFirewallDiagnostics(bool? rulePresent = null, string? profile = null)
    {
        Status = new FirewallStatus(rulePresent, profile);
    }

    public FirewallStatus Status { get; set; }

    /// <summary>Set to make the check throw, the way a wedged netsh eventually does.</summary>
    public Exception? Throw { get; set; }

    public int Calls { get; private set; }

    public Task<FirewallStatus> InspectAsync(CancellationToken ct)
    {
        Calls++;
        return Throw is { } error ? Task.FromException<FirewallStatus>(error) : Task.FromResult(Status);
    }
}

/// <summary>An <see cref="IVpnDetector"/> that answers with whatever the test set.</summary>
public sealed class FakeVpnDetector : IVpnDetector
{
    public FakeVpnDetector(VpnDetectionResult? result = null) => Result = result ?? VpnDetectionResult.NotDetected;

    public VpnDetectionResult Result { get; set; }

    /// <summary>Set to make detection throw.</summary>
    public Exception? Throw { get; set; }

    public int Calls { get; private set; }

    public VpnDetectionResult Detect()
    {
        Calls++;
        return Throw is { } error ? throw error : Result;
    }

    /// <summary>A VPN adapter that holds the outbound route: the only case that warrants a warning.</summary>
    public static VpnDetectionResult Holding(string name = "Mullvad", string description = "Mullvad VPN Tunnel") =>
        new(VpnConfidence.High, name, description, "mullvad", HoldsDefaultRoute: true);

    /// <summary>A VPN adapter that is up but carries nothing: present, and deliberately silent.</summary>
    public static VpnDetectionResult Present(string name = "utun3", string description = "WireGuard Tunnel") =>
        new(VpnConfidence.Low, name, description, "wireguard", HoldsDefaultRoute: false);
}
