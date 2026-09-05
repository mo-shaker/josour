using RouteBridge.Infrastructure.Session;
using RouteBridge.Infrastructure.Tests.Support;

namespace RouteBridge.Infrastructure.Tests;

/// <summary>
/// Plan 8.5 (انهيار التطبيق): a work browser that survived an unclean exit is still pointing at a proxy that died with
/// the process. The next start-up closes it before anything else happens — and, just as important, does not close a
/// process that merely inherited the same id.
/// </summary>
public sealed class StaleWorkBrowserGuardTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Started = new(2026, 9, 5, 9, 30, 0, TimeSpan.Zero);

    private static StaleWorkBrowserGuard Create(InMemoryWorkBrowserRunMarkerStore markers, FakeProcessController processes) =>
        new(markers, processes, logger: null, graceful: TimeSpan.FromMilliseconds(50));

    private static WorkBrowserRunMarker Marker(string profileDirectory = "", int pid = 4242, string name = "chrome") =>
        new(pid, name, Started, profileDirectory, Started);

    [Fact]
    public async Task NoMarker_MeansTheLastRunCleanedUpAfterItself()
    {
        var markers = new InMemoryWorkBrowserRunMarkerStore();
        var processes = new FakeProcessController();

        var result = await Create(markers, processes).CleanupAsync(None);

        Assert.False(result.MarkerFound);
        Assert.False(result.ProcessFound);
        Assert.Empty(processes.Closed);
        Assert.Equal(0, markers.Clears);
    }

    [Fact]
    public async Task ABrowserLeftRunning_IsClosedAndTheMarkerRemoved()
    {
        var markers = new InMemoryWorkBrowserRunMarkerStore { Marker = Marker() };
        var processes = new FakeProcessController();
        processes.Add(4242, "chrome", Started);

        var result = await Create(markers, processes).CleanupAsync(None);

        Assert.True(result.MarkerFound);
        Assert.True(result.ProcessFound);
        Assert.True(result.Closed);
        Assert.Equal(4242, Assert.Single(processes.Closed));
        Assert.False(processes.IsRunning(4242));
        Assert.Null(markers.Marker);
    }

    [Fact]
    public async Task AProcessThatIsAlreadyGone_IsJustForgotten()
    {
        var markers = new InMemoryWorkBrowserRunMarkerStore { Marker = Marker() };
        var processes = new FakeProcessController(); // the Job Object did its job when the app died

        var result = await Create(markers, processes).CleanupAsync(None);

        Assert.True(result.MarkerFound);
        Assert.False(result.ProcessFound);
        Assert.Empty(processes.Closed);
        Assert.Null(markers.Marker);
    }

    [Fact]
    public async Task AReusedProcessId_IsNotKilled()
    {
        // Same id, different program: on a machine that has been up for days this is the normal case.
        var markers = new InMemoryWorkBrowserRunMarkerStore { Marker = Marker() };
        var processes = new FakeProcessController();
        processes.Add(4242, "explorer", Started);

        var result = await Create(markers, processes).CleanupAsync(None);

        Assert.False(result.ProcessFound);
        Assert.Empty(processes.Closed);
        Assert.True(processes.IsRunning(4242));
        Assert.Null(markers.Marker);
    }

    [Fact]
    public async Task AProcessWithTheSameNameButADifferentStart_IsNotKilled()
    {
        // The user's own Chrome, started after the crash, taking the recycled id.
        var markers = new InMemoryWorkBrowserRunMarkerStore { Marker = Marker() };
        var processes = new FakeProcessController();
        processes.Add(4242, "chrome", Started.AddMinutes(20));

        var result = await Create(markers, processes).CleanupAsync(None);

        Assert.False(result.ProcessFound);
        Assert.Empty(processes.Closed);
        Assert.True(processes.IsRunning(4242));
    }

    [Fact]
    public async Task AProcessThatRefusesToDie_IsReportedButTheMarkerStillGoes()
    {
        var markers = new InMemoryWorkBrowserRunMarkerStore { Marker = Marker() };
        var processes = new FakeProcessController { CloseSucceeds = false };
        processes.Add(4242, "chrome", Started);

        var result = await Create(markers, processes).CleanupAsync(None);

        Assert.True(result.ProcessFound);
        Assert.False(result.Closed);
        Assert.Null(markers.Marker); // a marker we cannot act on must not make every later start-up try again
    }

    [Fact]
    public async Task StaleChromiumSingletonFiles_AreRemovedFromTheWorkProfile()
    {
        // Left behind by a crash, they make the next launch hand itself off to a process that no longer exists.
        var profile = Directory.CreateTempSubdirectory("routebridge-profile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(profile, "SingletonLock"), "PC-1234");
            File.WriteAllText(Path.Combine(profile, "SingletonCookie"), "17");
            File.WriteAllText(Path.Combine(profile, "Preferences"), "{}");

            var markers = new InMemoryWorkBrowserRunMarkerStore { Marker = Marker(profile) };
            var processes = new FakeProcessController();

            var result = await Create(markers, processes).CleanupAsync(None);

            Assert.Equal(2, result.ProfileArtifactsRemoved);
            Assert.False(File.Exists(Path.Combine(profile, "SingletonLock")));
            Assert.False(File.Exists(Path.Combine(profile, "SingletonCookie")));
            Assert.True(File.Exists(Path.Combine(profile, "Preferences"))); // the profile itself is left alone
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public async Task RunningItTwice_HasNothingLeftToDo()
    {
        var markers = new InMemoryWorkBrowserRunMarkerStore { Marker = Marker() };
        var processes = new FakeProcessController();
        processes.Add(4242, "chrome", Started);
        var guard = Create(markers, processes);

        await guard.CleanupAsync(None);
        var second = await guard.CleanupAsync(None);

        Assert.False(second.MarkerFound);
        Assert.Single(processes.Closed);
    }

    [Fact]
    public void TheMarkerFile_RoundTripsAndDisappearsWhenCleared()
    {
        var directory = Directory.CreateTempSubdirectory("routebridge-marker-").FullName;
        try
        {
            var path = Path.Combine(directory, "work-browser.json");
            var store = new WorkBrowserRunMarkerStore(path);
            Assert.Null(store.Read());

            store.Write(new WorkBrowserRunMarker(4242, "msedge", Started, @"C:\profile", Started));
            var read = store.Read();

            Assert.NotNull(read);
            Assert.Equal(4242, read!.ProcessId);
            Assert.Equal("msedge", read.ProcessName);
            Assert.Equal(Started, read.StartedAtUtc);
            Assert.Equal(@"C:\profile", read.ProfileDirectory);

            store.Clear();
            Assert.Null(store.Read());
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMarkerFileThatIsNotJson_IsTreatedAsAbsent()
    {
        var directory = Directory.CreateTempSubdirectory("routebridge-marker-").FullName;
        try
        {
            var path = Path.Combine(directory, "work-browser.json");
            File.WriteAllText(path, "half a file");

            Assert.Null(new WorkBrowserRunMarkerStore(path).Read());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
