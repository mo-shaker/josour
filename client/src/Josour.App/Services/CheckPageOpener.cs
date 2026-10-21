using Josour.Infrastructure.Session;
using Josour.Infrastructure.Settings;

namespace Josour.App.Services;

/// <summary>
/// The guest's check page in the user's default browser, through <see cref="IShellService.TryOpenUrl"/>. The shell is
/// resolved lazily: it depends on the session coordinator, which depends on this.
/// </summary>
public sealed class CheckPageOpener : ICheckPageOpener
{
    private readonly IAppSettingsStore _settings;
    private readonly Func<IShellService> _shell;

    public CheckPageOpener(IAppSettingsStore settings, Func<IShellService> shell)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    public bool OpenOnConnect => _settings.Current.OpenCheckPageOnConnect;

    public bool TryOpen(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && _shell().TryOpenUrl(uri, out _);
}
