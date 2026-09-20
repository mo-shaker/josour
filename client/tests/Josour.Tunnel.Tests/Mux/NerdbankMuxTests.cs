using System.Net.Sockets;
using System.Text;
using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;

namespace Josour.Tunnel.Tests.Mux;

public class NerdbankMuxTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Open_Ok_ThenBidirectionalData_OverRealTcpTarget()
    {
        await using var pair = await MuxPair.CreateAsync();
        var (target, far) = await TcpTarget.CreateAsync();
        using var _far = far;
        MuxOpenRequest? seen = null;
        pair.Host.OpenRequested = (req, _) => { seen = req; return Task.FromResult(MuxOpenDecision.Ok(target)); };

        var open = await pair.Guest.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Timeout);
        Assert.True(open.IsOpen);
        Assert.Null(open.Reason);
        Assert.Equal(new MuxOpenRequest("example.com", 443), seen);

        await using var stream = open.Stream!;
        await stream.WriteAsync(Encoding.ASCII.GetBytes("ping"));
        var received = new byte[4];
        await far.ReceiveAsync(received, SocketFlags.None).WaitAsync(Timeout);
        Assert.Equal("ping", Encoding.ASCII.GetString(received));
        await far.SendAsync(Encoding.ASCII.GetBytes("pong"), SocketFlags.None);
        Assert.Equal("pong", Encoding.ASCII.GetString(await StreamIo.ReadExactAsync(stream, 4).WaitAsync(Timeout)));
        Assert.Equal(1, pair.Guest.Stats.OpenStreams);
        Assert.Equal(1, pair.Host.Stats.OpenStreams);
    }

    [Theory]
    [InlineData(OpenFailReason.NotAllowed)]
    [InlineData(OpenFailReason.PrivateIp)]
    [InlineData(OpenFailReason.PortNotAllowed)]
    [InlineData(OpenFailReason.DnsFailed)]
    [InlineData(OpenFailReason.ConnectFailed)]
    [InlineData(OpenFailReason.Limit)]
    [InlineData(OpenFailReason.IpLiteral)]
    public async Task Open_Rejected_CarriesEncodedReason(OpenFailReason reason)
    {
        await using var pair = await MuxPair.CreateAsync();
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Fail(reason));

        var open = await pair.Guest.OpenStreamAsync("blocked.example", 443, CancellationToken.None).WaitAsync(Timeout);

        Assert.False(open.IsOpen);
        Assert.Null(open.Stream);
        Assert.Equal(reason, open.Reason);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((pair.Host.Stats.OpenStreams > 0 || pair.Guest.Stats.OpenStreams > 0) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(0, pair.Host.Stats.OpenStreams);
        Assert.Equal(0, pair.Guest.Stats.OpenStreams);
    }

    [Fact]
    public async Task Open_WithoutHostHandler_IsNotAllowed()
    {
        await using var pair = await MuxPair.CreateAsync();
        var open = await pair.Guest.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(OpenFailReason.NotAllowed, open.Reason);
    }

    [Fact]
    public async Task Open_WhenHandlerThrows_IsConnectFailed()
    {
        await using var pair = await MuxPair.CreateAsync();
        pair.Host.OpenRequested = (_, _) => throw new InvalidOperationException("boom");
        var open = await pair.Guest.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(OpenFailReason.ConnectFailed, open.Reason);
    }

    [Fact]
    public async Task Open_WhenHostIsSlow_TimesOutAsConnectFailed()
    {
        await using var pair = await MuxPair.CreateAsync(new MuxOptions { EnableLiveness = false, OpenTimeout = TimeSpan.FromMilliseconds(300) });
        var release = new TaskCompletionSource();
        pair.Host.OpenRequested = async (_, ct) => { await release.Task.WaitAsync(ct); return MuxOpenDecision.Fail(OpenFailReason.NotAllowed); };
        var open = await pair.Guest.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(OpenFailReason.ConnectFailed, open.Reason);
        release.SetResult();
    }

    [Fact]
    public async Task HalfClose_MapsToShutdownSend_OnFarSide_AndBack()
    {
        await using var pair = await MuxPair.CreateAsync();
        var (target, far) = await TcpTarget.CreateAsync();
        using var _far = far;
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(target));

        var open = await pair.Guest.OpenStreamAsync("example.com", 80, CancellationToken.None).WaitAsync(Timeout);
        await using var stream = open.Stream!;
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET"));
        await ((IHalfClosable)stream).CompleteWritingAsync(CancellationToken.None);

        // The far end sees the data then a FIN (Shutdown(Send) at the host) and can reply afterwards
        var buffer = new byte[16];
        var n = await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout);
        Assert.Equal("GET", Encoding.ASCII.GetString(buffer, 0, n));
        Assert.Equal(0, await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout));
        await far.SendAsync(Encoding.ASCII.GetBytes("200 OK"), SocketFlags.None);
        far.Shutdown(SocketShutdown.Send);

        Assert.Equal("200 OK", Encoding.ASCII.GetString(await StreamIo.ReadExactAsync(stream, 6).WaitAsync(Timeout)));
        Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pair.Host.Stats.OpenStreams > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(0, pair.Host.Stats.OpenStreams);
    }

    [Fact]
    public async Task FarSideClose_PropagatesEofToGuest_AndGuestDisposeClosesFarSocket()
    {
        await using var pair = await MuxPair.CreateAsync();
        var (target, far) = await TcpTarget.CreateAsync();
        using var _far = far;
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(target));

        var open = await pair.Guest.OpenStreamAsync("example.com", 80, CancellationToken.None).WaitAsync(Timeout);
        var stream = open.Stream!;
        await far.SendAsync(Encoding.ASCII.GetBytes("bye"), SocketFlags.None);
        far.Shutdown(SocketShutdown.Send);
        Assert.Equal("bye", Encoding.ASCII.GetString(await StreamIo.ReadExactAsync(stream, 3).WaitAsync(Timeout)));
        Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout));

        await stream.DisposeAsync();
        var buffer = new byte[1];
        Assert.Equal(0, await far.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(Timeout));
    }

    [Fact]
    public async Task Ping_ReturnsRoundTrip_BothDirections()
    {
        await using var pair = await MuxPair.CreateAsync();
        var rttGuest = await pair.Guest.PingAsync(CancellationToken.None).WaitAsync(Timeout);
        var rttHost = await pair.Host.PingAsync(CancellationToken.None).WaitAsync(Timeout);
        Assert.InRange(rttGuest.TotalMilliseconds, 0, 5000);
        Assert.InRange(rttHost.TotalMilliseconds, 0, 5000);
    }

    [Fact]
    public async Task GoAway_ReachesPeer_WithReason()
    {
        await using var pair = await MuxPair.CreateAsync();
        await pair.Guest.CloseAsync(GoAwayReason.Expired);
        await pair.Host.Completion.WaitAsync(Timeout);
        Assert.Equal(GoAwayReason.Expired, pair.Host.RemoteGoAway);
        Assert.True(pair.Host.IsClosed);
        await Assert.ThrowsAsync<MuxClosedException>(() => pair.Guest.OpenStreamAsync("example.com", 443, CancellationToken.None));
    }

    [Fact]
    public async Task OpenStreams_FailAfterPeerGoAway()
    {
        await using var pair = await MuxPair.CreateAsync();
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(new TestTarget()));
        var open = await pair.Guest.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Timeout);
        Assert.True(open.IsOpen);
        await pair.Host.CloseAsync(GoAwayReason.SessionEnd);
        await pair.Guest.Completion.WaitAsync(Timeout);
        Assert.Equal(GoAwayReason.SessionEnd, pair.Guest.RemoteGoAway);
        await Assert.ThrowsAsync<MuxClosedException>(() => pair.Guest.OpenStreamAsync("example.com", 443, CancellationToken.None));
    }

    [Fact]
    public async Task TransportDies_WithoutGoAway_FaultsCompletion()
    {
        // A network drop or the other side's process dying: no GOAWAY, the transport ends abruptly.
        // It must surface as a fault so the application does not tell the server of an ordinary end.
        await using var pair = await MuxPair.CreateAsync(blackholeGuest: true);
        pair.GuestRaw!.Dispose(); // cutting the socket abruptly: no GOAWAY and no orderly close

        var e = await Assert.ThrowsAsync<MuxClosedException>(() => pair.Host.Completion.WaitAsync(Timeout));
        // Whichever path got there first (the control loop, or the transport ending with no GOAWAY, or a read fault on the cut socket),
        // the result is a fault rather than a clean close. What matters is that Completion does not complete successfully.
        Assert.True(e.Message.Contains("closed by peer", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains("dead", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains("faulted", StringComparison.OrdinalIgnoreCase), e.Message);
        Assert.True(pair.Host.IsClosed);
    }

    [Fact]
    public async Task Liveness_DeclaresTunnelDead_WhenPongsStop()
    {
        // Liveness on the host alone: if the guest checked its liveness too it would close its transport first,
        // so the host would reach EOF before the PONG timeout matured and the test would become a race.
        var hostOptions = new MuxOptions { PingInterval = TimeSpan.FromMilliseconds(100), DeadAfter = TimeSpan.FromMilliseconds(600) };
        var guestOptions = new MuxOptions { EnableLiveness = false };
        await using var pair = await MuxPair.CreateAsync(guestOptions, hostOptions, blackholeGuest: true);
        await pair.Host.PingAsync(CancellationToken.None).WaitAsync(Timeout);

        pair.GuestRaw!.Blackhole();
        var e = await Assert.ThrowsAsync<MuxClosedException>(() => pair.Host.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("PONG", e.Message);
        Assert.True(pair.Host.IsClosed);
    }

    [Fact]
    public async Task Stats_CountTransportBytes_BothWays()
    {
        await using var pair = await MuxPair.CreateAsync();
        var target = new TestTarget(produce: 100_000);
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(target));
        var open = await pair.Guest.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Timeout);
        await using var stream = open.Stream!;
        await StreamIo.WriteAllAsync(stream, 50_000);
        await ((IHalfClosable)stream).CompleteWritingAsync(CancellationToken.None);
        Assert.Equal(100_000, await StreamIo.ReadToEndAsync(stream).WaitAsync(Timeout));

        // What the host observes (its receipt, the half-close, and its counters) settles shortly after our read completes.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
               && !(target.PeerHalfClosed && target.Received == 50_000
                    && pair.Host.Stats.BytesUp >= 100_000 && pair.Host.Stats.BytesDown >= 50_000))
        {
            await Task.Delay(20);
        }

        Assert.InRange(pair.Guest.Stats.BytesUp, 50_000, 60_000);
        Assert.InRange(pair.Guest.Stats.BytesDown, 100_000, 120_000);
        Assert.InRange(pair.Host.Stats.BytesUp, 100_000, 120_000);
        Assert.InRange(pair.Host.Stats.BytesDown, 50_000, 60_000);
        Assert.Equal(50_000, target.Received);
        Assert.True(target.PeerHalfClosed);
    }

    [Theory]
    [InlineData("example.com:443", true, "example.com", 443)]
    [InlineData("xn--mgbh0fb.example:8443", true, "xn--mgbh0fb.example", 8443)]
    [InlineData("example.com", false, "", 0)]
    [InlineData(":443", false, "", 0)]
    [InlineData("example.com:", false, "", 0)]
    [InlineData("example.com:0", false, "", 0)]
    [InlineData("example.com:65536", false, "", 0)]
    [InlineData("[::1]:443", false, "", 0)]
    [InlineData("", false, "", 0)]
    public void TryParseName_Table(string name, bool ok, string host, int port)
    {
        Assert.Equal(ok, NerdbankMux.TryParseName(name, out var h, out var p));
        if (ok)
        {
            Assert.Equal(host, h);
            Assert.Equal(port, p);
        }
    }

    [Fact]
    public async Task OpenStreamAsync_RejectsIpv6Literal_Locally()
    {
        await using var pair = await MuxPair.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => pair.Guest.OpenStreamAsync("::1", 443, CancellationToken.None));
    }

    // ---------- The window and the stream limit (docs/protocol.md section 5 after the week-5 amendment) ----------

    [Fact]
    public async Task Window_WithoutDerivation_IsTheContractDefault()
    {
        await using var pair = await MuxPair.CreateAsync();
        Assert.Equal(1024 * 1024, pair.Guest.Window.ReceiveWindow);
        Assert.Equal(256, pair.Guest.Window.MaxConcurrentStreams);
    }

    [Fact]
    public async Task Window_WithExplicitOverride_DropsTheStreamLimitWithIt()
    {
        var options = new MuxOptions { EnableLiveness = false, ReceiveWindow = 4 * 1024 * 1024 };
        await using var pair = await MuxPair.CreateAsync(options, options);
        Assert.Equal(4 * 1024 * 1024, pair.Guest.Window.ReceiveWindow);
        Assert.Equal(64, pair.Guest.Window.MaxConcurrentStreams);
        Assert.Equal(MuxWindow.MemoryBudget, pair.Guest.Window.WorstCaseBytes);
    }

    /// <summary>
    /// Reserving the stream's slot at the guest before offering: what exceeds the limit is answered immediately with limit and does not trouble the host,
    /// and the slot comes back when the stream is disposed. (The host's limit on the wire is enforced by StreamLimiter in Egress.)
    /// </summary>
    [Fact]
    public async Task Open_BeyondTheStreamLimit_IsRejectedLocally_AndRecoversAfterClose()
    {
        var guestOptions = new MuxOptions { EnableLiveness = false, MaxConcurrentStreams = 2 };
        await using var pair = await MuxPair.CreateAsync(guestOptions);
        var reachedHost = 0;
        pair.Host.OpenRequested = (_, _) =>
        {
            Interlocked.Increment(ref reachedHost);
            return Task.FromResult(MuxOpenDecision.Ok(new TestTarget()));
        };

        var first = await pair.Guest.OpenStreamAsync("a.test", 443, CancellationToken.None).WaitAsync(Timeout);
        var second = await pair.Guest.OpenStreamAsync("b.test", 443, CancellationToken.None).WaitAsync(Timeout);
        var third = await pair.Guest.OpenStreamAsync("c.test", 443, CancellationToken.None).WaitAsync(Timeout);

        Assert.True(first.IsOpen);
        Assert.True(second.IsOpen);
        Assert.False(third.IsOpen);
        Assert.Equal(OpenFailReason.Limit, third.Reason);
        Assert.Equal(2, Volatile.Read(ref reachedHost)); // the third never reached the host at all
        Assert.Equal(2, pair.Guest.Stats.OpenStreams);

        await first.Stream!.DisposeAsync();
        var afterClose = await pair.Guest.OpenStreamAsync("d.test", 443, CancellationToken.None).WaitAsync(Timeout);
        Assert.True(afterClose.IsOpen);

        await second.Stream!.DisposeAsync();
        await afterClose.Stream!.DisposeAsync();
    }

    [Fact]
    public async Task Open_RejectedByHost_DoesNotLeakTheReservation()
    {
        var guestOptions = new MuxOptions { EnableLiveness = false, MaxConcurrentStreams = 1 };
        await using var pair = await MuxPair.CreateAsync(guestOptions);
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Fail(OpenFailReason.NotAllowed));

        for (var i = 0; i < 5; i++)
        {
            var open = await pair.Guest.OpenStreamAsync("blocked.test", 443, CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal(OpenFailReason.NotAllowed, open.Reason); // no limit: the reservation comes back every time
        }
        Assert.Equal(0, pair.Guest.Stats.OpenStreams);
    }

    /// <summary>
    /// The basis that lets each side derive its window from its own measurement: the receiving window in Nerdbank protocol 3 is the
    /// <b>receiver's</b> property and is announced in the offer/accept frame, so the backpressure in each direction follows its receiver's window rather than its sender's.
    /// Here the two bands are deliberately different: the guest 1 MiB and the host 4 MiB.
    /// </summary>
    [Fact]
    public async Task Windows_AreAdvertisedPerReceiver_SoTheTwoSidesMayDiffer()
    {
        const int guestWindow = 1024 * 1024;
        const int hostWindow = 4 * 1024 * 1024;
        await using var pair = await MuxPair.CreateAsync(
            new MuxOptions { EnableLiveness = false, ReceiveWindow = guestWindow },
            new MuxOptions { EnableLiveness = false, ReceiveWindow = hostWindow });

        // The seeded channel (PING/PONG/GOAWAY) works in both directions despite the different bands.
        await pair.Guest.PingAsync(CancellationToken.None).WaitAsync(Timeout);
        await pair.Host.PingAsync(CancellationToken.None).WaitAsync(Timeout);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new TestTarget(produce: 32 * 1024 * 1024, gate: gate); // a stalled consumer + a producer that does not stop
        pair.Host.OpenRequested = (_, _) => Task.FromResult(MuxOpenDecision.Ok(target));
        var open = await pair.Guest.OpenStreamAsync("both.test", 443, CancellationToken.None).WaitAsync(Timeout);
        var stream = open.Stream!;

        long written = 0;
        var writer = Task.Run(async () =>
        {
            var chunk = new byte[64 * 1024];
            for (var i = 0; i < 32 * 16; i++)
            {
                await stream.WriteAsync(chunk);
                Interlocked.Add(ref written, chunk.Length);
            }
        });

        // Both directions stop at their receiver's window; we wait for both numbers to settle (and we read nothing ourselves).
        var guestToHost = await Plateau(() => Volatile.Read(ref written));
        var hostToGuest = await Plateau(() => target.Produced);

        // Guest -> Host is governed by the host's window (4 MiB), not the guest's (1 MiB).
        Assert.InRange(guestToHost, 3 * 1024L * 1024, 8 * 1024L * 1024);
        // Host -> Guest is governed by the guest's window (1 MiB), not the host's.
        Assert.InRange(hostToGuest, 1024L * 1024, 3 * 1024L * 1024);
        Assert.True(guestToHost > hostToGuest, $"{guestToHost} vs {hostToGuest}");

        gate.SetResult();
        await stream.DisposeAsync();
        await Task.WhenAny(writer, Task.Delay(Timeout));
    }

    /// <summary>It waits for a counter to stop growing (the backpressure point) and then returns its value.</summary>
    private static async Task<long> Plateau(Func<long> counter)
    {
        var last = counter();
        var stable = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            var now = counter();
            stable = now == last ? stable + 1 : 0;
            last = now;
            if (stable >= 4) return now; // 400 ms with no movement = a full stop
        }
        return last;
    }

    [Fact]
    public void Wire_Names_MatchProtocol()
    {
        Assert.Equal("not_allowed", MuxWire.ToWire(OpenFailReason.NotAllowed));
        Assert.Equal("private_ip", MuxWire.ToWire(OpenFailReason.PrivateIp));
        Assert.Equal("port_not_allowed", MuxWire.ToWire(OpenFailReason.PortNotAllowed));
        Assert.Equal("dns_failed", MuxWire.ToWire(OpenFailReason.DnsFailed));
        Assert.Equal("connect_failed", MuxWire.ToWire(OpenFailReason.ConnectFailed));
        Assert.Equal("limit", MuxWire.ToWire(OpenFailReason.Limit));
        Assert.Equal("ip_literal", MuxWire.ToWire(OpenFailReason.IpLiteral));
        Assert.Equal("session_end", MuxWire.ToWire(GoAwayReason.SessionEnd));
        Assert.Equal("protocol_error", MuxWire.ToWire(GoAwayReason.ProtocolError));
        Assert.Equal("expired", MuxWire.ToWire(GoAwayReason.Expired));
        Assert.Equal(7, (int)OpenFailReason.IpLiteral);
        Assert.Equal(TunnelRole.Guest, TunnelRole.Guest);
    }
}
