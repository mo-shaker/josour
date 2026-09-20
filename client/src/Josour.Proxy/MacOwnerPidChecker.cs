using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Josour.Proxy;

/// <summary>
/// The macOS answer to "which process opened this connection", via <c>lsof</c>.
/// <para>
/// This is the control that keeps the product from being a device-wide proxy. The local CONNECT proxy listens on
/// loopback, so any program on the machine can reach it; only this tells the work browser apart from the rest, and
/// acceptance criterion 10 — Teams and Outlook and the normal browser stay on the user's own connection — rests
/// entirely on it. Windows does the same job through <c>iphlpapi!GetExtendedTcpTable</c>.
/// </para>
/// <para>
/// <b>Why a subprocess and not libproc.</b> macOS has no public call that maps a socket to a PID; the syscall route
/// is <c>proc_listpids</c> plus <c>proc_pidfdinfo</c> over every descriptor of every process, decoding
/// <c>socket_fdinfo</c> — a large, version-sensitive struct to lay out by hand for a security control that must not
/// be subtly wrong. <c>lsof</c> does exactly that walk, ships with every macOS, and prints a documented field
/// format. It is also what the rest of this codebase already does for platform facts it cannot ask for directly
/// (<c>netsh</c>, <c>socketfilterfw</c>, <c>sw_vers</c>).
/// </para>
/// <para>
/// <b>Why no cache.</b> Measured at about 20 ms per call, which is cheap enough to ask every time. Caching the
/// port-to-PID table would open a real hole: an ephemeral port the browser has released can be reused by another
/// process within the cache window, and a stale row would admit it. Twenty milliseconds is a better price than
/// that answer being occasionally wrong.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOwnerPidChecker : IOwnerPidChecker
{
    /// <summary>Absolute, because this is a security control and <c>PATH</c> is not.</summary>
    public const string Lsof = "/usr/sbin/lsof";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<int, string?> _query;

    public MacOwnerPidChecker()
        : this(RunLsof)
    {
    }

    /// <summary>Takes the proxy's port and returns lsof's field output, or null when it could not be run (tests).</summary>
    public MacOwnerPidChecker(Func<int, string?> query) => _query = query;

    public static MacOwnerPidChecker Instance { get; } = new();

    /// <param name="remoteEndPoint">The client's end as the proxy sees it — so its LOCAL port.</param>
    /// <param name="localEndPoint">The proxy's listening end.</param>
    public int? GetOwnerPid(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(localEndPoint);

        var output = _query(localEndPoint.Port);
        return output is null ? null : Parse(output, clientPort: remoteEndPoint.Port, proxyPort: localEndPoint.Port);
    }

    /// <summary>
    /// Reads lsof's <c>-F</c> field output: a <c>p&lt;pid&gt;</c> line, then that process's <c>f</c>/<c>n</c> lines
    /// until the next <c>p</c>. Public because it is the half of this control that can be tested exhaustively
    /// without a socket, and a security decision deserves that.
    /// <para>
    /// Both ends of a loopback connection belong to some process, and when the browser talks to a proxy in the
    /// same machine lsof lists both. Matching on the pair — local port is the client's AND remote port is the
    /// proxy's — is what picks the client rather than the listener, which is this process itself.
    /// </para>
    /// </summary>
    public static int? Parse(string output, int clientPort, int proxyPort)
    {
        int? pid = null;
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length < 2)
            {
                continue;
            }

            switch (line[0])
            {
                case 'p':
                    pid = int.TryParse(line[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : null;
                    break;

                case 'n' when pid is not null && Matches(line[1..], clientPort, proxyPort):
                    return pid;
            }
        }

        return null;
    }

    /// <summary>Matches <c>127.0.0.1:1234-&gt;127.0.0.1:5678</c>, and the bracketed IPv6 spelling of the same.</summary>
    private static bool Matches(string name, int clientPort, int proxyPort)
    {
        var arrow = name.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return false; // a listening socket, which has no peer and is never the client
        }

        return PortOf(name[..arrow]) == clientPort && PortOf(name[(arrow + 2)..]) == proxyPort;
    }

    private static int? PortOf(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon >= 0
               && int.TryParse(endpoint[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? port
            : null;
    }

    private static string? RunLsof(int proxyPort)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Lsof)
            {
                // -n and -P: no DNS and no service-name lookups, which is both faster and free of surprises.
                // -sTCP:ESTABLISHED: a connection being accepted is established by definition.
                ArgumentList =
                {
                    "-nP",
                    "-iTCP:" + proxyPort.ToString(CultureInfo.InvariantCulture),
                    "-sTCP:ESTABLISHED",
                    "-Fpn",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)DefaultTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and the kill.
                }

                return null;
            }

            // lsof exits non-zero when nothing matched, which is an answer ("no such connection"), not a failure.
            // Either way the output is what gets parsed, and an empty one yields null — which the proxy refuses.
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Cannot tell. The caller's RejectUnknownOwner decides, and it fails closed.
            return null;
        }
    }
}
