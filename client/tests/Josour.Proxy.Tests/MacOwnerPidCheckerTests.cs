using System.Net;
using System.Net.Sockets;
using Josour.Core.Browser;
using Josour.Proxy;

namespace Josour.Proxy.Tests;

/// <summary>
/// The control that keeps this from being a device-wide proxy, on macOS.
/// <para>
/// The parsing is tested against captured lsof output so it runs anywhere; the last test opens a real socket and
/// asks about it, which is the only way to know the arguments and the field format are right.
/// </para>
/// </summary>
public class MacOwnerPidCheckerTests
{
    // Real output from `lsof -nP -iTCP:53246 -sTCP:ESTABLISHED -Fpn`, where one process holds both ends of a
    // loopback connection: the client socket 53247->53246 and the listening side's 53246->53247.
    private const string BothEnds = """
        p76769
        f4
        n127.0.0.1:53247->127.0.0.1:53246
        f5
        n127.0.0.1:53246->127.0.0.1:53247
        """;

    [Fact]
    public void The_client_end_is_picked_not_the_listener()
    {
        // Both ends are listed and both mention the same two ports. Matching the PAIR in the right order is what
        // distinguishes the process that connected from the process being connected to — which is us.
        Assert.Equal(76769, MacOwnerPidChecker.Parse(BothEnds, clientPort: 53247, proxyPort: 53246));
    }

    [Fact]
    public void A_connection_nobody_reported_is_unknown()
    {
        Assert.Null(MacOwnerPidChecker.Parse(BothEnds, clientPort: 9999, proxyPort: 53246));
    }

    [Fact]
    public void The_reversed_pair_does_not_match()
    {
        // Asking with the ports the wrong way round must not quietly succeed by finding the listener's row.
        Assert.Null(MacOwnerPidChecker.Parse(BothEnds, clientPort: 53246, proxyPort: 53247 + 1));
    }

    [Fact]
    public void Each_name_line_belongs_to_the_pid_above_it()
    {
        const string twoProcesses = """
            p111
            n127.0.0.1:5000->127.0.0.1:9000
            p222
            n127.0.0.1:5001->127.0.0.1:9000
            """;

        Assert.Equal(111, MacOwnerPidChecker.Parse(twoProcesses, 5000, 9000));
        Assert.Equal(222, MacOwnerPidChecker.Parse(twoProcesses, 5001, 9000));
    }

    [Fact]
    public void An_ipv6_connection_is_read_the_same_way()
    {
        const string v6 = """
            p333
            n[::1]:5002->[::1]:9000
            """;

        Assert.Equal(333, MacOwnerPidChecker.Parse(v6, 5002, 9000));
    }

    [Fact]
    public void A_listening_socket_is_never_the_client()
    {
        const string listener = """
            p444
            n127.0.0.1:9000
            """;

        Assert.Null(MacOwnerPidChecker.Parse(listener, 9000, 9000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pnotanumber\nn127.0.0.1:1->127.0.0.1:2")]
    public void Output_that_makes_no_sense_is_unknown_rather_than_an_exception(string output)
    {
        // Unknown is refused by the proxy's fail-closed default, so the safe answer is also the quiet one.
        Assert.Null(MacOwnerPidChecker.Parse(output, 1, 2));
    }

    [MacFact]
    public void It_names_this_very_process_as_the_owner_of_a_real_connection()
    {
        // The only test that proves the lsof invocation itself — the path, the arguments and the field format.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var proxyEnd = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(proxyEnd);
        using var accepted = listener.Accept();

        var clientEnd = (IPEndPoint)client.LocalEndPoint!;
        var owner = MacOwnerPidChecker.Instance.GetOwnerPid(clientEnd, proxyEnd);

        Assert.Equal(Environment.ProcessId, owner);
    }
}

/// <summary>
/// The other half of the control, end to end: the real lsof checker in a real proxy, refusing a connection from
/// a process the browser did not start.
/// <para>
/// The positive case was measured with the spike tool — Chrome's nineteen connections all admitted, none refused.
/// This is the case that matters more: the local proxy listens on loopback, so anything on the machine can reach
/// it, and acceptance criterion 10 says Teams and Outlook and the ordinary browser stay on the user's own
/// connection. The test IS a process the browser did not start.
/// </para>
/// </summary>
public class MacOwnerCheckIntegrationTests
{
    private sealed class OwnsNothing : IBrowserSession
    {
        public bool IsRunning => true;

        /// <summary>The shape of the real question: this pid is not in the browser's process tree.</summary>
        public bool OwnsProcess(int pid) => false;

        public Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
            => Task.FromResult(new BrowserLaunchResult(true, null, null));

        public Task CloseAsync(TimeSpan graceful, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [MacFact]
    public async Task A_connection_from_a_process_the_browser_did_not_start_is_refused()
    {
        await using var proxy = Proxies.Start(
            browser: new OwnsNothing(),
            checker: MacOwnerPidChecker.Instance,
            rejectUnknown: true);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port));

        // The proxy identifies the owner (this test), asks the browser whether it is one of its own, is told no,
        // and drops it. Whether that arrives as an orderly close or a reset is the kernel's business and varies;
        // what must be true is that nothing was served.
        try
        {
            var read = await client.ReceiveAsync(new byte[1], SocketFlags.None);
            Assert.Equal(0, read);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Also a refusal.
        }

        Assert.Equal(1, proxy.Counters.RejectedByOwner);
        Assert.Equal(0, proxy.Counters.Tunneled);
    }

    [MacFact]
    public async Task The_owner_is_identified_at_all_which_is_what_makes_the_refusal_a_decision()
    {
        // A refusal that happened because nothing could be identified would be indistinguishable from this one,
        // and would mean the control was not working rather than working. So: the checker names this process.
        await using var proxy = Proxies.Start(
            browser: new OwnsNothing(), checker: MacOwnerPidChecker.Instance, rejectUnknown: true);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port));

        var owner = MacOwnerPidChecker.Instance.GetOwnerPid(
            (IPEndPoint)client.LocalEndPoint!, new IPEndPoint(IPAddress.Loopback, proxy.Port));

        Assert.Equal(Environment.ProcessId, owner);
    }
}
