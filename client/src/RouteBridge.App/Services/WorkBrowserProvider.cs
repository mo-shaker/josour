using Microsoft.Extensions.Logging;
using RouteBridge.Browser;
using RouteBridge.Browser.Registry;
using RouteBridge.Core.Browser;
using RouteBridge.Infrastructure.Session;

namespace RouteBridge.App.Services;

/// <summary>
/// Picks the work browser on this machine: Chrome first, then Edge, keeping only the ones that are actually installed
/// (<see cref="BrowserLocator"/>) and putting the ones NOT managed by enterprise policy (<see cref="PolicyDetector"/>) ahead
/// of the managed ones — a managed browser ignores <c>--proxy-server</c>, which is exactly the case the probe page catches.
/// A managed browser is still offered last so the user gets the real reason from the launch result instead of "not found".
/// </summary>
public sealed class WorkBrowserProvider : IWorkBrowserProvider
{
    private static readonly BrowserKind[] Order = { BrowserKind.Chrome, BrowserKind.Edge };

    private readonly IRegistryReader _registry;
    private readonly BrowserLocator _locator;
    private readonly ILogger<WorkBrowserProvider> _logger;

    public WorkBrowserProvider(ILogger<WorkBrowserProvider> logger, IRegistryReader? registry = null)
    {
        _logger = logger;
        _registry = registry ?? (OperatingSystem.IsWindows() ? WindowsRegistryReader.Instance : NullRegistryReader.Instance);
        _locator = new BrowserLocator(_registry);
    }

    /// <summary>A dedicated profile under %LOCALAPPDATA%; never the user's own Chrome/Edge profile.</summary>
    public string ProfileDirectory { get; } = BrowserCommandLine.DefaultProfileDirectory();

    public IReadOnlyList<BrowserKind> Preference()
    {
        var unmanaged = new List<BrowserKind>();
        var managed = new List<BrowserKind>();
        foreach (var kind in Order)
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

    public IBrowserSession Create(BrowserKind kind) => new BrowserLauncher(_registry, _locator);
}
