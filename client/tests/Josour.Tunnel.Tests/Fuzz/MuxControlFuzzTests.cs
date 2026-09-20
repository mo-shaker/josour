using Josour.Core.Tunnel;
using Josour.Tunnel.Mux;
using Josour.Tunnel.Tests.Mux;
using Xunit.Abstractions;

namespace Josour.Tunnel.Tests.Fuzz;

/// <summary>
/// Fuzzing <b>what we own inside Nerdbank's frames</b>: the seeded control channel (PING/PONG/GOAWAY in nine bytes),
/// the status-byte protocol at open time, and the channel name <c>host:port</c>. The hostile peer here is
/// <see cref="RawMuxPeer"/>: it speaks Nerdbank protocol 3 perfectly and lies on top of it.
/// </summary>
public class MuxControlFuzzTests
{
    private const byte CtlPing = 1;
    private const byte CtlPong = 2;
    private const byte CtlGoAway = 3;
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public MuxControlFuzzTests(ITestOutputHelper output) => _output = output;

    // ---------- Unknown frame types ----------

    /// <summary>
    /// A frame type outside {1,2,3}: the contract says <c>GOAWAY(protocol_error)</c> then a close. And more importantly, the close must surface
    /// as <b>a fault</b>: a clean close does not raise <c>Died</c> in <c>TunnelSession</c>, so a hostile peer used to end the session
    /// with no reason and the server was told of an ordinary end.
    /// </summary>
    /// <remarks>
    /// The cases run in parallel rather than in sequence: each one pays half a second of GOAWAY draining (correct behaviour — we wait for the frame to arrive before
    /// dropping the transport), and running them in sequence makes seven cases three and a half seconds in the default suite for nothing.
    /// </remarks>
    [Fact]
    public async Task UnknownControlFrameType_SendsGoAwayProtocolError_AndFaultsCompletion()
    {
        await Task.WhenAll(new byte[] { 0, 4, 0x7f, 0xff }.Select(async type =>
        {
            await using var peer = await RawMuxPeer.CreateAsync();
            await peer.WriteControlFrameAsync(type, new byte[8]);

            var verdict = await peer.SettleAsync(Settle);
            Assert.True(verdict.Completed, $"type 0x{type:x2}: {verdict.Describe()}");
            Assert.True(verdict.Faulted, $"an unknown control frame (0x{type:x2}) closed the tunnel cleanly: {verdict.Describe()}");
            Assert.IsType<MuxClosedException>(verdict.Error);
            Assert.Equal(GoAwayReason.ProtocolError, peer.Victim.RemoteGoAway);
            Assert.True(await peer.WaitForGoAwayAsync(Short), $"type 0x{type:x2}: no GOAWAY reached the peer");
            Assert.Equal((byte)GoAwayReason.ProtocolError, peer.LastGoAwayReason);
        }));
    }

    /// <summary>A GOAWAY reason outside the contract (1..3): treated as a protocol error rather than a clean close with an unknown reason.</summary>
    [Fact]
    public async Task GoAwayWithUnknownReason_IsAProtocolError()
    {
        await Task.WhenAll(new byte[] { 0, 4, 0xff }.Select(async reason =>
        {
            await using var peer = await RawMuxPeer.CreateAsync();
            var payload = new byte[8];
            payload[0] = reason;
            await peer.WriteControlFrameAsync(CtlGoAway, payload);

            var verdict = await peer.SettleAsync(Settle);
            Assert.True(verdict.Completed, $"reason {reason}: {verdict.Describe()}");
            Assert.True(verdict.Faulted, $"reason {reason}: {verdict.Describe()}");
            Assert.IsType<MuxClosedException>(verdict.Error);
            Assert.Equal(GoAwayReason.ProtocolError, peer.Victim.RemoteGoAway);
        }));
    }

    /// <summary><c>GOAWAY(protocol_error)</c> from the other side: an abnormal end the application must know about.</summary>
    [Fact]
    public async Task PeerGoAwayProtocolError_FaultsCompletion()
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        var payload = new byte[8];
        payload[0] = (byte)GoAwayReason.ProtocolError;
        await peer.WriteControlFrameAsync(CtlGoAway, payload);

        var verdict = await peer.SettleAsync(Settle);
        Assert.True(verdict.Faulted, verdict.Describe());
        Assert.IsType<MuxClosedException>(verdict.Error);
        Assert.Equal(GoAwayReason.ProtocolError, peer.Victim.RemoteGoAway);
    }

    /// <summary><c>GOAWAY(session_end)</c> and <c>(expired)</c> stay a clean close: a deliberate end, not a death.</summary>
    [Theory]
    [InlineData(GoAwayReason.SessionEnd)]
    [InlineData(GoAwayReason.Expired)]
    public async Task PeerGoAwayWithContractReason_StaysAClean_Close(GoAwayReason reason)
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        var payload = new byte[8];
        payload[0] = (byte)reason;
        await peer.WriteControlFrameAsync(CtlGoAway, payload);

        var verdict = await peer.SettleAsync(Settle);
        Assert.True(verdict.Completed, verdict.Describe());
        Assert.False(verdict.Faulted, verdict.Describe());
        Assert.Equal(reason, peer.Victim.RemoteGoAway);
    }

    // ---------- Truncated and split frames ----------

    /// <summary>1..8 bytes then silence: no hang, no mis-consumption, and the frame that completes after them is handled correctly.</summary>
    [Fact]
    public async Task PartialControlFrame_IsHeldUntilItCompletes()
    {
        await Task.WhenAll(Enumerable.Range(1, 8).Select(async prefix =>
        {
            await using var peer = await RawMuxPeer.CreateAsync();
            var frame = new byte[9];
            frame[0] = CtlPing;
            frame[1] = 0xAB;

            await peer.WriteControlAsync(frame.AsMemory(0, prefix));
            await Task.Delay(200);
            Assert.False(peer.Victim.IsClosed, $"a {prefix}-byte partial control frame closed the tunnel by itself");
            Assert.Equal(0, peer.PongsReceived);

            await peer.WriteControlAsync(frame.AsMemory(prefix));
            Assert.True(await peer.WaitForPongsAsync(1, Short), $"prefix {prefix}: the completed PING was never answered");
            Assert.False(peer.Victim.IsClosed);
        }));
    }

    /// <summary>Frames split across single-byte writes: no difference in the result, and this is what the real wire does under congestion.</summary>
    [Fact]
    public async Task ControlFrames_SplitAcrossSingleByteWrites_AreStillParsed()
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        const int pings = 20;
        var payload = new byte[pings * 9];
        for (var i = 0; i < pings; i++)
        {
            payload[i * 9] = CtlPing;
            payload[i * 9 + 1] = (byte)i;
        }
        for (var i = 0; i < payload.Length; i++) await peer.WriteControlAsync(payload.AsMemory(i, 1));

        Assert.True(await peer.WaitForPongsAsync(pings, TimeSpan.FromSeconds(20)), $"only {peer.PongsReceived} of {pings} PONGs came back");
        Assert.False(peer.Victim.IsClosed);
    }

    // ---------- Hostile payloads on a sound channel ----------

    /// <summary>A PONG with a nonce we did not request: it is dropped, adds nothing to the map, and does not kill the tunnel.</summary>
    [Fact]
    public async Task UnsolicitedPongs_AreIgnored_AndDoNotGrowThePendingMap()
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        peer.AnswerPings = true;
        for (var i = 0; i < 500; i++)
        {
            var payload = new byte[8];
            BitConverter.TryWriteBytes(payload, (ulong)i);
            await peer.WriteControlFrameAsync(CtlPong, payload);
        }
        await Task.Delay(300);

        Assert.False(peer.Victim.IsClosed, "unsolicited PONGs must not close the tunnel");
        Assert.Equal(0, peer.Victim.PendingPings);
        // The tunnel is still usable: a PING from us comes back with a PONG.
        var rtt = await peer.Victim.PingAsync(CancellationToken.None).WaitAsync(Short);
        Assert.InRange(rtt.TotalMilliseconds, 0, 10_000);
    }

    /// <summary>
    /// A PING flood: it must all be answered with no unbounded memory growth. The backpressure here is real (the control channel's window is 4 MiB),
    /// so the purpose is to prove the answering is governed by the window rather than a queue growing in our memory.
    /// </summary>
    [Fact]
    public async Task PingFlood_IsAnswered_WithoutUnboundedMemory()
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        const int pings = 20_000;
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var payload = new byte[pings * 9];
        for (var i = 0; i < pings; i++)
        {
            payload[i * 9] = CtlPing;
            BitConverter.TryWriteBytes(payload.AsSpan(i * 9 + 1, 8), (ulong)i);
        }
        await peer.WriteControlAsync(payload);

        Assert.True(await peer.WaitForPongsAsync(pings, TimeSpan.FromSeconds(60)), $"only {peer.PongsReceived} of {pings} PONGs came back");
        var after = GC.GetTotalMemory(forceFullCollection: true);
        _output.WriteLine($"ping flood: {pings} PINGs answered; managed heap {before / 1024} KiB → {after / 1024} KiB");

        Assert.False(peer.Victim.IsClosed);
        Assert.Equal(0, peer.Victim.PendingPings);
        Assert.True(after - before < 64L * 1024 * 1024, $"the ping flood grew the heap by {(after - before) / 1024 / 1024} MiB");
    }

    /// <summary>Random bytes on the control channel: always a defined final state, and never an exception of another type.</summary>
    [Fact]
    public Task RandomControlBytes_AlwaysEndInADefinedState() => RandomControlBytesAsync(FuzzSeed.Cases(32));

    [Trait("Category", "Benchmark")]
    [Fact]
    public Task Deep_RandomControlBytes() => RandomControlBytesAsync(FuzzSeed.Cases(600));

    /// <summary>
    /// The cases are entirely independent so they run in parallel batches: each one pays half a second of GOAWAY draining (the correct behaviour:
    /// we wait for the frame to arrive before dropping the transport), and running them in sequence makes it minutes for nothing.
    /// </summary>
    private async Task RandomControlBytesAsync(int cases)
    {
        const int lanes = 8;
        using var watch = new UnobservedExceptionWatch();
        var faulted = 0;
        var alive = 0;

        for (var start = 0; start < cases; start += lanes)
        {
            var batch = Enumerable.Range(start, Math.Min(lanes, cases - start)).Select(async i =>
            {
                var rng = FuzzSeed.For("control", i);
                await using var peer = await RawMuxPeer.CreateAsync();
                await peer.WriteControlAsync(Mutate.Random(rng, rng.Next(1, 512)));

                // Three seconds rather than ten: the terminating path settles within half a second (the GOAWAY drain), while a payload that keeps
                // the tunnel alive (under nine bytes, or PING/PONG frames valid by chance) waits out the whole timeout —
                // and at ten seconds one case in sixty would have added ten seconds to the default suite.
                var verdict = await peer.SettleAsync(TimeSpan.FromSeconds(3));
                if (!verdict.Completed)
                {
                    // Under nine bytes, or bytes that are all valid PING/PONG: the tunnel is alive and that is acceptable.
                    Assert.False(peer.Victim.IsClosed, $"case {i}: mux is closed but Completion never settled");
                    Interlocked.Increment(ref alive);
                    return;
                }
                Assert.True(verdict.Faulted, $"case {i}: random control bytes produced a clean close: {verdict.Describe()}");
                Assert.IsType<MuxClosedException>(verdict.Error);
                Interlocked.Increment(ref faulted);
            });
            await Task.WhenAll(batch);
        }

        var unobserved = watch.Drain();
        var ours = unobserved.Where(UnobservedExceptionWatch.IsOurs).ToList();
        _output.WriteLine($"random control bytes: {cases} cases, {faulted} faulted, {alive} still alive; unobserved: {ours.Count} ours, {unobserved.Count - ours.Count} from Nerdbank.Streams");
        // What we make must be zero; the noise from Nerdbank's internal tasks is documented and we cannot fix it.
        Assert.True(ours.Count == 0, UnobservedExceptionWatch.Describe(ours));
    }

    // ---------- The open protocol: the name and the status byte ----------

    /// <summary>
    /// Hostile channel names: the contract says <c>host:port</c> with no IPv6 literal. Everything that does not match must come back as
    /// <c>OPEN_FAIL(not_allowed)</c>, rather than reaching the egress policy or hanging the offerer.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(":")]
    [InlineData(":443")]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:-1")]
    [InlineData("host:+443")]
    [InlineData("host: 443")]
    [InlineData("host:443 ")]
    [InlineData("a:b:443")]
    [InlineData("[::1]:443")]
    [InlineData("host:99999999999999999999")]
    [InlineData("host:4e2")]
    [InlineData("host:0x1bb")]
    public async Task AdversarialChannelNames_AreRejectedBeforeThePolicy(string name)
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        var reached = 0;
        peer.Victim.OpenRequested = (_, _) =>
        {
            Interlocked.Increment(ref reached);
            return Task.FromResult(MuxOpenDecision.Ok(new TestTarget()));
        };

        var status = await peer.OfferAndReadStatusAsync(name, Short);

        Assert.Equal((byte)OpenFailReason.NotAllowed, status);
        Assert.Equal(0, reached);
        Assert.False(peer.Victim.IsClosed);
    }

    /// <summary>
    /// Huge channel names: the 300-character limit in <c>TryParseName</c> stops an unbounded name being passed to the egress policy or to
    /// the domain set. Names larger than a Nerdbank frame never leave the hostile peer at all (<c>null</c> here),
    /// which is a free extra bound but not our bound: what is required is that none of this reaches past the parser.
    /// </summary>
    [Theory]
    [InlineData(301)]
    [InlineData(4096)]
    [InlineData(64 * 1024)]
    public async Task HugeChannelName_NeverReachesThePolicy(int length)
    {
        await using var peer = await RawMuxPeer.CreateAsync();
        var reached = 0;
        peer.Victim.OpenRequested = (_, _) => { Interlocked.Increment(ref reached); return Task.FromResult(MuxOpenDecision.Fail(OpenFailReason.NotAllowed)); };

        var status = await peer.OfferAndReadStatusAsync(new string('x', length) + ":443", TimeSpan.FromSeconds(20));

        _output.WriteLine($"name of {length} chars → status {(status is null ? "none (the peer could not even offer it)" : status.ToString())}");
        Assert.True(status is null or (byte)OpenFailReason.NotAllowed, $"a {length}-char name produced status {status}");
        Assert.Equal(0, reached);
    }

    /// <summary>
    /// A flood of offers past the band's limit: the refusal is <c>OPEN_FAIL(limit)</c>, the counter returns to zero, and memory stays bounded.
    /// With no limit on the <b>acceptance</b> path, the other side alone can decide how many channels we create.
    /// </summary>
    [Fact]
    public async Task OfferFloodBeyondTheStreamLimit_IsRejectedAsLimit()
    {
        const int limit = 8;
        await using var peer = await RawMuxPeer.CreateAsync(
            victimOptions: new MuxOptions { EnableLiveness = false, ReceiveWindow = MuxWindow.NearWindow, MaxConcurrentStreams = limit });

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Victim.OpenRequested = async (_, ct) => { await hold.Task.WaitAsync(ct); return MuxOpenDecision.Fail(OpenFailReason.NotAllowed); };

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var offers = Enumerable.Range(0, 64).Select(i => peer.OfferAndReadStatusAsync($"h{i}.test:443", TimeSpan.FromSeconds(20))).ToArray();
        // The gate opens while the flood is still running: the accepted ones are held until the offers exceed the limit, and then they are released.
        var release = Task.Run(async () => { await Task.Delay(1500); hold.TrySetResult(); });
        var statuses = await Task.WhenAll(offers);
        await release;
        var after = GC.GetTotalMemory(forceFullCollection: true);

        var limited = statuses.Count(s => s == (byte)OpenFailReason.Limit);
        _output.WriteLine($"offer flood: {limited} of 64 offers rejected as limit (concurrent cap {limit}); heap {before / 1024} KiB → {after / 1024} KiB");
        Assert.True(limited >= 64 - limit, $"only {limited} of 64 offers were capped; the accept path has no limit of its own");
        Assert.True(after - before < 64L * 1024 * 1024, $"the offer flood grew the heap by {(after - before) / 1024 / 1024} MiB");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (peer.Victim.Stats.OpenStreams > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(0, peer.Victim.Stats.OpenStreams);
    }

    /// <summary>A status byte outside 0..7 from a hostile host: it is normalised to a reason from the contract, and no open stream appears.</summary>
    [Theory]
    [InlineData((byte)8)]
    [InlineData((byte)9)]
    [InlineData((byte)0x7f)]
    [InlineData((byte)0xff)]
    public async Task UnknownOpenStatusByte_IsNormalisedToAContractReason(byte status)
    {
        await using var peer = await RawMuxPeer.CreateAsync(victimRole: TunnelRole.Guest);
        peer.AcceptOffersWith(_ => status);

        var open = await peer.Victim.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Short);

        Assert.False(open.IsOpen);
        Assert.NotNull(open.Reason);
        Assert.True(MuxWire.IsValid(open.Reason!.Value), $"status 0x{status:x2} produced reason {(int)open.Reason!.Value}, which is not in the contract");
        Assert.Equal(0, peer.Victim.Stats.OpenStreams);
    }

    /// <summary>A hostile host that accepts the channel and then closes it with no status byte: a defined failure, with no hang and no leaked reservation.</summary>
    [Fact]
    public async Task ChannelClosedBeforeTheStatusByte_FailsCleanly()
    {
        await using var peer = await RawMuxPeer.CreateAsync(victimRole: TunnelRole.Guest);
        peer.AcceptOffersWith(_ => null);

        var open = await peer.Victim.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Short);

        Assert.False(open.IsOpen);
        Assert.Equal(OpenFailReason.ConnectFailed, open.Reason);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (peer.Victim.Stats.OpenStreams > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(0, peer.Victim.Stats.OpenStreams);
    }

    /// <summary>Fuzzing the status byte: any value from 0 to 255 ends either as an open stream or as a reason from the contract.</summary>
    [Fact]
    public async Task StatusByteFuzz_CoversTheWholeByteRange()
    {
        var opened = 0;
        var failed = 0;
        for (var value = 0; value < 256; value++)
        {
            await using var peer = await RawMuxPeer.CreateAsync(victimRole: TunnelRole.Guest);
            peer.AcceptOffersWith(_ => (byte)value);
            var open = await peer.Victim.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Short);
            if (open.IsOpen)
            {
                Assert.Equal(0, value); // 0 = OPEN_OK, alone
                opened++;
                await open.Stream!.DisposeAsync();
            }
            else
            {
                Assert.True(MuxWire.IsValid(open.Reason!.Value), $"status {value} produced reason {(int)open.Reason!.Value}");
                failed++;
            }
        }
        _output.WriteLine($"status byte fuzz: {opened} opened, {failed} rejected with a contract reason");
        Assert.Equal(1, opened);
        Assert.Equal(255, failed);
    }

    // ---------- A GOAWAY mid-traffic ----------

    /// <summary>A GOAWAY arriving while data is moving on a stream: a defined close, and the stream gives an IO error rather than a surprising type.</summary>
    [Fact]
    public async Task GoAwayMidStream_ClosesEverythingWithAKnownExceptionType()
    {
        await using var peer = await RawMuxPeer.CreateAsync(victimRole: TunnelRole.Guest);
        peer.AcceptOffersWith(_ => (byte)0);

        var open = await peer.Victim.OpenStreamAsync("example.com", 443, CancellationToken.None).WaitAsync(Short);
        Assert.True(open.IsOpen);
        var stream = open.Stream!;

        var writer = Task.Run(async () =>
        {
            var chunk = new byte[16 * 1024];
            try
            {
                for (var i = 0; i < 4096; i++) await stream.WriteAsync(chunk);
                return (Exception?)null;
            }
            catch (Exception e) { return e; }
        });

        await Task.Delay(100);
        var payload = new byte[8];
        payload[0] = (byte)GoAwayReason.SessionEnd;
        await peer.WriteControlFrameAsync(CtlGoAway, payload);

        var verdict = await peer.SettleAsync(Settle);
        Assert.True(verdict.Completed, verdict.Describe());
        Assert.Equal(GoAwayReason.SessionEnd, peer.Victim.RemoteGoAway);

        var error = await writer.WaitAsync(Settle);
        // The writer either finished what it had before the close or saw an IOException/ObjectDisposedException; no type outside those two.
        Assert.True(error is null or IOException or ObjectDisposedException or OperationCanceledException,
            $"writing during GOAWAY threw {error?.GetType().FullName}: {error?.Message}");
        await stream.DisposeAsync();
    }
}
