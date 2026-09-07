using Microsoft.Extensions.Logging;
using Josour.Browser;
using Josour.Browser.Registry;
using Josour.Core.Browser;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.Settings;

namespace Josour.App.Services;

/// <summary>
/// Picks the work browser on this machine: the user's preferred one first (week 6 — the <c>preferredBrowser</c> setting
/// existed since week 1 and nothing read it), then the other, keeping only the ones that are actually installed
/// (<see cref="BrowserLocator"/>) and putting the ones NOT managed by enterprise policy (<see cref="PolicyDetector"/>) ahead
/// of the managed ones — a managed browser ignores <c>--proxy-server</c>, which is exactly the case the probe page catches.
/// A managed browser is still offered last so the user gets the real reason from the launch result instead of "not found".
/// <para>
/// The preference is an ordering, never a restriction: a user who picks Chrome on a machine where Chrome is not installed
/// gets Edge and a session, not an error about a browser they cannot control.
/// </para>
/// </summary>
public sealed class WorkBrowserProvider : IWorkBrowserProvider
{
    private static readonly BrowserKind[] DefaultOrder = { BrowserKind.Chrome, BrowserKind.Edge };

    private readonly IRegistryReader _registry;
    private readonly BrowserLocator _locator;
    private readonly IWorkBrowserRunMarkerStore _markers;
    private readonly IProcessController _processes;
    private readonly IAppSettingsStore _settings;
    private readonly ILogger<WorkBrowserProvider> _logger;

    public WorkBrowserProvider(
        IWorkBrowserRunMarkerStore markers,
        IProcessController processes,
        IAppSettingsStore settings,
        ILogger<WorkBrowserProvider> logger,
        IRegistryReader? registry = null)
    {
        _markers = markers;
        _processes = processes;
        _settings = settings;
        _logger = logger;
        _registry = registry ?? (OperatingSystem.IsWindows() ? WindowsRegistryReader.Instance : NullRegistryReader.Instance);
        _locator = new BrowserLocator(_registry);
    }

    /// <summary>The install order with the user's preference (if any) moved to the front.</summary>
    private IReadOnlyList<BrowserKind> Order()
    {
        var preferred = _settings.Current.PreferredBrowser switch
        {
            BrowserPreference.Chrome => (BrowserKind?)BrowserKind.Chrome,
            BrowserPreference.Edge => BrowserKind.Edge,
            _ => null,
        };

        return preferred is { } kind
            ? new[] { kind }.Concat(DefaultOrder.Where(k => k != kind)).ToArray()
            : DefaultOrder;
    }

    /// <summary>A dedicated profile under %LOCALAPPDATA%; never the user's own Chrome/Edge profile.</summary>
    public string ProfileDirectory { get; } = BrowserCommandLine.DefaultProfileDirectory();

    public IReadOnlyList<BrowserKind> Preference()
    {
        var unmanaged = new List<BrowserKind>();
        var managed = new List<BrowserKind>();
        foreach (var kind in Order())
        {
            if (_locator.Locate(kind) is null)
            {
                continue;
            }

            var policy = PolicyDetector.Detect(kind, _registry);
            if (policy.AnyManaged)
            {
                _logger.LogWarning("{Kind} is managed by policy ({Findings}); it is only a fallback for the work browser", kind, string.Join("; ", policy.Findings));
                managed.Add(kind);
            }
            else
            {
                unmanaged.Add(kind);
            }
        }

        var preference = unmanaged.Concat(managed).ToList();
        if (preference.Count == 0)
        {
            _logger.LogError("Neither Chrome nor Edge was found: the work browser cannot be started");
        }

        return preference;
    }

    /// <summary>
    /// A launcher wrapped in <see cref="MarkedBrowserSession"/>, so a browser that outlives an unclean exit is written
    /// down and closed at the next start-up (plan 8.5).
    /// </summary>
    public IBrowserSession Create(BrowserKind kind) =>
        new MarkedBrowserSession(new BrowserLauncher(_registry, _locator), _markers, _processes, ProfileDirectory, _logger);
}
