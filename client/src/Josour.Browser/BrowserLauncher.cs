using System.Diagnostics;
using System.Runtime.Versioning;
using Josour.Browser.Native;
using Josour.Browser.Registry;
using Josour.Core.Browser;

namespace Josour.Browser;

/// <summary>
/// The work browser (IBrowserSession): locating the path and verifying the publisher -> detecting policies -> exit_type=Normal -> a Job Object (KILL_ON_JOB_CLOSE)
/// -> launching with plan 8.2's command line -> detecting a handoff (exiting within 3 seconds = InstanceHandoff). The close: WM_CLOSE to the job's windows, then
/// a polite wait, then closing the job's handle (killing whatever is left). Off Windows: LaunchAsync returns Failure=Other with a clear message.
/// </summary>
public sealed class BrowserLauncher : IBrowserSession
{
    public static readonly TimeSpan DefaultHandoffWindow = TimeSpan.FromSeconds(3);

    private readonly IRegistryReader _registry;
    private readonly BrowserLocator _locator;
    private readonly TimeSpan _handoffWindow;
    private readonly object _gate = new();
    private Process? _process;
    private IDisposable? _job;
    private Func<IReadOnlyList<int>>? _jobPids;
    private HashSet<int> _lastKnownPids = new();
    private int _closed;

    public BrowserLauncher(IRegistryReader? registry = null, BrowserLocator? locator = null, TimeSpan? handoffWindow = null)
    {
        _registry = registry ?? (OperatingSystem.IsWindows() ? WindowsRegistryReader.Instance : NullRegistryReader.Instance);
        _locator = locator ?? new BrowserLocator(_registry);
        _handoffWindow = handoffWindow ?? DefaultHandoffWindow;
    }

    public BrowserLocation? Location { get; private set; }
    public BrowserPolicyStatus? Policy { get; private set; }
    public IReadOnlyList<string>? Arguments { get; private set; }
    public bool InstanceHandoff { get; private set; }
    public bool ExitTypeSet { get; private set; }
    public int? ProcessId => _process?.Id;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                if (_process is null) return false;
                try { if (!_process.HasExited) return true; } catch { }
                return LivePids().Count > 0;
            }
        }
    }

    public bool OwnsProcess(int pid)
    {
        lock (_gate)
        {
            if (_process is null) return false;
            if (_process.Id == pid) return true;
            return LivePids().Contains(pid);
        }
    }

    public Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new BrowserLaunchResult(false, BrowserLaunchFailure.Other, "browser launch is Windows-only (Job Object + registry + Authenticode); this platform: " + Environment.OSVersion.Platform));
        return LaunchWindowsAsync(options, ct);
    }

    [SupportedOSPlatform("windows")]
    private async Task<BrowserLaunchResult> LaunchWindowsAsync(BrowserLaunchOptions options, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_process is not null) return new BrowserLaunchResult(false, BrowserLaunchFailure.Other, "browser already launched by this session");
        }

        var location = _locator.Locate(options.Kind);
        Location = location;
        if (location is null) return new BrowserLaunchResult(false, BrowserLaunchFailure.NotFound, $"{BrowserLocator.ExeName(options.Kind)} not found in App Paths or default locations");
        if (location.PublisherVerified == false)
            return new BrowserLaunchResult(false, BrowserLaunchFailure.Other, $"publisher of {location.Path} is '{location.Publisher ?? "unsigned"}', expected '{BrowserLocator.ExpectedPublisher(options.Kind)}'");

        var policy = PolicyDetector.Detect(options.Kind, _registry);
        Policy = policy;
        if (policy.AnyManaged)
            return new BrowserLaunchResult(false, BrowserLaunchFailure.ManagedByPolicy, string.Join("; ", policy.Findings));

        try { Directory.CreateDirectory(options.ProfileDirectory); }
        catch (Exception e) { return new BrowserLaunchResult(false, BrowserLaunchFailure.Other, "cannot create profile directory: " + e.Message); }
        ExitTypeSet = ProfilePreferences.SetExitTypeNormal(options.ProfileDirectory);

        var arguments = BrowserCommandLine.Arguments(options);
        Arguments = arguments;
        JobObject? job = null;
        Process? process = null;
        try
        {
            job = JobObject.Create();
            var psi = new ProcessStartInfo(location.Path) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Path.GetDirectoryName(location.Path) ?? string.Empty };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            job.Assign(process);
            lock (_gate)
            {
                _process = process;
                _job = job;
                _jobPids = job.ProcessIds;
            }
        }
        catch (Exception e)
        {
            try { process?.Kill(true); } catch { }
            process?.Dispose();
            job?.Dispose();
            return new BrowserLaunchResult(false, BrowserLaunchFailure.Other, $"launch failed: {e.GetType().Name}: {e.Message}");
        }

        // A handoff: if another instance is using the same profile, the new process hands it the link and exits within moments.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_handoffWindow);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            InstanceHandoff = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Still running after the handoff window: a successful launch
        }
        if (InstanceHandoff && LivePids().Count == 0)
        {
            var code = SafeExitCode(process);
            return new BrowserLaunchResult(false, BrowserLaunchFailure.InstanceHandoff, $"browser process exited within {_handoffWindow.TotalSeconds:F0} s (exit code {code}); another instance owns the profile");
        }
        return new BrowserLaunchResult(true, null, null);
    }

    public async Task CloseAsync(TimeSpan graceful, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Process? process;
        IDisposable? job;
        lock (_gate)
        {
            process = _process;
            job = _job;
        }
        if (process is null) return;

        try
        {
            if (OperatingSystem.IsWindows()) PostCloseWindows();
            var deadline = DateTime.UtcNow + graceful;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && LivePids().Count > 0)
                await Task.Delay(100, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            job?.Dispose(); // KILL_ON_JOB_CLOSE kills whatever is left
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose();
            lock (_gate)
            {
                _job = null;
                _jobPids = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private void PostCloseWindows()
    {
        var pids = LivePids();
        if (pids.Count > 0) WindowCloser.PostCloseToWindowsOf(pids);
    }

    private HashSet<int> LivePids()
    {
        var query = _jobPids;
        if (query is null)
        {
            var p = _process;
            if (p is null) return new HashSet<int>();
            try { return p.HasExited ? new HashSet<int>() : new HashSet<int> { p.Id }; }
            catch { return new HashSet<int>(); }
        }
        try
        {
            var set = new HashSet<int>(query());
            _lastKnownPids = set;
            return set;
        }
        catch
        {
            return _lastKnownPids;
        }
    }

    private static int? SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return null; }
    }
}
