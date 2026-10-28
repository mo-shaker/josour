using Josour.Core.Net;
using Josour.Infrastructure.SystemProxy;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The crash-safe sequence of ADR-0015 point 2: the marker is on disk before anything changes and leaves only after the
/// settings are back, every failure puts back what was changed, and a restore never runs twice.
/// </summary>
public sealed class SystemProxyGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "josour-proxy-guard-" + Guid.NewGuid().ToString("N"));
    private readonly FakeController _controller = new();
    private readonly SystemProxyMarkerStore _markers;
    private readonly RecordingSafetyNet _net = new();

    public SystemProxyGuardTests()
    {
        _markers = new SystemProxyMarkerStore(Path.Combine(_dir, "system-proxy.json"));
        _controller.Markers = _markers;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SystemProxyGuard Guard() => new(_controller, _markers, _net);

    [Fact]
    public async Task Apply_WritesTheMarkerBeforeChangingAnything_AndArmsTheSafetyNet()
    {
        var guard = Guard();

        await guard.ApplyAsync(50123, Array.Empty<string>(), CancellationToken.None);

        Assert.True(_controller.MarkerWasOnDiskAtApply);
        Assert.Equal(new[] { "capture", "apply", "read-back" }, _controller.Calls);
        var marker = _markers.Read()!;
        Assert.Equal(50123, marker.Port);
        Assert.Equal(Environment.ProcessId, marker.ProcessId);
        Assert.Equal("before", marker.Snapshot.Data);
        Assert.True(guard.IsApplied);
        Assert.Equal(1, _net.Armed);
        Assert.True(OwnProxyPorts.Contains(50123));
        await guard.RestoreAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Apply_WhenTheMarkerCannotBeWritten_ChangesNothing()
    {
        var guard = new SystemProxyGuard(_controller, new UnwritableMarkers(), _net);

        var ex = await Assert.ThrowsAsync<SystemProxyException>(() => guard.ApplyAsync(50124, Array.Empty<string>(), CancellationToken.None));

        Assert.Equal("marker_failed", ex.Reason);
        Assert.DoesNotContain("apply", _controller.Calls);
        Assert.Equal(0, _net.Armed);
    }

    [Fact]
    public async Task Apply_ThatFails_PutsBackWhatChanged_AndLeavesNoMarker()
    {
        _controller.FailApply = new SystemProxyException("apply_failed", "half applied");
        var guard = Guard();

        var ex = await Assert.ThrowsAsync<SystemProxyException>(() => guard.ApplyAsync(50125, Array.Empty<string>(), CancellationToken.None));

        Assert.Equal("apply_failed", ex.Reason);
        Assert.Equal(new[] { "capture", "apply", "restore:50125" }, _controller.Calls);
        Assert.Null(_markers.Read());
        Assert.False(guard.IsApplied);
        Assert.Equal(1, _net.Disarmed);
        Assert.False(OwnProxyPorts.Contains(50125));
    }

    [Fact]
    public async Task Apply_ThatDoesNotReadBack_IsNotApplied_AndIsPutBack()
    {
        _controller.ReadsBack = false;
        var guard = Guard();

        var ex = await Assert.ThrowsAsync<SystemProxyException>(() => guard.ApplyAsync(50126, Array.Empty<string>(), CancellationToken.None));

        Assert.Equal("not_applied", ex.Reason);
        Assert.Contains("restore:50126", _controller.Calls);
        Assert.Null(_markers.Read());
    }

    [Fact]
    public async Task Apply_Cancelled_IsACancellation_NotAFailure_AndIsPutBack()
    {
        using var cts = new CancellationTokenSource();
        _controller.OnApply = () => cts.Cancel();
        var guard = Guard();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.ApplyAsync(50127, Array.Empty<string>(), cts.Token));

        Assert.Contains("restore:50127", _controller.Calls);
        Assert.Null(_markers.Read());
    }

    [Fact]
    public async Task Apply_WithAMarkerLeftBehind_RestoresThatFirst_SoTheSnapshotIsTheUsersOwn()
    {
        _markers.Write(new SystemProxyRunMarker(4242, DateTimeOffset.UtcNow, 40000, new SystemProxySnapshot("fake", "user's own"), DateTimeOffset.UtcNow));
        var guard = Guard();

        await guard.ApplyAsync(50128, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(new[] { "restore:40000", "capture", "apply", "read-back" }, _controller.Calls);
        Assert.Equal("user's own", _controller.LastRestored!.Data);
        Assert.Equal(50128, _markers.Read()!.Port);
        await guard.RestoreAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Restore_PutsBack_RemovesTheMarker_Disarms_AndRunsOnce()
    {
        var guard = Guard();
        await guard.ApplyAsync(50129, Array.Empty<string>(), CancellationToken.None);

        Assert.True(await guard.RestoreAsync(CancellationToken.None));
        Assert.False(await guard.RestoreAsync(CancellationToken.None));

        Assert.Single(_controller.Calls, c => c == "restore:50129");
        Assert.Null(_markers.Read());
        Assert.Equal(1, _net.Disarmed);
        Assert.False(OwnProxyPorts.Contains(50129));
    }

    [Fact]
    public async Task Restore_ThatFails_KeepsTheMarker_ForTheNextAttempt()
    {
        var guard = Guard();
        await guard.ApplyAsync(50130, Array.Empty<string>(), CancellationToken.None);
        _controller.FailRestore = true;

        Assert.False(await guard.RestoreAsync(CancellationToken.None));
        Assert.NotNull(_markers.Read());
        Assert.True(guard.IsApplied);

        _controller.FailRestore = false;
        Assert.True(await guard.RestoreAsync(CancellationToken.None));
        Assert.Null(_markers.Read());
    }

    [Fact]
    public async Task Unsupported_RefusesToApply()
    {
        var guard = new SystemProxyGuard(UnsupportedSystemProxyController.Instance, _markers);
        Assert.False(guard.IsSupported);
        var ex = await Assert.ThrowsAsync<SystemProxyException>(() => guard.ApplyAsync(50131, Array.Empty<string>(), CancellationToken.None));
        Assert.Equal("unsupported", ex.Reason);
        Assert.Null(_markers.Read());
    }

    [Fact]
    public async Task Guardian_RestoresWhenTheAppIsGone_AndLeavesWhenTheMarkerIs()
    {
        var guard = Guard();

        // A marker whose process does not exist (a PID far beyond any in use): the app died with the change in force.
        _markers.Write(new SystemProxyRunMarker(int.MaxValue - 7, DateTimeOffset.UtcNow, 40001, new SystemProxySnapshot("fake", "x"), DateTimeOffset.UtcNow));
        Assert.Equal(0, await SystemProxyGuardian.RunAsync(int.MaxValue - 7, DateTimeOffset.UtcNow, guard, _markers));
        Assert.Contains("restore:40001", _controller.Calls);
        Assert.Null(_markers.Read());

        // No marker at all (the app restored on its own): nothing to do.
        Assert.Equal(0, await SystemProxyGuardian.RunAsync(Environment.ProcessId, DateTimeOffset.UtcNow, guard, _markers));

        // A marker of another process: not this guardian's to act on, even though its own parent is gone.
        _markers.Write(new SystemProxyRunMarker(Environment.ProcessId, DateTimeOffset.UtcNow, 40002, new SystemProxySnapshot("fake", "y"), DateTimeOffset.UtcNow));
        Assert.Equal(0, await SystemProxyGuardian.RunAsync(int.MaxValue - 7, DateTimeOffset.UtcNow, guard, _markers));
        Assert.DoesNotContain("restore:40002", _controller.Calls);
    }

    [Fact]
    public void Guardian_TellsALivingProcessFromAReusedPid()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var started = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero);
        Assert.True(SystemProxyGuardian.IsAlive(Environment.ProcessId, started));
        Assert.False(SystemProxyGuardian.IsAlive(Environment.ProcessId, started.AddHours(-3)));
        Assert.False(SystemProxyGuardian.IsAlive(int.MaxValue - 7, started));
    }

    [Fact]
    public void MarkerStore_RoundTrips_AndLeavesNoTemporaryFile()
    {
        var marker = new SystemProxyRunMarker(12, DateTimeOffset.UnixEpoch, 50132, new SystemProxySnapshot("macos-networksetup", "[{\"service\":\"Wi-Fi\"}]"), DateTimeOffset.UnixEpoch);

        Assert.True(_markers.Write(marker));

        Assert.Equal(marker, _markers.Read());
        Assert.False(File.Exists(_markers.FilePath + ".tmp"));
        _markers.Clear();
        Assert.Null(_markers.Read());
    }

    private SystemProxyRunMarker Marker(int pid, int port, DateTimeOffset? written = null, DateTimeOffset? started = null)
        => new(pid, started ?? DateTimeOffset.UtcNow, port, new SystemProxySnapshot("fake", "m" + port), written ?? DateTimeOffset.UtcNow);

    [Fact]
    public void MarkerStore_ACorruptMarker_IsMovedAside_RatherThanBlockingEverySession()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_markers.FilePath, string.Empty); // what a power cut can leave without the flush

        Assert.Null(_markers.Read());
        Assert.False(_markers.Exists);
        Assert.True(File.Exists(_markers.CorruptFilePath));
    }

    [Fact]
    public async Task AMarkerThatIsThereButUnreadable_StillCountsAsApplied_AndIsNotDropped()
    {
        var unreadable = new UnreadableMarkers();
        var guard = new SystemProxyGuard(_controller, unreadable, _net);

        Assert.True(guard.IsApplied);
        Assert.False(await guard.RestoreAsync(CancellationToken.None));
        Assert.Equal(0, unreadable.Cleared);
        var ex = await Assert.ThrowsAsync<SystemProxyException>(() => guard.ApplyAsync(50140, Array.Empty<string>(), CancellationToken.None));
        Assert.Equal("restore_failed", ex.Reason);
        Assert.DoesNotContain("apply", _controller.Calls);
    }

    [Fact]
    public void MarkerStore_ClearIf_LeavesAnotherSessionsMarker()
    {
        var mine = Marker(100, 50141);
        var theirs = Marker(200, 50142);
        _markers.Write(theirs);

        Assert.False(_markers.ClearIf(mine));
        Assert.Equal(theirs, _markers.Read());
        Assert.True(_markers.ClearIf(theirs));
        Assert.False(_markers.Exists);
    }

    [Fact]
    public async Task RestoreIf_AMarkerThatWasReplaced_IsNotRestored()
    {
        var guard = Guard();
        var seen = Marker(int.MaxValue - 7, 50143);
        _markers.Write(Marker(4343, 50144)); // another process's session since

        Assert.False(await guard.RestoreIfAsync(seen, CancellationToken.None));

        Assert.DoesNotContain(_controller.Calls, c => c.StartsWith("restore", StringComparison.Ordinal));
        Assert.Equal(50144, _markers.Read()!.Port);
    }

    [Fact]
    public async Task Guardian_LeavesWhenItsSessionsMarkerIsReplacedByTheNextSession()
    {
        var guard = Guard();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var started = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero);
        _markers.Write(Marker(Environment.ProcessId, 50145, DateTimeOffset.UtcNow.AddMinutes(-1), started));

        var guardian = SystemProxyGuardian.RunAsync(Environment.ProcessId, started, guard, _markers);
        await Task.Delay(300);
        Assert.False(guardian.IsCompleted); // its process lives and its marker is there: it keeps watching

        _markers.Write(Marker(Environment.ProcessId, 50146, DateTimeOffset.UtcNow, started)); // the same app's next session
        Assert.Equal(0, await guardian.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(_controller.Calls, c => c.StartsWith("restore", StringComparison.Ordinal));
        Assert.Equal(50146, _markers.Read()!.Port);
    }

    [Fact]
    public async Task MarkerStore_TheLockIsHeldAgainstASecondHolder_UntilReleased()
    {
        var other = new SystemProxyMarkerStore(_markers.FilePath);
        var first = await _markers.LockAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);

        Assert.Null(await other.LockAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None));

        first!.Dispose();
        using var second = await other.LockAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(second);
    }

    [UnixFact]
    public void MarkerStore_WritesAFileOnlyItsOwnerCanRead()
    {
        Assert.True(_markers.Write(Marker(1, 50147)));
        if (OperatingSystem.IsWindows()) return; // [UnixFact] skips it there; this tells the analyzer too
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_markers.FilePath));
    }

    private sealed class UnreadableMarkers : ISystemProxyMarkerStore
    {
        public int Cleared { get; private set; }
        public bool Exists => true;
        public SystemProxyRunMarker? Read() => null;
        public bool Write(SystemProxyRunMarker marker) => true;
        public void Clear() => Cleared++;
    }

    private sealed class FakeController : ISystemProxyController
    {
        public List<string> Calls { get; } = new();
        public SystemProxyMarkerStore? Markers { get; set; }
        public bool MarkerWasOnDiskAtApply { get; private set; }
        public SystemProxyException? FailApply { get; set; }
        public bool FailRestore { get; set; }
        public bool ReadsBack { get; set; } = true;
        public Action? OnApply { get; set; }
        public SystemProxySnapshot? LastRestored { get; private set; }

        public bool IsSupported => true;

        public Task<SystemProxySnapshot> CaptureAsync(CancellationToken ct)
        {
            Calls.Add("capture");
            return Task.FromResult(new SystemProxySnapshot("fake", "before"));
        }

        public Task ApplyAsync(SystemProxyTarget target, CancellationToken ct)
        {
            Calls.Add("apply");
            MarkerWasOnDiskAtApply = Markers?.Read() is not null;
            OnApply?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (FailApply is not null) throw FailApply;
            return Task.CompletedTask;
        }

        public Task<bool> IsAppliedAsync(SystemProxyTarget target, CancellationToken ct)
        {
            Calls.Add("read-back");
            return Task.FromResult(ReadsBack);
        }

        public Task RestoreAsync(SystemProxySnapshot snapshot, int port, CancellationToken ct)
        {
            if (FailRestore) throw new SystemProxyException("restore_failed", "test");
            Calls.Add("restore:" + port);
            LastRestored = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class UnwritableMarkers : ISystemProxyMarkerStore
    {
        public SystemProxyRunMarker? Read() => null;
        public bool Write(SystemProxyRunMarker marker) => false;
        public void Clear() { }
    }

    private sealed class RecordingSafetyNet : ISystemProxySafetyNet
    {
        public int Armed { get; private set; }
        public int Disarmed { get; private set; }
        public void Arm(SystemProxyRunMarker marker) => Armed++;
        public void Disarm() => Disarmed++;
    }
}
