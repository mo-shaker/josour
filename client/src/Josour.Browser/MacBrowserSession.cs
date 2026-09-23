using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Josour.Core.Browser;

namespace Josour.Browser;

/// <summary>
/// The work browser on macOS: an isolated profile pointed at the local proxy, and an answer to "is this process
/// one of ours" that the proxy's owner check depends on.
/// <para>
/// Windows contains the browser in a Job Object, which both kills the whole tree and enumerates it. macOS has no
/// such object, so containment is the process tree itself: <see cref="OwnsProcess"/> walks parents up to the
/// browser we started, and closing kills that tree. The tree is read fresh each time rather than cached, because
/// a cached set plus PID reuse is a way to admit a process nobody launched.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacBrowserSession : IBrowserSession
{
    private const int Sigterm = 15;

    private readonly object _gate = new();
    private readonly Func<string, string?> _locate;
    private readonly Func<IReadOnlyDictionary<int, int>> _processTree;
    private Process? _process;

    public MacBrowserSession()
        : this(MacBrowserLocator.Locate, MacProcessTree.Read)
    {
    }

    /// <param name="locate">Maps a browser's application name to its executable, or null when absent (tests).</param>
    /// <param name="processTree">Every process on the machine as pid → parent pid (tests).</param>
    public MacBrowserSession(Func<string, string?> locate, Func<IReadOnlyDictionary<int, int>> processTree)
    {
        _locate = locate;
        _processTree = processTree;
    }

    /// <summary>The executable that was launched, for diagnostics.</summary>
    public string? ExecutablePath { get; private set; }

    /// <summary>The launched browser's process id, for the diagnostics; null before a launch and after a close.</summary>
    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process?.Id;
            }
        }
    }

    /// <summary>The arguments it was launched with, for diagnostics.</summary>
    public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>
    /// True when <paramref name="pid"/> is the browser or a descendant of it.
    /// <para>
    /// Chrome does not open sockets from the process that was launched — a network service child does — so
    /// comparing against the launched pid alone would refuse every connection the browser makes. Walking the
    /// parent chain is what the Job Object's membership test does on Windows, spelled out.
    /// </para>
    /// </summary>
    public bool OwnsProcess(int pid)
    {
        int root;
        lock (_gate)
        {
            if (_process is null || _process.HasExited)
            {
                return false;
            }

            root = _process.Id;
        }

        return pid == root || MacProcessTree.IsDescendant(pid, root, _processTree());
    }

    public Task<BrowserLaunchResult> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_process is not null)
            {
                return Task.FromResult(new BrowserLaunchResult(
                    false, BrowserLaunchFailure.Other, "browser already launched by this session"));
            }
        }

        var path = _locate(MacBrowserLocator.ApplicationName(options.Kind));
        ExecutablePath = path;
        if (path is null)
        {
            return Task.FromResult(new BrowserLaunchResult(
                false,
                BrowserLaunchFailure.NotFound,
                $"{MacBrowserLocator.ApplicationName(options.Kind)} was not found in /Applications or ~/Applications"));
        }

        try
        {
            Directory.CreateDirectory(options.ProfileDirectory);
        }
        catch (Exception e)
        {
            return Task.FromResult(new BrowserLaunchResult(
                false, BrowserLaunchFailure.Other, "cannot create profile directory: " + e.Message));
        }

        ProfilePreferences.SetExitTypeNormal(options.ProfileDirectory);

        var arguments = BrowserCommandLine.Arguments(options);
        Arguments = arguments;

        try
        {
            // The executable inside the bundle, not `open`: `open` hands the request to an already-running copy
            // and returns, leaving nothing to own, nothing to wait on and nothing to kill. The proxy's owner
            // check needs a process of our own.
            var psi = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = false };
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            var process = Process.Start(psi)
                          ?? throw new InvalidOperationException("Process.Start returned null");

            lock (_gate)
            {
                _process = process;
            }

            return Task.FromResult(new BrowserLaunchResult(true, null, null));
        }
        catch (Exception e)
        {
            return Task.FromResult(new BrowserLaunchResult(
                false, BrowserLaunchFailure.Other, $"launch failed: {e.GetType().Name}: {e.Message}"));
        }
    }

    /// <summary>
    /// SIGTERM to the tree, then SIGKILL to whatever is still there. Chrome writes its profile out on SIGTERM;
    /// killing it outright is what makes it offer to restore tabs on the next run.
    /// </summary>
    public async Task CloseAsync(TimeSpan graceful, CancellationToken ct)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        using (process)
        {
            if (process.HasExited)
            {
                return;
            }

            var tree = _processTree();
            var members = MacProcessTree.Descendants(process.Id, tree).Append(process.Id).ToList();
            foreach (var pid in members)
            {
                Signal(pid, Sigterm);
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(graceful);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // It did not go quietly.
            }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    // Already gone between the check and the kill.
                }
            }
        }
    }

    private static void Signal(int pid, int signal)
    {
        try
        {
            _ = kill(pid, signal);
        }
        catch (EntryPointNotFoundException)
        {
            // Nothing sensible to do; the SIGKILL below is the fallback.
        }
    }

    /// <summary>.NET's Process.Kill sends SIGKILL and has no gentler option, so the polite signal is sent by hand.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>Where the browsers live on macOS.</summary>
[SupportedOSPlatform("macos")]
public static class MacBrowserLocator
{
    /// <summary>The bundle name, which is also the executable's name inside it.</summary>
    public static string ApplicationName(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Edge => "Microsoft Edge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The executable inside the bundle, from the two places an application is installed: the system folder, and
    /// the per-user one for anybody who cannot write to it.
    /// </summary>
    public static string? Locate(string applicationName)
    {
        foreach (var root in new[]
                 {
                     "/Applications",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
                 })
        {
            var path = Path.Combine(root, applicationName + ".app", "Contents", "MacOS", applicationName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

/// <summary>Reads the machine's process tree, so "did our browser start this" can be answered.</summary>
[SupportedOSPlatform("macos")]
public static class MacProcessTree
{
    /// <summary>Every process as pid → parent pid. Empty when <c>ps</c> could not be read.</summary>
    public static IReadOnlyDictionary<int, int> Read()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/ps")
            {
                ArgumentList = { "-axo", "pid=,ppid=" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return new Dictionary<int, int>();
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Exited in between.
                }

                return new Dictionary<int, int>();
            }

            return Parse(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // An empty tree means OwnsProcess answers false, which the proxy refuses. Failing closed.
            return new Dictionary<int, int>();
        }
    }

    /// <summary>Parses <c>ps -axo pid=,ppid=</c>: two numbers per line.</summary>
    public static IReadOnlyDictionary<int, int> Parse(string output)
    {
        var tree = new Dictionary<int, int>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid))
            {
                tree[pid] = ppid;
            }
        }

        return tree;
    }

    /// <summary>
    /// Whether <paramref name="pid"/> descends from <paramref name="ancestor"/>. Bounded: a corrupt tree with a
    /// cycle in it must not spin forever inside a connection check.
    /// </summary>
    public static bool IsDescendant(int pid, int ancestor, IReadOnlyDictionary<int, int> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var seen = 0;
        var current = pid;
        while (tree.TryGetValue(current, out var parent) && parent > 1 && seen++ < 64)
        {
            if (parent == ancestor)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>Every process below <paramref name="root"/>, deepest first, so children are signalled before parents.</summary>
    public static IReadOnlyList<int> Descendants(int root, IReadOnlyDictionary<int, int> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Keys.Where(pid => IsDescendant(pid, root, tree)).ToList();
    }
}
